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
- Every field above except the four `upcoming*` fields is counted inside `[fromUtc, toUtc]`.
- `upcomingCount`, `upcomingPendingCount`, `upcomingConfirmedCount` and `nextAppointmentAtUtc` are
  anchored on the server clock, not on `toUtc`: they count appointments that start after now and whose
  visit has not been closed yet (`Completed`, `Cancelled` and `NoShow` are excluded; a legacy `Rescheduled`
  row still owes a visit). Asking about a period that closed in 2020 therefore still reports what is owed
  next, and `nextAppointmentAtUtc` is null when nothing is.
- `confirmedCount` is a status count inside the period, so an appointment that has already started and was
  never marked `Completed` or `NoShow` still appears there. Nothing closes those rows automatically today.
