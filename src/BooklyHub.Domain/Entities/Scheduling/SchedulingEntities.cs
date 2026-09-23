using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.StaffMembers;

namespace BooklyHub.Domain.Entities.Scheduling;

public class WorkingHour : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid StaffId { get; set; }
    public Staff? Staff { get; set; }

    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    public DayOfWeek DayOfWeek { get; set; }
    public bool IsWorkingDay { get; set; } = true;

    public ICollection<WorkingHourInterval> Intervals { get; set; } = [];

    public WorkingHour()
    {
        Id = Guid.NewGuid();
    }
}

public class WorkingHourInterval : Entity<Guid>
{
    public Guid WorkingHourId { get; set; }
    public WorkingHour? WorkingHour { get; set; }

    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public bool IsBreak { get; set; } // true if lunch/coffee break, false if working shift

    public WorkingHourInterval()
    {
        Id = Guid.NewGuid();
    }

    public WorkingHourInterval(TimeSpan startTime, TimeSpan endTime, bool isBreak = false)
    {
        Id = Guid.NewGuid();
        StartTime = startTime;
        EndTime = endTime;
        IsBreak = isBreak;
    }
}

public class Holiday : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid? LocationId { get; set; } // Null for all locations in the tenant
    public Location? Location { get; set; }

    public string Name { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool RecurringAnnually { get; set; }

    public Holiday()
    {
        Id = Guid.NewGuid();
    }
}

public class AvailabilityException : Entity<Guid>, ITenantEntity
{
    public Guid TenantId { get; set; }
    public Guid StaffId { get; set; }
    public Staff? Staff { get; set; }

    public DateTime StartDateTimeUtc { get; set; }
    public DateTime EndDateTimeUtc { get; set; }
    public bool IsAvailable { get; set; } // false = time off / sick leave; true = custom working shift
    public string? Reason { get; set; }

    public AvailabilityException()
    {
        Id = Guid.NewGuid();
    }
}
