# Architecture Overview

## Solution Structure

The solution follows a clean-architecture layout with six projects plus two test projects and a shared test library.

```
SentinelBackend.sln
├── SentinelBackend.Api/            ASP.NET Core Web API (host)
├── SentinelBackend.Ingestion/      Worker service (telemetry consumer)
├── SentinelBackend.Application/    Interfaces, services, DTOs
├── SentinelBackend.Domain/         Entities and enums (no dependencies)
├── SentinelBackend.Infrastructure/ EF Core, Azure SDK integrations, repositories
├── SentinelBackend.Contracts/      Shared wire-format DTOs
└── tests/
    ├── SentinelBackend.Api.Tests/
    ├── SentinelBackend.Ingestion.Tests/
    └── SentinelBackend.Tests.Shared/   Shared test helpers (TestDb, FakeTenantContext)
```

### Dependency Direction

```
Domain  ←  Application  ←  Infrastructure
                ↑                ↑
            Contracts        Persistence (EF Core)
                ↑
        Api / Ingestion (hosts)
```

- **Domain** has zero dependencies — pure entities and enums.
- **Application** depends only on Domain. Defines interfaces (`IDeviceRepository`, `IDeviceTwinService`, etc.) and service implementations.
- **Infrastructure** depends on Application + Domain. Implements interfaces using EF Core, Azure IoT Hub SDK, and Azure DPS SDK.
- **Api** and **Ingestion** are top-level hosts that wire everything together.

## Hosting

### SentinelBackend.Api

ASP.NET Core Web API hosting:
- REST controllers for devices, alarms, sites, assignments, manufacturing, DPS webhook, and auth
- JWT Bearer authentication with role-based authorization policies
- `CommandExecutorWorker` — a `BackgroundService` that polls for pending commands and executes IoT Hub direct methods
- `OfflineMonitorWorker` — a `BackgroundService` that periodically scans device connectivity state and raises/resolves `DeviceOffline` alarms
- Scalar API reference UI in development mode
- Secrets loaded from Azure Key Vault via `DefaultAzureCredential`

### SentinelBackend.Ingestion

.NET Worker Service hosting:
- `TelemetryIngestionWorker` — a `BackgroundService` consuming IoT Hub's Event Hubs-compatible endpoint via `EventProcessorClient`
- Checkpoints stored in Azure Blob Storage (`iot-checkpoints` container)
- Raw payloads archived to Blob Storage (`raw-telemetry` container)
- Connects to the same Azure SQL database as the API

## Azure Dependencies

| Service | Purpose |
|---|---|
| Azure SQL | Primary operational database |
| Azure Key Vault | Secrets (connection strings, JWT key, DPS keys) |
| Azure IoT Hub | Device registry, twin management, direct methods, telemetry endpoint |
| Azure DPS | Device provisioning with custom allocation webhook |
| Azure Blob Storage | Event Hub checkpoints, raw telemetry archive |
| Application Insights | Logging and tracing (wired via standard .NET logging) |

## Configuration

All secrets are stored in Azure Key Vault and loaded at startup. The only config in `appsettings.json` is logging levels. `appsettings.Development.json` contains the Key Vault URL.

Key Vault secrets used:
- `SqlConnectionString` — Azure SQL connection string
- `IoTHubServiceConnectionString` — IoT Hub service policy connection string
- `IoTHubEventHubConnectionString` — IoT Hub Event Hubs-compatible endpoint
- `StorageConnectionString` — Blob Storage connection string
- `DpsConnectionString` — DPS service connection string
- `DpsIotHubHostName` — IoT Hub hostname for DPS allocation
- `DpsEnrollmentPrimaryKey` — Enrollment group symmetric key
- `DpsWebhookSecret` — Secret for validating DPS webhook calls
- `JwtSigningKey` — HMAC-SHA256 signing key for JWT tokens
- `JwtIssuer`, `JwtAudience` — JWT token validation parameters

## Dependency Injection

All infrastructure services are registered in `Infrastructure/DependencyInjection.cs`:

| Registration | Lifetime | Purpose |
|---|---|---|
| `SentinelDbContext` | Scoped | EF Core database context |
| `ASP.NET Core Identity` | Scoped | User management (ApplicationUser, roles) |
| `ProvisioningServiceClient` | Singleton | Azure DPS client |
| `RegistryManager` | Singleton | IoT Hub twin management |
| `ServiceClient` | Singleton | IoT Hub direct method invocation |
| `IDeviceRepository` → `DeviceRepository` | Scoped | Device data access |
| `IAlarmService` → `AlarmService` | Scoped | Alarm creation (with dedup), resolution, auto-resolve |
| `IDeviceTwinService` → `DeviceTwinService` | Scoped | Twin desired property updates |
| `IDirectMethodService` → `DirectMethodService` | Scoped | Direct method invocation |
| `IDpsEnrollmentService` → `DpsEnrollmentService` | Scoped | DPS key derivation |
| `IDpsAllocationService` → `DpsAllocationService` | Scoped | DPS custom allocation logic |
| `IManufacturingBatchService` → `ManufacturingBatchService` | Scoped | Batch device creation |
| `ITenantContext` → `HttpTenantContext` | Scoped | Tenant scoping from JWT claims (Api only) |
