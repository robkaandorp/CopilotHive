using System.Text.RegularExpressions;

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

    // ── Factory ────────────────────────────────────────────────────────────────

    private static WorkerConfigProvisioner Create(InMemoryEnv env, FetchController fetch) =>
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
    // ResolveConfigRepoCredential — precedence, whitespace, revert, guard
    // ===========================================================================

    [Fact]
    public async Task ResolveConfigRepoCredential_OperatorGhToken_Wins()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "operator-gh-token"),
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var fetch = new FetchController();
        // Provisioning writes a token, but operator GH_TOKEN wins.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("operator-gh-token", prov.ResolveConfigRepoCredential());
    }

    [Fact]
    public async Task ResolveConfigRepoCredential_OperatorGithubToken_WinsOverProvisionedWhenNoGhToken()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var fetch = new FetchController();
        // Provisioning would write GH_TOKEN, but the alias group is satisfied by operator GITHUB_TOKEN,
        // so no GH_TOKEN is written. The resolver must then read the operator GITHUB_TOKEN.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]); // alias precedence: not provisioned
        Assert.Equal("operator-github-token", prov.ResolveConfigRepoCredential());
    }

    [Fact]
    public async Task ResolveConfigRepoCredential_NoOperatorToken_ProvisionedTokenApplies()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // No operator token; the provisioned token (written to GH_TOKEN) is the resolved credential.
        Assert.Equal("ghp_provisioned", env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolveConfigRepoCredential_WhitespaceOperatorTokens_TreatedAsAbsent(string whitespace)
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, whitespace),
            (WorkerConfigProvisioner.GitHubTokenVar, whitespace));
        var fetch = new FetchController();
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        var prov = Create(env, fetch);

        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // Whitespace operator values = absent; the provisioned token applies.
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// Bug-2 regression (iteration 2): <c>ResolveConfigRepoCredential</c> must apply
    /// whitespace-as-absent at EACH precedence step. The operator set <c>GH_TOKEN</c> to a real
    /// value (so <c>IsOperatorProvided</c> is true), but the LIVE env value is whitespace. The
    /// iteration-1 code returned the normalized whitespace (null) immediately and stopped; the
    /// fix falls through to <c>GITHUB_TOKEN</c>. Asserting the EXACT <c>GITHUB_TOKEN</c> value
    /// makes this removal-proof: reverting the fix returns null instead.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolveConfigRepoCredential_WhitespaceLiveGhToken_FallsThroughToGithubToken(string whitespace)
    {
        // Operator set BOTH aliases to real values — so both are "operator-provided" per the
        // snapshot. The snapshot is taken before the first fetch.
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "operator-gh-token"),
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // Provisioning supplies a token but does NOT write GH_TOKEN (operator alias group is
        // satisfied), so the operator values are untouched.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("operator-gh-token", prov.ResolveConfigRepoCredential());

        // Mutate the LIVE GH_TOKEN to whitespace AFTER the snapshot was taken. This simulates an
        // external change (e.g. another process cleared it). IsOperatorProvided still returns
        // true (the snapshot captured the real value), but the live value is now whitespace.
        env.Write(WorkerConfigProvisioner.GhTokenVar, whitespace);

        // The resolver must fall THROUGH the whitespace GH_TOKEN to the valid GITHUB_TOKEN.
        Assert.Equal("operator-github-token", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// Bug-2 regression (iteration 2): <c>ResolveConfigRepoCredential</c> must apply
    /// whitespace-as-absent at EACH precedence step. The operator set ONLY <c>GITHUB_TOKEN</c>
    /// (no operator <c>GH_TOKEN</c>), so the operator-GH_TOKEN branch is not entered at all and
    /// the operator-GITHUB_TOKEN branch IS entered; the LIVE <c>GITHUB_TOKEN</c> is whitespace,
    /// so the resolver must fall through to the provisioned token (written to <c>GH_TOKEN</c>).
    /// Asserting the EXACT provisioned token value makes this removal-proof: if the per-step
    /// GITHUB_TOKEN normalization were removed (whitespace treated as present), the resolver
    /// would return the whitespace from the GITHUB_TOKEN branch instead of the provisioned token.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ResolveConfigRepoCredential_WhitespaceLiveGithubToken_FallsThroughToProvisionedToken(string whitespace)
    {
        // Operator set ONLY GITHUB_TOKEN to a real value — so the operator-GH_TOKEN branch is
        // not entered at all, and the operator-GITHUB_TOKEN branch IS entered. The snapshot is
        // taken before the first fetch.
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GitHubTokenVar, "operator-github-token"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // Provisioning does NOT write GH_TOKEN (alias group satisfied by operator GITHUB_TOKEN).
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]); // alias precedence: not provisioned
        Assert.Equal("operator-github-token", prov.ResolveConfigRepoCredential());

        // Mutate the LIVE GITHUB_TOKEN to whitespace AFTER the snapshot was taken (simulating an
        // external change), and place the provisioned token in live GH_TOKEN — the final
        // fallback step reads it.
        env.Write(WorkerConfigProvisioner.GitHubTokenVar, whitespace);
        env.Write(WorkerConfigProvisioner.GhTokenVar, "ghp_provisioned");

        // The resolver must fall THROUGH the whitespace GITHUB_TOKEN to the provisioned token.
        // Assert the exact provisioned value — removing the per-step GITHUB_TOKEN normalization
        // returns the whitespace instead.
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// On RPC failure, the provisioned token reverts to the operator snapshot. With no operator
    /// token, <c>ResolveConfigRepoCredential</c> must read <c>null</c> from the env (the reverted
    /// state), not a stale provisioned token.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_RpcFailureAfterSuccess_RevertsProvisionedTokenToNull()
    {
        var env = new InMemoryEnv();
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("ghp_provisioned", prov.ResolveConfigRepoCredential());

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

        // The provisioned token is reverted to null (no operator value); the resolver reads null.
        Assert.Null(env[WorkerConfigProvisioner.GhTokenVar]);
        Assert.Null(prov.ResolveConfigRepoCredential());
    }

    /// <summary>
    /// On RPC failure with an operator token present, the operator token survives the revert and
    /// is the resolved credential — the stale provisioned token is gone.
    /// </summary>
    [Fact]
    public async Task ResolveConfigRepoCredential_RpcFailureAfterSuccess_OperatorTokenSurvivesRevert()
    {
        var env = new InMemoryEnv(
            (WorkerConfigProvisioner.GhTokenVar, "operator-gh-token"));
        var fetch = new FetchController();
        var prov = Create(env, fetch);

        // Provisioning does not write (operator GH_TOKEN present), so the operator value is the
        // credential before and after the failure.
        fetch.EnqueueResponse(Resp(githubToken: "ghp_provisioned", setToken: true));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("operator-gh-token", prov.ResolveConfigRepoCredential());

        fetch.EnqueueException(new RpcException(new Status(StatusCode.Unavailable, "orchestrator down")));
        await prov.EnsureProvisionedAsync(null, TestContext.Current.CancellationToken);

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

    // ===========================================================================
    // Release metadata — CHANGELOG heading + Directory.Build.props VersionPrefix
    // ===========================================================================

    [Fact]
    public void Changelog_TopHeading_Matches_DirectoryBuildProps_VersionPrefix()
    {
        var propsText = File.ReadAllText(DirectoryBuildPropsPath());
        var propsMatch = Regex.Match(propsText, @"<VersionPrefix>([^<]+)</VersionPrefix>");
        Assert.True(propsMatch.Success,
            $"{DirectoryBuildPropsPath()} must contain a non-empty <VersionPrefix> element.");

        var propsVersion = propsMatch.Groups[1].Value;

        var changelogText = File.ReadAllText(ChangelogPath());
        var headingMatch = Regex.Match(changelogText, @"^## \[([^\]]+)\]", RegexOptions.Multiline);
        Assert.True(headingMatch.Success,
            $"{ChangelogPath()} must contain at least one '## [<version>]' heading.");

        var headingVersion = headingMatch.Groups[1].Value;

        Assert.True(
            string.Equals(propsVersion, headingVersion, StringComparison.Ordinal),
            $"Directory.Build.props <VersionPrefix> is '{propsVersion}' but the top CHANGELOG.md heading '## [...]' is '{headingVersion}'; they must match.");
    }

    /// <summary>
    /// Resolves the CHANGELOG.md path relative to the test assembly location, walking up to the
    /// repository root.
    /// </summary>
    private static string ChangelogPath()
    {
        var dir = AppContext.BaseDirectory;
        // Walk up until we find CHANGELOG.md.
        while (dir is not null && !File.Exists(Path.Combine(dir, "CHANGELOG.md")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "CHANGELOG.md");
    }

    /// <summary>
    /// Resolves the Directory.Build.props path relative to the test assembly location, walking up
    /// to the repository root.
    /// </summary>
    private static string DirectoryBuildPropsPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "Directory.Build.props");
    }
}