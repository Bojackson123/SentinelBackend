namespace SentinelBackend.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelBackend.Api.Workers;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;
using SentinelBackend.Tests.Shared;
using Xunit;

public class CommandExecutorTests
{
    private static IConfiguration CreateWorkerConfiguration(int pollIntervalSeconds = 1)
    {
        var values = new Dictionary<string, string?>
        {
            ["CommandExecutor:PollIntervalSeconds"] = pollIntervalSeconds.ToString(),
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class FakeDirectMethodService : IDirectMethodService
    {
        public int StatusToReturn { get; set; } = 200;
        public string? ResponseJsonToReturn { get; set; } = "{\"result\": \"ok\"}";
        public Exception? ExceptionToThrow { get; set; }
        public List<(string DeviceId, string MethodName)> Invocations { get; } = [];

        public Task<DirectMethodResult> InvokeAsync(
            string deviceId, string methodName, string? payloadJson = null,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((deviceId, methodName));

            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            return Task.FromResult(new DirectMethodResult(StatusToReturn, ResponseJsonToReturn));
        }
    }

    private static (IServiceScopeFactory scopeFactory, string dbName, FakeDirectMethodService fakeMethod) CreateServices()
    {
        var dbName = Guid.NewGuid().ToString();
        var fakeMethod = new FakeDirectMethodService();

        var services = new ServiceCollection();
        services.AddScoped(_ =>
            new SentinelDbContext(new DbContextOptionsBuilder<SentinelDbContext>()
                .UseInMemoryDatabase(dbName).Options));
        services.AddScoped<IDirectMethodService>(_ => fakeMethod);
        var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<IServiceScopeFactory>(), dbName, fakeMethod);
    }

    private static SentinelDbContext OpenDb(string dbName) =>
        new(new DbContextOptionsBuilder<SentinelDbContext>()
            .UseInMemoryDatabase(dbName).Options);

    [Fact]
    public async Task Worker_ProcessesPendingCommand_Success()
    {
        var (scopeFactory, dbName, fakeMethod) = CreateServices();

        // Seed data via a separate context
        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            await setupDb.SaveChangesAsync();

            setupDb.CommandLogs.Add(new CommandLog
            {
                DeviceId = seed.Device.Id,
                CommandType = "reboot",
                Status = CommandStatus.Pending,
                RequestedByUserId = "admin-1",
                RequestedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );
        await worker.RunPollCycleAsync();

        using var assertDb = OpenDb(dbName);
        var command = await assertDb.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Succeeded, command.Status);
        Assert.NotNull(command.SentAt);
        Assert.NotNull(command.CompletedAt);
        Assert.Contains("ok", command.ResponseJson!);

        Assert.Single(fakeMethod.Invocations);
        Assert.Equal("GP-202604-00001", fakeMethod.Invocations[0].DeviceId);
        Assert.Equal("reboot", fakeMethod.Invocations[0].MethodName);
    }

    [Fact]
    public async Task Worker_ProcessesPendingCommand_DeviceError()
    {
        var (scopeFactory, dbName, fakeMethod) = CreateServices();
        fakeMethod.StatusToReturn = 500;
        fakeMethod.ResponseJsonToReturn = "{\"error\": \"internal\"}";

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            await setupDb.SaveChangesAsync();

            setupDb.CommandLogs.Add(new CommandLog
            {
                DeviceId = seed.Device.Id,
                CommandType = "ping",
                Status = CommandStatus.Pending,
                RequestedByUserId = "admin-1",
                RequestedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );
        await worker.RunPollCycleAsync();

        using var assertDb = OpenDb(dbName);
        var command = await assertDb.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Failed, command.Status);
        Assert.Contains("500", command.ErrorMessage!);
    }

    [Fact]
    public async Task Worker_ProcessesPendingCommand_Timeout()
    {
        var (scopeFactory, dbName, fakeMethod) = CreateServices();
        fakeMethod.ExceptionToThrow = new TimeoutException("Connection timed out");

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            await setupDb.SaveChangesAsync();

            setupDb.CommandLogs.Add(new CommandLog
            {
                DeviceId = seed.Device.Id,
                CommandType = "sync-now",
                Status = CommandStatus.Pending,
                RequestedByUserId = "admin-1",
                RequestedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );
        await worker.RunPollCycleAsync();

        using var assertDb = OpenDb(dbName);
        var command = await assertDb.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.TimedOut, command.Status);
        Assert.Contains("timed out", command.ErrorMessage!);
    }

    [Fact]
    public async Task Worker_MapsCommandTypes_ToDirectMethods()
    {
        var (scopeFactory, dbName, fakeMethod) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            await setupDb.SaveChangesAsync();

            var commandTypes = new[] { "reboot", "ping", "capture-snapshot", "run-self-test", "sync-now", "clear-fault" };
            foreach (var ct in commandTypes)
            {
                setupDb.CommandLogs.Add(new CommandLog
                {
                    DeviceId = seed.Device.Id,
                    CommandType = ct,
                    Status = CommandStatus.Pending,
                    RequestedByUserId = "admin-1",
                    RequestedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await setupDb.SaveChangesAsync();
        }

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );
        await worker.RunPollCycleAsync();

        var expectedMethods = new[] { "reboot", "ping", "captureSnapshot", "runSelfTest", "syncNow", "clearFault" };
        var methodNames = fakeMethod.Invocations.Select(i => i.MethodName).ToList();
        foreach (var expected in expectedMethods)
        {
            Assert.Contains(expected, methodNames);
        }
    }

    [Fact]
    public async Task Worker_SkipsNoDeviceId()
    {
        var (scopeFactory, dbName, _) = CreateServices();

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.DeviceId = null;
            await setupDb.SaveChangesAsync();

            setupDb.CommandLogs.Add(new CommandLog
            {
                DeviceId = seed.Device.Id,
                CommandType = "ping",
                Status = CommandStatus.Pending,
                RequestedByUserId = "admin-1",
                RequestedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await setupDb.SaveChangesAsync();
        }

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );
        await worker.RunPollCycleAsync();

        using var assertDb = OpenDb(dbName);
        var command = await assertDb.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Failed, command.Status);
        Assert.Contains("no IoT Hub device ID", command.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessSingleCommand_Success()
    {
        var (scopeFactory, dbName, fakeMethod) = CreateServices();
        int commandId;

        using (var setupDb = OpenDb(dbName))
        {
            var seed = await TestDb.SeedFullHierarchyAsync(setupDb);
            seed.Device.Status = DeviceStatus.Active;
            await setupDb.SaveChangesAsync();

            var cmd = new CommandLog
            {
                DeviceId = seed.Device.Id,
                CommandType = "ping",
                Status = CommandStatus.Pending,
                RequestedByUserId = "admin-1",
                RequestedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            setupDb.CommandLogs.Add(cmd);
            await setupDb.SaveChangesAsync();
            commandId = cmd.Id;
        }

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );

        // Test the Service Bus path directly
        await worker.ProcessSingleCommandAsync(commandId);

        using var assertDb = OpenDb(dbName);
        var command = await assertDb.CommandLogs.FirstAsync();
        Assert.Equal(CommandStatus.Succeeded, command.Status);
        Assert.Single(fakeMethod.Invocations);
    }

    [Fact]
    public async Task ProcessSingleCommand_NonExistentId_NoException()
    {
        var (scopeFactory, _, _) = CreateServices();

        var worker = new CommandExecutorWorker(
            scopeFactory,
            NullLogger<CommandExecutorWorker>.Instance,
            CreateWorkerConfiguration()
        );

        // Command ID 999 doesn't exist — should silently skip
        await worker.ProcessSingleCommandAsync(999);
    }
}
