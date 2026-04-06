namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Exceptions;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Services;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Infrastructure.Repositories;
using SentinelBackend.Tests.Shared;
using Xunit;

public class DpsAllocationServiceTests
{
    private static (DpsAllocationService Service, SentinelDbContext Db) CreateService()
    {
        var db = TestDb.Create();
        var repo = new DeviceRepository(db);
        var options = Options.Create(new DpsOptions
        {
            IotHubHostName = "sentinel-iot-hub-dev.azure-devices.net",
            EnrollmentGroupPrimaryKey = "test-key",
            WebhookSecret = "test-secret",
        });
        var service = new DpsAllocationService(repo, options, NullLogger<DpsAllocationService>.Instance);
        return (service, db);
    }

    [Fact]
    public async Task Allocate_FirstBoot_TransitionsToUnprovisioned()
    {
        var (service, db) = CreateService();
        using (db)
        {
            db.Devices.Add(new Device
            {
                SerialNumber = "GP-202604-00050",
                Status = DeviceStatus.Manufactured,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var result = await service.AllocateAsync("GP-202604-00050", ["hub1"]);

            Assert.Equal("sentinel-iot-hub-dev.azure-devices.net", result.IotHubHostName);
            Assert.NotNull(result.InitialTwin);

            var updated = await db.Devices.FirstAsync(d => d.SerialNumber == "GP-202604-00050");
            Assert.Equal(DeviceStatus.Unprovisioned, updated.Status);
            Assert.Equal("GP-202604-00050", updated.DeviceId);
            Assert.NotNull(updated.ProvisionedAt);
        }
    }

    [Fact]
    public async Task Allocate_ReProvision_DoesNotResetStatus()
    {
        var (service, db) = CreateService();
        using (db)
        {
            db.Devices.Add(new Device
            {
                SerialNumber = "GP-202604-00051",
                DeviceId = "GP-202604-00051",
                Status = DeviceStatus.Active,
                ProvisionedAt = DateTime.UtcNow.AddDays(-10),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            await service.AllocateAsync("GP-202604-00051", ["hub1"]);

            var updated = await db.Devices.FirstAsync(d => d.SerialNumber == "GP-202604-00051");
            Assert.Equal(DeviceStatus.Active, updated.Status);
        }
    }

    [Fact]
    public async Task Allocate_UnknownDevice_Throws()
    {
        var (service, db) = CreateService();
        using (db)
        {
            await Assert.ThrowsAsync<DpsAllocationException>(
                () => service.AllocateAsync("NONEXISTENT", ["hub1"]));
        }
    }

    [Fact]
    public async Task Allocate_DecommissionedDevice_Throws()
    {
        var (service, db) = CreateService();
        using (db)
        {
            db.Devices.Add(new Device
            {
                SerialNumber = "GP-202604-00052",
                Status = DeviceStatus.Decommissioned,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<DpsAllocationException>(
                () => service.AllocateAsync("GP-202604-00052", ["hub1"]));
        }
    }

    [Fact]
    public async Task Allocate_SetsInitialTwinProperties()
    {
        var (service, db) = CreateService();
        using (db)
        {
            db.Devices.Add(new Device
            {
                SerialNumber = "GP-202604-00053",
                HardwareRevision = "rev-2.1",
                Status = DeviceStatus.Manufactured,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var result = await service.AllocateAsync("GP-202604-00053", ["hub1"]);

            Assert.NotNull(result.InitialTwin?.Tags);
            Assert.Equal("GP-202604-00053", result.InitialTwin.Tags["serialNumber"]);
            Assert.Equal("rev-2.1", result.InitialTwin.Tags["hardwareRevision"]);

            Assert.NotNull(result.InitialTwin.Properties?.Desired);
            Assert.Equal(300, result.InitialTwin.Properties.Desired["telemetryIntervalSeconds"]);
        }
    }
}
