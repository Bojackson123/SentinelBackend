namespace SentinelBackend.Ingestion.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;
using SentinelBackend.Tests.Shared;
using Xunit;

/// <summary>
/// Tests for telemetry-fallback alarm detection during ingestion (Phase 5).
/// Validates that HighWater alarms are raised/resolved based on telemetry data.
/// </summary>
public class TelemetryAlarmTests
{
    [Fact]
    public async Task HighWaterAlarm_True_RaisesAlarm()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        seed.Device.DeviceId = "GP-202604-00001";
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // Simulate what ingestion worker does when HighWaterAlarm == true
        var (alarm, wasCreated) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id,
            "HighWater",
            AlarmSeverity.Critical,
            AlarmSourceType.TelemetryFallback,
            triggerMessageId: "msg-001");

        Assert.True(wasCreated);
        Assert.Equal("HighWater", alarm.AlarmType);
        Assert.Equal(AlarmSeverity.Critical, alarm.Severity);
        Assert.Equal(AlarmSourceType.TelemetryFallback, alarm.SourceType);
        Assert.Equal("msg-001", alarm.TriggerMessageId);
    }

    [Fact]
    public async Task HighWaterAlarm_False_AutoResolvesAlarm()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // First create an active HighWater alarm
        await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        // Simulate ingestion receiving HighWaterAlarm = false
        var resolved = await alarmService.AutoResolveAlarmsAsync(
            seed.Device.Id, "HighWater", "Auto-resolved: high water condition cleared");

        Assert.Equal(1, resolved);

        var alarm = await db.Alarms.FirstAsync();
        Assert.Equal(AlarmStatus.Resolved, alarm.Status);
        Assert.NotNull(alarm.ResolvedAt);
    }

    [Fact]
    public async Task HighWaterAlarm_Repeated_True_SuppressesDuplicate()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // First telemetry with HighWaterAlarm = true
        var (alarm1, created1) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical,
            AlarmSourceType.TelemetryFallback, triggerMessageId: "msg-001");

        // Second telemetry with HighWaterAlarm = true
        var (alarm2, created2) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical,
            AlarmSourceType.TelemetryFallback, triggerMessageId: "msg-002");

        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(alarm1.Id, alarm2.Id);
    }

    [Fact]
    public async Task HighWaterAlarm_CycleRaisesNewAlarm_AfterResolution()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // Raise and resolve first alarm
        var (alarm1, _) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        await alarmService.AutoResolveAlarmsAsync(seed.Device.Id, "HighWater");

        // Raise a new alarm (condition recurred)
        var (alarm2, wasCreated) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.True(wasCreated);
        Assert.NotEqual(alarm1.Id, alarm2.Id);
        Assert.Equal(2, await db.Alarms.CountAsync());
    }

    [Fact]
    public async Task HighWaterAlarm_WithAssignment_PopulatesOwnership()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        db.DeviceAssignments.Add(new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "tech-1",
            AssignedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        var (alarm, _) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "HighWater", AlarmSeverity.Critical, AlarmSourceType.TelemetryFallback);

        Assert.Equal(seed.Site.Id, alarm.SiteId);
        Assert.Equal(seed.Customer.Id, alarm.CustomerId);
        Assert.Equal(seed.Company.Id, alarm.CompanyId);
    }

    [Fact]
    public async Task AutoResolve_NoOp_WhenNoActiveAlarms()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // No alarms exist — should return 0  without error
        var resolved = await alarmService.AutoResolveAlarmsAsync(
            seed.Device.Id, "HighWater", "Auto-resolved: high water condition cleared");

        Assert.Equal(0, resolved);
    }
}
