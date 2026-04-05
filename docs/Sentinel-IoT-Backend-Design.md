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
	  "deviceId": "pump-00123",
	  "timestampUtc": "2026-04-05T00:00:00Z",
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

Fields:

- id (int, PK)
- device_id (FK → Devices)
- last_seen_at (datetime)
- panel_voltage (double, nullable)
- pump_current (double, nullable)
- high_water_alarm (bool, nullable)
- temperature_c (double, nullable)
- signal_rssi (int, nullable)
- runtime_seconds (int, nullable) — duration of the most recent pump cycle
- cycle_count (int, nullable) — cumulative lifetime pump cycle count
- updated_at (datetime)

TelemetryHistory


Stores time-series historical telemetry for all devices. Retained indefinitely for future ML model training and long-term trend analysis.

Fields:

- id
- device_id
- timestamp_utc
- panel_voltage
- pump_current
- high_water_alarm
- temperature_c
- signal_rssi
- runtime_seconds
- cycle_count
- firmware_version
- received_at

Alarms


Stores alarm events detected from device telemetry.

Fields:

- id
- device_id
- alarm_type
- severity
- started_at
- cleared_at
- is_active
- details_json

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

Fields:

- id (int, PK)
- device_id (FK → Devices)
- site_id (FK → Sites)
- assigned_at (datetime)
- assigned_by_user_id
- unassigned_at (datetime, nullable)
- unassigned_by_user_id (nullable)
- unassignment_reason (enum, nullable)

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
- Technician selects or creates a Site and assigns the device to it
- Device transitions Unprovisioned → Assigned
- Backend writes desired properties to the IoT Hub twin (telemetry interval, thresholds, etc.)
- A DeviceAssignment record is created linking the device to the site and customer

The device is now linked to a site, which belongs to a customer (homeowner or plumbing company account).


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

Scalability Considerations

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

Example API Surface

Device endpoints

- GET /api/devices
- GET /api/devices/{deviceId}
- GET /api/devices/{deviceId}/state
- GET /api/devices/{deviceId}/telemetry
- GET /api/devices/{deviceId}/alarms

Configuration endpoints

- PATCH /api/devices/{deviceId}/desired-properties

Command endpoints (Sentinel device only — not pump hardware)

- POST /api/devices/{deviceId}/commands/reboot
- POST /api/devices/{deviceId}/commands/ping
- POST /api/devices/{deviceId}/commands/capture-snapshot
- POST /api/devices/{deviceId}/commands/clear-fault

Manufacturing endpoints (internal admin only)

- POST /api/manufacturing/batches

Provisioning endpoints

- POST /api/dps/allocate (DPS webhook)

Site and customer endpoints

- GET /api/sites
- POST /api/sites
- GET /api/sites/{siteId}/devices
- GET /api/customers
- GET /api/companies

Lead endpoints

- GET /api/leads
- PATCH /api/leads/{leadId}/status


---

Suggested Initial Implementation Plan

Phase 1: Foundation

- create Azure resources (IoT Hub, DPS, Event Hub, Service Bus, Key Vault, SQL)

- create .NET solution structure

- connect ingestion worker to IoT Hub endpoint

- persist telemetry to LatestDeviceState and TelemetryHistory

- expose basic read API

Phase 2: Device lifecycle and provisioning

- implement DPS allocation webhook

- implement manufacturing batch service

- add device status state machine (Manufactured → Unprovisioned → Assigned → Active → Decommissioned)

- add tech assignment flow (claim device by serial number, create Site and DeviceAssignment)

- implement device twins with initial configuration

Phase 3: Multi-tenant model

- add Company and Customer entities

- implement tenant-scoped data access

- add Subscription management and Stripe integration

- add role-based access control for company admin vs. homeowner vs. internal admin

Phase 4: Alarms and notifications

- route high water alarm messages to Service Bus

- create alarm processor and Alarm records

- implement notification or escalation workflows

- add lead creation when subscriptions lapse

Phase 5: Frontend support

- optimize read models for each app persona (consumer, company, internal)

- add filtering, paging, and summaries

- support dashboard queries against TelemetryHistory

Phase 6: Production hardening

- monitoring and alerting

- role-based access enforcement

- telemetry history retention policy

- load testing

- backup/restore planning


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


- Sentinel devices connect to Azure IoT Hub via DPS

- devices send read-only telemetry from the grinder pump control panel to IoT Hub

- a .NET worker consumes telemetry from the Event Hubs-compatible endpoint

- alarms are routed to Service Bus for immediate processing and notification

- processed data is stored in Azure SQL

- an ASP.NET Core API exposes device, alarm, and telemetry data

- three React applications (internal/tech, company, consumer) talk only to the API

- device configuration is managed through IoT Hub twins

- remote actions on the Sentinel device are handled through direct methods

- historical telemetry is retained for future ML model training

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