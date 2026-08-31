using CopilotHive.Shared;
using CopilotHive.Shared.Grpc;

using Grpc.Core;

using SharpCoder.Providers;

namespace CopilotHive.Worker;

/// <summary>
/// Applies orchestrator-provisioned LLM configuration to the worker process environment so
/// worker containers can run WITHOUT any LLM credentials of their own.
/// <para>
/// <b>Provider settings vs. token.</b> The provider settings (<c>LLM_PROVIDER</c>,
/// <c>OLLAMA_URL</c>, <c>OLLAMA_API_KEY</c>, <c>OLLAMA_MODEL</c>, <c>GITHUB_MODEL</c>) are read by
/// the orchestrator from ITS OWN process environment. The <c>github_token</c> is NOT: the
/// orchestrator reads it from the STORED ADMIN OAuth RECORD
/// (<c>UserService.GetActiveAccessTokenAsync</c>). Nothing in this flow reads a token from the
/// worker's — or the orchestrator's — environment.
/// </para>
/// <para>
/// <b>Operator overrides.</b> The worker environment is snapshotted EXACTLY ONCE, before the
/// first provisioning call. Any variable that held a non-whitespace value in that snapshot is an
/// OPERATOR value and is never replaced or cleared. Everything else is provisioned space: a later
/// response REPLACES a previously-provisioned value and CLEARS one that is no longer provisioned.
/// Because the snapshot is taken before the first call, a retry can never mistake a
/// previously-provisioned value for an operator override.
/// </para>
/// <para>
/// <b>Whitespace is absence</b> for every provisioned variable, both in the snapshot and in the
/// provisioning response.
/// </para>
/// <para>
/// <b>Secrets are never logged.</b> Only field NAMES are logged. Exceptions raised on the gRPC
/// boundary are rendered through <see cref="SafeExceptionLog"/> because an RPC or LLM HTTP error
/// payload can echo provisioned configuration back to the caller.
/// </para>
/// </summary>
public sealed class WorkerConfigProvisioner
{
    /// <summary>Preferred environment variable used when provisioning a GitHub token.</summary>
    public const string GhTokenVar = "GH_TOKEN";

    /// <summary>Alias environment variable that an operator may set instead of <see cref="GhTokenVar"/>.</summary>
    public const string GitHubTokenVar = "GITHUB_TOKEN";

    /// <summary>Environment variable naming the LLM provider.</summary>
    public const string LlmProviderVar = "LLM_PROVIDER";

    /// <summary>Environment variable holding the Ollama endpoint URL.</summary>
    public const string OllamaUrlVar = "OLLAMA_URL";

    /// <summary>Environment variable holding the Ollama Cloud API key.</summary>
    public const string OllamaApiKeyVar = "OLLAMA_API_KEY";

    /// <summary>Environment variable holding the Ollama model.</summary>
    public const string OllamaModelVar = "OLLAMA_MODEL";

    /// <summary>Environment variable holding the GitHub Models model.</summary>
    public const string GitHubModelVar = "GITHUB_MODEL";

    /// <summary>
    /// Environment variable holding the operator-set config repository URL. The provisioner
    /// tracks it in the operator snapshot but NEVER writes a provisioned value back to it —
    /// the environment only ever carries the operator value.
    /// </summary>
    public const string ConfigRepoUrlVar = "CONFIG_REPO_URL";

    /// <summary>Provider token produced by <c>ChatClientFactory.ParseProviderAndModel</c> for GitHub Copilot.</summary>
    public const string CopilotProvider = "copilot";

    /// <summary>Provider token produced by <c>ChatClientFactory.ParseProviderAndModel</c> for GitHub Models.</summary>
    public const string GitHubProvider = "github";

    /// <summary>Provider token for hosted Ollama Cloud, which requires an API key.</summary>
    public const string OllamaCloudProvider = "ollama-cloud";

    /// <summary>
    /// Provider token for a locally hosted Ollama. It is the ONLY local variant and needs no
    /// mandatory credential.
    /// </summary>
    public const string OllamaLocalProvider = "ollama-local";

    /// <summary>Every variable this provisioner may write, excluding the token aliases.</summary>
    private static readonly string[] SettingVars =
    [
        LlmProviderVar, OllamaUrlVar, OllamaApiKeyVar, OllamaModelVar, GitHubModelVar,
    ];

    private readonly Func<GetWorkerConfigRequest, CancellationToken, Task<GetWorkerConfigResponse>> _fetch;
    private readonly Func<string, string?> _readEnv;
    private readonly Action<string, string?> _writeEnv;
    private readonly string _workerId;
    private readonly WorkerLogger _log = new("Provisioning");

    /// <summary>
    /// The worker environment as it looked BEFORE the first provisioning call. Populated exactly
    /// once by <see cref="EnsureSnapshot"/>; <c>null</c> until then.
    /// </summary>
    private Dictionary<string, string?>? _operatorSnapshot;

    /// <summary>Variables currently holding a value written by this provisioner.</summary>
    private readonly HashSet<string> _provisionedVars = new(StringComparer.Ordinal);

    /// <summary>
    /// The most recently provisioned non-whitespace <c>config_repo_url</c> from the response, or
    /// <c>null</c> when the latest successful response carried none. Cleared on RPC failure
    /// together with the provisioned environment variables.
    /// </summary>
    private string? _provisionedConfigRepoUrl;

    /// <summary>
    /// The most recently provisioned non-whitespace GitHub token from the response, tracked
    /// in memory ONLY for the config-repo credential chain — it is the token the orchestrator
    /// provisioned for THIS worker, independent of what the environment currently holds.
    /// <para>
    /// Lifecycle (the same stale-state contract as <see cref="_provisionedConfigRepoUrl"/>):
    /// replaced or CLEARED on every successful provisioning response (a whitespace token is
    /// absence, so a later response without a token clears it); cleared on an RPC
    /// availability/server failure BEFORE the fallible env revert; NOT cleared on
    /// caller-triggered cancellation (the rethrow happens before provisioned state is touched).
    /// </para>
    /// <para>
    /// This field is ADDITIVE to the environment write: <see cref="ApplyTokenAlias"/> still
    /// writes the provisioned token to <c>GH_TOKEN</c> under the operator-override and
    /// alias-precedence rules because SharpCoder's <c>ChatClientFactory</c> reads
    /// <c>GH_TOKEN</c> from the environment for LLM auth. Only the config-repo credential
    /// chain (see <see cref="ResolveConfigRepoCredential"/>) consumes this field.
    /// </para>
    /// </summary>
    private string? _provisionedGithubToken;

    /// <summary>
    /// Creates a provisioner.
    /// </summary>
    /// <param name="workerId">The worker's identifier, sent with the request for orchestrator-side logging.</param>
    /// <param name="fetch">Performs the <c>GetWorkerConfig</c> unary RPC.</param>
    /// <param name="readEnv">Environment reader seam. Defaults to the process environment.</param>
    /// <param name="writeEnv">Environment writer seam. Defaults to the process environment.</param>
    public WorkerConfigProvisioner(
        string workerId,
        Func<GetWorkerConfigRequest, CancellationToken, Task<GetWorkerConfigResponse>> fetch,
        Func<string, string?>? readEnv = null,
        Action<string, string?>? writeEnv = null)
    {
        _workerId = workerId;
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
        _writeEnv = writeEnv ?? ((name, value) => Environment.SetEnvironmentVariable(name, value));
    }

    /// <summary>
    /// The most recently provisioned non-whitespace <c>config_repo_url</c> from the response,
    /// or <c>null</c> when the latest successful response carried none. Cleared to <c>null</c>
    /// when a subsequent successful response has no URL, and on RPC failure together with the
    /// provisioned environment variables.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The environment snapshot has not been taken yet — call <see cref="EnsureProvisionedAsync"/>
    /// first.
    /// </exception>
    public string? ProvisionedConfigRepoUrl
    {
        get
        {
            EnsureSnapshotTaken();
            return _provisionedConfigRepoUrl;
        }
    }

    /// <summary>
    /// The config repo URL to use for config-repo operations: the operator-set
    /// <c>CONFIG_REPO_URL</c> (first non-whitespace value, tracked by the snapshot mechanism;
    /// whitespace is absence) wins; otherwise the provisioned value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The environment snapshot has not been taken yet — call <see cref="EnsureProvisionedAsync"/>
    /// first.
    /// </exception>
    public string? ResolvedConfigRepoUrl
    {
        get
        {
            EnsureSnapshotTaken();
            var operatorUrl = _operatorSnapshot!.TryGetValue(ConfigRepoUrlVar, out var op) ? op : null;
            return operatorUrl ?? _provisionedConfigRepoUrl;
        }
    }

    /// <summary>
    /// Resolves the token for config-repo operations in this precedence:
    /// <list type="number">
    ///   <item>the in-memory provisioned token (the token the orchestrator provisioned for
    ///   this worker, tracked by <see cref="_provisionedGithubToken"/> — replaced or cleared
    ///   on every successful response, cleared on an RPC availability/server failure, and
    ///   NOT cleared on caller-triggered cancellation);</item>
    ///   <item>the <c>GH_TOKEN</c> environment value (whether operator-set or
    ///   provisioned-into-the-env);</item>
    ///   <item>the <c>GITHUB_TOKEN</c> environment value.</item>
    /// </list>
    /// <para>
    /// Whitespace is absence at EACH step, enforced per candidate via
    /// <see cref="GitCredentialResolver.Resolve"/>. The environment is NOT an operator-vs-
    /// provisioned distinction here — a live non-whitespace <c>GH_TOKEN</c> value applies
    /// whenever no in-memory provisioned token exists.
    /// </para>
    /// <para>
    /// <see cref="ConfigRepoUrlVar"/>'s sibling secret <c>GITHUB_CONFIG_REPO_TOKEN</c> is
    /// NEVER read from the worker's environment: it exists only on the git CHILD PROCESS
    /// environment (the askpass mechanism sets it just before each git invocation), so it is
    /// not a candidate in this chain and is not part of the operator snapshot.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The environment snapshot has not been taken yet — call <see cref="EnsureProvisionedAsync"/>
    /// first.
    /// </exception>
    public string? ResolveConfigRepoCredential()
    {
        EnsureSnapshotTaken();

        // In-memory provisioned token first (whitespace is absence, so a cleared field
        // falls through to the environment chain), then the env aliases. Each candidate
        // is passed RAW — GitCredentialResolver treats whitespace as absence per step
        // and returns the selected candidate unchanged.
        return GitCredentialResolver.Resolve(
            _provisionedGithubToken,
            _readEnv(GhTokenVar),
            _readEnv(GitHubTokenVar));
    }

    /// <summary>
    /// The credential group a task's provider requires.
    /// </summary>
    public enum CredentialRequirement
    {
        /// <summary>No mandatory credential — a local Ollama needs none.</summary>
        None,

        /// <summary>The GH_TOKEN/GITHUB_TOKEN alias group.</summary>
        GitHubTokenAlias,

        /// <summary>The <c>OLLAMA_API_KEY</c> variable.</summary>
        OllamaApiKey,
    }

    /// <summary>
    /// Fetches provisioning UNCONDITIONALLY (never only on credential absence) and applies it,
    /// then verifies that the credential the task's provider needs is actually available.
    /// <para>
    /// An RPC failure is NON-FATAL: the worker falls back to operator-provided environment
    /// variables and continues. The next first-client-creation retries the fetch.
    /// </para>
    /// </summary>
    /// <param name="taskModel">The task's model, used only to resolve the required credential.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureProvisionedAsync(string? taskModel, CancellationToken ct)
    {
        var fetched = await FetchAndApplyAsync(ct);

        // Credential recovery routes through the SAME provider-resolution algorithm the chat
        // client factory uses, so a wrong-provider provisioning is detected rather than masked.
        var requirement = ResolveRequiredCredential(taskModel);
        if (requirement == CredentialRequirement.None || IsSatisfied(requirement))
            return;

        if (!fetched)
        {
            _log.Warn($"No {DescribeRequirement(requirement)} available and provisioning is unavailable " +
                      "— falling back to operator environment; the fetch is retried before the next client creation.");
            return;
        }

        _log.Warn($"Provisioning succeeded but supplied no {DescribeRequirement(requirement)}, " +
                  "which this task's provider requires — client creation may fail.");
    }

    /// <summary>
    /// Resolves which credential group a task needs by delegating provider resolution to
    /// <c>ChatClientFactory.ParseProviderAndModel</c>, so an unprefixed or null model falls back
    /// to <c>LLM_PROVIDER</c> exactly as the chat client factory does.
    /// </summary>
    /// <param name="taskModel">The task's model string, possibly unprefixed or <c>null</c>.</param>
    /// <returns>The credential group required by the resolved provider.</returns>
    /// <exception cref="InvalidOperationException">
    /// The resolved provider is not one this system supports. That is an operator error, not a
    /// provisioning case, so it is never silently defaulted.
    /// </exception>
    public static CredentialRequirement ResolveRequiredCredential(string? taskModel)
    {
        var (provider, _) = ChatClientFactory.ParseProviderAndModel(taskModel);

        return provider switch
        {
            CopilotProvider or GitHubProvider => CredentialRequirement.GitHubTokenAlias,
            OllamaCloudProvider => CredentialRequirement.OllamaApiKey,
            OllamaLocalProvider => CredentialRequirement.None,
            _ => throw new InvalidOperationException(
                $"Unsupported LLM provider '{provider}' — cannot determine which credentials this task requires."),
        };
    }

    /// <summary>
    /// Performs the RPC and applies the response.
    /// <para>
    /// On RPC failure NO stale provisioned value may survive: every variable this provisioner
    /// previously wrote is restored to its pre-first-fetch operator snapshot value, or removed
    /// when the operator never set it. That is what makes the documented "fall back to operator
    /// env" behaviour real — otherwise provider recovery would resolve against a stale
    /// provisioned <c>LLM_PROVIDER</c> and keep using a stale token or API key.
    /// </para>
    /// <para>
    /// CALLER CANCELLATION IS NOT AN AVAILABILITY FAILURE. gRPC surfaces a cancelled call as
    /// <see cref="StatusCode.Cancelled"/>, which is indistinguishable by status alone from a
    /// server-side cancel. When the caller's own token is the cause, the cancellation is
    /// rethrown as <see cref="OperationCanceledException"/> BEFORE any fallback, so the caller
    /// observes cancellation instead of a silently degraded "continue on operator env" path.
    /// Genuine availability failures (<see cref="StatusCode.Unavailable"/> and every other
    /// status) stay on the non-fatal revert-and-continue path.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when the response was fetched and applied; <c>false</c> when the RPC failed.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled — the fetch is abandoned, not downgraded to a fallback.
    /// </exception>
    private async Task<bool> FetchAndApplyAsync(CancellationToken ct)
    {
        EnsureSnapshot();

        GetWorkerConfigResponse response;
        try
        {
            response = await _fetch(new GetWorkerConfigRequest { WorkerId = _workerId }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The fetch delegate may surface cancellation directly rather than as an
            // RpcException. Propagate — never treat it as unavailability.
            throw;
        }
        catch (RpcException ex)
        {
            // Cancellation requested by THIS caller is a real cancellation, not a fallback case.
            // Rethrow before touching provisioned state so the caller unwinds normally.
            ct.ThrowIfCancellationRequested();

            // The provisioned config repo URL and the in-memory provisioned GitHub token are
            // part of the provisioned state: a stale-but-known URL or token must NOT survive
            // an RPC failure. Both are cleared BEFORE the fallible env revert so they are
            // dropped even if an environment write throws.
            _provisionedConfigRepoUrl = null;
            _provisionedGithubToken = null;

            // Drop every provisioned value BEFORE reporting the fallback, so the subsequent
            // credential check and any later client creation see only operator-provided state.
            var reverted = RevertProvisionedToOperatorSnapshot();

            // Sanitized: an RPC error payload can echo provisioned configuration.
            _log.Warn($"GetWorkerConfig RPC failed [{SafeExceptionLog.Describe(ex)}] " +
                      $"— reverted provisioned vars=[{Render(reverted)}]; falling back to operator-provided " +
                      "environment variables; will retry before the next client creation.");
            return false;
        }

        Apply(response);
        return true;
    }

    /// <summary>
    /// Restores every currently-provisioned variable to the value captured in the pre-first-fetch
    /// operator snapshot, removing it entirely when the operator had not set it. A variable that
    /// was operator-provided in the snapshot is never in <see cref="_provisionedVars"/> to begin
    /// with, so an initial operator value can never be touched here.
    /// </summary>
    /// <returns>The NAMES of the variables that were reverted, for logging.</returns>
    private List<string> RevertProvisionedToOperatorSnapshot()
    {
        var reverted = new List<string>();

        // Copy first: the loop mutates the set.
        foreach (var name in _provisionedVars.ToArray())
        {
            // Null restores the operator value when one existed, else removes the variable.
            var operatorValue = _operatorSnapshot is not null
                && _operatorSnapshot.TryGetValue(name, out var snapshot)
                ? snapshot
                : null;

            _writeEnv(name, operatorValue);
            _provisionedVars.Remove(name);
            reverted.Add(name);
        }

        return reverted;
    }

    /// <summary>
    /// Applies a provisioning response to the process environment under the operator-override,
    /// alias-precedence and whitespace-is-absence rules.
    /// </summary>
    /// <param name="response">The orchestrator's response.</param>
    public void Apply(GetWorkerConfigResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        EnsureSnapshot();

        var applied = new List<string>();
        var cleared = new List<string>();

        ApplyTokenAlias(Normalize(response.HasGithubToken ? response.GithubToken : null), applied, cleared);

        ApplyVar(LlmProviderVar, Normalize(response.HasLlmProvider ? response.LlmProvider : null), applied, cleared);
        ApplyVar(OllamaUrlVar, Normalize(response.HasOllamaUrl ? response.OllamaUrl : null), applied, cleared);
        ApplyVar(OllamaApiKeyVar, Normalize(response.HasOllamaApiKey ? response.OllamaApiKey : null), applied, cleared);
        ApplyVar(OllamaModelVar, Normalize(response.HasOllamaModel ? response.OllamaModel : null), applied, cleared);
        ApplyVar(GitHubModelVar, Normalize(response.HasGithubModel ? response.GithubModel : null), applied, cleared);

        // The config repo URL and the in-memory provisioned GitHub token are tracked in
        // memory only — they are NEVER written to the environment by these fields (the
        // env only ever carries the operator value for CONFIG_REPO_URL; the token is still
        // written to GH_TOKEN by ApplyTokenAlias below under the operator-override and
        // alias-precedence rules, because SharpCoder's ChatClientFactory reads GH_TOKEN
        // from the env for LLM auth). Whitespace is absence: a response with no/whitespace
        // token CLEARS the in-memory field. Both fields are set UNCONDITIONALLY — not
        // gated on operator overrides, matching the stale-state contract.
        _provisionedConfigRepoUrl = Normalize(response.HasConfigRepoUrl ? response.ConfigRepoUrl : null);
        _provisionedGithubToken = Normalize(response.HasGithubToken ? response.GithubToken : null);

        // Names only — a provisioned VALUE is never written to a log.
        _log.Info($"Applied provisioning: set=[{Render(applied)}], cleared=[{Render(cleared)}]");
    }

    /// <summary>Renders a list of variable names for logging, or a placeholder when empty.</summary>
    private static string Render(IReadOnlyList<string> names) =>
        names.Count == 0 ? "(none)" : string.Join(", ", names);

    /// <summary>Treats whitespace as absence.</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Captures the operator-provided environment exactly once, BEFORE the first provisioning
    /// call, so no retry can mistake a previously-provisioned value for an operator override.
    /// <c>CONFIG_REPO_URL</c> is registered as a tracked variable so operator-vs-provisioned
    /// tracking works for the config repo URL; a provisioned <c>CONFIG_REPO_URL</c> is NEVER
    /// written back to the environment (the env only ever carries the operator value).
    /// </summary>
    private void EnsureSnapshot()
    {
        if (_operatorSnapshot is not null) return;

        _operatorSnapshot = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GhTokenVar] = Normalize(_readEnv(GhTokenVar)),
            [GitHubTokenVar] = Normalize(_readEnv(GitHubTokenVar)),
            [ConfigRepoUrlVar] = Normalize(_readEnv(ConfigRepoUrlVar)),
        };
        foreach (var name in SettingVars)
            _operatorSnapshot[name] = Normalize(_readEnv(name));
    }

    /// <summary>
    /// Guards the config-repo accessors: the operator snapshot must exist before any accessor
    /// use. Callers always run <see cref="EnsureProvisionedAsync"/> first, which takes the
    /// snapshot exactly once.
    /// </summary>
    private void EnsureSnapshotTaken()
    {
        if (_operatorSnapshot is null)
            throw new InvalidOperationException(
                "The environment snapshot has not been taken yet — call EnsureProvisionedAsync before using the config-repo accessors.");
    }

    /// <summary>
    /// Whether the operator supplied this variable before any provisioning happened.
    /// </summary>
    private bool IsOperatorProvided(string name) =>
        _operatorSnapshot is not null
        && _operatorSnapshot.TryGetValue(name, out var value)
        && value is not null;

    /// <summary>
    /// Applies the token under alias precedence: an operator value in EITHER alias suppresses
    /// the environment write entirely, and provisioning only ever writes <see cref="GhTokenVar"/>
    /// — never <see cref="GitHubTokenVar"/>, so an operator who set only <c>GITHUB_TOKEN</c>
    /// never gets a competing <c>GH_TOKEN</c>. The in-memory provisioned token field
    /// (<see cref="_provisionedGithubToken"/>, consumed only by the config-repo credential
    /// chain) is updated independently in <see cref="Apply"/> and is NOT affected by this gate.
    /// </summary>
    private void ApplyTokenAlias(string? provisioned, List<string> applied, List<string> cleared)
    {
        if (IsOperatorProvided(GhTokenVar) || IsOperatorProvided(GitHubTokenVar))
            return;

        ApplyVar(GhTokenVar, provisioned, applied, cleared);
    }

    /// <summary>
    /// Sets, replaces or clears one provisioned variable. Operator values are never touched.
    /// </summary>
    private void ApplyVar(string name, string? provisioned, List<string> applied, List<string> cleared)
    {
        if (IsOperatorProvided(name))
            return;

        if (provisioned is not null)
        {
            _writeEnv(name, provisioned);
            _provisionedVars.Add(name);
            applied.Add(name);
            return;
        }

        // No longer provisioned: clear the value this provisioner previously wrote.
        if (_provisionedVars.Remove(name))
        {
            _writeEnv(name, null);
            cleared.Add(name);
        }
    }

    /// <summary>
    /// Whether the credential group is currently satisfied in the process environment.
    /// </summary>
    private bool IsSatisfied(CredentialRequirement requirement) => requirement switch
    {
        CredentialRequirement.None => true,
        CredentialRequirement.GitHubTokenAlias =>
            Normalize(_readEnv(GhTokenVar)) is not null || Normalize(_readEnv(GitHubTokenVar)) is not null,
        CredentialRequirement.OllamaApiKey => Normalize(_readEnv(OllamaApiKeyVar)) is not null,
        _ => throw new InvalidOperationException($"Unhandled credential requirement '{requirement}'."),
    };

    /// <summary>Renders a credential group by VARIABLE NAME for logging.</summary>
    private static string DescribeRequirement(CredentialRequirement requirement) => requirement switch
    {
        CredentialRequirement.None => "credential",
        CredentialRequirement.GitHubTokenAlias => $"{GhTokenVar}/{GitHubTokenVar}",
        CredentialRequirement.OllamaApiKey => OllamaApiKeyVar,
        _ => throw new InvalidOperationException($"Unhandled credential requirement '{requirement}'."),
    };
}
