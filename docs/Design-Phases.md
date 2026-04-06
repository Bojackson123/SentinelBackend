Sentinel Backend Implementation Phases


Version: 1.0

Status: Draft for Execution

Platform: .NET 9 + Azure

Last Updated: April 2026

1. Purpose


This document defines the implementation phases for the Sentinel backend based on the approved architecture.

Its purpose is to answer:


- what can begin immediately

- what should be built first

- what is safe to parallelize

- what should wait for a few follow-up decisions

- what “done” looks like for each phase

This is an execution document, not a replacement for the architecture doc.


---

2. Implementation Strategy


The backend should be built in vertical slices, not as isolated technical layers.

Recommended order:


1. establish the platform foundation

2. build the operational database and tenant model

3. implement one end-to-end telemetry slice

4. add assignment, configuration, and command workflows

5. implement alarming after finalizing a few remaining rules

6. add retention, archive, and production hardening

This approach gives usable progress early while reducing rework in the areas that still need minor specification decisions.


---

3. Current Readiness Assessment

3.1 Safe to Start Immediately


The following areas are sufficiently defined in the architecture and can begin now:


- solution and project structure

- infrastructure-as-code scaffolding

- Azure environment setup

- EF Core entities and migrations

- tenant/user/site/company/customer core tables

- device inventory and assignment model

- JWT authentication and policy-based authorization

- tenant scoping and global query filters

- ingestion worker skeleton

- telemetry deduplication

- latest state persistence

- connectivity state persistence

- failed ingress handling

- device APIs

- command log pipeline skeleton

- offline monitor skeleton

- observability scaffolding

3.2 Areas That Need a Short Follow-Up Spec Before Final Implementation


The following should not block foundation work, but should be finalized before full implementation:


- historical ownership attribution by event time vs processing time

- explicit alarm wire contract and clear/resolve semantics

- final routing strategy for alarm bookkeeping

- firmware capability matrix source of truth

- notification behavior and escalation rules


---

4. Phase Overview

Phase	Name	Start Now	Main Outcome
0	Foundation and Delivery Setup	Yes	Repo, CI/CD, Azure baseline, shared conventions
1	Core Domain, Data Model, and Auth	Yes	Operational DB, tenant model, authz, core APIs scaffold
2	Device Inventory and Assignment	Yes	Devices can be created, assigned, queried, and scoped correctly
3	Telemetry Ingestion MVP	Yes	End-to-end device telemetry ingestion to SQL and API reads
4	Device Configuration and Commands	Yes	Desired property updates and async direct methods
5	Alarming Foundation	Partial	Alarm model and APIs can start; automation waits on final rules
6	Notifications and Escalation	Partial	Depends on notification policy decisions
7	Archive, Retention, and Query Optimization	Yes	Cold archive and retention pipeline
8	Production Hardening and Operational Readiness	Yes	Resilience, dashboards, alerting, runbooks

---

5. Phase 0 — Foundation and Delivery Setup

5.1 Goal


Create the development and deployment foundation so feature work can proceed safely and consistently.

5.2 Scope

- create repository structure

- create solution/projects from the architecture

- establish coding standards and conventions

- set up CI pipeline

- set up CD pipeline or deployment automation

- create initial Azure environments

- set up secrets and managed identity strategy

- set up local development baseline

- create observability baseline

5.3 Deliverables

Repository and solution structure

	src/
	  SentinelBackend.Api/
	  SentinelBackend.Ingestion/
	  SentinelBackend.Domain/
	  SentinelBackend.Application/
	  SentinelBackend.Infrastructure/
	  SentinelBackend.Contracts/
	tests/
	  SentinelBackend.Api.Tests/
	  SentinelBackend.Ingestion.Tests/
	docs/
	  Sentinel-IoT-Backend-Architecture.md
	  Sentinel-Backend-Implementation-Phases.md
	  adr/

Engineering standards

- branch strategy

- pull request template

- issue template

- code style rules

- nullable reference types enabled

- analyzers enabled

- structured logging conventions

- API versioning strategy if needed

- Result<T> usage guidelines

Azure baseline

- resource groups per environment

- Azure SQL

- Storage Account

- Key Vault

- Application Insights / Log Analytics

- IoT Hub

- DPS

- Service Bus

- managed identities

- RBAC assignments

CI baseline

- restore/build/test

- migration validation

- lint/analyzer validation

- publish artifacts

- optional IaC validation

5.4 Can Start Now


Yes.

5.5 Exit Criteria


Phase 0 is complete when:


- the solution builds in CI

- test projects run in CI

- configuration is environment-based

- secrets are not stored in source control

- initial Azure resources exist for dev

- team members can run API and worker locally

- logging and tracing libraries are wired in


---

6. Phase 1 — Core Domain, Data Model, and Auth

6.1 Goal


Establish the application backbone: entities, persistence, tenancy, and security.

6.2 Scope

- implement core domain entities and enums

- define EF Core mappings and migrations

- implement ASP.NET Core Identity

- implement JWT authentication

- implement tenant-aware authorization policies

- implement global query filters for tenant-sensitive entities

- create seed/reference data approach

- scaffold core API endpoints

6.3 Initial Tables to Implement


Priority 1 tables:


- Devices

- DeviceAssignments

- DeviceConnectivityState

- LatestDeviceState

- TelemetryHistory

- Alarms

- AlarmEvents

- FailedIngressMessages

- CommandLog

- Companies

- Customers

- Sites

- ApplicationUser

- Subscriptions

- Leads

- MaintenanceWindows

6.4 Key Constraints to Implement Early

- unique Devices.device_id

- unique Devices.serial_number

- one active assignment per device

- unique DeviceConnectivityState.device_id

- unique LatestDeviceState.device_id

- unique (TelemetryHistory.device_id, message_id)

- subscription owner exclusivity

- role-to-tenant consistency

- soft-delete conventions

6.5 API Skeletons to Add

- GET /api/devices

- GET /api/devices/{deviceId}

- GET /api/devices/{deviceId}/state

- GET /api/alarms

- GET /api/alarms/{alarmId}/events

- POST /api/devices/{serialNumber}/assign

- GET /api/devices/{deviceId}/commands/{commandId}

These can start as simple vertical slices using real persistence.

6.6 Can Start Now


Yes.

6.7 Exit Criteria


Phase 1 is complete when:


- DB schema is created by migration

- tenant-aware auth works for all main roles

- API can return tenant-scoped devices and alarms

- one active assignment per device is enforced

- integration tests verify tenant isolation

- audit-friendly timestamps and soft-delete behavior are implemented


---

7. Phase 2 — Device Inventory and Assignment

7.1 Goal


Enable inventory management and installation workflows before telemetry starts flowing at scale.

7.2 Scope

- device creation/import flow for manufacturing records

- serial number handling

- assignment workflow

- unassignment workflow

- device status transitions

- site creation and lookup

- company/customer ownership relationships

- twin update trigger on assignment

- assignment history queries

7.3 Recommended Status Transition Rules to Implement

- Manufactured -> Unprovisioned

- Unprovisioned -> Assigned

- Assigned -> Active on first accepted telemetry

- any -> Decommissioned through administrative workflow

7.4 APIs in This Phase

- POST /api/sites

- POST /api/devices/{serialNumber}/assign

- optional POST /api/devices/{deviceId}/unassign

- GET /api/devices

- GET /api/devices/{deviceId}

7.5 Business Rules to Lock In During Implementation

- homeowner cannot assign devices

- company users can assign only within their company

- assignment creates history record

- only one active assignment per device

- twin desired properties include site timezone after assignment

7.6 Can Start Now


Yes.

7.7 Exit Criteria


Phase 2 is complete when:


- internal and company techs can assign devices

- active assignment is queryable

- historical assignment records are preserved

- device status transitions are enforced

- API authorization matches tenant rules

- assignment triggers desired property update workflow or command queue entry


---

8. Phase 3 — Telemetry Ingestion MVP

8.1 Goal


Deliver the first complete backend slice from IoT Hub message receipt to API query.

This is the most important “start now” phase.

8.2 Scope

- Event Hubs-compatible endpoint consumer

- message envelope validation

- JSON deserialization

- device identity resolution from system properties

- deduplication by (device_id, message_id)

- telemetry persistence

- connectivity state update

- latest state update

- raw payload archive stub or implementation

- poison message handling

- basic replay safety

- first-telemetry activation transition

8.3 Recommended MVP Message Types


Start with:


- telemetry

- lifecycle

Delay advanced diagnostic processing unless needed immediately.

8.4 Processing Rules to Implement in MVP

Deduplication

- unique key on (device_id, message_id)

Latest state ordering

- update latest state only if incoming timestampUtc is newer

Connectivity update

- update DeviceConnectivityState for every successfully processed message

Poison handling

- malformed or unrecoverable messages go to FailedIngressMessages

- checkpoint only after durable handling

Device trust model

- use IoT Hub system property for device identity

- ignore payload ownership hints

8.5 Suggested Worker Components

- EventProcessorHostedService

- MessageEnvelopeValidator

- TelemetryDeserializer

- DeviceIdentityResolver

- OwnershipResolver

- TelemetryPersistenceService

- LatestStateUpdater

- ConnectivityUpdater

- FailedIngressRecorder

- RawPayloadArchiver

8.6 APIs to Enable After This Phase

- GET /api/devices/{deviceId}/state

- GET /api/devices/{deviceId}/telemetry

8.7 Can Start Now


Yes.

8.8 Exit Criteria


Phase 3 is complete when:


- a test device can send telemetry through IoT Hub

- ingestion persists telemetry rows

- deduplication works under replay

- latest state is correct under out-of-order delivery

- connectivity updates independently of latest state ordering

- poison messages are durably captured

- API can return current state and telemetry history


---

9. Phase 4 — Device Configuration and Commands

9.1 Goal


Support remote configuration and operator-initiated device actions.

9.2 Scope

- desired property update API

- twin patch service

- async command submission

- command execution worker

- IoT Hub direct method invocation

- command status tracking

- audit logging

9.3 APIs in This Phase

- PATCH /api/devices/{deviceId}/desired-properties

- POST /api/devices/{deviceId}/commands/reboot

- POST /api/devices/{deviceId}/commands/ping

- POST /api/devices/{deviceId}/commands/capture-snapshot

- POST /api/devices/{deviceId}/commands/run-self-test

- POST /api/devices/{deviceId}/commands/sync-now

- POST /api/devices/{deviceId}/commands/clear-fault

- GET /api/devices/{deviceId}/commands/{commandId}

9.4 Implementation Notes

- API returns 202 Accepted

- write pending request to CommandLog

- background worker performs direct method call

- update command status to Sent, Succeeded, Failed, or TimedOut

- desired properties should be audited and versioned where practical

9.5 Can Start Now


Yes.

9.6 Exit Criteria


Phase 4 is complete when:


- authorized users can patch desired properties

- authorized users can submit async commands

- commands are executed by worker service

- status can be queried

- all actions are tenant-aware and audited


---

10. Phase 5 — Alarming Foundation

10.1 Goal


Implement the domain model and API surface for alarms, and begin processor scaffolding.

10.2 Scope Safe to Start Now

- Alarms and AlarmEvents persistence

- alarm query endpoints

- acknowledge endpoint

- suppress/clear endpoint shape

- offline monitor job skeleton

- Service Bus topic/subscription scaffolding

- duplicate active incident suppression logic framework

10.3 Scope That Should Wait for Final Rules


Before implementing full automation, finalize:


- explicit alarm payload contract

- alarm active vs cleared semantics

- clear vs suppress naming

- routing ownership for connectivity/archive bookkeeping

- firmware-specific fallback behavior

10.4 APIs in This Phase

- GET /api/alarms

- POST /api/alarms/{alarmId}/acknowledge

- POST /api/alarms/{alarmId}/clear or preferably /suppress

- GET /api/alarms/{alarmId}/events

10.5 Can Start Now


Partial.

10.6 Exit Criteria


Phase 5 is complete when:


- alarm incidents and audit events can be stored and queried

- alarm acknowledgment works

- suppress/clear behavior is implemented consistently with final naming

- offline monitor skeleton is deployed

- final automation rules are documented for implementation


---

11. Phase 6 — Notifications and Escalation

11.1 Goal


Deliver alarm-driven notification workflows.

11.2 Scope

- notification incident model

- notification attempts

- dispatch worker

- retries

- escalation tracking

- channel integration

11.3 Decision Dependencies


This phase should wait until product rules are defined for:


- SMS/email/push/voice support

- who is notified by tenant type

- retry schedules

- quiet hours

- acknowledgment behavior

- escalation policy

11.4 Can Start Now


Partial.

Safe now:


- tables

- interfaces

- event contracts

- worker scaffolding

Wait:


- final delivery logic

11.5 Exit Criteria


Phase 6 is complete when:


- alarm-triggered notification incidents are created

- channel delivery attempts are tracked

- retry and escalation behavior matches product policy

- delivery failures are observable and auditable


---

12. Phase 7 — Archive, Retention, and Query Optimization

12.1 Goal


Support long-term data retention without overloading hot SQL storage.

12.2 Scope

- raw payload archive to Blob/ADLS

- archive URI reference storage

- SQL retention policy for hot telemetry

- hot-to-cold archival job if needed

- query index tuning

- pagination implementation

- optional time-based partitioning plan

12.3 Can Start Now


Yes.

The basic archive plumbing can begin during Phase 3 and be completed here.

12.4 Exit Criteria


Phase 7 is complete when:


- raw payloads are archived

- telemetry history retention is enforced

- hot-path queries remain performant

- cold archive is retrievable for diagnostics and analytics use


---

13. Phase 8 — Production Hardening and Operational Readiness

13.1 Goal


Prepare the system for stable production rollout.

13.2 Scope

- dashboards

- alerts

- ingestion lag monitoring

- offline counts monitoring

- command failure monitoring

- worker restart monitoring

- load testing

- resilience testing

- runbooks

- backup and restore validation

- security review

- access review

- tenant isolation verification

- disaster recovery notes

13.3 Can Start Now


Yes, but most work lands after core functionality exists.

13.4 Exit Criteria


Phase 8 is complete when:


- operational dashboards exist

- alerts are actionable

- failure modes are documented

- scale test meets target assumptions

- support runbooks exist for top incident classes


---

14. What to Build Right Now


If execution started this week, the recommended immediate backlog is:

14.1 Week 1–2

- create solution and projects

- set up CI

- create Azure dev environment

- set up SQL, Key Vault, Storage, App Insights

- implement base domain abstractions

- implement ApplicationUser, tenant entities, and roles

- implement auth and JWT issuance/validation

- create first EF Core migration

- add tenant-aware query infrastructure

14.2 Week 2–3

- implement Devices, DeviceAssignments, LatestDeviceState,

DeviceConnectivityState, TelemetryHistory, FailedIngressMessages,

CommandLog, Alarms, AlarmEvents

- add device list/detail APIs

- add assignment API

- add authorization policies

- add integration tests for tenant isolation

14.3 Week 3–5

- implement ingestion worker skeleton

- connect to Event Hubs-compatible endpoint

- validate envelope

- persist telemetry

- implement deduplication

- update latest state

- update connectivity state

- record poison messages

- expose GET /api/devices/{deviceId}/state

- expose GET /api/devices/{deviceId}/telemetry

14.4 Week 5–6

- implement desired property patch workflow

- implement command submission and CommandLog

- implement command executor worker

- return 202 Accepted for commands

- expose command status API

This gives you a usable operational MVP before full alarm automation.


---

15. Recommended Parallel Workstreams


To reduce bottlenecks, work can be split as follows:

Workstream A — Platform/Foundation

- CI/CD

- Azure setup

- IaC

- environment configuration

- logging/tracing

Workstream B — Core App and Security

- Identity

- tenant model

- authorization

- API scaffolding

- EF Core migrations

Workstream C — Ingestion

- Event Hubs consumer

- validation

- dedup

- persistence

- connectivity/latest state

Workstream D — Device Management

- inventory

- assignments

- desired properties

- command pipeline

Workstream E — Architecture Decisions

- historical ownership rule

- alarm contract

- routing model

- firmware capability matrix

- notification policy

Workstream E should produce short ADRs while A-D move forward.


---

16. Decision Gates


The following gates should be passed before specific features are completed.

Gate A — Before Final Historical Query Features


Decide:


- ownership attribution uses assignment active at event time or processing time

- skew handling rules

- null-owner handling rules

Gate B — Before Alarm Automation


Decide:


- alarm payload schema

- raised vs cleared semantics

- suppress vs resolve naming

- duplicate-condition behavior

Gate C — Before Firmware-Aware Processing


Decide:


- capability matrix storage location

- version matching strategy

- update process

Gate D — Before Notification Delivery


Decide:


- channels

- escalation rules

- retry windows

- quiet hours

- tenant-type recipient rules


---

17. Suggested Definition of MVP


A practical MVP for Sentinel backend is reached when all of the following are true:


- devices can be represented in inventory

- devices can be assigned to sites

- telemetry can be ingested from IoT Hub

- telemetry is deduplicated and persisted

- latest state and connectivity state are queryable

- API enforces tenant scoping

- desired properties can be updated

- commands can be submitted asynchronously

- poison messages are captured

- basic dashboards and logs exist

Alarm automation can be delivered just after MVP if the final alarm contract is still being closed.


---

18. Out of Scope for Initial Coding


Do not spend early cycles on these unless required:


- custom MQTT broker logic

- frontend-specific API optimization before actual usage patterns exist

- complex analytics models

- ML pipelines

- deep reporting warehouse design

- advanced multi-hub routing before scale requires it

- X.509 rollout before symmetric-key MVP is working


---

19. Risks to Watch Early

19.1 Tenant leakage risk


Mitigation:


- auth policy tests

- query filter tests

- historical ownership tests

19.2 Ingestion replay/duplicate risk


Mitigation:


- unique constraints

- idempotent upserts

- integration replay tests

19.3 Out-of-order telemetry corrupting latest state


Mitigation:


- strict timestamp comparison

- separate connectivity table

19.4 Alarm semantics drift


Mitigation:


- finalize alarm wire contract before automation

19.5 Dev environment friction


Mitigation:


- standard local setup

- shared cloud dev resources

- replayable message fixtures


---

20. Immediate Action Checklist


Use this as the “start now” checklist.

Platform

-  Create repo structure and solution

-  Enable CI pipeline

-  Provision dev Azure resources

-  Configure Key Vault and managed identities

-  Add Application Insights and logging baseline

Core backend

-  Implement Domain/Application/Infrastructure layering

-  Add Result<T> pattern

-  Add EF Core DbContext and first migration

-  Implement Identity and JWT auth

-  Implement tenant context and authorization policies

Device and tenancy

-  Create Companies, Customers, Sites, Devices, DeviceAssignments

-  Enforce one active assignment per device

-  Implement device list/detail endpoints

-  Implement assignment workflow

Ingestion MVP

-  Create ingestion worker

-  Connect to Event Hubs-compatible endpoint

-  Validate telemetry envelope

-  Resolve device identity from system properties

-  Persist telemetry

-  Implement deduplication

-  Update latest state

-  Update connectivity state

-  Record failed ingress messages

Device operations

-  Implement desired property patch endpoint

-  Implement CommandLog

-  Implement command executor worker

-  Implement command status endpoint

ADRs to write in parallel

-  Historical ownership attribution rule

-  Alarm wire contract and lifecycle naming

-  Alarm routing/bookkeeping model

-  Firmware capability source of truth

-  Notification policy


---

21. Final Recommendation


Yes, coding should begin now.

Start with:


- Phase 0

- Phase 1

- Phase 2

- Phase 3

- Phase 4

Treat these as the first delivery track.

In parallel, write the small ADRs needed to unlock:


- full alarm automation

- notifications

- historical ownership finalization

That gives you forward progress without locking in the only parts of the design that still need a bit more precision.