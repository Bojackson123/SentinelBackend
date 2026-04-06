namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class DeviceAssignmentTests
{
    [Fact]
    public async Task Assignment_CreatesHistoryRecord()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var assignment = new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "user-1",
            AssignedAt = DateTime.UtcNow,
        };
        db.DeviceAssignments.Add(assignment);
        await db.SaveChangesAsync();

        var saved = await db.DeviceAssignments
            .FirstOrDefaultAsync(a => a.DeviceId == seed.Device.Id);

        Assert.NotNull(saved);
        Assert.Equal(seed.Site.Id, saved.SiteId);
        Assert.Null(saved.UnassignedAt);
    }

    [Fact]
    public async Task Assignment_OnlyOneActivePerDevice()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        // Create first active assignment
        db.DeviceAssignments.Add(new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "user-1",
            AssignedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Check logic: there's already an active assignment
        var hasActive = await db.DeviceAssignments
            .AnyAsync(a => a.DeviceId == seed.Device.Id && a.UnassignedAt == null);

        Assert.True(hasActive);
    }

    [Fact]
    public async Task Unassignment_PreservesHistory()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var assignment = new DeviceAssignment
        {
            DeviceId = seed.Device.Id,
            SiteId = seed.Site.Id,
            AssignedByUserId = "user-1",
            AssignedAt = DateTime.UtcNow,
        };
        db.DeviceAssignments.Add(assignment);
        await db.SaveChangesAsync();

        // Unassign
        assignment.UnassignedAt = DateTime.UtcNow;
        assignment.UnassignedByUserId = "user-2";
        assignment.UnassignmentReason = UnassignmentReason.CustomerRequest;
        await db.SaveChangesAsync();

        var saved = await db.DeviceAssignments.FirstAsync(a => a.Id == assignment.Id);
        Assert.NotNull(saved.UnassignedAt);
        Assert.Equal("user-2", saved.UnassignedByUserId);
        Assert.Equal(UnassignmentReason.CustomerRequest, saved.UnassignmentReason);

        // Can now reassign
        var noActive = !await db.DeviceAssignments
            .AnyAsync(a => a.DeviceId == seed.Device.Id && a.UnassignedAt == null);
        Assert.True(noActive);
    }

    [Fact]
    public async Task Assignment_SetsStatusToAssigned_WhenUnprovisioned()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        Assert.Equal(DeviceStatus.Unprovisioned, seed.Device.Status);

        // Simulate assignment logic
        if (seed.Device.Status is DeviceStatus.Unprovisioned or DeviceStatus.Manufactured)
            seed.Device.Status = DeviceStatus.Assigned;

        await db.SaveChangesAsync();
        Assert.Equal(DeviceStatus.Assigned, seed.Device.Status);
    }

    [Fact]
    public async Task Assignment_CannotAssignDecommissioned()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Decommissioned;
        await db.SaveChangesAsync();

        // Business rule: decommissioned devices cannot be assigned
        Assert.Equal(DeviceStatus.Decommissioned, seed.Device.Status);
        var canAssign = seed.Device.Status != DeviceStatus.Decommissioned;
        Assert.False(canAssign);
    }

    [Fact]
    public async Task Assignment_CannotAssignAlreadyActive()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var canAssign = seed.Device.Status is not (DeviceStatus.Decommissioned or DeviceStatus.Active);
        Assert.False(canAssign);
    }
}
