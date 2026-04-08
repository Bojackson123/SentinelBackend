# Testing

## Overview

The solution has **150 unit tests** across **20 test files** in two test projects, plus a shared infrastructure project and an **integration test project** for end-to-end Azure pipeline validation.

## Test Projects

| Project | Target | Tests |
|---------|--------|------:|
| `SentinelBackend.Api.Tests` | API controllers, services, domain logic | 124 |
| `SentinelBackend.Ingestion.Tests` | Telemetry ingestion worker, alarm detection | 26 |
| `SentinelBackend.Tests.Shared` | Shared helpers (no tests) | — |
| `SentinelBackend.IntegrationTests` | End-to-end Azure pipeline (skipped locally) | 4 |

All tests use **xUnit** with `[Fact]` attributes.

## Shared Infrastructure

### TestDb
Static helper that creates an isolated `SentinelDbContext` backed by EF Core's **InMemory provider**:

```csharp
public static SentinelDbContext Create() =>
    new(new DbContextOptionsBuilder<SentinelDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
```

Each test gets a unique database name (`Guid.NewGuid()`) — no cross-test contamination.

`TestDb.SeedFullHierarchyAsync(db)` creates a complete Company → Customer → Site → Device graph for tests that need a populated database. `SeedSecondHierarchyAsync` adds a second, independent hierarchy for cross-tenant isolation tests.

Returns a `SeedData` record with references to all created entities.

### FakeTenantContext
Test implementation of `ITenantContext` with the same `ApplyScope` logic as `HttpTenantContext` but with directly settable properties. Provides factory methods:
- `FakeTenantContext.Internal()` — sees everything
- `FakeTenantContext.ForCompany(companyId)` — company-scoped
- `FakeTenantContext.ForHomeowner(customerId)` — customer-scoped

### FakeMessagePublisher
In-memory implementation of `IMessagePublisher` for tests that need Service Bus message publishing assertions. Records all published messages in a `List<(string Queue, object Message, DateTimeOffset? Scheduled)>` for verification.

### FakeBlobArchiveService
In-memory implementation of `IBlobArchiveService` for tests that need blob archive operations without real Azure Blob Storage. Stores payloads in a `Dictionary<string, string>` keyed by URI.

### NullNotificationService
No-op implementation of `INotificationService` used in tests that don't verify notification behaviour.

## Test Files by Category

### Phase 1 — Domain & Data Model
| File | Tests | Coverage |
|------|------:|----------|
| `EntityModelTests.cs` | 11 | Entity creation, property defaults, navigation properties, enum values for all 17 entities |

### Phase 2 — Manufacturing, DPS, Assignment
| File | Tests | Coverage |
|------|------:|----------|
| `ManufacturingBatchTests.cs` | 7 | Batch generation, serial number format, sequence continuity, CSV export, DPS key derivation |
| `DpsAllocationServiceTests.cs` | 5 | First boot provisioning, re-provisioning, unknown device rejection, decommissioned device rejection, status transitions |
| `DeviceAssignmentTests.cs` | 6 | Assign/unassign flow, duplicate assignment rejection, status transitions, IoT Hub twin updates on assign |
| `DeviceStatusTransitionTests.cs` | 8 | Full lifecycle: Manufactured → Unprovisioned → Assigned → Active → Decommissioned, invalid transitions blocked |

### Phase 3 — Telemetry Ingestion
| File | Tests | Coverage |
|------|------:|----------|
| `IngestionLogicTests.cs` | 10 | Telemetry processing, latest state updates, ownership snapshot stamping, firmware version updates, lifecycle message handling, envelope validation failures, unknown device handling |
| `TelemetryDeduplicationTests.cs` | 5 | Exact duplicate rejection, same messageId different device allowed, only-if-newer latest state, out-of-order message handling |
| `ConnectivityStateTests.cs` | 5 | Connectivity tracking on telemetry, connectivity tracking on lifecycle, IsOffline reset, first-telemetry activation (Assigned → Active) |
| `OfflineSchedulingTests.cs` | 5 | Offline deadline scheduling via FakeMessagePublisher, zero-threshold skip, auto-resolve DeviceOffline alarm on telemetry arrival, no-auto-resolve when not offline, null publisher graceful skip |
| `TenantScopingTests.cs` | 9 | Device scoping (internal/company/homeowner), alarm scoping, site scoping, telemetry history scoping, cross-tenant isolation, zero-results for mismatched tenant |

### Phase 3–5 — Alarms
| File | Tests | Coverage |
|------|------:|----------|
| `AlarmWorkflowTests.cs` | 7 | Alarm creation, acknowledge transition, suppress with reason, resolve, event history recording, invalid transition rejection |
| `AlarmServiceTests.cs` | 13 | AlarmService: raise with dedup suppression, ownership snapshot, resolve, auto-resolve, alarm lifecycle cycles, trigger message storage |
| `OfflineMonitorTests.cs` | 5 | Offline threshold detection, alarm raising, auto-resolve on reconnect, duplicate suppression, maintenance window suppression, Active-only filtering |
| `OfflineCheckWorkerTests.cs` | 6 | Event-driven offline detection: stale deadline skip (newer telemetry), truly offline (mark + alarm), already-offline duplicate, non-active device skip, maintenance window suppression, unknown device ID |
| `TelemetryAlarmTests.cs` | 7 | Telemetry-fallback alarm detection: HighWater raise/auto-resolve, duplicate suppression, alarm cycling, ownership snapshot population |

### Phase 6 — Notifications & Escalation
| File | Tests | Coverage |
|------|------:|----------|
| `NotificationServiceTests.cs` | 9 | NotificationService: incident creation, duplicate suppression, acknowledge cancels pending, close on alarm resolve, escalation level progression, closed incident no-op, ownership propagation, AlarmService integration (raise creates incident, resolve closes incident) |

### Phase 7 — Archive, Retention, Query Optimization
| File | Tests | Coverage |
|------|------:|----------|
| `TelemetryRetentionTests.cs` | 6 | Hot retention purge, archive safety gate (RequireArchiveBeforePurge), unarchived purge when gate disabled, batch size limit, failed ingress purge, no-op on empty tables |
| `BlobArchiveServiceTests.cs` | 6 | Archive returns URI, archive idempotency, retrieval after archive, not-found returns null, delete removes content, delete not-found returns false |

### Phase 4 — Commands & Configuration
| File | Tests | Coverage |
|------|------:|----------|
| `CommandWorkflowTests.cs` | 8 | Command submission, valid command types, decommissioned device rejection, command status queries, pagination |
| `CommandExecutorTests.cs` | 7 | Background worker picks up Pending commands, marks Sent during execution, Succeeded on success, Failed on IoT Hub error, TimedOut handling, Service Bus single-command processing, non-existent command ID skip |
| `DesiredPropertyLogTests.cs` | 5 | Twin property updates, audit log creation, previous value tracking, error recording on IoT Hub failure, multiple properties in single request |

### Integration Tests — End-to-End Azure Pipeline
| File | Tests | Coverage |
|------|------:|----------|
| `EndToEndPipelineTests.cs` | 4 | D2C telemetry through full pipeline, HighWater alarm raised by ingestion, connectivity state updated, command round-trip (direct method invocation + status update) |

#### Integration Test Infrastructure

| Component | Role |
|-----------|------|
| `AzureFixture` | `IAsyncLifetime` fixture that creates an IoT Hub test device on setup and cascading-deletes all SQL rows + IoT Hub identity on teardown |
| `RequiresAzureFactAttribute` | Custom `[Fact]` that skips tests when Azure environment variables are not set |

#### Required Environment Variables

| Variable | Description |
|----------|-------------|
| `SENTINEL_SQL_CONNECTION` | Azure SQL connection string |
| `SENTINEL_IOTHUB_SERVICE_CONN` | IoT Hub service policy connection string |
| `SENTINEL_IOTHUB_HOSTNAME` | IoT Hub hostname (e.g. `sentinel-hub.azure-devices.net`) |

Tests are automatically skipped when these are not set, so `dotnet test` still works locally without Azure credentials. The API and Ingestion hosts must be running against the same Azure resources for the tests to pass.

## Running Tests

```bash
dotnet test
```

Or target a specific project:

```bash
dotnet test tests/SentinelBackend.Api.Tests
dotnet test tests/SentinelBackend.Ingestion.Tests
```

Run integration tests (requires Azure resources + running hosts):

```bash
set SENTINEL_SQL_CONNECTION=<your-sql-conn>
set SENTINEL_IOTHUB_SERVICE_CONN=<your-iothub-service-conn>
set SENTINEL_IOTHUB_HOSTNAME=<your-hub>.azure-devices.net
dotnet test tests/SentinelBackend.IntegrationTests
```

## Test Patterns

- **Arrange-Act-Assert** throughout
- **No mocking frameworks** — tests use the InMemory EF provider and `FakeTenantContext` directly
- **IoT Hub interactions** use interface-based fakes (`IIoTHubTwinService`, `IIoTHubCommandService`) injected via constructor
- **Each test is self-contained** — creates its own database, seeds its own data, verifies its own assertions
- **No test ordering dependencies** — all tests can run in parallel
