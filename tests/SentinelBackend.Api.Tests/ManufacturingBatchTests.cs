namespace SentinelBackend.Api.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Application.Services;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Repositories;
using SentinelBackend.Tests.Shared;
using Xunit;

public class ManufacturingBatchTests
{
    private sealed class FakeDpsEnrollmentService : IDpsEnrollmentService
    {
        public string DeriveDeviceKey(string registrationId) => $"derived-key-{registrationId}";
    }

    private ManufacturingBatchService CreateService(out Infrastructure.Persistence.SentinelDbContext db)
    {
        db = TestDb.Create();
        var repo = new DeviceRepository(db);
        return new ManufacturingBatchService(
            repo,
            new FakeDpsEnrollmentService(),
            NullLogger<ManufacturingBatchService>.Instance);
    }

    [Fact]
    public async Task GenerateBatch_CreatesDevicesWithStatus_Manufactured()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            var result = await svc.GenerateBatchAsync(3, "v1.2");

            Assert.Equal(3, result.Devices.Count);
            Assert.All(result.Devices, d =>
            {
                Assert.Equal(DeviceStatus.Manufactured, d.Status);
                Assert.NotNull(d.ManufacturedAt);
                Assert.Equal("v1.2", d.HardwareRevision);
            });
        }
    }

    [Fact]
    public async Task GenerateBatch_SerialNumbers_Sequential()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            var result = await svc.GenerateBatchAsync(3, null);

            var serials = result.Devices.Select(d => d.SerialNumber).ToList();
            // Serials should end with 00001, 00002, 00003
            Assert.EndsWith("00001", serials[0]);
            Assert.EndsWith("00002", serials[1]);
            Assert.EndsWith("00003", serials[2]);
        }
    }

    [Fact]
    public async Task GenerateBatch_SecondBatch_ContinuesSequence()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            await svc.GenerateBatchAsync(5, null);
            var result2 = await svc.GenerateBatchAsync(3, null);

            // Second batch should start at 00006
            Assert.EndsWith("00006", result2.Devices[0].SerialNumber);
            Assert.EndsWith("00008", result2.Devices[2].SerialNumber);
        }
    }

    [Fact]
    public async Task GenerateBatch_CsvRows_MatchDevices()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            var result = await svc.GenerateBatchAsync(2, "v2.0");

            Assert.Equal(2, result.CsvRows.Count);
            for (var i = 0; i < result.CsvRows.Count; i++)
            {
                Assert.Equal(result.Devices[i].SerialNumber, result.CsvRows[i].SerialNumber);
                Assert.Equal("v2.0", result.CsvRows[i].HardwareRevision);
                Assert.StartsWith("derived-key-", result.CsvRows[i].DerivedKey);
            }
        }
    }

    [Fact]
    public async Task GenerateBatch_DerivedKey_UsesSerialNumber()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            var result = await svc.GenerateBatchAsync(1, null);
            var row = result.CsvRows[0];

            Assert.Equal($"derived-key-{result.Devices[0].SerialNumber}", row.DerivedKey);
        }
    }

    [Fact]
    public async Task ExportToCsv_ReturnsValidCsv()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            var result = await svc.GenerateBatchAsync(2, "v1.0");
            var csvBytes = svc.ExportToCsv(result.CsvRows);
            var csvText = System.Text.Encoding.UTF8.GetString(csvBytes);

            // Should have header + 2 data rows
            var lines = csvText.Trim().Split('\n');
            Assert.True(lines.Length >= 3, $"Expected at least 3 lines, got {lines.Length}");
            Assert.Contains("SerialNumber", lines[0]);
            Assert.Contains("DerivedKey", lines[0]);
        }
    }

    [Fact]
    public async Task GenerateBatch_DevicesPersistedToDb()
    {
        var svc = CreateService(out var db);
        using (db)
        {
            await svc.GenerateBatchAsync(4, null);

            var count = db.Devices.Count();
            Assert.Equal(4, count);
        }
    }
}
