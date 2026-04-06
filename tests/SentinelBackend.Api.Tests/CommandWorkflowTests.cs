namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Tests.Shared;
using Xunit;

public class CommandWorkflowTests
{
    [Fact]
    public async Task SubmitCommand_CreatesWithPendingStatus()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);
        seed.Device.Status = DeviceStatus.Active;
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var command = new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "reboot",
            Status = CommandStatus.Pending,
            RequestedByUserId = "admin-1",
            RequestedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CommandLogs.Add(command);
        await db.SaveChangesAsync();

        var saved = await db.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Pending, saved.Status);
        Assert.Equal("reboot", saved.CommandType);
        Assert.NotEqual(0, saved.Id);
    }

    [Fact]
    public async Task Command_TransitionToSent()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var command = new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "ping",
            Status = CommandStatus.Pending,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.CommandLogs.Add(command);
        await db.SaveChangesAsync();

        command.Status = CommandStatus.Sent;
        command.SentAt = DateTime.UtcNow;
        command.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var saved = await db.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Sent, saved.Status);
        Assert.NotNull(saved.SentAt);
    }

    [Fact]
    public async Task Command_TransitionToSucceeded()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var command = new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "run-self-test",
            Status = CommandStatus.Sent,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.CommandLogs.Add(command);
        await db.SaveChangesAsync();

        command.Status = CommandStatus.Succeeded;
        command.CompletedAt = DateTime.UtcNow;
        command.ResponseJson = "{\"result\": \"ok\"}";
        command.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var saved = await db.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Succeeded, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.Contains("ok", saved.ResponseJson!);
    }

    [Fact]
    public async Task Command_TransitionToFailed()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var command = new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "capture-snapshot",
            Status = CommandStatus.Sent,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.CommandLogs.Add(command);
        await db.SaveChangesAsync();

        command.Status = CommandStatus.Failed;
        command.CompletedAt = DateTime.UtcNow;
        command.ErrorMessage = "Device returned status 500";
        command.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var saved = await db.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Failed, saved.Status);
        Assert.Contains("500", saved.ErrorMessage!);
    }

    [Fact]
    public async Task Command_TransitionToTimedOut()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var command = new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "sync-now",
            Status = CommandStatus.Sent,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.CommandLogs.Add(command);
        await db.SaveChangesAsync();

        command.Status = CommandStatus.TimedOut;
        command.CompletedAt = DateTime.UtcNow;
        command.ErrorMessage = "Direct method invocation timed out";
        command.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var saved = await db.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.TimedOut, saved.Status);
        Assert.Contains("timed out", saved.ErrorMessage!);
    }

    [Fact]
    public async Task Command_LinkedToDevice()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        db.CommandLogs.Add(new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "clear-fault",
            Status = CommandStatus.Pending,
            RequestedByUserId = "tech-1",
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var command = await db.CommandLogs
            .Include(c => c.Device)
            .FirstAsync();

        Assert.Equal(seed.Device.Id, command.DeviceId);
        Assert.Equal(seed.Device.SerialNumber, command.Device.SerialNumber);
    }

    [Fact]
    public async Task MultipleCommands_OrderedByRequestedAt()
    {
        using var db = TestDb.Create();
        var seed = await TestDb.SeedFullHierarchyAsync(db);

        var baseTime = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        var commandTypes = new[] { "reboot", "ping", "sync-now" };

        for (var i = 0; i < commandTypes.Length; i++)
        {
            db.CommandLogs.Add(new CommandLog
            {
                DeviceId = seed.Device.Id,
                CommandType = commandTypes[i],
                Status = CommandStatus.Pending,
                RequestedByUserId = "admin-1",
                RequestedAt = baseTime.AddMinutes(i),
                CreatedAt = baseTime.AddMinutes(i),
                UpdatedAt = baseTime.AddMinutes(i),
            });
        }
        await db.SaveChangesAsync();

        var commands = await db.CommandLogs
            .OrderByDescending(c => c.RequestedAt)
            .ToListAsync();

        Assert.Equal(3, commands.Count);
        Assert.Equal("sync-now", commands[0].CommandType);
        Assert.Equal("ping", commands[1].CommandType);
        Assert.Equal("reboot", commands[2].CommandType);
    }

    [Fact]
    public async Task PendingCommands_CanBeQueried()
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
        db.CommandLogs.Add(new CommandLog
        {
            DeviceId = seed.Device.Id,
            CommandType = "ping",
            Status = CommandStatus.Succeeded,
            RequestedByUserId = "admin-1",
            RequestedAt = DateTime.UtcNow.AddMinutes(-5),
            SentAt = DateTime.UtcNow.AddMinutes(-4),
            CompletedAt = DateTime.UtcNow.AddMinutes(-3),
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-3),
        });
        await db.SaveChangesAsync();

        var pending = await db.CommandLogs
            .Where(c => c.Status == CommandStatus.Pending)
            .ToListAsync();

        Assert.Single(pending);
        Assert.Equal("reboot", pending[0].CommandType);
    }
}
