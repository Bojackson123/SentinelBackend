# Telemetry Ingestion Pipeline

## Overview

The `SentinelBackend.Ingestion` project is a standalone .NET worker service that consumes messages from Azure IoT Hub's built-in Event Hub endpoint. It runs independently from the API and uses the same SQL database.

## Architecture

```
IoT Hub  ──►  Event Hub (built-in)  ──►  EventProcessorClient  ──►  TelemetryIngestionWorker
                                              │                            │
                                      checkpoint store              ┌──────┴──────┐
                                      (Blob: iot-checkpoints)       │  SQL Server │
                                                                    │  Blob Store │
                                                                    └─────────────┘
```

## Host Configuration (Program.cs)

The worker host registers:

| Service | Source | Purpose |
|---------|--------|---------|
| `EventProcessorClient` | `IoTHubEventHubConnectionString` | Reads from `$Default` consumer group |
| `BlobContainerClient` (checkpoints) | `StorageConnectionString` / `iot-checkpoints` | Stores partition offsets |
| `BlobContainerClient` (raw archive) | `StorageConnectionString` / `raw-telemetry` | Archives raw JSON |
| `SentinelDbContext` | `SqlConnectionString` | EF Core with retry-on-failure |

All secrets are loaded from Azure Key Vault via `AddAzureKeyVault`.

## Message Processing Flow

### 1. Event Received
The `EventProcessorClient` delivers each event to `HandleEventAsync`. The worker extracts:
- **deviceId** — from `iothub-connection-device-id` system property (set by IoT Hub, not the device)
- **messageType** — from user property `messageType` (defaults to `"telemetry"`)
- **payload** — UTF-8 JSON body

### 2. Envelope Validation
Four required fields are checked. If any fail, a `FailedIngressMessage` is recorded and the checkpoint advances:

| Check | Failure Reason |
|-------|---------------|
| JSON deserialises to `TelemetryMessage` | `DeserializationFailure` |
| Result is not null | `NullMessage` |
| `iothub-connection-device-id` present | `MissingDeviceIdentity` |
| `messageId` present | `MissingMessageId` |
| `timestampUtc` present | `MissingTimestamp` |

### 3. Device Lookup
The device is fetched by IoT Hub `DeviceId` (string) using `IgnoreQueryFilters()` to include soft-deleted devices. Includes `LatestState` and `ConnectivityState` for in-memory update.

If no device record exists → `UnknownDevice` failure recorded.

### 4. Deduplication
Checks `TelemetryHistory` for an existing row with the same `(DeviceId, MessageId)`. If found, the message is silently skipped and the checkpoint advances.

### 5. Ownership Snapshot
Resolves the current active assignment chain:
```
DeviceAssignment (UnassignedAt == null) → Site → Customer → Company
```
The resulting `AssignmentId`, `SiteId`, `CustomerId`, `CompanyId` are stamped onto the `TelemetryHistory` row. This denormalisation means historical queries don't need joins and are correct even if ownership changes later.

### 6. Raw Payload Archive
The original JSON is uploaded to Azure Blob Storage:
```
raw-telemetry/{deviceId}/{yyyy/MM/dd}/{messageId}.json
```
- **409 Conflict** (blob exists) is silently ignored (dedup at blob level)
- Other upload failures are logged but do **not** block processing

### 7. TelemetryHistory Insert
A new `TelemetryHistory` row is created with all telemetry fields, timestamps, ownership snapshot, and the blob URI.

### 8. Latest State Update (telemetry messages only)
The `LatestDeviceState` row is updated **only if the incoming timestamp is newer** than `LastTelemetryTimestampUtc`. This ensures out-of-order messages don't corrupt the snapshot.

Fields updated: `PanelVoltage`, `PumpCurrent`, `PumpRunning`, `HighWaterAlarm`, `TemperatureC`, `SignalRssi`, `RuntimeSeconds`, `ReportedCycleCount`, `LastMessageId`, `LastBootId`, `LastSequenceNumber`.

If no `LatestDeviceState` row exists, one is created.

### 9. Connectivity State Update (all message types)
The `DeviceConnectivityState` row is **always updated** regardless of message type or timestamp ordering:

- `LastMessageReceivedAt` = `DateTime.UtcNow`
- `LastEnqueuedAtUtc` = Event Hub enqueue time
- `LastMessageType` = `messageType`
- `IsOffline` = `false` (device just sent something)
- `LastTelemetryReceivedAt` — only updated for `messageType == "telemetry"`

If no row exists, one is created.

### 10. Status Transitions

**First telemetry activation:**
If `device.Status == Assigned` and `messageType == "telemetry"`, the status transitions to `Active`. This marks the device as operational after its first real data transmission post-assignment.

**Lifecycle/boot handling:**
If `messageType == "lifecycle"` and `BootId` is present, `connectivity.LastBootId` is updated.

**Firmware version:**
If the message contains a non-empty `FirmwareVersion`, `device.FirmwareVersion` is updated (any message type).

### 11. Checkpoint
After successful processing (or after recording a failure), `UpdateCheckpointAsync()` is called to advance the consumer's position in the partition.

## TelemetryMessage Contract

```csharp
public class TelemetryMessage
{
    public string? MessageId { get; set; }
    public DateTime? TimestampUtc { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? BootId { get; set; }
    public int? SequenceNumber { get; set; }
    public double? PanelVoltage { get; set; }
    public double? PumpCurrent { get; set; }
    public bool? PumpRunning { get; set; }
    public bool? HighWaterAlarm { get; set; }
    public double? TemperatureC { get; set; }
    public int? SignalRssi { get; set; }
    public int? RuntimeSeconds { get; set; }
    public int? ReportedCycleCount { get; set; }
}
```

## Error Handling

| Scenario | Action |
|----------|--------|
| Deserialization failure | Record `FailedIngressMessage`, checkpoint, continue |
| Missing required envelope field | Record `FailedIngressMessage`, checkpoint, continue |
| Unknown device | Record `FailedIngressMessage`, checkpoint, continue |
| Duplicate message | Skip silently, checkpoint, continue |
| Blob upload failure | Log warning, continue processing (non-fatal) |
| Partition error | Logged via `ProcessErrorAsync`, processing continues |
| `RecordFailedIngressAsync` itself throws | Caught and logged — never crashes the worker |

The worker never throws from `HandleEventAsync` — all errors are caught, recorded where possible, and processing continues. This ensures one bad message does not block the partition.
