using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for the config-repo URL accessors added to <see cref="WorkerConfigProvisioner"/>:
/// <see cref="WorkerConfigProvisioner.ProvisionedConfigRepoUrl"/>,
/// <see cref="WorkerConfigProvisioner.ResolvedConfigRepoUrl"/>, and
/// <see cref="WorkerConfigProvisioner.ResolveConfigRepoCredential"/>.
/// <para>
/// The RPC-failure revert contract is the SAME contract enforced by
/// <c>RpcFailureAfterSuccess_RemovesAllProvisionedValues</c>: after a successful provision, a
/// later failed fetch reverts every provisioned value to the operator snapshot and clears the
/// in-memory config-repo URL to <c>null</c>. These tests assert the EXACT reverted value (null),
/// not merely "not the stale value", so removing or weakening the URL-revert path fails them.
/// </para>
/// <para>
/// Every test uses in-memory env reader/writer seams and a <c>FetchController</c> — no real
/// process-env mutation, no timing delays. The collection serializes with the existing provisioner
/// tests because they share the <c>EnvVarMutation</c> collection.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class WorkerConfigProvisionerConfigRepoUrlTests
{
    // ── Test doubles ───────────────────────────────────────────────────────────

    /// <summary>
    /// An in-memory environment backed by an <c>Ordinal</c> dictionary so no test mutates the real
    /// process environment.
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
    /// RPC failure.
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
            Assert.Equal("worker-cfgrepo", req.WorkerId);
            var ex = Interlocked.Exchange(ref _nextException, null);
            if (ex is not null)
                return Task.FromException<GetWorkerConfigResponse>(ex);
            var resp = Interlocked.Exchange(ref _nextResponse, null);
            return Task.FromResult(resp ?? new GetWorkerConfigResponse());
        }
    }

    /// <summary>
    /// An in-memory environment whose <c>Write</c> throws once it sees a variable matching the
    /// configured trigger, simulating a fallible environment write (e.g. a container runtime that
    /// rejects a write mid-revert). Used to prove the provisioned config-repo URL is cleared BEFORE
    /// the fallible env-revert, so a stale URL never survives a write failure.
    /// </summary>
    private sealed class ThrowingEnv
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        private readonly string _triggerVar;
        private bool _armed;

        internal ThrowingEnv(string triggerVar, bool initiallyArmed, params (string Key, string? Value)[] initial)
        {
            _triggerVar = triggerVar;
            _armed = initiallyArmed;
            foreach (var (k, v) in initial)
                _values[k] = v;
        }

        /// <summary>Arms the throw so the next write to the trigger variable throws.</summary>
        internal void Arm() => _armed = true;

        public string? Read(string name) =>
            _values.TryGetValue(name, out var v) ? v : null;

        public void Write(string name, string? value)
        {
            if (_armed && string.Equals(name, _triggerVar, StringComparison.Ordinal))
            {
                _armed = false;
                throw new InvalidOperationException(
                    $"Simulated env-write failure while reverting '{name}'.");
            }
            _values[name] = value;
        }

        /// <summary>Gets the current value, or null if the key has never been set.</summary>
        public string? this[string name] => Read(name);
    }

    /// <summary>
    /// An in-memory environment that COUNTS every read per variable name and can be configured
    /// to THROW when a forbidden variable is read. Used to prove the never-read contract for
    /// <c>GITHUB_CONFIG_REPO_TOKEN</c>: the production code must never call
    /// <c>Read("GITHUB_CONFIG_REPO_TOKEN")</c> at all — not during the snapshot, not in
    /// <c>Apply</c>, and not in <c>ResolveConfigRepoCredential</c>. A test that only asserts the
    /// RESULT is unaffected would still pass if the variable were read and then discarded; this
    /// seam makes the READ itself observable.
    /// </summary>
    private sealed class ReadCountingEnv
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _readCounts = new(StringComparer.Ordinal);
        private readonly string? _forbiddenVar;

        internal ReadCountingEnv(string? forbiddenVar, params (string Key, string? Value)[] initial)
        {
            _forbiddenVar = forbiddenVar;
            foreach (var (k, v) in initial)
                _values[k] = v;
        }

        /// <summary>The number of times the named variable was read through this seam.</summary>
        internal int ReadCount(string name) => _readCounts.TryGetValue(name, out var c) ? c : 0;

        public string? Read(string name)
        {
            _readCounts[name] = ReadCount(name) + 1;

            if (_forbiddenVar is not null && string.Equals(name, _forbiddenVar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Forbidden environment read of '{name}' — this variable exists only on the git child process environment.");

            return _values.TryGetValue(name, out var v) ? v : null;
        }

        public void Write(string name, string? value) => _values[name] = value;

        /// <summary>Gets the current value WITHOUT counting the read.</summary>
        public string? Peek(string name) => _values.TryGetValue(name, out var v) ? v : null;
    }

    // ── Factory ────────────────────────────────────────────────────────────────
    private static WorkerConfigProvisioner Create(InMemoryEnv env, FetchController fetch) =>
        new("worker-cfgrepo", fetch.Fetch, env.Read, env.Write);

    /// <summary>
    /// Creates a provisioner backed by a <see cref="ReadCountingEnv"/> so every environment READ
    /// the production code performs is observable.
    /// </summary>
    private static WorkerConfigProvisioner CreateReadCounting(ReadCountingEnv env, FetchController fetch) =>
        new("worker-cfgrepo", fetch.Fetch, env.Read, env.Write);

    /// <summary>
    /// Creates a provisioner backed by a <see cref="ThrowingEnv"/> whose read/write seams route
    /// through the throwing environment.
    /// </summary>
    private static WorkerConfigProvisioner CreateThrowing(ThrowingEnv env, FetchController fetch) =>
        new("worker-cfgrepo", fetch.Fetch, env.Read, env.Write);

    /// <summary>
    /// Builds a response. The <c>config_repo_url</c> field uses proto3 <c>optional</c>, so setting
    /// the property marks it present (<c>HasConfigRepoUrl</c>); an unset response omits it.
    /// </summary>
    private static GetWorkerConfigResponse Resp(
        string? configRepoUrl = null, bool setConfigRepoUrl = false,
        string? githubToken = null, bool setToken = false)
    {
        var r = new GetWorkerConfigResponse();
        if (setConfigRepoUrl) r.ConfigRepoUrl = configRepoUrl;
        if (setToken) r.GithubToken = githubToken;
        return r;
    }

    // ===========================================================================
    // ProvisionedConfigRepoUrl — capture, clear, whitespace-absent, revert, guard
    // ===========================================================================

    [Fact]
    public async Task ProvisionedConfigRepoUrl_SuccessfulResponseWithUrl_CapturesExactValue()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/org/cfg-repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/org/cfg-repo.git", prov.ProvisionedConfigRepoUrl);
    }

    [Fact]
    public async Task ProvisionedConfigRepoUrl_SubsequentResponseWithNoUrl_ClearsToNull()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // First: a URL is provisioned.
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/org/first.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/org/first.git", prov.ProvisionedConfigRepoUrl);

        // Second: no config_repo_url field at all — must clear, not retain the prior value.
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(prov.ProvisionedConfigRepoUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ProvisionedConfigRepoUrl_WhitespaceValue_TreatedAsAbsent(string whitespace)
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        // A present-but-whitespace config_repo_url field is treated as absent.
        fetch.EnqueueResponse(Resp(configRepoUrl: whitespace, setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(prov.ProvisionedConfigRepoUrl);
    }

    /// <summary>
    /// The critical revert test for the URL: after a successful provision populates the URL, a
    /// later RPC failure must clear the provisioned URL to <c>null</c> — exactly the reverted
    /// state, not a stale retained value. This mirrors the contract of
    /// <c>RpcFailureAfterSuccess_RemovesAllProvisionedValues</c>.
    /// </summary>
    [Fact]
    public async Task ProvisionedConfigRepoUrl_RpcFailureAfterSuccess_RevertsToNullExactly()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // 1. A successful provision carries a config-repo URL.
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/org/stale-url.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/org/stale-url.git", prov.ProvisionedConfigRepoUrl);

        // 2. The next fetch fails.
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // 3. The reverted state is exactly null — a stale URL does NOT survive the failure.
        Assert.Null(prov.ProvisionedConfigRepoUrl);
    }

    /// <summary>
    /// After the revert, a subsequent SUCCESSFUL fetch provisions the URL cleanly again — the
    /// revert must not leave the provisioner believing a stale URL is still provisioned.
    /// </summary>
    [Fact]
    public async Task ProvisionedConfigRepoUrl_RpcFailureThenSuccess_ReprovisionsCleanly()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/org/first.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/org/first.git", prov.ProvisionedConfigRepoUrl);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(prov.ProvisionedConfigRepoUrl);

        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/org/second.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/org/second.git", prov.ProvisionedConfigRepoUrl);
    }

    /// <summary>
    /// Bug-1 regression (iteration 2): the env-revert step (<c>RevertProvisionedToOperatorSnapshot</c>)
    /// calls <c>_writeEnv</c>, which CAN throw. The iteration-1 code cleared
    /// <c>_provisionedConfigRepoUrl</c> AFTER that fallible revert, so a mid-revert write failure
    /// left a STALE URL in place. The fix clears the URL BEFORE the fallible revert.
    /// <para>
    /// This test provisions a URL, then drives an RPC failure whose env-revert throws mid-way
    /// (the first provisioned variable's write throws). The exception propagates (the revert is
    /// not swallowed), but <c>ProvisionedConfigRepoUrl</c> must ALREADY be <c>null</c> because the
    /// URL clear ran first. Asserting the EXACT reverted value (null) — not just "did not throw"
    /// — makes this removal-proof: reverting the fix (moving the clear back after the revert)
    /// makes the assertion fail with the stale URL.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProvisionedConfigRepoUrl_RpcFailureWithThrowingEnvRevert_UrlClearedBeforeFallibleWrite()
    {
        // The first provisioning writes LLM_PROVIDER (a setting var) and captures a URL.
        // The env is armed to throw when the revert tries to write LLM_PROVIDER back.
        var env = new ThrowingEnv(
            triggerVar: WorkerConfigProvisioner.LlmProviderVar,
            initiallyArmed: false,
            initial: Array.Empty<(string, string?)>());
        var fetch = new FetchController();
        var prov = CreateThrowing(env, fetch);

        // 1. A successful provision: LLM_PROVIDER is provisioned (so it is in _provisionedVars
        //    and will be reverted), and a config-repo URL is captured.
        var successResponse = new GetWorkerConfigResponse
        {
            ConfigRepoUrl = "https://github.com/org/stale-url.git",
            LlmProvider = "copilot",
        };
        fetch.EnqueueResponse(successResponse);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/org/stale-url.git", prov.ProvisionedConfigRepoUrl);
        Assert.Equal("copilot", env[WorkerConfigProvisioner.LlmProviderVar]);

        // 2. Arm the env to throw when the revert writes LLM_PROVIDER back to null.
        env.Arm();

        // 3. The next fetch fails. The revert will try to clear LLM_PROVIDER and the write throws.
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));

        // The env-write failure propagates out of FetchAndApplyAsync — the RPC-failure path does
        // not swallow env-write exceptions. We assert BOTH that it throws AND that the URL is
        // already cleared, proving the clear ran before the fallible write.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("Simulated env-write failure", thrown.Message);
        // The critical assertion: the URL is null despite the revert having thrown.
        Assert.Null(prov.ProvisionedConfigRepoUrl);
    }

    [Fact]
    public void ProvisionedConfigRepoUrl_PreSnapshot_ThrowsInvalidOperationException()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // No EnsureProvisionedAsync has run — the snapshot is not taken yet.
        var ex = Assert.Throws<InvalidOperationException>(() => prov.ProvisionedConfigRepoUrl);
        Assert.NotNull(ex.Message);
    }

    // ===========================================================================
    // ResolvedConfigRepoUrl — operator wins, provisioned fills, whitespace, revert, guard
    // ===========================================================================

    [Fact]
    public async Task ResolvedConfigRepoUrl_OperatorUrlWinsOverProvisioned()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.ConfigRepoUrlVar, "https://github.com/operator/repo.git"));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Operator value wins; the provisioned value never overrides it.
        Assert.Equal("https://github.com/operator/repo.git", prov.ResolvedConfigRepoUrl);
    }

    [Fact]
    public async Task ResolvedConfigRepoUrl_NoOperatorValue_ProvisionedValueFillsIn()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("https://github.com/provisioned/repo.git", prov.ResolvedConfigRepoUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolvedConfigRepoUrl_WhitespaceOperatorValue_ProvisionedShowsThrough(string whitespace)
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.ConfigRepoUrlVar, whitespace));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Whitespace operator value = absent, so the provisioned value shows through.
        Assert.Equal("https://github.com/provisioned/repo.git", prov.ResolvedConfigRepoUrl);
    }

    /// <summary>
    /// On RPC failure, <c>ResolvedConfigRepoUrl</c> follows the same revert contract: the
    /// provisioned URL is cleared to null. With no operator value, the resolved value must be
    /// exactly null (the reverted state), not a stale retained URL.
    /// </summary>
    [Fact]
    public async Task ResolvedConfigRepoUrl_RpcFailureAfterSuccess_RevertsToNullWhenNoOperatorValue()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/stale.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("https://github.com/provisioned/stale.git", prov.ResolvedConfigRepoUrl);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // No operator value + reverted provisioned URL = exactly null.
        Assert.Null(prov.ResolvedConfigRepoUrl);
    }

    /// <summary>
    /// On RPC failure with an operator value present, <c>ResolvedConfigRepoUrl</c> must reflect the
    /// operator value (which is never provisioned), while the provisioned URL is reverted. The
    /// resolved value must be the operator value, not the stale provisioned one.
    /// </summary>
    [Fact]
    public async Task ResolvedConfigRepoUrl_RpcFailureAfterSuccess_OperatorValueSurvivesRevert()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.ConfigRepoUrlVar, "https://github.com/operator/repo.git"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/stale.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        // Operator wins even before the failure.
        Assert.Equal("https://github.com/operator/repo.git", prov.ResolvedConfigRepoUrl);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The operator value survives the revert exactly; the stale provisioned value is gone.
        Assert.Equal("https://github.com/operator/repo.git", prov.ResolvedConfigRepoUrl);
    }

    [Fact]
    public void ResolvedConfigRepoUrl_PreSnapshot_ThrowsInvalidOperationException()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        var ex = Assert.Throws<InvalidOperationException>(() => prov.ResolvedConfigRepoUrl);
        Assert.NotNull(ex.Message);
    }

    // ===========================================================================
    // ResolveConfigRepoCredential — chain: in-memory provisioned token → GH_TOKEN
    // env → GITHUB_TOKEN env (whitespace is absence at EACH step), revert, guard
    // ===========================================================================

    /// <summary>
    /// The in-memory provisioned token is the FIRST candidate: it wins over both environment
    /// aliases even when both are set to different values. Neither alias is written through
    /// the env when the operator set one — the in-memory value resolves regardless.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_InMemoryProvisionedToken_WinsOverBothEnvAliases()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "env-gh-token"),
            (WorkerConfigProvisioner.GitHubTokenVar, "env-github-token"));
        var fetch = new FetchController();
        // A response token is applied to the in-memory field UNCONDITIONALLY — even though
        // the operator alias group is satisfied, so NOTHING is written to the env.
        fetch.EnqueueResponse(Resp(githubToken: "in-memory-token", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The env is untouched…
        Assert.Equal("env-gh-token", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("env-github-token", env[WorkerConfigProvisioner.GitHubTokenVar]);
        // …but the in-memory field still wins the chain.
        Assert.Equal("in-memory-token", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// With an EMPTY in-memory provisioned token (no response token), the chain falls through
    /// to the environment: <c>GH_TOKEN</c> resolves before <c>GITHUB_TOKEN</c>.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_NoInMemoryToken_GhTokenResolvesBeforeGithubToken()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "gh-token-value"),
            (WorkerConfigProvisioner.GitHubTokenVar, "github-token-value"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // No operator override: GH_TOKEN itself is now provisioned (alias precedence writes
        // the response token to GH_TOKEN). The chain reads the live env GH_TOKEN first.
        Assert.Equal("gh-token-value", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// With NO in-memory provisioned token (an empty response carries no token field, so the
    /// in-memory field stays null) and NO <c>GH_TOKEN</c> in the environment, the chain must
    /// reach its THIRD candidate and resolve the env <c>GITHUB_TOKEN</c> value exactly.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_NoInMemoryTokenNoGhToken_GithubTokenResolves()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, "github-token-value"));
        var fetch = new FetchController();
        // An EMPTY response: no github_token field at all, so the in-memory field stays null
        // and cannot masquerade as the GITHUB_TOKEN fallback.
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Nothing was provisioned into GH_TOKEN, so the first two candidates are absent…
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        // …and the third candidate — the env GITHUB_TOKEN — is the resolved credential.
        Assert.Equal("github-token-value", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The never-read contract for <c>GITHUB_CONFIG_REPO_TOKEN</c>, proven at the READ level
    /// rather than at the result level: the environment seam THROWS (and counts reads) if the
    /// variable is ever read. The variable exists only on the git CHILD PROCESS environment
    /// (the askpass mechanism sets it just before each git invocation), so no worker-side read
    /// path — the snapshot, <c>Apply</c>, or <c>ResolveConfigRepoCredential</c> — may touch it.
    /// <para>
    /// A result-level assertion would still pass if the production code read the variable and
    /// discarded it; this test fails outright (the throw propagates) the moment a read happens,
    /// and additionally asserts the read count is exactly zero.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_ConfigRepoTokenInEnv_IsNeverRead()
    {
        const string ForbiddenVar = "GITHUB_CONFIG_REPO_TOKEN";
        var env = new ReadCountingEnv(
            forbiddenVar: ForbiddenVar,
            initial: (ForbiddenVar, "child-process-only-token"));
        var fetch = new FetchController();
        // A full response so Apply() exercises every provisioning write path too.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = CreateReadCounting(env, fetch);

        // The snapshot + Apply must not read the forbidden variable (the seam would throw).
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The resolution chain must not read it either.
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());

        // A second cycle, this time an empty response, covers the clear path as well.
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(prov.ResolveConfigRepoCredential());

        // The decisive assertion: the variable was NEVER read, not merely ignored.
        Assert.Equal(0, env.ReadCount(ForbiddenVar));
        // Sanity: the seam DOES observe the chain's real reads, so a zero count above is
        // meaningful rather than a broken counter.
        Assert.True(env.ReadCount(WorkerConfigProvisioner.GhTokenVar) > 0);
    }

    /// <summary>
    /// Whitespace is absence at EACH step of the chain: a live env <c>GH_TOKEN</c> that is
    /// whitespace falls through to <c>GITHUB_TOKEN</c>. Asserting the EXACT value makes this
    /// removal-proof.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolveConfigRepoCredential_WhitespaceLiveGhToken_FallsThroughToGithubToken(string whitespace)
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "gh-token-value"),
            (WorkerConfigProvisioner.GitHubTokenVar, "github-token-value"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("gh-token-value", prov.ResolveConfigRepoCredential());

        // Mutate the LIVE GH_TOKEN to whitespace AFTER provisioning (simulating an external
        // change). The in-memory token is still empty, so the chain must fall through the
        // whitespace GH_TOKEN to the valid GITHUB_TOKEN.
        env.Write(WorkerConfigProvisioner.GhTokenVar, whitespace);

        Assert.Equal("github-token-value", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The whitespace-is-absence rule at the LAST chain step, genuinely reached: the in-memory
    /// token is absent (empty response) and <c>GH_TOKEN</c> is absent, so <c>GITHUB_TOKEN</c>
    /// is the only candidate examined. A whitespace value there must resolve to EXACTLY null —
    /// if the per-step whitespace handling were removed, the whitespace itself would be
    /// returned and this assertion fails.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolveConfigRepoCredential_WhitespaceGithubTokenIsLastCandidate_ResolvesNull(string whitespace)
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Set the LAST candidate to whitespace with the two earlier candidates absent, so the
        // GITHUB_TOKEN step is the one under test.
        env.Write(WorkerConfigProvisioner.GitHubTokenVar, whitespace);

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The complement of the previous test on the SAME reached step: with the in-memory token
    /// and <c>GH_TOKEN</c> absent, a real <c>GITHUB_TOKEN</c> value is resolved UNCHANGED —
    /// proving the last step is genuinely examined (not skipped) and returns the raw value.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_GithubTokenIsLastCandidate_ReturnedUnchanged()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // A padded value: returned byte-exact, exposing any trimming along the chain.
        env.Write(WorkerConfigProvisioner.GitHubTokenVar, "  padded-github-token  ");

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("  padded-github-token  ", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// On RPC failure the in-memory provisioned token is cleared (and the provisioned env token
    /// reverts). With no env candidates, <c>ResolveConfigRepoCredential</c> must resolve to
    /// EXACTLY null — not a stale token.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_RpcFailureAfterSuccess_ClearsInMemoryToken_ResolvesNull()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The reverted state is exactly null — a stale token does NOT survive the failure.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The chain passes candidates through RAW: a padded env <c>GH_TOKEN</c> is returned
    /// UNCHANGED (never trimmed) when no in-memory token exists. Complements the resolver's own
    /// untrimmed vectors — this proves the provisioner feeds candidates to
    /// GitCredentialResolver.Resolve without normalizing them first, so any
    /// trimming introduced anywhere along the chain fails this assertion.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolveConfigRepoCredential_PaddedLiveGhToken_NoInMemoryToken_ReturnedUnchanged(string whitespace)
    {
        // Operator set GITHUB_TOKEN so the response token (none here) would never reach the env;
        // the live GH_TOKEN carries surrounding whitespace and must come back WITH it.
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"),
            (WorkerConfigProvisioner.GhTokenVar, whitespace + "padded-gh-token" + whitespace));
        var fetch = new FetchController();
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(whitespace + "padded-gh-token" + whitespace, prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// On RPC failure the in-memory provisioned token is cleared, so the chain continues into
    /// the environment: an operator <c>GH_TOKEN</c> becomes the resolved credential — the stale
    /// provisioned token is gone.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_RpcFailureAfterSuccess_EnvOperatorTokenResolves()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "operator-gh-token"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // The operator alias group is satisfied, so the response token is captured in memory
        // but never written to the env; the in-memory field wins while it exists.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // In-memory token cleared → the env chain resolves the operator GH_TOKEN.
        Assert.Equal("operator-gh-token", prov.ResolveConfigRepoCredential());
    }

    [Fact]
    public void ResolveConfigRepoCredential_PreSnapshot_ThrowsInvalidOperationException()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        var ex = Assert.Throws<InvalidOperationException>(() => prov.ResolveConfigRepoCredential());
        Assert.NotNull(ex.Message);
    }

    // ===========================================================================
    // In-memory provisioned token — lifecycle (replace, clear, RPC revert, cancellation)
    //
    // ISOLATION RULE for this section: every test that asserts the IN-MEMORY token's
    // lifecycle sets a DISTINCT operator GITHUB_TOKEN. The operator alias suppresses the
    // GH_TOKEN mirror (ApplyTokenAlias never writes when either alias is operator-set), so
    // the response token lives ONLY in the in-memory field. A wrongly retained, wrongly
    // cleared or wrongly un-replaced in-memory value therefore resolves to a DIFFERENT exact
    // string than the assertion expects, and the test fails.
    // ===========================================================================

    /// <summary>The distinct operator fallback used to isolate the in-memory token's lifecycle.</summary>
    private const string OperatorFallbackToken = "operator-github-token-fallback";

    /// <summary>
    /// A later successful response with NO token CLEARS the in-memory provisioned token. The
    /// operator alias suppresses the GH_TOKEN mirror, so the resolved value falls through to the
    /// DISTINCT operator fallback — a retained stale token would resolve to "first-token".
    /// </summary>
    [Fact]
    public async Task InMemoryToken_LaterResponseWithNoToken_Clears_FallsThroughToOperatorFallback()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "first-token", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        // The token lives ONLY in memory: the mirror is suppressed by the operator alias.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("first-token", prov.ResolveConfigRepoCredential());

        // Second successful response: no github_token field at all — must clear, not retain.
        fetch.EnqueueResponse(new GetWorkerConfigResponse());
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Exact value: the operator fallback, NOT the stale "first-token".
        Assert.Equal(OperatorFallbackToken, prov.ResolveConfigRepoCredential());
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
    }

    /// <summary>
    /// A later successful response carrying a WHITESPACE token also CLEARS the in-memory value
    /// (whitespace is absence). With the mirror suppressed, the resolved value is the DISTINCT
    /// operator fallback — a retained stale token would resolve to "first-token".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task InMemoryToken_LaterResponseWithWhitespaceToken_Clears_FallsThroughToOperatorFallback(string whitespace)
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "first-token", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("first-token", prov.ResolveConfigRepoCredential());

        // A present-but-whitespace github_token field is treated as absent → clears.
        fetch.EnqueueResponse(Resp(githubToken: whitespace, setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(OperatorFallbackToken, prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// A later successful response with a FRESH token REPLACES the in-memory value. The operator
    /// alias suppresses the GH_TOKEN mirror, so the assertion can only be satisfied by the
    /// in-memory field actually being replaced: a retained first token resolves to
    /// "first-token" and a wrongly cleared field resolves to the operator fallback — both fail.
    /// </summary>
    [Fact]
    public async Task InMemoryToken_LaterResponseWithFreshToken_ReplacesExactly()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "first-token", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("first-token", prov.ResolveConfigRepoCredential());

        fetch.EnqueueResponse(Resp(githubToken: "second-fresh-token", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The mirror was never written, so this value can only come from the in-memory field.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("second-fresh-token", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// Caller cancellation surfacing as a DIRECT <see cref="OperationCanceledException"/> is
    /// excluded from the clearing: the rethrow happens before provisioned state is touched, so
    /// the in-memory token still resolves to the exact prior value. The operator alias
    /// suppresses the mirror, so a wrongly cleared field resolves to the operator fallback.
    /// </summary>
    [Fact]
    public async Task InMemoryToken_CallerCancellationDirectOce_DoesNotClear_StillResolvesExactPriorValue()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // A successful provision populates the in-memory token (mirror suppressed).
        fetch.EnqueueResponse(Resp(githubToken: "survives-cancellation", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("survives-cancellation", prov.ResolveConfigRepoCredential());

        // The caller's token is cancelled; the fetch surfaces a direct
        // OperationCanceledException, which FetchAndApplyAsync rethrows before touching
        // provisioned state.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        fetch.EnqueueException(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prov.EnsureProvisionedAsync(null, cts.Token));

        // Exact prior value — NOT the operator fallback a wrongly cleared field would yield.
        Assert.Equal("survives-cancellation", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The AMBIGUOUS gRPC shape that the exclusion actually exists for: an
    /// <c>RpcException(StatusCode.Cancelled)</c> raised while the CALLER's token is already
    /// cancelled. The <c>ct.ThrowIfCancellationRequested()</c> inside the <c>catch (RpcException)</c>
    /// block must rethrow BEFORE the token clear, so (i) an
    /// <see cref="OperationCanceledException"/> surfaces and (ii) the in-memory token still
    /// resolves to the EXACT prior value. The operator alias suppresses the GH_TOKEN mirror, so
    /// removing that guard makes this resolve to the operator fallback and fail.
    /// </summary>
    [Fact]
    public async Task InMemoryToken_CallerCancelledRpcException_DoesNotClear_StillResolvesExactPriorValue()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "provisioned-kept", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("provisioned-kept", prov.ResolveConfigRepoCredential());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "call cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prov.EnsureProvisionedAsync(null, cts.Token));

        // Exact prior value — the cancelled call never reached the clear.
        Assert.Equal("provisioned-kept", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The COMPLEMENT that pins the discrimination: a SERVER-side cancel
    /// (<c>StatusCode.Cancelled</c>) while the caller's token is NOT cancelled is an
    /// availability failure and takes the normal fallback path — the in-memory token IS
    /// cleared, so the resolved value is the DISTINCT operator fallback. Without this test the
    /// exclusion could be widened to "never clear on Cancelled" and go undetected.
    /// </summary>
    [Fact]
    public async Task InMemoryToken_ServerCancelledWithoutCallerCancel_ClearsAndFallsThroughToOperatorFallback()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "server-cancel-victim", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("server-cancel-victim", prov.ResolveConfigRepoCredential());

        // The caller's token is LIVE; the server reports Cancelled.
        fetch.EnqueueException(new RpcException(new Status(StatusCode.Cancelled, "server cancelled")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(OperatorFallbackToken, prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// The ORDERING proof: the in-memory token is cleared BEFORE the provisioner ENTERS the
    /// fallible <c>RevertProvisionedToOperatorSnapshot</c> — not merely before some later write
    /// inside it.
    /// <para>
    /// Construction: the operator set <c>GITHUB_TOKEN</c>, so <c>ApplyTokenAlias</c> never
    /// provisions <c>GH_TOKEN</c> and <c>LLM_PROVIDER</c> is the ONLY provisioned variable —
    /// hence the FIRST write the revert performs. The environment is armed to throw on that
    /// first write, so the revert fails immediately on entry.
    /// </para>
    /// <para>
    /// Decisiveness: after the exception propagates, <c>ResolveConfigRepoCredential</c> must
    /// return the DISTINCT operator fallback. Moving the clear after the revert (or removing it)
    /// leaves the stale provisioned token in memory and resolves to
    /// "token-before-throwing-revert" instead — the assertion fails on the exact value.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InMemoryToken_RpcFailureWithThrowingFirstRevertWrite_TokenClearedBeforeEnteringRevert()
    {
        // The operator alias is present from the start, so it is in the snapshot: GH_TOKEN is
        // never provisioned and LLM_PROVIDER is the only provisioned variable.
        var env = new ThrowingEnv(
            triggerVar: WorkerConfigProvisioner.LlmProviderVar,
            initiallyArmed: false,
            initial: (WorkerConfigProvisioner.GitHubTokenVar, OperatorFallbackToken));
        var fetch = new FetchController();
        var prov = CreateThrowing(env, fetch);

        var successResponse = new GetWorkerConfigResponse
        {
            GithubToken = "token-before-throwing-revert",
            LlmProvider = "copilot",
        };
        fetch.EnqueueResponse(successResponse);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("token-before-throwing-revert", prov.ResolveConfigRepoCredential());
        Assert.Equal("copilot", env.Read(WorkerConfigProvisioner.LlmProviderVar));
        // The mirror is suppressed: the token exists ONLY in memory.
        Assert.Null(env.Read(WorkerConfigProvisioner.GhTokenVar));

        // Arm the env so the revert's FIRST (and only) write throws.
        env.Arm();

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken));

        Assert.Contains("Simulated env-write failure", thrown.Message);
        // The decisive assertion: the resolved credential is the operator fallback, proving the
        // in-memory token was cleared BEFORE the revert's first write threw.
        Assert.Equal(OperatorFallbackToken, prov.ResolveConfigRepoCredential());
    }

    // ===========================================================================
    // CONFIG_REPO_URL snapshot registration — never written back to the env
    // ===========================================================================

    /// <summary>
    /// A provisioned <c>config_repo_url</c> is tracked in memory only and is NEVER written back to
    /// the <c>CONFIG_REPO_URL</c> environment variable. After a provisioning cycle that received a
    /// URL, the env var must still hold only the operator value (or remain unset).
    /// </summary>
    [Fact]
    public async Task Snapshot_ProvisionedConfigRepoUrlIsNeverWrittenToEnv()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The provisioned URL is captured in memory…
        Assert.Equal("https://github.com/provisioned/repo.git", prov.ProvisionedConfigRepoUrl);
        // …but the env var was NEVER written — it remains unset (null).
        Assert.Null(env[WorkerConfigProvisioner.ConfigRepoUrlVar]);
    }

    /// <summary>
    /// When the operator set <c>CONFIG_REPO_URL</c>, a provisioning cycle that receives a URL must
    /// NOT overwrite the operator value in the env. The env var still holds only the operator
    /// value; the provisioned value lives only in memory.
    /// </summary>
    [Fact]
    public async Task Snapshot_OperatorConfigRepoUrlIsPreserved_ProvisionedNeverOverwritesEnv()
    {
        const string OperatorUrl = "https://github.com/operator/repo.git";
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.ConfigRepoUrlVar, OperatorUrl));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The env var still holds ONLY the operator value.
        Assert.Equal(OperatorUrl, env[WorkerConfigProvisioner.ConfigRepoUrlVar]);
        // The provisioned value is captured in memory but not exposed via the env.
        Assert.Equal("https://github.com/provisioned/repo.git", prov.ProvisionedConfigRepoUrl);
        // Resolved shows the operator value (it wins).
        Assert.Equal(OperatorUrl, prov.ResolvedConfigRepoUrl);
    }

    /// <summary>
    /// After an RPC failure that reverts provisioned state, the <c>CONFIG_REPO_URL</c> env var
    /// must still hold only the operator value (or remain unset). The provisioned URL was never
    /// in the env, so the revert has nothing to write back — but this proves the env var is not
    /// touched by the revert for the config-repo URL.
    /// </summary>
    [Fact]
    public async Task Snapshot_RpcFailureDoesNotTouchOperatorConfigRepoUrlInEnv()
    {
        const string OperatorUrl = "https://github.com/operator/repo.git";
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.ConfigRepoUrlVar, OperatorUrl));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(OperatorUrl, env[WorkerConfigProvisioner.ConfigRepoUrlVar]);

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The operator value is untouched; the provisioned URL was never in the env.
        Assert.Equal(OperatorUrl, env[WorkerConfigProvisioner.ConfigRepoUrlVar]);
        Assert.Null(prov.ProvisionedConfigRepoUrl);
    }

    /// <summary>
    /// A whitespace operator <c>CONFIG_REPO_URL</c> is treated as absent in the snapshot, so the
    /// provisioned URL fills in via <c>ResolvedConfigRepoUrl</c>. The env var is not written.
    /// </summary>
    [Fact]
    public async Task Snapshot_WhitespaceOperatorConfigRepoUrl_TreatedAsAbsent_ProvisionedFills()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.ConfigRepoUrlVar, "   "));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(configRepoUrl: "https://github.com/provisioned/repo.git", setConfigRepoUrl: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Whitespace operator value = absent; the provisioned value fills in.
        Assert.Equal("https://github.com/provisioned/repo.git", prov.ResolvedConfigRepoUrl);
        // The env var still holds the whitespace (never written by provisioning).
        Assert.Equal("   ", env[WorkerConfigProvisioner.ConfigRepoUrlVar]);
    }

}