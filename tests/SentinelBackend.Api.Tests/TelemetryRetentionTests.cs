namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelBackend.Api.Workers;
using SentinelBackend.Application;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Tests.Shared;
using Xunit;

public class TelemetryRetentionTests
{
    private static DbContextOptions<SentinelDbContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<SentinelDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static TelemetryRetentionWorker CreateWorker(
        DbContextOptions<SentinelDbContext> dbOptions,
        RetentionOptions? opts = null)
    {
        opts ??= new RetentionOptions();

        var services = new ServiceCollection();
        services.AddScoped(_ => new SentinelDbContext(dbOptions));
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new TelemetryRetentionWorker(
            scopeFactory,
            NullLogger<TelemetryRetentionWorker>.Instance,
            Options.Create(opts));
    }

    private static async Task<Device> SeedDeviceAsync(SentinelDbContext db)
    {
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        return seed.Device;
    }

    [Fact]
    public async Task PurgesArchivedTelemetry_OlderThanHotRetention()
    {
        var dbOptions = CreateDbOptions();
        using var db = new SentinelDbContext(dbOptions);
        var device = await SeedDeviceAsync(db);

        db.TelemetryHistory.AddRange(
            new TelemetryHistory
            {
                DeviceId = device.Id,
                MessageId = "msg-old",
                MessageType = "telemetry",
                TimestampUtc = DateTime.UtcNow.AddDays(-100),
                EnqueuedAtUtc = DateTime.UtcNow.AddDays(-100),
                ReceivedAtUtc = DateTime.UtcNow.AddDays(-100),
                RawPayloadBlobUri = "https://blob/old.json"
            },
            new TelemetryHistory
            {
                DeviceId = device.Id,
                MessageId = "msg-recent",
                MessageType = "telemetry",
                TimestampUtc = DateTime.UtcNow.AddDays(-10),
                EnqueuedAtUtc = DateTime.UtcNow.AddDays(-10),
                ReceivedAtUtc = DateTime.UtcNow.AddDays(-10),
                RawPayloadBlobUri = "https://blob/recent.json"
            });
        await db.SaveChangesAsync();

        var worker = CreateWorker(dbOptions, new RetentionOptions { HotRetentionDays = 90 });
        await worker.RunPurgeCycleAsync();

        using var assertDb = new SentinelDbContext(dbOptions);
        var remaining = await assertDb.TelemetryHistory.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("msg-recent", remaining[0].MessageId);
    }

    [Fact]
    public async Task DoesNotPurge_WhenArchiveUriMissing_AndRequireArchiveIsTrue()
    {
        var dbOptions = CreateDbOptions();
        using var db = new SentinelDbContext(dbOptions);
        var device = await SeedDeviceAsync(db);

        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = device.Id,
            MessageId = "msg-unarchived",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow.AddDays(-100),
            EnqueuedAtUtc = DateTime.UtcNow.AddDays(-100),
            ReceivedAtUtc = DateTime.UtcNow.AddDays(-100),
            RawPayloadBlobUri = null
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(dbOptions, new RetentionOptions
        {
            HotRetentionDays = 90,
            RequireArchiveBeforePurge = true
        });
        await worker.RunPurgeCycleAsync();

        using var assertDb = new SentinelDbContext(dbOptions);
        Assert.Single(await assertDb.TelemetryHistory.ToListAsync());
    }

    [Fact]
    public async Task PurgesUnarchived_WhenRequireArchiveIsFalse()
    {
        var dbOptions = CreateDbOptions();
        using var db = new SentinelDbContext(dbOptions);
        var device = await SeedDeviceAsync(db);

        db.TelemetryHistory.Add(new TelemetryHistory
        {
            DeviceId = device.Id,
            MessageId = "msg-unarchived",
            MessageType = "telemetry",
            TimestampUtc = DateTime.UtcNow.AddDays(-100),
            EnqueuedAtUtc = DateTime.UtcNow.AddDays(-100),
            ReceivedAtUtc = DateTime.UtcNow.AddDays(-100),
            RawPayloadBlobUri = null
        });
        await db.SaveChangesAsync();

        var worker = CreateWorker(dbOptions, new RetentionOptions
        {
            HotRetentionDays = 90,
            RequireArchiveBeforePurge = false
        });
        await worker.RunPurgeCycleAsync();

        using var assertDb = new SentinelDbContext(dbOptions);
        Assert.Empty(await assertDb.TelemetryHistory.ToListAsync());
    }

    [Fact]
    public async Task RespectsBatchSizeLimit()
    {
        var dbOptions = CreateDbOptions();
        using var db = new SentinelDbContext(dbOptions);
        var device = await SeedDeviceAsync(db);

        for (int i = 0; i < 5; i++)
        {
            db.TelemetryHistory.Add(new TelemetryHistory
            {
                DeviceId = device.Id,
                MessageId = $"msg-{i}",
                MessageType = "telemetry",
                TimestampUtc = DateTime.UtcNow.AddDays(-100 - i),
                EnqueuedAtUtc = DateTime.UtcNow.AddDays(-100 - i),
                ReceivedAtUtc = DateTime.UtcNow.AddDays(-100 - i),
                RawPayloadBlobUri = $"https://blob/{i}.json"
            });
        }
        await db.SaveChangesAsync();

        var worker = CreateWorker(dbOptions, new RetentionOptions
        {
            HotRetentionDays = 90,
            PurgeBatchSize = 3
        });
        await worker.RunPurgeCycleAsync();

        using var assertDb = new SentinelDbContext(dbOptions);
        var remaining = await assertDb.TelemetryHistory.CountAsync();
        Assert.Equal(2, remaining);
    }

    [Fact]
    public async Task PurgesOldFailedIngressMessages()
    {
        var dbOptions = CreateDbOptions();
        using var db = new SentinelDbContext(dbOptions);

        db.FailedIngressMessages.AddRange(
            new FailedIngressMessage
            {
                SourceDeviceId = "dev-1",
                MessageId = "fail-old",
                PartitionId = "0",
                Offset = "100",
                EnqueuedAt = DateTime.UtcNow.AddDays(-60),
                FailureReason = "InvalidPayload",
                CreatedAt = DateTime.UtcNow.AddDays(-60)
            },
            new FailedIngressMessage
            {
                SourceDeviceId = "dev-1",
                MessageId = "fail-recent",
                PartitionId = "0",
                Offset = "200",
                EnqueuedAt = DateTime.UtcNow.AddDays(-5),
                FailureReason = "InvalidPayload",
                CreatedAt = DateTime.UtcNow.AddDays(-5)
            });
        await db.SaveChangesAsync();

        var worker = CreateWorker(dbOptions, new RetentionOptions { FailedIngressRetentionDays = 30 });
        await worker.RunPurgeCycleAsync();

        using var assertDb = new SentinelDbContext(dbOptions);
        var remaining = await assertDb.FailedIngressMessages.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("fail-recent", remaining[0].MessageId);
    }

    [Fact]
    public async Task NoOp_WhenNothingToDelete()
    {
        var dbOptions = CreateDbOptions();

        var worker = CreateWorker(dbOptions, new RetentionOptions { HotRetentionDays = 90 });
        await worker.RunPurgeCycleAsync();

        using var assertDb = new SentinelDbContext(dbOptions);
        Assert.Empty(await assertDb.TelemetryHistory.ToListAsync());
        Assert.Empty(await assertDb.FailedIngressMessages.ToListAsync());
    }
}
