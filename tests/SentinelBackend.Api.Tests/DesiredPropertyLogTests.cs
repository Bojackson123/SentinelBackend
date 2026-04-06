namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Tests.Shared;
using Xunit;

public class DesiredPropertyLogTests
{
    [Fact]
    public async Task DesiredPropertyLog_Persisted()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var now = DateTime.UtcNow;
        db.DesiredPropertyLogs.Add(new DesiredPropertyLog
        {
            DeviceId = seed.Device.Id,
            PropertyName = "telemetryIntervalSeconds",
            NewValue = "30",
            RequestedByUserId = "admin-1",
            RequestedAt = now,
            Success = true,
        });
        await db.SaveChangesAsync();

        var log = await db.DesiredPropertyLogs.FirstAsync();
        Assert.Equal("telemetryIntervalSeconds", log.PropertyName);
        Assert.Equal("30", log.NewValue);
        Assert.True(log.Success);
    }

    [Fact]
    public async Task DesiredPropertyLog_CapturesPreviousValue()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.DesiredPropertyLogs.Add(new DesiredPropertyLog
        {
            DeviceId = seed.Device.Id,
            PropertyName = "diagnosticsEnabled",
            PreviousValue = "false",
            NewValue = "true",
            RequestedByUserId = "tech-1",
            RequestedAt = DateTime.UtcNow,
            Success = true,
        });
        await db.SaveChangesAsync();

        var log = await db.DesiredPropertyLogs.FirstAsync();
        Assert.Equal("false", log.PreviousValue);
        Assert.Equal("true", log.NewValue);
    }

    [Fact]
    public async Task DesiredPropertyLog_FailureRecordsError()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.DesiredPropertyLogs.Add(new DesiredPropertyLog
        {
            DeviceId = seed.Device.Id,
            PropertyName = "rolloutRing",
            NewValue = "\"canary\"",
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            Success = false,
            ErrorMessage = "Device not found in IoT Hub",
        });
        await db.SaveChangesAsync();

        var log = await db.DesiredPropertyLogs.FirstAsync();
        Assert.False(log.Success);
        Assert.Contains("not found", log.ErrorMessage!);
    }

    [Fact]
    public async Task DesiredPropertyLog_MultipleEntriesForSameDevice()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var now = DateTime.UtcNow;
        db.DesiredPropertyLogs.AddRange(
            new DesiredPropertyLog
            {
                DeviceId = seed.Device.Id,
                PropertyName = "telemetryIntervalSeconds",
                NewValue = "30",
                RequestedByUserId = "admin-1",
                RequestedAt = now,
                Success = true,
            },
            new DesiredPropertyLog
            {
                DeviceId = seed.Device.Id,
                PropertyName = "siteTimezone",
                NewValue = "\"America/Chicago\"",
                RequestedByUserId = "admin-1",
                RequestedAt = now,
                Success = true,
            },
            new DesiredPropertyLog
            {
                DeviceId = seed.Device.Id,
                PropertyName = "alarmThresholds",
                NewValue = "{\"highWater\":true}",
                RequestedByUserId = "admin-1",
                RequestedAt = now,
                Success = true,
            }
        );
        await db.SaveChangesAsync();

        var count = await db.DesiredPropertyLogs.CountAsync(l => l.DeviceId == seed.Device.Id);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task DesiredPropertyLog_LinkedToDevice()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.DesiredPropertyLogs.Add(new DesiredPropertyLog
        {
            DeviceId = seed.Device.Id,
            PropertyName = "rolloutRing",
            NewValue = "\"production\"",
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            Success = true,
        });
        await db.SaveChangesAsync();

        var log = await db.DesiredPropertyLogs
            .Include(l => l.Device)
            .FirstAsync();

        Assert.Equal(seed.Device.SerialNumber, log.Device.SerialNumber);
    }
}
