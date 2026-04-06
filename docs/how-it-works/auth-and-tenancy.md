# Authentication & Multi-Tenancy

## Authentication

### JWT Bearer Tokens
The API uses symmetric HMAC-SHA256 JWT tokens issued by the `AuthController`.

**Token generation flow:**
1. `POST /api/auth/register` — creates an ASP.NET Core Identity user with a role claim
2. `POST /api/auth/login` — validates credentials, generates a JWT with:
   - `sub` — user ID
   - `email`
   - `role` — single role from `UserRole` enum
   - `companyId` — if the user belongs to a company
   - `customerId` — if the user is a homeowner
3. Token expires after 1 hour

**Configuration** (in `appsettings.json` / Key Vault):
- `JwtSigningKey` — HMAC-SHA256 secret (minimum 32 bytes)
- `JwtIssuer` / `JwtAudience` — validation parameters

### Identity
`ApplicationUser` extends `IdentityUser` with `CompanyId`, `CustomerId`, `FirstName`, `LastName`. The `SentinelDbContext` inherits from `IdentityDbContext<ApplicationUser>`.

---

## Roles

| Role | Enum Value | Scope | Typical Use |
|------|-----------|-------|-------------|
| InternalAdmin | `InternalAdmin` | Global | Sentinel platform operators — full access |
| InternalTech | `InternalTech` | Global | Sentinel field technicians — read/write devices |
| CompanyAdmin | `CompanyAdmin` | Company | Installer company administrators |
| CompanyTech | `CompanyTech` | Company | Installer company field technicians |
| HomeownerViewer | `HomeownerViewer` | Customer | End-customer — read-only access to their devices |

---

## Authorization Policies

Defined in `Program.cs` and applied via `[Authorize(Policy = "...")]` on controllers/actions:

| Policy | Allowed Roles |
|--------|--------------|
| `InternalOnly` | InternalAdmin, InternalTech |
| `CompanyOrInternal` | InternalAdmin, InternalTech, CompanyAdmin, CompanyTech |
| `AllAuthenticated` | Any authenticated user (all 5 roles) |

**Special case:** The DPS webhook endpoint (`POST /api/dps/allocate`) uses a query-string `code` parameter validated against `DpsWebhookSecret` instead of JWT auth.

---

## Multi-Tenancy

### ITenantContext Interface

```csharp
public interface ITenantContext
{
    string UserId { get; }
    int? CompanyId { get; }
    int? CustomerId { get; }
    bool IsInternal { get; }
    bool IsCompanyUser { get; }
    bool IsHomeowner { get; }

    IQueryable<T> ApplyScope<T>(IQueryable<T> query) where T : class;
}
```

### HttpTenantContext (production)
Registered as `Scoped`. Extracts tenant identity from `HttpContext.User` claims:

- **IsInternal** — `true` if role is `InternalAdmin` or `InternalTech`
- **IsCompanyUser** — `true` if role is `CompanyAdmin` or `CompanyTech`
- **IsHomeowner** — `true` if role is `HomeownerViewer`
- **CompanyId / CustomerId** — parsed from JWT claims

### FakeTenantContext (tests)
Manually configured per test. Provides factory methods:
- `FakeTenantContext.Internal()` — internal user, no scoping
- `FakeTenantContext.ForCompany(companyId)` — company-scoped
- `FakeTenantContext.ForHomeowner(customerId)` — customer-scoped

### ApplyScope Logic

`ApplyScope<T>()` adds a `Where` filter to any `IQueryable<T>`. The scoping rules differ by entity type:

| Entity | Internal | Company User | Homeowner |
|--------|----------|-------------|-----------|
| **Device** | No filter | Devices with an active assignment at a site owned by a customer in the user's company | Devices with an active assignment at a site owned by the user's customer |
| **Alarm** | No filter | Alarms on devices with an active assignment → site → customer → company match | Alarms on devices with an active assignment → site → customer match |
| **Site** | No filter | Sites where `Customer.CompanyId == companyId` | Sites where `CustomerId == customerId` |
| **TelemetryHistory** | No filter | Records where `CompanyId == companyId` (denormalised snapshot) | Records where `CustomerId == customerId` |
| **Other types** | Passthrough | Passthrough | Passthrough |

**Key design decisions:**
- Device scoping uses **active assignments** (`UnassignedAt == null`) — a company only sees devices currently assigned to their customers
- TelemetryHistory uses **denormalised ownership columns** stamped at ingest time — no joins needed, historically accurate
- If a non-internal user has no CompanyId/CustomerId, `ApplyScope` returns `query.Where(_ => false)` — zero results, not an error
- Internal users see everything — no filter applied

### Usage Pattern
Controllers call `_tenantContext.ApplyScope(query)` on every data-access query:

```csharp
var devices = await _tenantContext.ApplyScope(db.Devices)
    .Include(d => d.LatestState)
    .ToListAsync();
```

This ensures tenant isolation is enforced uniformly at the data-access layer, not sprinkled across individual endpoints.
