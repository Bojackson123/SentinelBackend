# Data Model

## Entity–Relationship Overview

```
Company 1──* Customer 1──* Site 1──* DeviceAssignment *──1 Device
                                      └── Lead (1:1 per Device)

Device 1──1 LatestDeviceState
Device 1──1 DeviceConnectivityState
Device 1──* TelemetryHistory
Device 1──* Alarm 1──* AlarmEvent
Device 1──* CommandLog
Device 1──* DesiredPropertyLog (via FK)

Company 1──0..1 Subscription
Customer 1──0..1 Subscription

ApplicationUser *──0..1 Company
ApplicationUser *──0..1 Customer
```

---

## Entities

### Company
Installer / service-provider organisation that owns customers and controls devices.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK, auto-increment |
| Name | string(256) | required |
| ContactEmail | string(256) | required |
| ContactPhone | string(32) | |
| BillingEmail | string(256) | required |
| StripeCustomerId | string(256) | future Stripe integration |
| SubscriptionStatus | enum → string | Trialing / Active / PastDue / Cancelled / Suspended |
| IsDeleted / DeletedAt | bool / DateTime? | soft delete, filtered by global query filter |
| CreatedAt / UpdatedAt | DateTime | |

**Navigations:** `Customers`, `Subscription`

### Customer
End-customer (homeowner or business). May belong to a company or be standalone.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| CompanyId | int? | FK → Company (SetNull on delete) |
| FirstName / LastName | string(128) | required |
| Email | string(256) | required |
| Phone | string(32) | |
| StripeCustomerId | string(256) | |
| SubscriptionStatus | enum → string? | only for standalone homeowners |
| CreatedAt / UpdatedAt | DateTime | |

**Navigations:** `Company`, `Sites`, `Subscription`

### Site
Physical location where one or more devices are installed.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| CustomerId | int | FK → Customer (Restrict) |
| Name | string(256) | required |
| AddressLine1 / AddressLine2 | string(256) | Line1 required |
| City / State / PostalCode / Country | string | required |
| Latitude / Longitude | double? | |
| Timezone | string(64) | IANA tz, e.g. `America/Chicago` |
| IsDeleted / DeletedAt | bool / DateTime? | soft delete, query filter |
| CreatedAt / UpdatedAt | DateTime | |

**Navigations:** `Customer`, `DeviceAssignments`

### Device
Central entity — represents a physical IoT device through its full lifecycle.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK (internal) |
| DeviceId | string(128)? | IoT Hub identity, set at DPS provisioning. Unique filtered index |
| SerialNumber | string(64) | assigned at manufacturing. Unique index |
| HardwareRevision | string(64)? | |
| FirmwareVersion | string(64)? | updated on telemetry ingestion |
| Status | enum → string | Manufactured → Unprovisioned → Assigned → Active → Decommissioned |
| ProvisionedAt | DateTime? | first DPS allocation |
| ManufacturedAt | DateTime? | batch creation timestamp |
| IsDeleted / DeletedAt | bool / DateTime? | soft delete, query filter |
| CreatedAt / UpdatedAt | DateTime | |

**Navigations:** `LatestState`, `ConnectivityState`, `Assignments`, `TelemetryHistory`, `Alarms`, `Commands`, `Lead`

### DeviceAssignment
Links a device to a site for a period of time. Supports re-assignment by closing the previous record.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| DeviceId | int | FK → Device (Restrict) |
| SiteId | int | FK → Site (Restrict) |
| AssignedByUserId | string(450) | required |
| AssignedAt | DateTime | |
| UnassignedByUserId | string(450)? | |
| UnassignedAt | DateTime? | null = active assignment |
| UnassignmentReason | enum → string? | |

**Active assignment:** `UnassignedAt == null`

### LatestDeviceState
Materialised view — stores the most-recent telemetry snapshot for fast reads.

| Column | Type | Notes |
|--------|------|-------|
| DeviceId | int | PK + FK → Device (Cascade), 1:1 |
| LastTelemetryTimestampUtc | DateTime | |
| LastMessageId / LastBootId | string(256)? | |
| LastSequenceNumber | int? | |
| PanelVoltage / PumpCurrent | double? | |
| PumpRunning / HighWaterAlarm | bool? | |
| TemperatureC | double? | |
| SignalRssi | int? | |
| RuntimeSeconds / ReportedCycleCount | int? | |
| DerivedCycleCount | long? | |
| UpdatedAt | DateTime | |

Updated only when incoming telemetry has `timestampUtc > LastTelemetryTimestampUtc` (only-if-newer).

### DeviceConnectivityState
Tracks when a device last communicated, regardless of message type.

| Column | Type | Notes |
|--------|------|-------|
| DeviceId | int | PK + FK → Device (Cascade), 1:1 |
| LastMessageReceivedAt | DateTime | any message type |
| LastTelemetryReceivedAt | DateTime? | telemetry only |
| LastEnqueuedAtUtc | DateTime? | Event Hub enqueue time |
| LastMessageType | string(64)? | |
| LastBootId | string(256)? | from lifecycle messages |
| OfflineThresholdSeconds | int | default 900 (15 min) |
| IsOffline | bool | reset to false on each message |
| SuppressedByMaintenanceWindow | bool | |
| UpdatedAt | DateTime | |

Updated on **every** message (telemetry, lifecycle, etc).

### TelemetryHistory
Immutable time-series record. One row per ingested message, with an ownership snapshot.

| Column | Type | Notes |
|--------|------|-------|
| Id | long | PK |
| DeviceId | int | FK → Device (Restrict) |
| MessageId | string(256) | required. Unique with DeviceId |
| MessageType | string(64) | `telemetry`, `lifecycle`, etc. |
| TimestampUtc / EnqueuedAtUtc / ReceivedAtUtc | DateTime | three clock references |
| DeviceAssignmentId / SiteId / CustomerId / CompanyId | int? | ownership snapshot at ingest |
| *(telemetry fields)* | various | same as LatestDeviceState |
| FirmwareVersion / BootId / SequenceNumber | | |
| RawPayloadBlobUri | string(1024)? | link to blob archive |

**Indexes:**
- `(DeviceId, MessageId)` — unique, deduplication
- `(DeviceId, TimestampUtc DESC)` — range queries

### Alarm
Represents a condition on a device (high-water, fault, etc).

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| DeviceId | int | FK → Device (Restrict) |
| DeviceAssignmentId / SiteId / CustomerId / CompanyId | int? | ownership snapshot |
| AlarmType | string(128) | e.g. `HighWater`, `PumpFault` |
| Severity | enum → string | Info / Warning / Critical |
| Status | enum → string | Active → Acknowledged → Suppressed → Resolved |
| SourceType | enum → string | ExplicitMessage / TelemetryFallback / SystemGenerated |
| TriggerMessageId | string(256)? | |
| StartedAt / ResolvedAt | DateTime | |
| SuppressReason / SuppressedByUserId | string? | |
| DetailsJson | string? | arbitrary metadata |

**Index:** `(DeviceId, AlarmType, Status)` — fast lookup by active alarm type.

### AlarmEvent
Audit trail for alarm state transitions.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| AlarmId | int | FK → Alarm (Cascade) |
| EventType | string(64) | e.g. `Created`, `Acknowledged`, `Suppressed`, `Resolved` |
| UserId | string(450)? | who performed the action |
| Reason | string(1000)? | |
| MetadataJson | string? | |
| CreatedAt | DateTime | |

### CommandLog
Tracks async commands sent to devices via IoT Hub direct methods.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| DeviceId | int | FK → Device (Restrict) |
| CommandType | string(64) | reboot / ping / capture-snapshot / run-self-test / sync-now / clear-fault |
| Status | enum → string | Pending → Sent → Succeeded / Failed / TimedOut |
| RequestedByUserId | string(450) | |
| RequestedAt / SentAt / CompletedAt | DateTime? | lifecycle timestamps |
| ResponseJson | string? | raw response from direct method |
| ErrorMessage | string(2000)? | |

**Index:** `(DeviceId, Status)` — `CommandExecutorWorker` polls for Pending commands.

### DesiredPropertyLog
Audit log for IoT Hub twin desired property changes.

| Column | Type | Notes |
|--------|------|-------|
| Id | long | PK |
| DeviceId | int | FK → Device (Restrict) |
| PropertyName | string(256) | |
| PreviousValue / NewValue | string(1000)? | |
| RequestedByUserId | string(450) | |
| RequestedAt | DateTime | |
| Success | bool | |
| ErrorMessage | string(2000)? | |

**Index:** `(DeviceId, RequestedAt)` — chronological audit trail.

### FailedIngressMessage
Dead-letter table for messages that could not be processed.

| Column | Type | Notes |
|--------|------|-------|
| Id | long | PK |
| SourceDeviceId | string(128)? | |
| MessageId | string(256)? | |
| PartitionId / Offset | string | Event Hub position |
| EnqueuedAt | DateTime | |
| FailureReason | string(256) | DeserializationFailure / NullMessage / MissingDeviceIdentity / MissingMessageId / MissingTimestamp / UnknownDevice |
| ErrorMessage | string(2000)? | |
| RawPayload | string? | original JSON for debugging |
| CreatedAt | DateTime | |

### Subscription
Stripe subscription record linked to either a Company or Customer.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| StripeSubscriptionId / StripeCustomerId | string(256) | required |
| OwnerType | enum → string | Company / Homeowner |
| CompanyId / CustomerId | int? | FK (SetNull) |
| Status | enum → string | |
| CurrentPeriodStart / CurrentPeriodEnd | DateTime | |

### Lead
Tracks device transfer opportunities when a device changes ownership.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| DeviceId | int | FK → Device (Restrict), 1:1 |
| SiteId | int | FK → Site (Restrict) |
| PreviousCompanyId / PreviousCustomerId | int? | FK (SetNull) |
| Status | enum → string | Available / InNegotiation / Sold |

### MaintenanceWindow
Scheduled maintenance period that suppress offline alerts.

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| ScopeType | enum → string | Device / Site / Company |
| DeviceId / SiteId / CompanyId | int? | polymorphic scope |
| StartsAt / EndsAt | DateTime | |
| Reason | string(1000)? | |
| CreatedByUserId | string(450) | |

**Index:** `(ScopeType, EndsAt)` — efficient window lookup.

### ApplicationUser
Extends ASP.NET Core `IdentityUser` with tenant links.

| Column | Type | Notes |
|--------|------|-------|
| *(all IdentityUser columns)* | | |
| FirstName / LastName | string? | |
| CompanyId | int? | FK → Company (SetNull) |
| CustomerId | int? | FK → Customer (SetNull) |
| CreatedAt / UpdatedAt | DateTime | |

---

## Enums

| Enum | Values |
|------|--------|
| **DeviceStatus** | Manufactured, Unprovisioned, Assigned, Active, Decommissioned |
| **AlarmSeverity** | Info, Warning, Critical |
| **AlarmSourceType** | ExplicitMessage, TelemetryFallback, SystemGenerated |
| **AlarmStatus** | Active, Acknowledged, Suppressed, Resolved |
| **CommandStatus** | Pending, Sent, Succeeded, Failed, TimedOut |
| **LeadStatus** | Available, InNegotiation, Sold |
| **MaintenanceWindowScope** | Device, Site, Company |
| **SubscriptionOwnerType** | Company, Homeowner |
| **SubscriptionStatus** | Trialing, Active, PastDue, Cancelled, Suspended |
| **UnassignmentReason** | CustomerRequest, SubscriptionLapsed, Transferred, Decommissioned |
| **UserRole** | InternalAdmin, InternalTech, CompanyAdmin, CompanyTech, HomeownerViewer |

All enums stored as **strings** in SQL via `.HasConversion<string>()`.

---

## EF Core Conventions

- **Soft deletes** on Company, Site, Device via `IsDeleted` + global query filter (`HasQueryFilter`)
- **String enum storage** — no magic integers; every enum column uses `.HasConversion<string>()`
- **Unique indexes** — `Device.DeviceId` (filtered where not null), `Device.SerialNumber`, `(TelemetryHistory.DeviceId, MessageId)`
- **Composite indexes** on frequently queried paths (alarm lookup, command poll, telemetry range scan, desired property audit)
- **Delete behaviours:** Restrict on most relationships, Cascade for 1:1 state tables, SetNull for optional parent links

## Migration History

| # | Name | Purpose |
|---|------|---------|
| 1 | `InitialCreate` | Identity tables, Company, Customer, Site, Device, LatestDeviceState |
| 2 | `FixDeviceNullableColumns` | Make Device.DeviceId nullable (set at DPS, not manufacturing) |
| 3 | `FixDeviceIdNullable2` | Follow-up nullable column fix |
| 4 | `UseDeviceIntIdForLatestState` | Change LatestDeviceState PK from string DeviceId to int |
| 5 | `AddPhase1Entities` | Subscription, Lead, MaintenanceWindow, additional indexes |
| 6 | `AddPhase2Phase3Completion` | DeviceAssignment, TelemetryHistory, Alarm, AlarmEvent, FailedIngressMessage, DeviceConnectivityState |
| 7 | `AddDesiredPropertyLog` | CommandLog, DesiredPropertyLog (Phase 4) |
