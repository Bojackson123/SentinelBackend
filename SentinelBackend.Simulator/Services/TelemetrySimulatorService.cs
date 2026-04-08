namespace SentinelBackend.Simulator.Services;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using IoTHubDevice = Microsoft.Azure.Devices.Device;
using IoTHubRegistryManager = Microsoft.Azure.Devices.RegistryManager;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Contracts;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Manages simulated device fleet. Sends D2C telemetry through IoT Hub so it
/// flows through the full production pipeline: IoT Hub → Event Hubs →
/// TelemetryIngestionWorker → DB + Service Bus → alarm/offline workers.
///
/// Also registers direct method handlers so CommandExecutorWorker can invoke
/// commands on simulated devices.
/// </summary>
public class TelemetrySimulatorService : IDisposable
{
    private const string SimulatorHardwareRevision = "sim-v1.0";
    private const string SimulatorFirmwareVersion = "1.0.0-sim";
    private const string SimulatorAssignedBy = "simulator-tech";

    private readonly IDbContextFactory<SentinelDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetrySimulatorService> _logger;
    private readonly IConfiguration _configuration;

    private readonly ConcurrentDictionary<int, SimulatedDevice> _devices = new();
    private readonly ConcurrentQueue<LogEntry> _eventLog = new();
    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private string? _iotHubHostName;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public IReadOnlyCollection<SimulatedDevice> Devices => _devices.Values.ToList();
    public IReadOnlyCollection<LogEntry> RecentLogs => _eventLog.ToArray().TakeLast(200).ToArray();

    public event Action? OnStateChanged;

    public TelemetrySimulatorService(
        IDbContextFactory<SentinelDbContext> dbFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetrySimulatorService> logger,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    // ── Lifecycle ────────────────────────────────────────────────

    public async Task StartAsync(int deviceCount = 5)
    {
        if (IsRunning) return;

        _iotHubHostName = GetIoTHubHostName();
        await SeedDevicesAsync(deviceCount);
        await ConnectDeviceClientsAsync();

        _cts = new CancellationTokenSource();
        _runLoop = RunSimulationLoopAsync(_cts.Token);
        Log("🟢 Simulation started", $"{_devices.Count} devices sending D2C via IoT Hub");
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_runLoop is not null)
        {
            try { await _runLoop; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;

        await DisconnectDeviceClientsAsync();
        Log("🔴 Simulation stopped", "");
    }

    // ── Data Cleanup ─────────────────────────────────────────────

    public async Task ClearAllDataAsync()
    {
        if (IsRunning) await StopAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();

        var simulatorDevices = await db.Devices
            .IgnoreQueryFilters()
            .Where(d => d.SerialNumber.StartsWith("SIM-") || d.HardwareRevision == SimulatorHardwareRevision)
            .Select(d => new { d.Id, d.DeviceId, d.SerialNumber })
            .ToListAsync();

        var deviceIds = simulatorDevices.Select(d => d.Id).ToList();

        if (deviceIds.Count == 0)
        {
            Log("🧹 Clear data", "No simulator data found");
            return;
        }

        // Remove from IoT Hub
        var iotHubDeviceIds = simulatorDevices
            .Select(d => string.IsNullOrWhiteSpace(d.DeviceId) ? d.SerialNumber : d.DeviceId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await RemoveFromIoTHubAsync(iotHubDeviceIds);

        // Delete in dependency order — notification tables first
        var alarmIds = await db.Alarms
            .Where(a => deviceIds.Contains(a.DeviceId))
            .Select(a => a.Id)
            .ToListAsync();

        if (alarmIds.Count > 0)
        {
            await db.NotificationAttempts
                .Where(na => db.NotificationIncidents
                    .Where(ni => alarmIds.Contains(ni.AlarmId))
                    .Select(ni => ni.Id)
                    .Contains(na.NotificationIncidentId))
                .ExecuteDeleteAsync();

            await db.EscalationEvents
                .Where(e => db.NotificationIncidents
                    .Where(ni => alarmIds.Contains(ni.AlarmId))
                    .Select(ni => ni.Id)
                    .Contains(e.NotificationIncidentId))
                .ExecuteDeleteAsync();

            await db.NotificationIncidents
                .Where(ni => alarmIds.Contains(ni.AlarmId))
                .ExecuteDeleteAsync();
        }

        // Delete command logs
        await db.CommandLogs
            .Where(c => deviceIds.Contains(c.DeviceId))
            .ExecuteDeleteAsync();

        // Delete maintenance windows for these devices
        await db.MaintenanceWindows
            .Where(mw => mw.DeviceId.HasValue && deviceIds.Contains(mw.DeviceId.Value))
            .ExecuteDeleteAsync();

        await db.TelemetryHistory.Where(t => deviceIds.Contains(t.DeviceId)).ExecuteDeleteAsync();
        await db.LatestDeviceStates.Where(s => deviceIds.Contains(s.DeviceId)).ExecuteDeleteAsync();
        await db.DeviceConnectivityStates.Where(c => deviceIds.Contains(c.DeviceId)).ExecuteDeleteAsync();
        await db.Alarms.Where(a => deviceIds.Contains(a.DeviceId)).ExecuteDeleteAsync();
        await db.DeviceAssignments.Where(a => deviceIds.Contains(a.DeviceId)).ExecuteDeleteAsync();
        await db.Devices.IgnoreQueryFilters().Where(d => deviceIds.Contains(d.Id)).ExecuteDeleteAsync();

        await db.Sites.Where(s => s.Name == "Simulator Site").ExecuteDeleteAsync();
        await db.Customers.Where(c => c.Email == "sim-customer@sentinel.test").ExecuteDeleteAsync();
        await db.Companies.IgnoreQueryFilters().Where(c => c.Name == "Simulator Corp").ExecuteDeleteAsync();

        _devices.Clear();
        _totalSent = 0;
        _totalAlarms = 0;
        _totalCommandsReceived = 0;

        Log("🧹 Data cleared", $"Removed {deviceIds.Count} devices from DB + IoT Hub");
    }

    // ── Device Scenario Controls ────────────────────────────────

    public void TriggerHighWater(int deviceDbId)
    {
        if (_devices.TryGetValue(deviceDbId, out var d))
        {
            d.ForceHighWater = true;
            Log("⚠️ HighWater forced", d.SerialNumber);
        }
    }

    public void ClearHighWater(int deviceDbId)
    {
        if (_devices.TryGetValue(deviceDbId, out var d))
        {
            d.ForceHighWater = false;
            Log("✅ HighWater cleared", d.SerialNumber);
        }
    }

    public void TriggerOffline(int deviceDbId)
    {
        if (_devices.TryGetValue(deviceDbId, out var d))
        {
            d.ForceOffline = true;
            Log("📴 Device forced offline", $"{d.SerialNumber} — will stop sending D2C (offline detection will kick in)");
        }
    }

    public void BringOnline(int deviceDbId)
    {
        if (_devices.TryGetValue(deviceDbId, out var d))
        {
            d.ForceOffline = false;
            Log("📶 Device brought online", d.SerialNumber);
        }
    }

    // ── Core Simulation Loop ────────────────────────────────────

    private async Task RunSimulationLoopAsync(CancellationToken ct)
    {
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            foreach (var device in _devices.Values)
            {
                if (ct.IsCancellationRequested) break;
                if (device.ForceOffline) continue;

                try
                {
                    device.Tick(rng);
                    await SendTelemetryAsync(device, ct);
                    Interlocked.Increment(ref _totalSent);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Sim tick failed for {Serial}", device.SerialNumber);
                    Log("❌ Error", $"{device.SerialNumber}: {ex.Message}");
                }
            }

            OnStateChanged?.Invoke();

            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private int _totalSent;
    public int TotalMessagesSent => _totalSent;

    private int _totalAlarms;
    public int TotalAlarmsRaised => _totalAlarms;

    private int _totalCommandsReceived;
    public int TotalCommandsReceived => _totalCommandsReceived;

    /// <summary>
    /// Sends a D2C telemetry message through IoT Hub. The TelemetryIngestionWorker
    /// in the Ingestion host picks this up, writes to DB, schedules offline checks,
    /// and evaluates alarms — exercising the full production pipeline.
    /// </summary>
    private async Task SendTelemetryAsync(SimulatedDevice device, CancellationToken ct)
    {
        if (device.DeviceClient is null)
        {
            _logger.LogDebug("No DeviceClient for {Serial}, skipping", device.SerialNumber);
            return;
        }

        var now = DateTime.UtcNow;
        var messageId = $"sim-{device.DeviceDbId}-{now.Ticks}";

        var telemetry = new TelemetryMessage
        {
            MessageId = messageId,
            TimestampUtc = now,
            FirmwareVersion = SimulatorFirmwareVersion,
            PanelVoltage = device.PanelVoltage,
            PumpCurrent = device.PumpCurrent,
            PumpRunning = device.PumpRunning,
            HighWaterAlarm = device.HighWaterAlarm,
            TemperatureC = device.TemperatureC,
            SignalRssi = device.SignalRssi,
            RuntimeSeconds = device.RuntimeSeconds,
        };

        var json = JsonSerializer.Serialize(telemetry);
        using var iotMessage = new Message(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            MessageId = messageId,
        };
        iotMessage.Properties["messageType"] = "telemetry";

        await device.DeviceClient.SendEventAsync(iotMessage, ct);
    }

    // ── DeviceClient Management ─────────────────────────────────

    private async Task ConnectDeviceClientsAsync()
    {
        if (_iotHubHostName is null) return;

        using var scope = _scopeFactory.CreateScope();
        var registryManager = scope.ServiceProvider.GetService<IoTHubRegistryManager>();
        if (registryManager is null)
        {
            Log("⚠️ Cannot connect", "RegistryManager not configured");
            return;
        }

        var connected = 0;
        foreach (var device in _devices.Values)
        {
            try
            {
                var iotDevice = await registryManager.GetDeviceAsync(device.IoTDeviceId);
                if (iotDevice?.Authentication?.SymmetricKey?.PrimaryKey is null)
                {
                    Log("⚠️ No device key", device.IoTDeviceId);
                    continue;
                }

                var authMethod = new DeviceAuthenticationWithRegistrySymmetricKey(
                    device.IoTDeviceId,
                    iotDevice.Authentication.SymmetricKey.PrimaryKey);

                var client = DeviceClient.Create(
                    _iotHubHostName,
                    authMethod,
                    TransportType.Mqtt);

                await client.OpenAsync();

                // Register direct method handlers for all supported command types
                await client.SetMethodDefaultHandlerAsync(HandleDirectMethodAsync, device);

                device.DeviceClient = client;
                connected++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect DeviceClient for {DeviceId}", device.IoTDeviceId);
                Log("❌ Connect failed", $"{device.IoTDeviceId}: {ex.Message}");
            }
        }

        Log("☁️ Connected", $"{connected}/{_devices.Count} devices connected to IoT Hub via MQTT");
    }

    private async Task DisconnectDeviceClientsAsync()
    {
        var disconnected = 0;
        foreach (var device in _devices.Values)
        {
            if (device.DeviceClient is not null)
            {
                try
                {
                    await device.DeviceClient.CloseAsync();
                    device.DeviceClient.Dispose();
                    disconnected++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting {DeviceId}", device.IoTDeviceId);
                }
                finally
                {
                    device.DeviceClient = null;
                }
            }
        }

        if (disconnected > 0)
            Log("☁️ Disconnected", $"{disconnected} devices disconnected from IoT Hub");
    }

    /// <summary>
    /// Handles direct method invocations from IoT Hub (triggered by CommandExecutorWorker).
    /// Simulates a successful device response for all known command types.
    /// </summary>
    private Task<MethodResponse> HandleDirectMethodAsync(MethodRequest request, object userContext)
    {
        var device = (SimulatedDevice)userContext;
        Interlocked.Increment(ref _totalCommandsReceived);

        var commandName = request.Name;
        var responsePayload = commandName switch
        {
            "reboot" => """{"result":"rebooting","estimatedMs":5000}""",
            "ping" => $$$"""{"result":"pong","uptimeSeconds":{{{device.RuntimeSeconds}}}}""",
            "captureSnapshot" => """{"result":"snapshot captured","frameId":"sim-frame-001"}""",
            "runSelfTest" => """{"result":"all systems nominal","checks":6,"passed":6}""",
            "syncNow" => """{"result":"synced","configVersion":"1.0"}""",
            "clearFault" => """{"result":"fault cleared"}""",
            _ => $$$"""{"result":"unknown command","method":"{{{commandName}}}"}""",
        };

        Log("📥 Command received", $"{device.SerialNumber} — {commandName}");

        device.LastCommandReceived = commandName;
        device.LastCommandAt = DateTime.UtcNow;

        OnStateChanged?.Invoke();

        return Task.FromResult(new MethodResponse(
            Encoding.UTF8.GetBytes(responsePayload), 200));
    }

    // ── Seeding ─────────────────────────────────────────────────

    private async Task SeedDevicesAsync(int count)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        _devices.Clear();

        // Find or create simulator company
        var company = await db.Companies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Name == "Simulator Corp");

        if (company is null)
        {
            company = new Company
            {
                Name = "Simulator Corp",
                ContactEmail = "sim@sentinel.test",
                BillingEmail = "billing@sentinel.test",
                SubscriptionStatus = SubscriptionStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
        }

        var customer = await db.Customers
            .FirstOrDefaultAsync(c => c.CompanyId == company.Id && c.Email == "sim-customer@sentinel.test");

        if (customer is null)
        {
            customer = new Customer
            {
                CompanyId = company.Id,
                FirstName = "Sim",
                LastName = "User",
                Email = "sim-customer@sentinel.test",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        var site = await db.Sites
            .FirstOrDefaultAsync(s => s.CustomerId == customer.Id && s.Name == "Simulator Site");

        if (site is null)
        {
            site = new Site
            {
                CustomerId = customer.Id,
                Name = "Simulator Site",
                AddressLine1 = "100 Test Lane",
                City = "Austin",
                State = "TX",
                PostalCode = "73301",
                Country = "US",
                Timezone = "America/Chicago",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Sites.Add(site);
            await db.SaveChangesAsync();
        }

        // Find existing simulator devices
        var existingDevices = await db.Devices
            .IgnoreQueryFilters()
            .Where(d => d.HardwareRevision == SimulatorHardwareRevision || d.SerialNumber.StartsWith("SIM-"))
            .OrderBy(d => d.SerialNumber)
            .Take(count)
            .ToListAsync();

        var toCreate = count - existingDevices.Count;

        if (toCreate > 0)
        {
            using var scope = _scopeFactory.CreateScope();
            var manufacturingService = scope.ServiceProvider.GetRequiredService<IManufacturingBatchService>();

            var batch = await manufacturingService.GenerateBatchAsync(
                toCreate,
                SimulatorHardwareRevision);

            var serials = batch.Devices.Select(d => d.SerialNumber).ToArray();
            var createdDevices = await db.Devices
                .IgnoreQueryFilters()
                .Where(d => serials.Contains(d.SerialNumber))
                .ToListAsync();

            foreach (var created in createdDevices)
            {
                created.FirmwareVersion ??= SimulatorFirmwareVersion;
                created.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
            existingDevices.AddRange(createdDevices);

            Log("🏭 Manufactured", $"Created {createdDevices.Count} new devices");
        }

        foreach (var d in existingDevices)
        {
            d.FirmwareVersion ??= SimulatorFirmwareVersion;

            if (d.Status == DeviceStatus.Manufactured)
            {
                await SimulateFirstBootDpsAsync(d.SerialNumber);
                await db.Entry(d).ReloadAsync();
            }

            if (!string.IsNullOrWhiteSpace(d.DeviceId))
            {
                await EnsureIoTHubDeviceAsync(d.DeviceId!);
            }

            await EnsureAssignedAsync(db, d, site.Id);
        }

        await db.SaveChangesAsync();

        // Build in-memory state
        foreach (var d in existingDevices.OrderBy(d => d.SerialNumber).Take(count))
        {
            var assignment = await db.DeviceAssignments
                .Where(a => a.DeviceId == d.Id && a.UnassignedAt == null)
                .FirstOrDefaultAsync();

            if (assignment is null) continue;

            var assignmentSite = await db.Sites
                .Where(s => s.Id == assignment.SiteId)
                .Select(s => new { s.Id, s.CustomerId })
                .FirstOrDefaultAsync();

            if (assignmentSite is null) continue;

            var assignmentCustomer = await db.Customers
                .Where(c => c.Id == assignmentSite.CustomerId)
                .Select(c => new { c.CompanyId })
                .FirstOrDefaultAsync();

            if (assignmentCustomer?.CompanyId is null) continue;

            _devices[d.Id] = new SimulatedDevice
            {
                DeviceDbId = d.Id,
                SerialNumber = d.SerialNumber,
                IoTDeviceId = d.DeviceId ?? d.SerialNumber,
                CompanyId = assignmentCustomer.CompanyId.Value,
                CustomerId = assignmentSite.CustomerId,
                SiteId = assignmentSite.Id,
                AssignmentId = assignment?.Id,
            };
        }

        Log("🏭 Devices seeded", $"{_devices.Count} devices ready ({toCreate} manufactured this run)");
    }

    private async Task SimulateFirstBootDpsAsync(string serialNumber)
    {
        using var scope = _scopeFactory.CreateScope();
        var dpsAllocation = scope.ServiceProvider.GetRequiredService<IDpsAllocationService>();

        await dpsAllocation.AllocateAsync(serialNumber, []);
        Log("☁️ DPS allocated", $"{serialNumber} moved to Unprovisioned");
    }

    private async Task EnsureAssignedAsync(SentinelDbContext db, Device device, int siteId)
    {
        var activeAssignment = await db.DeviceAssignments
            .FirstOrDefaultAsync(a => a.DeviceId == device.Id && a.UnassignedAt == null);

        if (activeAssignment is not null)
        {
            if (device.Status == DeviceStatus.Unprovisioned || device.Status == DeviceStatus.Manufactured)
            {
                device.Status = DeviceStatus.Assigned;
                device.UpdatedAt = DateTime.UtcNow;
                Log("🧰 Installed", $"{device.SerialNumber} marked Assigned");
            }
            return;
        }

        var assignment = new DeviceAssignment
        {
            DeviceId = device.Id,
            SiteId = siteId,
            AssignedAt = DateTime.UtcNow,
            AssignedByUserId = SimulatorAssignedBy,
        };
        db.DeviceAssignments.Add(assignment);

        if (device.Status == DeviceStatus.Unprovisioned || device.Status == DeviceStatus.Manufactured)
        {
            device.Status = DeviceStatus.Assigned;
        }

        device.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Log("🧰 Installed", $"{device.SerialNumber} assigned to Simulator Site");
    }

    private async Task EnsureIoTHubDeviceAsync(string deviceId)
    {
        using var scope = _scopeFactory.CreateScope();
        var registryManager = scope.ServiceProvider.GetService<IoTHubRegistryManager>();

        if (registryManager is null)
        {
            Log("⚠️ IoT Hub skipped", "RegistryManager is not configured");
            return;
        }

        try
        {
            var existing = await registryManager.GetDeviceAsync(deviceId);
            if (existing is null)
            {
                await registryManager.AddDeviceAsync(new IoTHubDevice(deviceId));
                Log("☁️ IoT identity created", deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed ensuring IoT Hub identity for {DeviceId}", deviceId);
            Log("❌ IoT Hub error", $"{deviceId}: {ex.Message}");
        }
    }

    private async Task RemoveFromIoTHubAsync(IReadOnlyCollection<string> deviceIds)
    {
        using var scope = _scopeFactory.CreateScope();
        var registryManager = scope.ServiceProvider.GetService<IoTHubRegistryManager>();

        if (registryManager is null)
        {
            Log("⚠️ IoT Hub cleanup skipped", "RegistryManager is not configured");
            return;
        }

        var removed = 0;
        var missing = 0;
        var failed = 0;

        foreach (var deviceId in deviceIds)
        {
            try
            {
                var existing = await registryManager.GetDeviceAsync(deviceId);
                if (existing is null)
                {
                    missing++;
                    continue;
                }
                await registryManager.RemoveDeviceAsync(deviceId);
                removed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed removing IoT Hub device {DeviceId}", deviceId);
            }
        }

        Log("☁️ IoT Hub cleanup", $"Removed={removed}, Missing={missing}, Failed={failed}");
    }

    private string GetIoTHubHostName()
    {
        var connectionString = _configuration["IoTHubServiceConnectionString"]
            ?? _configuration["IoTHubConnectionString"]
            ?? throw new InvalidOperationException("IoTHubServiceConnectionString is not configured.");

        var csBuilder = Microsoft.Azure.Devices.IotHubConnectionStringBuilder.Create(connectionString);
        return csBuilder.HostName;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void Log(string icon, string message)
    {
        var entry = new LogEntry(DateTime.UtcNow, icon, message);
        _eventLog.Enqueue(entry);
        while (_eventLog.Count > 300) _eventLog.TryDequeue(out _);
        _logger.LogInformation("[SIM] {Icon} {Message}", icon, message);
        OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        // Fire-and-forget cleanup of device clients
        foreach (var device in _devices.Values)
        {
            try { device.DeviceClient?.Dispose(); } catch { }
        }
    }

    // ── Snapshot Query ──────────────────────────────────────────

    public async Task<DashboardSnapshot> GetSnapshotAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var deviceIds = _devices.Keys.ToList();
        if (deviceIds.Count == 0)
            return new DashboardSnapshot(0, 0, 0, 0, 0, []);

        var activeAlarms = await db.Alarms
            .Where(a => deviceIds.Contains(a.DeviceId)
                && (a.Status == AlarmStatus.Active || a.Status == AlarmStatus.Acknowledged))
            .CountAsync();

        var recentTelemetry = await db.TelemetryHistory
            .Where(t => deviceIds.Contains(t.DeviceId)
                && t.TimestampUtc > DateTime.UtcNow.AddMinutes(-5))
            .CountAsync();

        var offlineDevices = await db.DeviceConnectivityStates
            .Where(c => deviceIds.Contains(c.DeviceId) && c.IsOffline)
            .CountAsync();

        var pendingCommands = await db.CommandLogs
            .Where(c => deviceIds.Contains(c.DeviceId)
                && (c.Status == CommandStatus.Pending || c.Status == CommandStatus.Sent))
            .CountAsync();

        var alarmDetails = await db.Alarms
            .Where(a => deviceIds.Contains(a.DeviceId)
                && (a.Status == AlarmStatus.Active || a.Status == AlarmStatus.Acknowledged))
            .OrderByDescending(a => a.StartedAt)
            .Take(20)
            .Select(a => new { a.Id, a.Device.SerialNumber, a.AlarmType, a.Severity, a.Status, a.StartedAt })
            .ToListAsync();

        var alarmInfos = alarmDetails
            .Select(a => new AlarmInfo(a.Id, a.SerialNumber, a.AlarmType,
                a.Severity.ToString(), a.Status.ToString(), a.StartedAt))
            .ToList();

        return new DashboardSnapshot(
            _devices.Count, activeAlarms, offlineDevices, recentTelemetry, pendingCommands, alarmInfos);
    }
}

// ── Supporting Types ────────────────────────────────────────────

public class SimulatedDevice
{
    public int DeviceDbId { get; init; }
    public string SerialNumber { get; init; } = "";
    public string IoTDeviceId { get; init; } = "";
    public int CompanyId { get; init; }
    public int CustomerId { get; init; }
    public int SiteId { get; init; }
    public int? AssignmentId { get; init; }

    // IoT Hub device client for D2C + direct methods
    public DeviceClient? DeviceClient { get; set; }

    // Current telemetry values
    public double PanelVoltage { get; set; } = 24.0;
    public double PumpCurrent { get; set; } = 0.0;
    public bool PumpRunning { get; set; }
    public bool HighWaterAlarm { get; set; }
    public double TemperatureC { get; set; } = 22.0;
    public int SignalRssi { get; set; } = -65;
    public int RuntimeSeconds { get; set; } = 1000;

    // Scenario overrides
    public bool ForceHighWater { get; set; }
    public bool ForceOffline { get; set; }

    // Command tracking
    public string? LastCommandReceived { get; set; }
    public DateTime? LastCommandAt { get; set; }

    private int _cycleCounter;

    public void Tick(Random rng)
    {
        PanelVoltage = 23.5 + rng.NextDouble() * 1.5;
        TemperatureC = 20.0 + rng.NextDouble() * 8.0;
        SignalRssi = -80 + rng.Next(30);

        _cycleCounter++;
        if (_cycleCounter % 10 < 3)
        {
            PumpRunning = true;
            PumpCurrent = 3.5 + rng.NextDouble() * 2.0;
            RuntimeSeconds += 3;
        }
        else
        {
            PumpRunning = false;
            PumpCurrent = 0.0;
        }

        HighWaterAlarm = ForceHighWater;
    }
}

public record LogEntry(DateTime Timestamp, string Icon, string Message);

public record DashboardSnapshot(
    int TotalDevices,
    int ActiveAlarms,
    int OfflineDevices,
    int RecentTelemetryCount,
    int PendingCommands,
    List<AlarmInfo> Alarms);

public record AlarmInfo(
    int Id, string Serial, string AlarmType,
    string Severity, string Status, DateTime StartedAt);
