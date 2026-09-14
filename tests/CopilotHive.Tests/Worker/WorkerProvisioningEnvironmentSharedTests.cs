using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for the SHARED environment provenance: the worker PROCESS's
/// <see cref="WorkerProvisioningEnvironment"/> — one operator snapshot plus one provisioned-variable
/// set — used by TWO INDEPENDENT provisioners, exactly as sequential connection attempts use it.
/// <para>
/// <b>The defect these pin.</b> A worker builds a fresh service (and therefore a fresh provisioner)
/// per connection attempt. If each provisioner snapshotted the environment for itself, attempt B
/// would read the values attempt A had just had PROVISIONED into the environment and treat them as
/// ORIGINAL operator overrides — promoting server-provisioned state into operator authority, so B
/// could never replace or clear them. Sharing the provenance keeps the snapshot ORIGINAL.
/// </para>
/// <para>
/// <b>Removal-proof strategy.</b> Every assertion is on an EXACT value of the environment (or of
/// the resolved config-repo metadata) AFTER B has run. Reintroducing the regression for B (building
/// B's provisioner with fresh, isolated state over the same fake environment) makes B's snapshot
/// capture A's provisioned values, so the clearing cases resolve to A's stale value instead of
/// <c>null</c> and the replacement cases resolve to A's value instead of B's — each assertion fails
/// by name.
/// </para>
/// <para>
/// Every test injects in-memory environment reader/writer seams and TCS/response-gated fetch
/// delegates: no real process-env mutation, no network, no timing delays.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerProvisioningEnvironmentSharedTests
{
    private const string WorkerIdA = "worker-attempt-a";
    private const string WorkerIdB = "worker-attempt-b";
    private const string FixtureModel = "copilot/fixture-model";

    // ── Test doubles ───────────────────────────────────────────────────────────

    /// <summary>
    /// An in-memory environment backed by an <c>Ordinal</c> dictionary, so no test mutates the real
    /// process environment and a REMOVED variable is distinguishable from one that was never set.
    /// </summary>
    private sealed class FakeEnv
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        internal FakeEnv(params (string Key, string? Value)[] initial)
        {
            foreach (var (key, value) in initial)
                _values[key] = value;
        }

        /// <summary>The current value, or <c>null</c> when the variable is absent.</summary>
        internal string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;

        /// <summary>Whether the variable is PRESENT (even if it holds whitespace).</summary>
        internal bool IsSet(string name) => _values.ContainsKey(name);

        internal string? Read(string name) =>
            _values.TryGetValue(name, out var value) ? value : null;

        internal void Write(string name, string? value)
        {
            if (value is null) _values.Remove(name);
            else _values[name] = value;
        }
    }

    /// <summary>
    /// A fetch delegate for ONE attempt: returns an enqueued response, an enqueued exception, or an
    /// empty response. The request's worker identity is asserted so an attempt can never fetch
    /// through another attempt's provisioner unnoticed.
    /// </summary>
    private sealed class AttemptFetch(string expectedWorkerId)
    {
        private GetWorkerConfigResponse? _nextResponse;
        private Exception? _nextException;
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal void Enqueue(GetWorkerConfigResponse response) => _nextResponse = response;

        internal void EnqueueException(Exception exception) => _nextException = exception;

        internal Task<GetWorkerConfigResponse> Fetch(GetWorkerConfigRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            Assert.Equal(expectedWorkerId, request.WorkerId);

            var exception = Interlocked.Exchange(ref _nextException, null);
            if (exception is not null)
                return Task.FromException<GetWorkerConfigResponse>(exception);

            var response = Interlocked.Exchange(ref _nextResponse, null);
            return Task.FromResult(response ?? new GetWorkerConfigResponse());
        }
    }

    /// <summary>
    /// Builds ONE attempt's provisioner over the SHARED provenance — the production shape: a
    /// separate instance (its own identity, fetch and response provenance) working through the
    /// process's environment state.
    /// </summary>
    private static WorkerConfigProvisioner Attempt(
        string workerId, AttemptFetch fetch, WorkerProvisioningEnvironment shared) =>
        new(workerId, fetch.Fetch, shared);

    /// <summary>
    /// Builds ONE attempt's provisioner over FRESH, ISOLATED provenance — the REGRESSION shape used
    /// only by the removal demonstration (it is what makes a later attempt snapshot the previous
    /// attempt's provisioned values as operator overrides).
    /// </summary>
    private static WorkerConfigProvisioner IsolatedAttempt(
        string workerId, AttemptFetch fetch, FakeEnv env) =>
        new(workerId, fetch.Fetch, env.Read, env.Write);

    private static GetWorkerConfigResponse Response(
        string? githubToken = null, bool setToken = false,
        string? llmProvider = null, bool setProvider = false,
        string? ollamaUrl = null, bool setUrl = false,
        string? ollamaApiKey = null, bool setApiKey = false,
        string? ollamaModel = null, bool setOllamaModel = false,
        string? githubModel = null, bool setGithubModel = false,
        string? configRepoUrl = null, bool setConfigRepoUrl = false)
    {
        var response = new GetWorkerConfigResponse();
        if (setToken) response.GithubToken = githubToken;
        if (setProvider) response.LlmProvider = llmProvider;
        if (setUrl) response.OllamaUrl = ollamaUrl;
        if (setApiKey) response.OllamaApiKey = ollamaApiKey;
        if (setOllamaModel) response.OllamaModel = ollamaModel;
        if (setGithubModel) response.GithubModel = githubModel;
        if (setConfigRepoUrl) response.ConfigRepoUrl = configRepoUrl;
        return response;
    }

    // ===========================================================================
    // 1. B replaces and clears A-owned values through the SAME state object
    // ===========================================================================

    /// <summary>
    /// A provisions settings, a token and response metadata; B then runs over the SAME shared
    /// provenance. B's response REPLACES the setting it carries, CLEARS the A-owned values it omits,
    /// and does NOT inherit A's config-repo URL or in-memory token. The operator's original
    /// non-blank setting survives BOTH attempts.
    /// </summary>
    [Fact]
    public async Task SharedState_BReplacesAndClearsAOwnedValues_WhileOperatorSettingSurvives()
    {
        const string OperatorOllamaUrl = "http://operator:11434";
        var env = new FakeEnv(
            (WorkerConfigProvisioner.OllamaUrlVar, OperatorOllamaUrl),
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(
            githubToken: "ghp_attempt_a", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaUrl: "http://attempt-a:11434", setUrl: true,
            ollamaModel: "attempt-a-model", setOllamaModel: true,
            githubModel: "attempt-a-github-model", setGithubModel: true,
            configRepoUrl: "https://github.com/org/attempt-a.git", setConfigRepoUrl: true));
        var a = Attempt(WorkerIdA, fetchA, shared);

        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // A's provisioned values landed; the operator alias suppressed the GH_TOKEN mirror, so the
        // operator alias is untouched and A's token lives only in A's own memory.
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("attempt-a-model", env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("attempt-a-github-model", env[WorkerConfigProvisioner.GitHubModelVar]);
        Assert.Equal(OperatorOllamaUrl, env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("operator-github-token", env[WorkerConfigProvisioner.GitHubTokenVar]);
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());
        Assert.Equal("https://github.com/org/attempt-a.git", a.ProvisionedConfigRepoUrl);

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(Response(llmProvider: "copilot", setProvider: true));
        var b = Attempt(WorkerIdB, fetchB, shared);

        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // REPLACED — B's value, not A's.
        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);
        // CLEARED — A-owned values B omitted are gone, NOT promoted to operator overrides.
        Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Null(env[WorkerConfigProvisioner.GitHubModelVar]);
        // The ORIGINAL operator setting survives both attempts untouched.
        Assert.Equal(OperatorOllamaUrl, env[WorkerConfigProvisioner.OllamaUrlVar]);
        Assert.Equal("operator-github-token", env[WorkerConfigProvisioner.GitHubTokenVar]);

        // B's response metadata does NOT come from A: B provisioned no URL and no token.
        Assert.Null(b.ProvisionedConfigRepoUrl);
        Assert.Null(b.ResolvedConfigRepoUrl);
        Assert.Equal("operator-github-token", b.ResolveConfigRepoCredential());

        // The cleared variables were genuinely REMOVED, not left as empty strings.
        Assert.False(env.IsSet(WorkerConfigProvisioner.OllamaModelVar));
        Assert.False(env.IsSet(WorkerConfigProvisioner.GitHubModelVar));
        Assert.False(env.IsSet(WorkerConfigProvisioner.GhTokenVar));
    }

    /// <summary>
    /// The same contract with the FIRST alias case: an operator who set only <c>GH_TOKEN</c>. A's
    /// response token is captured in A's memory but never written to the environment; B omitting the
    /// token must not remove or re-author the operator's value.
    /// </summary>
    [Fact]
    public async Task SharedState_OperatorGhTokenOnly_StaysAuthoritativeForBothAttempts()
    {
        const string OperatorGhToken = "operator-gh-token";
        var env = new FakeEnv((WorkerConfigProvisioner.GhTokenVar, OperatorGhToken));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(githubToken: "ghp_attempt_a", setToken: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // The operator alias suppressed the mirror; A's token is A's own in-memory provenance.
        Assert.Equal(OperatorGhToken, env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(new GetWorkerConfigResponse());
        var b = Attempt(WorkerIdB, fetchB, shared);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal(OperatorGhToken, env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.GitHubTokenVar]);
        // B has no provisioned token of its own, so the operator env value is the fallback.
        Assert.Equal(OperatorGhToken, b.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// BOTH alias cases, parameterized over the variable the OPERATOR set. In each, A's provisioned
    /// token must never be written to the OTHER alias and B's omission must never clear or re-author
    /// the operator's value — the shared provenance does not widen or narrow alias precedence.
    /// </summary>
    /// <param name="operatorAlias">The alias the operator set: <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c>.</param>
    [Theory]
    [InlineData(WorkerConfigProvisioner.GhTokenVar)]
    [InlineData(WorkerConfigProvisioner.GitHubTokenVar)]
    public async Task SharedState_BothAliasCases_OperatorValueStaysAuthoritative(string operatorAlias)
    {
        const string OperatorToken = "operator-alias-token";
        var otherAlias = string.Equals(operatorAlias, WorkerConfigProvisioner.GhTokenVar, StringComparison.Ordinal)
            ? WorkerConfigProvisioner.GitHubTokenVar
            : WorkerConfigProvisioner.GhTokenVar;

        var env = new FakeEnv((operatorAlias, OperatorToken));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(githubToken: "ghp_attempt_a", setToken: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // Neither alias was written: the operator's group is satisfied, so the mirror is suppressed,
        // and provisioning NEVER writes the non-preferred alias at all.
        Assert.Equal(OperatorToken, env[operatorAlias]);
        Assert.Null(env[otherAlias]);
        // A's response token still lives in A's own memory for the config-repo chain.
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(new GetWorkerConfigResponse());
        var b = Attempt(WorkerIdB, fetchB, shared);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal(OperatorToken, env[operatorAlias]);
        Assert.Null(env[otherAlias]);
        Assert.Equal(OperatorToken, b.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// An RPC AVAILABILITY failure on B takes the existing revert-and-continue path — and because
    /// the snapshot is the operator's, the revert CLEARS every variable B owns rather than promoting
    /// A's values. A's own in-memory response provenance is untouched by B's failure.
    /// </summary>
    [Fact]
    public async Task SharedState_BRpcAvailabilityFailure_ClearsAOwnedValuesWithoutPromotingThem()
    {
        var env = new FakeEnv();
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(
            githubToken: "ghp_attempt_a", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaModel: "attempt-a-model", setOllamaModel: true,
            ollamaApiKey: "attempt-a-key", setApiKey: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal("ghp_attempt_a", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("attempt-a-model", env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("attempt-a-key", env[WorkerConfigProvisioner.OllamaApiKeyVar]);

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        var b = Attempt(WorkerIdB, fetchB, shared);

        // Non-fatal: the attempt continues on the operator environment.
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Null(env[WorkerConfigProvisioner.OllamaApiKeyVar]);

        // A's OWN response provenance is per-attempt: a stale attempt keeps what it captured, and a
        // failure in a LATER attempt is not evidence about it.
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// B's response provenance must not come from A. A provisions a URL and a token; B's response
    /// omits both, so B's in-memory values are cleared and the chain falls through to the ORIGINAL
    /// operator environment — the intentional, documented fallback.
    /// </summary>
    [Fact]
    public async Task SharedState_BResponseMetadata_DoesNotComeFromA_OperatorFallbackStillResolves()
    {
        const string OperatorFallbackToken = "operator-original-fallback-token";
        const string OperatorConfigRepoUrl = "https://github.com/operator/repo.git";
        var env = new FakeEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken),
            (WorkerConfigProvisioner.ConfigRepoUrlVar, OperatorConfigRepoUrl));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(
            githubToken: "ghp_attempt_a", setToken: true,
            configRepoUrl: "https://github.com/org/attempt-a.git", setConfigRepoUrl: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/org/attempt-a.git", a.ProvisionedConfigRepoUrl);
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(new GetWorkerConfigResponse());
        var b = Attempt(WorkerIdB, fetchB, shared);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // B's metadata is its OWN: nothing of A's survives into B.
        Assert.Null(b.ProvisionedConfigRepoUrl);
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());
        Assert.Equal(OperatorFallbackToken, b.ResolveConfigRepoCredential());
        Assert.Equal(OperatorConfigRepoUrl, b.ResolvedConfigRepoUrl);

        // B's provisioner does not read A's in-memory token, only its OWN (absent) one, so the
        // chain reaches the original operator values exactly.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    // ===========================================================================
    // 2. Whitespace-is-absence is unchanged across the shared state
    // ===========================================================================

    /// <summary>
    /// A whitespace operator value is ABSENCE (not an override) for BOTH attempts sharing the state:
    /// A provisions over it, and B's omission then CLEARS A's value instead of leaving it because a
    /// whitespace "override" appeared to exist.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task SharedState_WhitespaceOperatorValue_IsAbsentForBothAttempts(string whitespace)
    {
        var env = new FakeEnv(
            (WorkerConfigProvisioner.GhTokenVar, whitespace),
            (WorkerConfigProvisioner.LlmProviderVar, whitespace));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(
            githubToken: "ghp_attempt_a", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal("ghp_attempt_a", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(new GetWorkerConfigResponse());
        var b = Attempt(WorkerIdB, fetchB, shared);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    /// <summary>
    /// A whitespace PROVISIONED value is absence too, and the shared state does not change that:
    /// B's present-but-whitespace token clears A's in-memory token rather than replacing it with
    /// whitespace.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task SharedState_WhitespaceProvisionedToken_BClearsItsOwnToken(string whitespace)
    {
        const string OperatorFallbackToken = "operator-original-fallback-token";
        var env = new FakeEnv((WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(githubToken: "ghp_attempt_a", setToken: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(Response(githubToken: whitespace, setToken: true));
        var b = Attempt(WorkerIdB, fetchB, shared);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal(OperatorFallbackToken, b.ResolveConfigRepoCredential());
    }

    // ===========================================================================
    // 3. Caller cancellation on B preserves applied state and PROPAGATES
    // ===========================================================================

    /// <summary>
    /// A caller-triggered cancellation on B is NOT an availability failure: it is rethrown BEFORE any
    /// fallback, so the state A applied through the shared provenance survives byte for byte and the
    /// caller observes cancellation instead of a silently degraded attempt.
    /// </summary>
    [Fact]
    public async Task SharedState_BCallerCancellation_PropagatesAndPreservesAppliedState()
    {
        var env = new FakeEnv();
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(
            githubToken: "ghp_attempt_a", setToken: true,
            llmProvider: "copilot", setProvider: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_attempt_a", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ghp_attempt_a", a.ResolveConfigRepoCredential());

        // B applies its OWN provisioning first, replacing A's provider and token and adding a model.
        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(Response(
            githubToken: "ghp_attempt_b", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaModel: "b-model", setOllamaModel: true));
        var b = Attempt(WorkerIdB, fetchB, shared);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Equal("ghp_attempt_b", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("b-model", env[WorkerConfigProvisioner.OllamaModelVar]);

        // ...then the caller cancels while B's next fetch is in flight. gRPC reports that as
        // StatusCode.Cancelled, which is indistinguishable by status alone from a server cancel — the
        // CALLER's token is what must decide.
        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();
        fetchB.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "call cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => b.EnsureProvisionedAsync(FixtureModel, callerCts.Token));

        // NOT a fallback: every applied value survives exactly, in BOTH attempts' provenance.
        Assert.Equal("ghp_attempt_b", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("b-model", env[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("ghp_attempt_b", b.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The complement that pins the discrimination for the shared state: a SERVER-side cancel
    /// (<c>StatusCode.Cancelled</c>) with a LIVE caller token is an availability failure and takes the
    /// normal revert path, clearing the A-owned values B would have inherited.
    /// </summary>
    [Fact]
    public async Task SharedState_BServerCancelledWithLiveCallerToken_RevertsToOperatorSnapshot()
    {
        var env = new FakeEnv();
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(llmProvider: "ollama-cloud", setProvider: true));
        var a = Attempt(WorkerIdA, fetchA, shared);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "server cancelled")));
        var b = Attempt(WorkerIdB, fetchB, shared);

        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
    }

    // ===========================================================================
    // 4. Independent state fixtures never influence each other
    // ===========================================================================

    /// <summary>
    /// Provenance sharing is EXPLICIT: two state objects over two separate environments are fully
    /// independent. Provisioning in one leaves the other's environment and snapshot untouched, which
    /// is what keeps the fixtures (and the PUBLIC constructor's per-instance behavior) honest.
    /// </summary>
    [Fact]
    public async Task IndependentStates_DoNotInfluenceEachOther()
    {
        var envOne = new FakeEnv((WorkerConfigProvisioner.OllamaModelVar, "operator-one-model"));
        var envTwo = new FakeEnv();
        var stateOne = new WorkerProvisioningEnvironment(envOne.Read, envOne.Write);
        var stateTwo = new WorkerProvisioningEnvironment(envTwo.Read, envTwo.Write);

        var fetchOne = new AttemptFetch(WorkerIdA);
        fetchOne.Enqueue(Response(
            githubToken: "ghp_one", setToken: true,
            llmProvider: "ollama-cloud", setProvider: true,
            ollamaModel: "provisioned-one", setOllamaModel: true));
        var one = Attempt(WorkerIdA, fetchOne, stateOne);

        var fetchTwo = new AttemptFetch(WorkerIdB);
        fetchTwo.Enqueue(Response(llmProvider: "copilot", setProvider: true));
        var two = Attempt(WorkerIdB, fetchTwo, stateTwo);

        await one.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
        await two.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // The SECOND state saw only its own environment: nothing the first attempt provisioned.
        Assert.Equal("copilot", envTwo[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Null(envTwo[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(envTwo[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("operator-one-model", envOne[WorkerConfigProvisioner.OllamaModelVar]);
        Assert.Equal("ghp_one", envOne[WorkerConfigProvisioner.GhTokenVar]);

        // A later response on ONE clears only what ONE owns.
        fetchOne.Enqueue(new GetWorkerConfigResponse());
        await one.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Null(envOne[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("copilot", envTwo[WorkerConfigProvisioner.LlmProviderVar]);
    }

    // ===========================================================================
    // 5. Redaction: names only, never values — through the shared state
    // ===========================================================================

    /// <summary>
    /// Provisioning secrets through the SHARED provenance still logs VARIABLE NAMES only. Both the
    /// apply line and the RPC-failure fallback line are exercised (the failure path renders the
    /// reverted names), and no secret VALUE appears anywhere in the captured output.
    /// </summary>
    [Fact]
    public async Task SharedState_LogsVariableNamesOnly_NeverProvisionedValues()
    {
        const string ProvisionedToken = "ghp_shared_secret_value";
        const string ProvisionedKey = "ollama-shared-secret-key";
        var env = new FakeEnv();
        var shared = new WorkerProvisioningEnvironment(env.Read, env.Write);

        var output = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(output);
        Console.SetError(output);

        try
        {
            var fetchA = new AttemptFetch(WorkerIdA);
            fetchA.Enqueue(Response(
                githubToken: ProvisionedToken, setToken: true,
                ollamaApiKey: ProvisionedKey, setApiKey: true,
                llmProvider: "ollama-cloud", setProvider: true));
            var a = Attempt(WorkerIdA, fetchA, shared);
            await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

            var fetchB = new AttemptFetch(WorkerIdB);
            fetchB.EnqueueException(new RpcException(
                new Status(StatusCode.Unavailable, $"token={ProvisionedToken} key={ProvisionedKey}")));
            var b = Attempt(WorkerIdB, fetchB, shared);
            await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        var logged = output.ToString();

        // Names ARE logged — the diagnostics stay useful…
        Assert.Contains("Applied provisioning", logged, StringComparison.Ordinal);
        Assert.Contains(WorkerConfigProvisioner.GhTokenVar, logged, StringComparison.Ordinal);
        Assert.Contains(WorkerConfigProvisioner.OllamaApiKeyVar, logged, StringComparison.Ordinal);
        Assert.Contains("GetWorkerConfig RPC failed", logged, StringComparison.Ordinal);

        // …and no provisioned VALUE ever is, not even when the failure payload echoes it.
        Assert.DoesNotContain(ProvisionedToken, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(ProvisionedKey, logged, StringComparison.Ordinal);
    }

    // ===========================================================================
    // 6. The PUBLIC constructor keeps fresh, isolated provenance
    // ===========================================================================

    /// <summary>
    /// The PUBLIC constructor's <c>readEnv</c>/<c>writeEnv</c> seams are preserved and its provenance
    /// is ISOLATED per instance: two provisioners built that way over the SAME environment each take
    /// their OWN snapshot. That is the pre-existing behavior for direct callers, and it is exactly
    /// the shape whose stale-snapshot consequence the shared production path removes.
    /// </summary>
    [Fact]
    public async Task PublicConstructor_IsolatedProvenance_SecondSnapshotSeesFirstProvisions()
    {
        var env = new FakeEnv();
        var fetchA = new AttemptFetch(WorkerIdA);
        fetchA.Enqueue(Response(llmProvider: "ollama-cloud", setProvider: true));
        var a = IsolatedAttempt(WorkerIdA, fetchA, env);
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);

        var fetchB = new AttemptFetch(WorkerIdB);
        fetchB.Enqueue(new GetWorkerConfigResponse());
        var b = IsolatedAttempt(WorkerIdB, fetchB, env);
        await b.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        // Isolated provenance: B's snapshot captured A's provisioned value as an "operator" value,
        // so B's omission does NOT clear it. Documented here because it is the very consequence the
        // shared production path prevents — the assertions above are the fix's contract.
        Assert.Equal("ollama-cloud", env[WorkerConfigProvisioner.LlmProviderVar]);

        // The seams themselves are the caller's: B's Apply writes through the supplied writer.
        b.Apply(Response(githubToken: "ghp_b", setToken: true));
        Assert.Equal("ghp_b", env[WorkerConfigProvisioner.GhTokenVar]);

        // Isolation holds in the OTHER direction too: A's provisioned tracking never saw B's write,
        // so A's own revert touches only A's variable and leaves B's value alone. With a SHARED state
        // object B's write would be in the same provisioned set and would be reverted here.
        fetchA.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "down")));
        await a.EnsureProvisionedAsync(FixtureModel, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.LlmProviderVar]);
        Assert.Equal("ghp_b", env[WorkerConfigProvisioner.GhTokenVar]);
    }
}
