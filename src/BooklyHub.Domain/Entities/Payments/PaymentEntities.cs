using BooklyHub.Domain.Common;
using BooklyHub.Domain.Entities.Appointments;
using BooklyHub.Domain.Enums;

namespace BooklyHub.Domain.Entities.Payments;

public class Payment : AggregateRoot<Guid>, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public PaymentProviderType Provider { get; set; } = PaymentProviderType.Simulated;
    public string? ProviderPaymentId { get; set; }
    public string? IdempotencyKey { get; set; }

    public ICollection<PaymentTransaction> Transactions { get; set; } = [];
    public ICollection<Refund> Refunds { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? LastModifiedBy { get; set; }

    public Payment()
    {
        Id = Guid.NewGuid();
    }
}

public class PaymentTransaction : Entity<Guid>
{
    public Guid PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public decimal Amount { get; set; }
    public PaymentTransactionType Type { get; set; } = PaymentTransactionType.Charge;
    public PaymentStatus Status { get; set; } = PaymentStatus.Paid;
    public string? ProviderTransactionId { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public PaymentTransaction()
    {
        Id = Guid.NewGuid();
    }
}

public class Refund : Entity<Guid>
{
    public Guid PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Refunded;
    public string? ProviderRefundId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Refund()
    {
        Id = Guid.NewGuid();
    }
}
