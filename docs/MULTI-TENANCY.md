# BooklyHub — Multi-Tenancy & Data Isolation

## 1. Isolation Model

BooklyHub utilizes a **Shared Database, Shared Schema** multi-tenancy model with a strict logical tenant boundary enforced at the database abstraction layer:

```mermaid
graph TD
    subgraph HTTP Requests
        ReqA[Tenant A Request: tenant_id = AAA]
        ReqB[Tenant B Request: tenant_id = BBB]
    end

    subgraph ASP.NET Core Pipeline
        TRM[TenantResolutionMiddleware]
        TC[ITenantContext Provider]
    end

    subgraph Data Access Layer
        GQF[EF Core Global Query Filter]
        DB[(Shared SQL Server Database)]
    end

    ReqA --> TRM
    ReqB --> TRM
    TRM --> TC
    TC --> GQF
    GQF -->|WHERE TenantId = AAA| DB
    GQF -->|WHERE TenantId = BBB| DB
```

---

## 2. Tenant Context Resolution (`TenantResolutionMiddleware`)

The `TenantResolutionMiddleware` runs at the front of the HTTP pipeline and establishes the active `ITenantContext`:

1. **Authenticated Users**: The middleware inspects the JWT claims for `tenant_id`. For authenticated staff or customers, the JWT claim is the **cryptographic source of truth** and cannot be spoofed by custom request headers.
2. **Public / Anonymous Endpoints**: For public endpoints (e.g. guest booking portals or availability checks), the tenant is resolved via the `X-Tenant-ID` request header or subdomain routing.
3. **Tenant Existence & Status Check**: The middleware confirms that the resolved `TenantId` exists in the database and `IsActive == true`. If invalid or inactive, the request is rejected with `400 Bad Request` or `403 Forbidden`.

---

## 3. Dynamic EF Core Global Query Filters

All entities implementing `ITenantEntity` (`Appointments`, `Customers`, `Staff`, `Services`, `Payments`, etc.) have their database queries automatically parameterized:

```csharp
// Dynamic filter generated in ApplicationDbContext.OnModelCreating:
(Guid?)e.TenantId == CurrentTenantId || IsPlatformAdmin
```

### Key Technical Safeguards:
- **Null Safety**: The filter is built with `Expression.Convert(tenantIdProp, typeof(Guid?)) == currentTenantIdProp`, preventing runtime `InvalidOperationException: Nullable object must have a value` when filtering.
- **SQL Parameterization**: EF Core parameterizes `CurrentTenantId` as `@__CurrentTenantId_0` in SQL Server, ensuring optimal query plan caching and query reuse.
- **Platform Admin Bypass**: Platform administrators (`IsPlatformAdmin == true`) can query across tenants for maintenance, invoicing, and support.

---

## 4. Elimination of Cross-Tenant IDOR (Insecure Direct Object References)

In traditional web applications, querying an entity by its primary key (e.g. `GET /api/v1/customers/{id}`) often causes security leaks if the developer forgets to append `AND TenantId = currentTenant`.

In BooklyHub:
- Even if an attacker in Tenant B guesses or extracts a valid `CustomerId` belonging to Tenant A, the Global Query Filter automatically injects `WHERE TenantId = 'Tenant-B-Guid'`.
- SQL Server returns 0 matching rows.
- The API handler triggers a `NotFoundException` (HTTP 404), completely concealing Tenant A's data.
- This behavior is continuously verified in `tests/BooklyHub.IntegrationTests/Security/CrossTenantSecurityTests.cs`.

---

## 5. Write-Path Enforcement and Host Workers

The global query filter only constrains reads, so two further rules close the loop:

**Cross-tenant write invariant (`ApplicationDbContext.SaveChangesAsync`).**
Any `Added`, `Modified` or `Deleted` entity whose `TenantId` differs from the bound tenant throws
`CrossTenantAccessViolationException` (HTTP 403). It applies only when a tenant is bound and the caller is
not a platform administrator, so request handlers get a second net behind their own validation while
seeding and host work still operate across tenants.

**System scope for background workers (`SystemTenantScope.CreateSystemScope`).**
Workers resolve `IServiceScopeFactory.CreateScope()`, which carries an **unbound** `ITenantContext`:
`TenantId == null` and `IsPlatformAdmin == false`, so the filter matches nothing and a sweep would silently
process zero rows. All three host workers — `OutboxProcessorBackgroundService`,
`AppointmentReminderBackgroundService` and `AppointmentNoShowBackgroundService` — therefore open a system
scope, which binds `SetTenant(Guid.Empty, isPlatformAdmin: true)` for that scope only.

Because that scope is platform-admin, the cross-tenant write invariant above is not what limits it: the
no-show sweep is the one worker that writes, and it scopes itself with an explicit
`a.TenantId == tenantId` predicate per tenant, under a per-tenant `sp_getapplock`.

- `IgnoreQueryFilters()` was rejected as the fix: it also drops the `!IsDeleted` soft-delete filter and has to be remembered per query.
- Binding the scope to one tenant was rejected too: `OutboxMessage` carries no `TenantId`, and the reminder sweep is cross-tenant by design.
- Request-path code must never call `CreateSystemScope()`; the middleware owns tenant binding for HTTP calls.

Verified by `tests/BooklyHub.IntegrationTests/Notifications/NotificationDeliveryTests.cs`, which fails when
either worker is switched back to a plain scope (zero rows read, and the outbox message is marked processed
without any email being sent).
