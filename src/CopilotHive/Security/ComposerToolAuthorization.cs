using CopilotHive.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Security;

/// <summary>
/// The outcome of ONE Composer tool authorization check: allowed, or refused with a
/// <b>model-visible</b>, secret-free explanation.
/// </summary>
public sealed record ComposerToolAuthorizationResult
{
    private ComposerToolAuthorizationResult(bool isAllowed, string? denialReason)
    {
        IsAllowed = isAllowed;
        DenialReason = denialReason;
    }

    /// <summary>Whether the tool call may run (and therefore may produce side effects).</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// Host-authored, MODEL-VISIBLE refusal text, or <c>null</c> when allowed. It never contains a
    /// secret, a stack trace or internal host state.
    /// </summary>
    public string? DenialReason { get; }

    /// <summary>The single allowed result.</summary>
    public static ComposerToolAuthorizationResult Allow() => new(isAllowed: true, denialReason: null);

    /// <summary>A refusal carrying the reason the model is told.</summary>
    /// <param name="reason">Model-visible, secret-free refusal text.</param>
    public static ComposerToolAuthorizationResult Deny(string reason) => new(isAllowed: false, reason);
}

/// <summary>
/// THE CENTRAL COMPOSER TOOL AUTHORIZATION HELPER: the ONE place that maps every tool name on the
/// Composer's surface onto a <see cref="HiveCapability"/>, and the ONE place that resolves the acting
/// account FRESH and evaluates it through <see cref="HivePolicy"/>.
/// <para>
/// TWO PROPERTIES ARE STRUCTURAL:
/// <list type="number">
/// <item>
/// <b>DEFAULT DENY.</b> <see cref="CapabilityFor"/> returns <c>null</c> for any name that is not in
/// the map, and <see cref="AuthorizeAsync"/> refuses such a name. A tool added later without a map
/// entry is therefore DENIED, never implicitly allowed: a missing legitimate name shows up as a
/// refusal (loud) instead of as an unguarded capability (silent).
/// </item>
/// <item>
/// <b>NO STALE AUTHORIZATION TRUTH.</b> Every check re-reads the acting account through
/// <see cref="HivePrincipalResolver.ResolveByUserIdAsync"/> (current stored state) and then asks
/// <see cref="HivePolicy"/>. Nothing is cached, so a demotion, promotion or disablement between two
/// calls takes effect on the NEXT call.
/// </item>
/// </list>
/// </para>
/// <para>
/// IDENTITY INPUT: an identity-bound runtime supplies its FIXED account id, so exactly that account is
/// re-read. The LEGACY single-admin runtime has no account id; while authentication is ENABLED it
/// re-reads the original/bootstrap account id (<see cref="UserService.GetAdminUserAsync"/>) on every
/// invocation, so a disabled, demoted, role-invalid or unreadable original account takes effect with
/// no fallback. In open/no-auth mode that legacy runtime is the explicit local operator — the ONLY
/// branch that yields an authority without a persisted account, and it is unreachable while
/// authentication is enabled.
/// </para>
/// <para>
/// FAIL CLOSED: a resolver refusal, an unreadable directory, a null decision or any unexpected fault
/// denies. Caller cancellation is not an authorization answer, so it propagates unchanged.
/// </para>
/// <para>
/// REGISTRATION: stateless with respect to users — it holds the resolver, the policy catalog, the user
/// directory and the configured mode, and retains no principal — so one instance is safe for the whole
/// host. <see cref="FromServiceProvider"/> builds it from the already-registered primitives, so no
/// extra DI registration is needed.
/// </para>
/// </summary>
public sealed class ComposerToolAuthorization
{
    /// <summary>
    /// The ONLY goal fields <c>update_goal</c> may touch. A call naming any other field is refused
    /// outright: an unsupported field is not an authorization question, it is a call the Composer
    /// surface does not have.
    /// </summary>
    private static readonly HashSet<string> SupportedUpdateGoalFields = new(StringComparer.Ordinal)
    {
        "description",
        "priority",
        "scope",
        "repositories",
        "target_repositories",
        "depends_on",
        "documents",
        "status",
        "release",
    };

    /// <summary>
    /// THE TOOL MAP: every tool name the Composer's agent can be handed, mapped onto the single
    /// capability that tool requires. Names ABSENT from this map are DENIED.
    /// <para>
    /// The read-only surface — including the SharpCoder built-in child read tools a sub-agent may
    /// invoke (<c>read_file</c>/<c>glob</c>/<c>grep</c>) — is <see cref="HiveCapability.SharedRead"/>.
    /// The interactive surface (conversation, issue reporting, goal drafts, web, sub-agents) is
    /// <see cref="HiveCapability.InteractiveMember"/>. Execution, release and triage control is
    /// <see cref="HiveCapability.ExecutionOperator"/>. Global/agent configuration and the
    /// knowledge-document corpus are <see cref="HiveCapability.GlobalAdmin"/>.
    /// </para>
    /// <para>
    /// DELIBERATELY ABSENT: <c>write_file</c>, <c>edit_file</c>, <c>execute_bash_command</c>,
    /// <c>load_skill</c> and <c>list_skills</c> are not part of the Composer surface at all — the
    /// Composer's agent is constructed with file WRITES, bash and skills DISABLED, so those tools are
    /// never registered — and this map denies them if they ever were. That disabled shape is asserted
    /// end to end by <c>ComposerToolAuthorizationTests</c>.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, HiveCapability> ToolMap = new(StringComparer.Ordinal)
    {
        // ── SharedRead (Viewer and above) ──
        ["get_goal"] = HiveCapability.SharedRead,
        ["get_phase_output"] = HiveCapability.SharedRead,
        ["list_goals"] = HiveCapability.SharedRead,
        ["search_goals"] = HiveCapability.SharedRead,
        ["get_release"] = HiveCapability.SharedRead,
        ["list_releases"] = HiveCapability.SharedRead,
        ["list_issues"] = HiveCapability.SharedRead,
        ["get_issue"] = HiveCapability.SharedRead,
        ["read_document"] = HiveCapability.SharedRead,
        ["search_knowledge"] = HiveCapability.SharedRead,
        ["list_documents"] = HiveCapability.SharedRead,
        ["traverse_graph"] = HiveCapability.SharedRead,
        ["list_config_files"] = HiveCapability.SharedRead,
        ["read_config_file"] = HiveCapability.SharedRead,
        ["list_repositories"] = HiveCapability.SharedRead,
        ["git_log"] = HiveCapability.SharedRead,
        ["git_diff"] = HiveCapability.SharedRead,
        ["git_show"] = HiveCapability.SharedRead,
        ["git_branch"] = HiveCapability.SharedRead,
        ["git_blame"] = HiveCapability.SharedRead,
        ["get_recent_logs"] = HiveCapability.SharedRead,

        // The SharpCoder built-in READ tools. On the Composer's own agent they are the
        // repository-reading surface; on a sub-agent child they are the only file tools a child has
        // (children are built with writes, bash and skills disabled), so a child can never gain a
        // capability its parent lacks.
        ["read_file"] = HiveCapability.SharedRead,
        ["glob"] = HiveCapability.SharedRead,
        ["grep"] = HiveCapability.SharedRead,

        // ── InteractiveMember (Member and above) ──
        ["ask_user"] = HiveCapability.InteractiveMember,
        ["get_current_time"] = HiveCapability.InteractiveMember,
        ["create_issue"] = HiveCapability.InteractiveMember,
        // create_goal ALWAYS produces a Draft (approval/dispatch is an ExecutionOperator action).
        ["create_goal"] = HiveCapability.InteractiveMember,
        ["web_search"] = HiveCapability.InteractiveMember,
        ["web_fetch"] = HiveCapability.InteractiveMember,
        // The four SharpCoder sub-agent tools. Children inherit the parent's captured pair, so their
        // own calls are authorized against the SAME account — there is no escalation path.
        ["start_sub_agent"] = HiveCapability.InteractiveMember,
        ["await_sub_agents"] = HiveCapability.InteractiveMember,
        ["get_sub_agent_status"] = HiveCapability.InteractiveMember,
        ["list_sub_agent_models"] = HiveCapability.InteractiveMember,

        // ── ExecutionOperator (Operator and above) ──
        ["approve_goal"] = HiveCapability.ExecutionOperator,
        ["cancel_goal"] = HiveCapability.ExecutionOperator,
        ["extend_goal_iterations"] = HiveCapability.ExecutionOperator,
        ["delete_goal"] = HiveCapability.ExecutionOperator,
        ["create_release"] = HiveCapability.ExecutionOperator,
        ["update_release"] = HiveCapability.ExecutionOperator,
        ["review_goal"] = HiveCapability.ExecutionOperator,
        ["update_issue"] = HiveCapability.ExecutionOperator,
        ["git_fetch"] = HiveCapability.ExecutionOperator,
        // Field-sensitive: see IsSupportedUpdateGoalField and the argument inspection in AuthorizeAsync.
        ["update_goal"] = HiveCapability.ExecutionOperator,

        // ── GlobalAdmin (Admin only) ──
        ["update_agents_md"] = HiveCapability.GlobalAdmin,
        ["edit_agents_md"] = HiveCapability.GlobalAdmin,
        ["commit_config_changes"] = HiveCapability.GlobalAdmin,
        ["create_document"] = HiveCapability.GlobalAdmin,
        ["update_document"] = HiveCapability.GlobalAdmin,
        ["delete_document"] = HiveCapability.GlobalAdmin,
        ["link_document"] = HiveCapability.GlobalAdmin,
        ["unlink_document"] = HiveCapability.GlobalAdmin,
    };

    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly UserService _users;
    private readonly HiveAuthMode _mode;
    private readonly ILogger<ComposerToolAuthorization> _logger;

    /// <summary>Creates the central Composer tool authorization helper.</summary>
    /// <param name="resolver">The trusted principal resolver (the ONLY identity source).</param>
    /// <param name="policy">The capability catalog and evaluator.</param>
    /// <param name="users">The trusted user directory (used to re-read the legacy original account id).</param>
    /// <param name="mode">The EXPLICIT authentication mode supplied by the host.</param>
    /// <param name="logger">Logger used for secret-free refusals.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ComposerToolAuthorization(
        HivePrincipalResolver resolver,
        HivePolicy policy,
        UserService users,
        HiveAuthMode mode,
        ILogger<ComposerToolAuthorization> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the helper from the primitives the host already registers.
    /// <para>
    /// ALL-OR-NOTHING, and never a silent degradation:
    /// <list type="bullet">
    /// <item>NONE present yields <c>null</c>. That is the genuinely-absent, all-or-nothing case — the
    /// approved legacy exception (a <c>userState == null</c> legacy or explicit open/no-auth construction
    /// with no primitives at all). The Composer refuses to run IDENTITY-BOUND state on it.</item>
    /// <item>SOME but not all present THROWS <see cref="InvalidOperationException"/>. A container that
    /// supplies part of the set proves a boundary was intended, so quietly returning <c>null</c> would
    /// disable authorization while appearing to configure it. Failing closed is what keeps "partially
    /// registered" from ever meaning "unguarded".</item>
    /// <item>All four present yields the helper.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="services">The provider to resolve the primitives from.</param>
    /// <returns>
    /// The helper, or <c>null</c> when the provider is absent or exposes NONE of the primitives.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A provider exposes SOME but not all of the security primitives (a partial registration).
    /// </exception>
    public static ComposerToolAuthorization? FromServiceProvider(IServiceProvider? services)
    {
        // No provider to ask at all: nothing can be configured, so this is the all-absent case.
        if (services is null)
            return null;

        var resolver = services.GetService<HivePrincipalResolver>();
        var policy = services.GetService<HivePolicy>();
        var users = services.GetService<UserService>();
        var mode = services.GetService<HiveAuthMode>();

        var present = (resolver is null ? 0 : 1) + (policy is null ? 0 : 1)
            + (users is null ? 0 : 1) + (mode is null ? 0 : 1);

        // GENUINELY ABSENT — all-or-nothing. The Composer tolerates a null gate only for legacy
        // (userState == null) state; it refuses to build identity-bound state without one.
        if (present == 0)
            return null;

        if (present < 4)
        {
            // PARTIAL REGISTRATION FAILS CLOSED. Naming the missing pieces is secret-free and diagnostic;
            // authorization itself never proceeds on a partially configured container.
            var missing = new List<string>(4);
            if (resolver is null) missing.Add(nameof(HivePrincipalResolver));
            if (policy is null) missing.Add(nameof(HivePolicy));
            if (users is null) missing.Add(nameof(UserService));
            if (mode is null) missing.Add(nameof(HiveAuthMode));

            throw new InvalidOperationException(
                "The security primitives are only partially registered (" +
                string.Join(", ", missing) +
                " missing), so the Composer tool-authorization boundary cannot be built. " +
                "Register every primitive or none of them; a partial registration is never treated as \"no authorization\".");
        }

        return new ComposerToolAuthorization(
            resolver!,
            policy!,
            users!,
            mode!,
            services.GetService<ILogger<ComposerToolAuthorization>>() ?? NullLogger<ComposerToolAuthorization>.Instance);
    }

    /// <summary>
    /// The capability <paramref name="toolName"/> requires, or <c>null</c> when the name is not part of
    /// the Composer surface — which every caller must treat as DENY.
    /// </summary>
    /// <param name="toolName">The model-visible function name that is about to run.</param>
    /// <returns>The required capability, or <c>null</c> for an unmapped name.</returns>
    public static HiveCapability? CapabilityFor(string? toolName) =>
        toolName is not null && ToolMap.TryGetValue(toolName, out var capability) ? capability : null;

    /// <summary>Whether <paramref name="field"/> is one of the goal fields <c>update_goal</c> supports.</summary>
    /// <param name="field">The <c>field</c> argument of an <c>update_goal</c> call.</param>
    public static bool IsSupportedUpdateGoalField(string? field) =>
        field is not null && SupportedUpdateGoalFields.Contains(field);

    /// <summary>The mapped tool names as a detached snapshot (diagnostics and tests).</summary>
    public static IReadOnlyCollection<string> MappedToolNames => ToolMap.Keys.ToArray();

    /// <summary>
    /// Binds this helper to ONE runtime's fixed identity. The returned gate is what the Composer hands
    /// to SharpCoder's tool-authorization hook and to its own mutating tool methods, so every effective
    /// invocation of that runtime is authorized against the SAME account — re-read fresh every time.
    /// </summary>
    /// <param name="userId">
    /// The runtime's fixed activated account id, or <c>null</c> for the legacy single-admin runtime
    /// (re-resolved per invocation: the local operator in open mode, the original account while
    /// authentication is enabled). <c>null</c> is the SOLE legacy sentinel: a supplied <c>0</c> or negative
    /// id is NOT treated as legacy — it is routed through the resolver, which REFUSES a non-positive id, so
    /// such a runtime is denied rather than inheriting legacy authority.
    /// </param>
    /// <returns>The per-runtime gate.</returns>
    public ComposerToolAuthorizationGate Bind(int? userId) => new(this, userId, _mode);

    /// <summary>
    /// Authorizes ONE tool call. Always re-reads the acting account; nothing is cached.
    /// </summary>
    /// <param name="userId">
    /// The runtime's fixed account id, or <c>null</c> for the legacy runtime. <c>null</c> is the SOLE legacy
    /// sentinel; a supplied <c>0</c> or negative id is REFUSED as an invalid account id (it never selects
    /// legacy authority).
    /// </param>
    /// <param name="toolName">The model-visible tool name that is about to run.</param>
    /// <param name="arguments">The snapshotted call arguments, or <c>null</c> when not available.</param>
    /// <param name="ct">
    /// The CALLER's cancellation token, threaded into the fresh directory lookup. Cancellation observed
    /// during that lookup PROPAGATES to the caller and is never converted into a refusal.
    /// </param>
    /// <returns>An allow, or a denial carrying model-visible text.</returns>
    public Task<ComposerToolAuthorizationResult> AuthorizeAsync(
        int? userId, string? toolName, IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct) =>
        AuthorizeCoreAsync(userId, toolName, arguments, ct);

    /// <summary>
    /// AUTHORIZES THE CAPABILITY ONLY, skipping argument inspection. It exists for the Composer's own
    /// <c>update_goal</c> method, which validates its field itself (through
    /// <see cref="ComposerToolAuthorizationGate.UpdateGoalFieldDenialAsync"/>) and then needs just the
    /// ExecutionOperator answer. It is deliberately NOT public: an external caller must go through
    /// <see cref="AuthorizeAsync"/>, which always applies the argument rules, so this can never be used to
    /// bypass them.
    /// </summary>
    internal Task<ComposerToolAuthorizationResult> AuthorizeCapabilityAsync(
        int? userId, string? toolName, CancellationToken ct) =>
        AuthorizeCoreAsync(userId, toolName, arguments: null, ct, inspectArguments: false);

    private async Task<ComposerToolAuthorizationResult> AuthorizeCoreAsync(
        int? userId, string? toolName, IReadOnlyDictionary<string, object?>? arguments, CancellationToken ct,
        bool inspectArguments = true)
    {
        // 1. DEFAULT DENY: an unmapped name is never implicitly allowed.
        var capability = CapabilityFor(toolName);
        if (capability is not { } required)
        {
            return Deny($"'{toolName ?? "<none>"}' is not an available Composer tool.", toolName, capability: null, refusal: "unmapped-tool");
        }

        // 2. ARGUMENT INSPECTION: update_goal may only touch the fields the surface supports. Any other
        //    field is refused before the capability is asked about, so an unsupported field can never be
        //    reached by an account that happens to hold ExecutionOperator. It is FAIL CLOSED: an absent
        //    argument snapshot (or an absent field) cannot be verified, so it denies rather than skipping
        //    the rule. SharpCoder always supplies a non-null snapshot, and the Composer's own update_goal
        //    method supplies the field it actually received through
        //    ComposerToolAuthorizationGate.UpdateGoalFieldDenialAsync — so the identical rule runs on both
        //    enforcement points and neither can be bypassed by omitting the argument.
        if (inspectArguments && string.Equals(toolName, "update_goal", StringComparison.Ordinal))
        {
            var field = ReadStringArgument(arguments, "field");
            if (!IsSupportedUpdateGoalField(field))
            {
                return Deny(
                    $"'{field ?? "<none>"}' cannot be updated through update_goal. Supported fields: "
                    + "description, priority, scope, repositories, target_repositories, depends_on, documents, status, release.",
                    toolName,
                    required,
                    refusal: "unsupported-update-field");
            }
        }

        // 3. RESOLVE THE ACTING PRINCIPAL FRESH, then ask the policy catalog.
        ActingUserPrincipal? principal;
        try
        {
            principal = await ResolveActingPrincipalAsync(userId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's, not an authorization answer.
            throw;
        }
        catch (Exception)
        {
            // Resolution can never be "allow on error".
            return Deny($"{UnverifiedReason} '{toolName}' was refused.", toolName, required, refusal: "resolution-fault");
        }

        if (principal is null)
        {
            return Deny($"{UnverifiedReason} '{toolName}' was refused.", toolName, required, refusal: "unresolved-account");
        }

        bool allowed;
        try
        {
            allowed = _policy.IsAllowed(principal, required);
        }
        catch (Exception)
        {
            return Deny($"{UnverifiedReason} '{toolName}' was refused.", toolName, required, refusal: "policy-fault");
        }

        return allowed
            ? ComposerToolAuthorizationResult.Allow()
            : Deny(
                $"The current user's role does not permit '{toolName}'.",
                toolName,
                required,
                refusal: "insufficient-capability");
    }

    /// <summary>The fixed, secret-free explanation of every fail-closed verification failure.</summary>
    private const string UnverifiedReason =
        "The current user's account could not be verified, so";

    /// <summary>
    /// Reads the acting principal for one authorization check, from CURRENT stored state.
    /// <para>
    /// <c>null</c> is the SOLE legacy sentinel. EVERY non-null id — including <c>0</c> and negative values —
    /// is treated as an account id and routed through
    /// <see cref="HivePrincipalResolver.ResolveByUserIdAsync"/>, which already refuses a non-positive id, so
    /// fail-closed comes for free. There is deliberately no "invalid id falls back to legacy" branch: that
    /// would make a caller-supplied <c>0</c>/negative id inherit legacy authority.
    /// </para>
    /// <list type="bullet">
    /// <item>An identity-bound runtime resolves exactly its account; a refusal stays a refusal.</item>
    /// <item>The <c>null</c> legacy runtime in open/no-auth mode resolves the explicit local operator
    /// THROUGH <see cref="HivePrincipalResolver.ResolveAsync"/>, so the open-mode decision stays owned by
    /// the resolver (this type never constructs an <see cref="ActingUserPrincipal"/> itself).</item>
    /// <item>The <c>null</c> legacy runtime while authentication is ENABLED re-reads the ORIGINAL account id
    /// on every invocation, so a disabled/demoted/role-invalid/unreadable original takes effect at
    /// once.</item>
    /// </list>
    /// </summary>
    private async Task<ActingUserPrincipal?> ResolveActingPrincipalAsync(int? userId, CancellationToken ct)
    {
        // NULL IS THE ONLY LEGACY SENTINEL. Any non-null id — positive, zero or negative — is an account id,
        // so it takes the by-id path and a non-positive value is refused there.
        if (userId is { } id)
        {
            var resolution = await _resolver.ResolveByUserIdAsync(id, ct).ConfigureAwait(false);
            return resolution.Principal;
        }

        if (!_mode.AuthEnabled)
        {
            // EXPLICIT open/no-auth mode — but the MODE DECISION STAYS IN THE RESOLVER. Its ResolveAsync
            // yields the local operator ONLY on its explicit open-mode branch and is unreachable there while
            // authentication is enabled, so no failed identity can fall through to local-operator authority.
            // This type therefore never constructs an ActingUserPrincipal itself.
            var openResolution = await _resolver.ResolveAsync(claimsPrincipal: null, ct).ConfigureAwait(false);
            return openResolution.Principal;
        }

        // Authenticated legacy single-admin runtime: the bootstrap account id is re-read EVERY time, so
        // nothing about that identity is cached here.
        var original = await _users.GetAdminUserAsync(ct).ConfigureAwait(false);
        if (original is null)
            return null;

        var originalResolution = await _resolver.ResolveByUserIdAsync(original.Id, ct).ConfigureAwait(false);
        return originalResolution.Principal;
    }

    /// <summary>Reads one string argument out of a snapshotted argument dictionary.</summary>
    private static string? ReadStringArgument(IReadOnlyDictionary<string, object?>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
            return null;

        return value switch
        {
            null => null,
            string text => text,
            // The snapshot keeps arbitrary model-supplied values: anything that is not already a string
            // is compared by its text form — and an unsupported field name is denied either way.
            _ => value.ToString(),
        };
    }

    /// <summary>
    /// Logs a secret-free refusal (tool name, capability and refusal kind only — never an argument value
    /// or a credential) and returns it. Diagnostics never throw and never change the outcome.
    /// </summary>
    private ComposerToolAuthorizationResult Deny(
        string reason, string? toolName, HiveCapability? capability, string refusal)
    {
        try
        {
            _logger.LogWarning(
                "Refused Composer tool '{ToolName}' (capability {Capability}, {Refusal}): {Reason}",
                toolName ?? "<none>",
                capability?.ToString() ?? "<unmapped>",
                refusal,
                reason);
        }
        catch (Exception)
        {
            // Reporting a denial must never become a second, louder failure.
        }

        return ComposerToolAuthorizationResult.Deny(reason);
    }
}

/// <summary>
/// ONE runtime's authorization boundary: the central <see cref="ComposerToolAuthorization"/> helper
/// bound to that runtime's fixed identity. The Composer exposes this object to SharpCoder's
/// <c>AgentOptions.AuthorizeTool</c> hook (through <see cref="AuthorizeToolAsync"/>) and to its own
/// mutating tool methods (through <see cref="ToolDenialAsync"/>), so both enforcement points ask the
/// same question of the same helper.
/// <para>
/// It holds NO principal and no cached decision: every call re-reads current stored state through the
/// helper, so a role change or disablement between two calls is observed immediately.
/// </para>
/// </summary>
public sealed class ComposerToolAuthorizationGate
{
    private readonly ComposerToolAuthorization _owner;
    private readonly int? _userId;
    private readonly HiveAuthMode _mode;

    internal ComposerToolAuthorizationGate(ComposerToolAuthorization owner, int? userId, HiveAuthMode mode)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _userId = userId;
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
    }

    /// <summary>
    /// The runtime's fixed activated account id, or <c>null</c> for the legacy single-admin runtime
    /// (re-resolved per invocation).
    /// </summary>
    public int? UserId => _userId;

    /// <summary>
    /// The SharpCoder tool-authorization correlation captured with the callback: the acting user's
    /// identity as host metadata (never parsed by SharpCoder, never a credential).
    /// </summary>
    public string CorrelationId =>
        _userId is { } id
            ? $"composer-user:{id}"
            : _mode.AuthEnabled ? "composer-legacy-original" : "composer-local-operator";

    /// <summary>
    /// The SharpCoder <c>AgentOptions.AuthorizeTool</c> callback for this runtime: a deny becomes
    /// <see cref="SharpCoder.ToolAuthorizationDecision.Deny"/> carrying the model-visible reason, and an
    /// unexpected fault fails CLOSED (a denied decision), never open.
    /// </summary>
    /// <param name="context">The tool call SharpCoder is about to run.</param>
    /// <param name="ct">
    /// SharpCoder's own cancellation token for this call, forwarded UNCHANGED into the fresh directory
    /// lookup (never <see cref="CancellationToken.None"/>). Cancellation observed there propagates back to
    /// SharpCoder instead of being converted into a denial.
    /// </param>
    /// <returns>The decision; never <c>null</c>.</returns>
    public async Task<SharpCoder.ToolAuthorizationDecision> AuthorizeToolAsync(
        SharpCoder.ToolAuthorizationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var result = await _owner
                .AuthorizeAsync(_userId, context.ToolName, context.Arguments, ct)
                .ConfigureAwait(false);

            return result.IsAllowed
                ? SharpCoder.ToolAuthorizationDecision.Allow
                : SharpCoder.ToolAuthorizationDecision.Deny(result.DenialReason);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an authorization answer: let SharpCoder propagate it.
            throw;
        }
        catch (Exception)
        {
            // FAIL CLOSED. The model gets a fixed, host-neutral refusal — never the exception text.
            return SharpCoder.ToolAuthorizationDecision.Deny(
                "The current user's account could not be verified, so the tool call was refused.");
        }
    }

    /// <summary>
    /// The denial text for one tool call, or <c>null</c> when it is allowed. Used by the Composer-owned
    /// mutating tool methods so enforcement is not hook-only; the check runs before any side effect, and
    /// a fault denies rather than allowing.
    /// </summary>
    /// <param name="toolName">The tool name whose capability is required.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Model-visible denial text, or <c>null</c> when allowed.</returns>
    public async Task<string?> ToolDenialAsync(string toolName, CancellationToken ct = default)
    {
        try
        {
            var result = await _owner.AuthorizeAsync(_userId, toolName, arguments: null, ct).ConfigureAwait(false);
            return result.IsAllowed ? null : result.DenialReason;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return $"{UnverifiedReason} '{toolName}' was refused.";
        }
    }

    /// <summary>The fixed, secret-free explanation of every fail-closed verification failure.</summary>
    private const string UnverifiedReason =
        "The current user's account could not be verified, so";

    /// <summary>
    /// The denial text for one <c>update_goal</c> call, or <c>null</c> when it is allowed. It applies the
    /// SAME rules as the hook — unsupported field first, then the ExecutionOperator capability — using the
    /// field the tool method actually received, so the method-level enforcement is not weaker than the
    /// hook's argument inspection.
    /// </summary>
    /// <param name="field">The <c>field</c> argument the tool method received.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Model-visible denial text, or <c>null</c> when allowed.</returns>
    public async Task<string?> UpdateGoalFieldDenialAsync(string? field, CancellationToken ct = default)
    {
        if (!ComposerToolAuthorization.IsSupportedUpdateGoalField(field))
        {
            return $"'{field ?? "<none>"}' cannot be updated through update_goal. Supported fields: "
                + "description, priority, scope, repositories, target_repositories, depends_on, documents, status, release.";
        }

        // The field is validated above, so only the CAPABILITY is asked about here — exactly the part of the
        // hook's decision that applies to the method-level call.
        try
        {
            var result = await _owner.AuthorizeCapabilityAsync(_userId, "update_goal", ct).ConfigureAwait(false);
            return result.IsAllowed ? null : result.DenialReason;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return $"{UnverifiedReason} 'update_goal' was refused.";
        }
    }
}
