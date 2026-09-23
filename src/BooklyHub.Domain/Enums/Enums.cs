namespace BooklyHub.Domain.Enums;

public enum AppointmentStatus
{
    Pending = 1,
    Confirmed = 2,
    CheckedIn = 3,
    InProgress = 4,
    Completed = 5,
    Cancelled = 6,
    NoShow = 7,
    Rescheduled = 8
}

public enum PaymentStatus
{
    Pending = 1,
    Authorized = 2,
    Paid = 3,
    Failed = 4,
    Refunded = 5,
    PartiallyRefunded = 6
}

public enum PaymentProviderType
{
    Simulated = 1,
    Stripe = 2
}

public enum PaymentTransactionType
{
    Charge = 1,
    Refund = 2
}

public enum RecurrencePattern
{
    Daily = 1,
    Weekly = 2,
    Biweekly = 3,
    Monthly = 4
}

public enum RecurrenceConflictPolicy
{
    SkipConflicts = 1,
    AbortSeries = 2
}

public enum OutboxMessageStatus
{
    Pending = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4
}
