namespace SentinelBackend.IntegrationTests;

using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Devices.Client;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Contracts;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;
using Xunit;

/// <summary>
/// End-to-end integration tests that exercise the full production pipeline:
///   Simulator (DeviceClient) → IoT Hub → Event Hubs → TelemetryIngestionWorker → SQL
///   → Service Bus → OfflineCheckWorker / CommandExecutorWorker
///
/// These tests require the API + Ingestion hosts to be running against the same
/// Azure resources. Set environment variables and run both hosts before executing:
///
///   SENTINEL_SQL_CONNECTION       — Azure SQL connection string
///   SENTINEL_IOTHUB_SERVICE_CONN  — IoT Hub service policy connection string
///   SENTINEL_IOTHUB_HOSTNAME      — IoT Hub hostname
///
/// Tests are automatically skipped when environment variables are not set.
/// </summary>
public class EndToEndPipelineTests : IClassFixture<AzureFixture>, IAsyncLifetime
{
    private readonly AzureFixture _fixture;

    public EndToEndPipelineTests(AzureFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!_fixture.IsConfigured) return;

        // Ensure a Device row exists in SQL for the test device.
        // The ingestion worker needs a matching Device.DeviceId to process messages.
        var existing = await _fixture.Db!.Devices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.DeviceId == _fixture.TestDeviceId);

        if (existing is null)
        {
            _fixture.Db.Devices.Add(new Device
            {
                SerialNumber = _fixture.TestDeviceId,
                DeviceId = _fixture.TestDeviceId,
                Status = DeviceStatus.Active,
                HardwareRevision = "inttest-v1.0",
                FirmwareVersion = "1.0.0-test",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await _fixture.Db.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [RequiresAzureFact]
    public async Task TelemetryD2C_FlowsThroughPipeline_AppearsInDatabase()
    {
        var messageId = $"inttest-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;

        var telemetry = new TelemetryMessage
        {
            MessageId = messageId,
            TimestampUtc = now,
            FirmwareVersion = "1.0.0-test",
            PanelVoltage = 24.1,
            PumpCurrent = 4.2,
            PumpRunning = true,
            HighWaterAlarm = false,
            TemperatureC = 25.5,
            SignalRssi = -60,
            RuntimeSeconds = 5000,
        };

        var json = JsonSerializer.Serialize(telemetry);
        using var iotMessage = new Message(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = messageId,
        };
        iotMessage.Properties["messageType"] = "telemetry";

        // Send D2C message
        await _fixture.DeviceClient!.SendEventAsync(iotMessage);

        // Poll for the message to appear in the database (ingestion worker processing)
        TelemetryHistory? record = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            // Use a fresh context to avoid stale cache
            await using var pollDb = new SentinelDbContext(
                new DbContextOptionsBuilder<SentinelDbContext>()
                    .UseSqlServer(_fixture.SqlConnectionString)
                    .Options);

            record = await pollDb.TelemetryHistory
                .FirstOrDefaultAsync(t => t.MessageId == messageId);

            if (record is not null) break;
        }

        Assert.NotNull(record);
        Assert.Equal(messageId, record.MessageId);
        Assert.Equal("telemetry", record.MessageType);
        Assert.InRange(record.PanelVoltage ?? 0, 24.0, 24.2);
        Assert.Equal(true, record.PumpRunning);
    }

    [RequiresAzureFact]
    public async Task HighWaterAlarm_RaisedByIngestionWorker()
    {
        var messageId = $"inttest-hw-{Guid.NewGuid():N}";

        var telemetry = new TelemetryMessage
        {
            MessageId = messageId,
            TimestampUtc = DateTime.UtcNow,
            PanelVoltage = 24.0,
            HighWaterAlarm = true,
        };

        var json = JsonSerializer.Serialize(telemetry);
        using var iotMessage = new Message(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = messageId,
        };
        iotMessage.Properties["messageType"] = "telemetry";

        await _fixture.DeviceClient!.SendEventAsync(iotMessage);

        // Wait for ingestion + alarm detection
        Alarm? alarm = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            await using var pollDb = new SentinelDbContext(
                new DbContextOptionsBuilder<SentinelDbContext>()
                    .UseSqlServer(_fixture.SqlConnectionString)
                    .Options);

            var device = await pollDb.Devices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.DeviceId == _fixture.TestDeviceId);

            if (device is not null)
            {
                alarm = await pollDb.Alarms
                    .Where(a => a.DeviceId == device.Id
                        && a.AlarmType == "HighWater"
                        && a.Status == AlarmStatus.Active)
                    .FirstOrDefaultAsync();
            }

            if (alarm is not null) break;
        }

        Assert.NotNull(alarm);
        Assert.Equal("HighWater", alarm.AlarmType);
        Assert.Equal(AlarmSeverity.Critical, alarm.Severity);
    }

    [RequiresAzureFact]
    public async Task ConnectivityState_UpdatedOnTelemetry()
    {
        var messageId = $"inttest-conn-{Guid.NewGuid():N}";

        var telemetry = new TelemetryMessage
        {
            MessageId = messageId,
            TimestampUtc = DateTime.UtcNow,
            PanelVoltage = 24.0,
        };

        var json = JsonSerializer.Serialize(telemetry);
        using var iotMessage = new Message(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = messageId,
        };
        iotMessage.Properties["messageType"] = "telemetry";

        await _fixture.DeviceClient!.SendEventAsync(iotMessage);

        // Wait for connectivity state update
        DeviceConnectivityState? conn = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            await using var pollDb = new SentinelDbContext(
                new DbContextOptionsBuilder<SentinelDbContext>()
                    .UseSqlServer(_fixture.SqlConnectionString)
                    .Options);

            var device = await pollDb.Devices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.DeviceId == _fixture.TestDeviceId);

            if (device is not null)
            {
                conn = await pollDb.DeviceConnectivityStates
                    .FirstOrDefaultAsync(c => c.DeviceId == device.Id);
            }

            if (conn is not null && conn.LastMessageReceivedAt > DateTime.UtcNow.AddSeconds(-30))
                break;
        }

        Assert.NotNull(conn);
        Assert.False(conn.IsOffline);
        Assert.Equal("telemetry", conn.LastMessageType);
    }

    [RequiresAzureFact]
    public async Task CommandExecution_RoundTrip()
    {
        // Register a direct method handler on the test device
        var commandReceived = new TaskCompletionSource<string>();
        await _fixture.DeviceClient!.SetMethodDefaultHandlerAsync((request, _) =>
        {
            commandReceived.TrySetResult(request.Name);
            var response = """{"result":"ok"}""";
            return Task.FromResult(new MethodResponse(
                Encoding.UTF8.GetBytes(response), 200));
        }, null);

        // First, ensure the device row exists and get its DB ID
        await using var db = new SentinelDbContext(
            new DbContextOptionsBuilder<SentinelDbContext>()
                .UseSqlServer(_fixture.SqlConnectionString)
                .Options);

        var device = await db.Devices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.DeviceId == _fixture.TestDeviceId);

        Assert.NotNull(device);

        // Submit a command via the database (simulating what the API controller does)
        var command = new CommandLog
        {
            DeviceId = device.Id,
            CommandType = "ping",
            Status = CommandStatus.Pending,
            RequestedByUserId = "inttest",
            RequestedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.CommandLogs.Add(command);
        await db.SaveChangesAsync();

        // Publish to Service Bus so CommandExecutorWorker picks it up immediately
        if (!string.IsNullOrWhiteSpace(_fixture.ServiceBusConnectionString))
        {
            await using var sbClient = new ServiceBusClient(_fixture.ServiceBusConnectionString);
            await using var sender = sbClient.CreateSender("device-commands");
            var sbMessage = new ServiceBusMessage(
                JsonSerializer.Serialize(new { CommandId = command.Id }))
            {
                ContentType = "application/json",
            };
            await sender.SendMessageAsync(sbMessage);
        }

        // Wait for the CommandExecutorWorker to pick it up and invoke the direct method
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var methodName = await commandReceived.Task.WaitAsync(cts.Token);
            Assert.Equal("ping", methodName);
        }
        catch (OperationCanceledException)
        {
            // Command may not have been picked up yet — assert on DB state
        }

        // Verify the command was processed
        var deadline = DateTime.UtcNow.AddSeconds(15);
        CommandLog? result = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            await using var pollDb = new SentinelDbContext(
                new DbContextOptionsBuilder<SentinelDbContext>()
                    .UseSqlServer(_fixture.SqlConnectionString)
                    .Options);

            result = await pollDb.CommandLogs.FirstOrDefaultAsync(c => c.Id == command.Id);
            if (result?.Status is CommandStatus.Succeeded or CommandStatus.Failed or CommandStatus.TimedOut)
                break;
        }

        Assert.NotNull(result);
        Assert.Equal(CommandStatus.Succeeded, result.Status);
    }
}
