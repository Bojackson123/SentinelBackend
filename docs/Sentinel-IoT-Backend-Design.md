Sentinel IoT Backend Design

Overview


This document describes the backend architecture for the Sentinel platform — a scalable IoT monitoring system for grinder pump installations. The backend processes device telemetry, manages alarms, handles device provisioning, and supports a multi-tenant business model serving both independent homeowners and plumbing companies.

The backend is built with .NET 9 and Azure services, with Azure IoT Hub as the primary device communication layer.


---

Domain Context

What is a Grinder Pump?


A grinder pump is a sewage pump installed at a residence or commercial building that grinds waste and pumps it to the municipal sewer system under pressure. These pumps run automatically in response to water level in the holding tank. A control panel at the installation site manages the pump and displays status indicators including a high water alarm light.

What is the Sentinel Device?


The Sentinel device is a custom hardware add-on that taps into an existing grinder pump control panel and reads its signals. It does not control the pump — it is a read-only monitoring device. The Sentinel device reads electrical signals from the panel such as voltage, pump current draw, run state, and alarm outputs, and transmits this data to the cloud over a cellular connection.

The core value proposition is early warning. The high water alarm on the physical control panel is often subtle and easy to miss. The Sentinel device detects alarm conditions before they become service calls and notifies the right people proactively.

Current telemetry fields:

- panelVoltage — AC voltage on the panel
- pumpCurrent — current draw of the pump motor during a cycle
- highWaterAlarm — whether the high water float switch is active
- temperatureC — enclosure or ambient temperature
- signalRssi — cellular signal strength
- cycleCount — total number of pump run cycles (cumulative lifetime counter)
- runtimeSeconds — duration of the most recent pump cycle in seconds
- firmwareVersion — version of firmware running on the Sentinel device

Cycle count canonical rule:

The authoritative rule for incrementing the pump cycle count is the explicit pump_start_event field. When firmware sends pump_start_event = true, the backend increments the cycle count. The backend does not infer cycle starts from pump_running state transitions (false → true), as this can cause double-counting when both signals are present. If future firmware revisions cannot guarantee pump_start_event, the fallback rule (infer from pump_running transition) must be documented and enforced as a firmware-version-specific behavior.

Note: telemetry fields are subject to revision once the physical device is finalized.


---

Multi-Tenant Model


The platform supports two distinct tenant types.

Tenant Type 1: Independent Homeowner


An individual homeowner purchases the Sentinel device and pays a recurring subscription directly to Sentinel. A field technician from the Sentinel team installs the device. The homeowner has access to the consumer-facing app, where they can view their device's telemetry and receive alarm notifications.

Responsibilities:
- Homeowner pays for the device and the subscription
- Homeowner has read access to their own device data
- Sentinel technicians install and manage the hardware

Tenant Type 2: Plumbing Company


A plumbing company purchases devices in bulk and pays a commercial subscription to the Sentinel platform. The plumbing company's technicians install devices at their customers' (homeowners') sites. In this model the homeowner does not have access to the app or the data — the plumbing company owns the monitoring relationship.

The selling point for plumbing companies is proactive service: they are alerted first when something goes wrong at a customer site, enabling them to arrive before the homeowner even notices a problem. This allows plumbing companies to "own" the grinder pump repair relationship with their customers.

Responsibilities:
- Plumbing company pays for devices and the commercial subscription
- Plumbing company manages all sites and homeowners under their account
- Plumbing company receives alarm notifications and telemetry access
- Homeowners are managed records within the company's account and do not log in

Summary table:

| Aspect | Independent Homeowner | Plumbing Company |
|---|---|---|
| Pays for devices | Yes | Yes |
| Pays subscription | Individual | Commercial |
| App access | Yes (consumer app) | Yes (company admin app) |
| Homeowner sees data | Yes | No |
| Manages multiple sites | No | Yes |
| Installs devices | Sentinel tech | Company tech |


---

Goals

- Support secure communication with field devices

- Scale from a small deployment to a large device fleet

- Separate device communication from business APIs

- Provide reliable telemetry ingestion and alarm processing

- Enable proactive alarm notification and early warning delivery

- Support a multi-tenant model with independent homeowners and plumbing companies

- Preserve historical telemetry for future ML model training

- Prepare for multiple React frontend apps without redesigning the backend


---

Non-Goals

- Building a custom MQTT broker or device gateway

- Letting devices communicate directly with the web API

- Using the UI as the primary source of telemetry consumption

- Treating Azure IoT Hub as the main application database

- Sending commands to the grinder pump or control panel (the Sentinel device is read-only)


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
	| endpoint  |   | alarm topic    |
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


- critical alarm workflows via a topic with multiple subscriptions

- decoupled event handling with independent consumers

- integration with downstream business processing

Application Database


Suggested options:


- Azure SQL

- Azure Database for PostgreSQL

Used for:


- latest device state

- historical telemetry

- alarms and events

- customer/site metadata

- device inventory for application use

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
	  "timestampUtc": "2026-04-05T00:00:00Z",
	  "messageId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
	  "schemaVersion": 1,
	  "bootId": "boot-20260405-001",
	  "sequenceNumber": 1821,
	  "panelVoltage": 240.5,
	  "pumpCurrent": 8.3,
	  "cycleCount": 1821,
	  "runtimeSeconds": 46,
	  "highWaterAlarm": false,
	  "temperatureC": 31.2,
	  "signalRssi": -73,
	  "firmwareVersion": "1.2.0"
	}

Suggested application properties

	messageType=telemetry
	schemaVersion=1

For alarms:


	messageType=alarm
	severity=critical
	alarmType=high_water
	schemaVersion=1

These properties can be used by IoT Hub routing rules.

Important: device identity trust model

The payload body may include fields such as deviceId or siteId for convenience and debugging, but the backend must not treat these as authoritative.

- Authoritative device identity must come from IoT Hub system properties (the authenticated device connection identity).
- Site, customer, and tenant mapping must be resolved from the backend's own database (Devices → DeviceAssignments → Sites → Customers).
- Routing and business logic must never depend on device-supplied siteId or similar fields.
- Device-supplied fields in the payload body should be logged for diagnostics but ignored for authorization, routing, and data attribution.


---

Message Contract

All device-to-cloud messages must include a standard envelope. This section defines which fields are required, which are recommended, and which source is authoritative for each.

Required envelope fields

| Field | Source | Description |
|---|---|---|
| messageId | Payload body (device-generated) | Unique identifier per message. Required for deduplication. Device firmware must guarantee uniqueness per device. |
| timestampUtc | Payload body (device clock) | Source event time. Used for state ordering and time-series storage. |
| schemaVersion | Application property or payload body | Message schema version. Used for validation and forward compatibility. |
| messageType | Application property | One of: telemetry, alarm, diagnostic, lifecycle. Used by IoT Hub routing rules. |

Recommended envelope fields

| Field | Source | Description |
|---|---|---|
| bootId | Payload body | Unique identifier per device boot cycle. Useful for diagnostics, sequence analysis, and detecting clock resets after reboot. |
| sequenceNumber | Payload body | Monotonically increasing counter per boot cycle. Enables ordering analysis and gap detection. |

Authoritative source rules

| Data | Authoritative Source | Notes |
|---|---|---|
| Device identity | IoT Hub system property (connectionDeviceId) | Never trust device-supplied deviceId in payload body |
| Site / customer / tenant | Backend database (Devices → DeviceAssignments → Sites → Customers) | Never trust device-supplied siteId |
| Message timestamp | Payload body (timestampUtc) | Device clock is source of truth for event ordering. Backend records received_at separately. |
| Schema version | Application property or payload body (schemaVersion) | Used for validation routing |
| Message type | Application property (messageType) | Used by IoT Hub message routing rules |


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

Twin update strategy:

1. DPS initial twin — sets system-wide defaults at first boot (telemetryIntervalSeconds, diagnosticsEnabled, highWaterThreshold). These defaults apply to all devices uniformly.

2. Tech assignment twin update — when a device is assigned to a site, the backend updates the twin with site-specific configuration (timezone from the Site record). This is the only twin update during the assignment flow.

3. Ongoing twin updates — the API can update desired properties post-assignment to change telemetry interval, thresholds, or diagnostics for individual devices.

Important: the Sentinel device is read-only with respect to the grinder pump hardware. Desired properties configure the Sentinel device's own behavior (reporting frequency, alarm thresholds for notification purposes, diagnostics). They do not control anything on the pump or control panel.

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


Direct methods should be used for immediate actions on the Sentinel monitoring device itself. They do not send signals to the grinder pump or control panel — the Sentinel device is read-only with respect to the pump hardware.

Suggested methods:


- reboot — restart the Sentinel device

- ping — verify device is reachable and responsive

- captureSnapshot — request an immediate telemetry sample outside the normal interval

- clearFault — clear a device-side fault condition

- runSelfTest — run an on-device self-diagnostic

- syncNow — force immediate sync of reported properties

Guidance:


- direct methods should be short-running

- they should return a simple status payload

- long-running tasks should acknowledge receipt and report progress later through telemetry or reported properties


---

Routing Strategy

Route 1: telemetry


Route all normal telemetry to the built-in Event Hubs-compatible endpoint for ingestion by the worker service.

Route 2: alarms


Route critical alarm messages to an Azure Service Bus topic for immediate processing and notifications. A topic with subscriptions is used instead of a queue so that multiple independent consumers (alarm processor, notification service, audit logger) can each receive a copy of the message independently.

Initial topic subscriptions:

- AlarmProcessing — creates/updates Alarm records in the database
- NotificationDispatch — triggers notification workflows and escalation
- AuditLog — records all alarm signals for compliance and diagnostics

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

- checkpoint in batches: per partition, every N messages (e.g., 100) or every T seconds (e.g., 30), whichever comes first. Do not checkpoint after every individual message — this becomes expensive at scale and limits throughput. Rely on idempotent processing to tolerate replay on restart.

- persist messages durably before checkpointing. A checkpoint means "all messages up to this offset have been durably processed."

- support retries with exponential backoff for transient failures (database unavailable, network errors). Do not checkpoint until the message is durably persisted or explicitly routed to the poison-message store.

- maintain idempotency using (device_id, message_id) as the deduplication key. If message_id is already present in TelemetryHistory for that device, the message is a duplicate — skip processing and treat as eligible for checkpoint.

- route unparseable, malformed, or unprocessable messages to the FailedIngressMessages table (see Data Model) rather than retrying indefinitely. Event Hubs does not provide a native dead-letter queue like Service Bus. The FailedIngressMessages table serves as a durable poison-message store.

- log validation failures and poison messages separately for monitoring and alerting.

Deduplication strategy

The primary deduplication key is (device_id, message_id).

- device_id is resolved from IoT Hub system properties (connectionDeviceId), not from the payload body.
- message_id is a required field in the message contract. Device firmware must guarantee uniqueness per device (e.g., UUID or monotonic counter per boot cycle).
- If a message with the same (device_id, message_id) already exists in TelemetryHistory, the ingestion worker skips it as a no-op.
- bootId and sequenceNumber are recommended fields for diagnostics and ordering analysis but are not part of the primary dedup key.
- timestampUtc is not included in the dedup key because device clocks can drift, repeat, or jump after reboot. Dedup relies on message_id uniqueness.

Failure-handling matrix

| Failure Condition | Action | Checkpoint? |
|---|---|---|
| Malformed JSON / unparseable payload | Write to FailedIngressMessages with error metadata | Yes — message is unrecoverable |
| Unknown or unsupported schemaVersion | Write to FailedIngressMessages | Yes — cannot process without schema |
| Missing required envelope fields (messageId, timestampUtc) | Write to FailedIngressMessages | Yes — cannot dedup or order |
| Transient database error | Retry with exponential backoff | No — do not checkpoint until durable outcome |
| Duplicate message (device_id + message_id exists) | Idempotent no-op, skip processing | Yes — already processed |
| Downstream notification provider failure | Retry notification independently | Yes — alarm/telemetry persisted, notification retries separately |
| Device not found in Devices table | Write to FailedIngressMessages (orphan message) | Yes — cannot attribute |
| State ordering rejection (older timestamp) | Skip LatestDeviceState update, still write TelemetryHistory | Yes — history is append-only |


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


The application database is the queryable operational store for all business data. The IoT Hub and DPS are not used for application queries.

Devices


Stores application-level device metadata and lifecycle state.

Fields:

- id (int, PK)
- device_id (string, unique) — IoT Hub device identity
- serial_number (string, unique) — format GP-YYYYMM-NNNNN, flashed at manufacturing
- hardware_revision (string, nullable) — hardware variant for tracking future revisions
- firmware_version (string, nullable) — last known firmware version from telemetry
- status (enum) — see Device Lifecycle section
- provisioned_at (datetime, nullable) — when the device first connected through DPS
- created_at (datetime)
- updated_at (datetime)

LatestDeviceState


Stores the most recent known telemetry snapshot for each device. One record per device, updated on every telemetry message. Used for dashboard queries and current-state display.

State ordering rule: when a new telemetry message arrives, the ingestion worker compares the incoming message's timestampUtc against the stored last_telemetry_timestamp_utc. The upsert only proceeds if the incoming timestamp is strictly newer. This prevents late-arriving or replayed messages from overwriting correct state with stale data.

Fields:

- id (int, PK)
- device_id (FK → Devices)
- last_seen_at (datetime) — last time any message was received from this device (database write time)
- last_telemetry_timestamp_utc (datetime) — source event time from the most recent telemetry message. This is the field used for state ordering comparisons, not updated_at.
- last_message_id (string, nullable) — messageId from the most recent accepted telemetry message. Used for dedup verification and diagnostics.
- last_boot_id (string, nullable) — bootId from the most recent accepted message. Useful for detecting device reboots.
- last_sequence_number (int, nullable) — sequenceNumber from the most recent accepted message per boot cycle.
- panel_voltage (double, nullable)
- pump_current (double, nullable)
- high_water_alarm (bool, nullable)
- temperature_c (double, nullable)
- signal_rssi (int, nullable)
- runtime_seconds (int, nullable) — duration of the most recent pump cycle
- cycle_count (int, nullable) — cumulative lifetime pump cycle count
- updated_at (datetime) — database write time. Do not use for state ordering.

Upsert logic (Azure SQL / T-SQL):

The ingestion worker uses an explicit UPDATE-then-INSERT pattern. Azure SQL does not support MySQL-style ON DUPLICATE KEY UPDATE. MERGE is avoided due to well-documented edge cases under concurrency.

	-- Step 1: Try to update existing row, only if incoming timestamp is newer
	UPDATE LatestDeviceState
	SET
	    panel_voltage = @panelVoltage,
	    pump_current = @pumpCurrent,
	    high_water_alarm = @highWaterAlarm,
	    temperature_c = @temperatureC,
	    signal_rssi = @signalRssi,
	    runtime_seconds = @runtimeSeconds,
	    cycle_count = @cycleCount,
	    last_seen_at = SYSUTCDATETIME(),
	    last_telemetry_timestamp_utc = @timestampUtc,
	    last_message_id = @messageId,
	    last_boot_id = @bootId,
	    last_sequence_number = @sequenceNumber,
	    updated_at = SYSUTCDATETIME()
	WHERE device_id = @deviceId
	  AND last_telemetry_timestamp_utc < @timestampUtc;

	-- Step 2: Insert if no row exists yet
	IF @@ROWCOUNT = 0 AND NOT EXISTS (
	    SELECT 1 FROM LatestDeviceState WHERE device_id = @deviceId
	)
	BEGIN
	    INSERT INTO LatestDeviceState (
	        device_id, panel_voltage, pump_current, high_water_alarm,
	        temperature_c, signal_rssi, runtime_seconds, cycle_count,
	        last_seen_at, last_telemetry_timestamp_utc, last_message_id,
	        last_boot_id, last_sequence_number, updated_at
	    )
	    VALUES (
	        @deviceId, @panelVoltage, @pumpCurrent, @highWaterAlarm,
	        @temperatureC, @signalRssi, @runtimeSeconds, @cycleCount,
	        SYSUTCDATETIME(), @timestampUtc, @messageId,
	        @bootId, @sequenceNumber, SYSUTCDATETIME()
	    );
	END;

TelemetryHistory


Stores time-series historical telemetry for all devices. Uses fully typed columns for all known metrics to support efficient dashboard queries, time-series charting, and future ML model training. Retained indefinitely.

Fully typed columns are preferred over raw JSON storage because dashboard queries (e.g., chart panel_voltage over time, aggregate pump_current by device) need indexed, typed data for acceptable query performance at scale.

Fields:

- id (bigint, PK) — bigint to accommodate high row volume
- device_id (FK → Devices)
- message_id (string) — from message envelope, used for deduplication with unique index on (device_id, message_id)
- timestamp_utc (datetime) — source event time from device
- panel_voltage (double, nullable)
- pump_current (double, nullable)
- high_water_alarm (bool, nullable)
- temperature_c (double, nullable)
- signal_rssi (int, nullable)
- runtime_seconds (int, nullable)
- cycle_count (int, nullable)
- firmware_version (string, nullable)
- boot_id (string, nullable)
- sequence_number (int, nullable)
- payload_json (nvarchar(max), nullable) — raw message payload preserved for forward compatibility and debugging
- received_at (datetime) — database write time

Indexes:

- Unique: (device_id, message_id) — deduplication enforcement
- Clustered or primary query index: (device_id, timestamp_utc DESC) — supports time-range queries per device
- Consider partitioning by timestamp_utc if row volume exceeds performance targets

Alarms


Stores alarm incidents. Each alarm represents a single openable/closeable incident for a specific alarm condition on a device. Alarm state transitions are recorded separately in AlarmEvents for audit.

Fields:

- id (int, PK)
- device_id (FK → Devices)
- alarm_type (enum: HighWater, PumpOverload, PanelPowerLoss, SensorFailure, CommunicationFault, DeviceOffline)
- severity (enum: Critical, Warning, Info)
- status (enum: Active, Acknowledged, Suppressed, Resolved)
- started_at (datetime) — when the alarm condition was first detected
- resolved_at (datetime, nullable) — when the alarm was fully resolved
- suppress_reason (string, nullable) — reason provided by operator for manual clear/suppress
- suppressed_by_user_id (string, nullable, FK → ApplicationUser)
- details_json (nvarchar(max), nullable) — additional context from the triggering message
- created_at (datetime)
- updated_at (datetime)

AlarmEvents


Stores the full audit trail of state transitions for each alarm incident. Every status change, acknowledgment, suppression, or resolution is recorded as an event.

Fields:

- id (int, PK)
- alarm_id (FK → Alarms)
- event_type (enum: Raised, Acknowledged, Silenced, Suppressed, AutoCleared, ManualCleared, Reopened, Resolved)
- user_id (string, nullable, FK → ApplicationUser) — set for operator-initiated events
- reason (string, nullable) — required for ManualCleared and Suppressed events
- metadata_json (nvarchar(max), nullable) — additional event context
- created_at (datetime)

Alarm lifecycle rules

The alarm lifecycle supports these transitions:

1. Raised — telemetry indicates an alarm condition (e.g., highWaterAlarm = true). A new Alarm record is created in Active status.

2. Acknowledged — an operator acknowledges the alarm. Status remains Active but the acknowledgment is recorded as an AlarmEvent. This indicates awareness, not resolution.

3. Suppressed (manual clear) — an operator manually clears the alarm. A reason is required. The alarm transitions to Suppressed status. While suppressed, the system will not raise a new alarm for the same condition on the same device, even if telemetry continues to report the condition as active.

4. Resolved after suppression — while an alarm is in Suppressed status, the system monitors incoming telemetry. When the physical condition actually clears (e.g., highWaterAlarm = false), the alarm transitions to Resolved status and the system re-arms for future alarm detection on that device/condition.

5. Auto-cleared — telemetry indicates the alarm condition has cleared (e.g., highWaterAlarm transitions from true to false) while the alarm is in Active or Acknowledged status. The alarm transitions to Resolved.

6. New incident after resolution — once an alarm is Resolved (whether from auto-clear or post-suppression resolution), the system is re-armed. The next active signal for the same condition opens a brand new Alarm record.

State machine:

	                 +--------+
	  telemetry  --> | Active | <-- (new incident)
	  alarm signal   +---+----+
	                     |
	          +----------+----------+
	          |                     |
	     operator ack          operator clear
	          |                (reason required)
	          v                     |
	   +--------------+             v
	   | Acknowledged |       +------------+
	   +------+-------+       | Suppressed |
	          |               +------+-----+
	          |                      |
	     condition clears       condition clears
	     (auto-clear)          (re-arm)
	          |                      |
	          v                      v
	   +----------+           +----------+
	   | Resolved |           | Resolved |
	   +----------+           +----------+

Companies


Stores plumbing company tenant accounts.

Fields:

- id (int, PK)
- name
- contact_email
- billing_email
- stripe_customer_id (nullable)
- subscription_status (enum)
- created_at
- updated_at

Customers


Stores individual homeowner accounts. In the independent homeowner model, the customer is the direct subscriber. In the plumbing company model, the customer is a managed homeowner record under the company's account and does not have app access.

Fields:

- id (int, PK)
- first_name
- last_name
- email
- phone
- company_id (FK → Companies, nullable) — set when homeowner is managed by a plumbing company
- stripe_customer_id (nullable) — set only for independent homeowners
- subscription_status (enum)
- created_at
- updated_at

Sites


Stores physical installation locations. A site represents a house, commercial building, or other location where one or more Sentinel devices are installed. Each site belongs to a customer.

Fields:

- id (int, PK)
- customer_id (FK → Customers)
- name
- address
- city
- state
- postal_code
- latitude (nullable)
- longitude (nullable)
- timezone
- created_at
- updated_at

DeviceAssignment


Tracks the assignment history of devices to sites and customers over time. A new assignment record is created whenever a device is moved to a different site or changes ownership. This table preserves the full history of where each device has been deployed and under which account, supporting historical data attribution and ML training data integrity.

During orphaned states (device exists but has no active assignment), the ingestion worker still processes telemetry. Data is attributed to the device record only — site and customer context are null until a new assignment is created. Historical telemetry from orphaned periods remains attributable via the device_id and can be retroactively linked if the device is reassigned.

Fields:

- id (int, PK)
- device_id (FK → Devices)
- site_id (FK → Sites)
- assigned_at (datetime)
- assigned_by_user_id (string, FK → ApplicationUser)
- unassigned_at (datetime, nullable)
- unassigned_by_user_id (string, nullable, FK → ApplicationUser)
- unassignment_reason (enum, nullable: Reassignment, Decommission, SubscriptionLapsed, Other)

Subscriptions


Stores billing subscription records linked to Stripe. A subscription belongs to either a Company or a Customer, not both.

Fields:

- id (int, PK)
- stripe_subscription_id
- stripe_customer_id
- owner_type (enum: Company, Customer)
- company_id (FK → Companies, nullable)
- customer_id (FK → Customers, nullable)
- status (enum: Trialing, Active, PastDue, Cancelled, Suspended)
- current_period_start
- current_period_end
- created_at
- updated_at

Leads


Tracks abandoned devices that are candidates for re-assignment or re-sale. A lead is created when a plumbing company stops paying their subscription, leaving devices physically installed at homeowner sites with no active subscriber. The device remains physically installed because it is tied to that grinder pump installation. The lead represents a revenue recovery or re-provisioning opportunity.

Fields:

- id (int, PK)
- device_id (FK → Devices, nullable)
- site_id (FK → Sites, nullable)
- previous_company_id (FK → Companies, nullable)
- previous_customer_id (FK → Customers, nullable)
- status (enum: Available, InNegotiation, Sold)
- notes
- created_at
- updated_at

ApplicationUser


Represents all authenticated users of the platform. Extends ASP.NET Core IdentityUser for authentication and authorization. Links to domain entities (Company, Customer) for data scoping.

Fields:

- id (string, PK) — from IdentityUser
- first_name
- last_name
- role (enum: InternalAdmin, InternalTech, CompanyAdmin, CompanyTech, HomeownerViewer)
- company_id (FK → Companies, nullable) — set for CompanyAdmin and CompanyTech
- customer_id (FK → Customers, nullable) — set for HomeownerViewer
- all standard IdentityUser fields (email, phone, password hash, etc.)

CommandLog


Stores an audit record for every remote command sent to a Sentinel device. Command endpoints are asynchronous — the API creates a CommandLog record immediately and returns 202 Accepted. A background service executes the command and updates the status.

Fields:

- id (int, PK)
- device_id (FK → Devices)
- command_type (enum: Reboot, Ping, CaptureSnapshot, ClearFault, RunSelfTest, SyncNow)
- status (enum: Pending, Sent, Succeeded, Failed, TimedOut)
- requested_by_user_id (string, FK → ApplicationUser)
- requested_at (datetime)
- sent_at (datetime, nullable)
- completed_at (datetime, nullable)
- response_json (nvarchar(max), nullable) — direct method response payload
- error_message (string, nullable)
- created_at (datetime)
- updated_at (datetime)

FailedIngressMessages


Stores telemetry messages that could not be processed by the ingestion worker. Serves as a durable poison-message store since Event Hubs does not provide a native dead-letter queue.

Fields:

- id (bigint, PK)
- source_device_id (string, nullable) — from IoT Hub system properties if available
- message_id (string, nullable) — from payload if parseable
- partition_id (string) — Event Hubs partition
- offset (string) — Event Hubs offset for the message
- enqueued_at (datetime) — Event Hubs enqueue time
- failure_reason (enum: MalformedJson, UnknownSchema, MissingRequiredFields, UnknownDevice, ProcessingError)
- error_message (string) — detailed error description
- raw_payload (nvarchar(max)) — original message body for inspection and potential reprocessing
- headers_json (nvarchar(max), nullable) — application properties and system properties
- created_at (datetime)

NotificationIncidents


Stores notification workflows triggered by alarms. Separate from CommandLog, which is device-command-focused. A notification incident represents a single notification workflow triggered by an alarm event.

Fields:

- id (int, PK)
- alarm_id (FK → Alarms)
- status (enum: Pending, InProgress, Delivered, Escalated, Failed, Cancelled)
- priority (enum: Critical, High, Normal, Low)
- target_user_id (string, nullable, FK → ApplicationUser)
- target_channel (enum: Push, SMS, Email, InApp)
- created_at (datetime)
- updated_at (datetime)

NotificationAttempts


Stores individual delivery attempts for each notification incident. Tracks retries and per-attempt outcomes.

Fields:

- id (int, PK)
- notification_incident_id (FK → NotificationIncidents)
- attempt_number (int)
- channel (enum: Push, SMS, Email, InApp)
- status (enum: Sent, Delivered, Failed, Bounced)
- provider_response (string, nullable) — response from notification provider
- attempted_at (datetime)
- completed_at (datetime, nullable)

EscalationEvents


Stores escalation decisions when a notification is not acknowledged within the expected timeframe. Tracks the escalation chain for each alarm notification.

Fields:

- id (int, PK)
- notification_incident_id (FK → NotificationIncidents)
- escalated_from_user_id (string, nullable, FK → ApplicationUser)
- escalated_to_user_id (string, FK → ApplicationUser)
- escalation_reason (enum: NoAcknowledgment, ProviderFailure, ManualEscalation)
- escalated_at (datetime)


---

Device Lifecycle


Devices move through five stages from factory production to end of service.

Stage 1: Manufacturing


A backend admin or automated process creates a manufacturing batch.

- Generates serial numbers in the format GP-YYYYMM-NNNNN
- Creates Device records in status Manufactured
- Derives a symmetric key per device from the DPS enrollment group key (HMAC-SHA256)
- Exports a CSV of serial numbers and derived keys to the factory
- Factory flashes the serial number and key onto the physical Sentinel device

At this point the device exists in the database but has never touched Azure.


Stage 2: First Boot (DPS Provisioning)


The device powers on for the first time in the field.

- Device contacts Azure DPS using its flashed credentials
- DPS calls the allocation webhook (POST /api/dps/allocate)
- Backend validates the webhook secret and looks up the device by serial number
- Device transitions Manufactured → Unprovisioned
- Backend returns the IoT Hub hostname and an initial device twin configuration
- DPS registers the device in IoT Hub and directs it to connect

The device is now known to IoT Hub but not yet assigned to any customer or site.


Stage 3: Tech Assignment


A field technician installs the Sentinel device at a physical site.

- Technician uses the app to claim the device by serial number
- Backend validates the device is in Unprovisioned status
- Technician selects an existing Site or creates a new one and assigns the device to it
- Only technicians (internal or company) can create new Sites — homeowners cannot
- Company technicians are scoped to their own organization and can only assign to sites under their company's customers
- Device transitions Unprovisioned → Assigned
- Backend updates the device twin with site-specific configuration (timezone from the Site record)
- A DeviceAssignment record is created linking the device to the site and customer

The device is now linked to a site, which belongs to a customer (homeowner or plumbing company account).

Note: domain business logic errors in the assignment flow use the Result<T> pattern rather than exceptions. Validation failures (e.g., invalid status transition, unauthorized scope) return typed error results.


Stage 4: Active Operation


The device begins sending telemetry.

- Device transitions Assigned → Active on first telemetry received
- Ingestion worker consumes telemetry from the Event Hubs endpoint
- LatestDeviceState is upserted on every message
- TelemetryHistory records are written for long-term retention and ML training
- High water alarm and threshold events route to Service Bus and create Alarm records
- Twin desired properties can be updated via the API to change telemetry interval, thresholds, etc.
- Direct methods (reboot, ping, captureSnapshot, etc.) can be invoked by operators on the Sentinel device — not on the pump or control panel


Stage 5: Decommission


Device is removed from service.

- Status set to Decommissioned
- DPS webhook rejects any future provisioning attempts
- Device identity can be disabled in IoT Hub
- Historical data is retained indefinitely
- If a device is abandoned due to a lapsed subscription it becomes a Lead rather than being immediately decommissioned


---

Lead Lifecycle


When a plumbing company stops paying their subscription, the Sentinel devices they deployed remain physically installed at their homeowners' sites. These become Leads — abandoned devices that represent a re-provisioning or re-sale opportunity.

Key points:

- A device is installed once at a grinder pump and stays there permanently
- If an independent homeowner moves to a new home, a new device will be installed at the new home for free; the original device at the old site becomes a Lead
- Leads track the previous company or customer owner for context
- Lead statuses: Available → InNegotiation → Sold
- When a lead converts to Sold, the device can be re-provisioned to a new tenant and a new DeviceAssignment record is created


---

User Roles and Apps


The platform supports three front-end applications serving different user types.

Internal Admin / Technician App


Used by the Sentinel internal team:
- Manufacturing batch management
- Device provisioning and assignment
- Fleet-wide monitoring and support
- Lead management

Company App (Plumbing Companies)


Used by plumbing company staff:
- View all sites and devices under their account
- Receive alarm notifications and view telemetry
- Manage homeowner records in their account
- View device health across their fleet
- Homeowners in this model do not have app access

Consumer App (Independent Homeowners)


Used by individual homeowners subscribed directly to Sentinel:
- View their own device's current state and telemetry history
- Receive alarm notifications
- Manage their subscription and account


---

Identity and Authentication


The platform uses ASP.NET Core Identity for user management and authentication. All authenticated users are represented by an ApplicationUser entity that extends IdentityUser.

User hierarchy (highest to lowest privilege):

1. InternalAdmin — Sentinel staff with full system access (god mode)
2. InternalTech — Sentinel field technicians who provision and install devices
3. CompanyAdmin — Plumbing company administrators who manage their fleet
4. CompanyTech — Plumbing company field technicians scoped to their org
5. HomeownerViewer — Independent homeowners with read-only access to their own device

Key rules:

- Internal staff (InternalAdmin, InternalTech) have no CompanyId or CustomerId — they can operate across all tenants
- CompanyAdmin and CompanyTech are always linked to a CompanyId — all data access is scoped to that company's customers and sites
- HomeownerViewer is always linked to a CustomerId — data access is scoped to their own sites and devices
- A Customer entity represents a homeowner record (both standalone and company-managed) — this is the domain record, not the login identity
- The ApplicationUser entity is the login identity with optional FK links to Company and/or Customer
- Company technicians can only create sites, assign devices, and view data within their own organization

Authentication approach:

- ASP.NET Core Identity with JWT bearer tokens
- Role-based authorization using the UserRole enum
- IdentityDbContext used as the base for the application database context
- DeviceAssignment.AssignedByUserId references ApplicationUser.Id (string PK from Identity)


---

Multi-Tenant Isolation


Tenant isolation is a first-class concern. The platform uses a polymorphic tenant model that maps directly to the two business models.

Tenant boundary definition

- In the plumbing company model, the tenant is the Company. All data access is scoped by company_id.
- In the independent homeowner model, the tenant is the Customer. All data access is scoped by customer_id.
- Internal Sentinel staff (InternalAdmin, InternalTech) bypass tenant filters via role-based policies.

Tenant resolution rules

| User Role | Tenant Type | Tenant ID Source | Scope |
|---|---|---|---|
| InternalAdmin | None (global) | N/A | All data across all tenants |
| InternalTech | None (global) | N/A | All data across all tenants |
| CompanyAdmin | Company | ApplicationUser.CompanyId | Company's customers, sites, devices |
| CompanyTech | Company | ApplicationUser.CompanyId | Company's customers, sites, devices |
| HomeownerViewer | Customer | ApplicationUser.CustomerId | Own sites and devices only |

Data isolation enforcement

- Every queryable entity that contains tenant-sensitive data must be filterable by the tenant chain: Devices → DeviceAssignments → Sites → Customers → Companies.
- API authorization middleware resolves the current tenant context from the authenticated user's claims (CompanyId or CustomerId) and applies it as a global query filter.
- EF Core global query filters enforce tenant scoping at the data layer as a defense-in-depth measure. This ensures that even if controller-level authorization is misconfigured, cross-tenant data leakage cannot occur through the ORM.
- Internal roles bypass tenant filters through an explicit policy (e.g., IgnoreTenantFilter claim or role check), never through the absence of a filter.
- All API endpoints must enforce tenant scoping. There must be no endpoint that returns unscoped data to non-internal users.
- Telemetry and alarm data inherit tenant scope through the device → assignment → site → customer chain. An unassigned (orphaned) device's data is visible only to internal staff until reassigned.


---

Error Handling Pattern


Domain business logic uses a Result<T> pattern for expected failure cases. Exceptions are reserved for truly exceptional situations (infrastructure failures, programmer errors).

The Result<T> type lives in the Domain project and is used by Application services. Controllers map error results to appropriate HTTP status codes.

Examples of Result<T> usage:

- Invalid device status transition → error result with typed reason
- Unauthorized scope (company tech trying to access another org) → error result
- Device not found by serial number → error result
- Site validation failure → error result

Examples of exceptions (kept for infrastructure concerns):

- Database connection failure
- Azure SDK transient faults
- Configuration errors at startup


---

Suggested .NET Solution Structure

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
	  Sentinel-IoT-Backend-Design.md

Project responsibilities

SentinelBackend.Api

- controllers/endpoints

- auth

- request validation

- orchestration of application services

SentinelBackend.Ingestion

- background hosted services

- Event Hubs processing

- message handlers

- checkpointing and retry logic

SentinelBackend.Domain

- entities

- enums

- business rules

- domain events

SentinelBackend.Application

- use cases

- command/query handlers

- service interfaces

SentinelBackend.Infrastructure

- EF Core

- Azure SDK integrations

- repositories

- background infrastructure services

SentinelBackend.Contracts

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

Scale Targets


Explicit scale assumptions for the initial deployment. These numbers drive database sizing, IoT Hub SKU selection, partition count, and retention policy decisions.

| Metric | Target |
|---|---|
| Device count | ~5,000 devices |
| Telemetry interval | Every 5 minutes |
| Messages per device per day | 288 |
| Total messages per day | ~1,440,000 |
| Average message payload size | ~500 bytes (JSON) |
| Daily ingestion volume | ~720 MB |
| Hot retention period (Azure SQL) | 90 days |
| Hot retention row count | ~130 million rows |
| Cold retention | Indefinite (blob archive for ML training) |
| Acceptable dashboard query latency | < 500ms for latest state, < 2s for 90-day history range |
| Acceptable alarm processing latency | < 5s from device event to alarm record creation |
| IoT Hub partitions | 4 (sufficient for 5K devices at 5min intervals; increase if message rate grows) |

These targets validate that Azure SQL is an appropriate Phase 1–2 database. At ~130M rows in 90 days with proper indexing (device_id, timestamp_utc) and potential partitioning, query performance is achievable on Azure SQL S3/P1 tier or above. If device count or message frequency increases significantly beyond these targets, evaluate tiered storage (hot SQL + cold blob) or migration to a time-series-optimized store.


---

Scalability Considerations

1. Start with DPS


Even for a small fleet, DPS should be used from day one to avoid redesign later.

DPS authentication: the initial deployment uses symmetric key authentication with enrollment groups. Each device derives its key from the group enrollment key using HMAC-SHA256. This is the simplest rollout model and is appropriate for the initial fleet size.

Future migration to X.509: for production fleets exceeding ~1,000 devices, or when regulatory/compliance requirements demand it, the platform should migrate to X.509 certificate-based authentication. This provides stronger per-device identity without shared secrets. The migration path is:

- Provision a device CA certificate
- Issue per-device certificates during manufacturing (replacing the symmetric key flashing step)
- Create an X.509 enrollment group in DPS
- New devices use X.509; existing devices continue on symmetric keys until rotated
- Update the DPS allocation webhook to handle both authentication types during the transition period

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

- use batch checkpointing in the ingestion worker (every N messages or T seconds per partition)

- implement exponential backoff for transient faults

- route unparseable and poison messages to the FailedIngressMessages table (Event Hubs does not provide a native dead-letter queue)

- validate schema version in every incoming message

- keep device payloads compact

- require messageId in the message contract for deduplication

- design consumers to be idempotent using (device_id, message_id) dedup key


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

Device offline detection

A device is considered offline when no telemetry has been received within a configurable threshold. This is a top operational concern for real fleet management.

Offline detection rules:

- Each device type has a configurable offline threshold, defaulting to 3x the expected telemetry interval (e.g., 15 minutes for a device reporting every 5 minutes).
- The threshold is evaluated against last_seen_at in LatestDeviceState compared to the current time.
- When a device exceeds the offline threshold, a DeviceOffline alarm is raised using the standard alarm lifecycle (Active → Acknowledged → Resolved).
- When the device resumes sending telemetry, the DeviceOffline alarm is auto-cleared.
- Maintenance windows can be configured per device or per site to suppress offline alarms during planned downtime (e.g., firmware updates, seasonal shutdowns, power maintenance). During a maintenance window, the offline detection rule is suspended and no DeviceOffline alarm is raised.
- Offline status should be queryable via the API for fleet dashboards (e.g., GET /api/devices?status=offline).
- The offline detection check can run as a periodic background job in the ingestion worker or as a separate hosted service.

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


Maintain a latest-state table for quick UI queries. Use source event time (last_telemetry_timestamp_utc) for state ordering, not database write time.

Historical archive pattern


Store historical telemetry separately from latest-state records. Use fully typed columns for queryable metrics.

Alarm lifecycle pattern


Track alarm incidents and their state transitions as separate entities (Alarms + AlarmEvents). Support Active → Acknowledged → Suppressed → Resolved lifecycle with full audit trail.

Command audit pattern


Every remote command should be logged in the CommandLog table with:


- who initiated it

- when it was sent

- target device

- result

- full lifecycle (Pending → Sent → Succeeded/Failed/TimedOut)

Soft delete pattern


Entities that support soft deletes (Devices, Customers, Companies, Sites) use an is_deleted flag and deleted_at timestamp. EF Core global query filters automatically exclude soft-deleted records from all queries. To include deleted records (e.g., for admin recovery or audit views), queries must explicitly opt out of the global filter using .IgnoreQueryFilters().

Notification audit pattern


Notification workflows are tracked separately from device commands using NotificationIncidents, NotificationAttempts, and EscalationEvents. This keeps the command audit trail clean and provides independent retry/escalation tracking for notifications.


---

Example API Surface

Device endpoints

- GET /api/devices?siteId=...&status=...&page=...&pageSize=...
- GET /api/devices/{deviceId}
- GET /api/devices/{deviceId}/state
- GET /api/devices/{deviceId}/telemetry?startUtc=...&endUtc=...&pageSize=...&cursor=...
- GET /api/devices/{deviceId}/alarms?status=...&page=...&pageSize=...

Configuration endpoints

- PATCH /api/devices/{deviceId}/desired-properties

Command endpoints (Sentinel device only — not pump hardware)

All command endpoints are asynchronous. They create a CommandLog record, return 202 Accepted with the command ID, and execute the command in the background. Clients poll for completion or subscribe to status updates.

- POST /api/devices/{deviceId}/commands/reboot → 202 Accepted + { commandId, status: "Pending" }
- POST /api/devices/{deviceId}/commands/ping → 202 Accepted + { commandId, status: "Pending" }
- POST /api/devices/{deviceId}/commands/capture-snapshot → 202 Accepted + { commandId, status: "Pending" }
- POST /api/devices/{deviceId}/commands/clear-fault → 202 Accepted + { commandId, status: "Pending" }
- GET /api/devices/{deviceId}/commands/{commandId} → 200 OK + { commandId, status, response, ... }

Manufacturing endpoints (internal admin only)

- POST /api/manufacturing/batches

Provisioning endpoints

- POST /api/dps/allocate (DPS webhook)

Site and customer endpoints

- GET /api/sites?customerId=...&page=...&pageSize=...
- POST /api/sites
- GET /api/sites/{siteId}/devices
- GET /api/customers?companyId=...&page=...&pageSize=...
- GET /api/companies?page=...&pageSize=...

Alarm endpoints

- GET /api/alarms?deviceId=...&status=...&alarmType=...&page=...&pageSize=...
- POST /api/alarms/{alarmId}/acknowledge
- POST /api/alarms/{alarmId}/clear — requires { reason: "..." } in request body
- GET /api/alarms/{alarmId}/events — returns AlarmEvents audit trail

Lead endpoints

- GET /api/leads?status=...&page=...&pageSize=...
- PATCH /api/leads/{leadId}/status

Pagination strategy

The API uses two pagination approaches based on data characteristics:

- Entity list endpoints (devices, sites, customers, companies, alarms, leads) use Gridify for filtering, sorting, and offset-based pagination. These tables are small enough that offset pagination performs well, and Gridify provides a consistent query string contract for the frontend.
- Time-series history endpoints (telemetry history, alarm events) use cursor-based pagination with opaque cursor tokens. This is necessary because these tables can grow to hundreds of millions of rows, where SQL OFFSET scans degrade significantly. Cursor-based pagination provides consistent performance regardless of page depth.

Gridify query string examples:

	GET /api/devices?filter=status=Active&orderBy=createdAt desc&page=1&pageSize=25
	GET /api/sites?filter=customerId=5&orderBy=name&page=1&pageSize=50

Cursor-based query string examples:

	GET /api/devices/{deviceId}/telemetry?startUtc=2026-01-01&endUtc=2026-04-01&pageSize=100
	GET /api/devices/{deviceId}/telemetry?pageSize=100&cursor=eyJ0cyI6IjIwMjYtMDMtMTVUMTI6MDA6MDBaIn0


---

Suggested Initial Implementation Plan

Phase 1: Foundation

- create Azure resources (IoT Hub, DPS, Event Hub, Service Bus topic, Key Vault, Azure SQL)

- create .NET solution structure

- connect ingestion worker to IoT Hub endpoint with batch checkpointing (N messages OR T seconds)

- implement message envelope validation (messageId, timestampUtc, schemaVersion required)

- implement deduplication using (device_id, message_id)

- implement state ordering using last_telemetry_timestamp_utc comparison

- persist telemetry to LatestDeviceState and TelemetryHistory (fully typed columns)

- implement FailedIngressMessages table for poison message handling

- expose basic read API

Phase 2: Device lifecycle and provisioning

- ~~implement DPS allocation webhook~~ ✅ DONE

- ~~implement manufacturing batch service~~ ✅ DONE

- scaffold ASP.NET Core Identity with ApplicationUser entity (IdentityDbContext, UserRole, CompanyId/CustomerId links)

- add custom Result<T> type in Domain for domain error handling

- align DeviceStatus enum with design doc: Manufactured → Unprovisioned → Assigned → Active → Decommissioned

- add device status state machine with validated transitions using Result<T>

- add tech assignment flow:
  - claim device by serial number
  - assign to existing Site or create new Site (tech-only)
  - company techs scoped to their org's customers/sites
  - create DeviceAssignment record with full audit fields (assigned_by_user_id)
  - update device twin with site timezone on assignment

- update ingestion worker to transition Assigned → Active on first telemetry

- implement async command pattern with CommandLog (POST → 202 Accepted → background execution → poll for status)

Phase 3: Multi-tenant model

- ~~add Company and Customer entities~~ ✅ DONE (entities exist)

- implement polymorphic tenant isolation (Company for plumbing co model, Customer for homeowner model)

- add EF Core global query filters for tenant scoping as defense-in-depth

- implement tenant-scoped data access (company techs see only their org)

- add Subscription management and Stripe integration

- add JWT authentication middleware and role-based authorization policies

- enforce authorization on all API endpoints with tenant context resolution

Phase 4: Alarms and notifications

- route high water alarm messages to Service Bus topic

- create alarm processor with Alarms + AlarmEvents split model

- implement alarm lifecycle: Active → Acknowledged → Suppressed → Resolved

- implement suppress-after-manual-clear rule (reason required, re-arm on condition clear)

- implement offline detection with configurable thresholds and DeviceOffline alarm type

- implement notification workflow: NotificationIncidents → NotificationAttempts → EscalationEvents

- add lead creation when subscriptions lapse

Phase 5: Frontend support

- integrate Gridify for entity list endpoint filtering/sorting/pagination

- implement cursor-based pagination for telemetry history and alarm events

- optimize read models for each app persona (consumer, company, internal)

- support dashboard queries against TelemetryHistory typed columns

Phase 6: Production hardening

- monitoring and alerting (ingestion throughput, checkpoint lag, poison message rate)

- role-based access enforcement audit

- telemetry history tiered storage (hot SQL 90 days, cold blob archive)

- load testing against documented scale targets (5K devices, 1.44M messages/day)

- backup/restore planning

- maintenance window support for offline detection suppression


---

Risks and Design Notes

Risk: Using IoT Hub as the app database


Mitigation: persist all app-facing data in a dedicated database.

Risk: Tight coupling of ingestion and API


Mitigation: isolate telemetry processing in a worker service.

Risk: Hardcoded provisioning approach


Mitigation: adopt DPS from the start.

Risk: Unbounded telemetry storage growth


Mitigation: telemetry history is retained intentionally for ML training. Define a tiered storage strategy (hot SQL storage for recent data, cold blob archive for older records) and implement it before data volumes grow significantly.

Risk: Overuse of direct methods


Mitigation: use desired properties for persistent configuration; use direct methods only for immediate one-time actions on the Sentinel device.

Risk: Tenant data isolation


Mitigation: enforce tenant-scoped queries at the application layer from the beginning. Homeowners managed by plumbing companies must never be able to query data outside their tenant scope.

Risk: Lead management gaps when subscriptions lapse


Mitigation: automate lead creation when a subscription transitions to Cancelled or Suspended so no installed devices are orphaned without a lead record.


---

Recommended Final Architecture


For this product, the recommended architecture is:


- Sentinel devices connect to Azure IoT Hub via DPS (symmetric key initially, with documented X.509 migration path)

- devices send read-only telemetry from the grinder pump control panel to IoT Hub

- a .NET worker consumes telemetry from the Event Hubs-compatible endpoint with batch checkpointing

- alarms are routed to a Service Bus topic for immediate processing, with independent subscriptions for alarm processing, notification dispatch, and audit logging

- processed data is stored in Azure SQL with fully typed telemetry columns

- an ASP.NET Core API exposes device, alarm, and telemetry data with tenant-scoped authorization

- three React applications (internal/tech, company, consumer) talk only to the API

- device configuration is managed through IoT Hub twins

- remote actions on the Sentinel device are handled asynchronously through direct methods with CommandLog audit trail

- failed/unparseable messages are stored in a FailedIngressMessages table (durable poison-message store)

- historical telemetry is retained for future ML model training with tiered hot/cold storage

- multi-tenant isolation is enforced at the data layer using EF Core global query filters

This architecture gives a clean separation of concerns and a good path to scale without redesigning the system later.


---

Summary


Azure IoT Hub is the secure and scalable device communication layer. The Sentinel device is a read-only monitor that taps into existing grinder pump control panels — it does not control any pump hardware. The .NET backend is split into:


- an ingestion service for telemetry processing and alarm detection

- an API service for business logic, multi-tenant data access, and UI support

The platform serves two tenant types (independent homeowners and plumbing companies) with role-appropriate apps. All telemetry history is retained for future ML model training.

This provides:


- better scale

- cleaner design

- easier operations

- stronger security

- a clear multi-tenant data model

- a better long-term path as the number of devices grows