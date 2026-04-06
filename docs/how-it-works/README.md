# Sentinel Backend — How It Works

This folder documents the current state of the Sentinel IoT backend as implemented through Phases 0–4.

## Contents

| Document | Description |
|---|---|
| [Architecture Overview](architecture-overview.md) | Solution structure, projects, hosting, and how they connect |
| [Data Model](data-model.md) | All entities, enums, relationships, and EF Core conventions |
| [Authentication & Tenancy](auth-and-tenancy.md) | JWT auth, roles, policies, and tenant scoping |
| [API Reference](api-reference.md) | Every endpoint, request/response shapes, and authorization |
| [Telemetry Ingestion](telemetry-ingestion.md) | Event Hubs consumer, processing rules, and data flow |
| [Testing](testing.md) | Test infrastructure, test inventory, and patterns used |

## Phase Status

| Phase | Name | Status |
|---|---|---|
| 0 | Foundation & Delivery Setup | Complete |
| 1 | Core Domain, Data Model, Auth | Complete |
| 2 | Device Inventory & Assignment | Complete |
| 3 | Telemetry Ingestion MVP | Complete |
| 4 | Device Configuration & Commands | Complete |
| 5 | Alarming Foundation | Not started |
| 6 | Notifications & Escalation | Not started |
| 7 | Archive, Retention, Query Optimization | Not started |
| 8 | Production Hardening | Not started |
