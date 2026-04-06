namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Tests.Shared;
using Xunit;

public class TenantScopingTests
{
    [Fact]
    public async Task InternalUser_SeesAllDevices()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        // Assign both devices so they have active assignments
        await AssignDevice(db, seed1);
        await AssignDevice(db, seed2);

        var tenant = FakeTenantContext.Internal();
        var devices = await tenant.ApplyScope(db.Devices.AsNoTracking()).ToListAsync();

        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task CompanyUser_SeesOnlyOwnCompanyDevices()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        await AssignDevice(db, seed1);
        await AssignDevice(db, seed2);

        var tenant = FakeTenantContext.ForCompany(seed1.Company.Id);
        var devices = await tenant.ApplyScope(db.Devices.AsNoTracking()
            .Include(d => d.Assignments).ThenInclude(a => a.Site).ThenInclude(s => s.Customer))
            .ToListAsync();

        Assert.Single(devices);
        Assert.Equal(seed1.Device.Id, devices[0].Id);
    }

    [Fact]
    public async Task CompanyUser_CannotSeeOtherCompanyDevices()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        await AssignDevice(db, seed1);
        await AssignDevice(db, seed2);

        // Company user for seed2 should NOT see seed1 devices
        var tenant = FakeTenantContext.ForCompany(seed2.Company.Id);
        var devices = await tenant.ApplyScope(db.Devices.AsNoTracking()
            .Include(d => d.Assignments).ThenInclude(a => a.Site).ThenInclude(s => s.Customer))
            .ToListAsync();

        Assert.Single(devices);
        Assert.Equal(seed2.Device.Id, devices[0].Id);
    }

    [Fact]
    public async Task HomeownerUser_SeesOnlyOwnCustomerDevices()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        await AssignDevice(db, seed1);
        await AssignDevice(db, seed2);

        var tenant = FakeTenantContext.ForHomeowner(seed1.Customer.Id);
        var devices = await tenant.ApplyScope(db.Devices.AsNoTracking()
            .Include(d => d.Assignments).ThenInclude(a => a.Site))
            .ToListAsync();

        Assert.Single(devices);
        Assert.Equal(seed1.Device.Id, devices[0].Id);
    }

    [Fact]
    public async Task UnrecognizedRole_SeesNothing()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        await AssignDevice(db, seed1);

        // No role set
        var tenant = new FakeTenantContext { UserId = "nobody" };
        var devices = await tenant.ApplyScope(db.Devices.AsNoTracking()).ToListAsync();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task CompanyUser_SiteScoping_SeesOnlyOwnCompanySites()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        var tenant = FakeTenantContext.ForCompany(seed1.Company.Id);
        var sites = await tenant.ApplyScope(db.Sites.AsNoTracking()
            .Include(s => s.Customer))
            .ToListAsync();

        Assert.Single(sites);
        Assert.Equal(seed1.Site.Id, sites[0].Id);
    }

    [Fact]
    public async Task CompanyUser_AlarmScoping_SeesOnlyOwnCompanyAlarms()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        await AssignDevice(db, seed1);
        await AssignDevice(db, seed2);

        // Create alarms for both devices
        db.Alarms.Add(new Alarm
        {
            DeviceId = seed1.Device.Id,
            AlarmType = "HighWater",
            Severity = AlarmSeverity.Critical,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Alarms.Add(new Alarm
        {
            DeviceId = seed2.Device.Id,
            AlarmType = "LowVoltage",
            Severity = AlarmSeverity.Warning,
            Status = AlarmStatus.Active,
            SourceType = AlarmSourceType.TelemetryFallback,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var tenant = FakeTenantContext.ForCompany(seed1.Company.Id);
        var alarms = await tenant.ApplyScope(db.Alarms.AsNoTracking()
            .Include(a => a.Device).ThenInclude(d => d.Assignments).ThenInclude(a => a.Site).ThenInclude(s => s.Customer))
            .ToListAsync();

        Assert.Single(alarms);
        Assert.Equal("HighWater", alarms[0].AlarmType);
    }

    [Fact]
    public async Task CompanyUser_TelemetryScoping_SeesOnlyOwnCompanyTelemetry()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = seed1.Device.Id,
            MessageId = "msg-c1",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow,
            EnqueuedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            CompanyId = seed1.Company.Id,
        });
        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = seed2.Device.Id,
            MessageId = "msg-c2",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow,
            EnqueuedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            CompanyId = seed2.Company.Id,
        });
        await db.SaveChangesAsync();

        var tenant = FakeTenantContext.ForCompany(seed1.Company.Id);
        var telemetry = await tenant.ApplyScope(db.TelemetryHistory.AsNoTracking()).ToListAsync();

        Assert.Single(telemetry);
        Assert.Equal("msg-c1", telemetry[0].MessageId);
    }

    [Fact]
    public async Task UnassignedDevice_NotVisibleToCompanyUser()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);

        // Device not assigned — company user should not see it
        var tenant = FakeTenantContext.ForCompany(seed1.Company.Id);
        var devices = await tenant.ApplyScope(db.Devices.AsNoTracking()
            .Include(d => d.Assignments).ThenInclude(a => a.Site).ThenInclude(s => s.Customer))
            .ToListAsync();

        Assert.Empty(devices);
    }

    private static async Task AssignDevice(SentinelDbContext db, SeedData seed)
    {
        db.DeviceAssignments.Add(new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "test-user",
            AssignedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
