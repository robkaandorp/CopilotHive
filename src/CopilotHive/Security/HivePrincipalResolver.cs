using System.Security.Claims;

using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Security;

/// <summary>
/// Maps an AUTHENTICATED GitHub <see cref="ClaimsPrincipal"/> onto the account CopilotHive actually
/// persists, and fails CLOSED when it cannot.
/// <para>
/// ON EVERY RESOLUTION this resolver reads the GitHub identity claim
/// (<see cref="ClaimTypes.NameIdentifier"/>) from the supplied principal and then re-reads the
/// account through the trusted directory — <see cref="UserService.GetByGitHubIdAsync"/> — building
/// the immutable <see cref="ActingUserPrincipal"/> from CURRENT stored state. No role, enabled flag
/// or name from the cookie/claims is ever treated as authorization truth: a cookie that still says
/// "admin" for an account since demoted to Viewer or disabled resolves to a Viewer, or to a refusal.
/// </para>
/// <para>
/// The original/bootstrap account is identified by comparing the resolved id to
/// <see cref="UserService.GetAdminUserAsync"/> (the lowest-id activated row) — one directory read.
/// That flag confers no authority: because role and enabled state always come from current stored
/// state, the ORIGINAL identity never overrides a disabled or demoted account.
/// </para>
/// <para>
/// FAIL-CLOSED OUTCOMES: an unauthenticated principal, a missing/blank GitHub identity claim, no
/// persisted activated account, a DISABLED account, an unrecognised persisted role and a failed
/// directory read all produce an explicit <see cref="PrincipalResolution"/> REFUSAL that carries no
/// principal at all. A refusal is never an exception (a caller must not be able to turn it into
/// "allow on error") and never an open-mode principal, so an anonymous failure can never select open
/// mode.
/// </para>
/// <para>
/// OPEN MODE is an EXPLICIT input (<see cref="HiveAuthMode.Open"/>), never inferred here: in that
/// mode the resolver yields the full <see cref="ActingUserPrincipal.LocalOperator"/> principal for
/// backward compatibility, and does so without consulting the directory. While authentication is
/// enabled that branch is unreachable, so no failed identity can fall through to it.
/// </para>
/// <para>
/// REGISTRATION: registered as a SINGLETON and therefore stateless with respect to users — it holds
/// the configured mode and the trusted directory only, and NEVER retains a resolved principal. The
/// credential-bearing <see cref="UserEntity"/> a read returns is projected immediately and then
/// dropped, so no secret outlives the call.
/// </para>
/// </summary>
public sealed class HivePrincipalResolver
{
    /// <summary>
    /// The fixed fragment of the refusal text <see cref="UserRoleText.Parse"/> raises for an
    /// unrecognised persisted role. A persisted-role conversion failure reaches this resolver as an
    /// <see cref="InvalidOperationException"/> surfaced by the EF Core read, so this fragment is the
    /// only way to tell an unreadable ROLE apart from any other directory failure. If that text ever
    /// changes, such a row is still REFUSED — it simply degrades to
    /// <see cref="PrincipalRefusalReason.DirectoryFailure"/> — so the classification can never become
    /// more permissive.
    /// </summary>
    private const string UnknownPersistedRoleMessageFragment = "is not a supported CopilotHive user role";

    private readonly UserService _userService;
    private readonly HiveAuthMode _mode;
    private readonly ILogger<HivePrincipalResolver> _logger;

    /// <summary>
    /// Initialises a new <see cref="HivePrincipalResolver"/>.
    /// </summary>
    /// <param name="userService">The trusted user directory (the ONLY identity source).</param>
    /// <param name="mode">The EXPLICIT authentication mode supplied by the host.</param>
    /// <param name="logger">Logger used for secret-free refusal diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public HivePrincipalResolver(
        UserService userService,
        HiveAuthMode mode,
        ILogger<HivePrincipalResolver> logger)
    {
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The EXPLICIT authentication mode this resolver was configured with.</summary>
    public HiveAuthMode Mode => _mode;

    /// <summary>
    /// Resolves the acting principal for <paramref name="claimsPrincipal"/> from CURRENT stored state.
    /// </summary>
    /// <param name="claimsPrincipal">
    /// The authenticated principal of the current request/circuit, or <c>null</c>/an unauthenticated
    /// principal when nobody is signed in. In open mode it is ignored entirely.
    /// </param>
    /// <param name="ct">Cancellation token; cancellation is never converted into a refusal.</param>
    /// <returns>
    /// A resolved principal, or a <see cref="PrincipalResolution"/> refusal that carries no principal.
    /// </returns>
    public async Task<PrincipalResolution> ResolveAsync(ClaimsPrincipal? claimsPrincipal, CancellationToken ct)
    {
        // OPEN MODE: an EXPLICIT host decision (both OAuth env vars unset). Only here may a principal
        // exist without a persisted account; this branch is unreachable while auth is enabled.
        if (!_mode.AuthEnabled)
        {
            return PrincipalResolution.Resolved(ActingUserPrincipal.LocalOperator());
        }

        if (claimsPrincipal?.Identity?.IsAuthenticated != true)
        {
            return Refuse(
                PrincipalRefusalReason.Unauthenticated,
                "The caller is not authenticated.",
                subject: null);
        }

        // The GitHub numeric id is the ONLY claim used, and only as an IDENTITY to look up — never as
        // authorization truth. A blank claim value is refused instead of being looked up.
        var gitHubId = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(gitHubId))
        {
            return Refuse(
                PrincipalRefusalReason.MissingGitHubIdentity,
                "The authenticated principal carries no GitHub identity claim.",
                subject: null);
        }

        // Re-read the account on EVERY resolution so role/enabled changes take effect immediately.
        UserEntity? account;
        try
        {
            account = await _userService.GetByGitHubIdAsync(gitHubId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A directory read that cannot be completed proves nothing, so it is a refusal — never a
            // success and never a fallback principal.
            return RefuseReadFailure(ex, gitHubId);
        }

        if (account is null)
        {
            return Refuse(
                PrincipalRefusalReason.UnknownAccount,
                "No activated CopilotHive account exists for the authenticated GitHub identity.",
                subject: gitHubId);
        }

        // Project into locals IMMEDIATELY: the credential-bearing entity is never retained past this
        // block, so no OAuth material can leak into a principal, a field or a log.
        var userId = account.Id;
        var storedGitHubId = account.GitHubId;
        var role = account.Role;
        var enabled = account.Enabled;

        // A disabled account is refused BEFORE any status (including originality) is considered: the
        // original identity never overrides disabled state.
        if (!enabled)
        {
            return Refuse(
                PrincipalRefusalReason.DisabledAccount,
                "The account is disabled.",
                subject: storedGitHubId);
        }

        // One directory read identifies the original/bootstrap account (lowest-id activated row).
        int? originalUserId;
        try
        {
            var original = await _userService.GetAdminUserAsync(ct).ConfigureAwait(false);
            originalUserId = original?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RefuseReadFailure(ex, storedGitHubId);
        }

        try
        {
            var principal = ActingUserPrincipal.ForActivatedAccount(
                userId,
                storedGitHubId,
                role,
                enabled,
                isOriginal: originalUserId == userId);

            return PrincipalResolution.Resolved(principal);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An undefined role value (or a non-positive id) means the stored row is unreadable: a
            // refusal, never a default role.
            return Refuse(
                PrincipalRefusalReason.UnknownRole,
                "The persisted account role is not a supported CopilotHive role.",
                subject: storedGitHubId);
        }
        catch (ArgumentException)
        {
            return Refuse(
                PrincipalRefusalReason.DirectoryFailure,
                "The persisted account row cannot form a principal (it carries no GitHub identity).",
                subject: null);
        }
    }

    /// <summary>
    /// Classifies a failed directory read. Cancellation is the caller's, so it propagates; an
    /// unrecognised PERSISTED ROLE is reported as <see cref="PrincipalRefusalReason.UnknownRole"/>;
    /// every other failure is reported as <see cref="PrincipalRefusalReason.DirectoryFailure"/>.
    /// </summary>
    private PrincipalResolution RefuseReadFailure(Exception failure, string? subject)
    {
        var reason = IsUnknownPersistedRoleFailure(failure)
            ? PrincipalRefusalReason.UnknownRole
            : PrincipalRefusalReason.DirectoryFailure;

        var detail = reason == PrincipalRefusalReason.UnknownRole
            ? "The persisted account role is not a supported CopilotHive role."
            : "The user directory could not be read, so the caller's identity could not be verified.";

        return Refuse(reason, detail, subject, failure);
    }

    /// <summary>
    /// Whether <paramref name="failure"/> (or any inner cause) is the persisted-role conversion
    /// refusal raised by <see cref="UserRoleText.Parse"/>.
    /// </summary>
    private static bool IsUnknownPersistedRoleFailure(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains(UnknownPersistedRoleMessageFragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Logs a secret-free refusal (identity subject and reason only — never a claim dump or a
    /// credential) and returns it.
    /// </summary>
    private PrincipalResolution Refuse(
        PrincipalRefusalReason reason,
        string detail,
        string? subject,
        Exception? failure = null)
    {
        // Diagnostics never throw and never carry credential material: the GitHub numeric id is the
        // only identity fragment logged.
        try
        {
            _logger.LogWarning(
                failure,
                "Refused to resolve an acting principal ({Reason}) for GitHub identity {GitHubId}: {Detail}",
                reason,
                subject ?? "<none>",
                detail);
        }
        catch (Exception)
        {
            // Logging must never change the authorization outcome.
        }

        return PrincipalResolution.Refused(reason, detail);
    }
}
