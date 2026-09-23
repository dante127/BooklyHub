using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Exceptions;

namespace BooklyHub.Domain.Entities.Reviews;

public class Review : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }

    public Guid AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid StaffId { get; set; }
    public Staff? Staff { get; set; }

    public Guid ServiceId { get; set; }
    public Service? Service { get; set; }

    public int Rating { get; set; } // 1 to 5
    public string? Comment { get; set; }
    public string? Response { get; set; }
    public bool IsVerified { get; set; } = true;
    public bool IsPublished { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public Review()
    {
        Id = Guid.NewGuid();
    }

    public static Review Create(
        Guid tenantId,
        Guid appointmentId,
        Guid customerId,
        Guid staffId,
        Guid serviceId,
        int rating,
        string? comment = null,
        string? createdBy = null)
    {
        if (rating is < 1 or > 5)
        {
            throw new BusinessRuleValidationException("InvalidRating", "Rating must be between 1 and 5 stars.");
        }

        return new Review
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppointmentId = appointmentId,
            CustomerId = customerId,
            StaffId = staffId,
            ServiceId = serviceId,
            Rating = rating,
            Comment = comment,
            IsVerified = true,
            IsPublished = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }
}
