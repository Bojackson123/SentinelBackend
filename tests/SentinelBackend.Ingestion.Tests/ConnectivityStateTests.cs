namespace SentinelBackend.Ingestion.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class ConnectivityStateTests
{
    [Fact]
    public async Task ConnectivityState_UpdatesIndependently_FromLatestState()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var latestState = new LatestDeviceState
        {
            DeviceId = seed.Device.Id,
            LastTelemetryTimestampUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow,
        };
        db.LatestDeviceStates.Add(latestState);

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            LastMessageType = "telemetry",
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow,
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        // Old message arrives — should update connectivity but NOT latest state
        var oldTimestamp = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        connectivity.LastMessageReceivedAt = now;
        connectivity.UpdatedAt = now;

        if (oldTimestamp > latestState.LastTelemetryTimestampUtc)
        {
            latestState.LastTelemetryTimestampUtc = oldTimestamp;
        }

        await db.SaveChangesAsync();

        Assert.Equal(new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc), latestState.LastTelemetryTimestampUtc);
        Assert.Equal(now, connectivity.LastMessageReceivedAt);
    }

    [Fact]
    public async Task FailedIngress_RecordedDurably()
    {
        using var db = TestDb.Create();

        db.FailedIngressMessages.Add(new FailedIngressMessage
        {
            SourceDeviceId = "unknown-device",
            PartitionId = "0",
            Offset = "12345",
            EnqueuedAt = DateTime.UtcNow,
            FailureReason = "UnknownDevice",
            ErrorMessage = "No device record",
            RawPayload = "{\"test\": true}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var count = await db.FailedIngressMessages.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OfflineThreshold_DefaultsTo900Seconds()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        Assert.Equal(900, connectivity.OfflineThresholdSeconds);
    }

    [Fact]
    public async Task ConnectivityState_TelemetryMessage_UpdatesLastTelemetryReceivedAt()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        // Simulate telemetry message
        var now = DateTime.UtcNow;
        connectivity.LastMessageReceivedAt = now;
        connectivity.LastMessageType = "telemetry";
        connectivity.LastTelemetryReceivedAt = now;
        connectivity.IsOffline = false;
        connectivity.UpdatedAt = now;
        await db.SaveChangesAsync();

        Assert.Equal(now, connectivity.LastTelemetryReceivedAt);
        Assert.Equal("telemetry", connectivity.LastMessageType);
        Assert.False(connectivity.IsOffline);
    }

    [Fact]
    public async Task FailedIngress_MultipleReasons_AllStored()
    {
        using var db = TestDb.Create();

        db.FailedIngressMessages.Add(new FailedIngressMessage
        {
            SourceDeviceId = "dev-1",
            PartitionId = "0",
            Offset = "100",
            EnqueuedAt = DateTime.UtcNow,
            FailureReason = "DeserializationFailure",
            ErrorMessage = "Bad JSON",
            RawPayload = "{bad}",
            CreatedAt = DateTime.UtcNow,
        });
        db.FailedIngressMessages.Add(new FailedIngressMessage
        {
            SourceDeviceId = null,
            MessageId = "msg-1",
            PartitionId = "1",
            Offset = "200",
            EnqueuedAt = DateTime.UtcNow,
            FailureReason = "MissingDeviceIdentity",
            ErrorMessage = "No iothub-connection-device-id",
            RawPayload = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        db.FailedIngressMessages.Add(new FailedIngressMessage
        {
            SourceDeviceId = "dev-2",
            MessageId = "msg-2",
            PartitionId = "2",
            Offset = "300",
            EnqueuedAt = DateTime.UtcNow,
            FailureReason = "MissingTimestamp",
            ErrorMessage = "Required envelope field timestampUtc missing",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var reasons = await db.FailedIngressMessages
            .Select(f => f.FailureReason)
            .ToListAsync();

        Assert.Equal(3, reasons.Count);
        Assert.Contains("DeserializationFailure", reasons);
        Assert.Contains("MissingDeviceIdentity", reasons);
        Assert.Contains("MissingTimestamp", reasons);
    }
}
