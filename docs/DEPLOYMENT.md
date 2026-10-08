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

**Those two published ports are fixed numbers, and a machine already running SQL Server or Redis cannot start this
stack on them.** Measured on a development host with a local SQL Server instance and another project's Redis
container up: `docker compose up -d` built both images, started `redis` and `sqlserver`, and then failed with
`Error response from daemon: Ports are not available: exposing port TCP 127.0.0.1:1433 -> 127.0.0.1:0: listen tcp
127.0.0.1:1433: bind: An attempt was made to access a socket in a way forbidden by its access permissions.`
(`Get-NetTCPConnection -State Listen -LocalPort 1433,6379,5000` showed one process holding `1433` on the wildcard
address and another holding `6379`; `5000` was free, which is why the API published normally. Filtering `netstat` to
`127.0.0.1` finds neither, because both listeners are on `::` / `0.0.0.0` and the collision is with the wildcard, not
with the loopback address.) This is not
a defect in the file — the ports are the operator's debugging handles, and a clean server has them free — but an
operator who wants both on one box has to move them, and the way to do that without editing the tracked file is an
override:

```yaml
# docker-compose.override.yml (or -f docker-compose.yml -f <this file>)
services:
  sqlserver:
    ports: !override
      - "127.0.0.1:14330:1433"
  redis:
    ports: !override
      - "127.0.0.1:63790:6379"
```

`!override` is what makes this replace the base list instead of appending to it; a plain `ports:` in the override
leaves `1433` in the config and fails the same way. Nothing inside the network addresses these ports by the host
number — `api` reaches `sqlserver,1433` and `redis:6379` by service name — so remapping the host side changes only
what the operator's own database client types. §7's run used exactly this override.

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

Run in this order after a first deploy; each step names what a correct answer looks like.

**All eight steps below were run against this repository on 2026-10-07**, on a fresh volume with
`SEED_DEMO_TENANTS=true`, `PAYMENTS_PROVIDER=None`, `NOTIFICATIONS_PROVIDER=None` and `ALLOWED_HOSTS=*` (the
`.env` the operator copies from `.env.example`, filled with generated values). The quoted output is the output; the
two tokens in step 8's login response are the only elisions, replaced by what was measured about them.

1. `docker compose --env-file .env config --quiet` — exits `0`. With the required variable missing it fails here,
   before any image is built, and names it:
   ```
   $ docker compose --env-file /dev/null config --quiet
   error while interpolating services.sqlserver.environment.[]: required variable MSSQL_SA_PASSWORD is
   missing a value: MSSQL_SA_PASSWORD must be set (see .env.example)
   exit 1
   ```
2. `docker compose --env-file .env up -d --build` — both images build, `migrator` exits 0, `api` reaches
   `healthy`. Measured on this run (the image layers were **cache hits** — an identical context had been built
   earlier in the same session, so this says nothing about how long a first cold build takes; the volume, though,
   was genuinely empty and the migration and seed ran for real):
   ```
   Container booklyhub-sqlserver  Healthy
   Container booklyhub-migrator   Exited        # exit code 0
   Container booklyhub-api        Started
   $ docker ps --filter name=booklyhub --format '{{.Names}}\t{{.Status}}'
   booklyhub-redis      Up 24 seconds (healthy)
   booklyhub-sqlserver  Up 24 seconds (healthy)
   booklyhub-api        Up 13 seconds (healthy)
   booklyhub-migrator   Exited (0) 13 seconds ago
   ```
   On a host that already runs SQL Server or Redis this is the step that fails, with the port error in §1.
3. `docker inspect -f '{{range .State.Health.Log}}{{.ExitCode}} {{.Output}}{{end}}' booklyhub-api` — every probe
   exit code is `0`. Measured: `0 {"status":"Healthy","totalDurationMs":0.6,"checks":[]}` three times. A `400`
   here is a `Host` problem (§3), not an application problem.
4. `curl -i http://127.0.0.1:5000/health/ready` — `200`, and the body names `Database`. Measured:
   ```
   HTTP/1.1 200 OK
   Content-Type: application/json; charset=utf-8
   Server: Kestrel
   X-Correlation-Id: 74ca8e0e88d945ef95739e2270ad7427

   {"status":"Healthy","totalDurationMs":17,"checks":[{"name":"Database","status":"Healthy","durationMs":12.9}]}
   ```
   `/health/live` answers the same envelope with an empty `checks` array — it proves the process serves, nothing
   else. With `HTTPS_REDIRECT_PORT` set, expect `307` and read the `Location` instead.
5. `curl -i -H 'Host: anyone-else.example' http://127.0.0.1:5000/health/live` — with `ALLOWED_HOSTS=*` this run
   answered **`200`**, which is the documented meaning of `*`: no filter is installed. Name the domain and the
   same request answers `400`; that case, and the probe's `Host` derivation for every spelling, is §3's table.
   (Re-measuring the named case on this compose stack was queued and then lost: the host's Docker engine stopped
   mid-session, after step 8's last request, so the stack was not there to restart with a named `ALLOWED_HOSTS`.)
6. `docker exec booklyhub-api id` — measured:
   `uid=10001(appuser) gid=10001(appuser) groups=10001(appuser)`. A `uid=0` means the image was built from the
   version of `Dockerfile` that used `adduser`, which never produced an image at all.
7. `docker exec booklyhub-api printenv Payments__Provider Notifications__Provider` — measured: `None`, `None`.
   With `None`, §10's money and message limits are live and must be stated to the client.
8. **Sign in, read the catalog, book, and try to book the same slot twice.** The seeded platform administrator is
   the only account the seeder writes, and it belongs to no tenant (`tenantId: null`), so this walk names the
   tenant per request with `X-Tenant-Id`. `POST /api/v1/auth/login` → measured `200`:
   ```json
   {"accessToken":"eyJ... (1123 bytes)","refreshToken":"...","expiresAtUtc":"2026-10-07T14:39:12Z",
    "user":{"email":"admin@booklyhub.test","tenantId":null,"roles":["PlatformAdmin"],"permissions":"25 entries"}}
   ```
   `GET /api/v1/services` with that bearer and `X-Tenant-Id: <apex>` → `200`, three services, each carrying
   `durationMinutes` and both buffer fields. That was the deployed image's **bare array**, and it is kept as measured:
   the tree now pages both catalog reads, so the same call answers `{total, page, pageSize, items}` with `total: 3`
   (`docs/API.md` §7), and the `Z` on the read below is likewise the current code's answer rather than this image's.
   The same request with **no** header and no token →
   ```json
   {"title":"Bad Request","status":400,"detail":"Active tenant context is required.",
    "instance":"/api/v1/services","correlationId":"5d3125eaf05f4799a5d1c45d04c46a9e"}
   ```
   `GET /api/v1/availability?locationId=…&serviceId=…&staffId=…&date=2026-10-08&tenantId=…` → `200`, `isOpen: true`,
   26 slots; the same query for the weekend (`2026-10-10`, `2026-10-11`) → `isOpen: true` with `slots: []`, which
   is the seeded working week saying it is closed without the response having a way to say so. The first slot
   offered was `2026-10-08T12:30:00Z` → `13:15:00Z` with one `availableResourceIds` entry, and the booking below
   allocated exactly that resource.

   `POST /api/v1/appointments` with that slot, a real `customerId`, a bearer token, `X-Tenant-Id` and an
   `Idempotency-Key` → **`201 Created`**, `Location: http://127.0.0.1:5000/api/v1/appointments/603af0fa-…`, and:
   ```json
   {"id":"603af0fa-863e-42b1-9ebc-073a830d2d4c","status":"Confirmed","startAtUtc":"2026-10-08T12:30:00Z",
    "endAtUtc":"2026-10-08T13:15:00Z","durationMinutes":45,"price":120.00,"currency":"USD",
    "customerName":"Emily Watson","staffName":"Dr. Marcus Vance","locationName":"Downtown Dental Center",
    "allocatedResourceIds":["0333fd0c-1d3b-47a1-9b55-203c243ff47b"]}
   ```
   Then the three controls that make this a test rather than a demo, all on the same running stack:
   - same `Idempotency-Key`, byte-identical body → `201` again with **the same `id`** (`603af0fa-…`), and no
     second row: the retry a client sends after a dropped connection cannot double-book.
   - same `Idempotency-Key`, different body → `409` `{"title":"Idempotency Key Conflict","detail":"This
     Idempotency-Key was already used for a different request."}`.
   - fresh `Idempotency-Key`, same slot → `409` `{"title":"Booking Conflict","detail":"That time slot is already
     booked for the selected staff member.","correlationId":"b8c6642a…"}`. The app lock and the availability guard
     fired on a real SQL Server instance, in Production, over HTTP.
   - `GET /api/v1/appointments?fromUtc=2026-10-08T00:00:00Z&toUtc=2026-10-09T00:00:00Z` as `apex-dental` →
     `totalCount: 1`; the **same request** with `X-Tenant-Id` set to `luxe-salon` → `totalCount: 0`.
   - `GET /api/v1/reports/dashboard` → `200`, `totalAppointments: 1`, `grossRevenue: 120.00`,
     `upcomingConfirmedCount: 1`, `staleExecutionCount: 0`.

   One thing this walk measured that the docs did not know: the `201` above answers `"startAtUtc":"…T12:30:00Z"`,
   but reading **that same row back** from `GET /api/v1/appointments` and `GET /api/v1/appointments/{id}` answers
   `"startAtUtc":"2026-10-08T12:30:00"` with no `Z` (`endAtUtc` and `createdAtUtc` likewise). The columns are
   `datetime2`, which stores no zone, so a value EF materializes arrives labeled `Unspecified` and System.Text.Json
   writes it naked — while a value the process wrote itself still carries the `Utc` label it was given at the
   request boundary. The server therefore states the reading it made on the write path and does not on the read
   paths, and a client that parses a naked ISO instant as *its own* local time shifts every appointment it lists.
   `docs/API.md` §4 claimed "Responses always carry `Z`"; that claim is corrected to what was measured, and the fix
   is `TIME-01` in `AUDIT-STATUS.md` §1 — it is a labelling defect, not a stored-value defect: the row in the
   database is the right instant, and the booking guard, the cutoff policy and the dashboard all agree on it.

   `TIME-01` is fixed, and the two quotes above stay exactly as they were taken — they are the measurement that
   found it. The proving fact is `UtcInstantBoundaryTests.AnAppointmentReadBackOverHttp_MustLabelEveryInstantAsUtc`,
   run against real SQL Server for all three `…AtUtc` fields on both read shapes (the entity path and the paged
   projection). Its control was run on a copy of the tree at `HEAD` with the converter absent, where it failed
   **3 of 3** with `"2026-10-10T10:00:00"`, `"2026-10-10T10:30:00"` and `"2026-10-08T07:22:12.8116335"`; the same
   tree, asserted with the projection first, failed the projection too, which is why the fix sits at the EF model
   boundary instead of in the DTOs. It passes in the fixed tree, with 290 unit and 349 integration tests green and
   `dotnet ef migrations has-pending-model-changes -p src/BooklyHub.Infrastructure` reporting no model change — the
   fix adds no migration, because a column that stores no zone still stores no zone. What has **not** been re-run is
   this checklist's own step 8 against the compose stack: the host's Docker engine stopped at the end of that session,
   so an operator verifying the deployed image should expect `Z` on the reads and treat a naked answer as the
   regression this fact now catches on every push.

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
7. **Revocation is per credential, and an access token in hand always runs out.** `POST /api/v1/auth/logout`
   revokes the **presented** refresh row and nothing else — it is not a reuse signal, it burns no chain, and it
   leaves the account's other live sessions alone (`FAN-01`); its answer is always `204`, so it tells a caller
   nothing about which tokens exist. `POST /api/v1/auth/change-password` is the one call that revokes every live
   credential of the account, in the same transaction as the new hash. What neither can do is stop an access token
   already minted: permissions and identity live frozen in its claims, so a revoked account keeps serving until
   `Jwt:ExpirationMinutes` elapses (`SECURITY.md` §3.2).

Everything above is reachable from a clean tree; none of it is a hidden defect. A deployment that needs one of
them needs a decision, a provider implementation, or a route — and the decision belongs to the product owner, not
to this file. Three items that used to be on this list are not any more:

- a read appointment's `…AtUtc` fields answering with no zone designator (`TIME-01`) is fixed and pinned by a test,
  and §7 step 8 keeps the measurement that found it;
- **the anonymous availability day is bounded** (`PERF-04`): at most 500 slots, `slotsTruncated: true` when a day is
  longer, which caps the response the roster used to size — 6.7 KB at 1 staff, 111 KB at 10, **683 KB at 50** before
  the ceiling;
- **`GET /api/v1/staff` no longer answers contact details to an anonymous caller** (`API-08`): `email` and
  `phoneNumber` are `null` unless the caller holds `staff.read`, and both catalog reads page. The names stay public,
  because the booking portal renders them; `docs/API.md` §7 is the contract.
