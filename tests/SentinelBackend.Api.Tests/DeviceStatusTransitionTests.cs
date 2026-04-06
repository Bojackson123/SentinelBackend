namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class DeviceStatusTransitionTests
{
    [Fact]
    public async Task FullLifecycle_Manufactured_To_Decommissioned()
    {
        using var db = TestDb.Create();

        var device = new Device
        {
            SerialNumber = "GP-202604-00100",
            Status = DeviceStatus.Manufactured,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        // Manufactured → Unprovisioned (DPS first boot)
        device.Status = DeviceStatus.Unprovisioned;
        device.DeviceId = device.SerialNumber;
        device.ProvisionedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(DeviceStatus.Unprovisioned, device.Status);
        Assert.NotNull(device.ProvisionedAt);

        // Unprovisioned → Assigned (assignment)
        device.Status = DeviceStatus.Assigned;
        await db.SaveChangesAsync();
        Assert.Equal(DeviceStatus.Assigned, device.Status);

        // Assigned → Active (first telemetry)
        device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();
        Assert.Equal(DeviceStatus.Active, device.Status);

        // Active → Decommissioned (admin action)
        device.Status = DeviceStatus.Decommissioned;
        await db.SaveChangesAsync();
        Assert.Equal(DeviceStatus.Decommissioned, device.Status);
    }

    [Fact]
    public async Task FirstTelemetry_Transitions_AssignedToActive()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Assigned;
        await db.SaveChangesAsync();

        // Simulate ingestion worker transition
        if (seed.Device.Status == DeviceStatus.Assigned)
        {
            seed.Device.Status = DeviceStatus.Active;
        }
        await db.SaveChangesAsync();

        Assert.Equal(DeviceStatus.Active, seed.Device.Status);
    }

    [Fact]
    public async Task FirstTelemetry_DoesNotTransition_IfAlreadyActive()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        // Should not change
        if (seed.Device.Status == DeviceStatus.Assigned)
        {
            seed.Device.Status = DeviceStatus.Active;
        }
        Assert.Equal(DeviceStatus.Active, seed.Device.Status);
    }

    [Fact]
    public async Task DpsAllocation_Manufactured_To_Unprovisioned()
    {
        using var db = TestDb.Create();

        var device = new Device
        {
            SerialNumber = "GP-202604-00101",
            Status = DeviceStatus.Manufactured,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        // Simulate DPS allocation
        var isFirstBoot = device.Status == DeviceStatus.Manufactured;
        Assert.True(isFirstBoot);

        device.DeviceId = device.SerialNumber;
        device.ProvisionedAt = DateTime.UtcNow;
        if (isFirstBoot)
            device.Status = DeviceStatus.Unprovisioned;

        await db.SaveChangesAsync();

        Assert.Equal(DeviceStatus.Unprovisioned, device.Status);
        Assert.Equal(device.SerialNumber, device.DeviceId);
    }

    [Fact]
    public async Task DpsReProvision_DoesNotResetStatus()
    {
        using var db = TestDb.Create();

        var device = new Device
        {
            SerialNumber = "GP-202604-00102",
            DeviceId = "GP-202604-00102",
            Status = DeviceStatus.Active,
            ProvisionedAt = DateTime.UtcNow.AddDays(-30),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        // Simulate re-provision — status should remain Active
        var isFirstBoot = device.Status == DeviceStatus.Manufactured;
        Assert.False(isFirstBoot);

        device.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Assert.Equal(DeviceStatus.Active, device.Status);
    }

    [Fact]
    public async Task SoftDelete_SetsIsDeletedAndTimestamp()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Device.IsDeleted = true;
        seed.Device.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Query filter should exclude soft-deleted devices
        var visible = await db.Devices.ToListAsync();
        Assert.Empty(visible);

        // IgnoreQueryFilters should show it
        var all = await db.Devices.IgnoreQueryFilters().ToListAsync();
        Assert.Single(all);
        Assert.True(all[0].IsDeleted);
    }

    [Fact]
    public async Task SoftDelete_Company_ExcludedByQueryFilter()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Company.IsDeleted = true;
        seed.Company.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var visible = await db.Companies.ToListAsync();
        Assert.Empty(visible);

        var all = await db.Companies.IgnoreQueryFilters().ToListAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task SoftDelete_Site_ExcludedByQueryFilter()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        seed.Site.IsDeleted = true;
        seed.Site.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var visible = await db.Sites.ToListAsync();
        Assert.Empty(visible);

        var all = await db.Sites.IgnoreQueryFilters().ToListAsync();
        Assert.Single(all);
    }
}
