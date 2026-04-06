# Testing

## Overview

The solution has **116 unit tests** across **15 test files** in two test projects, plus a shared infrastructure project.

## Test Projects

| Project | Target | Tests |
|---------|--------|------:|
| `SentinelBackend.Api.Tests` | API controllers, services, domain logic | 90 |
| `SentinelBackend.Ingestion.Tests` | Telemetry ingestion worker, alarm detection | 22 |
| `SentinelBackend.Tests.Shared` | Shared helpers (no tests) | — |

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
| `TenantScopingTests.cs` | 9 | Device scoping (internal/company/homeowner), alarm scoping, site scoping, telemetry history scoping, cross-tenant isolation, zero-results for mismatched tenant |

### Phase 3–5 — Alarms
| File | Tests | Coverage |
|------|------:|----------|
| `AlarmWorkflowTests.cs` | 7 | Alarm creation, acknowledge transition, suppress with reason, resolve, event history recording, invalid transition rejection |
| `AlarmServiceTests.cs` | 13 | AlarmService: raise with dedup suppression, ownership snapshot, resolve, auto-resolve, alarm lifecycle cycles, trigger message storage |
| `OfflineMonitorTests.cs` | 5 | Offline threshold detection, alarm raising, auto-resolve on reconnect, duplicate suppression, maintenance window suppression, Active-only filtering |
| `TelemetryAlarmTests.cs` | 7 | Telemetry-fallback alarm detection: HighWater raise/auto-resolve, duplicate suppression, alarm cycling, ownership snapshot population |

### Phase 4 — Commands & Configuration
| File | Tests | Coverage |
|------|------:|----------|
| `CommandWorkflowTests.cs` | 8 | Command submission, valid command types, decommissioned device rejection, command status queries, pagination |
| `CommandExecutorTests.cs` | 5 | Background worker picks up Pending commands, marks Sent during execution, Succeeded on success, Failed on IoT Hub error, TimedOut handling |
| `DesiredPropertyLogTests.cs` | 5 | Twin property updates, audit log creation, previous value tracking, error recording on IoT Hub failure, multiple properties in single request |

## Running Tests

```bash
dotnet test
```

Or target a specific project:

```bash
dotnet test tests/SentinelBackend.Api.Tests
dotnet test tests/SentinelBackend.Ingestion.Tests
```

## Test Patterns

- **Arrange-Act-Assert** throughout
- **No mocking frameworks** — tests use the InMemory EF provider and `FakeTenantContext` directly
- **IoT Hub interactions** use interface-based fakes (`IIoTHubTwinService`, `IIoTHubCommandService`) injected via constructor
- **Each test is self-contained** — creates its own database, seeds its own data, verifies its own assertions
- **No test ordering dependencies** — all tests can run in parallel
