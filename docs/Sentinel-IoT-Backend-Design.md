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

- hardware_revision

- firmware_version

- installed_at

- status

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

- cycle_count

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

- alarm_type

- severity

- started_at

- cleared_at

- is_active

- details_json

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

Customers


Stores tenant/customer information.

Fields:


- id

- name

- contact_info

- created_at


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

Command endpoints

- POST /api/devices/{deviceId}/commands/reboot

- POST /api/devices/{deviceId}/commands/ping

- POST /api/devices/{deviceId}/commands/clear-fault

Administrative endpoints

- POST /api/devices/register

- POST /api/devices/provisioning/enrollment

- GET /api/sites

- GET /api/customers


---

Suggested Initial Implementation Plan

Phase 1: Foundation

- create Azure resources

- create .NET solution structure

- connect ingestion worker to IoT Hub endpoint

- persist telemetry to database

- expose basic read API

Phase 2: Device management

- add DPS support

- add device registration workflows

- implement twins

- add direct method endpoints

Phase 3: Alarms and workflows

- route alarm messages to Service Bus

- create alarm processor

- implement notification or escalation workflows

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

- alarms are optionally routed to Service Bus for immediate processing

- processed data is stored in SQL or PostgreSQL

- an ASP.NET Core API exposes device, alarm, and telemetry data

- the future React app talks only to the API

- device configuration is managed through twins

- remote actions are handled through direct methods

This architecture gives a clean separation of concerns and a good path to scale without redesigning the system later.


---

Summary


Azure IoT Hub should be used as the secure and scalable device communication layer, not as the full backend itself. The .NET backend should be split into:


- an ingestion service for telemetry processing

- an API service for business logic and UI support

This provides:


- better scale

- cleaner design

- easier operations

- stronger security

- a better long-term path as the number of devices grows