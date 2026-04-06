Sentinel IoT Backend Architecture


Version: 1.0

Status: Final

Platform: .NET 9 + Azure

Last Updated: April 2026

1. Overview


Sentinel is a multi-tenant IoT monitoring platform for grinder pump installations. Field devices read electrical and status signals from existing grinder pump control panels and transmit telemetry to the cloud over cellular connectivity. The Sentinel device is strictly read-only with respect to the pump and control panel. It does not control the pump hardware.

The backend is responsible for:


- secure device provisioning and communication

- telemetry ingestion and normalization

- alarm detection and lifecycle management

- tenant-scoped data access

- device configuration and remote diagnostics

- long-term telemetry retention for analytics and future ML use

This architecture is designed to support two business models:


1. independent homeowners who subscribe directly to Sentinel

2. plumbing companies who manage devices across many customer sites

The backend separates device communication from application queries and business APIs. Azure IoT Hub handles device connectivity. A .NET ingestion pipeline processes messages asynchronously. Azure SQL serves as the operational application database. Frontend applications talk only to the ASP.NET Core API.


---

2. Business Context

2.1 Grinder Pump Context


A grinder pump is a sewage pump installed at a residence or commercial site. It runs automatically when wastewater in the holding basin reaches a certain level. The installation includes a control panel with indicators such as high-water alarm, panel power, and pump activity.

2.2 Sentinel Device Context


The Sentinel device is an add-on monitor installed at the grinder pump control panel. It passively reads signals such as:


- panel voltage

- pump current

- pump running state

- high-water alarm output

- enclosure temperature

- cellular signal strength

- firmware and health diagnostics

The value of the platform is early warning. The on-site high-water alarm may be missed by the homeowner, but Sentinel detects the condition and notifies the appropriate party before it becomes a reactive service call.


---

3. Goals and Non-Goals

3.1 Goals

- support secure per-device communication at fleet scale

- provision devices through Azure DPS from day one

- decouple ingestion from API workloads

- persist normalized telemetry and alarm history in a queryable application database

- support both homeowner and plumbing-company tenant models

- enforce strict tenant isolation

- retain telemetry for long-term analytics and ML training

- support multiple frontend applications without backend redesign

- provide reliable alarming, notifications, and audit trails

3.2 Non-Goals

- building a custom MQTT broker

- allowing devices to communicate directly with the web API

- using Azure IoT Hub as the application query database

- controlling the grinder pump or control panel hardware

- making frontend clients consume IoT Hub directly


---

4. Architectural Principles

1. IoT Hub is the device communication layer, not the app database.

2. The API serves processed business data from Azure SQL, not live IoT Hub reads.

3. All ingestion is asynchronous and idempotent.

4. Device identity is taken only from IoT Hub system properties.

5. Site, customer, and tenant attribution come only from backend data.

6. Historical data is stamped with ownership at ingest time.

7. Latest state and connectivity state are tracked separately.

8. Alarm incidents and alarm audit events are separate entities.

9. Expected domain failures use Result<T>; infrastructure failures use exceptions.

10. Tenant isolation is enforced in both authorization and the data layer.


---

5. Final High-Level Architecture

	+-------------------------+
	| Sentinel Field Devices  |
	| - telemetry             |
	| - alarms                |
	| - diagnostics           |
	+------------+------------+
	             |
	             v
	+-------------------------+
	| Azure DPS               |
	| - enrollment groups     |
	| - provisioning          |
	| - optional custom       |
	|   allocation function   |
	+------------+------------+
	             |
	             v
	+-------------------------+
	| Azure IoT Hub           |
	| - per-device identity   |
	| - MQTT over TLS         |
	| - twins                 |
	| - direct methods        |
	| - routing               |
	+------+------------+-----+
	       |            |
	       |            +------------------------------+
	       v                                           v
	+----------------------------+          +--------------------------+
	| IoT Hub Event Hubs-        |          | Azure Service Bus Topic  |
	| compatible endpoint        |          | - explicit alarm events  |
	| - telemetry                |          | - fan-out subscriptions  |
	| - lifecycle                |          +-----------+--------------+
	| - diagnostics                          |           |
	+--------------+-------------+           |           |
	               |                         |           |
	               v                         v           v
	+-------------------------------+   +---------------------------+
	| .NET Ingestion Worker         |   | Alarm Processor(s)        |
	| - validation                  |   | - create/update alarms    |
	| - deduplication               |   | - notification incidents  |
	| - enrichment                  |   | - audit events            |
	| - SQL persistence             |   +---------------------------+
	| - blob archiving              |
	| - state updates               |
	+---------------+---------------+
	                |
	                v
	+---------------------------------------------+
	| Azure SQL + Blob/ADLS Archive               |
	| - devices                                   |
	| - assignments                               |
	| - latest state                              |
	| - connectivity state                        |
	| - telemetry history                         |
	| - alarms/events                             |
	| - tenant metadata                           |
	| - failed ingress                            |
	| - cold archive                              |
	+----------------+----------------------------+
	                 |
	                 v
	+---------------------------------------------+
	| ASP.NET Core API                            |
	| - device management                         |
	| - tenant-scoped queries                     |
	| - desired property updates                  |
	| - async direct method requests              |
	| - auth and authorization                    |
	+----------------+----------------------------+
	                 |
	                 v
	+---------------------------------------------+
	| React Applications                          |
	| - Internal Admin / Tech App                 |
	| - Plumbing Company App                      |
	| - Consumer Homeowner App                    |
	+---------------------------------------------+


---

6. Azure Service Selection

6.1 Azure IoT Hub


IoT Hub is the central device communication platform.

Responsibilities:


- device identity and authentication

- secure MQTT over TLS connectivity

- cloud-to-device operations

- device twins

- direct methods

- message routing

6.2 Azure Device Provisioning Service


DPS handles zero-touch or low-touch provisioning.

Responsibilities:


- enrollment groups

- first-boot registration

- assignment to IoT Hub

- optional custom allocation logic for future multi-hub growth

Initial authentication model:


- symmetric key enrollment groups

Planned future path:


- X.509 certificate-based provisioning for larger fleets or stronger compliance requirements

6.3 Azure SQL Database


Azure SQL is the operational application database.

It stores:


- device metadata

- current state

- connectivity state

- telemetry history

- alarm incidents and audit events

- tenant, site, and customer data

- command logs

- failed ingress messages

Azure SQL is chosen because:


- the platform is already Azure-native

- the current design uses T-SQL semantics

- the scale target is reasonable for Phase 1 and Phase 2

- operational and reporting workloads need relational querying

6.4 Azure Storage Account / ADLS Gen2


Blob or Data Lake storage is used for:


- Event Hubs checkpoint storage

- long-term telemetry archive

- raw payload archive

- troubleshooting artifacts and diagnostics

6.5 Azure Service Bus


Service Bus is used for explicit alarm workflows requiring fan-out.

Initial subscriptions:


- AlarmProcessing

- NotificationDispatch

- AuditLog

6.6 Key Vault


Used for:


- service credentials

- connection strings

- certificates

- encryption secrets

6.7 Application Insights / Azure Monitor / Log Analytics


Used for:


- distributed tracing

- metrics

- failures

- ingestion lag

- alarm throughput

- API latency

- operational dashboards and alerts


---

7. Multi-Tenant Model


The platform supports two tenant types.

7.1 Independent Homeowner Model

- the homeowner purchases the device and pays Sentinel directly

- Sentinel staff install and manage the device

- the homeowner has app access to their own telemetry, alarms, and subscription

Tenant boundary: Customer

7.2 Plumbing Company Model

- the plumbing company purchases devices in bulk and pays a commercial subscription

- company technicians install devices at customer homes

- the plumbing company owns the monitoring relationship

- homeowners in this model do not log in

Tenant boundary: Company

7.3 Tenant Resolution Rules

User Role	Tenant Type	Scope
InternalAdmin	None	Global
InternalTech	None	Global
CompanyAdmin	Company	All data for their company
CompanyTech	Company	All data for their company
HomeownerViewer	Customer	Own sites and devices only

7.4 Historical Ownership Rule


Historical telemetry and alarm data must not be resolved only through the current device assignment. That would leak old history after reassignment.

Therefore, every historical row is stamped at ingest time with the ownership snapshot active at that time:


- device_assignment_id

- site_id

- customer_id

- company_id

If a device is temporarily unassigned, these fields remain null and the data is visible only to internal staff until reassignment.

This rule is mandatory for correct tenant isolation and historical reporting.


---

8. Device Identity and Provisioning

8.1 Manufacturing


During manufacturing:


- a device record is created

- a serial number is generated in the format GP-YYYYMM-NNNNN

- a DPS registration identity is prepared

- a symmetric key is derived from the DPS enrollment group key using HMAC-SHA256

- credentials are flashed into the device

For version 1, the DPS registrationId and IoT Hub deviceId will match the manufacturing serial number to simplify support and provisioning.

8.2 First Boot


On first boot:


- the device connects to DPS using its flashed credentials

- DPS validates the registration

- if custom allocation is needed, DPS invokes the configured Azure Function allocation handler

- the handler verifies the device exists and is not decommissioned

- DPS assigns the target IoT Hub

- the device is provisioned and connects to IoT Hub

- the device transitions from Manufactured to Unprovisioned

8.3 Site Assignment


A technician assigns the device after installation.

Rules:


- only internal techs or company techs can assign devices

- homeowners cannot create sites or assign devices

- company techs are scoped to their own organization

- assignment creates a DeviceAssignment history record

- the device twin is updated with site-specific configuration, especially timezone

- the device transitions from Unprovisioned to Assigned

8.4 Active Operation


On first accepted telemetry after assignment:


- the device transitions from Assigned to Active

8.5 Decommissioning


When removed from service:


- the device transitions to Decommissioned

- DPS reprovisioning is rejected

- the IoT Hub identity may be disabled

- historical data is retained

- if the device remains physically installed but ownership lapses, it becomes a Lead instead of being deleted


---

9. Device Communication Model

9.1 Device-to-Cloud Protocol


Devices connect using:


- MQTT over TLS

- Azure IoT Hub

- JSON payloads

- application properties for message classification

9.2 Message Types


Supported device-to-cloud message types:


- telemetry

- alarm

- diagnostic

- lifecycle

messageType must be provided as an IoT Hub application property.

9.3 Device Twins


Desired properties are used for persistent device configuration:


- telemetry interval

- diagnostics enabled

- alarm thresholds

- rollout ring

- site timezone

Reported properties are used for current device metadata and health:


- firmware version

- hardware revision

- signal strength

- last boot reason

- last applied configuration version

Important: desired properties configure only the Sentinel device. They do not control the grinder pump.

9.4 Direct Methods


Direct methods are used only for immediate actions on the Sentinel device itself.

Supported methods:


- reboot

- ping

- captureSnapshot

- runSelfTest

- syncNow

- clearFault

All commands are executed asynchronously from the API through CommandLog.


---

10. Message Contract

10.1 Required Envelope Fields


Every device-to-cloud message must include:


Field	Source	Required	Notes
messageId	payload body	Yes	Unique per device; used for dedup
timestampUtc	payload body	Yes	Source event time
schemaVersion	app property or payload	Yes	Contract version
messageType	app property	Yes	telemetry, alarm, diagnostic, lifecycle

10.2 Recommended Fields

Field	Source	Purpose
bootId	payload body	identifies a boot cycle
sequenceNumber	payload body	ordering and gap detection
firmwareVersion	payload body or reported twin	processing capability selection

10.3 Trust Model


Authoritative source rules:


Data	Authoritative Source
device identity	IoT Hub system property connectionDeviceId
site / customer / company	backend database assignment chain
message type	IoT Hub application property
source event time	payload timestampUtc
ingest receive time	backend-generated received_at_utc
enqueue time	IoT Hub/Event Hubs system metadata
Payload deviceId, siteId, or other ownership hints may be logged for diagnostics but must never be used for authorization or attribution.

10.4 Example Telemetry Payload

	{
	  "messageId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
	  "timestampUtc": "2026-04-05T00:00:00Z",
	  "schemaVersion": 2,
	  "bootId": "boot-20260405-001",
	  "sequenceNumber": 1821,
	  "panelVoltage": 240.5,
	  "pumpCurrent": 8.3,
	  "pumpRunning": true,
	  "pumpStartEvent": true,
	  "runtimeSeconds": 46,
	  "reportedCycleCount": 1821,
	  "highWaterAlarm": false,
	  "temperatureC": 31.2,
	  "signalRssi": -73,
	  "firmwareVersion": "1.2.0"
	}

Example application properties:


	messageType=telemetry
	schemaVersion=2

Explicit alarm example:


	messageType=alarm
	alarmType=high_water
	severity=critical
	schemaVersion=2


---

11. Canonical Processing Rules

11.1 Deduplication Rule


The ingestion deduplication key is:


- (device_id, message_id)

If the key already exists, the message is treated as already processed and is safe to checkpoint.

11.2 Latest State Ordering Rule


LatestDeviceState is updated only when the incoming timestampUtc is strictly newer than the stored last_telemetry_timestamp_utc.

This prevents stale or replayed data from overwriting newer state.

11.3 Connectivity Rule


Connectivity is tracked separately from latest telemetry state.

Any successfully authenticated and processed message type updates DeviceConnectivityState.last_message_received_at, even if the message is older than the current latest telemetry snapshot.

This prevents clock skew or out-of-order events from causing false offline alarms.

11.4 Pump Cycle Rule


The platform stores two cycle count concepts:


- reported_cycle_count: the cumulative count reported by the device

- derived_cycle_count: the backend-maintained authoritative count

Canonical rule:


- when firmware supports pumpStartEvent, the backend increments derived_cycle_count only when pumpStartEvent = true

- the backend does not infer cycles from pumpRunning transitions when explicit pumpStartEvent is available

- device-reported reportedCycleCount is stored for diagnostics and reconciliation

- for legacy firmware without pumpStartEvent, the fallback strategy must be firmware-version-specific and explicitly configured

11.5 Alarm Source Rule


Alarm creation must have a single canonical source per alarm type.

Final rule:


- explicit messageType=alarm messages are the canonical source for device-originated alarms when supported by the firmware

- telemetry booleans such as highWaterAlarm update current state and may serve as fallback alarm sources only for firmware versions flagged as legacy

- DeviceOffline alarms are generated by the backend, not by device messages

- alarm processors must be firmware-aware to prevent double creation from both alarm messages and telemetry booleans

A firmware capability matrix is maintained in application configuration or metadata to define which versions support:


- explicit alarm messages

- pumpStartEvent

- telemetry-only fallback behavior


---

12. Routing Strategy

12.1 Telemetry, Lifecycle, Diagnostic Messages


These are routed from IoT Hub to the Event Hubs-compatible endpoint and processed by the ingestion worker.

12.2 Explicit Alarm Messages


Critical alarm messages are routed to a Service Bus topic.

Initial topic subscriptions:


- AlarmProcessing

- NotificationDispatch

- AuditLog

12.3 Raw Payload Archival


Raw payloads are archived to Blob/ADLS for long-term troubleshooting and ML use. Azure SQL stores normalized typed fields and a reference to the raw archive when needed.

This avoids unbounded large JSON storage in the hot relational database.


---

13. Backend Components

13.1 Ingestion Worker


Responsibilities:


- consume events from the IoT Hub Event Hubs-compatible endpoint

- validate message envelope and schema

- deserialize payloads

- resolve device identity from IoT Hub metadata

- resolve current assignment and ownership snapshot

- persist telemetry and lifecycle records

- update latest telemetry state

- update connectivity state

- archive raw payloads

- write failed messages to durable poison storage

- transition device state to Active on first accepted telemetry

Processing rules:


- checkpoint per partition every N messages or T seconds

- do not checkpoint until data is durable

- use exponential backoff for transient failures

- do not retry unrecoverable malformed messages forever

13.2 Alarm Processor


Responsibilities:


- consume explicit alarm messages from Service Bus

- create or update alarm incidents

- enforce alarm state transitions

- create AlarmEvents

- create notification incidents

- suppress duplicate active incidents for the same device and alarm type

13.3 Offline Monitor


A periodic background job evaluates connectivity state.

Rules:


- default offline threshold is 3x the expected telemetry interval

- threshold is configurable per device type and overrideable per device

- the job raises DeviceOffline alarms when exceeded

- alarms auto-resolve when telemetry resumes

- maintenance windows suppress offline alarm generation

13.4 Command Executor


A background service reads pending command requests from CommandLog, invokes IoT Hub direct methods, and updates command status.

API calls return 202 Accepted immediately.

13.5 ASP.NET Core API


Responsibilities:


- tenant-scoped queries

- device detail and telemetry access

- alarm and event access

- site/customer/company management

- assignment workflows

- desired property updates

- asynchronous command submission

- auth and authorization enforcement


---

14. Data Model

14.1 Devices


Application-level device inventory.

Key fields:


- id

- device_id unique

- serial_number unique

- hardware_revision

- firmware_version

- status (Manufactured, Unprovisioned, Assigned, Active, Decommissioned)

- provisioned_at

- created_at

- updated_at

- is_deleted

- deleted_at

14.2 DeviceAssignments


Historical assignment chain.

Key fields:


- id

- device_id

- site_id

- assigned_at

- assigned_by_user_id

- unassigned_at

- unassigned_by_user_id

- unassignment_reason

There can be only one active assignment per device at a time.

14.3 DeviceConnectivityState


Tracks connectivity independent of latest telemetry ordering.

Key fields:


- device_id unique

- last_message_received_at

- last_telemetry_received_at

- last_enqueued_at_utc

- last_message_type

- offline_threshold_seconds

- is_offline

- suppressed_by_maintenance_window

- updated_at

14.4 LatestDeviceState


Stores one latest telemetry snapshot per device.

Key fields:


- device_id unique

- last_telemetry_timestamp_utc

- last_message_id

- last_boot_id

- last_sequence_number

- panel_voltage

- pump_current

- pump_running

- high_water_alarm

- temperature_c

- signal_rssi

- runtime_seconds

- reported_cycle_count

- derived_cycle_count

- updated_at

Rule: this table is ordered only by last_telemetry_timestamp_utc.

14.5 TelemetryHistory


Append-only operational telemetry history.

Key fields:


- id bigint

- device_id

- message_id

- message_type

- timestamp_utc

- enqueued_at_utc

- received_at_utc

- device_assignment_id nullable

- site_id nullable

- customer_id nullable

- company_id nullable

- panel_voltage nullable

- pump_current nullable

- pump_running nullable

- high_water_alarm nullable

- temperature_c nullable

- signal_rssi nullable

- runtime_seconds nullable

- reported_cycle_count nullable

- derived_cycle_count nullable

- firmware_version nullable

- boot_id nullable

- sequence_number nullable

- raw_payload_blob_uri nullable

Indexes:


- unique (device_id, message_id)

- query index (device_id, timestamp_utc DESC)

- consider partitioning by time as volume grows

Retention:


- 90 days hot in Azure SQL

- indefinite cold archive in Blob/ADLS

14.6 Alarms


One record per incident.

Key fields:


- id

- device_id

- device_assignment_id nullable

- site_id nullable

- customer_id nullable

- company_id nullable

- alarm_type

- severity

- status (Active, Acknowledged, Suppressed, Resolved)

- source_type (ExplicitMessage, TelemetryFallback, SystemGenerated)

- trigger_message_id nullable

- started_at

- resolved_at nullable

- suppress_reason nullable

- suppressed_by_user_id nullable

- details_json nullable

- created_at

- updated_at

14.7 AlarmEvents


Audit trail for alarm transitions.

Key fields:


- id

- alarm_id

- event_type

- user_id nullable

- reason nullable

- metadata_json nullable

- created_at

14.8 FailedIngressMessages


Durable poison-message storage.

Key fields:


- id bigint

- source_device_id nullable

- message_id nullable

- partition_id

- offset

- enqueued_at

- failure_reason

- error_message

- raw_payload

- headers_json

- created_at

14.9 Tenant and User Tables


Core business tables:


- Companies

- Customers

- Sites

- ApplicationUser

- Subscriptions

- Leads

Important constraints:


- Subscriptions must reference exactly one owner: Company or Customer

- ApplicationUser role and FK relationships must be consistent

- soft-delete query filters apply to tenant-sensitive entities

14.10 MaintenanceWindows


Used to suppress offline alarms during planned downtime.

Key fields:


- id

- scope_type (Device, Site, Company)

- device_id nullable

- site_id nullable

- company_id nullable

- starts_at

- ends_at

- reason

- created_by_user_id

- created_at

14.11 CommandLog


Async command audit.

Key fields:


- id

- device_id

- command_type

- status (Pending, Sent, Succeeded, Failed, TimedOut)

- requested_by_user_id

- requested_at

- sent_at

- completed_at

- response_json

- error_message

- created_at

- updated_at

14.12 Notification Tables


Notification workflow tracking:


- NotificationIncidents

- NotificationAttempts

- EscalationEvents

These are separate from device commands.


---

15. Alarm Lifecycle


Alarm lifecycle rules:


1. 
Raised


	- a new incident is opened when a canonical alarm source indicates an active condition


2. 
Acknowledged


	- an operator acknowledges awareness

	- the alarm remains active


3. 
Suppressed


	- an operator manually clears the incident

	- a reason is required

	- while suppressed, the system does not open a new incident for the same still-active condition


4. 
Resolved


	- the physical condition has cleared

	- the system is re-armed for future incidents


State behavior:


	Active -> Acknowledged -> Resolved
	   |           |
	   |           +-> Suppressed -> Resolved
	   |
	   +-> Suppressed -> Resolved

Notes:


- a new incident is created only after the prior one is resolved

- DeviceOffline follows the same incident model

- notification retries and escalations are tracked separately from alarm status


---

16. Security and Authorization

16.1 Authentication


The platform uses ASP.NET Core Identity with JWT bearer tokens.

All users are stored as ApplicationUser.

16.2 Authorization


Authorization is policy-driven and tenant-aware.

Rules:


- internal roles can access all tenants

- company roles are scoped by CompanyId

- homeowner roles are scoped by CustomerId

- all tenant-sensitive endpoints must apply tenant scoping

- EF Core global query filters provide defense in depth

16.3 Command Permissions


Version 1 command policy:


- InternalAdmin, InternalTech: all commands

- CompanyAdmin, CompanyTech: allowed on owned devices

- HomeownerViewer: no remote commands in v1

This can be expanded later if customer-safe commands are identified.

16.4 Device Security

- per-device credentials

- TLS-only communication

- device disable and reprovision control

- secrets stored in Key Vault

- managed identity preferred for Azure service access


---

17. Reliability and Failure Handling

17.1 Ingestion Reliability


The ingestion worker must:


- batch checkpoint by partition

- persist before checkpoint

- retry transient failures with exponential backoff

- remain idempotent under replay

- tolerate duplicate delivery

- tolerate out-of-order telemetry

17.2 Poison Messages


Malformed or unrecoverable messages are written to FailedIngressMessages.

Examples:


- malformed JSON

- unknown schema version

- missing required envelope fields

- unknown device identity

- unrecoverable processing failure

These messages are checkpointed after durable recording.

17.3 First-Insert Concurrency


LatestDeviceState and DeviceConnectivityState must have unique constraints on device_id.

Use:


- update-first logic

- insert-if-missing

- retry update on duplicate-key insert race

Avoid MERGE.


---

18. Observability


The platform must emit logs, metrics, and traces for:

Device Fleet Health

- last seen

- device online/offline counts

- provisioning status

- twin update failures

- command success/failure rates

Ingestion Health

- messages processed per minute

- processing latency

- checkpoint lag

- replay count

- poison message rate

- duplicate message rate

Alarming Health

- alarm creation latency

- notification queue depth

- notification delivery outcomes

- escalation frequency

Application Health

- API latency

- authorization failures

- SQL performance

- worker restarts

- background job latency


---

19. Scale Targets


Initial design assumptions:


Metric	Target
Device fleet	5,000 devices
Telemetry interval	5 minutes
Messages per device/day	288
Total messages/day	1.44 million
Avg payload size	~500 bytes
Daily ingress volume	~720 MB
Hot SQL retention	90 days
Alarm creation latency	< 5 seconds
Latest-state API latency	< 500 ms
90-day history query latency	< 2 seconds
IoT Hub partitions	4 initially
If scale grows materially beyond this, the next evaluation points are:


- SQL partitioning strategy

- richer cold-storage query patterns

- time-series offload options

- X.509 device identity rollout


---

20. API Surface

20.1 Device Queries

- GET /api/devices

- GET /api/devices/{deviceId}

- GET /api/devices/{deviceId}/state

- GET /api/devices/{deviceId}/telemetry

- GET /api/devices/{deviceId}/alarms

20.2 Device Configuration

- PATCH /api/devices/{deviceId}/desired-properties

20.3 Async Device Commands

- POST /api/devices/{deviceId}/commands/reboot

- POST /api/devices/{deviceId}/commands/ping

- POST /api/devices/{deviceId}/commands/capture-snapshot

- POST /api/devices/{deviceId}/commands/run-self-test

- POST /api/devices/{deviceId}/commands/sync-now

- POST /api/devices/{deviceId}/commands/clear-fault

- GET /api/devices/{deviceId}/commands/{commandId}

20.4 Assignment and Provisioning

- POST /api/devices/{serialNumber}/assign

- POST /api/sites

- POST /api/manufacturing/batches

- DPS allocation is handled through DPS configuration and Azure Function integration, not through a public general-purpose app endpoint

20.5 Alarm Endpoints

- GET /api/alarms

- POST /api/alarms/{alarmId}/acknowledge

- POST /api/alarms/{alarmId}/clear

- GET /api/alarms/{alarmId}/events

20.6 Pagination Rules

- offset pagination for small entity grids: devices, sites, customers, companies, leads

- cursor pagination for large append-only streams: telemetry history, alarm events


---

21. Solution Structure

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

Responsibilities:

SentinelBackend.Api

- controllers/endpoints

- auth

- request validation

- orchestration

SentinelBackend.Ingestion

- Event Hubs consumers

- alarm handlers

- offline monitor

- command executor

- checkpointing and retries

SentinelBackend.Domain

- entities

- enums

- business rules

- Result<T>

SentinelBackend.Application

- use cases

- command/query handlers

- service interfaces

SentinelBackend.Infrastructure

- EF Core

- Azure SDK integrations

- repositories

- persistence

- identity implementation

SentinelBackend.Contracts

- DTOs

- API contracts

- message schemas


---

22. Final Architecture Decisions


The final Sentinel backend architecture is:


- Azure DPS for provisioning

- Azure IoT Hub for device identity and communication

- MQTT over TLS from the device

- IoT Hub routing to:
	- Event Hubs-compatible endpoint for telemetry/lifecycle/diagnostics

	- Service Bus topic for explicit alarms

	- Blob/ADLS archive for raw storage as needed


- .NET ingestion workers for async processing

- Azure SQL as the operational system of record

- Blob/ADLS for indefinite cold archive

- ASP.NET Core API for all application access

- EF Core global query filters plus policy-based authorization for tenant isolation

- device twins for persistent device configuration

- async direct methods with CommandLog

- alarm incidents separated from alarm audit events

- ownership snapshot stamping on historical records

- separate telemetry state and connectivity state

- fully typed telemetry history in hot storage with cold archival for ML retention

This architecture provides:


- secure device communication

- operational scalability

- strong tenant isolation

- clear auditability

- reliable alarming

- clean separation of concerns

- an implementation path that does not require architectural redesign as the fleet grows


---

23. Summary


Sentinel is a read-only monitoring platform for grinder pump installations. Its backend must support secure device connectivity, reliable telemetry ingestion, alarming, historical retention, and strict multi-tenant access control.

This final architecture uses Azure IoT Hub and DPS for device-facing concerns, .NET worker services for asynchronous ingestion and alarms, Azure SQL for operational application data, Blob/ADLS for long-term archive, and ASP.NET Core for tenant-scoped APIs.

The critical implementation decisions in this final design are:


- do not trust device-supplied ownership fields

- do not query IoT Hub for application reads

- stamp historical rows with ownership at ingest time

- separate connectivity from latest telemetry state

- enforce canonical event-source rules for alarms and cycle counting

- treat the Sentinel device as read-only with respect to pump hardware

This gives Sentinel a scalable, secure, and maintainable backend foundation for both homeowner and plumbing-company deployments.