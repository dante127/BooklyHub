# BooklyHub — REST API Reference & Specification

## 1. Global API Conventions

- **Base URL**: `/api/v1`
- **Content Type**: `application/json`
- **Timestamps**: All timestamps must be transmitted and received in ISO 8601 UTC format (e.g. `2026-10-25T14:30:00Z`).
- **Standard Headers**:
  - `Authorization`: `Bearer <jwt_token>` (Required for authenticated routes)
  - `X-Tenant-ID`: `<guid>` (Required for public endpoints when not authenticated)
  - `X-Correlation-ID`: `<string>` (Optional client tracing identifier)
  - `Idempotency-Key`: `<guid_or_string>` (Recommended for all state-changing `POST`/`PUT` operations)

### 1.1 The Error Envelope

Every failure the API answers is an `application/problem+json` document — including the failures raised
before any action runs. Which members are present depends on who produced the response, so the table is
the contract:

| Member | Present on | Meaning |
| :--- | :--- | :--- |
| `status` | every failure | the HTTP status, repeated inside the body |
| `title` | every failure | a short name for the failure; for a bare pipeline status it is the HTTP reason phrase |
| `instance` | every failure | the request path that produced it |
| `correlationId` | every failure | matches the `X-Correlation-Id` response header — the handle support searches logs by |
| `detail` | action-path failures | the explanation, e.g. which appointment was not found |
| `errors` | `400` validation failures | property name → messages |
| `rule` | `422` business-rule failures | the machine-readable rule name, so a client never string-matches prose |
| `type` | `401`/`403`/`404` from the pipeline | the RFC 9110 section for that status; absent on `429` and on the action path |
| `traceId` | pipeline failures | the W3C trace context id; the action path carries `correlationId` instead |

Two writers produce these documents and they are deliberately not merged:

- **An action ran and failed.** `ExceptionHandlingMiddleware` maps the exception type to a status
  (`ValidationException` → 400, `NotFoundException` → 404, `BookingConflictException` → 409,
  `InvalidStateTransitionException` / `BusinessRuleValidationException` → 422,
  `CrossTenantAccessViolationException` → 403, `UnauthorizedAccessException` → 401, anything else → 500).
- **No action ran.** Authentication's `401` challenge, the permission handler's `403`, a `404` for a path
  with no endpoint, a `429` from the rate limiter. `UseStatusCodePages` writes these through the same
  problem-details service, and it sits *inside* the exception handler, so a thrown exception is still
  answered by the mapping above rather than by a status page.

For a client that means: parse `status`, `title`, `instance` and `correlationId` unconditionally, and treat
`detail`, `errors`, `rule`, `type` and `traceId` as present only where the table says so.

A request the client abandons mid-flight is the one failure with no body: it is answered `499` and logged
at Warning, because a closed tab is not a server fault and must not be counted in the `5xx` rate. A
cancellation nobody at the client asked for (an internal deadline, a provider that hung) is still a `500`
and still logged at Error.


---

## 2. Authentication & Identity

### `POST /api/v1/auth/login`
Authenticates a user and returns an access token with a rotating refresh token.

**Request Payload:**
```json
{
  "email": "dr.smith@apexdental.com",
  "password": "Password123!"
}
```

**Response (200 OK):**
```json
{
  "userId": "d7a4697f-bc3a-4a69-8bc3-3b1451f28b43",
  "tenantId": "e1f13b64-897c-473d-9d78-b11c2ad4cb59",
  "email": "dr.smith@apexdental.com",
  "fullName": "Dr. Sarah Smith",
  "roles": ["Staff", "TenantAdmin"],
  "accessToken": "eyJhbGciOiJIUzI1NiIsIn...",
  "refreshToken": "4f5c9e2b-7c51-4e76-88cf-9a9be8525b6a",
  "expiresAtUtc": "2026-09-23T11:15:00Z"
}
```

---

## 3. Availability Engine

### `GET /api/v1/availability/slots`
Computes all available booking slots for a given service and date range.

**Query Parameters:**
- `locationId` (Guid, required)
- `serviceId` (Guid, required)
- `startDate` (DateOnly `yyyy-MM-dd`, required)
- `endDate` (DateOnly `yyyy-MM-dd`, required)
- `staffId` (Guid, optional)

**Response (200 OK):**
```json
[
  {
    "date": "2026-10-25",
    "slots": [
      {
        "startAtUtc": "2026-10-25T13:00:00Z",
        "endAtUtc": "2026-10-25T13:30:00Z",
        "staffId": "912389f4-1234-4567-8901-abcdef123456",
        "staffName": "Dr. Sarah Smith",
        "isAvailable": true
      },
      {
        "startAtUtc": "2026-10-25T13:30:00Z",
        "endAtUtc": "2026-10-25T14:00:00Z",
        "staffId": "912389f4-1234-4567-8901-abcdef123456",
        "staffName": "Dr. Sarah Smith",
        "isAvailable": true
      }
    ]
  }
]
```

---

## 4. Appointments

### `GET /api/v1/appointments`
The schedule reader: one paged list, filtered by location, staff, customer and status.

**Query parameters:** `locationId`, `staffId`, `customerId`, `status`, `fromUtc`, `toUtc`, `page` (default 1),
`pageSize` (default 20, clamped to 1..100). There is no `nowUtc` parameter, for the same reason the collection
queue has none: the window is decided by the server clock, so a caller cannot pull a not-yet-due booking into
the page by lying about the time.

**The default window.** Called with neither `fromUtc` nor `toUtc`, the list is `StartAtUtc >= now` — the
upcoming book, ordered nearest first. Before this, no bound meant every row the tenant had ever booked, and
the descending sort put the furthest future appointment on page 1, which is the one page a desk opens by
default and the least useful thing to find on it.

Supplying *either* bound opts out of the default completely; nothing is injected at the other end. A caller
who asks for `toUtc=2026-01-01` to browse history gets history — a default start of today bolted onto that
request would return an empty page, and an empty page from a schedule endpoint reads as an empty diary rather
than a bad query.

**`totalCount` counts the window, not the book.** Because the window is part of the query, the count on the
default page is the number of upcoming appointments, not the number of rows in the tenant. A caller that
wants the whole book has to ask for it with both bounds open.

**Ordering is `StartAtUtc`, then `Id`.** The tie-break is what makes paging correct rather than merely
deterministic-looking: two bookings in the same minute have no order between them, and SQL Server is free to
return them differently on each read. Without a tie-break that shows up as one booking appearing on two pages
and another never appearing at all — which is invisible at page 1 and only visible once the book is bigger
than one page. Note this is the second *ascending* reader (the collection queue is oldest-debt-first); the
dashboard's counters are aggregates and do not order at all.

**Not indexed for the default path (open, PERF-09 family).** The window filters `TenantId` + `StartAtUtc`
and sorts by `StartAtUtc, Id`; no index on the table leads with that pair. The three existing composite
indexes — `IX_Appointments_Tenant_Customer_StartAt`, `IX_Appointments_Tenant_Status_StartAt`,
`IX_Appointments_Tenant_Staff_TimeRange` — all lead with a column the default request does not supply, so
they serve the *filtered* variants and the default page reads via the `TenantId` seek and sorts. Recorded
here rather than migrated: an index is a schema change with a write cost, and the read that needs it has no
measured volume behind it yet.

### `POST /api/v1/appointments`
Atomically books an appointment with transactional concurrency locks.

**Request Payload:**
```json
{
  "locationId": "7b82405d-6548-4be0-80d1-d249fbf1b920",
  "serviceId": "50c2688b-1e7a-42fc-873b-5517173b060d",
  "staffId": "912389f4-1234-4567-8901-abcdef123456",
  "customerId": "89b7fa83-f36b-4e8c-bb01-9a997d8e8749",
  "startAtUtc": "2026-10-25T13:00:00Z",
  "notes": "First-time patient consultation"
}
```

**Response (201 Created):**
```json
{
  "id": "e3b0c442-98fc-1c14-9afb-4c8996fb9242",
  "tenantId": "e1f13b64-897c-473d-9d78-b11c2ad4cb59",
  "locationId": "7b82405d-6548-4be0-80d1-d249fbf1b920",
  "locationName": "Apex Dental - Downtown",
  "serviceId": "50c2688b-1e7a-42fc-873b-5517173b060d",
  "serviceName": "Comprehensive Dental Exam & Cleaning",
  "staffId": "912389f4-1234-4567-8901-abcdef123456",
  "staffName": "Dr. Sarah Smith",
  "customerId": "89b7fa83-f36b-4e8c-bb01-9a997d8e8749",
  "customerName": "Michael Scott",
  "startAtUtc": "2026-10-25T13:00:00Z",
  "endAtUtc": "2026-10-25T13:45:00Z",
  "durationMinutes": 45,
  "price": 120.00,
  "currency": "USD",
  "status": "Confirmed",
  "notes": "First-time patient consultation",
  "allocatedResourceIds": ["c1e2d3f4-5678-90ab-cdef-1234567890ab"],
  "createdAtUtc": "2026-09-23T10:15:30Z"
}
```

### `POST /api/v1/appointments/{id}/reschedule`
Atomically moves an appointment to a new slot while preserving the original appointment on failure.

**Request Payload:**
```json
{
  "newStartAtUtc": "2026-10-26T15:00:00Z",
  "reason": "Client requested change of schedule"
}
```

### `POST /api/v1/appointments/{id}/status`
Transitions an appointment status according to the domain state machine.

**Request Payload:**
```json
{
  "newStatus": "CheckedIn",
  "reason": "Patient arrived at clinic front desk"
}
```

**When `reason` stops being optional** (`/transition`, and `/cancel` as it always did):
- `newStatus: Cancelled` — the validator requires it, `BadRequest` 400, the same as `POST /{id}/cancel`. Before
  this the reason was a rule a caller could step around by choosing this URL (WRI-01).
- `newStatus: NoShow` (or any status `PaymentLedger.EndsCollection` names) while the appointment still owes
  money — `UnprocessableEntity` 422, rule `DebtWriteOffReasonRequired`, and the message carries the amount
  being stranded (`"60.00 of 100.00"`). The amount is read from the payment rows inside the same transaction,
  so it is the remainder after deposits, not the booked price.
- Leaving a `NoShow` for `Completed` — rule `NoShowRestatementRequiresReason`, also 422. The reason is the only
  place a correction of an absence is written down, since the status column cannot say both that the patient
  was absent and that she was in the chair (SWP-03).
- A settled booking closes with no reason at all: nothing is stranded, so nothing has to be explained.
- The refusal is the point: a `NoShow` cannot be charged and the outstanding queue lists only `Confirmed` and
  `Completed`, so writing an unexplained balance off takes the debt off every surface at once. Refused, the
  row stays `Confirmed` and stays on the queue.

### `POST /api/v1/appointments/recurring`
Creates a series of recurring appointments with configurable conflict policies (`SkipConflicts` or `AbortSeries`).

---

## 5. Reporting & Analytics

### `GET /api/v1/reports/dashboard`
Executes high-performance set-based SQL aggregations for executive reporting.

**Query Parameters:**
- `fromUtc` (DateTime, optional; defaults to 30 days ago)
- `toUtc` (DateTime, optional; defaults to now)

**Response (200 OK):**
```json
{
  "fromUtc": "2026-08-24T00:00:00Z",
  "toUtc": "2026-09-23T10:00:00Z",
  "totalAppointments": 142,
  "completedCount": 118,
  "confirmedCount": 15,
  "cancelledCount": 6,
  "noShowCount": 3,
  "grossRevenue": 17850.00,
  "cancellationRatePercent": 4.2,
  "noShowRatePercent": 2.1,
  "upcomingCount": 9,
  "upcomingPendingCount": 2,
  "upcomingConfirmedCount": 6,
  "nextAppointmentAtUtc": "2026-09-23T11:30:00Z",
  "staleExecutionCount": 1,
  "topServices": [
    {
      "serviceId": "50c2688b-1e7a-42fc-873b-5517173b060d",
      "serviceName": "Comprehensive Dental Exam & Cleaning",
      "bookingsCount": 65,
      "revenue": 7800.00
    }
  ],
  "topStaff": [
    {
      "staffId": "912389f4-1234-4567-8901-abcdef123456",
      "staffName": "Dr. Sarah Smith",
      "completedBookings": 58,
      "averageRating": 4.95
    }
  ]
}
```

**Counter semantics:**
- Every field above except `upcomingCount`, `upcomingPendingCount`, `upcomingConfirmedCount`,
  `nextAppointmentAtUtc` and `staleExecutionCount` is counted inside `[fromUtc, toUtc]`.
- `upcomingCount`, `upcomingPendingCount`, `upcomingConfirmedCount` and `nextAppointmentAtUtc` are
  anchored on the server clock, not on `toUtc`: they count appointments that start after now and whose
  visit has not been closed yet (`Completed`, `Cancelled` and `NoShow` are excluded; a legacy `Rescheduled`
  row still owes a visit). Asking about a period that closed in 2020 therefore still reports what is owed
  next, and `nextAppointmentAtUtc` is null when nothing is.
- `staleExecutionCount` is likewise anchored on the server clock: appointments that have already ended and
  are still `CheckedIn` or `InProgress`. The customer appeared, so these are the clinic's own recording gap
  and are deliberately not reported as no-shows.
- A `Confirmed` appointment whose visit window has passed is closed to `NoShow` by a background sweep every
  15 minutes, once per tenant, under an exclusive per-tenant application lock. The rule is
  `NoShowClosurePolicy`: more than 6 hours past `EndAtUtc` (grace measured from the end of the visit, so a
  late arrival is still an arrival) and no more than 14 days past it, so a first run cannot restate a
  period somebody already reported. The closure writes one `AppointmentStatusHistory` row attributed to
  `system:no-show-sweep` and sends no notification.
- If a staff member edits one of the appointments being closed after the sweep has read it, the row's
  `RowVersion` token makes the sweep's write fail rather than overwrite: the tenant's whole batch rolls back
  and is re-judged from a fresh read, up to three times. A booking the front desk closed as `Completed` is
  therefore never restated as `NoShow`, and a re-judgement never costs the neighbouring closures either —
  only the row that moved is re-evaluated. A tenant whose rows keep moving under the sweep is not judged in
  that tick; it is reported in `SkippedContendedTenant` and picked up by the next one, while every other
  tenant is still swept normally.
  Money moves the row it settles: `POST /api/v1/payments/charge` and `POST /api/v1/payments/refund` both end
  by restamping the appointment's audit columns, which bumps the same `RowVersion`, so a payment or refund that
  lands after the sweep has read the ledger is caught the same way and the re-judged batch asks the money
  question again instead of committing the closure. A refund racing an edit of the appointment still succeeds:
  that restamp is unconditional by id, because money a provider already captured must not come back as a
  failure.
  **A wrong absence is correctable (SWP-03, closed).** The sweep decides attendance from a timer, so it can
  close a visit that did happen — the patient was in the chair and nobody checked her in — and the row is then
  out of the completed counts and the revenue with nothing in the book saying so. `NoShow` therefore has
  exactly one outgoing transition, to `Completed`, and it requires a reason: the history row is the only place
  both the wrong closure and its correction survive. It reaches `Completed` and nothing else, so a correction
  cannot put a booking back on the schedule or re-arm a reminder, and the money question the closure ended
  simply restarts — a restated row is chargeable and listed as owed again, including one whose balance was
  waived as an absence. The refund path was never the problem: it reads the payment's status, not the
  appointment's, so the amount was always recoverable while the fact was not.
- A booking that still owes money is **not** closed. `PaymentLedger.ValidateCharge` refuses to charge a
  `NoShow`, so closing an unpaid row would write the balance off silently; the sweep leaves it `Confirmed`
  and counts it in its `SkippedWithBalance` result. Those rows are the population of
  `GET /api/v1/payments/outstanding-visits` (section 6), which is where a human decides whether to collect
  or to waive — and waiving on that human's behalf, through `POST /api/v1/appointments/{id}/transition`,
  now has to state the reason (section 4).
  **Residual (RPT-01, open):** `grossRevenue` sums `Appointment.Price` over the `Completed` and `Confirmed`
  rows in the period, so it is booked value, not money the ledger holds — an unpaid visit and a half-paid
  one inflate it the same way. The queue makes that gap visible and gives the amount actually outstanding;
  it does not change what the dashboard reports.

---

## 6. Payments

### `GET /api/v1/payments/outstanding-visits`
The collection queue: visits the clinic is past its window on and still owes money for.

**Permission:** `payments.read` (`TenantOwner`, `TenantAdmin`, `Manager`, `Receptionist`, `Accountant`;
`Staff` has none). Deliberately not `reports.read`, which `Receptionist` does not hold — the desk that
chases payment is the desk that needs this list. This is the first endpoint in the codebase gated on
`payments.read`; the permission existed in the role map with no surface behind it.

**Query parameters:** `page` (default 1) and `pageSize` (default 20, clamped to 1..100 exactly as the
appointment search clamps it). There is no `nowUtc` parameter: the window comes from the server clock, so a
caller cannot pull a not-yet-overdue booking into the queue by lying about the time.

**What is in the queue** — a row has to satisfy all four:
1. Its `Status` is in `AppointmentStatusSet.Collectable`: `Confirmed` or `Completed`. `Pending` is excluded
   because nothing was ever agreed to render; `CheckedIn`/`InProgress` because that row has to be closed
   before its money question can be answered at all (that is `staleExecutionCount`); `Cancelled` and
   `NoShow` because they end the visit with nothing owed. `Completed` is in both this set and `Closed`, and
   that is the point: the same status answers "is the visit over" and "is it paid" differently.
2. `EndAtUtc` is more than 6 hours in the past — `NoShowClosurePolicy.DeadlineUtc`, the same bound the sweep
   uses, so the two surfaces cannot disagree about when a visit became overdue. There is **no** lookback
   bound: the sweep needs one because it writes, a queue does not, because a debt from three months ago is
   still owed and dropping it from the list does not collect it.
3. The ledger balance is still open: captured payments (`Paid`, `PartiallyRefunded`, `Refunded`) minus
   settled refunds, against the price the booking was made at. A free booking owes nothing and is not
   listed; a visit refunded back to zero is listed again.
4. It belongs to the calling tenant. A platform admin passing `X-Tenant-Id` has the global filter open, so
   this predicate is the only scoping left on the way.

`amountDue` comes from `PaymentLedger.Outstanding`, the same object the charge path enforces its limits
with, so the queue never invites an amount the ledger would refuse. Membership, however, is decided in SQL
(`PaymentLedgerQuery.WhereOwing`), because a queue that filtered in memory would have to load every
overdue booking to show a dozen. That is one rule written twice, and
`OutstandingVisitQueueTests.EveryPaymentShape_SqlMembershipAndAmountsMustMatchTheLedger` runs both forms
over thirteen payment shapes in one book: no rows, wholly paid, partly paid, refunded to zero, partly
refunded, two payments that settle, two that do not, a capture that never happened, an attempt still
`Pending`, three statuses refused on status, one row refused on the window.

**Response:** `PaginatedList<OutstandingVisitDto>`, oldest debt first (`EndAtUtc`, then `Id`, so a page can
neither repeat a booking nor drop one between two page reads).

```json
{
  "items": [
    {
      "appointmentId": "0b6a2b3c-2f77-4b1c-9d0e-1a2b3c4d5e6f",
      "customerId": "6a71c2d0-9e3b-4f11-8a55-2b3c4d5e6f70",
      "customerName": "Ziad Patient",
      "serviceName": "Comprehensive Dental Exam & Cleaning",
      "staffName": "Dr. Sarah Smith",
      "endAtUtc": "2026-09-28T11:30:00Z",
      "price": 120.00,
      "netPaid": 40.00,
      "amountDue": 80.00,
      "currency": "USD",
      "status": "Confirmed"
    }
  ],
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 7,
  "totalPages": 1,
  "hasNextPage": false,
  "hasPreviousPage": false
}
```

**Closing a row.** The queue only reads; the write verbs already existed and are what a caller uses next:
- `POST /api/v1/payments/charge` (`payments.manage`, honours `Idempotency-Key`) — collecting the `amountDue`
  shown clears the row. Charging past the price is refused on the ledger, with
  `AppointmentAlreadyPaid` or `PaymentExceedsAmountDue`.
- `POST /api/v1/payments/refund` (`payments.refund`) — draws against one payment's remaining balance, so a
  refund back to zero puts the visit into the queue again.
  Both of those write the appointment row as well as the money rows: they restamp its audit columns, which
  moves its `RowVersion` and is what lets the no-show sweep notice a ledger that changed under a closure it had
  already decided. A client reading `lastModifiedAtUtc`/`lastModifiedBy` on an appointment will therefore see
  money writes in it, not only status edits.
- `POST /api/v1/appointments/{id}/transition` with `newStatus: NoShow` (`appointments.update`) — waiving the
  balance. The queue only lists `Confirmed` and `Completed`, and a `NoShow` cannot be charged, so this verb
  removes a real debt from every collectable surface in one call. It now says so: with a balance still open
  the transition is refused `422 DebtWriteOffReasonRequired` until a `reason` is supplied, which is the
  written-down justification the money question needs. One property of this path is still open and recorded
  rather than changed here: `AppointmentCutoffPolicy.EnsureCancellable` runs only when the target is
  `Cancelled`, so a write-off is never gated by the tenant's cancellation cutoff.
