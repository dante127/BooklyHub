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
  **Residual (SWP-03, open — a policy question, not a race):** a fully paid booking nobody ever closed is
  recorded as an absence by the sweep, one tick after the balance skip if the payment arrives late. `NoShow`
  has no outgoing transition, so the attendance record cannot be restated afterwards through any endpoint; the
  amount stays refundable because the refund gate reads the payment's status, not the appointment's.
- A booking that still owes money is **not** closed. `PaymentLedger.ValidateCharge` refuses to charge a
  `NoShow`, so closing an unpaid row would write the balance off silently; the sweep leaves it `Confirmed`
  and counts it in its `SkippedWithBalance` result. Those rows are the population of
  `GET /api/v1/payments/outstanding-visits` (section 6), which is where a human decides whether to collect
  or to waive.
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
  balance. Two things about this path are worth knowing before anyone relies on the queue for control:
  `AppointmentCutoffPolicy.EnsureCancellable` runs only when the target is `Cancelled`, so a write-off is
  never gated by the tenant's cutoff, and the transition validator puts no requirement on `reason`, so an
  unexplained write-off of a real debt is accepted today. Both are open findings, recorded rather than
  changed here.
