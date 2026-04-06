namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class TelemetryDeduplicationTests
{
    [Fact]
    public async Task LatestState_OnlyUpdatedWhenNewer()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var state = new LatestDeviceState
        {
            DeviceId = seed.Device.Id,
            LastTelemetryTimestampUtc = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc),
            PanelVoltage = 240.0,
            UpdatedAt = DateTime.UtcNow,
        };
        db.LatestDeviceStates.Add(state);
        await db.SaveChangesAsync();

        // Older message should NOT overwrite
        var olderTimestamp = new DateTime(2026, 4, 5, 11, 0, 0, DateTimeKind.Utc);
        if (olderTimestamp > state.LastTelemetryTimestampUtc)
        {
            state.PanelVoltage = 200.0;
        }
        Assert.Equal(240.0, state.PanelVoltage);

        // Newer message SHOULD overwrite
        var newerTimestamp = new DateTime(2026, 4, 5, 13, 0, 0, DateTimeKind.Utc);
        if (newerTimestamp > state.LastTelemetryTimestampUtc)
        {
            state.LastTelemetryTimestampUtc = newerTimestamp;
            state.PanelVoltage = 241.5;
        }
        await db.SaveChangesAsync();

        Assert.Equal(241.5, state.PanelVoltage);
        Assert.Equal(newerTimestamp, state.LastTelemetryTimestampUtc);
    }

    [Fact]
    public async Task LatestState_PreservesAllFieldsOnUpdate()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var state = new LatestDeviceState
        {
            DeviceId = seed.Device.Id,
            LastTelemetryTimestampUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow,
        };
        db.LatestDeviceStates.Add(state);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        state.LastTelemetryTimestampUtc = now;
        state.LastMessageId = "msg-100";
        state.LastBootId = "boot-abc";
        state.LastSequenceNumber = 42;
        state.PanelVoltage = 238.5;
        state.PumpCurrent = 3.2;
        state.PumpRunning = true;
        state.HighWaterAlarm = false;
        state.TemperatureC = 25.3;
        state.SignalRssi = -67;
        state.RuntimeSeconds = 3600;
        state.ReportedCycleCount = 150;
        state.DerivedCycleCount = 152;
        state.UpdatedAt = now;
        await db.SaveChangesAsync();

        var loaded = await db.LatestDeviceStates.FirstAsync(s => s.DeviceId == seed.Device.Id);
        Assert.Equal("msg-100", loaded.LastMessageId);
        Assert.Equal("boot-abc", loaded.LastBootId);
        Assert.Equal(42, loaded.LastSequenceNumber);
        Assert.Equal(238.5, loaded.PanelVoltage);
        Assert.Equal(3.2, loaded.PumpCurrent);
        Assert.True(loaded.PumpRunning);
        Assert.False(loaded.HighWaterAlarm);
        Assert.Equal(25.3, loaded.TemperatureC);
        Assert.Equal(-67, loaded.SignalRssi);
        Assert.Equal(3600, loaded.RuntimeSeconds);
        Assert.Equal(150, loaded.ReportedCycleCount);
        Assert.Equal(152L, loaded.DerivedCycleCount);
    }

    [Fact]
    public async Task TelemetryHistory_DuplicateDetection_ByDeviceIdAndMessageId()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = seed.Device.Id,
            MessageId = "msg-001",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow,
            EnqueuedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var isDuplicate = await db.TelemetryHistory
            .AnyAsync(t => t.DeviceId == seed.Device.Id && t.MessageId == "msg-001");
        Assert.True(isDuplicate);

        var isNotDuplicate = await db.TelemetryHistory
            .AnyAsync(t => t.DeviceId == seed.Device.Id && t.MessageId == "msg-002");
        Assert.False(isNotDuplicate);
    }

    [Fact]
    public async Task TelemetryHistory_SameMessageIdDifferentDevice_NotDuplicate()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = seed1.Device.Id,
            MessageId = "msg-shared",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow,
            EnqueuedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Same messageId but different device — not a duplicate
        var isDuplicate = await db.TelemetryHistory
            .AnyAsync(t => t.DeviceId == seed2.Device.Id && t.MessageId == "msg-shared");
        Assert.False(isDuplicate);
    }

    [Fact]
    public async Task TelemetryHistory_OwnershipSnapshot_Stamped()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.DeviceAssignments.Add(new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "user-1",
            AssignedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var assignment = await db.DeviceAssignments
            .Where(a => a.DeviceId == seed.Device.Id && a.UnassignedAt == null)
            .Select(a => new { a.Id, a.SiteId, a.Site.CustomerId, a.Site.Customer.CompanyId })
            .FirstAsync();

        var telemetry = new TelemetryHistory
        {
            DeviceId = seed.Device.Id,
            MessageId = "msg-owned",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow,
            EnqueuedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            DeviceAssignmentId = assignment.Id,
            SiteId = assignment.SiteId,
            CustomerId = assignment.CustomerId,
            CompanyId = assignment.CompanyId,
        };
        db.TelemetryHistory.Add(telemetry);
        await db.SaveChangesAsync();

        var saved = await db.TelemetryHistory.FirstAsync(t => t.MessageId == "msg-owned");
        Assert.Equal(seed.Site.Id, saved.SiteId);
        Assert.Equal(seed.Customer.Id, saved.CustomerId);
        Assert.Equal(seed.Company.Id, saved.CompanyId);
    }
}
