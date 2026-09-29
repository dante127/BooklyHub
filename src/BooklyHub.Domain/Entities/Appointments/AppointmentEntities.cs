using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Resources;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Enums;
using BooklyHub.Domain.Events;
using BooklyHub.Domain.Exceptions;

namespace BooklyHub.Domain.Entities.Appointments;

public class Appointment : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity, IConcurrencyEntity
{
    public Guid TenantId { get; set; }
    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    public Guid ServiceId { get; set; }
    public Service? Service { get; set; }

    public Guid StaffId { get; set; }
    public Staff? Staff { get; set; }

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid? RecurringAppointmentId { get; set; }
    public RecurringAppointment? RecurringAppointment { get; set; }

    public DateTime StartAtUtc { get; set; }
    public DateTime EndAtUtc { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";

    public AppointmentStatus Status { get; private set; } = AppointmentStatus.Pending;

    public string? CancellationReason { get; private set; }
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }

    /// <summary>One key buys one booking; see the charge path on Payment for the same contract.</summary>
    public string? IdempotencyKey { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public ICollection<AppointmentResource> AppointmentResources { get; set; } = [];
    public ICollection<AppointmentStatusHistory> StatusHistories { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public Appointment()
    {
        Id = Guid.NewGuid();
    }

    public static Appointment Create(
        Guid tenantId,
        Guid locationId,
        Guid serviceId,
        Guid staffId,
        Guid customerId,
        DateTime startAtUtc,
        DateTime endAtUtc,
        int durationMinutes,
        decimal price,
        string currency = "USD",
        string? notes = null,
        Guid? recurringAppointmentId = null,
        string? createdBy = null)
    {
        if (endAtUtc <= startAtUtc)
        {
            throw new BusinessRuleValidationException("InvalidTimeRange", "Appointment EndAtUtc must be greater than StartAtUtc.");
        }

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LocationId = locationId,
            ServiceId = serviceId,
            StaffId = staffId,
            CustomerId = customerId,
            StartAtUtc = startAtUtc,
            EndAtUtc = endAtUtc,
            DurationMinutes = durationMinutes,
            Price = price,
            Currency = currency,
            Notes = notes,
            RecurringAppointmentId = recurringAppointmentId,
            Status = AppointmentStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        appointment.StatusHistories.Add(new AppointmentStatusHistory
        {
            AppointmentId = appointment.Id,
            FromStatus = null,
            ToStatus = AppointmentStatus.Pending,
            ChangedAtUtc = DateTime.UtcNow,
            ChangedBy = createdBy,
            Reason = "Initial booking creation"
        });

        appointment.AddDomainEvent(new AppointmentCreatedEvent(
            tenantId,
            appointment.Id,
            customerId,
            staffId,
            serviceId,
            startAtUtc,
            endAtUtc));

        return appointment;
    }

    public bool CanTransitionTo(AppointmentStatus newStatus)
    {
        return (Status, newStatus) switch
        {
            (AppointmentStatus.Pending, AppointmentStatus.Confirmed) => true,
            (AppointmentStatus.Pending, AppointmentStatus.Cancelled) => true,

            (AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn) => true,
            (AppointmentStatus.Confirmed, AppointmentStatus.Completed) => true,
            (AppointmentStatus.Confirmed, AppointmentStatus.Cancelled) => true,
            (AppointmentStatus.Confirmed, AppointmentStatus.NoShow) => true,

            (AppointmentStatus.CheckedIn, AppointmentStatus.InProgress) => true,
            (AppointmentStatus.CheckedIn, AppointmentStatus.Completed) => true,
            (AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled) => true,
            (AppointmentStatus.CheckedIn, AppointmentStatus.NoShow) => true,

            (AppointmentStatus.InProgress, AppointmentStatus.Completed) => true,
            (AppointmentStatus.InProgress, AppointmentStatus.Cancelled) => true,

            _ => false
        };
    }

    /// <summary>
    /// Returns the appended history row. A caller changing an appointment that is already stored has to
    /// register that row with the context as new: a row that reaches the change tracker only through the
    /// StatusHistories navigation of a tracked appointment is treated as an existing row, and the update
    /// it generates matches nothing.
    /// </summary>
    public AppointmentStatusHistory TransitionTo(AppointmentStatus newStatus, DateTime nowUtc, string? reason = null, string? changedBy = null)
    {
        if (!CanTransitionTo(newStatus))
        {
            throw new InvalidStateTransitionException(
                Status.ToString(),
                newStatus.ToString(),
                $"Transition from {Status} to {newStatus} is not permitted.");
        }

        // Execution statuses describe something happening at the appointment, so they may not be set
        // before the appointment could conceivably be happening; anything earlier is a data entry error
        // that would corrupt reporting and the reminder sweep.
        var tooEarly = newStatus switch
        {
            AppointmentStatus.CheckedIn => nowUtc < StartAtUtc - TimeSpan.FromHours(1),
            AppointmentStatus.InProgress => nowUtc < StartAtUtc - TimeSpan.FromMinutes(15),
            AppointmentStatus.Completed => nowUtc < StartAtUtc,
            AppointmentStatus.NoShow => nowUtc < StartAtUtc,
            _ => false
        };

        if (tooEarly)
        {
            throw new BusinessRuleValidationException(
                "TransitionTooEarly",
                $"Cannot mark an appointment as {newStatus} before its start time ({StartAtUtc:O}).");
        }

        var previousStatus = Status;
        Status = newStatus;

        if (newStatus == AppointmentStatus.Cancelled)
        {
            CancellationReason = reason;
        }

        var history = new AppointmentStatusHistory
        {
            AppointmentId = Id,
            FromStatus = previousStatus,
            ToStatus = newStatus,
            ChangedAtUtc = DateTime.UtcNow,
            ChangedBy = changedBy,
            Reason = reason
        };
        StatusHistories.Add(history);

        // Trigger domain events
        switch (newStatus)
        {
            case AppointmentStatus.Confirmed:
                AddDomainEvent(new AppointmentConfirmedEvent(TenantId, Id, CustomerId, StartAtUtc));
                break;
            case AppointmentStatus.Cancelled:
                AddDomainEvent(new AppointmentCancelledEvent(TenantId, Id, CustomerId, reason ?? "Cancelled by user"));
                break;
            case AppointmentStatus.Completed:
                AddDomainEvent(new AppointmentCompletedEvent(TenantId, Id, CustomerId, StaffId, ServiceId));
                break;
        }

        return history;
    }

    /// <summary>Moves the appointment in place and keeps it active. Returns the appended history row, which carries the same persistence rule as <see cref="TransitionTo"/>.</summary>
    public AppointmentStatusHistory Reschedule(DateTime newStartAtUtc, DateTime newEndAtUtc, string? reason = null, string? changedBy = null)
    {
        if (newEndAtUtc <= newStartAtUtc)
        {
            throw new BusinessRuleValidationException("InvalidTimeRange", "Rescheduled EndAtUtc must be greater than StartAtUtc.");
        }

        if (Status != AppointmentStatus.Confirmed && Status != AppointmentStatus.Pending)
        {
            throw new InvalidStateTransitionException(
                Status.ToString(),
                AppointmentStatus.Rescheduled.ToString(),
                $"Cannot reschedule an appointment in {Status} state.");
        }

        var oldStart = StartAtUtc;
        StartAtUtc = newStartAtUtc;
        EndAtUtc = newEndAtUtc;
        DurationMinutes = (int)(newEndAtUtc - newStartAtUtc).TotalMinutes;

        var history = new AppointmentStatusHistory
        {
            AppointmentId = Id,
            FromStatus = Status,
            ToStatus = Status, // Remains active with updated times
            ChangedAtUtc = DateTime.UtcNow,
            ChangedBy = changedBy,
            Reason = $"Rescheduled from {oldStart:O} to {newStartAtUtc:O}. Reason: {reason}"
        };
        StatusHistories.Add(history);

        AddDomainEvent(new AppointmentRescheduledEvent(TenantId, Id, CustomerId, oldStart, newStartAtUtc, newEndAtUtc, reason));

        return history;
    }
}

public class AppointmentStatusHistory : Entity<Guid>
{
    public Guid AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }

    public AppointmentStatus? FromStatus { get; set; }
    public AppointmentStatus ToStatus { get; set; }
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ChangedBy { get; set; }
    public string? Reason { get; set; }

    public AppointmentStatusHistory()
    {
        Id = Guid.NewGuid();
    }
}

public class RecurringAppointment : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public RecurrencePattern Pattern { get; set; }
    public int Interval { get; set; } = 1; // every N days/weeks/months
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? NumberOfOccurrences { get; set; }
    public int CreatedAppointmentsCount { get; set; }

    public ICollection<Appointment> Appointments { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public RecurringAppointment()
    {
        Id = Guid.NewGuid();
    }
}
