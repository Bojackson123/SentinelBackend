# Simulator

The **SentinelBackend.Simulator** project is a standalone Blazor Server application that generates realistic telemetry from a fleet of virtual grinder-pump devices. It follows the full production pipeline — telemetry is sent as **Device-to-Cloud (D2C) messages through Azure IoT Hub**, then ingested by the `TelemetryIngestionWorker` via Event Hubs, exactly like a physical device. It also responds to **direct method invocations** from the `CommandExecutorWorker`, enabling end-to-end command round-trip testing. The simulator exercises manufacturing, first-boot DPS allocation, and technician assignment before telemetry generation.

## Why it exists

- Exercises the **full production pipeline** end-to-end: Device → IoT Hub → Event Hubs → `TelemetryIngestionWorker` → SQL + Service Bus → Workers (alarms, notifications, offline detection).
- Responds to **direct method commands** (reboot, ping, etc.) from `CommandExecutorWorker`, testing the command round-trip path.
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
┌────────────────────────────────────────────────────────────────────────┐
│  Blazor Server (port 7299)                                             │
│                                                                        │
│  ┌──────────────┐     ┌───────────────────────────────┐                │
│  │  Home.razor   │────▶│  TelemetrySimulatorService    │                │
│  │  (Dashboard)  │◀────│  (Singleton engine)           │                │
│  └──────────────┘     └───────────┬───────────────────┘                │
│                                   │                                     │
│              ┌────────────────────┼──────────────────────┐              │
│              │                    │                       │              │
│              ▼                    ▼                       ▼              │
│  ┌──────────────────┐  ┌─────────────────────┐  ┌──────────────────┐   │
│  │  DeviceClient(s)  │  │  IDbContextFactory   │  │  RegistryManager │   │
│  │  (per device)     │  │  → DB for seeding    │  │  → device keys   │   │
│  └────────┬─────────┘  └─────────────────────┘  └──────────────────┘   │
│           │ D2C messages + direct method handlers                       │
└───────────┼────────────────────────────────────────────────────────────┘
            │
            ▼
┌───────────────────────┐     ┌──────────────────────────────────────────┐
│   Azure IoT Hub       │────▶│  Event Hubs (built-in endpoint)          │
│   (D2C + Methods)     │     │                                          │
└───────────────────────┘     └─────────────────┬────────────────────────┘
                                                │
                                                ▼
                              ┌──────────────────────────────────────────┐
                              │  TelemetryIngestionWorker                │
                              │  → DB (TelemetryHistory, LatestState,    │
                              │    ConnectivityState, Alarms)            │
                              │  → Service Bus (offline-checks,          │
                              │    notifications)                        │
                              └──────────────────────────────────────────┘
```

### Key components

| Component | Role |
|---|---|
| `Program.cs` | Configures Blazor Server, Key Vault, EF Core, manufacturing + DPS + IoT Hub services, and the simulator singleton |
| `TelemetrySimulatorService` | Singleton engine — manufactures devices, simulates DPS first boot, simulates assignment, connects `DeviceClient` per device, runs the tick loop sending D2C messages, handles direct method callbacks |
| `SimulatedDevice` | In-memory model holding live telemetry values, scenario flags, and `DeviceClient` reference per device |
| `Home.razor` | Interactive dashboard — KPI counters (D2C sent, commands received, pending commands), device cards, alarm table, event log |
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

### 4. Device client connection

After assignment, each device's IoT Hub symmetric key is retrieved via `RegistryManager` and a `DeviceClient` is created using MQTT transport:

1. `ConnectDeviceClientsAsync()` iterates active simulated devices.
2. For each device, looks up the IoT Hub identity and extracts the symmetric key.
3. Creates `DeviceClient` with `DeviceAuthenticationWithRegistrySymmetricKey`.
4. Registers a **default direct method handler** (`HandleDirectMethodAsync`) that responds to:
   - `reboot` — returns `{"result":"rebooting","uptime":...}`
   - `ping` — returns `{"result":"pong","timestamp":"..."}`
   - `captureSnapshot` — returns `{"result":"snapshot_captured","file":"..."}`
   - `runSelfTest` — returns `{"result":"self_test_passed",...}`
   - `syncNow` — returns `{"result":"sync_complete","records":...}`
   - `clearFault` — returns `{"result":"fault_cleared"}`
5. Opens the client connection.

### 5. Tick loop

A background task runs every **3 seconds**. For each online device:

1. **`Tick()`** — randomizes telemetry values:
   - Panel voltage: 23.5–25.0 V
   - Temperature: 20–28 °C
   - Signal RSSI: −80 to −50 dBm
   - Pump cycles on for 3 of every 10 ticks (current 3.5–5.5 A when running)
   - `HighWaterAlarm` is driven by the `ForceHighWater` scenario flag

2. **`SendTelemetryAsync()`** — serializes a `TelemetryMessage` to JSON and sends it as a D2C message via `DeviceClient.SendEventAsync()`. The message includes `messageType = "telemetry"` as an IoT Hub application property. The `TelemetryIngestionWorker` picks it up from Event Hubs and writes it to SQL, evaluates alarms, and schedules offline checks via Service Bus.

3. Fires `OnStateChanged` so the Blazor dashboard re-renders.

### 6. Dashboard refresh

The `Home.razor` page subscribes to `OnStateChanged` and also runs a 2-second timer that calls `GetSnapshotAsync()`. The snapshot queries the database for:

- Active alarm count
- Offline device count
- Telemetry rows in the last 5 minutes
- Top 20 active/acknowledged alarms with details

## Dashboard sections

| Section | What it shows |
|---|---|
| **Controls bar** | Start/Stop button, device count input, running/stopped badge |
| **KPI row** | Total devices, D2C sent count, commands received, pending commands, offline count, ingested telemetry (5 min) |
| **Device fleet** | Card per device with live readings (voltage, current, pump state, HW alarm, temperature, signal, runtime) and last command received |
| **Active alarms table** | Alarm ID, device serial, type, severity, status, start time |
| **Event log** | Scrolling list of timestamped events (starts, stops, alarm raises/clears, commands, errors) |

## Scenario controls

Each device card has buttons to trigger scenarios:

| Button | Effect |
|---|---|
| **Trigger HW Alarm** | Sets `ForceHighWater = true` → next tick writes `HighWaterAlarm = true` → `IAlarmService` creates a Critical alarm |
| **Clear HW Alarm** | Sets `ForceHighWater = false` → next tick clears the alarm → `AutoResolveAlarmsAsync` resolves it |
| **Go Offline** | Sets `ForceOffline = true` → device is skipped in the tick loop (no D2C messages sent) → offline detection kicks in via Service Bus deadline scheduling |
| **Bring Online** | Sets `ForceOffline = false` → device resumes sending D2C messages → connectivity state updates, `DeviceOffline` alarm auto-resolves |

## DI registrations

```csharp
// EF Core — factory for concurrent access
builder.Services.AddDbContextFactory<SentinelDbContext>(options =>
    options.UseSqlServer(sqlConn, o => o.EnableRetryOnFailure()));

// Note: IAlarmService/INotificationService are no longer registered in the simulator.
// Alarm evaluation is now handled by the TelemetryIngestionWorker after it processes
// the D2C message from Event Hubs.

// Simulator engine
builder.Services.AddSingleton<TelemetrySimulatorService>();
```

`IDbContextFactory` is used instead of a single scoped `DbContext` because the simulator singleton outlives any DI scope and produces contexts on demand (for seeding, cleanup, and snapshot queries).

`TelemetrySimulatorService` depends on `IConfiguration` to extract the IoT Hub hostname from the `IoTHubServiceConnectionString` for building per-device `DeviceClient` instances.

## Data created

Running the simulator populates these tables:

| Table | Records |
|---|---|
| `Companies` | 1 ("Simulator Corp") |
| `Customers` | 1 ("Sim User") |
| `Sites` | 1 ("Simulator Site") |
| `Devices` | N simulator-marked rows (hardware revision `sim-v1.0`) |
| `DeviceAssignments` | N |
| `TelemetryHistory` | Grows continuously (~N rows every 3 s, written by IngestionWorker) |
| `LatestDeviceStates` | N (upserted by IngestionWorker each tick) |
| `DeviceConnectivityStates` | N (upserted by IngestionWorker each tick) |
| `Alarms` | Created/resolved by IngestionWorker and OfflineCheckWorker |
| `CommandLogs` | Created when commands are dispatched via the API |
| `NotificationIncidents` | Created when alarms fire (processed by NotificationDispatchWorker) |

> **Tip:** Simulator data is tagged with hardware revision `sim-v1.0` (legacy runs may also have `SIM-` serials).

## Cleanup

The **Clear All Data** action in the dashboard removes simulator data from SQL (including telemetry, alarms, command logs, maintenance windows, device assignments, connectivity states, device states, and devices) and also removes simulator devices from IoT Hub. Device clients are disconnected before deletion.

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
