namespace SentinelBackend.Ingestion;

using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs.Processor;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Contracts;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Infrastructure.Persistence;
using Azure.Messaging.EventHubs;
using SentinelBackend.Domain.Enums;

public class TelemetryIngestionWorker : BackgroundService
{
    private readonly EventProcessorClient _processor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryIngestionWorker> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TelemetryIngestionWorker(
        EventProcessorClient processor,
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetryIngestionWorker> logger)
    {
        _processor = processor;
        _scopeFactory = scopeFactory;
        _logger = logger;
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
        var json = Encoding.UTF8.GetString(args.Data.EventBody);

        TelemetryMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TelemetryMessage>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize message: {Json}", json);
            await args.UpdateCheckpointAsync();
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.DeviceId))
        {
            _logger.LogWarning("Message missing deviceId — skipping");
            await args.UpdateCheckpointAsync();
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();

        // Upsert device (include LatestState so we can upsert it in the same SaveChanges)
        var device = await db.Devices
            .Include(d => d.LatestState)
            .FirstOrDefaultAsync(d => d.DeviceId == message.DeviceId);

        if (device is null)
        {
            device = new Device
            {
                DeviceId = message.DeviceId,
                Status = DeviceStatus.Unprovisioned,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Devices.Add(device);
        }
        else
        {
            device.UpdatedAt = DateTime.UtcNow;
        }

        // Upsert latest state via navigation — EF Core resolves Device.Id FK automatically
        var state = device.LatestState;
        if (state is null)
        {
            state = new LatestDeviceState();
            device.LatestState = state;
            db.LatestDeviceStates.Add(state);
        }

        state.LastSeenAt = message.TimestampUtc ?? DateTime.UtcNow;
        state.PanelVoltage = message.PanelVoltage;
        state.PumpCurrent = message.PumpCurrent;
        state.HighWaterAlarm = message.HighWaterAlarm;
        state.TemperatureC = message.TemperatureC;
        state.SignalRssi = message.SignalRssi;
        state.RuntimeSeconds = message.RuntimeSeconds;
        state.CycleCount = message.CycleCount;
        state.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Persisted state for device {DeviceId}", message.DeviceId
        );

        await args.UpdateCheckpointAsync();
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Error processing event");
        return Task.CompletedTask;
    }
}