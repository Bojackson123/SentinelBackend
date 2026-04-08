namespace SentinelBackend.IntegrationTests;

using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Contracts;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;
using Xunit;
using TransportType = Microsoft.Azure.Devices.Client.TransportType;

/// <summary>
/// Shared test fixture that manages Azure resources (IoT Hub, SQL, Service Bus)
/// for integration tests. Creates a test device on setup and cleans up on teardown.
///
/// Secrets are loaded from Azure Key Vault (same vault the API uses).
/// Falls back to environment variables if set:
///   SENTINEL_SQL_CONNECTION, SENTINEL_IOTHUB_SERVICE_CONN, SENTINEL_IOTHUB_HOSTNAME
///
/// Tests are skipped when neither Key Vault nor env vars are available.
/// </summary>
public class AzureFixture : IAsyncLifetime
{
    public const string TestDevicePrefix = "inttest-";
    private const string KeyVaultUrl = "https://sentinel-key-vault-dev.vault.azure.net/";

    public string? SqlConnectionString { get; private set; }
    public string? IoTHubServiceConnectionString { get; private set; }
    public string? IoTHubHostName { get; private set; }
    public string? ServiceBusConnectionString { get; private set; }

    public string TestDeviceId { get; } = $"{TestDevicePrefix}{Guid.NewGuid():N}";
    public RegistryManager? Registry { get; private set; }
    public DeviceClient? DeviceClient { get; private set; }
    public SentinelDbContext? Db { get; private set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SqlConnectionString)
        && !string.IsNullOrWhiteSpace(IoTHubServiceConnectionString)
        && !string.IsNullOrWhiteSpace(IoTHubHostName);

    /// <summary>
    /// Shared secret loader — used by both the fixture and the skip attribute.
    /// Tries Key Vault first, then falls back to env vars.
    /// </summary>
    internal static (string? sql, string? iotConn, string? iotHost, string? sbConn) LoadSecrets()
    {
        // 1. Try env vars first (fast path, CI scenarios)
        var sql = Environment.GetEnvironmentVariable("SENTINEL_SQL_CONNECTION");
        var iotConn = Environment.GetEnvironmentVariable("SENTINEL_IOTHUB_SERVICE_CONN");
        var iotHost = Environment.GetEnvironmentVariable("SENTINEL_IOTHUB_HOSTNAME");
        var sbConn = Environment.GetEnvironmentVariable("SENTINEL_SERVICEBUS_CONN");

        if (!string.IsNullOrWhiteSpace(sql)
            && !string.IsNullOrWhiteSpace(iotConn)
            && !string.IsNullOrWhiteSpace(iotHost))
        {
            return (sql, iotConn, iotHost, sbConn);
        }

        // 2. Try Key Vault (uses DefaultAzureCredential — Visual Studio, az login, managed identity)
        try
        {
            var client = new SecretClient(new Uri(KeyVaultUrl), new DefaultAzureCredential());
            sql ??= client.GetSecret("SqlConnectionString").Value.Value;
            iotConn ??= client.GetSecret("IoTHubConnectionString").Value.Value;
            sbConn ??= client.GetSecret("ServiceBusConnectionString").Value.Value;

            // Parse hostname from the IoT Hub connection string
            if (string.IsNullOrWhiteSpace(iotHost) && !string.IsNullOrWhiteSpace(iotConn))
            {
                var parsed = Microsoft.Azure.Devices.IotHubConnectionStringBuilder.Create(iotConn);
                iotHost = parsed.HostName;
            }
        }
        catch
        {
            // Key Vault not reachable — tests will be skipped
        }

        return (sql, iotConn, iotHost, sbConn);
    }

    public async Task InitializeAsync()
    {
        var (sql, iotConn, iotHost, sbConn) = LoadSecrets();
        SqlConnectionString = sql;
        IoTHubServiceConnectionString = iotConn;
        IoTHubHostName = iotHost;
        ServiceBusConnectionString = sbConn;

        if (!IsConfigured) return;

        // Connect to SQL
        Db = new SentinelDbContext(new DbContextOptionsBuilder<SentinelDbContext>()
            .UseSqlServer(SqlConnectionString)
            .Options);

        // Create IoT Hub device
        Registry = RegistryManager.CreateFromConnectionString(IoTHubServiceConnectionString);
        var iotDevice = await Registry.AddDeviceAsync(new Device(TestDeviceId));

        // Build DeviceClient
        var authMethod = new DeviceAuthenticationWithRegistrySymmetricKey(
            TestDeviceId,
            iotDevice.Authentication.SymmetricKey.PrimaryKey);
        DeviceClient = Microsoft.Azure.Devices.Client.DeviceClient.Create(
            IoTHubHostName, authMethod, TransportType.Mqtt);
        await DeviceClient.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        // Disconnect device
        if (DeviceClient is not null)
        {
            try { await DeviceClient.CloseAsync(); } catch { }
            DeviceClient.Dispose();
        }

        // Remove from IoT Hub
        if (Registry is not null)
        {
            try { await Registry.RemoveDeviceAsync(TestDeviceId); } catch { }
        }

        // Clean up SQL rows created by this test device
        if (Db is not null)
        {
            var device = await Db.Devices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.DeviceId == TestDeviceId);

            if (device is not null)
            {
                var deviceId = device.Id;
                await Db.TelemetryHistory.Where(t => t.DeviceId == deviceId).ExecuteDeleteAsync();
                await Db.LatestDeviceStates.Where(s => s.DeviceId == deviceId).ExecuteDeleteAsync();
                await Db.DeviceConnectivityStates.Where(c => c.DeviceId == deviceId).ExecuteDeleteAsync();

                var alarmIds = await Db.Alarms.Where(a => a.DeviceId == deviceId).Select(a => a.Id).ToListAsync();
                if (alarmIds.Count > 0)
                {
                    await Db.NotificationAttempts
                        .Where(na => Db.NotificationIncidents
                            .Where(ni => alarmIds.Contains(ni.AlarmId))
                            .Select(ni => ni.Id)
                            .Contains(na.NotificationIncidentId))
                        .ExecuteDeleteAsync();
                    await Db.NotificationIncidents.Where(ni => alarmIds.Contains(ni.AlarmId)).ExecuteDeleteAsync();
                }

                await Db.Alarms.Where(a => a.DeviceId == deviceId).ExecuteDeleteAsync();
                await Db.CommandLogs.Where(c => c.DeviceId == deviceId).ExecuteDeleteAsync();
                await Db.DeviceAssignments.Where(a => a.DeviceId == deviceId).ExecuteDeleteAsync();
                await Db.Devices.IgnoreQueryFilters().Where(d => d.Id == deviceId).ExecuteDeleteAsync();
            }

            await Db.DisposeAsync();
        }
    }
}

/// <summary>
/// Trait-based skip: tests are skipped when Azure secrets are not available
/// (neither Key Vault nor environment variables).
/// </summary>
public sealed class RequiresAzureFactAttribute : FactAttribute
{
    public RequiresAzureFactAttribute()
    {
        var (sql, iotConn, iotHost, _) = AzureFixture.LoadSecrets();

        if (string.IsNullOrWhiteSpace(sql)
            || string.IsNullOrWhiteSpace(iotConn)
            || string.IsNullOrWhiteSpace(iotHost))
        {
            Skip = "Integration tests require Azure Key Vault access or SENTINEL_SQL_CONNECTION, SENTINEL_IOTHUB_SERVICE_CONN, and SENTINEL_IOTHUB_HOSTNAME environment variables.";
        }
    }
}
