namespace SentinelBackend.Api.Workers;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Event-driven offline detection worker.
///
/// Listens on the "offline-checks" Service Bus queue. Each message represents a
/// per-device deadline: "if no telemetry arrived by this time, the device is offline."
///
/// The ingestion worker schedules a deadline message every time a device sends telemetry.
/// When that message fires (after OfflineThresholdSeconds), this worker checks whether
/// newer telemetry has arrived since. If not, the device is marked offline and an alarm
/// is raised.
///
/// Falls back to a no-op when Service Bus is not configured (the polling
/// OfflineMonitorWorker acts as the safety net).
/// </summary>
public class OfflineCheckWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OfflineCheckWorker> _logger;
    private readonly ServiceBusClient? _serviceBusClient;

    public const string QueueName = "offline-checks";

    public OfflineCheckWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OfflineCheckWorker> logger,
        ServiceBusClient? serviceBusClient = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _serviceBusClient = serviceBusClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_serviceBusClient is null)
        {
            _logger.LogInformation(
                "OfflineCheckWorker disabled — no Service Bus configured. " +
                "Offline detection relies on OfflineMonitorWorker polling.");
            return;
        }

        _logger.LogInformation("OfflineCheckWorker started in Service Bus mode (queue={Queue})", QueueName);

        await using var processor = _serviceBusClient.CreateProcessor(QueueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 10,
        });

        processor.ProcessMessageAsync += async args =>
        {
            try
            {
                var body = args.Message.Body.ToString();
                var envelope = JsonSerializer.Deserialize<OfflineCheckMessage>(body);
                if (envelope is not null)
                {
                    await EvaluateDeviceAsync(envelope, args.CancellationToken);
                }
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing offline check message");
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
        _logger.LogInformation("OfflineCheckWorker stopped");
    }

    /// <summary>
    /// Evaluates whether a device is truly offline. Exposed as internal for testability.
    /// </summary>
    internal async Task EvaluateDeviceAsync(OfflineCheckMessage message, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var alarmService = scope.ServiceProvider.GetRequiredService<IAlarmService>();

        var connectivity = await db.DeviceConnectivityStates
            .Include(c => c.Device)
                .ThenInclude(d => d.Assignments.Where(a => a.UnassignedAt == null))
            .FirstOrDefaultAsync(c => c.DeviceId == message.DeviceId, ct);

        if (connectivity is null)
        {
            _logger.LogDebug("No connectivity state for device {DeviceId}, skipping offline check", message.DeviceId);
            return;
        }

        // Only evaluate Active devices
        if (connectivity.Device.Status != DeviceStatus.Active)
            return;

        // Check if newer telemetry arrived since the deadline was scheduled.
        // If LastMessageReceivedAt is after the expected time, this deadline is stale — ignore it.
        if (connectivity.LastMessageReceivedAt > message.ExpectedAfter)
        {
            _logger.LogDebug(
                "Device {DeviceId} sent telemetry at {LastReceived} after expected {Expected} — still online",
                message.DeviceId, connectivity.LastMessageReceivedAt, message.ExpectedAfter);
            return;
        }

        // Device has not sent anything since the deadline — it's offline
        if (connectivity.IsOffline)
        {
            // Already marked offline (duplicate deadline or safety-net already caught it)
            return;
        }

        var now = DateTime.UtcNow;
        connectivity.IsOffline = true;
        connectivity.UpdatedAt = now;

        // Check maintenance window suppression
        var isSuppressed = await IsDeviceInMaintenanceWindowAsync(db, connectivity.DeviceId, connectivity.Device, now, ct);
        connectivity.SuppressedByMaintenanceWindow = isSuppressed;

        if (!isSuppressed)
        {
            var elapsed = now - connectivity.LastMessageReceivedAt;
            await alarmService.RaiseAlarmAsync(
                connectivity.DeviceId,
                "DeviceOffline",
                AlarmSeverity.Warning,
                AlarmSourceType.SystemGenerated,
                detailsJson: JsonSerializer.Serialize(new
                {
                    lastMessageReceivedAt = connectivity.LastMessageReceivedAt,
                    thresholdSeconds = connectivity.OfflineThresholdSeconds,
                    elapsedSeconds = (int)elapsed.TotalSeconds,
                    detectionMode = "ServiceBus",
                }),
                cancellationToken: ct);

            _logger.LogInformation(
                "Device {DeviceId} marked offline (no message since {LastReceived}, threshold={Threshold}s)",
                message.DeviceId, connectivity.LastMessageReceivedAt, connectivity.OfflineThresholdSeconds);
        }
        else
        {
            _logger.LogDebug(
                "Device {DeviceId} offline but suppressed by maintenance window",
                message.DeviceId);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> IsDeviceInMaintenanceWindowAsync(
        SentinelDbContext db,
        int deviceId,
        Device device,
        DateTime now,
        CancellationToken ct)
    {
        var activeWindows = await db.MaintenanceWindows
            .Where(mw => mw.StartsAt <= now && mw.EndsAt > now)
            .ToListAsync(ct);

        var activeAssignment = device.Assignments?.FirstOrDefault(a => a.UnassignedAt == null);

        foreach (var window in activeWindows)
        {
            if (window.ScopeType == MaintenanceWindowScope.Device && window.DeviceId == deviceId)
                return true;

            if (window.ScopeType == MaintenanceWindowScope.Site
                && window.SiteId.HasValue
                && activeAssignment is not null
                && window.SiteId == activeAssignment.SiteId)
                return true;
        }

        return false;
    }
}

public record OfflineCheckMessage(int DeviceId, DateTime ExpectedAfter);
