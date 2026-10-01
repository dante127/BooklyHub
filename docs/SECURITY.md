# BooklyHub — Security & Authorization Architecture

## 1. Authentication Engine

BooklyHub implements a secure token-based authentication architecture combining short-lived JSON Web Tokens (JWT) with database-backed rotating refresh tokens:

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant Auth as AuthController
    participant Hasher as PasswordHasher (PBKDF2)
    participant JWT as JwtTokenGenerator
    participant DB as SQL Server (RefreshTokens)

    User->>Auth: POST /api/v1/auth/login (email, password)
    Auth->>Hasher: Verify password against stored hash & salt
    Hasher-->>Auth: Verified
    Auth->>JWT: Generate Access Token (60 mins expiry)
    Auth->>DB: Store cryptographically secure Refresh Token (30 days expiry)
    Auth-->>User: Returns { accessToken, refreshToken, userProfile }

    Note over User,Auth: Subsequent Authenticated Requests
    User->>Auth: Request with Bearer AccessToken
    Auth->>Auth: Validate Signature, Issuer, Audience, Expiry
```

### 1.1 Password Security (PBKDF2)
Passwords are never stored in plaintext or weak cryptographic hashes (MD5, SHA1). The `PasswordHasher` uses **PBKDF2 with HMAC-SHA256**:
- **Salt**: 16 bytes (128-bit) cryptographically random salt generated via `RandomNumberGenerator`.
- **Iterations**: 100,000 iterations.
- **Output Key**: 32 bytes (256-bit) subkey.
- **Constant-Time Verification**: `CryptographicOperations.FixedTimeEquals` prevents timing attacks.

### 1.2 Sliding Refresh Token Rotation
- Refresh tokens are hashed and stored with `ExpiresAtUtc`, `CreatedByIp`, and `RevokedAtUtc`.
- Each refresh token redemption generates a new replacement refresh token and revokes the old one, neutralizing stolen token replays.

---

## 2. Granular Permission-Based Authorization (RBAC)

Rather than hardcoding coarse roles in controller actions (`[Authorize(Roles = "Admin")]`), BooklyHub employs a granular **Permission-Based Authorization System**:

### 2.1 Role Hierarchy & Permissions
| Role | Capabilities |
| :--- | :--- |
| **`PlatformAdmin`** | System-wide configuration, tenant onboarding, cross-tenant maintenance. |
| **`TenantOwner`** | Full control over tenant settings, billing, locations, staff, and services. |
| **`TenantAdmin`** | Manage staff rosters, service pricing, reports, and appointments. |
| **`Manager`** | Schedule staff shifts, approve refunds, manage reviews. |
| **`Staff`** | View personal calendar, transition appointment statuses, add customer notes. |
| **`Receptionist`** | Check in customers, reschedule appointments, collect payments. |

### 2.2 Declarative Permission Attributes
Controllers use the `[HasPermission]` attribute:
```csharp
[HttpPost]
[HasPermission(Permissions.Appointments.Create)]
public async Task<IActionResult> BookAppointment(...)
```

The `PermissionAuthorizationHandler` checks the user's combined permissions loaded during authentication or dynamically checked against `RolePermissions`.

---

## 3. Rate Limiting & Defense-in-Depth

### 3.1 Rate Limiting Middleware
Configured in `Program.cs` through ASP.NET Core's built-in `RateLimiter`. Three fixed-window partitions, every one with `QueueLimit = 0` so an over-budget request is refused at once rather than held in a queue until the window turns:

- **General traffic**: 100 requests per minute per peer address.
- **Authentication endpoints** (`POST /api/v1/auth/login`, `POST /api/v1/auth/refresh-token`): 10 requests per minute per peer address, applied with `[EnableRateLimiting("auth")]` on top of the general budget.
- **Health probes** (`/health`, `/health/live`, `/health/ready`): 120 per minute instance-wide, in a partition of their own.

Every refusal answers with the standard problem envelope and a `Retry-After` header.

This section used to describe a limiter that did not exist. It claimed a *sliding* window for general traffic (the code has always been fixed-window) and a "strict fixed window limiter (10 login attempts per minute per IP)" for authentication (no such policy was ever registered — `grep` for `AddPolicy`/`EnableRateLimiting` returned nothing). Measured against the real pipeline before the fix: fifteen consecutive wrong-password logins were answered `401, 401, 401…`, ninety-five requests on any path left exactly five permits for the login box, and a sequential caller over budget was never refused at all — it sat in the 10-deep queue and was re-permitted seconds later, which also parked authenticated desk requests past five seconds with no status and answered `GET /health` with a `429`.

What these tiers deliberately do **not** protect against, stated so nobody reads them as more than they are:

- **The key is the address, not the account.** A limiter counts requests and cannot know whether one failed, so ten per minute is ten *attempts*, legitimate ones included. A clinic whose twenty staff sign in from one office address inside a minute will see the eleventh refused. The real defence against a targeted account is failure counting with backoff, which needs columns on `Users` and is tracked as `SEC-04`'s remaining half.
- **Behind a reverse proxy, the address is the proxy's.** Nothing in `src` calls `UseForwardedHeaders`, so partitioning on `Connection.RemoteIpAddress` collapses every user into a single bucket the moment a proxy sits in front. `docker-compose.yml` publishes `127.0.0.1:5000:8080`, so any remote client already reaches the instance through something; an operator choosing that shape must configure forwarded headers *and* trust the proxy that sets them.
- **Nothing records or locks out a failing account**, and the login response still distinguishes an unknown address from a deactivated one, which is an existence oracle. Both are `SEC-04`'s remaining halves, not this section's claim.


### 3.2 Automated Request Validation
All CQRS commands pass through MediatR's `ValidationBehavior<TRequest, TResponse>` backed by **FluentValidation**:
- Requests containing malformed inputs, past dates, or invalid email formats fail *before* database transactions or domain logic can execute.
- Prevents SQL injection and invalid state transitions at the gateway boundary.

### 3.3 Every Denial Answers in the Same Shape
A `401` from the JWT challenge, a `403` from the permission handler, a `404` for a path with no endpoint and a `429` from the limiter used to travel as a bare status code — no content type, no body — while anything thrown inside an action carried a problem document. The gap is not cosmetic: a client cannot show or log what it was refused for, and a refused request whose body is empty cannot be tied to a log line at all.

Both paths now write `application/problem+json` carrying `status`, `title`, `instance` and `correlationId` (the full field table is in `API.md` §1.1). Note what this did **not** involve: `ExceptionHandlingMiddleware` was measured to be the *outermost* boundary over authentication, tenant resolution, authorization and idempotency, so those requests were never outside its reach and moving the boundary would have changed nothing. The missing piece was a writer for the statuses that no action produces.
