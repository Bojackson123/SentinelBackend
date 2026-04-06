f# API Reference

All endpoints are served by `SentinelBackend.Api` on HTTPS. Requests require a valid JWT Bearer token unless noted otherwise.

## Authentication

### POST /api/auth/register
Create a new user account.

**Auth:** None  
**Request body:**
```json
{
  "email": "user@example.com",
  "password": "SecureP@ss1",
  "firstName": "Jane",
  "lastName": "Doe",
  "role": "CompanyTech",
  "companyId": 1,
  "customerId": null
}
```
**Response:** `200 OK` — `{ "id": "...", "email": "..." }`

### POST /api/auth/login
Authenticate and receive a JWT token.

**Auth:** None  
**Request body:**
```json
{
  "email": "user@example.com",
  "password": "SecureP@ss1"
}
```
**Response:** `200 OK` — `{ "token": "eyJ..." }`

Token contains claims: `sub` (user ID), `email`, `role`, `companyId`, `customerId`.  
Expires after 1 hour.

---

## Devices

### GET /api/devices
List devices visible to the current tenant.

**Auth:** AllAuthenticated  
**Query:** `page` (default 1), `pageSize` (default 25, max 100)  
**Response:** `{ total, page, pageSize, devices: [{ id, deviceId, serialNumber, hardwareRevision, firmwareVersion, status, provisionedAt, createdAt }] }`

### GET /api/devices/{deviceId}
Get device detail including latest state and connectivity.

**Auth:** AllAuthenticated  
**Response:** Device object with nested `latestState` and `connectivity` objects, or `404`.

### GET /api/devices/{deviceId}/state
Get the latest telemetry snapshot.

**Auth:** AllAuthenticated  
**Response:** `DeviceStateResponse` with all telemetry fields, or `404` if no state exists.

### GET /api/devices/{deviceId}/telemetry
Paginated telemetry history with cursor-based pagination.

**Auth:** AllAuthenticated  
**Query:** `after` (DateTime cursor), `pageSize` (default 50, max 200)  
**Response:** Array of telemetry records ordered by `timestampUtc` descending.

---

## Device Configuration (Phase 4)

### PATCH /api/devices/{deviceId}/desired-properties
Update IoT Hub twin desired properties for a device.

**Auth:** CompanyOrInternal  
**Request body:** Dictionary of property name → value
```json
{
  "telemetryIntervalSeconds": 30,
  "diagnosticsEnabled": true,
  "siteTimezone": "America/Chicago"
}
```
**Response:** `200 OK` — `{ "updated": ["telemetryIntervalSeconds", "diagnosticsEnabled", "siteTimezone"] }`  
**Error:** `502` if IoT Hub twin update fails.  
**Side effect:** Each property change is logged to `DesiredPropertyLogs` for audit.

---

## Commands (Phase 4)

### POST /api/devices/{deviceId}/commands/{commandType}
Submit an async command for background execution.

**Auth:** CompanyOrInternal  
**Valid command types:** `reboot`, `ping`, `capture-snapshot`, `run-self-test`, `sync-now`, `clear-fault`  
**Response:** `202 Accepted`
```json
{
  "commandId": 42,
  "commandType": "reboot",
  "status": "Pending",
  "requestedAt": "2026-04-05T12:00:00Z"
}
```
**Errors:**
- `400` — unsupported command type, or device is decommissioned
- `404` — device not found or not visible to tenant

The command is picked up by the `CommandExecutorWorker` background service, which invokes the IoT Hub direct method and updates the command status.

### GET /api/devices/{deviceId}/commands/{commandId}
Get the status of a specific command.

**Auth:** AllAuthenticated  
**Response:**
```json
{
  "commandId": 42,
  "commandType": "reboot",
  "status": "Succeeded",
  "requestedAt": "2026-04-05T12:00:00Z",
  "requestedByUserId": "user-id",
  "sentAt": "2026-04-05T12:00:05Z",
  "completedAt": "2026-04-05T12:00:06Z",
  "responseJson": "{\"result\": \"ok\"}",
  "errorMessage": null
}
```

### GET /api/devices/{deviceId}/commands
List all commands for a device (paginated).

**Auth:** AllAuthenticated  
**Query:** `page`, `pageSize`

---

## Assignments

### POST /api/devices/{serialNumber}/assign
Assign a device to a site.

**Auth:** CompanyOrInternal (homeowners blocked)  
**Request body:**
```json
{ "siteId": 5 }
```
**Response:** `200 OK` — `{ assignmentId, serialNumber, siteId }`  
**Errors:**
- `404` — device not found
- `400` — device is decommissioned or already active
- `409` — device already has an active assignment
- `403` — company user trying to assign to another company's site

**Side effects:**
- Creates `DeviceAssignment` record
- Transitions device status: Manufactured/Unprovisioned → Assigned
- Updates IoT Hub twin desired properties with `siteTimezone` and `siteId`

### POST /api/devices/{serialNumber}/unassign
Remove the active assignment from a device.

**Auth:** CompanyOrInternal (homeowners blocked)  
**Request body:**
```json
{ "reason": "Deinstallation" }
```
**Response:** `200 OK`

---

## Sites

### POST /api/sites
Create a new site under a customer.

**Auth:** CompanyOrInternal  
**Request body:**
```json
{
  "customerId": 1,
  "name": "Jane's Residence",
  "addressLine1": "456 Oak Ave",
  "city": "Austin",
  "state": "TX",
  "postalCode": "73301",
  "country": "US",
  "timezone": "America/Chicago"
}
```
**Response:** `201 Created` — `{ id, name }`

### GET /api/sites/{siteId}
Get site details (tenant-scoped).

**Auth:** CompanyOrInternal  
**Response:** Site object with address and timezone.

---

## Alarms

### GET /api/alarms
List alarms visible to the current tenant.

**Auth:** AllAuthenticated  
**Query:** `status` (filter by alarm status), `page`, `pageSize`

### POST /api/alarms/{alarmId}/acknowledge
Transition an active alarm to acknowledged.

**Auth:** AllAuthenticated  
**Error:** `400` if alarm is not in Active status.

### POST /api/alarms/{alarmId}/suppress
Suppress an alarm with a reason.

**Auth:** AllAuthenticated  
**Request body:** `{ "reason": "Scheduled maintenance" }`  
**Error:** `400` if alarm is already Resolved or Suppressed.

### GET /api/alarms/{alarmId}/events
Get the event history for an alarm (ordered chronologically).

---

## Manufacturing

### POST /api/manufacturing/batches
Generate a batch of device records for manufacturing.

**Auth:** InternalAdmin only  
**Request body:**
```json
{ "quantity": 100, "hardwareRevision": "v1.2" }
```
**Response:** CSV file download containing serial numbers, derived symmetric keys, hardware revision, and manufacturing timestamp.

**Side effects:** Creates `Device` records with status `Manufactured` and sequential serial numbers (format: `GP-YYYYMM-NNNNN`).

---

## DPS Webhook

### POST /api/dps/allocate
Custom allocation webhook called by Azure Device Provisioning Service.

**Auth:** Query parameter `code` must match `DpsWebhookSecret`  
**Request body:** `DpsAllocationRequest` from DPS  
**Response:** `DpsAllocationResponse` with IoT Hub assignment  
**Side effects:**
- Validates device exists by serial number (registration ID)
- Transitions device: Manufactured → Unprovisioned
- Sets `DeviceId` and `ProvisionedAt`
- Re-provision of already-provisioned device does not reset status
