namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;
using SentinelBackend.Tests.Shared;
using Xunit;

public class AlarmServiceTests
{
    [Fact]
    public async Task RaiseAlarm_CreatesNewAlarm_WithActiveStatus()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, wasCreated) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.True(wasCreated);
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.Equal("HighWater", alarm.AlarmType);
        Assert.Equal(seed.Device.Id, alarm.DeviceId);

        var events = await db.AlarmEvents.Where(e => e.AlarmId == alarm.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal("Created", events[0].EventType);
    }

    [Fact]
    public async Task RaiseAlarm_SuppressesDuplicate_WhenActiveAlarmExists()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (firstAlarm, firstCreated) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        var (secondAlarm, secondCreated) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(firstAlarm.Id, secondAlarm.Id);

        // Only one alarm should exist
        Assert.Equal(1, await db.Alarms.CountAsync());
    }

    [Fact]
    public async Task RaiseAlarm_SuppressesDuplicate_WhenAcknowledgedAlarmExists()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        alarm.Status = AlarmStatus.Acknowledged;
        await db.SaveChangesAsync();

        var (duplicate, wasCreated) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.False(wasCreated);
        Assert.Equal(alarm.Id, duplicate.Id);
    }

    [Fact]
    public async Task RaiseAlarm_AllowsNewAlarm_AfterPreviousResolved()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (firstAlarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        await service.ResolveAlarmAsync(firstAlarm.Id);

        var (secondAlarm, wasCreated) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.True(wasCreated);
        Assert.NotEqual(firstAlarm.Id, secondAlarm.Id);
        Assert.Equal(2, await db.Alarms.CountAsync());
    }

    [Fact]
    public async Task RaiseAlarm_AllowsDifferentAlarmTypes_OnSameDevice()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm1, created1) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        var (alarm2, created2) = await service.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        Assert.True(created1);
        Assert.True(created2);
        Assert.NotEqual(alarm1.Id, alarm2.Id);
    }

    [Fact]
    public async Task RaiseAlarm_PopulatesOwnershipSnapshot_WhenAssigned()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        // Create assignment
        var assignment = new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "tech-1",
            AssignedAt = DateTime.UtcNow,
        };
        db.DeviceAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());
        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.Equal(assignment.Id, alarm.DeviceAssignmentId);
        Assert.Equal(seed.Site.Id, alarm.SiteId);
        Assert.Equal(seed.Customer.Id, alarm.CustomerId);
        Assert.Equal(seed.Company.Id, alarm.CompanyId);
    }

    [Fact]
    public async Task RaiseAlarm_NullOwnership_WhenUnassigned()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        Assert.Null(alarm.DeviceAssignmentId);
        Assert.Null(alarm.SiteId);
        Assert.Null(alarm.CustomerId);
        Assert.Null(alarm.CompanyId);
    }

    [Fact]
    public async Task ResolveAlarm_TransitionsToResolved()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        var resolved = await service.ResolveAlarmAsync(alarm.Id, "user-1", "Condition cleared");

        Assert.NotNull(resolved);
        Assert.Equal(AlarmStatus.Resolved, resolved!.Status);
        Assert.NotNull(resolved.ResolvedAt);

        var events = await db.AlarmEvents.Where(e => e.AlarmId == alarm.Id).OrderBy(e => e.CreatedAt).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal("Created", events[0].EventType);
        Assert.Equal("Resolved", events[1].EventType);
        Assert.Equal("user-1", events[1].UserId);
    }

    [Fact]
    public async Task ResolveAlarm_ReturnsNull_WhenNotFound()
    {
        using var db = TestDb.Create();
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var result = await service.ResolveAlarmAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAlarm_NoOp_WhenAlreadyResolved()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        await service.ResolveAlarmAsync(alarm.Id);
        var result = await service.ResolveAlarmAsync(alarm.Id);

        Assert.NotNull(result);
        Assert.Equal(AlarmStatus.Resolved, result!.Status);

        // Should only have Created + one Resolved event, not two
        var resolvedEvents = await db.AlarmEvents
            .Where(e => e.AlarmId == alarm.Id && e.EventType == "Resolved")
            .CountAsync();
        Assert.Equal(1, resolvedEvents);
    }

    [Fact]
    public async Task AutoResolve_ResolvesAllActiveAlarms_ForDeviceAndType()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        // Create two alarms of same type (simulate earlier resolved + new)
        var (alarm1, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        // Also create a different alarm type that should NOT be resolved
        var (otherAlarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        var count = await service.AutoResolveAlarmsAsync(seed.Device.Id, "HighWater");

        Assert.Equal(1, count);

        var refreshedAlarm1 = await db.Alarms.FindAsync(alarm1.Id);
        Assert.Equal(AlarmStatus.Resolved, refreshedAlarm1!.Status);

        var refreshedOther = await db.Alarms.FindAsync(otherAlarm.Id);
        Assert.Equal(AlarmStatus.Active, refreshedOther!.Status);
    }

    [Fact]
    public async Task AutoResolve_ReturnsZero_WhenNoActiveAlarms()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var count = await service.AutoResolveAlarmsAsync(seed.Device.Id, "HighWater");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AutoResolve_ResolvesAcknowledgedAndSuppressedAlarms()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        alarm.Status = AlarmStatus.Suppressed;
        await db.SaveChangesAsync();

        var count = await service.AutoResolveAlarmsAsync(seed.Device.Id, "HighWater", "Condition cleared");

        Assert.Equal(1, count);
        var refreshed = await db.Alarms.FindAsync(alarm.Id);
        Assert.Equal(AlarmStatus.Resolved, refreshed!.Status);
    }

    [Fact]
    public async Task RaiseAlarm_StoresTriggerMessageId()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        var service = new AlarmService(db, NullLogger<AlarmService>.Instance, new NullNotificationService());

        var (alarm, _) = await service.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical,
            AlarmSourceType.TelemetryFallback, triggerMessageId: "msg-001");

        Assert.Equal("msg-001", alarm.TriggerMessageId);
    }
}
