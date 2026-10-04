namespace BooklyHub.Domain.Entities.Identity;

/// <summary>
/// FAN-01: every sign-in mints a fresh seven-day credential and nothing ever stopped the pile. Three sign-ins
/// across a week were three live chains, thirty were thirty, and each one is a separate way into the account that
/// survives the password it was issued beside. <c>SEC-05c</c>'s logout ends the session whose credential it is
/// handed and <c>SEC-05b</c>'s burn ends the chain a replay names; neither bounds how many chains exist, so the
/// residue was measured as a number rather than argued.
///
/// Five is a deployment judgement, not a derivation. The product runs small clinics: one desk browser, one phone,
/// one tablet, one shared front-desk machine, and one spare for the day the browser cache is cleared. Below that,
/// an ordinary account starts losing its own sessions in silence — an eviction is deliberately not announced, so
/// there is no message that would tell the owner which device just stopped working. Well above it, and the finding
/// is only deferred: the point is that the set of credentials a stolen laptop can be joined by is bounded and small.
///
/// Deliberately not a <c>TenantSetting</c> column. A per-tenant knob here is a policy decision nobody in the
/// product has made yet, and it would add a fourth documented-default site to the clock-and-defaults residue
/// (<c>EXP-01</c>) for a value no tenant has asked to change.
/// </summary>
public static class SessionFanOutPolicy
{
    public const int MaxLiveSessions = 5;

    /// <summary>
    /// How many live credentials must be revoked to bring the account back to the cap, where
    /// <paramref name="liveSessions"/> is counted <em>after</em> the credential being minted now is added — so a
    /// fresh sign-in that reaches the cap evicts nothing, and one that passes it evicts exactly the overshoot.
    /// The consequence the controller depends on: this is never <c>liveSessions</c> itself, so the newest
    /// credential — the one the caller is holding — cannot be in the evicted set.
    /// </summary>
    public static int EvictionsNeeded(int liveSessions) => Math.Max(0, liveSessions - MaxLiveSessions);
}
