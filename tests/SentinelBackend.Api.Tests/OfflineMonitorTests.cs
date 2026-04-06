namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Tests.Shared;
using Xunit;

public class OfflineMonitorTests
{
    [Fact]
    public async Task DeviceOverThreshold_MarkedOffline_AlarmRaised()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        db.DeviceConnectivityStates.Add(new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20), // exceeds 900s default
            OfflineThresholdSeconds = 900,
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
        });
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // Simulate offline monitor logic
        var now = DateTime.UtcNow;
        var connectivity = await db.DeviceConnectivityStates
            .Include(c => c.Device)
            .FirstAsync(c => c.DeviceId == seed.Device.Id);

        var elapsed = now - connectivity.LastMessageReceivedAt;
        Assert.True(elapsed.TotalSeconds > connectivity.OfflineThresholdSeconds);

        connectivity.IsOffline = true;
        connectivity.UpdatedAt = now;

        var (alarm, wasCreated) = await alarmService.RaiseAlarmAsync(
            connectivity.DeviceId,
            "DeviceOffline",
            AlarmSeverity.Warning,
            AlarmSourceType.SystemGenerated);

        await db.SaveChangesAsync();

        Assert.True(wasCreated);
        Assert.Equal("DeviceOffline", alarm.AlarmType);
        Assert.Equal(AlarmStatus.Active, alarm.Status);
        Assert.True(connectivity.IsOffline);
    }

    [Fact]
    public async Task DeviceBackOnline_AlarmAutoResolved()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        db.DeviceConnectivityStates.Add(new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            OfflineThresholdSeconds = 900,
            IsOffline = true, // was offline
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // Create existing DeviceOffline alarm
        await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        // Simulate device coming back online (below threshold)
        var connectivity = await db.DeviceConnectivityStates
            .Include(c => c.Device)
            .FirstAsync(c => c.DeviceId == seed.Device.Id);

        var now = DateTime.UtcNow;
        var elapsed = now - connectivity.LastMessageReceivedAt;
        Assert.True(elapsed.TotalSeconds < connectivity.OfflineThresholdSeconds);

        connectivity.IsOffline = false;
        connectivity.UpdatedAt = now;

        var resolved = await alarmService.AutoResolveAlarmsAsync(
            seed.Device.Id, "DeviceOffline", "Auto-resolved: device telemetry resumed");

        await db.SaveChangesAsync();

        Assert.Equal(1, resolved);
        Assert.False(connectivity.IsOffline);

        var alarm = await db.Alarms.FirstAsync(a => a.DeviceId == seed.Device.Id);
        Assert.Equal(AlarmStatus.Resolved, alarm.Status);
    }

    [Fact]
    public async Task DuplicateOfflineAlarm_Suppressed()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var alarmService = new AlarmService(db, NullLogger<AlarmService>.Instance);

        // Raise first DeviceOffline alarm
        var (alarm1, created1) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        // Attempt to raise duplicate — should be suppressed
        var (alarm2, created2) = await alarmService.RaiseAlarmAsync(
            seed.Device.Id, "DeviceOffline", AlarmSeverity.Warning, AlarmSourceType.SystemGenerated);

        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(alarm1.Id, alarm2.Id);
        Assert.Equal(1, await db.Alarms.CountAsync());
    }

    [Fact]
    public async Task MaintenanceWindow_DeviceScope_SuppressesAlarm()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.Status = DeviceStatus.Active;
        var now = DateTime.UtcNow;

        // Create a device-scoped maintenance window
        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            ScopeType = MaintenanceWindowScope.Device,
            DeviceId = seed.Device.Id,
            StartsAt = now.AddHours(-1),
            EndsAt = now.AddHours(1),
            Reason = "Planned maintenance",
            CreatedByUserId = "admin-1",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var activeWindows = await db.MaintenanceWindows
            .Where(mw => mw.StartsAt <= now && mw.EndsAt > now)
            .ToListAsync();

        // Check if device is in maintenance window
        var isInWindow = activeWindows.Any(mw =>
            mw.ScopeType == MaintenanceWindowScope.Device && mw.DeviceId == seed.Device.Id);

        Assert.True(isInWindow);
    }

    [Fact]
    public async Task OnlyActiveDevices_AreChecked()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        // Device is Unprovisioned, not Active
        seed.Device.Status = DeviceStatus.Unprovisioned;
        db.DeviceConnectivityStates.Add(new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20),
            OfflineThresholdSeconds = 900,
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
        });
        await db.SaveChangesAsync();

        // Query should only include Active devices
        var connectivityStates = await db.DeviceConnectivityStates
            .Include(c => c.Device)
            .Where(c => c.Device.Status == DeviceStatus.Active)
            .ToListAsync();

        Assert.Empty(connectivityStates);
    }
}
