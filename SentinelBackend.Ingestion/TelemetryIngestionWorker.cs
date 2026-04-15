namespace SentinelBackend.Ingestion;

using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Contracts;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

public class TelemetryIngestionWorker : BackgroundService
{
    private readonly EventProcessorClient _processor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBlobArchiveService _archiveService;
    private readonly IMessagePublisher? _messagePublisher;
    private readonly ILogger<TelemetryIngestionWorker> _logger;

    public const string OfflineCheckQueue = "offline-checks";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TelemetryIngestionWorker(
        EventProcessorClient processor,
        IServiceScopeFactory scopeFactory,
        IBlobArchiveService archiveService,
        ILogger<TelemetryIngestionWorker> logger,
        IMessagePublisher? messagePublisher = null)
    {
        _processor = processor;
        _scopeFactory = scopeFactory;
        _archiveService = archiveService;
        _logger = logger;
        _messagePublisher = messagePublisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessEventAsync += HandleEventAsync;
        _processor.ProcessErrorAsync += HandleErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        finally
        {
            await _processor.StopProcessingAsync();
        }
    }

    private async Task HandleEventAsync(ProcessEventArgs args)
    {
        // MaximumWaitTime can cause empty dispatches — skip if no event data
        if (!args.HasEvent)
        {
            return;
        }

        var json = Encoding.UTF8.GetString(args.Data.EventBody);
        var partitionId = args.Partition.PartitionId;
        var offset = args.Data.OffsetString;
        var enqueuedAt = args.Data.EnqueuedTime.UtcDateTime;

        // Device identity from IoT Hub system properties — authoritative per design doc
        string? deviceId = null;
        if (args.Data.SystemProperties.TryGetValue("iothub-connection-device-id", out var sysDeviceId))
        {
            deviceId = sysDeviceId?.ToString();
        }

        // Message type from application properties
        string messageType = "telemetry";
        if (args.Data.Properties.TryGetValue("messageType", out var msgType))
        {
            messageType = msgType?.ToString() ?? "telemetry";
        }

        // Deserialize payload
        TelemetryMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TelemetryMessage>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize message from partition {Partition}", partitionId);
            await RecordFailedIngressAsync(deviceId, null, partitionId, offset, enqueuedAt,
                "DeserializationFailure", ex.Message, json);
            await args.UpdateCheckpointAsync();
            return;
        }

        if (message is null)
        {
            _logger.LogWarning("Null message after deserialization — partition {Partition}", partitionId);
            await RecordFailedIngressAsync(deviceId, null, partitionId, offset, enqueuedAt,
                "NullMessage", "Deserialized to null", json);
            await args.UpdateCheckpointAsync();
            return;
        }

        // Validate device identity — must come from system properties
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning("Message missing IoT Hub device identity — partition {Partition}", partitionId);
            await RecordFailedIngressAsync(deviceId, message.MessageId, partitionId, offset, enqueuedAt,
                "MissingDeviceIdentity", "No iothub-connection-device-id", json);
            await args.UpdateCheckpointAsync();
            return;
        }

        // Validate required envelope fields
        if (string.IsNullOrWhiteSpace(message.MessageId))
        {
            _logger.LogWarning("Message missing messageId from device {DeviceId}", deviceId);
            await RecordFailedIngressAsync(deviceId, null, partitionId, offset, enqueuedAt,
                "MissingMessageId", "Required envelope field messageId missing", json);
            await args.UpdateCheckpointAsync();
            return;
        }

        if (message.TimestampUtc is null)
        {
            _logger.LogWarning("Message missing timestampUtc from device {DeviceId}", deviceId);
            await RecordFailedIngressAsync(deviceId, message.MessageId, partitionId, offset, enqueuedAt,
                "MissingTimestamp", "Required envelope field timestampUtc missing", json);
            await args.UpdateCheckpointAsync();
            return;
        }

        var receivedAtUtc = DateTime.UtcNow;
        var timestampUtc = message.TimestampUtc.Value;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();

        // Resolve device by IoT Hub deviceId
        var device = await db.Devices
            .IgnoreQueryFilters()
            .Include(d => d.LatestState)
            .Include(d => d.ConnectivityState)
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId);

        // Fallback: DPS uses SerialNumber as IoT Hub device identity.
        // If DeviceId hasn't been set yet (no custom allocation webhook),
        // match by SerialNumber and backfill DeviceId.
        if (device is null)
        {
            device = await db.Devices
                .IgnoreQueryFilters()
                .Include(d => d.LatestState)
                .Include(d => d.ConnectivityState)
                .FirstOrDefaultAsync(d => d.SerialNumber == deviceId);

            if (device is not null)
            {
                device.DeviceId = deviceId;
                device.ProvisionedAt ??= DateTime.UtcNow;
                device.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation(
                    "Auto-linked device {SerialNumber} to DeviceId {DeviceId}",
                    device.SerialNumber, deviceId);
            }
        }

        if (device is null)
        {
            _logger.LogWarning("Unknown device {DeviceId} — recording failed ingress", deviceId);
            await RecordFailedIngressAsync(deviceId, message.MessageId, partitionId, offset, enqueuedAt,
                "UnknownDevice", $"No device record for deviceId '{deviceId}'", json);
            await args.UpdateCheckpointAsync();
            return;
        }

        // ── Deduplication by (device_id, message_id) ────────────
        var isDuplicate = await db.TelemetryHistory
            .AnyAsync(t => t.DeviceId == device.Id && t.MessageId == message.MessageId);

        if (isDuplicate)
        {
            _logger.LogDebug("Duplicate message {MessageId} for device {DeviceId} — skipping",
                message.MessageId, deviceId);
            await args.UpdateCheckpointAsync();
            return;
        }

        // ── Resolve current ownership snapshot ───────────────────
        var activeAssignment = await db.DeviceAssignments
            .Where(a => a.DeviceId == device.Id && a.UnassignedAt == null)
            .Select(a => new { a.Id, a.SiteId, a.Site.CustomerId, a.Site.Customer.CompanyId })
            .FirstOrDefaultAsync();

        // ── Persist telemetry history ────────────────────────────
        // Archive raw payload to Blob storage
        var rawPayloadBlobUri = await _archiveService.ArchiveRawPayloadAsync(
            deviceId, timestampUtc, message.MessageId, json);

        var telemetryRecord = new TelemetryHistory
        {
            DeviceId = device.Id,
            MessageId = message.MessageId,
            MessageType = messageType,
            TimestampUtc = timestampUtc,
            EnqueuedAtUtc = enqueuedAt,
            ReceivedAtUtc = receivedAtUtc,
            DeviceAssignmentId = activeAssignment?.Id,
            SiteId = activeAssignment?.SiteId,
            CustomerId = activeAssignment?.CustomerId,
            CompanyId = activeAssignment?.CompanyId,
            PanelVoltage = message.PanelVoltage,
            PumpCurrent = message.PumpCurrent,
            PumpRunning = message.PumpRunning,
            HighWaterAlarm = message.HighWaterAlarm,
            TemperatureC = message.TemperatureC,
            SignalRssi = message.SignalRssi,
            RuntimeSeconds = message.RuntimeSeconds,
            ReportedCycleCount = message.ReportedCycleCount,
            FirmwareVersion = message.FirmwareVersion,
            BootId = message.BootId,
            SequenceNumber = message.SequenceNumber,
            RawPayloadBlobUri = rawPayloadBlobUri,
        };
        db.TelemetryHistory.Add(telemetryRecord);

        // ── Update latest state (only if newer) ──────────────────
        if (messageType == "telemetry")
        {
            var state = device.LatestState;
            if (state is null)
            {
                state = new LatestDeviceState { DeviceId = device.Id };
                device.LatestState = state;
                db.LatestDeviceStates.Add(state);
            }

            if (timestampUtc > state.LastTelemetryTimestampUtc)
            {
                state.LastTelemetryTimestampUtc = timestampUtc;
                state.LastMessageId = message.MessageId;
                state.LastBootId = message.BootId;
                state.LastSequenceNumber = message.SequenceNumber;
                state.PanelVoltage = message.PanelVoltage;
                state.PumpCurrent = message.PumpCurrent;
                state.PumpRunning = message.PumpRunning;
                state.HighWaterAlarm = message.HighWaterAlarm;
                state.TemperatureC = message.TemperatureC;
                state.SignalRssi = message.SignalRssi;
                state.RuntimeSeconds = message.RuntimeSeconds;
                state.ReportedCycleCount = message.ReportedCycleCount;
                state.UpdatedAt = receivedAtUtc;
            }
        }

        // ── Update connectivity state (any message type) ────────
        var connectivity = device.ConnectivityState;
        if (connectivity is null)
        {
            connectivity = new DeviceConnectivityState { DeviceId = device.Id };
            device.ConnectivityState = connectivity;
            db.DeviceConnectivityStates.Add(connectivity);
        }

        connectivity.LastMessageReceivedAt = receivedAtUtc;
        connectivity.LastEnqueuedAtUtc = enqueuedAt;
        connectivity.LastMessageType = messageType;
        if (messageType == "telemetry")
        {
            connectivity.LastTelemetryReceivedAt = receivedAtUtc;
        }
        var wasOffline = connectivity.IsOffline;
        connectivity.IsOffline = false;
        connectivity.UpdatedAt = receivedAtUtc;

        // ── Schedule offline-check deadline via Service Bus ──────
        // If Service Bus is configured, schedule a message that will fire after
        // the device's offline threshold. The OfflineCheckWorker will verify
        // whether newer telemetry arrived and raise a DeviceOffline alarm if not.
        if (_messagePublisher is not null && connectivity.OfflineThresholdSeconds > 0)
        {
            var deadline = receivedAtUtc.AddSeconds(connectivity.OfflineThresholdSeconds);
            try
            {
                await _messagePublisher.PublishAsync(
                    OfflineCheckQueue,
                    new OfflineCheckMessage(device.Id, receivedAtUtc),
                    new DateTimeOffset(deadline, TimeSpan.Zero));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to schedule offline-check for device {DeviceId}", deviceId);
            }
        }

        // ── Auto-resolve DeviceOffline alarm on telemetry arrival ─
        // The device just sent something — if it was previously offline, resolve the alarm.
        if (wasOffline)
        {
            try
            {
                var alarmSvc = scope.ServiceProvider.GetRequiredService<IAlarmService>();
                await alarmSvc.AutoResolveAlarmsAsync(
                    device.Id,
                    "DeviceOffline",
                    "Auto-resolved: device telemetry resumed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to auto-resolve DeviceOffline alarm for device {DeviceId}",
                    deviceId);
            }
        }

        // ── First-telemetry activation transition ────────────────
        if (device.Status == DeviceStatus.Assigned && messageType == "telemetry")
        {
            device.Status = DeviceStatus.Active;
            _logger.LogInformation("Device {DeviceId} activated on first telemetry", deviceId);
        }

        // ── Lifecycle message handling ───────────────────────────
        if (messageType == "lifecycle")
        {
            if (message.BootId is not null)
            {
                // Device boot event — update connectivity with boot info
                connectivity.LastBootId = message.BootId;
                _logger.LogInformation("Device {DeviceId} boot event — bootId {BootId}", deviceId, message.BootId);
            }
        }

        // Update firmware if reported
        if (!string.IsNullOrWhiteSpace(message.FirmwareVersion))
        {
            device.FirmwareVersion = message.FirmwareVersion;
        }

        device.UpdatedAt = receivedAtUtc;

        await db.SaveChangesAsync();

        // ── Telemetry-fallback alarm detection ───────────────────
        // After persisting telemetry, evaluate alarm conditions.
        // HighWaterAlarm is the primary fallback alarm for Phase 5.
        if (messageType == "telemetry")
        {
            try
            {
                var alarmService = scope.ServiceProvider.GetRequiredService<IAlarmService>();

                if (message.HighWaterAlarm == true)
                {
                    await alarmService.RaiseAlarmAsync(
                        device.Id,
                        "HighWater",
                        AlarmSeverity.Critical,
                        AlarmSourceType.TelemetryFallback,
                        triggerMessageId: message.MessageId);
                }
                else if (message.HighWaterAlarm == false)
                {
                    // Condition cleared — auto-resolve any active HighWater alarms
                    await alarmService.AutoResolveAlarmsAsync(
                        device.Id,
                        "HighWater",
                        "Auto-resolved: high water condition cleared");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to evaluate alarm conditions for device {DeviceId} message {MessageId} — telemetry already persisted",
                    deviceId, message.MessageId);
            }
        }

        _logger.LogInformation(
            "Processed {MessageType} {MessageId} for device {DeviceId}",
            messageType, message.MessageId, deviceId);

        await args.UpdateCheckpointAsync();
    }

    private async Task RecordFailedIngressAsync(
        string? deviceId, string? messageId,
        string partitionId, string offset, DateTime enqueuedAt,
        string failureReason, string? errorMessage, string? rawPayload)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();

            db.FailedIngressMessages.Add(new FailedIngressMessage
            {
                SourceDeviceId = deviceId,
                MessageId = messageId,
                PartitionId = partitionId,
                Offset = offset,
                EnqueuedAt = enqueuedAt,
                FailureReason = failureReason,
                ErrorMessage = errorMessage,
                RawPayload = rawPayload,
                CreatedAt = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record failed ingress message");
        }
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Error in partition {PartitionId}: {Operation}",
            args.PartitionId, args.Operation);
        return Task.CompletedTask;
    }
}

public record OfflineCheckMessage(int DeviceId, DateTime ExpectedAfter);