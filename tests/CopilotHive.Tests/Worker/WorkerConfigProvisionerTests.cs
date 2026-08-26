using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using SharpCoder.Providers;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for <see cref="WorkerConfigProvisioner"/> — applies orchestrator-provisioned LLM
/// configuration to the worker process environment.
/// <para>
/// Covers: token alias precedence (GH_TOKEN / GITHUB_TOKEN override group); whitespace-as-absent;
/// provisioned-vs-operator tracking (env snapshot before first provisioning call; retry
/// replace/clear rules that never touch operator values); RPC failure fallback;
/// wrong-provider credential recovery via <c>ChatClientFactory.ParseProviderAndModel</c>;
/// register-before-OAuth then fetch-after-sign-in.
/// </para>
/// <para>
/// Every test injects in-memory env reader/writer seams and TCS-gated fetch delegates — no
/// timing delays, no real process-env mutation (except the EnvVarMutation-serialized cases
/// that use real env to verify the default seam).
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class WorkerConfigProvisionerTests
{
    // ── Test doubles ───────────────────────────────────────────────────────────

    /// <summary>
    /// An in-memory environment that the provisioner reads from and writes to, backed by an
    /// <c>Ordinal</c> dictionary so no test mutates the real process environment.
    /// </summary>
    private sealed class InMemoryEnv
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        internal InMemoryEnv(params (string Key, string? Value)[] initial)
        {
            foreach (var (k, v) in initial)
                _values[k] = v;
        }

        public string? Read(string name) =>
            _values.TryGetValue(name, out var v) ? v : null;

        public void Write(string name, string? value) =>
            _values[name] = value;

        /// <summary>Gets the current value, or null if the key has never been set.</summary>
        public string? this[string name] => Read(name);
    }

    /// <summary>
    /// A fetch delegate that returns a fixed response, or throws an RpcException to simulate
    /// RPC failure. Uses a TCS gate so the test can control exactly when the fetch completes.
    /// </summary>
    private sealed class FetchController
    {
        private GetWorkerConfigResponse? _nextResponse;
        private Exception? _nextException;
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal void EnqueueResponse(GetWorkerConfigResponse response) => _nextResponse = response;
        internal void EnqueueException(Exception ex) => _nextException = ex;

        internal Task<GetWorkerConfigResponse> Fetch(GetWorkerConfigRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            Assert.Equal("worker-test", req.WorkerId);
            var ex = Interlocked.Exchange(ref _nextException, null);
            if (ex is not null)
                return Task.FromException<GetWorkerConfigResponse>(ex);
            var resp = Interlocked.Exchange(ref _nextResponse, null);
            return Task.FromResult(resp ?? new GetWorkerConfigResponse());
        }
    }

    // ── Factory ────────────────────────────────────────────────────────────────

    private static WorkerConfigProvisioner Create(InMemoryEnv env, FetchController fetch) =>
        new("worker-test", fetch.Fetch, env.Read, env.Write);

    private static GetWorkerConfigResponse Resp(
        string? githubToken = null, bool setToken = false,
        string? llmProvider = null, bool setProvider = false,
        string? ollamaUrl = null, bool setUrl = false,
        string? ollamaApiKey = null, bool setApiKey = false,
        string? ollamaModel = null, bool setOllamaModel = false,
        string? githubModel = null, bool setGithubModel = false)
    {
        var r = new GetWorkerConfigResponse();
        if (setToken) r.GithubToken = githubToken;
        if (setProvider) r.LlmProvider = llmProvider;
        if (setUrl) r.OllamaUrl = ollamaUrl;
        if (setApiKey) r.OllamaApiKey = ollamaApiKey;
        if (setOllamaModel) r.OllamaModel = ollamaModel;
        if (setGithubModel) r.GithubModel = githubModel;
        return r;
    }

    // ===========================================================================
    // 1. Token alias precedence + whitespace-as-absent
    // ===========================================================================

    [Fact]
    public async Task Apply_NoOperatorToken_ProvisionsGhToken()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task Apply_OperatorGhTokenSet_DoesNotProvisionGhToken()
    {
        var env = new InMemoryEnv((WorkerConfigProvisioner.GhTokenVar, "operator-gh-token"));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Operator value preserved — NOT replaced
        Assert.Equal("operator-gh-token", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task Apply_OperatorGithubTokenOnly_DoesNotProvisionGhToken()
    {
        // An operator who set ONLY GITHUB_TOKEN must NOT get a competing GH_TOKEN provisioned.
        var env = new InMemoryEnv((WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // GITHUB_TOKEN is the operator value, untouched
        Assert.Equal("operator-github-token", env[WorkerConfigProvisioner.GitHubTokenVar]);
        // GH_TOKEN must NOT be provisioned (alias group is satisfied by the operator's GITHUB_TOKEN)
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task Apply_BothOperatorAliasesSet_NeitherProvisioned()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "operator-gh"),
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github"));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("operator-gh", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("operator-github", env[WorkerConfigProvisioner.GitHubTokenVar]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Apply_WhitespaceOperatorToken_TreatedAsAbsent_ProvisionsGhToken(string whitespace)
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, whitespace),
            (WorkerConfigProvisioner.GitHubTokenVar, whitespace));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Whitespace counts as absent — GH_TOKEN gets provisioned
        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task Apply_WhitespaceProvisionedToken_TreatedAsAbsent()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        // The orchestrator sends a whitespace token — must be treated as absent
        fetch.EnqueueResponse(Resp(githubToken: "   ", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    // ===========================================================================
    // 2. Provisioned-vs-operator tracking + retry semantics
    // ===========================================================================

    [Fact]
    public async Task Apply_SnapshotBeforeFirstCall_LaterProvisionedValueNotMistakenForOperator()
    {
        // No operator values initially.
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First provisioning: sets GH_TOKEN
        fetch.EnqueueResponse(Resp(githubToken: "ghp_first", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_first", env[WorkerConfigProvisioner.GhTokenVar]);

        // Second provisioning: replaces GH_TOKEN with a new value.
        // Because the snapshot was taken BEFORE the first call, "ghp_first" is known to be
        // provisioned (not operator), so the replacement is allowed.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_second", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_second", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task Apply_LaterResponseReplacesProvisionedValue()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(llmProvider: "copilot", setProvider: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);

        fetch.EnqueueResponse(Resp(llmProvider: "ollama-cloud", setProvider: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    [Fact]
    public async Task Apply_LaterResponseClearsNoLongerProvisionedValue()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First response provisions OLLAMA_MODEL
        fetch.EnqueueResponse(Resp(ollamaModel: "llama3", setOllamaModel: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("llama3", env[WorkerConfigProvisioner.OllamaModelVar]);

        // Second response no longer provisions it — must be cleared
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
    }

    [Fact]
    public async Task Apply_LaterResponseNeverReplacesOperatorValue()
    {
        var env = new InMemoryEnv((WorkerConfigProvisioner.LlmProviderVar, "operator-provider"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(llmProvider: "provisioned-provider", setProvider: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("operator-provider", env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    [Fact]
    public async Task Apply_LaterResponseNeverClearsOperatorValue()
    {
        var env = new InMemoryEnv((WorkerConfigProvisioner.LlmProviderVar, "operator-provider"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First call with a provisioned value — should NOT touch operator value
        fetch.EnqueueResponse(Resp(llmProvider: "provisioned-provider", setProvider: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Second call without the field — should NOT clear operator value
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("operator-provider", env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    [Fact]
    public async Task Apply_SnapshotTakenOnce_NotRetakenOnSubsequentCalls()
    {
        // If a value was provisioned on the first call, a second call that also provisions
        // must treat the first provisioned value as PROVISIONED (not operator) because the
        // snapshot was taken before the first call. Prove this by having the second call
        // CLEAR the value — if the snapshot were re-taken, the first provisioned value would
        // look like an operator value and would NOT be cleared.
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First: provision GH_TOKEN
        fetch.EnqueueResponse(Resp(githubToken: "ghp_first", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_first", env[WorkerConfigProvisioner.GhTokenVar]);

        // Second: no token provisioned — must clear the previously-provisioned value
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task Apply_AllSettingsReplaceAndClear()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // Provision everything
        fetch.EnqueueResponse(Resp(
            llmProvider: "copilot", setProvider: true,
            ollamaUrl: "http://o:11434", setUrl: true,
            ollamaApiKey: "key1", setApiKey: true,
            ollamaModel: "llama3", setOllamaModel: true,
            githubModel: "gpt-5", setGithubModel: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("http://o:11434", env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Equal("key1", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
        Assert.Equal("llama3", env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("gpt-5", env[WorkerConfigProvisioner.GitHubModelVar]);

        // Later response: replace some, clear others
        fetch.EnqueueResponse(Resp(
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaModel: "mistral", setOllamaModel: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaUrlVar]); // cleared
        Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]); // cleared
        Assert.Equal("mistral", env[WorkerConfigProvisioner.OllamaModelVar]); // replaced
        Assert.Null(env[WorkerConfigProvisioner.GitHubModelVar]); // cleared
    }

    // ===========================================================================
    // 3. RPC failure fallback + unconditional re-fetch
    // ===========================================================================

    [Fact]
    public async Task EnsureProvisionedAsync_RpcFailure_NonFatal_FallsBackToOperatorEnv()
    {
        var env = new InMemoryEnv((WorkerConfigProvisioner.GhTokenVar, "operator-fallback"));
        var fetch = new FetchController();
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "secret-in-detail")));
        var prov = Create(env, fetch);

        // Must NOT throw — RPC failure is non-fatal
        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);

        // Operator value is the fallback
        Assert.Equal("operator-fallback", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_RpcFailure_RetriesOnNextCall()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First call: RPC fails
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "down")));
        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        Assert.Equal(1, fetch.CallCount);

        // Second call: succeeds — the fetch is retried unconditionally
        fetch.EnqueueResponse(Resp(githubToken: "ghp_after_retry", setToken: true));
        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        Assert.Equal(2, fetch.CallCount);
        Assert.Equal("ghp_after_retry", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_AlwaysFetches_EvenWhenCredentialExists()
    {
        // The fetch is UNCONDITIONAL — not only when a credential is missing.
        var env = new InMemoryEnv((WorkerConfigProvisioner.GhTokenVar, "already-have-token"));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(llmProvider: "copilot", setProvider: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);

        // The fetch happened even though the token was already present
        Assert.Equal(1, fetch.CallCount);
        // The operator token is untouched
        Assert.Equal("already-have-token", env[WorkerConfigProvisioner.GhTokenVar]);
        // Provider setting was provisioned
        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    // ===========================================================================
    // 4. Register-before-OAuth then fetch-after-sign-in
    // ===========================================================================

    [Fact]
    public async Task EnsureProvisionedAsync_NoTokenAtFirstCall_GetsTokenAfterSignIn()
    {
        // Simulates: worker registered before admin OAuth sign-in, so first provisioning
        // has no token. After sign-in, the next first-client-creation re-fetches and gets it.
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First fetch: no token (admin hasn't signed in yet)
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);

        // Second fetch: admin has signed in — token is now available
        fetch.EnqueueResponse(Resp(githubToken: "ghp_after_signin", setToken: true));
        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        Assert.Equal("ghp_after_signin", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    // ===========================================================================
    // 5. Wrong-provider credential recovery via ParseProviderAndModel
    // ===========================================================================

    [Fact]
    public void ResolveRequiredCredential_CopilotModel_RequiresGitHubTokenAlias()
    {
        // "gpt-5" is unprefixed → falls back to LLM_PROVIDER default "copilot"
        Assert.Equal(
            WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias,
            WorkerConfigProvisioner.ResolveRequiredCredential("gpt-5"));
    }

    [Fact]
    public void ResolveRequiredCredential_NullModel_FallsBackToCopilotProvider()
    {
        // null model → ParseProviderAndModel falls back to LLM_PROVIDER env, default "copilot"
        Assert.Equal(
            WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias,
            WorkerConfigProvisioner.ResolveRequiredCredential(null));
    }

    [Fact]
    public void ResolveRequiredCredential_GithubPrefixedModel_RequiresGitHubTokenAlias()
    {
        Assert.Equal(
            WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias,
            WorkerConfigProvisioner.ResolveRequiredCredential("github/gpt-5"));
    }

    [Fact]
    public void ResolveRequiredCredential_CopilotPrefixedModel_RequiresGitHubTokenAlias()
    {
        Assert.Equal(
            WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias,
            WorkerConfigProvisioner.ResolveRequiredCredential("copilot/gpt-5"));
    }

    [Fact]
    public void ResolveRequiredCredential_OllamaCloudModel_RequiresOllamaApiKey()
    {
        Assert.Equal(
            WorkerConfigProvisioner.CredentialRequirement.OllamaApiKey,
            WorkerConfigProvisioner.ResolveRequiredCredential("ollama-cloud/mistral"));
    }

    [Fact]
    public void ResolveRequiredCredential_OllamaLocalModel_RequiresNoCredential()
    {
        Assert.Equal(
            WorkerConfigProvisioner.CredentialRequirement.None,
            WorkerConfigProvisioner.ResolveRequiredCredential("ollama-local/llama3"));
    }

    [Fact]
    public async Task EnsureProvisionedAsync_CopilotModelNoToken_WarnsAboutMissingCredential()
    {
        // A copilot task with no token available and a successful fetch that didn't provide one
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(new GetWorkerConfigResponse()); // no token
        var prov = Create(env, fetch);

        // Should NOT throw — just warn
        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        // No assertion on log output here (WorkerLogger writes to Console); the key is no throw.
    }

    [Fact]
    public async Task EnsureProvisionedAsync_CopilotModelWithToken_NoWarning()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_token", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        Assert.Equal("ghp_token", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_OllamaLocalModel_NoCredentialNeeded()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(ollamaModel: "llama3", setOllamaModel: true));
        var prov = Create(env, fetch);

        // ollama-local needs no credential — should succeed without any token
        await prov.EnsureProvisionedAsync("ollama-local/llama3", TestContext.Current.CancellationToken);
        Assert.Equal("llama3", env[WorkerConfigProvisioner.OllamaModelVar]);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_OllamaCloudModelWithApiKey_Satisfied()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(ollamaApiKey: "ollama-key", setApiKey: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync("ollama-cloud/mistral", TestContext.Current.CancellationToken);
        Assert.Equal("ollama-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_RpcFailure_CopilotModelWithOperatorToken_Satisfied()
    {
        // RPC fails but operator has a token — credential is satisfied by the fallback
        var env = new InMemoryEnv((WorkerConfigProvisioner.GhTokenVar, "operator-token"));
        var fetch = new FetchController();
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "down")));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync("gpt-5", TestContext.Current.CancellationToken);
        // No throw — operator token satisfies the copilot requirement
    }

    // ── LLM_PROVIDER fallback for unprefixed models ────────────────────────────

    /// <summary>
    /// <c>ResolveRequiredCredential</c> delegates to the REAL
    /// <c>ChatClientFactory.ParseProviderAndModel</c>, which reads the PROCESS environment — not
    /// the provisioner's injected env seam. This test therefore mutates the actual process
    /// <c>LLM_PROVIDER</c> variable (serialized by the <c>EnvVarMutation</c> collection, restored
    /// in a <c>finally</c>) and asserts the EXACT resolved credential requirement.
    /// <para>
    /// Removed-proof: an unprefixed model resolves ONLY via the <c>LLM_PROVIDER</c> fallback. If
    /// that fallback were bypassed the provider would not be <c>ollama-cloud</c>, the requirement
    /// would not be <see cref="WorkerConfigProvisioner.CredentialRequirement.OllamaApiKey"/>, and
    /// these assertions would fail. The earlier version of this test set <c>LLM_PROVIDER</c> only
    /// in the provisioner's <c>InMemoryEnv</c>, which the parser never reads, so it proved nothing
    /// and passed even with the fallback broken.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ollama-cloud", WorkerConfigProvisioner.CredentialRequirement.OllamaApiKey)]
    [InlineData("ollama-local", WorkerConfigProvisioner.CredentialRequirement.None)]
    [InlineData("copilot", WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias)]
    [InlineData("github", WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias)]
    public void ResolveRequiredCredential_UnprefixedModel_ResolvesViaProcessLlmProviderFallback(
        string llmProvider, WorkerConfigProvisioner.CredentialRequirement expected)
    {
        var original = Environment.GetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, llmProvider);

            // Unprefixed: the ONLY way to a provider is the LLM_PROVIDER fallback. Pin both the
            // underlying production parser decision and the provisioner's credential mapping so
            // the test fails if either side bypasses the ambient fallback.
            var (resolvedProvider, _) = ChatClientFactory.ParseProviderAndModel("some-unprefixed-model");
            var actual = WorkerConfigProvisioner.ResolveRequiredCredential("some-unprefixed-model");

            Assert.Equal(llmProvider, resolvedProvider);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, original);
        }

        Assert.Equal(original, Environment.GetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar));
    }

    /// <summary>
    /// A null model has no prefix either, so it too resolves purely through the process
    /// <c>LLM_PROVIDER</c> fallback.
    /// </summary>
    [Fact]
    public void ResolveRequiredCredential_NullModel_ResolvesViaProcessLlmProviderFallback()
    {
        var original = Environment.GetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, "ollama-cloud");

            Assert.Equal(
                WorkerConfigProvisioner.CredentialRequirement.OllamaApiKey,
                WorkerConfigProvisioner.ResolveRequiredCredential(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, original);
        }
    }

    /// <summary>
    /// An EXPLICIT provider prefix must win over the process <c>LLM_PROVIDER</c>. Pairing this
    /// with the fallback tests pins the precedence in both directions: prefix beats env, and
    /// absence of a prefix falls through to env.
    /// </summary>
    [Fact]
    public void ResolveRequiredCredential_PrefixedModel_IgnoresProcessLlmProvider()
    {
        var original = Environment.GetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar);
        try
        {
            // Env says ollama-cloud (OLLAMA_API_KEY), but the prefix says copilot (token alias).
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, "ollama-cloud");

            Assert.Equal(
                WorkerConfigProvisioner.CredentialRequirement.GitHubTokenAlias,
                WorkerConfigProvisioner.ResolveRequiredCredential("copilot/claude-sonnet-4.6"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, original);
        }
    }

    /// <summary>
    /// End-to-end through <c>EnsureProvisionedAsync</c>: with the process <c>LLM_PROVIDER</c> set
    /// to <c>ollama-cloud</c>, an unprefixed model needs <c>OLLAMA_API_KEY</c>. The operator
    /// supplies it via the injected env, so the call completes without reporting a missing
    /// credential.
    /// </summary>
    [Fact]
    public async Task EnsureProvisionedAsync_UnprefixedModel_UsesProcessLlmProviderFallback()
    {
        var original = Environment.GetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, "ollama-cloud");

            var env = new InMemoryEnv(
                (WorkerConfigProvisioner.LlmProviderVar, "ollama-cloud"),
                (WorkerConfigProvisioner.OllamaApiKeyVar, "operator-ollama-key"));
            var fetch = new FetchController();
            var prov = Create(env, fetch);

            await prov.EnsureProvisionedAsync("some-unprefixed-model", TestContext.Current.CancellationToken);

            // The operator's OLLAMA_API_KEY is the credential the resolved provider requires,
            // and it survived untouched (provisioning supplied nothing).
            Assert.Equal("operator-ollama-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerConfigProvisioner.LlmProviderVar, original);
        }
    }

    // ===========================================================================
    // 6. Apply directly (public method)
    // ===========================================================================

    [Fact]
    public void Apply_AllFieldsPresent_WritesAllToEnv()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        prov.Apply(Resp(
            githubToken: "ghp_token", setToken: true,
            llmProvider: "copilot", setProvider: true,
            ollamaUrl: "http://o:11434", setUrl: true,
            ollamaApiKey: "key", setApiKey: true,
            ollamaModel: "llama3", setOllamaModel: true,
            githubModel: "gpt-5", setGithubModel: true));

        Assert.Equal("ghp_token", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("http://o:11434", env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Equal("key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
        Assert.Equal("llama3", env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("gpt-5", env[WorkerConfigProvisioner.GitHubModelVar]);
    }

    [Fact]
    public void Apply_NullResponse_Throws()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        Assert.Throws<ArgumentNullException>(() => prov.Apply(null!));
    }

    // ===========================================================================
    // Success -> RPC failure: provisioned values must NOT survive the fallback
    // ===========================================================================

    /// <summary>
    /// Regression test for the reviewer's MAJOR finding: after a SUCCESSFUL provision, a later
    /// FAILED fetch used to keep the stale provisioned values in the environment, so provider
    /// recovery resolved against a stale provisioned <c>LLM_PROVIDER</c> and kept using a stale
    /// token/API key - contradicting the documented "fall back to operator env" behaviour.
    /// <para>
    /// Every variable the provisioner wrote must be removed, because the operator set none of
    /// them in the pre-first-fetch snapshot.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RpcFailureAfterSuccess_RemovesAllProvisionedValues()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // 1. A successful provision populates every field.
        fetch.EnqueueResponse(Resp(
            githubToken: "ghp_provisioned", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaUrl: "http://provisioned:11434", setUrl: true,
            ollamaApiKey: "provisioned-key", setApiKey: true,
            ollamaModel: "provisioned-llama", setOllamaModel: true,
            githubModel: "provisioned-gpt", setGithubModel: true));

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);

        // 2. The next fetch fails.
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // 3. NO stale provisioned value survives.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Null(env[WorkerConfigProvisioner.GitHubModelVar]);
    }

    /// <summary>
    /// The same fallback must RESTORE - never destroy - a value the operator supplied before the
    /// first fetch. Here the operator set <c>LLM_PROVIDER</c> and <c>OLLAMA_URL</c>; provisioning
    /// may not overwrite them, and the RPC-failure revert must leave them exactly as they were
    /// while removing everything the provisioner itself wrote.
    /// </summary>
    [Fact]
    public async Task RpcFailureAfterSuccess_PreservesInitialOperatorValues()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.LlmProviderVar, "operator-provider"),
            (WorkerConfigProvisioner.OllamaUrlVar, "http://operator:11434"));

        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(
            githubToken: "ghp_provisioned", setToken: true,
            llmProvider: "provisioned-provider", setProvider: true,
            ollamaUrl: "http://provisioned:11434", setUrl: true,
            ollamaApiKey: "provisioned-key", setApiKey: true));

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Operator values were never overwritten by provisioning.
        Assert.Equal("operator-provider", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("http://operator:11434", env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("provisioned-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Operator values untouched; provisioned-only values gone.
        Assert.Equal("operator-provider", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("http://operator:11434", env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);
    }

    /// <summary>
    /// An operator token in EITHER alias suppresses provisioning entirely, so the RPC-failure
    /// revert has nothing of its own to remove and must leave the operator token intact.
    /// </summary>
    [Fact]
    public async Task RpcFailure_NeverClearsOperatorToken()
    {
        var env = new InMemoryEnv((WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Alias precedence: provisioning never wrote GH_TOKEN.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("operator-github-token", env[WorkerConfigProvisioner.GitHubTokenVar]);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Internal, "boom")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("operator-github-token", env[WorkerConfigProvisioner.GitHubTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    /// <summary>
    /// After the revert, a subsequent SUCCESSFUL fetch must provision cleanly again - the revert
    /// must not leave the provisioner believing variables are still provisioned.
    /// </summary>
    [Fact]
    public async Task RpcFailureThenSuccess_ReprovisionsCleanly()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "first-token", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("first-token", env[WorkerConfigProvisioner.GhTokenVar]);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);

        fetch.EnqueueResponse(Resp(githubToken: "second-token", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("second-token", env[WorkerConfigProvisioner.GhTokenVar]);
    }

    /// <summary>
    /// TCS-gated runtime sequence proving rollback happens only when the actual asynchronous fetch
    /// faults: while the second RPC is pending the successful response is still installed; once
    /// the RpcException arrives every provisioned token/provider/Ollama value is removed and the
    /// initial operator value remains byte-for-byte unchanged.
    /// </summary>
    [Fact]
    public async Task RpcFailureAfterGatedSuccessfulFetch_ClearsEveryStaleValueAndKeepsOperatorSnapshot()
    {
        const string OperatorGithubModel = "operator/github-model";
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubModelVar, OperatorGithubModel));
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReply = new TaskCompletionSource<GetWorkerConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReply = new TaskCompletionSource<GetWorkerConfigResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<GetWorkerConfigResponse> Fetch(GetWorkerConfigRequest request, CancellationToken ct)
        {
            Assert.Equal("worker-test", request.WorkerId);
            var call = Interlocked.Increment(ref calls);
            return call switch
            {
                1 => SignalThenAwait(firstEntered, firstReply, ct),
                2 => SignalThenAwait(secondEntered, secondReply, ct),
                _ => throw new InvalidOperationException($"Unexpected fetch call {call}."),
            };
        }

        static async Task<GetWorkerConfigResponse> SignalThenAwait(
            TaskCompletionSource<bool> entered,
            TaskCompletionSource<GetWorkerConfigResponse> reply,
            CancellationToken ct)
        {
            entered.TrySetResult(true);
            return await reply.Task.WaitAsync(ct);
        }

        var provisioner = new WorkerConfigProvisioner("worker-test", Fetch, env.Read, env.Write);

        var success = provisioner.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Snapshot has already happened, but the pending RPC has not written anything.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal(OperatorGithubModel, env[WorkerConfigProvisioner.GitHubModelVar]);

        firstReply.TrySetResult(Resp(
            githubToken: "stale-gh-token", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaUrl: "http://stale-ollama:11434", setUrl: true,
            ollamaApiKey: "stale-ollama-key", setApiKey: true,
            ollamaModel: "stale-ollama-model", setOllamaModel: true,
            githubModel: "must-not-replace-operator", setGithubModel: true));
        await success;

        Assert.Equal("stale-gh-token", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("http://stale-ollama:11434", env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Equal("stale-ollama-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);
        Assert.Equal("stale-ollama-model", env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal(OperatorGithubModel, env[WorkerConfigProvisioner.GitHubModelVar]);

        var failure = provisioner.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        await secondEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // A pending fetch does not mutate state; rollback is tied to the actual RPC fault.
        Assert.Equal("stale-gh-token", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal(OperatorGithubModel, env[WorkerConfigProvisioner.GitHubModelVar]);

        secondReply.TrySetException(new RpcException(
            new Status(StatusCode.Unavailable, "secret-bearing RPC detail")));
        await failure;

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal(OperatorGithubModel, env[WorkerConfigProvisioner.GitHubModelVar]);
        Assert.Equal(2, calls);
    }

    // ===========================================================================
    // Caller cancellation must PROPAGATE, never degrade into the availability fallback
    // ===========================================================================

    /// <summary>
    /// Regression test for the reviewer's MAJOR finding: gRPC surfaces a cancelled call as
    /// <c>StatusCode.Cancelled</c>, which the catch-all previously treated as a rolling-deployment
    /// availability failure — reverting provisioned state, logging a warning and returning false.
    /// When the CALLER's token is the cause, the cancellation must propagate instead.
    /// </summary>
    [Fact]
    public async Task EnsureProvisionedAsync_CallerCancelled_PropagatesInsteadOfFallingBack()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // gRPC reports a cancelled call as StatusCode.Cancelled.
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "call cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prov.EnsureProvisionedAsync(null, cts.Token));
    }

    /// <summary>
    /// Cancellation must propagate BEFORE any provisioned state is touched: a cancelled call is
    /// not evidence that provisioning is unavailable, so the previously-provisioned values must
    /// survive for the next attempt rather than being reverted.
    /// </summary>
    [Fact]
    public async Task EnsureProvisionedAsync_CallerCancelled_DoesNotRevertProvisionedState()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // A successful provision first.
        fetch.EnqueueResponse(Resp(
            githubToken: "ghp_provisioned", setToken: true,
            llmProvider: "copilot", setProvider: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);

        // Now the caller cancels.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "call cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prov.EnsureProvisionedAsync(null, cts.Token));

        // Untouched: cancellation is not an availability signal.
        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    /// <summary>
    /// A fetch delegate that surfaces cancellation directly as an
    /// <see cref="OperationCanceledException"/> (rather than wrapping it in an
    /// <see cref="RpcException"/>) must also propagate.
    /// </summary>
    [Fact]
    public async Task EnsureProvisionedAsync_FetchThrowsOperationCanceled_Propagates()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        fetch.EnqueueException(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prov.EnsureProvisionedAsync(null, cts.Token));
    }

    /// <summary>
    /// A SERVER-side cancel — <c>StatusCode.Cancelled</c> WITHOUT the caller having cancelled —
    /// is an availability failure and must stay on the non-fatal revert-and-continue path. This
    /// pins the discrimination: the status alone must not decide, the caller's token must.
    /// </summary>
    [Fact]
    public async Task EnsureProvisionedAsync_ServerCancelledWithoutCallerCancel_FallsBack()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);

        // Server-side cancel; the caller's token is NOT cancelled.
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "server cancelled")));

        // Non-fatal: no throw, and the provisioned value is reverted as for any availability failure.
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    /// <summary>
    /// <c>Unavailable</c> continues to take the non-fatal fallback path even when it happens to be
    /// observed while a DIFFERENT, uncancelled token is in use.
    /// </summary>
    [Fact]
    public async Task EnsureProvisionedAsync_UnavailableWithLiveToken_StillFallsBack()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "down")));

        // No throw — availability failures remain non-fatal.
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
    }
}
