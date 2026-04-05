Azure IoT Backend Design

Overview


This document describes the backend architecture for a scalable IoT platform built for grinder pump control panel devices. These devices collect operational telemetry and alarms and send data to a cloud backend for processing, storage, monitoring, and eventual presentation in a React-based UI.

The backend will be built with .NET and Azure services, with Azure IoT Hub as the primary device communication layer.


---

Goals

- Support secure communication with field devices

- Scale from a small deployment to a large device fleet

- Separate device communication from business APIs

- Provide reliable telemetry ingestion and alarm processing

- Enable remote device management and configuration

- Prepare for a future React frontend without redesigning the backend


---

Non-Goals

- Building a custom MQTT broker or device gateway

- Letting devices communicate directly with the web API

- Using the UI as the primary source of telemetry consumption

- Treating Azure IoT Hub as the main application database


---

High-Level Architecture

	+------------------------+
	| Grinder Pump Devices   |
	| - telemetry            |
	| - alarms               |
	| - diagnostics          |
	+-----------+------------+
	            |
	            v
	+------------------------+
	| Azure DPS              |
	| Device provisioning    |
	+-----------+------------+
	            |
	            v
	+------------------------+
	| Azure IoT Hub          |
	| - device identity      |
	| - secure connectivity  |
	| - twins                |
	| - direct methods       |
	| - message routing      |
	+-----+-------------+----+
	      |             |
	      |             |
	      v             v
	+-----------+   +----------------+
	| Event Hub |   | Service Bus    |
	| endpoint  |   | alarm queue    |
	+-----+-----+   +--------+-------+
	      |                   |
	      v                   v
	+-------------------------------+
	| .NET Ingestion Worker         |
	| - telemetry processing        |
	| - alarm handling              |
	| - enrichment                  |
	| - persistence                 |
	+---------------+---------------+
	                |
	                v
	+-------------------------------+
	| Application Database          |
	| - devices                     |
	| - latest state                |
	| - telemetry history           |
	| - alarms/events               |
	| - tenant/site metadata        |
	+---------------+---------------+
	                |
	                v
	+-------------------------------+
	| ASP.NET Core API              |
	| - device management           |
	| - data access for UI          |
	| - twin updates                |
	| - direct methods              |
	+---------------+---------------+
	                |
	                v
	+-------------------------------+
	| React Frontend                |
	| - dashboards                  |
	| - alarms                      |
	| - device management           |
	+-------------------------------+


---

Why Azure IoT Hub


Azure IoT Hub provides the device-facing infrastructure so the backend does not need to solve those problems manually.

Key benefits

1. Per-device identity and security


Each device has its own identity and credentials. If a device is compromised, it can be disabled independently without affecting the fleet.

2. Scalable ingestion


IoT Hub is built to accept large amounts of device-to-cloud traffic. It provides a path to scale as the number of devices or message frequency grows.

3. Standard IoT protocols


Devices can connect using MQTT, AMQP, or HTTPS. For this project, MQTT over TLS is the recommended choice.

4. Device provisioning


Azure Device Provisioning Service (DPS) allows devices to be registered and assigned to the proper IoT Hub automatically.

5. Device twins


Twins provide a cloud-managed configuration and state model:


- desired properties = configuration the backend wants the device to apply

- reported properties = state the device reports back

6. Direct methods


The backend can invoke device commands such as:


- reboot

- force sync

- test output

- clear fault

- request immediate telemetry sample

7. Message routing


Different message types can be routed differently:


- telemetry to ingestion pipeline

- critical alarms to queue-based processing

- diagnostic or raw payloads to storage

8. Managed infrastructure


IoT Hub removes the need to build and operate:


- broker connectivity

- per-device authentication

- secure command channels

- device registry

- connection lifecycle management

- large-scale message ingress


---

Core Azure Services

Azure IoT Hub


Used as the central device communication platform.

Responsibilities:


- secure device connections

- device identity registry

- cloud-to-device operations

- device twins

- direct methods

- message routing

Azure Device Provisioning Service (DPS)


Used to provision devices at scale.

Responsibilities:


- zero-touch or low-touch registration

- assigning devices to hubs

- supporting future multi-hub growth

Azure Storage Account


Used for:


- Event Processor checkpointing

- optional raw message archival

- possible long-term file storage

Azure Service Bus


Used for:


- critical alarm workflows

- decoupled event handling

- integration with downstream business processing

Application Database

Core responsibilities:

- Store latest device state (LatestDeviceState table for UI dashboards)

- Store historical telemetry with 90-day hot retention (TelemetryHistory)

- Store alarms and events with lifecycle tracking

- Store metadata (tenant/customer/site/device inventory)

Database commitment:

Primary operational database for Phase 1–2: Azure SQL.

Azure SQL provides native support for EF Core migrations, high availability, automated backups, and strong Azure integration. PostgreSQL + TimescaleDB evaluation is deferred to Phase 4 if time-series scaling becomes a scaling bottleneck.

Key tables (with soft deletes):

- Devices, Sites, Customers, Companies, Subscriptions all support soft deletes (deleted_at nullable column).
- TelemetryHistory (required composite index on device_id, timestamp_utc).
- LatestDeviceState (updated with upsert on newer-timestamp-wins rule).
- Alarms (includes acknowledged_at, silenced_until fields for multi-path clearing).

ASP.NET Core API


Used as the backend API for:


- future React frontend

- operator actions

- device configuration

- querying stored data

.NET Worker Service


Used for:


- telemetry ingestion

- parsing and validating messages

- persistence

- business event generation

Key Vault


Used for:


- connection strings

- certificates

- secrets

- credentials for backend services

Application Insights / Log Analytics


Used for:


- application monitoring

- telemetry

- distributed tracing

- diagnostics and alerting


---

Recommended Communication Pattern

Device-to-cloud


Devices should send telemetry and alarms to Azure IoT Hub, not directly to the ASP.NET Core API.

Reason:


- better scale

- better reliability

- stronger device identity model

- protocol support

- easier routing and buffering

Cloud-side processing


The backend should process telemetry asynchronously using a worker service that consumes data from the IoT Hub Event Hubs-compatible endpoint.

Reason:


- decouples ingestion from API traffic

- allows independent scaling

- supports checkpointing and parallel processing

UI access


The React frontend should call the ASP.NET Core API, which reads from the application database.

Reason:


- UI queries should use processed and indexed data

- avoids querying IoT Hub directly for app scenarios

- improves performance and stability


---

Device Design Expectations


Each field device should:


- provision through DPS

- connect to IoT Hub using MQTT over TLS

- send compact JSON messages

- send alarms as separate messages when possible

- maintain reported properties for current state

- receive desired property updates for configuration

- support a small set of direct methods for remote action


---

Suggested Message Types

Telemetry


Regular operational data.

Examples:


- panel voltage

- pump current

- cycle count

- pump runtime

- float states

- temperature

- battery level

- cellular signal strength

- firmware version

Alarm


Immediate fault or threshold event.

Examples:


- high water alarm

- pump overload

- panel power loss

- sensor failure

- communication fault

- enclosure intrusion

Diagnostic


Low-priority troubleshooting data.

Examples:


- startup logs

- internal watchdog events

- raw sensor health

- debug counters

Lifecycle


Device status transitions.

Examples:


- device booted

- firmware updated

- connected

- configuration applied

- reboot reason


---

Example Telemetry Payload

	{
	  "deviceId": "pump-00123",
	  "timestampUtc": "2026-04-05T00:00:00Z",
	  "panelVoltage": 240.5,
	  "pumpCurrent": 8.3,
	  "pumpRunning": true,
	  "pump_start_event": false,
	  "runtimeSeconds": 46,
	  "highWaterAlarm": false,
	  "temperatureC": 31.2,
	  "signalRssi": -73,
	  "firmwareVersion": "1.2.0"
	}

Note: cycleCount field has been removed from the schema. Cycle counting is backend-authoritative and derived from pump_start_event signals, not from device-reported values. This prevents edge-case handling for device counter resets or firmware updates and keeps the backend as the single source of truth for device life cycle events.

Suggested application properties

	messageType=telemetry
	schemaVersion=1
	siteId=site-1001

For alarms:


	messageType=alarm
	severity=critical
	alarmType=high_water
	schemaVersion=1

These properties can be used by IoT Hub routing rules.


---

Device Twins

Desired properties


Used by the backend to publish configuration for devices.

Examples:


- telemetry interval

- alarm thresholds

- firmware rollout ring

- sampling frequency

- site or timezone settings

- enabled diagnostics flags

Example:


	{
	  "telemetryIntervalSeconds": 300,
	  "highWaterThreshold": 1,
	  "diagnosticsEnabled": false
	}

Reported properties


Used by the device to report current state.

Examples:


- firmware version

- serial number

- hardware revision

- last reboot time

- last fault code

- network signal

- boot reason

- last configuration version applied

Example:


	{
	  "firmwareVersion": "1.2.0",
	  "serialNumber": "SN-ABC-12345",
	  "hardwareRevision": "rev-b",
	  "lastBootReason": "watchdog",
	  "signalRssi": -73
	}

Twin usage guidance

- use desired properties for persistent configuration

- use reported properties for device state

- do not overuse direct methods for configuration that should persist


---

Direct Methods


Direct methods should be used for immediate actions.

Suggested methods:


- reboot

- ping

- captureSnapshot

- clearFault

- runSelfTest

- syncNow

Guidance:


- direct methods should be short-running

- they should return a simple status payload

- long-running tasks should acknowledge receipt and report progress later through telemetry or reported properties


---

Routing Strategy

Route 1: telemetry


Route all normal telemetry to the built-in Event Hubs-compatible endpoint for ingestion by the worker service.

Route 2: alarms


Route critical alarm messages to Azure Service Bus for immediate processing and notifications.

Route 3: diagnostics


Optionally route diagnostics or raw payloads to Blob Storage for support and auditing.

Important note


If custom routes are created, routing behavior must be configured carefully so telemetry continues to reach the expected endpoint. Routing should be designed intentionally from the beginning.


---

Backend Responsibilities

1. Ingestion Worker

Responsibilities

- consume telemetry from IoT Hub Event Hubs-compatible endpoint

- deserialize messages

- validate schema and required fields

- enrich data with tenant/site/device metadata

- write normalized data to the database

- update latest state records

- create alarms/events

- publish internal business events if needed

Recommended SDKs

- Azure.Messaging.EventHubs

- Azure.Messaging.EventHubs.Processor

Processing requirements

- checkpoint after successful processing

- support retries and poison message handling

- maintain idempotency where possible

- log validation failures separately


---

2. ASP.NET Core API

Responsibilities

- serve device and telemetry data to the frontend

- manage device configuration

- expose alarm and history endpoints

- update twin desired properties

- invoke direct methods

- manage provisioning and device metadata workflows

Recommended SDKs

- Microsoft.Azure.Devices

- Microsoft.Azure.Devices.Provisioning.Service

Important rule


The API should read operational app data from the database, not directly from IoT Hub for every request.


---

Data Model


The application database should act as the queryable operational store.

Suggested tables

Devices


Stores application-level device metadata.

Fields:


- id

- device_id

- serial_number

- customer_id

- site_id

- hardware_revision (manufacturing record is authoritative; device twin reported property should match)

- firmware_version

- iot_hub_hostname (NEW: tracks which IoT Hub device is registered to, for migration readiness)

- installed_at

- status

- deleted_at (NEW: soft delete timestamp, nullable)

- created_at

- updated_at

LatestDeviceState


Stores the most recent known state for each device.

Fields:


- device_id

- last_seen_at

- panel_voltage

- pump_current

- high_water_alarm

- temperature_c

- signal_rssi

- runtime_seconds

- firmware_version (NEW: enables fast current-version lookup without querying history)

- last_fault_code

- updated_at

TelemetryHistory


Stores time-series or historical telemetry.

Fields:


- id

- device_id

- timestamp_utc

- message_type

- payload_json

- normalized_fields

- received_at

Alarms


Stores alarm and event history.

Fields:


- id

- device_id

- alarm_type (FK → AlarmTypes; constrained choices: high_water, pump_overload, sensor_failure, panel_power_loss, communication_fault, enclosure_intrusion)

- severity (computed: high_water always critical; other types default to warning/error, configurable per tenant in future phases)

- started_at

- cleared_at (nullable; null while active)

- is_active (boolean; synced with cleared_at)

- acknowledged_at (NEW: timestamp when operator or user acknowledged the alarm)

- acknowledged_by_user_id (NEW: FK → ApplicationUser)

- silenced_at (NEW: timestamp when user suppressed notifications)

- silenced_by_user_id (NEW: FK → ApplicationUser)

- silenced_until (NEW: datetime; silence persists until alarm clears or this time is reached)

- details_json

- deleted_at (NEW: soft delete, nullable)

Sites


Stores physical location metadata.

Fields:


- id

- customer_id

- name

- address

- timezone

- latitude

- longitude

- deleted_at (NEW: soft delete, nullable)

Customers


Stores tenant/customer information.

Fields:


- id

- name

- contact_info

- deleted_at (NEW: soft delete, nullable; when set, devices become orphaned, Lead is created)

- created_at


AlarmTypes (NEW lookup table)


Constrains alarm types and provides defaults.

Fields:


- id (PK)

- name (high_water, pump_overload, sensor_failure, panel_power_loss, communication_fault, enclosure_intrusion)

- default_severity (critical, error, warning)

- created_at

Usage: Alarms.alarm_type is a FK to this table, ensuring type safety and enabling indexed filtering.


CommandLog (NEW audit table)


Persists all remote commands for audit and operational tracking.

Fields:


- id (PK)

- initiated_by_user_id (FK → ApplicationUser; who triggered the command)

- target_device_id (FK → Device; which device)

- command_type (string: reboot, ping, clear_fault, run_self_test, sync_now, etc.)

- sent_at (datetime UTC; when command was dispatched)

- status (pending, success, failed)

- result_details (JSON, optional; error messages or return payload)

- completed_at (datetime, nullable)

- created_at

Usage: Supplements Application Insights for queryable command history via API without accessing log infrastructure.


---

Suggested .NET Solution Structure

	src/
	  PumpBackend.Api/
	  PumpBackend.Ingestion/
	  PumpBackend.Domain/
	  PumpBackend.Application/
	  PumpBackend.Infrastructure/
	  PumpBackend.Contracts/
	tests/
	  PumpBackend.Api.Tests/
	  PumpBackend.Ingestion.Tests/
	docs/
	  iot-backend-design.md

Project responsibilities

PumpBackend.Api

- controllers/endpoints

- auth

- request validation

- orchestration of application services

PumpBackend.Ingestion

- background hosted services

- Event Hubs processing

- message handlers

- checkpointing and retry logic

PumpBackend.Domain

- entities

- enums

- business rules

- domain events

PumpBackend.Application

- use cases

- command/query handlers

- service interfaces

PumpBackend.Infrastructure

- EF Core

- Azure SDK integrations

- repositories

- background infrastructure services

PumpBackend.Contracts

- DTOs

- API contracts

- shared message schemas


---

Recommended SDK Usage

Device management and cloud operations


Use Microsoft.Azure.Devices.

Use cases:


- create and manage device identities

- read and update device twins

- invoke direct methods

- run bulk jobs if needed

Key classes:


- RegistryManager

- ServiceClient

Telemetry ingestion


Use Event Hubs SDKs.

Use cases:


- read telemetry from IoT Hub endpoint

- process in parallel

- checkpoint with Blob Storage

Key packages:


- Azure.Messaging.EventHubs

- Azure.Messaging.EventHubs.Processor

Provisioning


Use DPS service SDK.

Use cases:


- manage enrollments

- enrollment groups

- registration workflows

Key package:


- Microsoft.Azure.Devices.Provisioning.Service


---

Telemetry History Storage & Scaling

Hot storage retention:

All telemetry is stored in TelemetryHistory table in Azure SQL for 90 days (hot storage). After 90 days, data should be archived to Blob Storage or Data Lake for long-term access and ML training.

Indexing strategy (REQUIRED for performance):

Add a composite index on TelemetryHistory:

	CREATE INDEX idx_telemetry_device_timestamp
	ON TelemetryHistory (device_id, timestamp_utc DESC)
	INCLUDE (payload_json, message_type);

This index enables efficient queries like "telemetry for device X in last 30 days" and supports quick pagination.

Deduplication strategy:

IoT Hub guarantees at-least-once delivery, so duplicate messages arrive. To handle duplicates:

Strategy: Unique key on (device_id, timestamp_utc, message_id) with upsert semantics.

Implementation:

	-- If duplicate arrives with same key, update the row (last-write-wins for message_id + timestamp combos)
	INSERT INTO TelemetryHistory (device_id, timestamp_utc, message_id, payload_json, received_at)
	VALUES (@device_id, @timestamp_utc, @message_id, @payload_json, @received_at)
	ON DUPLICATE KEY UPDATE
	  received_at = NOW(),
	  payload_json = @payload_json;

Prevents duplicate rows in history and keeps accurate received_at timestamps.

Future archival plan (Phase 4):

Define and implement a tiered storage strategy: hot (SQL, 90 days) → warm (Blob Archive tier) → cold (Data Lake, indefinitely). This plan ensures ML training data is retained without unbounded growth in the operational query database.

PostgreSQL + TimescaleDB evaluation:

TimescaleDB provides time-series optimizations and automatic data retention policies. Defer evaluation to Phase 4 if SQL scaling becomes a bottleneck.

---

Ingestion Worker Reliability & Concurrency

Deduplication at LatestDeviceState:

LatestDeviceState upsert must be safe for concurrent writes from multiple worker instances processing the same device. Use timestamp-based upsert:

	UPDATE LatestDeviceState SET
	  panel_voltage = @panel_voltage,
	  pump_current = @pump_current,
	  firmware_version = @firmware_version,
	  updated_at = NOW()
	WHERE device_id = @device_id
	  AND @timestamp_utc > updated_at;

This ensures only newer messages overwrite stale data, preventing race conditions.

Idempotency & checkpointing:

- Every telemetry message processed should trigger a checkpoint in Event Hubs after successful persistence.
- If a message fails (schema mismatch, parse error), log to dead-letter and checkpoint anyway to avoid replay loops.
- The combination of unique key dedup (TelemetryHistory) and checkpoint ensures the system can tolerate retries and pod restarts.

Validation & error handling:

- Validate schemaVersion in every message; reject unknown schemas.
- Parse failures → dead-letter queue.
- Database failures → exponential backoff, retry up to N times, then dead-letter.
- Poison messages (malformed JSON, missing device_id) → dead-letter with diagnostic log.

Cycle counting (backend authoritative):

- Cycle count is NOT derived from device-reported cycleCount (removed from schema).
- Backend counts from pump_start_event signals in incoming telemetry.
- When pump_running transitions from false → true, increment device cycle counter in application state.
- Anomaly: if pump_running immediately transitions back to false (fluttering), track as diagnostic but do not double-increment cycle count.
- Store derived cycle count in a DeviceCycleCounter table or LatestDeviceState.lifecycle_cycle_count for UI display.

---

1. Start with DPS


Even for a small fleet, DPS should be used from day one to avoid redesign later.

2. Separate API scale from ingestion scale


API traffic and telemetry traffic have different load patterns and should scale independently.

3. Use long-lived SDK clients


Azure SDK clients should generally be registered once and reused through dependency injection.

4. Process messages asynchronously


Do not perform all business processing inline with device reception in an HTTP request path.

5. Plan for partitioning


IoT Hub partitioning affects parallelism. Estimate based on:


- projected device count

- telemetry interval

- expected message bursts

- number of worker instances

6. Design for message rate, not only number of devices


Five thousand devices sending every five minutes is very different from five thousand devices sending every ten seconds.

7. Expect retries and duplicate delivery


The system should tolerate:


- transient failures

- duplicate messages

- out-of-order delivery in some cases

8. Keep IoT Hub out of query workloads


IoT Hub is not the read database for dashboards and frontend queries.


---

Reliability Considerations

- use checkpointing in the ingestion worker

- implement exponential backoff for transient faults

- use dead-letter or failed-message handling patterns

- validate schema version in every incoming message

- keep device payloads compact

- maintain message id or correlation id when possible

- design consumers to be idempotent


---

Security Considerations

Device security

- use per-device credentials

- prefer DPS-based provisioning

- use TLS for all device communication

- support device disable and rotation workflows

Backend security

- store secrets in Key Vault

- use managed identities where possible

- lock down service-to-service access

- log administrative actions

API security

- require authenticated access

- separate operator/admin permissions

- validate all twin and command operations

- audit remote actions such as reboot or clear fault


---

Observability


The platform should produce logs and metrics for:

Device connectivity

- connection/disconnection events

- last seen timestamps

- registration/provisioning status

Ingestion health

- messages processed per minute

- processing latency

- failed deserializations

- checkpoint lag

- queue depth for alarms

Application health

- API response times

- exception rates

- database performance

- background worker restarts

Recommended tooling

- Application Insights

- Azure Monitor

- Log Analytics

- custom dashboards and alerts


---

Operational Patterns

Latest state pattern


Maintain a latest-state table for quick UI queries.

Historical archive pattern


Store historical telemetry separately from latest-state records.

Alarm lifecycle pattern


Track alarm activation and clearing as explicit events.

Command audit pattern


Every remote command should be logged with:


- who initiated it

- when it was sent

- target device

- result


---

Example API Surface (v1)

All endpoints are versioned with `/api/v1/` prefix from the first deployment to support future clients.

Device endpoints

- GET /api/v1/devices

- GET /api/v1/devices/{deviceId}

- GET /api/v1/devices/{deviceId}/state

- GET /api/v1/devices/{deviceId}/telemetry

- GET /api/v1/devices/{deviceId}/alarms

Alarm endpoints

- POST /api/v1/alarms/{alarmId}/acknowledge

- POST /api/v1/alarms/{alarmId}/silence

- POST /api/v1/alarms/{alarmId}/clear

Configuration endpoints

- PATCH /api/v1/devices/{deviceId}/desired-properties

Command endpoints (all require JWT token)

- POST /api/v1/devices/{deviceId}/commands/reboot

- POST /api/v1/devices/{deviceId}/commands/ping

- POST /api/v1/devices/{deviceId}/commands/clear-fault

Administrative endpoints (role-based access)

- POST /api/v1/devices/register

- POST /api/v1/devices/provisioning/enrollment

- GET /api/v1/sites

- GET /api/v1/customers


---

Alarm Detection & Routing (Primary Model: Option A)

Devices send dedicated alarm messages for immediate routing and processing.

Device behavior:

When a device detects a fault condition (for example, high water level), it sends a separate alarm message with IoT Hub application properties:

	messageType=alarm
	alarmType=high_water
	severity=critical
	schemaVersion=1

The alarm message body contains minimal details:

	{
	  "deviceId": "pump-00123",
	  "alarmType": "high_water",
	  "timestampUtc": "2026-04-05T12:34:56Z",
	  "details": { "waterLevelCm": 47 }
	}

Regular telemetry messages continue to include state fields (highWaterAlarm: true/false) for ongoing state tracking and dashboards, even if a dedicated alarm was already sent.

IoT Hub routing:

IoT Hub message routing rules forward alarm messages (messageType=alarm) to Azure Service Bus for immediate, decoupled processing.

Routing rule example:

	Name: AlarmRoute
	Source: Device Messages
	Condition: messageType = 'alarm'
	Endpoint: Service Bus Queue (alarms)
	Enabled: true

Backend processing:

The Alarm Processor consumes from Service Bus, deserializes the alarm, validates against AlarmTypes lookup table, and creates or updates the Alarms table entry with started_at, severity, and is_active=true.


Alarm Lifecycle: Multi-Path Clearing

Alarms can be cleared through three independent mechanisms:

1. Auto-clear from device signal:

When the device detects the fault has resolved (water level drops), it sends highWaterAlarm: false in regular telemetry. The ingestion worker detects this state change and updates Alarms.is_active = false, cleared_at = now.

2. Manual operator clear:

An operator with appropriate role can call POST /api/v1/alarms/{alarmId}/clear to explicitly close the alarm regardless of device state. Useful for noise or false positives.

3. Acknowledge (separate operation):

An operator calls POST /api/v1/alarms/{alarmId}/acknowledge to mark acknowledged_at and acknowledged_by_user_id but does NOT close the alarm. Acknowledge is distinct from clear and primarily suppresses notifications.

Silence behavior:

- Silencing an alarm (POST /api/v1/alarms/{alarmId}/silence) suppresses all notifications until the alarm clears or the silence is manually lifted.

- The alarm state (is_active) reflects device reality and is not affected by silence.

- Silence persists across device state changes; clearing the alarm also clears the silence.


Incident identity for notification deduplication:

A single "incident" is opened when alarm transitions from inactive to active (started_at). That incident remains open until the alarm is cleared (cleared_at is set). One open incident per device + alarm_type at any given time.

Notification resend deduplication uses the key: device_id + alarm_type + incident_id + recipient + channel. This prevents duplicate 30-minute resend notifications for the same alarm across different users or channels.


---

Notification Delivery Architecture

Channels in Phase 3 (v1):

- SMS (Twilio)

- Push notifications (provider-agnostic, selected during ops setup)

- Email (provider-agnostic, selected during ops setup)

Escalation Policy:

Company accounts:

- Notify the company technical contact first.
- If no acknowledgment within 24 hours, escalate to internal Sentinel ops team for follow-up call or on-site visit to the company or homeowner.

Homeowner accounts:

- Notify homeowner directly.
- If unacknowledged after 24 hours, escalate to internal Sentinel ops team.

Notification cadence:

- Resend every 30 minutes while alarm is active and not silenced.
- Deduplication key: device_id + alarm_type + incident_id + recipient + channel.
- No duplicates if the same user already received the notification in the current resend window.

Notification preferences:

- Scope: Global per channel (SMS on/off, push on/off, email on/off), not per alarm type.
- Applied tenant-wide to all alarms for that customer.
- Users cannot opt out of specific alarm types; only entire channels.

Escalation ticket:

When 24-hour escalation is triggered, a CommandLog entry is created with command_type=escalation_triggered, initiated_by=system, and details capturing the alert that triggered it.


---

Subscription Lifecycle & Device Gating

Subscription cancellation flow:

Day 0 — Subscription expires or is cancelled:

- Ingestion continues: backend keeps processing and storing telemetry from devices.
- API/UI access remains functional.

Day 1–7 — Restricted access window (grace period start):

- Tenant loses all API and UI read access; the tenant cannot query their devices or historical data.
- Ingestion continues; data is stored.
- Only internal Sentinel techs can query the data via internal tools.

Day 7 — Lead creation:

- A Lead record is created for the lapsed customer with status=lead.
- Devices and Sites owned by that customer are soft-deleted (deleted_at is set) but retained.
- Devices transition to "orphaned" state (no active device assignment).

Day 7–37 — Grace period (Lead recovery window):

- Lead remains in grace state.
- Customer can be reactivated and resubscribe; devices are re-linked and restored to Active status.
- Internal ops can query and manage devices on behalf of former customer.
- Device data ingestion continues.

Day 37+ — Lead becomes sale-ready:

- Lead transitions to ready_to_sell state (eligible for sales outreach).
- Devices remain retained and queryable by internal ops.
- Future reactivation requires sales/ops approval.

IoT Hub device identity:

- Device remains registered in IoT Hub throughout the cancellation and grace periods.
- No immediate disable; device authentication remains valid to support graceful offboarding.
- Ops can manually disable a device identity per-device if needed during grace period.

Important: Ingestion continues because historical telemetry is valuable for ML training and diagnostics. Access is restricted, not data collection.


---

Device Lifecycle & Reassignment

Reassignment workflow (atomic operation):

When a device is physically moved to a new site or reassigned to a new customer:

1. In a single database transaction:
   - Close the current DeviceAssignment: unassigned_at = now, unassigned_by_user_id = current_user
   - Open the new DeviceAssignment: assigned_at = now, assigned_by_user_id = current_user, to new site

2. Device.status remains Active throughout (no state transition through Assigned or Unprovisioned).

3. DeviceAssignments history table captures both the close and open for audit.

Why atomic:

- DeviceAssignments is the detailed source of truth; status transitions are unnecessary.
- Reflects operational reality: pump doesn't become unassigned between sites.
- Ingestion worker processes telemetry independently; no need to interpret intermediate states.
- Query `SELECT * FROM DeviceAssignments WHERE device_id = X AND unassigned_at IS NULL` always gives current assignment.

Soft-delete behavior for customers:

When a Customer is soft-deleted (deleted_at is set):

- All Devices and Sites owned by that customer are NOT hard-deleted.
- Instead, devices become orphaned (no active DeviceAssignment).
- A Lead is created for the customer (as described in Subscription Lifecycle section).
- Ops workflow: none are automatically closed; ops team manually determines whether to keep, reactivate, or dispose of each device per business rules (logged in CommandLog for audit).


---

Suggested Initial Implementation Plan

Phase 1: Foundation

- create Azure resources

- create .NET solution structure

- connect ingestion worker to IoT Hub endpoint

- persist telemetry to database

- expose basic read API

Phase 2: Device management and authentication

- add DPS support

- add device registration workflows

- implement twins

- add direct method endpoints

- require JWT authentication on all endpoints

- implement role-based authorization (Operator, Technician, Viewer)

Phase 3: Alarms and notification delivery

- route device alarm messages to Service Bus

- create AlarmTypes lookup table and enforce type safety

- create alarm processor

- implement SMS, push, and email notification channels (Twilio, provider-agnostic push, provider-agnostic email)

- implement 24-hour escalation policy (company tech or homeowner first, then Sentinel ops team)

- implement 30-minute resend cadence with deduplication per device+type+recipient+channel

- implement silence/acknowledge semantics (suppress notifications, preserve state)

Phase 4: Frontend support

- optimize read models

- add filtering, paging, and summaries

- support React dashboard needs

Phase 5: Production hardening

- monitoring

- alerting

- role-based access

- backup/restore planning

- load testing

- rollout and support tools


---

Risks and Design Notes

Risk: Using IoT Hub as the app database


Mitigation: persist all app-facing data in a dedicated database.

Risk: Tight coupling of ingestion and API


Mitigation: isolate telemetry processing in a worker service.

Risk: Hardcoded provisioning approach


Mitigation: adopt DPS from the start.

Risk: Unbounded telemetry storage growth


Mitigation: define retention policy and storage strategy early.

Risk: Overuse of direct methods


Mitigation: use desired properties for persistent configuration.


---

Recommended Final Architecture


For this product, the recommended architecture is:


- devices connect to Azure IoT Hub via DPS

- devices send telemetry and alarms to IoT Hub

- a .NET worker consumes telemetry from the Event Hubs-compatible endpoint

- alarms are routed to Service Bus via IoT Hub message routing (messageType=alarm application properties)

- processed data is stored in Azure SQL (primary choice for Phase 1–2)

- an ASP.NET Core API exposes device, alarm, and telemetry data

- the future React app talks only to the API

- device configuration is managed through twins

- remote actions are handled through direct methods

This architecture gives a clean separation of concerns and a good path to scale without redesigning the system later.


---

Summary



- an ingestion service for telemetry processing
- an API service for business logic and UI support
This provides:





- a better long-term path as the number of devices grows

For this product, the recommended architecture is:

- Devices connect to Azure IoT Hub via DPS for secure, scalable provisioning and device identity.

- Devices send telemetry messages (every N minutes) and dedicated alarm messages (on-demand) to IoT Hub using MQTT over TLS.

- IoT Hub routes alarm messages (messageType=alarm) to Azure Service Bus via message routing rules for immediate processing; telemetry routes to the built-in Event Hubs endpoint for ingestion worker consumption.

- A .NET ingestion worker consumes telemetry from the Event Hubs endpoint, deserializes, validates (schema version check, required fields), deduplicates (unique key on device_id + timestamp_utc + message_id), and persists to Azure SQL (LatestDeviceState and TelemetryHistory).

- An Alarm Processor consumes from Service Bus, validates alarm type against AlarmTypes lookup, creates or updates Alarms table, and triggers notification workflows.

- Notification engine implements multi-channel delivery (SMS via Twilio, push and email provider-agnostic), 24-hour escalation policy, and 30-minute resend deduplication.

- Processed data is stored in Azure SQL as the operational queryable database: Devices, LatestDeviceState, TelemetryHistory, Alarms, Sites, Customers, Commands, Leads.

- An ASP.NET Core API exposes device, alarm, and telemetry data to the React frontend and internal dashboards, with JWT authentication and role-based authorization from Phase 2 onward.

- Device configuration is managed through IoT Hub device twins (desired properties for backend-to-device config; reported properties for device state).

- Remote operations (reboot, ping, clear fault) are handled through IoT Hub direct methods with audit logging in CommandLog.

This architecture provides:

- Clean separation of concerns (device gateway, ingestion, notification, business API).

- Independent scaling (ingestion workers, API instances, notification processors scale independently).

- Strong device security and identity model (DPS, per-device credentials, TLS).

- Reliable event processing (checkpointing, deduplication, idempotency).

- Tenant data isolation and soft-delete support for compliance and recovery.

- A clear path to scale from pilot to production without redesigning the core infrastructure.