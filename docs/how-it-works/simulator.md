# Simulator

The **SentinelBackend.Simulator** project is a standalone Blazor Server application that generates realistic telemetry from a fleet of virtual grinder-pump devices. It now follows the same lifecycle as production devices: manufacturing, first-boot DPS allocation, and technician assignment before telemetry generation. It writes to the same SQL database used by the API, so every record it creates is visible through the normal REST endpoints, alarm evaluations, and retention workers.

## Why it exists

- Provides a visual, interactive way to exercise the full data pipeline without physical hardware while still simulating manufacturing and DPS/IoT Hub provisioning.
- Lets you trigger alarm scenarios (high-water, device offline) on demand and watch the system react in real time.
- Useful for demos, development, and manual smoke testing.

## Running the simulator

```bash
dotnet run --project SentinelBackend.Simulator
```

The dashboard opens at **https://localhost:7299** (HTTP: 5299).

### Prerequisites

| Requirement | Detail |
|---|---|
| SQL Server | Same connection string as the API (resolved via Key Vault or `SqlConnectionString` config) |
| Azure Key Vault | `KeyVaultUrl` in `appsettings.json` — same vault the API uses |
| Azure DPS | `DpsConnectionString`, `DpsIotHubHostName`, `DpsEnrollmentPrimaryKey` |
| Azure IoT Hub service access | `IoTHubServiceConnectionString` (or `IoTHubConnectionString`) |
| .NET 9 SDK | Must be installed locally |

Event Hubs and Blob Storage are not required.

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│  Blazor Server (port 7299)                                 │
│                                                            │
│  ┌──────────────┐     ┌───────────────────────────────┐    │
│  │  Home.razor   │────▶│  TelemetrySimulatorService    │    │
│  │  (Dashboard)  │◀────│  (Singleton engine)           │    │
│  └──────────────┘     └───────────┬───────────────────┘    │
│         ▲  OnStateChanged          │                        │
│         │  + Timer (2 s)           │ Every 3 s per device   │
│         │                          ▼                        │
│         │              ┌──────────────────────┐             │
│         │              │  IDbContextFactory    │             │
│         │              │  → SentinelDbContext   │             │
│         │              └──────────┬───────────┘             │
│         │                         │                         │
│         │              ┌──────────▼───────────┐             │
│         └──────────────│  SQL Server           │             │
│                        │  (shared with API)    │             │
│                        └──────────────────────┘             │
└────────────────────────────────────────────────────────────┘
```

### Key components

| Component | Role |
|---|---|
| `Program.cs` | Configures Blazor Server, Key Vault, EF Core, manufacturing + DPS + IoT Hub services, and the simulator singleton |
| `TelemetrySimulatorService` | Singleton engine — manufactures devices, simulates DPS first boot, simulates assignment, runs the tick loop, writes telemetry, evaluates alarms |
| `SimulatedDevice` | In-memory model holding live telemetry values and scenario flags per device |
| `Home.razor` | Interactive dashboard — KPI counters, device cards, alarm table, event log |
| `app.css` | Dark-theme styling for the dashboard |

## How the simulation works

### 1. Manufacturing stage

When you click **Start Simulation**, the service calls `SeedDevicesAsync(count)`:

1. Creates (or finds) a **Simulator Corp** company, **Sim User** customer, and **Simulator Site**.
2. Creates missing devices through `IManufacturingBatchService.GenerateBatchAsync(...)`.
3. New devices are inserted in `Devices` with status **Manufactured**.

### 2. First boot provisioning stage (DPS)

For each manufactured simulator device:

1. Calls `IDpsAllocationService.AllocateAsync(...)` to simulate first boot.
2. Device transitions from **Manufactured** to **Unprovisioned** and receives `DeviceId`.
3. Simulator ensures the IoT Hub device identity exists via `RegistryManager`.

### 3. Installation stage

For each unprovisioned simulator device:

1. Creates an active `DeviceAssignment` to **Simulator Site**.
2. Device status transitions to **Assigned** to emulate field installation.
3. In-memory `SimulatedDevice` entries are built from active assignments.

### 4. Tick loop

A background task runs every **3 seconds**. For each online device:

1. **`Tick()`** — randomizes telemetry values:
   - Panel voltage: 23.5–25.0 V
   - Temperature: 20–28 °C
   - Signal RSSI: −80 to −50 dBm
   - Pump cycles on for 3 of every 10 ticks (current 3.5–5.5 A when running)
   - `HighWaterAlarm` is driven by the `ForceHighWater` scenario flag

2. **`WriteTelemetryAsync()`** — inserts a `TelemetryHistory` row, upserts `LatestDeviceState`, and upserts `DeviceConnectivityState`.

3. **`EvaluateAlarmsAsync()`** — uses `IAlarmService.RaiseAlarmAsync()` / `AutoResolveAlarmsAsync()` from the real domain layer to create or resolve alarms and trigger notification evaluation.

4. Fires `OnStateChanged` so the Blazor dashboard re-renders.

### 5. Dashboard refresh

The `Home.razor` page subscribes to `OnStateChanged` and also runs a 2-second timer that calls `GetSnapshotAsync()`. The snapshot queries the database for:

- Active alarm count
- Offline device count
- Telemetry rows in the last 5 minutes
- Top 20 active/acknowledged alarms with details

## Dashboard sections

| Section | What it shows |
|---|---|
| **Controls bar** | Start/Stop button, device count input, running/stopped badge |
| **KPI row** | Total devices, messages sent, active alarms, total alarms raised, offline count, recent telemetry count |
| **Device fleet** | Card per device with live readings (voltage, current, pump state, HW alarm, temperature, signal, runtime) |
| **Active alarms table** | Alarm ID, device serial, type, severity, status, start time |
| **Event log** | Scrolling list of timestamped events (starts, stops, alarm raises/clears, errors) |

## Scenario controls

Each device card has buttons to trigger scenarios:

| Button | Effect |
|---|---|
| **Trigger HW Alarm** | Sets `ForceHighWater = true` → next tick writes `HighWaterAlarm = true` → `IAlarmService` creates a Critical alarm |
| **Clear HW Alarm** | Sets `ForceHighWater = false` → next tick clears the alarm → `AutoResolveAlarmsAsync` resolves it |
| **Go Offline** | Sets `ForceOffline = true` → device is skipped in the tick loop (no telemetry written) |
| **Bring Online** | Sets `ForceOffline = false` → device resumes sending telemetry |

## DI registrations

```csharp
// EF Core — factory for concurrent access
builder.Services.AddDbContextFactory<SentinelDbContext>(options =>
    options.UseSqlServer(sqlConn, o => o.EnableRetryOnFailure()));

// Domain services used during alarm evaluation
builder.Services.AddScoped<IAlarmService, AlarmService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddSingleton<INotificationDispatcher, LoggingNotificationDispatcher>();

// Simulator engine
builder.Services.AddSingleton<TelemetrySimulatorService>();
```

`IDbContextFactory` is used instead of a single scoped `DbContext` because the simulator singleton outlives any DI scope and produces contexts on demand.

`LoggingNotificationDispatcher` is a no-op dispatcher that logs notification attempts rather than sending email/SMS — same stub used elsewhere in the system.

## Data created

Running the simulator populates these tables:

| Table | Records |
|---|---|
| `Companies` | 1 ("Simulator Corp") |
| `Customers` | 1 ("Sim User") |
| `Sites` | 1 ("Simulator Site") |
| `Devices` | N simulator-marked rows (hardware revision `sim-v1.0`) |
| `DeviceAssignments` | N |
| `TelemetryHistory` | Grows continuously (~N rows every 3 s) |
| `LatestDeviceStates` | N (upserted each tick) |
| `DeviceConnectivityStates` | N (upserted each tick) |
| `Alarms` | Created/resolved by scenario triggers |
| `NotificationIncidents` | Created when alarms fire (if notification rules exist) |

> **Tip:** Simulator data is tagged with hardware revision `sim-v1.0` (legacy runs may also have `SIM-` serials).

## Cleanup

The **Clear All Data** action in the dashboard removes simulator data and also removes simulator devices from IoT Hub.

Manual SQL cleanup (if needed) can target simulator markers:

```sql
DELETE FROM TelemetryHistory WHERE DeviceId IN (SELECT Id FROM Devices WHERE HardwareRevision = 'sim-v1.0' OR SerialNumber LIKE 'SIM-%');
DELETE FROM LatestDeviceStates WHERE DeviceId IN (SELECT Id FROM Devices WHERE HardwareRevision = 'sim-v1.0' OR SerialNumber LIKE 'SIM-%');
DELETE FROM DeviceConnectivityStates WHERE DeviceId IN (SELECT Id FROM Devices WHERE HardwareRevision = 'sim-v1.0' OR SerialNumber LIKE 'SIM-%');
DELETE FROM Alarms WHERE DeviceId IN (SELECT Id FROM Devices WHERE HardwareRevision = 'sim-v1.0' OR SerialNumber LIKE 'SIM-%');
DELETE FROM DeviceAssignments WHERE DeviceId IN (SELECT Id FROM Devices WHERE HardwareRevision = 'sim-v1.0' OR SerialNumber LIKE 'SIM-%');
DELETE FROM Devices WHERE HardwareRevision = 'sim-v1.0' OR SerialNumber LIKE 'SIM-%';
DELETE FROM Sites WHERE Name = 'Simulator Site';
DELETE FROM Customers WHERE Email = 'sim-customer@sentinel.test';
DELETE FROM Companies WHERE Name = 'Simulator Corp';
```
