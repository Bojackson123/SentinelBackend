namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Api.Workers;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Tests.Shared;
using Xunit;

public class OfflineCheckWorkerTests
{
    private static (IServiceScopeFactory scopeFactory, string dbName) CreateServices()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddScoped(_ =>
            new SentinelDbContext(new DbContextOptionsBuilder<SentinelDbContext>()
                .UseInMemoryDatabase(dbName).Options));
        services.AddScoped<IAlarmService>(sp =>
            new AlarmService(
                sp.GetRequiredService<SentinelDbContext>(),
                NullLogger<AlarmService>.Instance,
                new NullNotificationService()));
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IServiceScopeFactory>(), dbName);
    }

    private static SentinelDbContext OpenDb(string dbName) =>
        new(new DbContextOptionsBuilder<SentinelDbContext>()
            .UseInMemoryDatabase(dbName).Options);

    [Fact]
    public async Task StaleDeadline_NewerTelemetryArrived_SkipsOfflineMark()
    {
        var (scopeFactory, dbName) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            setupDb.DeviceConnectivityStates.Add(new DeviceConnectivityState
            {
                DeviceId = seed.Device.Id,
                LastMessageReceivedAt = DateTime.UtcNow,  // newer than deadline
                OfflineThresholdSeconds = 900,
                IsOffline = false,
                UpdatedAt = DateTime.UtcNow,
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new OfflineCheckWorker(
            scopeFactory,
            NullLogger<OfflineCheckWorker>.Instance);

        // Deadline from 20 minutes ago — but device sent telemetry just now
        var message = new OfflineCheckMessage(1, DateTime.UtcNow.AddMinutes(-20));
        await worker.EvaluateDeviceAsync(message);

        using var assertDb = OpenDb(dbName);
        var connectivity = await assertDb.DeviceConnectivityStates.FirstAsync();
        Assert.False(connectivity.IsOffline);
        Assert.Empty(await assertDb.Alarms.ToListAsync());
    }

    [Fact]
    public async Task DeviceTrulyOffline_MarkedOffline_AlarmRaised()
    {
        var (scopeFactory, dbName) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            setupDb.DeviceConnectivityStates.Add(new DeviceConnectivityState
            {
                DeviceId = seed.Device.Id,
                LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20),
                OfflineThresholdSeconds = 900,
                IsOffline = false,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new OfflineCheckWorker(
            scopeFactory,
            NullLogger<OfflineCheckWorker>.Instance);

        // ExpectedAfter is 5 minutes ago — device last message was 20 min ago, so it's truly offline
        var message = new OfflineCheckMessage(1, DateTime.UtcNow.AddMinutes(-5));
        await worker.EvaluateDeviceAsync(message);

        using var assertDb = OpenDb(dbName);
        var connectivity = await assertDb.DeviceConnectivityStates.FirstAsync();
        Assert.True(connectivity.IsOffline);

        var alarm = await assertDb.Alarms.FirstAsync();
        Assert.Equal("DeviceOffline", alarm.AlarmType);
        Assert.Equal(AlarmStatus.Active, alarm.Status);
    }

    [Fact]
    public async Task AlreadyOffline_NoDuplicateAlarm()
    {
        var (scopeFactory, dbName) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            setupDb.DeviceConnectivityStates.Add(new DeviceConnectivityState
            {
                DeviceId = seed.Device.Id,
                LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20),
                OfflineThresholdSeconds = 900,
                IsOffline = true,  // already marked offline
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new OfflineCheckWorker(
            scopeFactory,
            NullLogger<OfflineCheckWorker>.Instance);

        var message = new OfflineCheckMessage(1, DateTime.UtcNow.AddMinutes(-5));
        await worker.EvaluateDeviceAsync(message);

        using var assertDb = OpenDb(dbName);
        Assert.Empty(await assertDb.Alarms.ToListAsync());
    }

    [Fact]
    public async Task NonActiveDevice_Skipped()
    {
        var (scopeFactory, dbName) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Unprovisioned;  // not Active
            setupDb.DeviceConnectivityStates.Add(new DeviceConnectivityState
            {
                DeviceId = seed.Device.Id,
                LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20),
                OfflineThresholdSeconds = 900,
                IsOffline = false,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new OfflineCheckWorker(
            scopeFactory,
            NullLogger<OfflineCheckWorker>.Instance);

        var message = new OfflineCheckMessage(1, DateTime.UtcNow.AddMinutes(-5));
        await worker.EvaluateDeviceAsync(message);

        using var assertDb = OpenDb(dbName);
        var connectivity = await assertDb.DeviceConnectivityStates.FirstAsync();
        Assert.False(connectivity.IsOffline);
    }

    [Fact]
    public async Task MaintenanceWindow_SuppressesAlarm()
    {
        var (scopeFactory, dbName) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            setupDb.DeviceConnectivityStates.Add(new DeviceConnectivityState
            {
                DeviceId = seed.Device.Id,
                LastMessageReceivedAt = DateTime.UtcNow.AddMinutes(-20),
                OfflineThresholdSeconds = 900,
                IsOffline = false,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
            });

            var now = DateTime.UtcNow;
            setupDb.MaintenanceWindows.Add(new MaintenanceWindow
            {
                ScopeType = MaintenanceWindowScope.Device,
                DeviceId = seed.Device.Id,
                StartsAt = now.AddHours(-1),
                EndsAt = now.AddHours(1),
                Reason = "Planned maintenance",
                CreatedByUserId = "admin-1",
                CreatedAt = now,
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new OfflineCheckWorker(
            scopeFactory,
            NullLogger<OfflineCheckWorker>.Instance);

        var message = new OfflineCheckMessage(1, DateTime.UtcNow.AddMinutes(-5));
        await worker.EvaluateDeviceAsync(message);

        using var assertDb = OpenDb(dbName);
        var connectivity = await assertDb.DeviceConnectivityStates.FirstAsync();
        Assert.True(connectivity.IsOffline);
        Assert.True(connectivity.SuppressedByMaintenanceWindow);
        // No alarm raised because of maintenance window
        Assert.Empty(await assertDb.Alarms.ToListAsync());
    }

    [Fact]
    public async Task UnknownDeviceId_NoException()
    {
        var (scopeFactory, _) = CreateServices();

        var worker = new OfflineCheckWorker(
            scopeFactory,
            NullLogger<OfflineCheckWorker>.Instance);

        // Device ID 999 doesn't exist — should complete without error
        var message = new OfflineCheckMessage(999, DateTime.UtcNow.AddMinutes(-5));
        await worker.EvaluateDeviceAsync(message);
    }
}
