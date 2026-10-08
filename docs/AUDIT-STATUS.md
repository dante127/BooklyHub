# Audit status — what the original audit claimed and what the code does today

The first audit of this repository (2026-09-27) named findings with IDs like `SEC-04`, `BL-01`, `PERF-05`. The
remediation since then closed most of them, but not under those names: the early work was committed as phases
(`P0-1`…`P8-4`), and later findings got their own families (`KEY-02`, `FAN-01`, `DOC-04`, `PAG-01`, `LOC-01`). That
leaves a reader unable to tell whether `BL-05` is a bug still in the tree or a sentence that stopped being true six
weeks ago. This file answers that, per ID, with the evidence pointed at a file and a line.

Everything here was measured at `c67480f`; the line references and the availability timings below were re-checked
against the tree at `21bebf7`, after the `BL-05` and `SEC-13` fixes landed, the `PERF-05` row was re-measured at
`009254b` before it was closed, and the `DEP-*` rows were measured against `5ae2e74` as their control — every
negative control behind a verdict in this file ran in a detached `git worktree` at a named SHA, never by reverting
the fixed tree. A verdict of **live** means the described
behaviour is reachable in the
current code; **fixed** names the commit; **stale** means the audit's description does not match the code (and often
never did); **product decision** means closing it means inventing behaviour the repository has no opinion about,
which this remediation does not do from a bug queue.

## 1. Verdicts

| ID | Audited severity | Verdict | Evidence | Action |
|---|---|---|---|---|
| SEC-01 — committed signing key, config fallbacks, wrong prod env names | Critical | **fixed** (code) | `d767c25`; `DependencyInjection.cs:17-23` throws on an empty connection string, `JwtSigningSettings.cs:35-44` on an empty secret; compose now sets `Jwt__Secret` (`docker-compose.yml:45,64`) which is the key the code reads (`JwtSigningSettings.cs:8`) | rotating the key that is still in git history is an **operator** action (tracked as `SEC-02`) |
| SEC-03 — anonymous `?tenantId=` names the tenant on public reads | High | **live, narrower than claimed** | `TenantResolutionMiddleware.cs:57-68` resolves header/query and makes **no database call**; the only tenant-level `IsActive` read on any request path is `AvailabilityService.cs:405-414`, which answers an **empty day**, not a refusal | needs a product decision (is a tenant GUID a secret?); behaviour pinned by `InactiveTenantReadPathsTests` and recorded in `MULTI-TENANCY.md` §2 |
| SEC-06 — permissions frozen in 60-min claims; "policy provider matches anything" | High | **first half live, second half stale** | claims baked at `AuthServices.cs:48-51`, life 60 min (`appsettings.json:10`), already open in `SECURITY.md` §; `PermissionAuthorization.cs:19-35` is fail-closed — it succeeds only on `PlatformAdmin` or a matching `permission` claim, and `PermissionPolicyProvider.cs:25-31` never returns a permissive policy | revocation (`SEC-05c`, `ACT-01`) already bounds the frozen-claims window; nothing further to repair |
| SEC-09 — PII at `Information`; refused sign-ins unlogged | Medium | **fixed** | the three dispatch lines carry only lengths and the server's own recipient id (`NotificationAndCacheServices.cs`, pinned by `OutboundDispatchLogTests`); every refusal on the two doors that take a password emits `Refused at {source}: {reason} for user {id}. Correlation-Id {cid}` at `Warning` (`AuthController.cs` `RefusalReason`/`LogRefusal`, pinned by `RefusedSignInLogTests`, `LockedOutSignInLogTests`, `RefusedPasswordChangeLogTests`), and so does every refused refresh (`RefreshRefusalCause`, same line, source `auth:refresh-token`, pinned by `RefusedRefreshLogTests`) | the cause is logged, never sent — the body stays the single `Invalid or expired refresh token.` for all four causes (`unknown-credential`, `revoked-credential`, `expired-credential`, `inactive-account`), and the credential string is written nowhere. Two measured notes travel with the refresh half: a **soft-deleted** account is logged as `unknown-credential`, not as its own cause, because the filtered include drops the credential row before the guard sees it (§2.11); and the appointment handlers' dispatches still leave no recipient record anywhere (`SECURITY.md` §3.4) |
| SEC-11 — `LIKE` wildcards not escaped in search | Medium | **stale as claimed; closed on the defect the claim missed** | `.Contains(search)` (`CatalogControllers.cs:143`) does **not** reach SQL with `%`/`_` intact: EF Core 10 sends `LIKE @search_contains ESCAPE N'\'` with `%`, `_`, `[` and `\` prefixed before the term leaves the process. Measured on the real route — `?search=%` returns the one row whose name holds a percent sign, not the tenant's book; `a_a` misses `Ana`; `[AB]na` matches nothing; `\` finds the backslash row instead of faulting; `' OR 1=1--` is a parameter. Pinned by `LikeWildcardSearchTests` (6 facts), whose control replaces the predicate with a hand-built unescaped `EF.Functions.Like(c.FirstName, "%" + search + "%")` and fails **all six** | What the length walk found instead: from **3,999 characters** the pattern passes SQL Server's 4,000-character ceiling and the route answers `500`. Bounded by `MaxSearchTermCharacters = 256` — the widest column the predicate reads (`sys.columns`: name 100, email 256), so a longer term cannot be a substring of anything and the empty page is the only true answer. 7 more facts (`SearchTermLengthTests`), one of which reads the column widths off the EF model so a widening moves the bound in a failing test; `SECURITY.md` §3.7 |
| SEC-12 — no CORS policy, no HSTS, HTTPS redirect inert in Docker | Medium | **forwarded-headers and Docker-redirect halves fixed; CORS and HSTS live** | `UseForwardedHeaders` is now in the pipeline, registered only when the operator names a proxy or a network (`ForwardingSettings.cs`, `Program.cs` first-in-pipeline), reading `Forwarding:KnownProxies` / `Forwarding:KnownNetworks`, with the framework's inherited loopback trust withdrawn and the chain bounded — so behind a declared TLS-terminating proxy the rate-limit buckets are per real caller again instead of one shared bucket per deployment (`SECURITY.md` §3.6). The Docker half closed with `DEP-03`: `Dockerfile:32` exposes the one port the process binds (it said `8081` while `ASPNETCORE_URLS` said `8080`), and `HTTPS_REDIRECT_PORT` puts the redirect in the pipeline with the port a TLS terminator actually answers on — left unset it stays out rather than spending every plain request failing to work a port out. Still absent: no `AddCors`/`UseCors` anywhere in `src` (pipeline `Program.cs`) and no `UseHsts` | **deployment-dependent** for the two that remain. Absent CORS is fail-closed for browsers, so this is a missing stated policy, not an open door; both need the operator's deployment story, not a patch |
| SEC-13 — `AllowedHosts":"*"`, correlation ID taken verbatim | Low | **both halves closed; the second on a different defect than claimed** | `CorrelationIdMiddleware.Resolve` keeps a supplied id only when it matches `\A[A-Za-z0-9._-]{1,64}\z` and otherwise answers under a server-made GUID — the header, the problem body's `correlationId` and Serilog's `LogContext` property all read that one answer, so the caller's text can no longer be echoed at it or spliced raw into the output template (`Program.cs:21`), and the replace-not-strip choice is pinned by its own assertion (`CorrelationIdBoundaryTests`, 9 facts; `SECURITY.md` §3.5) | **The claimed residue was measured and is not true.** `DEP-03` ran `5ae2e74`, which has no host-filtering code, and `AllowedHosts=a.test` answered 400 to `Host: evil.test`: minimal hosting installs the middleware and binds the key itself, so `AllowedHosts` was never dead config and `UseHostFiltering()` was never missing. What was live is the *spelling*: the host splits the key on `';'` and trims nothing, so a comma list, a space inside an entry, or `*a.test` makes the site answer 400 to every Host **including the names on the list**, and `a.test; *` quietly drops the wildcard. `ServingSurface` reads and validates the key now (32 facts, `docs/DEPLOYMENT.md` §3 for the live matrix), and `appsettings.json:29` keeps `"*"` because widening a deployment's admitted hosts from a repository default is an operator's call, not a commit's. Also corrected here: the 100-char `AuditLogs.CorrelationId` column was never at risk, because nothing writes that table |
| API-08 — anonymous review wall exposes staff/service names | Low | **live by design** | `ReviewsController.cs:48-49` `[AllowAnonymous]`, tenant from header/query, `IsPublished` rows only (`:61`) | already documented as the public half; the stronger disclosure is `GET /api/v1/staff`, which answers `Email` and `PhoneNumber` (`CatalogControllers.cs:94-95`) |
| BL-01 — booking guard ignores working hours, business hours, holidays, leave | High | **fixed** | `52aff50` put preview and guard behind one rule set; all four calendars are read in `AvailabilityService.cs:468` (Holidays), `:500` (WorkingHours), `:509` (BusinessHours), `:517` (AvailabilityExceptions) and consumed by `Classify` (`:340-386`) | the real residual is **`CAL-01`** (coined here): only `WorkingHours` is ever seeded (`DatabaseSeeder.cs:126-129`), and Holidays / BusinessHours / AvailabilityExceptions have **no route and no seed**, so the guard enforces a calendar nobody can currently maintain |
| BL-02 — resource conflict ignores location scope and buffers | High | **fixed** | `62beadb` (location-wide allocator) + `f404dc3` (the index the occupancy read needed); occupancy loads by tenant+location (`AvailabilityService.cs:487-491`), the guard returns the resource ids it verified and the commands persist exactly those (`BookAppointmentCommand.cs:219,245-253`) | buffers applying to staff but not rooms is a documented choice (`SCHEDULING-CONCURRENCY.md` §3.2) |
| BL-05 — `DateTime.Kind` unvalidated; DST-gap conversion shifts wall time | High | **`Kind` half fixed, DST half stale** | the DST mapping is deliberate and documented (`TimeZoneHelper.cs:36-44`, `SCHEDULING-ENGINE.md` §DST). The `Kind` half was live and is now closed on measurement: the columns are `datetime2` and store the ticks they are handed, so a body of `09:00:00+00:00` on a host at UTC+3 was written as **12:00** — the shift equals the deployment's own offset. `UtcInstant.cs` resolves every inbound instant at the two POST bodies and the dashboard window, `Appointment.Create`/`Reschedule` refuse a `Kind=Local` value as `InvalidDateKind`, and the answers carry `Z` (`UtcInstantWriterTests`, `UtcInstantBoundaryTests`) | the query bounds are deliberately not routed through the resolver — measured, `fromUtc`/`toUtc` already bind offset-bearing values to a UTC instant and naked ones to the ticks the UTC column holds, so nothing observable would change; changing the DST policy stays a product decision |
| BL-08 — no actor↔appointment check on reviews; auto-published | Medium | **live, product decision** | `[Authorize]` with no permission (`ReviewsController.cs:35-36`); the review's `CustomerId`/`StaffId`/`ServiceId` are derived from the appointment, never from the caller (`SubmitReviewCommand.cs:76-84`); `Review.Create` sets `IsVerified`/`IsPublished` true (`ReviewEntities.cs:29-30,67-68`) | a relationship check needs a `User → Customer` identity that does not exist in the model; moderation needs a policy. Both are features |
| CONC-01 — lock scope plus a silent no-op provider | Medium | **fixed except a documented bypass** | location-then-staff ordering (`ApplicationDbContext.cs:256-265`); no fake providers in production (`d767c25`, `3696b2d`) | `TryAcquire*`/`ExecuteAppLock*` still return success on a non-SqlServer provider (`:302`, `:314`, `:322`); production is hard-wired to `UseSqlServer` (`DependencyInjection.cs:25-30`), so it is unreachable outside tests |
| DB-01 — two individually-incomplete isolation controls; "filter missing from the migration snapshot" | Medium | **fixed / stale** | the pairing is the remedy: global filters for `ITenantEntity`/`ISoftDeletable` (`ApplicationDbContext.cs:83-129`), a save-time cross-tenant write rejection (`:140-157`), explicit predicates where a filter provably cannot help (`:281-298`) — all added in `52aff50`. The snapshot claim is a category error: query filters are runtime translation, never schema | covered by `TenantIsolationTests` |
| DB-02 — composite keys without `TenantId`; recurring series loses its definition | Medium | **live at schema, product decision for the series** | `{StaffId, ServiceId}` (`OperationalConfigurations.cs:91`), `{ServiceId, ResourceGroupId}` (`:219`), `{AppointmentId, ResourceId}` (`:235`) carry no tenant; `WorkingHourInterval` has no `TenantId` at all (`SchedulingEntities.cs:27-48`) and is safe only because it is loaded through a tenant-filtered parent (`AvailabilityService.cs:500-507`); `RecurringAppointment` persists no service/staff/location/time-of-day (`AppointmentEntities.cs:291-312`) and nothing reads the series back | exploitation needs a handler bug, since `SaveChangesAsync` rejects foreign-tenant writes; making the series regenerable is a feature |
| PERF-01 — test host pinned to `(localdb)`, so CI never ran the SQL-Server tests | Critical | **fixed** | `BooklyHubWebApplicationFactory.cs:49-58` prefers `BOOKLYHUB_TEST_CONNECTIONSTRING` / `ConnectionStrings__DefaultConnection` and falls back to localdb only when nothing is injected; `.github/workflows/ci.yml:14-27,55-58` starts SQL 2022 and passes it | the localdb fallback is deliberate for a dev laptop |
| PERF-02 — availability costs 2-3 queries per candidate staff, no cache, public | Medium | **query half fixed, rest live** | one batched calendar load for all candidate staff (`AvailabilityService.cs:109-113` → `:468`, `:484`, `:500`, `:509`, `:517`), the staff loop is in-memory (`:131-161`); `ICacheService` is used only by idempotency (`PaymentAndIdempotencyServices.cs:54`); route is `[AllowAnonymous]` (`AvailabilityController.cs:21-22`) | measure before caching (the cache half is still open, and the route is still anonymous); the batching now has its regression test — `AvailabilityBatchingTests` pins the day at **11 statements whatever the roster size**, and its control (one read per candidate staff, injected in a throwaway worktree) moves the eight-staff day to 18 and the `WorkingHours` reads from 1 to 9 |
| PERF-03 — conflict lists re-inlined as huge `IN` lists per slot | Medium | **stale as a query defect** | `TryAllocateResources` (`AvailabilityService.cs:622-662`) takes no `DbContext` and no token — it filters the already-loaded occupancy through a `HashSet` (`:635-638`); inventory loads once (`:603-614`) | what remains is in-memory `O(staff × slots × occupancy)` work in `Classify` — CPU, not round trips, and `PERFORMANCE.md` §6 times the whole loop at single-digit milliseconds on a day with bookings |
| PERF-04 — uncapped availability payloads; pagination bounded in 1 of 4 list endpoints | Medium | **mostly fixed, one cap still absent** | `Paging` now fronts all four paged reads (`CatalogControllers.cs:134-135,150`, `ReviewsController.cs:66-67,72`, `AppointmentQueries.cs:82-83,134`, `OutstandingVisitQueries.cs:55-56,75`), ceiling 100 (`Paging.cs:18`). Still unbounded: the availability day — the candidate-staff roster (`AvailabilityService.cs:78-99`, no `Take`) multiplies the response linearly, measured at 6.7 KB for 1 staff, 111 KB for 10, **683 KB for 50** on an `[AllowAnonymous]` route (`PERFORMANCE.md` §6) — and the two anonymous catalog reads, which are not paged at all (`CatalogControllers.cs:27-55`, `:72-109`) | **measured corrections:** (i) the slot grid is not the cost — 15× more starts buys 1.5 ms empty, 4.2 ms busy, so the shift-window prefilter was considered and refused (`PERFORMANCE.md` §6); (ii) nothing in the API writes `TenantSettings` (the only writers are `DatabaseSeeder.cs:79,155,206`), so the 1440-starts shape needs a direct database edit and is not reachable from a caller; (iii) `docs/PERFORMANCE.md` §3 claimed every list paginates, corrected in this change. What remains is a product decision about what a bounded day says |
| PERF-05 — missing `(TenantId, LocationId, StartAtUtc)`, `(Status, StartAtUtc)`, sargable annual holidays | Medium | **all three answered; the third on a different diagnosis than the audit's** | the bare `(TenantId, LocationId, StartAtUtc)` was measured and refused: 352 logical reads against the `EndAtUtc` key that `INCLUDE`s `StartAtUtc` (`20261004105059_AddAppointmentLocationOccupancyIndex.cs:13-17`, `DATABASE.md` §3.1, `f404dc3`); `(TenantId, Status, StartAtUtc)` predates the audit (`20260923065132_InitialCreate.cs:953-955`); and the holiday read was costed (`PERFORMANCE.md` §7) — it is **sargable**, but it was not the missing key column that hurt: `TenantId` is the only equality in the predicate, so the read already lands on the tenant's rows and then pays a **key lookup per row** for `LocationId` and `RecurringAnnually`. Measured on the guard's own statement, 40 holiday rows in a 40,040-row table: **124 logical reads to return 10**, and at 200,040 rows the optimizer abandons `IX_Holidays_TenantId_Date` entirely for **one clustered scan of every tenant's calendar — 3,045 reads**. `20261006081209_CoverHolidayCalendarRead` adds the two columns as `INCLUDE`, same key, no new index: 4 reads and 3. | Pinned by `HolidayIndexTests` (shape, cost through the real guard, and a recurring holiday still closing its day — no other test reaches that half of the OR). Two refusals travel with it: `(TenantId, RecurringAnnually, Date) INCLUDE (LocationId)` measured identical at all six distributions, and `Scan count` was 1 on both sides of the fix, so only the page count discriminates. **The bound on all of it: nothing in `src` writes a `Holiday` row** (`AvailabilityService.cs:468` is the only reader; no command, route, or seeder inserts one), so every deployed calendar table is empty and this index pays out only once `CAL-01` populates it |
| QUAL-02 — a 20-argument DTO hand-built at 4 sites | Medium | **stale** | `ReviewDto` is 12 parameters (`SubmitReviewCommand.cs:11-23`) with exactly one construction site (`:89-101`); the widest dashboard DTO is 17 with one site (`ReportingQueries.cs:9-26`, `:175-192`) | the kernel of truth is that the one site mixes `review.*` with `appointment.Customer/Staff/Service` |
| QUAL-04 — two competing time sources | Medium | **fixed in the Application layer** | `IClock` has 48+ references across 16 files, one adapter (`CommonServices.cs:19`), and the three ledger stamps are gone: `PaymentCommands.cs` reads `_clock.UtcNow` once per handler and writes that instant to the rows it owns — the charge's `PaymentTransaction.TimestampUtc`, and both `Refund.CreatedAtUtc` and its refund ledger row, which the change tracker does not fill because `Refund`/`PaymentTransaction` are not `IAuditableEntity`. Pinned by `PaymentStampClockTests` (3 facts), one of which is the source rule itself: no file under `src/BooklyHub.Application` may name the machine clock, so a fourth stamp cannot appear without failing a test | what remains is not a second source: the `JWT expires` claim (`Infrastructure/Security/AuthServices.cs:57`) reads the wall clock on purpose, because a token has to agree with the clock its verifier really runs, and the Domain entities' `= DateTime.UtcNow` field defaults (`AppointmentEntities.cs:51,106,115,222,276,295,317`, `PaymentEntities.cs:23,43,60`) are a layering fact, not residue — `Domain` must not know `IClock`, and for an `IAuditableEntity` the tracker overwrites that default with `_clock.UtcNow` at save (`ApplicationDbContext.cs:160-166`) |
| QUAL-05 — god interface `IApplicationDbContext` | Medium | **live, now documented** | 35 `DbSet`s (`IApplicationDbContext.cs:20-54`, one more than the audit counted), injected into handlers (`AvailabilityService.cs:19`, `SubmitReviewCommand.cs:44`, `ReportingQueries.cs:48`) and controllers (`CatalogControllers.cs:18,63,117`) | split-by-module is a rewrite, not a fix; recorded here as knowingly open |

## 2. Corrections to claims made during the remediation

Recording these because each one was stated as fact at some point, and the tree says otherwise:

1. **`BL-01` was reported live in a session report and it is not.** The claim came from a `grep` for the calendar
   names whose output was cut by `head -20` after migration files filled the list; absence was concluded from a
   truncated list. The reads exist at `AvailabilityService.cs:468,500,509,517`. Rule: never conclude absence from a
   piped list that was cut.
2. **`Take(negative)` is not tolerated.** `PAG-01`'s notes first said only `Skip` faults; with the customers clamp
   removed, `?pageSize=-5` returned `500` — SQL rejects `FETCH NEXT -5 ROWS ONLY` too. Both operands fault.
3. **"pagination bounds enforced in 1 of 4 list endpoints" is now wrong in both directions.** Four routes clamp
   through `Paging`; two catalog routes are not paged at all, so bounding them is not a matter of clamping.
4. **`SEC-06`'s "dynamic policy provider matches anything"** does not describe `PermissionAuthorization.cs:19-35`.
5. **`QUAL-02`'s "20-arg, constructed 4×"** does not describe any DTO in the tree, in this revision or in the import.
6. **`DB-01`'s "filter not present in the migration snapshot"** confuses EF query filters with schema.
7. **"A test can read the host's log through a registered `ILoggerProvider`" is false here.** `Program.cs:24`
   calls `UseSerilog()`, which replaces the container's factory with `SerilogLoggerFactory` — it holds no
   `_providers` and never enumerates the registered ones, so such a collector stays empty forever and every
   privacy fact written against it passes by finding nothing. `SEC-09`'s collector sits on the `ILogger<>` seam
   instead; the measurement is recorded in `CollectingLogger`'s remarks and `SECURITY.md` §3.4.
8. **"`Kind=Utc` at the API boundary" is not the rule `BL-05` needed.** Applied literally it refuses a naked
   `2027-06-01T09:00:00` — which the field name `…AtUtc` already promises to read as UTC, and whose ticks are
   already right — while accepting nothing the binder breaks. What the measurement showed is that only
   `Kind=Local` carries a defect: an explicit offset is re-based onto the server's zone by the serializer and then
   written into `datetime2` as those shifted ticks. The rule is therefore convert-`Local`-back, label-`Unspecified`,
   and refuse `Local` at the writers. Routed through the same measurement, the two `GET` date bounds turned out to
   need nothing at all: MVC binds an offset to a UTC instant and a naked bound to the ticks the column holds, so a
   resolver there would be code no test can observe.
9. **"The slot grid is dead work worth skipping" was a shape argument, and the timing refused it.** The plan written
   before measurement was to intersect `CandidateStartsUtc` with each staff member's shift windows, on the reasoning
   that most of a day's starts are rejected at `Classify`. The measurement (`PERFORMANCE.md` §6) put the whole saving
   at 1.5 ms on an empty day and 4.2 ms on a busy one, at a grid density the API cannot produce because no route
   writes `SlotIntervalMinutes` — and the same run showed the cost is the roster, which multiplies both time and
   bytes linearly. Rule: a claim about how much work a loop does is not a claim about what a request costs.
10. **`PERF-05`'s holiday index is not missing a seekable column, and the claim that it was came from reading the
    predicate instead of the plan.** `|| h.RecurringAnnually` does look un-sargable, and the tempting fix is that column
    in the key. Costing the guard's own statement said the read was never short of a seek — `TenantId` is the only
    equality in it, a seek on `(TenantId, Date)` already reaches the tenant's whole calendar, and what the read then pays
    is a key lookup per row for the two columns the filter and the projection need. Coverage fixed it (124 → 4 logical
    reads on the same seed); the candidate that makes the OR seekable measured identical at every volume probed, because
    the recurring rows have to come back whatever the index says. Two related claims did not survive the replay either:
    `Scan count` was **1 on both sides**, so it proves nothing about access paths here, and a first version of the probe
    reported "40 rows returned for a 40-row tenant", which was really 10 rows counted four times. Rule: a predicate is
    not a plan — read the operators before choosing the column, and read the row count before trusting the cost.
11. **"A soft-deleted account arrives at the refresh guard as `tokenRecord.User == null`" was a comment, and the
    request disagreed.** It had been written into `AuthController`'s own guard note when `ACT-01` added the account
    check, and the plan for `SEC-09`'s refresh half followed it: five causes, one of them `deleted-account`. Seeding
    the row and deleting the account through the API's own read showed the credential comes back as *nothing at all*
    — the guard's `Include(rt => rt.User)` puts User's soft-delete filter on a required navigation, which EF turns
    into an inner join that drops the principal. So the classifier has four causes and a deleted account is refused
    and logged as `unknown-credential` with `(no row)`, pinned by `ADeletedAccount_MustBeRefusedAsACredentialThatWas
    NeverIssued` rather than argued. Rule: a claim about what a query *returns* is a claim about the query, and only
    running it answers it.

12. **`ForwardLimit = 1` was written down as the defence against a prefixed `X-Forwarded-For` chain, and its own
    mutation control falsified that.** The reasoning was the documented one — with no limit the middleware walks the
    whole chain and reports its leftmost entry, so a caller writing `myself, the-proxy's-client` picks the identity the
    limiter buckets on. The fact that was supposed to prove it
    (`AChainThatEndsWithTheDeclaredProxy_MustNotLetTheCallerReachPastIt`) passed with the setting removed, and so did
    the other two trusted-proxy facts: on this runtime the chain resolves from its **rightmost** entry whatever
    `ForwardLimit` says, so what stopped the caller was the declared-peer check, not the depth bound. The setting stays,
    documented for what it actually does — bound how far a trusted proxy's chain is walked — and `Program.cs` now says
    that instead of repeating the withdrawn claim. Rule: a control that passes after the fix is removed does not just
    fail to prove the fix, it disproves the reason given for it.
13. **A fact about a guard can pass on the binder, and only a control aimed at the guard finds out.** `SEC-11`'s
    length rule was first written as `!string.IsNullOrWhiteSpace(search) && search.Length > bound`, and the fact that
    "a term of nothing is still the whole book" passed. Re-running it with the blank-term half removed — the mutation
    that was supposed to break it — it passed again: MVC converts a query-string value that is nothing but spaces to
    `null` before the route sees it, so no blank term ever reaches the length check and the half that looked
    load-bearing was dead code. The rule is now the length alone, and the fact says out loud that it pins the binder's
    behaviour, not the guard's. Rule: a control has to be aimed at the thing the fact claims to test; a fact that
    survives every mutation of its own subject is describing somebody else's code.
14. **"Production refuses the stand-in providers" was written as a guard, and what it actually left behind was a
    host that cannot start.** `OutboundProviderPolicy` admitted one name, `Simulated`, and refused it in Production,
    so between those two rules there was no value a Production host could name. Measured on this host:
    `ASPNETCORE_ENVIRONMENT=Production` aborts in `AddInfrastructure` on the missing `ConnectionStrings:Redis`, and
    with a Redis string supplied it aborts on both provider keys — and naming `Simulated` there is also refused — so
    the `migrator` and `api` services in `docker-compose.yml`, which both declare Production, have never come up
    anywhere. `DEP-01` adds the second selection, `None`, whose bindings refuse instead of inventing: a charge
    answers `422 PaymentFailed` naming its own configuration key and writes no ledger row, and a send throws, which
    leaves the reminder unrecorded and the outbox message unprocessed with its reason in `Error`.
    Writing that fact down corrected a second claim in the same pass: `ConfigureTestConfiguration` cannot reach a
    value the container reads while it is being registered. `AddInfrastructure` runs before `builder.Build()`, and
    `WebApplicationFactory`'s in-memory configuration source does not exist until it does — the first version of
    `NoOutboundProviderDeploymentTests` set both keys to `None` and then charged `txn_sim_f33c…` as `Paid`, because
    the host had already bound the simulator from `appsettings.Development.json`. Rule: a configuration hook reaches
    the reads that happen after it, not the ones that happened before, and a test host that pretends otherwise proves
    the opposite of what it says. Two controls say what the two halves of this fix are each worth: putting the
    fixture's override back on `ConfigureTestConfiguration` fails 4 of the 5 deployment facts and the boot fact
    reports a host whose configuration answers `None` while its payment provider is the simulator, while reverting
    only the `DependencyInjection` ternaries fails exactly one fact — `SelectingNone_MustBind…`, which runs the real
    `AddInfrastructure` — and leaves the deployment group green. Those five facts test what the providers *do*, and
    the only place the config-to-binding rule can be tested is registration time.

15. **`/health/ready` was written as the readiness gate and its own predicate made it the full health route.**
    `Predicate = check => check.Tags.Contains("ready") || true` is not a filter, and nothing had ever been tagged
    `ready`, so the three routes differed only in which line of `Program.cs` mapped them. Measured against the real
    host before the fix: all three answered the same 7-byte `Healthy`, and with a database the server cannot open
    `/health/ready` answered `503` for a dependency it was never supposed to be asking about. `DEP-02` tags the
    `Database` check, makes the predicate mean what it says, and answers the routes with the checks each ran —
    `{"status":"Healthy","checks":[{"name":"Database","status":"Healthy","durationMs":0.9}]}` on `/health/ready`,
    an empty `checks` array on `/health/live`, `200`/`503`/`200` respectively with the database unreachable
    (`docs/API.md` §10). Three claims died in the measuring. The framework's default health body on this runtime is
    **no body at all** (`ResponseWriter = null` answers an empty `200`), so a fact that only asserts "the body does
    not contain the connection coordinates" passes on a host that says nothing; the fact now requires the names and
    then requires the coordinates to be absent, and adding a `description` field to the writer fails it. Redis was
    not made a check, because it cannot be probed inside a probe budget: against an endpoint nobody listens on, the
    client's first command threw after **5,981 ms**, and a 2-second `CancellationToken` made it *later* (**7,114 ms**)
    rather than sooner, because cancellation is not honoured while the connection is still being established — while
    the cache's only runtime consumer, the idempotency store, is a copy of a SQL row and replays correctly with every
    cache call failing (`ACacheThatFailsEveryCall_…`). Rule: a gate has to be measured against a dependency that is
    actually down, and a control that passes when the thing it guards is deleted is guarding the test, not the code.

16. **"The Docker stack has never started" was four defects, and closing them killed a claim this file had
    inherited.** `DEP-03` began as "make the compose deployment usable by a client" and ended with the first image
    this repository ever produced.
    - **The image had never built.** The runtime stage's `adduser` does not exist in
      `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04.5; measured on the pulled base: `command -v adduser` finds
      nothing, `command -v useradd` finds it), so the layer ended with exit 127 and every statement anyone had made
      about running this stack was about a stack that could not exist. It is now `useradd --uid 10001 --user-group`,
      the container runs as uid 10001, and `EXPOSE` names the one port the process binds (`8080`; `8081` advertised a
      listener nothing had opened).
    - **Nothing had ever executed `DatabaseSeeder`.** `BooklyHubWebApplicationFactory` sets `AutoMigrateAndSeed=false`,
      so 341 integration facts migrated a schema and walked past the seeding path. The first five facts to call it
      (`ProductionSeedTests`) found a seeder whose only product was a scripted demo: three invented clinics, their
      staff, and patients with names and emails — written unconditionally into the tenant table of a client's
      production database, where no route can delete them. `Seed:DemoTenants` now opts that half in, default off, and
      the "already seeded" sentinel moved from `db.Tenants` to `db.Permissions`, which is the first row set the method
      writes unconditionally. Two controls, in a throwaway worktree: these same five facts against `5ae2e74` fail 3 of
      5, each with *found 3* tenants; and with only the sentinel line reverted — the opt-in kept — the second run
      throws `Violation of PRIMARY KEY constraint 'PK_Permissions'. The duplicate key value is
      (appointments.cancel)`. That second control is the reason the sentinel is a fix and not a tidying: an operator
      who flips `SEED_DEMO_TENANTS` after the first run gets a stack trace from a one-shot migrator container, and
      `depends_on: service_completed_successfully` holds the API down behind it.
    - **`SEC-13`'s residue was wrong about which half was live.** The claim was `UseHostFiltering()` had never been
      called, so `AllowedHosts` was dead config. Measured against `5ae2e74`, which contains no host-filtering code at
      all: `AllowedHosts=a.test` answered **400** to `Host: evil.test`. Minimal hosting installs the middleware and
      binds the key by itself; calling `UseHostFiltering()` here would only have added a second copy of it to every
      request. What was actually live is the set of spellings an operator can write that silently destroy the defence,
      because the host splits the key on `';'` alone and never trims: `"a.test, b.test"` — the natural way to write a
      list in a shell — became **one** unmatchable pattern and answered **400 to every Host, including the two it
      named**; `"a test"` and `"*a.test"` did the same; and `"a.test; *"` answered 200 for `a.test` and 400 for
      everything else, so the `*` never fires and a deployment that says "admit every host" while naming one gets the
      narrower answer. `ServingSurface` (`src/BooklyHub.Infrastructure/Security/ServingSurface.cs`) now reads the key
      — splits on `';'` and `','`, trims, treats `*` as "no narrowing", passes `*.suffix` through untouched — and
      refuses at startup the entries the matcher cannot honour, in the same direction `ForwardingSettings` already
      failed. 32 facts (`ServingSurfaceTests`) pin the reading; the live matrix under this file's usual rule (a
      pre-`Build()` read is beyond `ConfigureTestConfiguration`, §2.14) is `docs/DEPLOYMENT.md` §3, run against both
      trees.
    - **Naming the hosts made the container look dead.** The healthcheck curled `http://127.0.0.1:8080/health/live`,
      whose default `Host` is that address — not one of the names — so it got 400, the service never reported healthy,
      and an orchestrator honouring `service_healthy` restarts a working instance forever. The probe now asks as the
      first name the operator gave, and it has to survive every spelling `ServingSurface` accepts, because each one
      otherwise marks a healthy container unhealthy (all measured against the running stack): a comma list needs the
      comma cut as well as the `;` one; a bare `*` is not a `Host` at all — sending it answered **400 even with no
      filter installed** — and `*.a.test` admits a subdomain but not the suffix, so the probe asks
      `probe.a.test`. `HEALTH_PROBE_HOST` overrides the whole derivation.
    Two costs travel with the fix rather than hiding under it. `HTTPS_REDIRECT_PORT` set means **every** plain-HTTP
    request is redirected, `/health/live` included (measured `307` for an admitted Host, `400` for a foreign one,
    because host filtering runs first) — an orchestrator that demands `2xx` has to probe the HTTPS side or leave the
    port unset. And `docs/API.md` §10 now states which of these the test host cannot reach: the redirect and the
    filter are decided before `builder.Build()`, so no fixture can flip them and their evidence is live, not unit.

17. **Writing the handover document turned into an audit of `README.md`, and the front page was the least measured
    file in the repository.** `DEP-04` is a documentation change, so there is no failing test to quote as its control;
    instead every number it now prints was re-run or re-read for this commit, and four claims refused to survive that.
    - **The test counts were two orders of magnitude stale.** The front page said "23 tests" for the unit suite and
      "6 tests" for the integration suite. Measured on `503696a`: **290 unit facts (~3 s) and 346 integration facts
      (~33 s against LocalDB)**, both `Failed: 0`. Nobody had updated those figures since the first commit, which is
      how a README ends up advertising a fraction of the evidence that exists.
    - **The demo-data table described a database the seeder does not write.** It promised Apex 2 locations, 3
      dentists and 6 services with 3 treatment rooms and 2 X-ray machines, Luxe 4 stylists and 5 services and 4
      chairs, Pulse 3 trainers and 4 programs and 2 studios and 2 squat racks. `DatabaseSeeder.cs:95-263` writes, per
      tenant, **one** location and **one** staff member: Apex 3 services + 2 resource groups holding 3 resources +
      2 customers + 1 appointment with 1 review; Luxe 2 services + 1 group with 2 chairs + 1 customer; Pulse 2
      services, no resource groups, 1 customer. Total: 3 tenants, 3 locations, 3 staff, 7 services, 5 resources, 4
      customers. A client who reads the old table and counts the rows on arrival finds two thirds of the clinic
      missing, so the table now states what the block writes and calls it a demonstration of the data model rather
      than a data set.
    - **`Webhook Replay Protection` named a feature that does not exist.** `grep -rli webhook src --include=*.cs`
      returns **no source file** (the only hits under `src` are two build-output copies of `Microsoft.OpenApi.dll`,
      which is a reminder that a grep without `--include` in this tree answers from `bin/`) — the idempotency
      middleware is real, the webhook is not, and the phrase had been riding in the heading next to it. Same class
      one line further down: the front page said the dashboard runs "≤ 4 pure SQL set-based queries" while
      `NPlusOneQueryTests.cs:173` asserts **≤ 5**, and the comment above it explains the fifth is the
      stale-executions counter `P6-2` added. The README had been overtaken by its own tests.
    - **The quickstart's only UI instruction 404s on the deployment it documents.** "Open http://localhost:5000/swagger"
      — `5000` is the port *compose* publishes, and compose runs `ASPNETCORE_ENVIRONMENT=Production`, where Swagger is
      not mapped at all (`Program.cs:306` gates it on Development). The local `http` launch profile is `5089`. Both
      facts are now stated where the operator meets them, with `docs/API.md` named as the contract for a deployed
      instance — that file is the one with a test behind it (`DocumentedApiShapeTests`).
    Two smaller ones, both about pointing a reader at something that is not there: the badge and the clone URL addressed
    `github.com/BooklyHub/BooklyHub` while `git remote -v` says the origin is `github.com/dante127/BooklyHub`, so the CI
    badge could only ever render a repository that does not exist; and the header declared MIT while the tree had no
    `LICENSE` file — a declared license with no text is not a grant, and a client handover cannot ship one.

18. **`DEP-05` ran the published deployment end to end, and two documented claims did not survive the run**
    (`7b36f06`, `78f5510`, this commit). What was executed: `docker compose config`, the image build, the whole stack
    from an empty volume with `SEED_DEMO_TENANTS=true`, all eight steps of `docs/DEPLOYMENT.md` §7, and a booking walk
    over HTTP against it. The evidence is in §7 now, in the output's own words, so this entry records only what the
    walk cost the documentation:
    - **`docs/API.md` §4 said "Responses always carry `Z`".** They do not. `POST /api/v1/appointments` answered
      `"startAtUtc":"2026-10-08T12:30:00Z"`, and `GET /api/v1/appointments` plus `GET /api/v1/appointments/{id}`
      answered the **same row** as `"startAtUtc":"2026-10-08T12:30:00"` — no designator, for `startAtUtc`, `endAtUtc`
      and `createdAtUtc` alike. The mechanism is not mysterious: `datetime2` stores ticks and no zone, so a value EF
      materializes arrives `Kind=Unspecified` and System.Text.Json writes it naked, while the value the process wrote
      itself still carries the `Utc` label the request boundary gave it. This is a **labelling** defect, not a
      stored-value one — the booking guard, the cutoff policy and the dashboard all agree on the instant, and the row
      in the database is right — but a client that parses a naked ISO instant as its own local time shifts every
      appointment it lists, which is the `BL-05` failure mode arriving from the direction the tests never pointed.
      Tracked as `TIME-01` in §3. The claim is corrected to the measured spelling, with the read-side caveat stated
      as the contract until that lands.
    - **`docs/API.md` §7 said both catalog reads answer `400 Active tenant context is required.` when no tenant
      resolves.** True for the anonymous caller that was measured. False for the one authenticated caller this
      deployment can actually sign in as: the seeded platform administrator has no `tenant_id` claim, so
      `TenantResolutionMiddleware.cs:37` gives it `Guid.Empty` and `isPlatformAdmin: true`, and the read answers
      `200 []` instead of refusing. Nothing leaked — the filter matched no row — but the guard has a hole shaped
      exactly like "a client cannot tell an empty clinic from a missing header", and only a live walk finds it,
      because every fixture in the suite signs in as a tenant-scoped test user that does not exist on a fresh
      database.
    - Two ignore-file defects, each in its own commit because each was measured separately: **`.gitignore` listed
      `.dockerignore`**, so the file that decides the build context was untracked and therefore absent from every
      clone — a clean clone sent 172.16 kB of context where this tree sent 54.97 kB, and after the operator followed
      the README's own `dotnet build` it sent **306.93 MB in 15.7 s**. The shipped image was never affected (81
      entries in `/app`, publish output only), which is why this is a build-time cost rather than a leak. And
      **`.env` was not ignored** — the handover's first instruction is `cp .env.example .env` and fill it with the SA
      password, the signing secret and the seeding administrator's password, and a probe build of a clone printed all
      three marker values back out of the `COPY . .` layer while `git status` showed `?? .env`: one `git add -A` from
      being a second `SEC-02`. `.env.example` stays tracked; the probe can no longer find `.env`.
    - The CI claim this file has carried since `DEP-04` ("measured locally, CI run not yet observed") is closed by
      reading it: Actions run **#59** at `head_sha=b78fd40` is `completed / success`, #58 at `503696a` and #57 at
      `5ae2e74` likewise. `ci.yml` builds and runs the two suites and does **not** build the image or render the
      compose file, so the green badge is not evidence about `Dockerfile` — that evidence is now §7, produced by hand.
    - One host-side fact that is a note, not a defect: `docker-compose.yml:10,25` publish `127.0.0.1:1433` and
      `127.0.0.1:6379` as fixed numbers, and on this workstation both were already taken (a local SQL Server instance
      on 1433, another project's Redis on 6379), so `up -d` failed with `Ports are not available … forbidden by its
      access permissions` after building both images. §1 now states it and gives the `!override` recipe; a plain
      `ports:` in an override **appends** and fails the same way, which is the trap.


## 3. What is genuinely live, in the order the next work should take it

1. `TIME-01` — the first code defect this queue has received from a live deployment rather than from a test, and the
   only one that changes what a client's calendar renders. A `…AtUtc` field on the appointment **read** paths answers
   with no zone designator (`docs/DEPLOYMENT.md` §7 step 8 records both spellings of one row), because `datetime2`
   carries no zone and EF materializes `Kind=Unspecified`. The stored instant is correct; the label is missing, and a
   client parsing the naked value as local time moves every appointment it lists by its own offset. Fix it where the
   value enters the CLR object rather than in each DTO, so the next read path cannot reintroduce it, and pin it with
   an assertion on a **read**: the suite does check this designator, twice, at
   `UtcInstantBoundaryTests.cs:204,230` — both times on the `201` response of the booking that wrote the row, which
   is the one path that already carries it. 636 tests pass because the covered half is correct.
2. `PERF-04` residue — the availability day is the one anonymous response with no bound on it, and its size grows
   linearly with the candidate-staff roster (683 KB at 50 staff, `PERFORMANCE.md` §6). Bounding it needs a policy
   number and a decision about what a truncated day should say; `CAL-01`'s calendars and the two anonymous catalog
   reads are the same class of "invent behaviour" choice. What does *not* need a decision is the grid's cost: it was
   measured, it is 1.5-4.2 ms at a density no caller can request, and the prefilter over `CandidateStartsUtc` was
   refused on that number (`PERFORMANCE.md` §6). `DEP-05` added one measured fact to this item: the seeded working
   week makes a closed Saturday answer `isOpen: true` with `slots: []` — the response has no way to say "closed",
   only a way to say "open, nobody here".
3. `CAL-01` — Holidays / BusinessHours / AvailabilityExceptions have a reader in the booking guard and no route and no
   seed, so enforcing them means maintaining them by hand in the database. Measured while closing `PERF-05`: the case is
   stronger than "no route and no seed" for Holidays, which has **no writer in `src` at all**, so every deployed
   calendar table is empty and the guard's close-check is dead code until this is decided.
4. Onboarding, which `DEP-01`'s walk exposed and no finding ID owns: **the only writer of a `Tenant` row in `src` is
   `DatabaseSeeder.cs`**, and there is no tenant route (`api/v1/tenants` does not exist). `DEP-03` made the demo block
   opt-in (`Seed:DemoTenants`, default off) because a client's production tenant table must not arrive pre-filled,
   which sharpens rather than closes this: the default first run now leaves **zero** tenants, so the way to put the
   first customer's clinic into a fresh production database is still `INSERT` by hand — with the demo half as the only
   alternative, and it writes Apex Dental, Luxe Salon and Pulse Fitness (`America/New_York`, `America/Chicago`,
   `America/Los_Angeles`) plus their invented staff and patients. That is `SEC-03`'s tenant gate wearing a deployment
   hat: whoever decides who may create a tenant decides how a client gets one. `DEP-05` found the same gate on the
   *user* side, and it is sharper: the demo block writes staff and customers rows and **no users**, so on a seeded
   production database the only credential that exists is a platform administrator with no tenant, and there is no
   route that can create a tenant-scoped account (`Controllers/` has no user endpoint). Until onboarding is decided,
   a client cannot get a receptionist.
5. The two halves of `SEC-12` that this file has never been able to close from code — a stated **CORS policy** (which
   origins, if any, may send credentialed requests to this API) and **HSTS**. Absent CORS is fail-closed for browsers
   and the absence is a missing statement rather than an open door; both need the operator's deployment story, not a
   patch. (`DEP-03`'s host-header work settles the third thing this list used to call "the same class": the key was
   never dead, only unspellable — see §1 `SEC-13` and §2.16.)
6. Product decisions, not to be taken from this queue: `SEC-03`'s tenant gate, `BL-08`'s authorship and moderation,
   `DB-02`'s recurring-series definition, `QUAL-05`'s module split.
7. Operator action only: `SEC-02` — rotate the secret committed before `d767c25`.
8. One deployment measurement `DEP-05` did not get: `docs/DEPLOYMENT.md` §7 step 5 with `ALLOWED_HOSTS` **naming** a
   domain, against the compose stack (the host filter itself and the probe's `Host` derivation for every spelling are
   §3's tables, measured on a live host and on the running stack respectively). This is the one row of the checklist
   whose answer this repository is asserting from §3 rather than from §7's run.
