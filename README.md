# Sentinel IoT Backend

Multi-tenant IoT monitoring platform for grinder pump installations. Field devices read electrical and status signals from existing grinder pump control panels and transmit telemetry to the cloud over cellular connectivity.

## What It Does

- **Device provisioning** — manufacturing batch generation, DPS allocation, symmetric key derivation
- **Telemetry ingestion** — Event Hub consumer with deduplication, ownership snapshots, blob archival
- **Alarm management** — creation, acknowledgement, suppression, resolution with full event audit trail
- **Device commands** — async direct methods (reboot, ping, self-test, etc.) via IoT Hub
- **Twin configuration** — desired property updates with audit logging
- **Multi-tenancy** — role-based access with automatic query scoping per company/customer

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 9 |
| API | ASP.NET Core Web API |
| Database | Azure SQL + EF Core 9 |
| Auth | ASP.NET Core Identity + JWT Bearer (HMAC-SHA256) |
| IoT | Azure IoT Hub + Device Provisioning Service |
| Messaging | Event Hubs (IoT Hub built-in endpoint) |
| Storage | Azure Blob Storage (raw telemetry archive) |
| Secrets | Azure Key Vault |
| Monitoring | Application Insights |

## Solution Structure

```
SentinelBackend.sln
├── SentinelBackend.Domain          # Entities, enums — no dependencies
├── SentinelBackend.Application     # Interfaces, services, DTOs
├── SentinelBackend.Contracts       # Shared API contracts
├── SentinelBackend.Infrastructure  # EF Core, IoT Hub, DPS, blob implementations
├── SentinelBackend.Api             # ASP.NET Core host, controllers, auth
├── SentinelBackend.Ingestion       # Worker service — Event Hub telemetry consumer
└── tests/
    ├── SentinelBackend.Api.Tests
    ├── SentinelBackend.Ingestion.Tests
    └── SentinelBackend.Tests.Shared
```

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Azure subscription with IoT Hub, DPS, SQL, Key Vault, Blob Storage, Application Insights
- `KeyVaultUrl` environment variable pointing to your Key Vault

### Key Vault Secrets

| Secret | Purpose |
|--------|---------|
| `SqlConnectionString` | Azure SQL connection string |
| `IoTHubConnectionString` | IoT Hub service policy connection string |
| `IoTHubEventHubConnectionString` | Built-in Event Hub-compatible endpoint |
| `DpsPrimaryKey` | DPS enrollment group symmetric key |
| `StorageConnectionString` | Blob Storage connection string |
| `JwtSigningKey` | HMAC-SHA256 signing key for JWT tokens |
| `DpsWebhookSecret` | Shared secret for DPS custom allocation webhook |

### Run

```bash
# API
dotnet run --project SentinelBackend.Api

# Telemetry ingestion worker
dotnet run --project SentinelBackend.Ingestion

# Tests
dotnet test
```

### Apply Migrations

```bash
dotnet ef database update --project SentinelBackend.Infrastructure --startup-project SentinelBackend.Api
```

## Documentation

See [`docs/how-it-works/`](docs/how-it-works/README.md) for detailed documentation:

- [Architecture Overview](docs/how-it-works/architecture-overview.md)
- [API Reference](docs/how-it-works/api-reference.md)
- [Data Model](docs/how-it-works/data-model.md)
- [Telemetry Ingestion](docs/how-it-works/telemetry-ingestion.md)
- [Auth & Tenancy](docs/how-it-works/auth-and-tenancy.md)
- [Testing](docs/how-it-works/testing.md)

## Tests

91 unit tests across 13 files covering device lifecycle, ingestion pipeline, tenant isolation, alarms, commands, and configuration.

```bash
dotnet test --verbosity normal
```
