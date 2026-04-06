namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class EntityModelTests
{
    [Fact]
    public async Task Company_CRUD_WithCustomers()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var company = await db.Companies
            .Include(c => c.Customers)
            .FirstAsync(c => c.Id == seed.Company.Id);

        Assert.Equal("Acme Pumps", company.Name);
        Assert.Single(company.Customers);
        Assert.Equal("Jane", company.Customers.First().FirstName);
    }

    [Fact]
    public async Task Customer_LinkedToCompany()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var customer = await db.Customers
            .Include(c => c.Company)
            .FirstAsync(c => c.Id == seed.Customer.Id);

        Assert.NotNull(customer.Company);
        Assert.Equal(seed.Company.Id, customer.CompanyId);
    }

    [Fact]
    public async Task Site_LinkedToCustomer()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var site = await db.Sites
            .Include(s => s.Customer)
            .FirstAsync(s => s.Id == seed.Site.Id);

        Assert.NotNull(site.Customer);
        Assert.Equal("America/Chicago", site.Timezone);
    }

    [Fact]
    public async Task Device_UniqueSerialNumber()
    {
        using var db = TestDb.Create();

        db.Devices.Add(new Device
        {
            SerialNumber = "GP-202604-00200",
            DeviceId = "GP-202604-00200",
            Status = DeviceStatus.Manufactured,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var count = await db.Devices.CountAsync(d => d.SerialNumber == "GP-202604-00200");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Device_HasNavigationProperties()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        // Add related entities
        db.LatestDeviceStates.Add(new LatestDeviceState
        {
            DeviceId = seed.Device.Id,
            LastTelemetryTimestampUtc = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.DeviceConnectivityStates.Add(new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var device = await db.Devices
            .Include(d => d.LatestState)
            .Include(d => d.ConnectivityState)
            .Include(d => d.Assignments)
            .FirstAsync(d => d.Id == seed.Device.Id);

        Assert.NotNull(device.LatestState);
        Assert.NotNull(device.ConnectivityState);
        Assert.Empty(device.Assignments);
    }

    [Fact]
    public async Task DeviceConnectivityState_OneToOne()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.DeviceConnectivityStates.Add(new DeviceConnectivityState
        {
            DeviceId = seed.Device.Id,
            LastMessageReceivedAt = DateTime.UtcNow,
            LastMessageType = "telemetry",
            LastBootId = "boot-123",
            IsOffline = false,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var connectivity = await db.DeviceConnectivityStates
            .FirstAsync(c => c.DeviceId == seed.Device.Id);

        Assert.Equal("telemetry", connectivity.LastMessageType);
        Assert.Equal("boot-123", connectivity.LastBootId);
        Assert.False(connectivity.IsOffline);
        Assert.Equal(900, connectivity.OfflineThresholdSeconds);
    }

    [Fact]
    public async Task CommandLog_Persisted()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.CommandLogs.Add(new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "reboot",
            Status = CommandStatus.Pending,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var cmd = await db.CommandLogs.FirstAsync();
        Assert.Equal("reboot", cmd.CommandType);
        Assert.Equal(CommandStatus.Pending, cmd.Status);
    }

    [Fact]
    public async Task MaintenanceWindow_Persisted()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            ScopeType = MaintenanceWindowScope.Site,
            SiteId = seed.Site.Id,
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddHours(2),
            Reason = "Firmware update",
            CreatedByUserId = "admin-1",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var mw = await db.MaintenanceWindows.FirstAsync();
        Assert.Equal(MaintenanceWindowScope.Site, mw.ScopeType);
        Assert.Equal("Firmware update", mw.Reason);
    }

    [Fact]
    public async Task FailedIngressMessage_Persisted()
    {
        using var db = TestDb.Create();

        db.FailedIngressMessages.Add(new FailedIngressMessage
        {
            SourceDeviceId = "GP-202604-00999",
            MessageId = "bad-msg-1",
            PartitionId = "0",
            Offset = "99999",
            EnqueuedAt = DateTime.UtcNow,
            FailureReason = "DeserializationFailure",
            ErrorMessage = "Unexpected token",
            RawPayload = "{bad json}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var failed = await db.FailedIngressMessages.FirstAsync();
        Assert.Equal("DeserializationFailure", failed.FailureReason);
        Assert.NotNull(failed.RawPayload);
    }

    [Fact]
    public async Task TelemetryHistory_HasRawPayloadBlobUri()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = seed.Device.Id,
            MessageId = "msg-blob",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow,
            EnqueuedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            RawPayloadBlobUri = "https://storage.blob.core.windows.net/raw-telemetry/GP-202604-00001/2026/04/05/msg-blob.json",
        });
        await db.SaveChangesAsync();

        var t = await db.TelemetryHistory.FirstAsync(t => t.MessageId == "msg-blob");
        Assert.Contains("raw-telemetry", t.RawPayloadBlobUri);
    }

    [Fact]
    public async Task TwoCompanyHierarchies_Isolated()
    {
        using var db = TestDb.Create();
        var seed1 = await TestDb.SeedFullHierarchyAsync(db);
        var seed2 = await TestDb.SeedSecondHierarchyAsync(db);

        Assert.NotEqual(seed1.Company.Id, seed2.Company.Id);
        Assert.NotEqual(seed1.Customer.Id, seed2.Customer.Id);
        Assert.NotEqual(seed1.Site.Id, seed2.Site.Id);
        Assert.NotEqual(seed1.Device.Id, seed2.Device.Id);

        Assert.Equal(2, await db.Companies.CountAsync());
        Assert.Equal(2, await db.Customers.CountAsync());
        Assert.Equal(2, await db.Sites.CountAsync());
        Assert.Equal(2, await db.Devices.CountAsync());
    }
}
