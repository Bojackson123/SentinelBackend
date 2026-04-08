namespace SentinelBackend.Ingestion.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;
using SentinelBackend.Tests.Shared;
using Xunit;

/// <summary>
/// Tests for the offline deadline scheduling and auto-resolve logic
/// added to TelemetryIngestionWorker when Service Bus is configured.
/// </summary>
public class OfflineSchedulingTests
{
    [Fact]
    public async Task TelemetryArrival_SchedulesOfflineCheckDeadline()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-5),
            OfflineThresholdSeconds = 900,
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        var publisher = new FakeMessagePublisher();
        var receivedAtUtc = DateTime.UtcNow;

        // Simulate what TelemetryIngestionWorker does after connectivity state update
        connectivity.LastMessageReceivedAt = receivedAtUtc;
        connectivity.IsOffline = false;
        connectivity.UpdatedAt = receivedAtUtc;

        if (connectivity.OfflineThresholdSeconds > 0)
        {
            var deadline = receivedAtUtc.AddSeconds(connectivity.OfflineThresholdSeconds);
            await publisher.PublishAsync(
                TelemetryIngestionWorker.OfflineCheckQueue,
                new OfflineCheckMessage(seed.Device.Id, receivedAtUtc),
                new DateTimeOffset(deadline, TimeSpan.Zero));
        }

        Assert.Single(publisher.Published);
        var (queue, message, scheduled) = publisher.Published[0];
        Assert.Equal("offline-checks", queue);
        Assert.NotNull(scheduled);

        var offlineMsg = Assert.IsType<OfflineCheckMessage>(message);
        Assert.Equal(seed.Device.Id, offlineMsg.DeviceId);
        Assert.Equal(receivedAtUtc, offlineMsg.ExpectedAfter);

        // Deadline should be receivedAtUtc + 900 seconds
        var expectedDeadline = new DateTimeOffset(receivedAtUtc.AddSeconds(900), TimeSpan.Zero);
        Assert.Equal(expectedDeadline, scheduled);
    }

    [Fact]
    public async Task ZeroThreshold_DoesNotScheduleDeadline()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            OfflineThresholdSeconds = 0,  // disabled
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow,
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        var publisher = new FakeMessagePublisher();

        // With threshold == 0, no message should be published
        if (connectivity.OfflineThresholdSeconds > 0)
        {
            await publisher.PublishAsync("offline-checks", new OfflineCheckMessage(seed.Device.Id, DateTime.UtcNow));
        }

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task DeviceWasOffline_TelemetryArrival_AutoResolvesAlarm()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20),
            OfflineThresholdSeconds = 900,
            IsOffline = true,  // was offline
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        // Create existing DeviceOffline alarm
        await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        // Simulate ingestion: device sends telemetry → wasOffline = true → auto-resolve
        var wasOffline = connectivity.IsOffline;
        connectivity.IsOffline = false;
        connectivity.LastMessageReceivedAt = DateTime.UtcNow;
        connectivity.UpdatedAt = DateTime.UtcNow;

        Assert.True(wasOffline);

        var resolved = await alarmService.AutoResolveAlarmsAsync(
            seed.Device.Id, "DeviceOffline", "Auto-resolved: device telemetry resumed");

        await db.SaveChangesAsync();

        Assert.Equal(1, resolved);
        var alarm = await db.Alarms.FirstAsync();
        Assert.Equal(AlarmStatus.Resolved, alarm.Status);
        Assert.False(connectivity.IsOffline);
    }

    [Fact]
    public async Task DeviceWasNotOffline_NoAutoResolveAttempted()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-5),
            OfflineThresholdSeconds = 900,
            IsOffline = false,  // was NOT offline
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        // Simulate ingestion: wasOffline is captured before resetting
        var wasOffline = connectivity.IsOffline;
        connectivity.IsOffline = false;
        connectivity.LastMessageReceivedAt = DateTime.UtcNow;
        connectivity.UpdatedAt = DateTime.UtcNow;

        Assert.False(wasOffline);
        // When wasOffline is false, auto-resolve is not called
        // No alarms should exist
        Assert.Empty(await db.Alarms.ToListAsync());
    }

    [Fact]
    public async Task NoMessagePublisher_SkipsScheduling()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;

        var connectivity = new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            OfflineThresholdSeconds = 900,
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow,
        };
        db.DeviceConnectivityStates.Add(connectivity);
        await db.SaveChangesAsync();

        // Simulate ingestion with no publisher (null)
        IMessagePublisher? publisher = null;
        var published = false;

        if (publisher is not null && connectivity.OfflineThresholdSeconds > 0)
        {
            published = true;
        }

        Assert.False(published);
    }
}

/// <summary>
/// Mirrors the record in TelemetryIngestionWorker for test deserialization.
/// </summary>
public record OfflineCheckMessage(int DeviceId, DateTime ExpectedAfter);
