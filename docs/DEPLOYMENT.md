# Deployment and handover

What a client deployment of BooklyHub needs, in the order an operator meets it, with every number below measured
against this repository rather than copied from a specification. Where a setting has a cost, the cost is stated;
where the build cannot do something a client would expect, §10 says so instead of leaving it to be discovered.

This file is the handover document. The behaviour of individual routes is in [`API.md`](API.md), the security model
in [`SECURITY.md`](SECURITY.md), and the schema in [`DATABASE.md`](DATABASE.md).

## 1. Topology

`docker-compose.yml` starts four services. Only three of them are long-running.

| Service | Image | Listens | Published | Restarts |
| :--- | :--- | :--- | :--- | :--- |
| `sqlserver` | `mssql/server:2022-latest` | 1433 | `127.0.0.1:1433` | `unless-stopped` |
| `redis` | `redis:7-alpine` | 6379 | `127.0.0.1:6379` | `unless-stopped` |
| `migrator` | built from `Dockerfile`, command `--migrate-only` | — | none | `"no"` (one-shot; must exit 0) |
| `api` | built from the same `Dockerfile` | `http://+:8080` | `127.0.0.1:5000` | `unless-stopped` |

Three properties of the `api` image an operator has to know before putting it behind anything:

- **It is not root.** The runtime stage creates `appuser` with a fixed uid/gid 10001 (`useradd`, not `adduser` —
  the latter does not exist in `mcr.microsoft.com/dotnet/aspnet:10.0`, which is why this image had never built;
  see `AUDIT-STATUS.md` §2.16). A Kubernetes `securityContext`, a mounted volume's ownership, or a filesystem
  policy that expects uid 0 will all have to be told about 10001.
- **It speaks plain HTTP on one port.** `EXPOSE 8080` and nothing else; it holds no certificate and listens on no
  TLS port. TLS terminates in front of it, and §3 is how the container learns what is in front of it.
- **It has an HTTP client only because the healthcheck needs one.** `curl` is installed in the runtime stage; the
  base image has none (`command -v curl` finds nothing, measured), so a probe that wants to ask a real HTTP
  question needs it. Nothing else in the container is installed beyond the framework.

`api` depends on `migrator: service_completed_successfully` and `redis: service_healthy`. A failed migration
therefore stops the API from starting rather than letting it serve against an empty schema — the migration and
seeding failures are deliberately unhandled in `Program.cs:205-228` so the process exits.

Ports are published on `127.0.0.1` only: this stack is meant to sit behind a reverse proxy, and a host that
forwards 443 to `127.0.0.1:5000` is the intended shape. Publishing `1433` or `6379` on a public interface is not
part of any deployment this document supports.

## 2. Settings

Compose reads an `.env` file (`cp .env.example .env`), and the `:?` guards mean `docker compose config` refuses to
render a deployment that leaves one unset — that check happens client-side, before any container starts.

| Variable (compose) | Key the process reads | Required | What it does, and what it costs |
| :--- | :--- | :--- | :--- |
| `MSSQL_SA_PASSWORD` | `ConnectionStrings:DefaultConnection` (assembled in compose) | **yes** | Also the SQL Server `sa` password. Nothing here connects as `sa` for a request path; the connection string is used by the app and the migrator both. |
| — | `ConnectionStrings:Redis` | **yes in Production** | An empty value aborts startup (`DependencyInjection.cs`). A Redis that dies *later* changes no client-visible answer, which is why it is not a health check (§5). |
| `JWT_SECRET` | `Jwt:Secret` | **yes** | HMAC signing key, at least 32 bytes, no fallback. Rotating it invalidates every live token and every live refresh chain at once — there is no key versioning, so plan the rotation for a moment with no sessions worth keeping, or accept that users are signed out. |
| — | `Jwt:Issuer`, `Jwt:Audience` | **yes** | Must match what the deployment mints; defaults `BooklyHub` / `BooklyHubClients` ship in `appsettings.json`. |
| — | `Jwt:ExpirationMinutes` | no (default 60) | Access-token lifetime. Permissions are frozen inside the token, so this is also how long a revoked permission keeps working (§`SECURITY.md` §3.2). |
| `PAYMENTS_PROVIDER` | `Payments:Provider` | **yes in Production** | `None` (the default) charges nothing and answers every charge `422 PaymentFailed` naming its own configuration key, writing no ledger row. `Simulated` is the only other value and **Production refuses it** — it answers `Paid` with a synthetic `txn_sim_*` id. A real gateway implements the provider interface and names it here. |
| `NOTIFICATIONS_PROVIDER` | `Notifications:Provider` | **yes in Production** | `None` sends nothing and every send throws, which leaves a reminder **unrecorded** and an outbox message **unprocessed with its reason in `Error`** — that is the point: a completed task is the only thing a caller reads as "it left". `Simulated` logs `[... DISPATCHED]` and returns success, and Production refuses it. |
| `SEED_ADMIN_EMAIL`, `SEED_ADMIN_PASSWORD` | `Seed:AdminEmail`, `Seed:AdminPassword` | **yes, on the first seeding run** | The platform administrator. An empty email refuses the run before it writes anything, naming the key (`ProductionSeedTests`). |
| `SEED_DEMO_TENANTS` | `Seed:DemoTenants` | no (default `false`) | `false` seeds roles, permissions and the administrator only — **zero tenants**. `true` adds the scripted demo: Apex Dental, Luxe Salon, Pulse Fitness with their staff and invented patients. Read on the **first** run only; on a database that already has reference rows, flipping it later is a logged no-op (§4). |
| `ALLOWED_HOSTS` | `AllowedHosts` | no (default `*`) | The `Host` values the site answers on. `*` admits every host and installs no narrowing. **`*` is the safe default only in the sense that it is the old behaviour**: it answers any `Host` header, so name your real domain in production. Spellings the matcher cannot honour (a space inside an entry, `*a.test`, `*` beside a name) **refuse to start** rather than lock out traffic — §3 has the measured table. |
| `HTTPS_REDIRECT_PORT` | `HttpsRedirection:Port`, or `ASPNETCORE_HTTPS_PORT` | no (default unset) | Left unset, no redirect middleware is installed. Set, every plain-HTTP request is redirected to that port — **including `/health/live`**, which then answers `307` (§3). A non-port value (hex, a sign, `4,43`, out of range) refuses startup, because a redirect asked for and silently not installed is a deployment that believes it redirects. |
| `FORWARDING_KNOWN_PROXIES` | `Forwarding:KnownProxies` | no (default unset) | The **address** of the proxy that writes `X-Forwarded-For`. Without it every caller behind one proxy shares the global 100/min and the auth 10/min buckets, so one anonymous caller can lock the sign-in door for the whole deployment. A host *name* is refused at startup; give the address the connections actually arrive from. `Forwarding:KnownNetworks` takes CIDR blocks for a proxy whose address moves. |
| `HEALTH_PROBE_HOST` | — (compose only) | no | Overrides the `Host` the container's own healthcheck asks with. Only needed when the derivation in §3's second half does not fit the deployment's naming. |
| — | `AutoMigrateAndSeed` | no (default `false`) | `true` migrates and seeds on every boot. In compose this stays `false` and the one-shot `migrator` does the work instead, because an auto-migrating replica is a schema race between instances. |
| — | `Serilog:MinimumLevel:*` | no | `Information` by default. Refusals and dispatch failures are `Warning`; nothing at `Information` carries a credential or a patient's contact details (§`SECURITY.md` §3.4, `SEC-09`). |

There are **no fallback secrets and no default connection strings** in the build: a Production host that is missing
one of the required keys aborts startup naming it. That is deliberate (`AUDIT-STATUS.md` §1 `SEC-01`) and it is the
reason a compose file with an unset variable fails at `docker compose config` rather than at 3 a.m.

## 3. The serving surface, measured

`AllowedHosts` and the redirect port are read **before** `builder.Build()`, because they decide whether a
middleware is in the pipeline at all. That has a testing consequence an operator should know about: no
integration fixture can change them (`ConfigureTestConfiguration` lands after the read), so their evidence is a
live host started with those environment variables. The matrix below is that host, run twice — once on this
build, once on `5ae2e74` in a throwaway worktree as the control. `/health/live` is the probe URL; `200` means the
route answered, `400` means the host filter refused before routing, and a named redirect port turns each `200`
into `307`.

| `AllowedHosts` | this build | `5ae2e74` (before) |
| :--- | :--- | :--- |
| unset, or `*` | no filter — `a.test` `200`, `evil.test` `200` | same |
| `a.test` | `a.test` `200`, `evil.test` `400` | same |
| `a.test;b.test` | both `200`, foreign `400` | same |
| `a.test, b.test` | both `200`, foreign `400` | **`400` to all three, including the two it names** |
| `*.a.test` | `x.a.test` `200`, `a.test` `400`, foreign `400` | same |
| `a test`, `*a.test`, `a.test*` | **refuses to start**, naming the entry and the key | starts, then answers `400` to everything (`a test`) or to the host the wildcard was meant to admit (`*a.test`) |
| `a.test; *` | **refuses to start** — the two declarations contradict each other | starts; `a.test` `200`, foreign `400`, i.e. the `*` silently did nothing |
| `AllowedHosts__0=a.test`, `AllowedHosts__1=b.test` | no narrowing, foreign `200` | same |

Four things that table buys, each of which is a deployment note rather than a code note:

1. **The host filter is not something this repository installs.** ASP.NET Core's minimal hosting puts
   `HostFilteringMiddleware` in and binds `AllowedHosts` by itself. The audit's remaining `SEC-13` claim — that the
   key was dead config because `UseHostFiltering()` had never been called — measured false (§`AUDIT-STATUS.md` §1).
2. **A comma is the one spelling that destroys the defence silently.** The host splits the key on `;` and trims
   nothing, so `"a.test, b.test"` is one unmatchable pattern and the site refuses **its own** names with no startup
   error and nothing in the log. This build splits on both delimiters, so either spelling works.
3. **The indexed environment spelling is read by nobody.** `AllowedHosts__0` / `__1` produces a configuration
   *section*, and neither the host's own binding nor this build reads one. Write the delimited string.
4. **`*` is not a Host header.** Sending `Host: *` answered `400` even with no filter installed, which is why the
   healthcheck below derives a name instead of passing the value through.

The compose healthcheck therefore derives the probe `Host` from the same key it is testing, and the derivation has
to survive every spelling §3 accepts, because each one otherwise marks a **working** container unhealthy (all
measured against the running stack):

| `ALLOWED_HOSTS` | the probe asks with | why |
| :--- | :--- | :--- |
| unset or empty | `localhost` | no filter, so any `Host` answers |
| `*` | `localhost` | a bare `*` as a `Host` is refused (§3, note 4) |
| `bookly.example` | `bookly.example` | the name the operator gave |
| `a.test, b.test` / `a.test ;b.test` | `a.test` | split on both delimiters, spaces removed |
| `*.bookly.example` | `probe.bookly.example` | the suffix pattern admits a subdomain, **not** the bare suffix |
| anything, with `HEALTH_PROBE_HOST=x` | `x` | explicit override |

A deployment whose real probe name is not derivable — a wildcard list where `probe.` is not a name the proxy
forwards, for instance — sets `HEALTH_PROBE_HOST` and is exempt from the derivation.

## 4. Migrating and seeding

```bash
docker compose --env-file .env run --rm migrator        # migrate + seed, then exit
dotnet run --project src/BooklyHub.Api -- --migrate-only  # the same step, on a host
```

`--migrate-only` runs `MigrateAsync` then `DatabaseSeeder.SeedAsync` and exits **before** the web server starts;
`AutoMigrateAndSeed=true` runs the same block and then serves. Failures are unhandled on purpose, so a broken
schema produces a non-zero exit instead of traffic.

Seeding is **one-shot by design**. The sentinel is "has this database got its permissions rows yet", which is the
first thing the method writes unconditionally. Measured on the second run of a fresh stack:

```
[11:06:57 INF] Database already seeded. Skipping initial seeding.
```

and the migrator container exits 0. This is why `SEED_DEMO_TENANTS` is read once: an operator who runs the stack
with the default, then flips it to `true` and brings it up again, gets the log line above and no demo tenants —
the intended outcome (a half-applied demo block is worse than none) but not an obvious one. To change what the
demo half did, restore a database from before the first run.

After a **reference-only** first run (`SEED_DEMO_TENANTS=false`, the compose default) the database holds:

| Table | Rows |
| :--- | :--- |
| `Roles` | 5 (the system roles) |
| `Permissions` | `Permissions.All.Count` — every permission the code grants against |
| `Users` | 1 — the platform administrator from `SEED_ADMIN_EMAIL` |
| `Tenants` | **0** |
| `StaffMembers`, `Customers` | 0 |

Those numbers are pinned by `ProductionSeedTests` (5 facts), which is the first test code ever to execute
`DatabaseSeeder` — the fixture host sets `AutoMigrateAndSeed=false`, so 341 integration facts before it migrated a
schema and walked past the seeding path.

With `SEED_DEMO_TENANTS=true` the demo block adds, per tenant, **one** location, **one** staff member with a
working week, and a couple of invented patients: Apex Dental (3 services, 2 resource groups holding 3 resources,
2 customers, one completed appointment with a review), Luxe Salon (2 services, 1 resource group with 2 chairs,
1 customer), Pulse Fitness (2 services, no resource groups, 1 customer). Read it as a demonstration of the data
model rather than a data set to shrink: it writes 3 tenants and 4 customers, and it is the only code in `src` that
writes a `Tenant` row at all.

**There is no supported way to add a tenant.** The only writers of a `Tenant` row in `src` are inside the seeder's
demo block, and there is no `api/v1/tenants` route (`SEC-03`'s gate, unresolved). So a client's own clinic gets in
one of two ways today: `SEED_DEMO_TENANTS=true` and edit the three invented tenants down by hand, or `INSERT` the
rows directly. This is a product decision that has been deliberately not taken from a bug queue; §10 lists it as
the first thing a handover conversation has to answer.

## 5. Health, readiness, and what an orchestrator should do

| Route | Checks | Healthy answer |
| :--- | :--- | :--- |
| `GET /health` | every registered check | `{"status":"Healthy","totalDurationMs":11.2,"checks":[{"name":"Database","status":"Healthy","durationMs":6.6}]}` |
| `GET /health/live` | none | `{"status":"Healthy","totalDurationMs":0.1,"checks":[]}` — 52 bytes measured |
| `GET /health/ready` | checks tagged `ready` | the `Database` check alone, 109 bytes, 0.9 ms |

With the database unreachable: `/health` and `/health/ready` `503`, `/health/live` stays `200` — a working process
is not killed because a dependency moved. `Degraded` answers `200` on `/health`. The gate is the database and
nothing else; **Redis is deliberately not a probe target**, measured twice over: against an endpoint nobody
listens on, the client's first command threw after **5,981 ms**, and a 2-second `CancellationToken` made it
*slower* (**7,114 ms**) because cancellation is not honoured while the connection is still being established. A
probe that can take seven seconds is a probe an orchestrator times out. What the cache actually guards — idempotent
replay — is a *copy* of a SQL row, so a deployment with a dead Redis still replays the charge from the database and
keeps serving (`ACacheThatFailsEveryCall_…`), and every swallowed cache call leaves a `Warning` in the log.

Compose marks `api` healthy only after `start_period: 20s` and 6 retries at 10 s intervals, so a cold SQL Server
start does not condemn a good container; and the probe asks with an admitted `Host` (§3) — a probe that gets `400`
from the host filter is how a working instance gets restarted forever.

Two wiring notes for anything that replaces compose's healthcheck:

- **`/health/live` returns `307` when `HTTPS_REDIRECT_PORT` is set.** `curl -f` treats that as success, so the
  compose probe is fine, but a Kubernetes `httpGet` liveness probe has to be told `3xx` is acceptable, and an ELB /
  ALB target-group check that requires `200` will mark a working instance unhealthy. Either probe the HTTPS side,
  or leave the redirect port unset and let the proxy redirect — which is the state this image was built for.
- **`/health` and `/health/ready` run a database query and are rate-limited**, in their own partition at 120/min
  with no queue (`Program.cs:89-97`), because they are anonymous and an unthrottled probe is a cheap way to flood
  SQL. An orchestrator with several probes a second across several replicas is inside that budget; a monitoring
  system polling every replica every second is not.

## 6. Backup, retention, and what this build never deletes

The operational tables append one row per reminder, per replay, per credential rotation and per dispatched event,
and a fourth background worker (`RetentionSweepBackgroundService`) removes the ones that have stopped answering any
question, in bounded batches, on horizons named in exactly one place (`RetentionPolicy`):

| Rows | Horizon |
| :--- | :--- |
| Idempotency records | past their 24-hour replay window |
| Refresh tokens | 30 days past `ExpiresAtUtc` |
| Outbox messages | 30 days, **only if delivered cleanly** |
| Sent notifications | 90 days |

What it keeps, on purpose, is the part an operator has to know before a storage-cost conversation: a **dead-letter
outbox row is kept indefinitely** (it is the only record that a notification was owed and never happened, and
`DB-03`'s distinct dead-letter status does not exist), an undelivered reminder is kept, status histories are kept,
and soft-deleted rows are kept — soft delete is a tenant-facing promise, not a deletion.

Backup guidance follows from that rather than from taste: a full SQL Server backup of `BooklyHubDb` restores the
whole application state, because nothing outside the database is authoritative — Redis holds cache and idempotency
copies, and the files in the image are read-only. A restore needs `MSSQL_SA_PASSWORD` and `JWT_SECRET` to be the
ones the deployment uses now, not the ones from when the backup was taken, and restoring a database older than the
`JWT_SECRET` rotation is a set of refresh chains that no longer verify.

## 7. Smoke checklist

Run in this order after a first deploy; each step names what a correct answer looks like, measured on this build.

1. `docker compose --env-file .env config --quiet` — exits 0, or names the variable you left unset.
2. `docker compose --env-file .env up -d --build` — `migrator` exits 0, `api` reaches `healthy`
   (`docker ps` shows `(healthy)` after ~20-30 s).
3. `docker inspect -f '{{.State.Health.Log}}' booklyhub-api` — the last probe exited 0. A `400` here is a `Host`
   problem (§3), not an application problem.
4. `curl -i http://127.0.0.1:5000/health/ready` with an admitted `Host` — `200`, and the body names `Database`.
   With `HTTPS_REDIRECT_PORT` set, expect `307` and read the `Location` instead.
5. `curl -i -H 'Host: anyone-else.example' http://127.0.0.1:5000/health/live` — **`400`** if `ALLOWED_HOSTS`
   names your domain. A `200` here means the filter is not installed, i.e. `ALLOWED_HOSTS` is still `*`.
6. `docker exec booklyhub-api id` — `uid=10001(appuser)`. A `uid=0` means the image was built from the version of
   `Dockerfile` that used `adduser`, which never produced an image at all.
7. `docker exec booklyhub-api printenv Payments__Provider Notifications__Provider` — `None`, unless you registered
   providers. With `None`, §10's money and message limits are live and must be stated to the client.
8. A sign-in as the administrator, one catalog read, and one booking — the walk that proves the schema, the seed
   and the availability engine together. **This walk is not yet recorded with real responses**; `DEP-05` owns it,
   and until that commit lands the honest statement to a client is "steps 1-7 are measured, step 8 is not".

## 8. Logs, and what to look at when something is refused

Serilog writes the request log (`UseSerilogRequestLogging`) plus application logs to stdout. What an operator
needs to know:

- Every refused sign-in, password change and refresh emits `Refused at {source}: {reason} for user {id}.
  Correlation-Id {cid}` at `Warning`. The **cause is logged and never sent**: all four refresh refusal causes
  produce the same single body, so the log is the only place the difference exists (`SEC-09`).
- The correlation id is the caller's `X-Correlation-Id` **only when** it matches `[A-Za-z0-9._-]{1,64}`; anything
  else is replaced by a server-made GUID, and that one answer appears in the header, the problem body's
  `correlationId`, and the log context (`SEC-13`). So a caller cannot make the log say arbitrary text, and a
  support ticket can quote a `correlationId` it received.
- Providers named `None` log their refusal as a `Warning`/`Error` with the configuration key in the text — that is
  the audit trail for "why did this patient never get their reminder".

## 9. Operator actions this repository cannot do

- **`SEC-02`: rotate the signing key that was committed before `d767c25`.** That is a secret that left the
  repository, and no commit here can un-know it. Rotation invalidates every live session.
- **Say what is in front of the container.** `Forwarding:KnownProxies` with the proxy's address, or the rate
  limiter buckets the whole deployment into one caller. `AllowedHosts` with the real domain names, or every `Host`
  header is answered. `HTTPS_REDIRECT_PORT` if the proxy does not redirect.
- **State the CORS and HSTS policy.** Neither exists in this build: there is no `AddCors`/`UseCors` anywhere in
  `src` and no `UseHsts`. Absent CORS is fail-closed for browsers (a cross-origin credentialed call is refused by
  the browser), so this is a missing statement rather than an open door — but it means a browser front-end on a
  different origin needs the policy decided first (`SEC-12`).
- **Back up before upgrading**, and read the migration list in `src/BooklyHub.Infrastructure/Migrations` — a
  deployment that skips a release still gets every migration, because migration is one command.

## 10. What a client cannot do with this build yet

Stated as the handover conversation needs it, not as a defect list. Each item names the finding that tracks it.

1. **No way to add a tenant.** No route, and the only `Tenant` writers are inside the seeder's demo block
   (`SEC-03`, §4). A new clinic means a database edit.
2. **No payment gateway.** `Payments:Provider=None` refuses every charge with a reason and writes no ledger row.
   A real gateway implements the provider interface and registers it. `Simulated` exists for development and
   Production refuses it, because it answers every positive charge as `Paid` with a synthetic `txn_sim_*` id.
3. **No message delivery.** `Notifications:Provider=None` sends nothing, so **reminders never leave the process
   and outbox messages pile up unprocessed with their reason in `Error`**. That pile is bounded by nothing —
   a deployment running `None` for months accumulates dead-letter rows that retention deliberately keeps (§6).
4. **The calendars are empty.** `Holidays`, `BusinessHours` and `AvailabilityExceptions` have **no route and no
   seed**, and `Holidays` has no writer in `src` at all, so the booking guard's holiday and exception checks are
   enforcing a calendar nobody can currently maintain. Only `WorkingHour` rows exist, and only because the demo
   block writes them (`CAL-01`, `BL-01`'s residual).
5. **Reviews are auto-published and unmoderated**, with no check that the reviewer attended the visit
   (`BL-08`): `Review.Create` sets `IsVerified` and `IsPublished` true and there is no unpublish route.
6. **A recurring series cannot be replayed.** `RecurringAppointment` persists no service, staff, location or
   time-of-day, so the series definition survives but the pattern does not regenerate (`DB-02`).
7. **`GET /api/v1/staff` is `[AllowAnonymous]` and its answer includes `Email` and `PhoneNumber`** (`API-08`). The
   public review wall is names only; this is the wider disclosure, and an unauthenticated caller can harvest a
   clinic's whole roster with contact details. Narrowing the projection — or putting the route behind a permission
   — is a product decision that has not been taken from the queue, so today's answer stands and is documented
   rather than quietly trimmed.
8. **The anonymous availability day is unbounded.** Its size grows linearly with the candidate-staff roster —
   6.7 KB at 1 staff, 111 KB at 10, **683 KB at 50** (`PERF-04`). Bounding it needs a ceiling number and a
   decision about what a truncated day says.
9. **Revocation is per credential, and an access token in hand always runs out.** `POST /api/v1/auth/logout`
   revokes the **presented** refresh row and nothing else — it is not a reuse signal, it burns no chain, and it
   leaves the account's other live sessions alone (`FAN-01`); its answer is always `204`, so it tells a caller
   nothing about which tokens exist. `POST /api/v1/auth/change-password` is the one call that revokes every live
   credential of the account, in the same transaction as the new hash. What neither can do is stop an access token
   already minted: permissions and identity live frozen in its claims, so a revoked session keeps serving until
   `Jwt:ExpirationMinutes` elapses (`SECURITY.md` §3.2).

Everything above is reachable from a clean tree; none of it is a hidden defect. A deployment that needs one of
them needs a decision, a provider implementation, or a route — and the decision belongs to the product owner, not
to this file.
