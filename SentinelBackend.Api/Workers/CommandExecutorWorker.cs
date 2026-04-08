namespace SentinelBackend.Api.Workers;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

public class CommandExecutorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommandExecutorWorker> _logger;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly TimeSpan _pollInterval;
    private bool _directMethodUnavailableLogged;

    public const string QueueName = "device-commands";

    public CommandExecutorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CommandExecutorWorker> logger,
        IConfiguration configuration,
        ServiceBusClient? serviceBusClient = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _serviceBusClient = serviceBusClient;

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
        if (_serviceBusClient is not null)
        {
            _logger.LogInformation("CommandExecutorWorker started in Service Bus mode (queue={Queue})", QueueName);
            await RunServiceBusModeAsync(stoppingToken);
        }
        else
        {
            _logger.LogInformation(
                "CommandExecutorWorker started in polling mode (interval={PollIntervalSeconds}s)",
                _pollInterval.TotalSeconds);
            await RunPollingModeAsync(stoppingToken);
        }

        _logger.LogInformation("CommandExecutorWorker stopped");
    }

    private async Task RunServiceBusModeAsync(CancellationToken stoppingToken)
    {
        await using var processor = _serviceBusClient!.CreateProcessor(QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 5,
        });

        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var body = args.Message.Body.ToString();
                var envelope = JsonSerializer.Deserialize<CommandMessage>(body);
                if (envelope?.CommandId > 0)
                {
                    await ProcessSingleCommandAsync(envelope.CommandId, args.CancellationToken);
                }
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Service Bus command message");
                // Message will be retried by Service Bus (default lock timeout)
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Service Bus processor error (source={Source})", args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        await processor.StopProcessingAsync();
    }

    private async Task RunPollingModeAsync(CancellationToken stoppingToken)
    {
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
    }

    /// <summary>
    /// Runs a single poll cycle. Exposed as internal for testability (polling fallback).
    /// </summary>
    internal async Task RunPollCycleAsync(CancellationToken stoppingToken = default)
    {
        await ProcessPendingCommandsAsync(stoppingToken);
    }

    /// <summary>
    /// Processes a single command by ID. Used by the Service Bus message handler.
    /// Exposed as internal for testability.
    /// </summary>
    internal async Task ProcessSingleCommandAsync(int commandId, CancellationToken stoppingToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        IDirectMethodService directMethod;
        try
        {
            directMethod = scope.ServiceProvider.GetRequiredService<IDirectMethodService>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct method service unavailable while processing command {CommandId}", commandId);
            throw; // Let Service Bus retry
        }

        var command = await db.CommandLogs
            .Include(c => c.Device)
            .FirstOrDefaultAsync(c => c.Id == commandId && c.Status == CommandStatus.Pending, stoppingToken);

        if (command is null)
        {
            _logger.LogDebug("Command {CommandId} not found or no longer Pending, skipping", commandId);
            return;
        }

        await ExecuteCommandAsync(db, directMethod, command, stoppingToken);
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

            await ExecuteCommandAsync(db, directMethod, command, stoppingToken);
        }
    }

    private async Task ExecuteCommandAsync(
        SentinelDbContext db,
        IDirectMethodService directMethod,
        Domain.Entities.CommandLog command,
        CancellationToken stoppingToken)
    {
        var deviceId = command.Device.DeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            command.Status = CommandStatus.Failed;
            command.ErrorMessage = "Device has no IoT Hub device ID";
            command.CompletedAt = DateTime.UtcNow;
            command.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
            return;
        }

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

public record CommandMessage(int CommandId);
