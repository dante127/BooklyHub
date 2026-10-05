using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Exceptions;

namespace BooklyHub.Application.Appointments;

/// <summary>
/// A cutoff is one tenant policy with two verbs: how late an appointment may still be cancelled, and how
/// late it may still be moved. Cancel, the generic transition to Cancelled and reschedule all ask here, so
/// no path can be used as a door around a rule another path enforces, and an actor the tenant trusts to
/// override one cutoff is trusted to override the other.
/// </summary>
public static class AppointmentCutoffPolicy
{
    // A tenant with no settings row still gets a cutoff, and it gets the one the entity hands a tenant that did
    // create a row and never edited it. The two used to be copies of the same number written in two files.
    private const int DefaultCancellationCutoffHours = TenantSetting.DefaultCancellationCutoffHours;
    private const int DefaultReschedulingCutoffHours = TenantSetting.DefaultReschedulingCutoffHours;

    private static readonly string[] CutoffOverrideRoles =
    [
        Roles.PlatformAdmin,
        Roles.TenantOwner,
        Roles.TenantAdmin,
        Roles.Manager
    ];

    public static void EnsureCancellable(
        TenantSetting? settings,
        DateTime startAtUtc,
        DateTime nowUtc,
        ICurrentUser currentUser)
        => EnsureLeadTime(
            settings?.CancellationCutoffHours ?? DefaultCancellationCutoffHours,
            startAtUtc,
            nowUtc,
            currentUser,
            "CancellationCutoffExceeded",
            "cancelled");

    public static void EnsureReschedulable(
        TenantSetting? settings,
        DateTime startAtUtc,
        DateTime nowUtc,
        ICurrentUser currentUser)
        => EnsureLeadTime(
            settings?.ReschedulingCutoffHours ?? DefaultReschedulingCutoffHours,
            startAtUtc,
            nowUtc,
            currentUser,
            "RescheduleCutoffExceeded",
            "rescheduled");

    private static void EnsureLeadTime(
        int cutoffHours,
        DateTime startAtUtc,
        DateTime nowUtc,
        ICurrentUser currentUser,
        string ruleName,
        string verb)
    {
        if (CutoffOverrideRoles.Any(currentUser.IsInRole))
        {
            return;
        }

        if (startAtUtc - nowUtc < TimeSpan.FromHours(cutoffHours))
        {
            throw new BusinessRuleValidationException(
                ruleName,
                $"Appointments cannot be {verb} within {cutoffHours} hours of the start time.");
        }
    }
}
