namespace BooklyHub.Domain.Exceptions;

public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }
}

public class BusinessRuleValidationException : DomainException
{
    public string RuleName { get; }

    public BusinessRuleValidationException(string ruleName, string message) : base(message)
    {
        RuleName = ruleName;
    }
}

public class InvalidStateTransitionException : DomainException
{
    public string CurrentState { get; }
    public string TargetState { get; }

    public InvalidStateTransitionException(string currentState, string targetState, string message) 
        : base(message)
    {
        CurrentState = currentState;
        TargetState = targetState;
    }
}

public class BookingConflictException : DomainException
{
    public BookingConflictException(string message) : base(message)
    {
    }
}

public class NotFoundException : DomainException
{
    public NotFoundException(string message) : base(message)
    {
    }
}

public class CrossTenantAccessViolationException : DomainException
{
    public CrossTenantAccessViolationException(string message = "Cross-tenant access is strictly prohibited.") 
        : base(message)
    {
    }
}
