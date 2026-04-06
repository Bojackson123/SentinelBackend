namespace SentinelBackend.Api.Workers;

using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

public class CommandExecutorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommandExecutorWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private bool _directMethodUnavailableLogged;

    public CommandExecutorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CommandExecutorWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var pollIntervalSeconds =
            configuration.GetValue<int?>("CommandExecutor:PollIntervalSeconds") ?? 5;
        if (pollIntervalSeconds <= 0)
        {
            pollIntervalSeconds = 5;
        }

        _pollInterval = TimeSpan.FromSeconds(pollIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CommandExecutorWorker started with poll interval {PollIntervalSeconds}s",
            _pollInterval.TotalSeconds
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPollCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending commands");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("CommandExecutorWorker stopped");
    }

    /// <summary>
    /// Runs a single poll cycle. Exposed as internal for testability.
    /// </summary>
    internal async Task RunPollCycleAsync(CancellationToken stoppingToken = default)
    {
        await ProcessPendingCommandsAsync(stoppingToken);
    }

    private async Task ProcessPendingCommandsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        IDirectMethodService directMethod;
        try
        {
            directMethod = scope.ServiceProvider.GetRequiredService<IDirectMethodService>();
            _directMethodUnavailableLogged = false;
        }
        catch (Exception ex)
        {
            if (!_directMethodUnavailableLogged)
            {
                _logger.LogError(
                    ex,
                    "Direct method execution is unavailable due to IoT Hub service configuration. "
                        + "Pending commands will be retried until configuration is fixed."
                );
                _directMethodUnavailableLogged = true;
            }

            return;
        }

        var pendingCommands = await db.CommandLogs
            .Include(c => c.Device)
            .Where(c => c.Status == CommandStatus.Pending)
            .OrderBy(c => c.RequestedAt)
            .Take(10)
            .ToListAsync(stoppingToken);

        foreach (var command in pendingCommands)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            var deviceId = command.Device.DeviceId;
            if (string.IsNullOrEmpty(deviceId))
            {
                command.Status = CommandStatus.Failed;
                command.ErrorMessage = "Device has no IoT Hub device ID";
                command.CompletedAt = DateTime.UtcNow;
                command.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
                continue;
            }

            // Map command type to IoT Hub direct method name
            var methodName = MapCommandToMethod(command.CommandType);

            command.Status = CommandStatus.Sent;
            command.SentAt = DateTime.UtcNow;
            command.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);

            try
            {
                var result = await directMethod.InvokeAsync(
                    deviceId, methodName, cancellationToken: stoppingToken);

                command.ResponseJson = result.ResponseJson;

                if (result.Status is >= 200 and < 300)
                {
                    command.Status = CommandStatus.Succeeded;
                    _logger.LogInformation(
                        "Command {CommandId} ({CommandType}) on device {DeviceId} succeeded",
                        command.Id, command.CommandType, deviceId);
                }
                else
                {
                    command.Status = CommandStatus.Failed;
                    command.ErrorMessage = $"Device returned status {result.Status}";
                    _logger.LogWarning(
                        "Command {CommandId} ({CommandType}) on device {DeviceId} failed with status {Status}",
                        command.Id, command.CommandType, deviceId, result.Status);
                }
            }
            catch (TimeoutException)
            {
                command.Status = CommandStatus.TimedOut;
                command.ErrorMessage = "Direct method invocation timed out";
                _logger.LogWarning(
                    "Command {CommandId} ({CommandType}) on device {DeviceId} timed out",
                    command.Id, command.CommandType, deviceId);
            }
            catch (Exception ex)
            {
                command.Status = CommandStatus.Failed;
                command.ErrorMessage = ex.Message;
                _logger.LogError(ex,
                    "Command {CommandId} ({CommandType}) on device {DeviceId} failed",
                    command.Id, command.CommandType, deviceId);
            }

            command.CompletedAt = DateTime.UtcNow;
            command.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
        }
    }

    private static string MapCommandToMethod(string commandType) => commandType switch
    {
        "reboot" => "reboot",
        "ping" => "ping",
        "capture-snapshot" => "captureSnapshot",
        "run-self-test" => "runSelfTest",
        "sync-now" => "syncNow",
        "clear-fault" => "clearFault",
        _ => commandType,
    };
}
