using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>Why <see cref="ProviderEligibility"/> refused a provider selection. Every value is fail-closed.</summary>
public enum ProviderDenyReason
{
    /// <summary>No denial: the selection is allowed.</summary>
    None = 0,

    /// <summary>No activated account currently exists for the identity (unknown / deleted).</summary>
    UnknownAccount = 1,

    /// <summary>The account is disabled.</summary>
    DisabledAccount = 2,

    /// <summary>The account's stored role is not a defined role.</summary>
    UndefinedRole = 3,

    /// <summary>
    /// An activated NON-original account selected (or inherits) a Copilot-backed primary or compaction model.
    /// The only GitHub token this installation holds is the ORIGINAL admin's, which never serves another identity.
    /// </summary>
    NonOriginalCopilot = 4,

    /// <summary>
    /// A non-original identity's model selection could not be classified (its provider failed to resolve), so it
    /// cannot be proven free of the original account's Copilot credential.
    /// </summary>
    UnresolvableProvider = 5,

    /// <summary>The account's current standing could not be read (directory fault), so it cannot be proven eligible.</summary>
    IdentityUnavailable = 6,
}

/// <summary>
/// A FRESH, secret-free view of one account's standing, read from the stored directory at decision time (never
/// captured from a principal at construction). It carries exactly what the provider gate needs: whether the account
/// is currently activated, enabled, role-valid, and the installation's original/bootstrap identity.
/// </summary>
internal sealed record ProviderIdentity
{
    private ProviderIdentity(bool activated, bool enabled, bool roleDefined, bool isOriginal)
    {
        Activated = activated;
        Enabled = enabled;
        RoleDefined = roleDefined;
        IsOriginal = isOriginal;
    }

    /// <summary>Whether an activated account currently exists under the identity's id.</summary>
    public bool Activated { get; }

    /// <summary>Whether the stored account is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Whether the stored role is a defined <see cref="UserRole"/>.</summary>
    public bool RoleDefined { get; }

    /// <summary>
    /// Whether the account IS the installation's original/bootstrap account (identity only: this is independent of
    /// the account's current role, which confers no extra Copilot authority and takes none away).
    /// </summary>
    public bool IsOriginal { get; }

    /// <summary>The identity of an account that no longer exists (or never did): always denied.</summary>
    public static ProviderIdentity Unknown { get; } = new(activated: false, enabled: false, roleDefined: false, isOriginal: false);

    /// <summary>Builds the identity of an existing activated account from its freshly stored state.</summary>
    public static ProviderIdentity ForAccount(bool enabled, bool roleDefined, bool isOriginal) =>
        new(activated: true, enabled, roleDefined, isOriginal);

    /// <summary>
    /// Reads the account's CURRENT standing from the stored directory (the same re-read the runtime manager's
    /// admission/recheck uses): existence, enabled flag, role validity, and whether it is the original/bootstrap
    /// account (the lowest-id activated row).
    /// <para>
    /// SECRET-FREE BY CONSTRUCTION: it uses ONLY the directory's credential-free projection
    /// (<see cref="UserService.ListActivatedUsersAsync"/>, which selects identity columns and never
    /// <c>access_token</c>/<c>refresh_token</c> nor materializes a full <c>UserEntity</c>). The original account is the
    /// first row of that id-ordered projection — exactly the row <see cref="UserService.GetAdminUserAsync"/> would
    /// select — so the gate never needs, and never reads, the original admin's credentials. Do NOT replace this with
    /// <c>GetAdminUserAsync</c>: that loads the whole entity including plaintext tokens.
    /// </para>
    /// </summary>
    /// <param name="users">The persisted user directory.</param>
    /// <param name="userId">The internal account id.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ProviderIdentity> ReadAsync(UserService users, int userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(users);

        var accounts = await users.ListActivatedUsersAsync(ct);
        var fresh = accounts.FirstOrDefault(a => a.Id == userId);
        if (fresh is null)
            return Unknown;

        var originalId = accounts.Count == 0 ? (int?)null : accounts.Min(a => a.Id);
        return ForAccount(fresh.Enabled, IsDefinedRole(fresh.Role), isOriginal: originalId == userId);
    }

    private static bool IsDefinedRole(UserRole role)
    {
        try
        {
            UserRoleText.EnsureDefined(role, nameof(role));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>
/// The identity-bound provider gate handed to a runtime's <see cref="ComposerAgentService"/>: given the model the
/// runtime is about to use as its primary and its explicit compaction model, it re-reads the CURRENT account and
/// returns the typed <see cref="ProviderEligibility"/>. It is evaluated BEFORE any client creation or network call.
/// </summary>
public delegate Task<ProviderEligibility> ProviderGate(string primaryModel, string? compactionModel, CancellationToken ct);

/// <summary>The typed fail-closed error raised when a provider selection is not eligible. Carries no secrets.</summary>
public sealed class ProviderNotEligibleException(ProviderDenyReason reason)
    : InvalidOperationException($"The selected model provider is not available to this account ({reason}).")
{
    /// <summary>Why the selection was denied.</summary>
    public ProviderDenyReason Reason { get; } = reason;
}

/// <summary>
/// The outcome of the identity-bound provider gate: allowed, or denied with a typed
/// <see cref="ProviderDenyReason"/>. A denial is DATA — never an exception — so construction, deferral and (later)
/// client creation can all settle into a safe typed outcome.
/// </summary>
public readonly record struct ProviderEligibility(bool IsAllowed, ProviderDenyReason Reason)
{
    /// <summary>The allowed outcome.</summary>
    public static ProviderEligibility Allowed { get; } = new(true, ProviderDenyReason.None);

    /// <summary>A denial for <paramref name="reason"/>.</summary>
    public static ProviderEligibility Denied(ProviderDenyReason reason) => new(false, reason);

    /// <summary>
    /// THE ONE decision rule, a PURE function of its inputs (it reads no token, builds no client and touches no
    /// network, so it can run BEFORE any of them):
    /// <list type="bullet">
    /// <item>authentication disabled (open/legacy) → allowed, byte-unchanged behaviour;</item>
    /// <item>unknown / disabled / role-invalid identity → denied;</item>
    /// <item>the ORIGINAL identity → allowed regardless of its current role;</item>
    /// <item>an activated NON-original identity → denied when the primary model OR the effective compaction model is
    /// Copilot-backed (or cannot be classified); a non-Copilot (e.g. Ollama) selection stays eligible.</item>
    /// </list>
    /// </summary>
    /// <param name="authEnabled">Whether authentication is enabled.</param>
    /// <param name="identity">The identity's FRESH standing; <c>null</c> is treated as unknown.</param>
    /// <param name="primaryModel">
    /// The runtime's ACTUAL primary model; <c>null</c> means none. Any non-null value — even blank — is classified as
    /// given (a blank model handed to client creation could otherwise fall back to a default provider).
    /// </param>
    /// <param name="compactionModel">
    /// The explicit compaction model exactly as the client creator sees it. <c>null</c>/empty means "inherit the
    /// primary", which the primary check already covers; any other value (even whitespace-only) is classified as
    /// given, because it is passed verbatim to client creation.
    /// </param>
    /// <param name="providerOf">Resolves a model's provider name.</param>
    /// <param name="logger">Optional logger.</param>
    internal static ProviderEligibility Evaluate(
        bool authEnabled, ProviderIdentity? identity, string? primaryModel, string? compactionModel,
        Func<string, string> providerOf, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(providerOf);

        if (!authEnabled)
            return Allowed;
        if (identity is null || !identity.Activated)
            return Denied(ProviderDenyReason.UnknownAccount);
        if (!identity.Enabled)
            return Denied(ProviderDenyReason.DisabledAccount);
        if (!identity.RoleDefined)
            return Denied(ProviderDenyReason.UndefinedRole);
        if (identity.IsOriginal)
            return Allowed;

        // Activated NON-original identity: decided purely from the selection, BEFORE any token probe.
        if (primaryModel is not null && Classify(primaryModel, providerOf, logger) is { IsAllowed: false } primaryDenial)
            return primaryDenial;
        if (!string.IsNullOrEmpty(compactionModel) && Classify(compactionModel, providerOf, logger) is { IsAllowed: false } compactionDenial)
            return compactionDenial;

        return Allowed;
    }

    /// <summary>
    /// Gate for ONE model selection (the per-client form later client creation shares): the same rule as
    /// <see cref="Evaluate"/> with no compaction model.
    /// </summary>
    internal static ProviderEligibility EvaluateModel(
        bool authEnabled, ProviderIdentity? identity, string? model, Func<string, string> providerOf, ILogger? logger) =>
        Evaluate(authEnabled, identity, model, compactionModel: null, providerOf, logger);

    private static ProviderEligibility Classify(string model, Func<string, string> providerOf, ILogger? logger)
    {
        try
        {
            return string.Equals(providerOf(model), LlmConnectionCoordinator.CopilotProvider, StringComparison.OrdinalIgnoreCase)
                ? Denied(ProviderDenyReason.NonOriginalCopilot)
                : Allowed;
        }
        catch (Exception ex)
        {
            // Fail CLOSED for a non-original identity: unlike the legacy deferral predicate (which treats an
            // unparseable model as non-Copilot so the real error surfaces), an unclassifiable model cannot be proven
            // free of the original account's credential.
            try { logger?.LogWarning(ex, "Could not resolve provider for model '{Model}'; denying for a non-original identity", model); }
            catch { /* diagnostics never throw */ }
            return Denied(ProviderDenyReason.UnresolvableProvider);
        }
    }
}
