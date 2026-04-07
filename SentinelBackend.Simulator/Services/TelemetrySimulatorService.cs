namespace SentinelBackend.Simulator.Services;

using System.Collections.Concurrent;
using IoTHubDevice = Microsoft.Azure.Devices.Device;
using IoTHubRegistryManager = Microsoft.Azure.Devices.RegistryManager;
using Microsoft.EntityFrameworkCore;
using SentinelBackend.Application.Interfaces;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;
using SentinelBackend.Infrastructure.Persistence;

/// <summary>
/// Manages simulated device fleet. Generates telemetry, writes to the real DB,
/// evaluates alarms, and exposes live state for the Blazor dashboard.
/// </summary>
public class TelemetrySimulatorService : IDisposable
{
    private const string SimulatorHardwareRevision = "sim-v1.0";
    private const string SimulatorFirmwareVersion = "1.0.0-sim";
    private const string SimulatorAssignedBy = "simulator-tech";

    private readonly IDbContextFactory<SentinelDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetrySimulatorService> _logger;

    private readonly ConcurrentDictionary<int, SimulatedDevice> _devices = new();
    private readonly ConcurrentQueue<LogEntry> _eventLog = new();
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public IReadOnlyCollection<SimulatedDevice> Devices => _devices.Values.ToList();
    public IReadOnlyCollection<LogEntry> RecentLogs => _eventLog.ToArray().TakeLast(200).ToArray();

    public event Action? OnStateChanged;

    public TelemetrySimulatorService(
        IDbContextFactory<SentinelDbContext> dbFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetrySimulatorService> logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────────

    public async Task StartAsync(int deviceCount = 5)
    {
        if (IsRunning) return;

        await SeedDevicesAsync(deviceCount);
        _cts = new CancellationTokenSource();
        _runLoop = RunSimulationLoopAsync(_cts.Token);
        Log("🟢 Simulation started", $"{_devices.Count} devices active");
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

        var iotHubDeviceIds = simulatorDevices
            .Select(d => string.IsNullOrWhiteSpace(d.DeviceId) ? d.SerialNumber : d.DeviceId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await RemoveFromIoTHubAsync(iotHubDeviceIds);

        // Delete in dependency order — notification tables may not exist yet (Phase 6 migration)
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

        Log("🧹 Data cleared", $"Removed {deviceIds.Count} devices and all related records");
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
            Log("📴 Device forced offline", d.SerialNumber);
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
                    await WriteTelemetryAsync(device, ct);
                    await EvaluateAlarmsAsync(device, ct);
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

    private async Task WriteTelemetryAsync(SimulatedDevice device, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var messageId = $"sim-{device.DeviceDbId}-{now.Ticks}";

        var record = new TelemetryHistory
        {
            DeviceId = device.DeviceDbId,
            MessageId = messageId,
            MessageType = "telemetry",
            TimestampUtc = now,
            EnqueuedAtUtc = now,
            ReceivedAtUtc = now,
            SiteId = device.SiteId,
            CustomerId = device.CustomerId,
            CompanyId = device.CompanyId,
            DeviceAssignmentId = device.AssignmentId,
            PanelVoltage = device.PanelVoltage,
            PumpCurrent = device.PumpCurrent,
            PumpRunning = device.PumpRunning,
            HighWaterAlarm = device.HighWaterAlarm,
            TemperatureC = device.TemperatureC,
            SignalRssi = device.SignalRssi,
            RuntimeSeconds = device.RuntimeSeconds,
        };
        db.TelemetryHistory.Add(record);

        // Update latest state
        var state = await db.LatestDeviceStates.FindAsync([device.DeviceDbId], ct);
        if (state is null)
        {
            state = new LatestDeviceState { DeviceId = device.DeviceDbId };
            db.LatestDeviceStates.Add(state);
        }
        state.LastTelemetryTimestampUtc = now;
        state.LastMessageId = messageId;
        state.PanelVoltage = device.PanelVoltage;
        state.PumpCurrent = device.PumpCurrent;
        state.PumpRunning = device.PumpRunning;
        state.HighWaterAlarm = device.HighWaterAlarm;
        state.TemperatureC = device.TemperatureC;
        state.SignalRssi = device.SignalRssi;
        state.RuntimeSeconds = device.RuntimeSeconds;
        state.UpdatedAt = now;

        // Update connectivity
        var conn = await db.DeviceConnectivityStates.FindAsync([device.DeviceDbId], ct);
        if (conn is null)
        {
            conn = new DeviceConnectivityState { DeviceId = device.DeviceDbId };
            db.DeviceConnectivityStates.Add(conn);
        }
        conn.LastMessageReceivedAt = now;
        conn.LastTelemetryReceivedAt = now;
        conn.LastEnqueuedAtUtc = now;
        conn.LastMessageType = "telemetry";
        conn.IsOffline = false;
        conn.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
    }

    private async Task EvaluateAlarmsAsync(SimulatedDevice device, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var alarmService = scope.ServiceProvider.GetRequiredService<IAlarmService>();

        if (device.HighWaterAlarm)
        {
            var (alarm, wasCreated) = await alarmService.RaiseAlarmAsync(
                device.DeviceDbId,
                "HighWater",
                AlarmSeverity.Critical,
                AlarmSourceType.TelemetryFallback,
                cancellationToken: ct);

            if (wasCreated)
            {
                Interlocked.Increment(ref _totalAlarms);
                Log("🚨 ALARM raised", $"{device.SerialNumber} — HighWater (ID={alarm.Id})");
            }
        }
        else
        {
            var resolved = await alarmService.AutoResolveAlarmsAsync(
                device.DeviceDbId, "HighWater", "Condition cleared", ct);
            if (resolved > 0)
            {
                Log("✅ ALARM resolved", $"{device.SerialNumber} — HighWater ({resolved} resolved)");
            }
        }
    }

    private int _totalAlarms;
    public int TotalAlarmsRaised => _totalAlarms;

    // ── Seeding ─────────────────────────────────────────────────

    private async Task SeedDevicesAsync(int count)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        _devices.Clear();

        // Find or create a simulator company
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

        // Find existing simulator devices by marker revision (and legacy SIM serials)
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

        // Build in-memory state for all selected devices
        foreach (var d in existingDevices.OrderBy(d => d.SerialNumber).Take(count))
        {
            var assignment = await db.DeviceAssignments
                .Where(a => a.DeviceId == d.Id && a.UnassignedAt == null)
                .FirstOrDefaultAsync();

            if (assignment is null)
            {
                continue;
            }

            var assignmentSite = await db.Sites
                .Where(s => s.Id == assignment.SiteId)
                .Select(s => new { s.Id, s.CustomerId })
                .FirstOrDefaultAsync();

            if (assignmentSite is null)
            {
                continue;
            }

            var assignmentCustomer = await db.Customers
                .Where(c => c.Id == assignmentSite.CustomerId)
                .Select(c => new { c.CompanyId })
                .FirstOrDefaultAsync();

            if (assignmentCustomer is null)
            {
                continue;
            }

            if (assignmentCustomer.CompanyId is null)
            {
                continue;
            }

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
    }

    // ── Snapshot Query ──────────────────────────────────────────

    public async Task<DashboardSnapshot> GetSnapshotAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var deviceIds = _devices.Keys.ToList();
        if (deviceIds.Count == 0)
            return new DashboardSnapshot(0, 0, 0, 0, []);

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

        var alarmDetails = await db.Alarms
            .Where(a => deviceIds.Contains(a.DeviceId)
                && (a.Status == AlarmStatus.Active || a.Status == AlarmStatus.Acknowledged))
            .Select(a => new AlarmInfo(a.Id, a.Device.SerialNumber, a.AlarmType,
                a.Severity.ToString(), a.Status.ToString(), a.StartedAt))
            .OrderByDescending(a => a.StartedAt)
            .Take(20)
            .ToListAsync();

        return new DashboardSnapshot(
            _devices.Count, activeAlarms, offlineDevices, recentTelemetry, alarmDetails);
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

    private int _cycleCounter;

    public void Tick(Random rng)
    {
        // Voltage: normal ~24V with small drift
        PanelVoltage = 23.5 + rng.NextDouble() * 1.5;

        // Temperature: seasonal drift, slight randomness
        TemperatureC = 20.0 + rng.NextDouble() * 8.0;

        // Signal: varies
        SignalRssi = -80 + rng.Next(30);

        // Pump cycles every ~10 ticks
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

        // Apply scenario overrides
        HighWaterAlarm = ForceHighWater;
    }
}

public record LogEntry(DateTime Timestamp, string Icon, string Message);

public record DashboardSnapshot(
    int TotalDevices,
    int ActiveAlarms,
    int OfflineDevices,
    int RecentTelemetryCount,
    List<AlarmInfo> Alarms);

public record AlarmInfo(
    int Id, string Serial, string AlarmType,
    string Severity, string Status, DateTime StartedAt);
