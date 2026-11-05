namespace CopilotHive.Security;

/// <summary>
/// Why a principal resolution REFUSED the caller. Every value here is a FAIL-CLOSED outcome: the
/// resolution carries no principal at all, so a caller cannot accidentally treat a refusal as an
/// anonymous or open-mode identity.
/// </summary>
public enum PrincipalRefusalReason
{
    /// <summary>No refusal — the resolution produced a principal.</summary>
    None = 0,

    /// <summary>The supplied principal was absent or not authenticated.</summary>
    Unauthenticated = 1,

    /// <summary>The authenticated principal carried no GitHub identity claim (<c>ClaimTypes.NameIdentifier</c>).</summary>
    MissingGitHubIdentity = 2,

    /// <summary>No ACTIVATED account exists for that GitHub identity (for example a never-invited or still-pending user).</summary>
    UnknownAccount = 3,

    /// <summary>The account exists but is DISABLED. Original identity never overrides disabled state.</summary>
    DisabledAccount = 4,

    /// <summary>The persisted role is not a supported role, so it is refused rather than defaulted.</summary>
    UnknownRole = 5,

    /// <summary>The trusted directory read failed, so nothing could be verified (never treated as success).</summary>
    DirectoryFailure = 6,

    /// <summary>
    /// The <see cref="CircuitUserPrincipalAccessor"/> was never initialised while authentication is
    /// enabled, so there is no identity to resolve.
    /// </summary>
    AccessorNotInitialized = 7,
}

/// <summary>
/// The result of ONE principal resolution: either an immutable
/// <see cref="ActingUserPrincipal"/> or an explicit, secret-free refusal.
/// <para>
/// A refusal carries <see cref="Principal"/> = <c>null</c> and a
/// <see cref="PrincipalRefusalReason"/>, so fail-closed handling is structural: there is no
/// "anonymous principal" to grant anything to, and an anonymous failure can never be read as open
/// mode.
/// </para>
/// </summary>
public sealed record PrincipalResolution
{
    private PrincipalResolution(
        ActingUserPrincipal? principal,
        PrincipalRefusalReason refusalReason,
        string? refusalDetail)
    {
        Principal = principal;
        RefusalReason = refusalReason;
        RefusalDetail = refusalDetail;
    }

    /// <summary>The resolved acting principal, or <c>null</c> when the resolution refused.</summary>
    public ActingUserPrincipal? Principal { get; }

    /// <summary><see cref="PrincipalRefusalReason.None"/> when resolved; otherwise the refusal reason.</summary>
    public PrincipalRefusalReason RefusalReason { get; }

    /// <summary>
    /// Secret-free explanation of a refusal (for logs and diagnostics), or <c>null</c> when resolved.
    /// It never contains credential material.
    /// </summary>
    public string? RefusalDetail { get; }

    /// <summary>Whether this resolution produced a principal.</summary>
    public bool IsResolved => Principal is not null;

    /// <summary>Creates a successful resolution.</summary>
    /// <param name="principal">The immutable acting principal.</param>
    /// <returns>The resolved result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="principal"/> is null.</exception>
    public static PrincipalResolution Resolved(ActingUserPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new PrincipalResolution(principal, PrincipalRefusalReason.None, refusalDetail: null);
    }

    /// <summary>Creates a fail-closed refusal that carries NO principal.</summary>
    /// <param name="reason">The refusal reason; must not be <see cref="PrincipalRefusalReason.None"/>.</param>
    /// <param name="refusalDetail">Optional secret-free explanation.</param>
    /// <returns>The refusal result.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is <see cref="PrincipalRefusalReason.None"/> or undefined.</exception>
    public static PrincipalResolution Refused(
        PrincipalRefusalReason reason,
        string? refusalDetail = null)
    {
        if (reason is PrincipalRefusalReason.None || !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason), reason, "A refusal must carry a concrete, defined refusal reason.");
        }

        return new PrincipalResolution(
            principal: null,
            refusalReason: reason,
            refusalDetail: refusalDetail);
    }
}
