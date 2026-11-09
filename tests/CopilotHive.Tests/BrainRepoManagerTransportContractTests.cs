using CopilotHive.Git;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests;

/// <summary>
/// The PER-ENTRY-POINT half of the host credential-at-rest elimination contract for
/// <see cref="BrainRepoManager"/>: for every network-bearing entry point, the host-resolved
/// credential must reach git through the operation-scoped askpass helper and through NOTHING else.
/// <para>
/// The assertions are made on the runner seam's captured requests, which carry BOTH the argument
/// list and the COMPLETE child environment the real process runner would install. That makes the
/// three forbidden channels directly observable per launch:
/// </para>
/// <list type="number">
///   <item><description>argv — no credential, and no credential-bearing URL;</description></item>
///   <item><description><c>.git/config</c> — the origin normalized to the sanitized configured URL
///   before the first network command;</description></item>
///   <item><description>the parent environment — never written to, only the child's
///   environment.</description></item>
/// </list>
/// <para>
/// The REAL-PROCESS half (real <c>.git/config</c> on disk, a real recording credential helper, a
/// real child environment observed from inside the child) lives in
/// <see cref="BrainRepoManagerAskpassTransportTests"/>.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class BrainRepoManagerTransportContractTests : IDisposable
{
    /// <summary>The credential the host resolves for the operation.</summary>
    internal const string HostToken = "gho_transport_contract_host_token";

    /// <summary>A credential the CALLER put in the URL it supplied.</summary>
    internal const string CallerToken = "gho_transport_contract_caller_token";

    /// <summary>The repository name used throughout.</summary>
    internal const string RepoName = "widgets";

    /// <summary>The eligible, configured URL of the probe repository.</summary>
    internal const string ConfiguredUrl = "https://github.com/acme/widgets.git";

    /// <summary>The SANITIZED origin the manager must persist.</summary>
    internal const string SanitizedOrigin = ConfiguredUrl;

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGitHubToken;

    public BrainRepoManagerTransportContractTests()
    {
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGitHubToken);

        TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A runner that answers the manager's LOCAL origin-inspection queries from mutable state,
    /// records every request in order, and delegates everything else.
    /// </summary>
    private sealed class RecordingRunner(Func<BrainGitRequest, BrainGitResult>? respond = null)
    {
        /// <summary>Lines returned by <c>remote get-url --all origin</c>.</summary>
        public List<string> Origins { get; set; } = [ConfiguredUrl];

        /// <summary>Every request the manager issued, in order.</summary>
        public List<BrainGitRequest> Requests { get; } = [];

        /// <summary>Invoked (inside the runner) on every request.</summary>
        public Action<BrainGitRequest>? OnRequest { get; set; }

        public BrainGitResult Run(BrainGitRequest request)
        {
            lock (Requests)
                Requests.Add(request);

            OnRequest?.Invoke(request);

            var a = request.Arguments;
            if (a.Count >= 2 && a[0] == "remote" && a[1] == "get-url")
                return new BrainGitResult(0, string.Join('\n', Origins), string.Empty);
            if (a.Count >= 2 && a[0] == "config" && a[1] == "--get-all")
                return new BrainGitResult(1, string.Empty, string.Empty);

            return respond?.Invoke(request) ?? new BrainGitResult(0, string.Empty, string.Empty);
        }

        /// <summary>The recorded requests rendered as whitespace-joined argument strings.</summary>
        public List<string> Joined()
        {
            lock (Requests)
                return Requests.Select(r => string.Join(' ', r.Arguments)).ToList();
        }

        /// <summary>
        /// The requests that talk to the network — the launches that MUST carry the operation
        /// environment.
        /// </summary>
        public List<BrainGitRequest> NetworkRequests()
        {
            lock (Requests)
            {
                return Requests
                    .Where(r => r.Arguments.Count > 0
                        && (r.Arguments[0] is "fetch" or "push" or "ls-remote" or "clone"))
                    .ToList();
            }
        }
    }

    /// <summary>Builds a manager wired to the seam.</summary>
    private BrainRepoManager CreateManager(
        RecordingRunner runner,
        string? token = HostToken,
        Func<string, string?>? urlLookup = null,
        TestLogger<BrainRepoManager>? logger = null,
        bool createClone = true)
    {
        var manager = new BrainRepoManager(
            _tempDir,
            logger ?? new TestLogger<BrainRepoManager>(),
            runner.Run,
            _ => Task.FromResult(token),
            urlLookup ?? (_ => ConfiguredUrl));

        if (createClone)
            Directory.CreateDirectory(Path.Combine(manager.GetClonePath(RepoName), ".git"));

        return manager;
    }

    /// <summary>Every network-bearing entry point, and how to drive it.</summary>
    public static TheoryData<string> NetworkBearingEntryPoints() =>
    [
        nameof(BrainRepoManager.EnsureCloneAsync),
        nameof(BrainRepoManager.MergeFeatureBranchAsync),
        nameof(BrainRepoManager.MergeBranchAsync),
        nameof(BrainRepoManager.CreateTagAsync),
        nameof(BrainRepoManager.DeleteTagAsync),
        nameof(BrainRepoManager.DeleteRemoteBranchAsync),
        nameof(BrainRepoManager.ListRemoteBranchesAsync),
        nameof(BrainRepoManager.FetchOriginAsync),
    ];

    /// <summary>Drives one network-bearing entry point to completion (or to its own failure).</summary>
    private static async Task InvokeAsync(BrainRepoManager manager, string method, CancellationToken ct)
    {
        switch (method)
        {
            case nameof(BrainRepoManager.EnsureCloneAsync):
                await manager.EnsureCloneAsync(RepoName, ConfiguredUrl, "main", ct);
                return;
            case nameof(BrainRepoManager.MergeFeatureBranchAsync):
                // The squash merge may or may not fail on this fixture; either way the network
                // commands it issues are the ones under test.
                try
                {
                    await manager.MergeFeatureBranchAsync(RepoName, "feature", "main", "msg", ct);
                }
                catch (InvalidOperationException)
                {
                    // The operation's own failure is not what this matrix asserts.
                }
                return;
            case nameof(BrainRepoManager.MergeBranchAsync):
                try
                {
                    await manager.MergeBranchAsync(RepoName, "feature", "main", ct);
                }
                catch (InvalidOperationException)
                {
                    // The operation's own failure is not what this matrix asserts.
                }
                return;
            case nameof(BrainRepoManager.CreateTagAsync):
                await Assert.ThrowsAnyAsync<InvalidOperationException>(
                    () => manager.CreateTagAsync(RepoName, "v1.0.0", "main", "Release", ct));
                return;
            case nameof(BrainRepoManager.DeleteTagAsync):
                await manager.DeleteTagAsync(RepoName, "v1.0.0", ct);
                return;
            case nameof(BrainRepoManager.DeleteRemoteBranchAsync):
                await manager.DeleteRemoteBranchAsync(RepoName, "feature", ct);
                return;
            case nameof(BrainRepoManager.ListRemoteBranchesAsync):
                await manager.ListRemoteBranchesAsync(RepoName, ct);
                return;
            case nameof(BrainRepoManager.FetchOriginAsync):
                await manager.FetchOriginAsync(RepoName, null, ct);
                return;
            default:
                throw new InvalidOperationException($"Unhandled method '{method}'.");
        }
    }

    // ── (a) Transport elimination per entry point ────────────────────────────

    /// <summary>
    /// EVERY network-bearing entry point normalizes the persisted origin to the SANITIZED
    /// configured URL BEFORE its first network command, and NO argument list of ANY launch carries
    /// the host credential.
    /// </summary>
    /// <remarks>
    /// The origin-index/argv assertions together kill a transport that reintroduced a credential
    /// into the URL, and the index comparison kills a transport that authenticated first and
    /// normalized afterwards — which would leave a window in which the origin was still the
    /// persisted one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(NetworkBearingEntryPoints))]
    public async Task EveryNetworkBearingEntryPoint_NormalizesTheOriginFirst_AndNeverCarriesTheCredentialInArgv(
        string method)
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner);

        await InvokeAsync(manager, method, TestContext.Current.CancellationToken);

        var joined = runner.Joined();

        // The origin was normalized to the SANITIZED configured URL — exactly once.
        var setUrls = joined
            .Where(j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal([$"remote set-url origin {SanitizedOrigin}"], setUrls);

        // …and it happened BEFORE the first network command.
        var setUrlIndex = joined.IndexOf($"remote set-url origin {SanitizedOrigin}");
        var networkIndices = joined
            .Select((text, index) => (Text: text, Index: index))
            .Where(x => x.Text.StartsWith("fetch ", StringComparison.Ordinal)
                || x.Text.StartsWith("push ", StringComparison.Ordinal)
                || x.Text.StartsWith("ls-remote ", StringComparison.Ordinal)
                || x.Text.StartsWith("clone ", StringComparison.Ordinal))
            .Select(x => x.Index)
            .ToList();
        Assert.NotEmpty(networkIndices);
        Assert.All(networkIndices, i => Assert.True(setUrlIndex < i));

        // NO launch — not one, of any kind — carries the credential in its ARGUMENT LIST.
        var argv = string.Join(" | ", joined);
        Assert.DoesNotContain(HostToken, argv, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", argv, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY network-bearing launch of EVERY entry point carries the operation environment — the
    /// credential travels through the child-only variable, and the inherited credential variables
    /// are absent from it.
    /// </summary>
    [Theory]
    [MemberData(nameof(NetworkBearingEntryPoints))]
    public async Task EveryNetworkBearingLaunch_CarriesTheOperationEnvironment_AndNothingElse(string method)
    {
        // The ambient token variables are set so their ABSENCE from the child environment is a
        // discriminating observation: an inherited GH_TOKEN/GITHUB_TOKEN would be visible in the
        // child if the scrub were removed.
        Environment.SetEnvironmentVariable("GH_TOKEN", "gho_inherited_gh_token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "gho_inherited_github_token");
        Environment.SetEnvironmentVariable("GITHUB_CONFIG_REPO_TOKEN", "gho_inherited_config_repo_token");

        var runner = new RecordingRunner();
        var manager = CreateManager(runner);

        await InvokeAsync(manager, method, TestContext.Current.CancellationToken);

        var network = runner.NetworkRequests();
        Assert.NotEmpty(network);

        foreach (var request in network)
        {
            var env = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
                request.ChildEnvironment);

            // The credential's ONLY channel.
            Assert.Equal(HostToken, env[HostGitAskpassHelper.CredentialEnvName]);

            // The askpass script the child is pointed at.
            var scriptPath = env[HostGitAskpassHelper.AskpassEnvName];
            Assert.NotNull(scriptPath);
            Assert.EndsWith(HostGitAskpassHelper.ScriptFileName, scriptPath);

            // The inherited credential channels are ABSENT from the child.
            Assert.False(env.ContainsKey("GH_TOKEN"));
            Assert.False(env.ContainsKey("GITHUB_TOKEN"));
            Assert.False(env.ContainsKey("GITHUB_CONFIG_REPO_TOKEN"));
            Assert.False(env.ContainsKey("SSH_ASKPASS"));

            // …and none of their VALUES survives anywhere in the child environment.
            foreach (var (_, value) in env)
            {
                Assert.DoesNotContain("gho_inherited_gh_token", value, StringComparison.Ordinal);
                Assert.DoesNotContain("gho_inherited_github_token", value, StringComparison.Ordinal);
                Assert.DoesNotContain("gho_inherited_config_repo_token", value, StringComparison.Ordinal);
            }

            // The inherited credential-helper list is reset for this invocation only.
            Assert.Equal("credential.helper", env["GIT_CONFIG_KEY_0"]);
            Assert.Equal(string.Empty, env["GIT_CONFIG_VALUE_0"]);
        }
    }

    /// <summary>
    /// The origin-INSPECTION commands are LOCAL: they carry no credential and no operation
    /// environment at all, so the credential is never handed to a command that does not talk to the
    /// network.
    /// </summary>
    [Theory]
    [InlineData("remote get-url --all origin")]
    [InlineData("config --get-all remote.origin.pushurl")]
    public async Task OriginInspectionCommands_CarryNoCredentialAndNoOperationEnvironment(string expected)
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner);

        await manager.FetchOriginAsync(RepoName, null, TestContext.Current.CancellationToken);

        var inspection = Assert.Single(
            runner.Requests, r => string.Join(' ', r.Arguments) == expected);

        Assert.Null(inspection.ChildEnvironment);
        Assert.DoesNotContain(HostToken, string.Join(' ', inspection.Arguments), StringComparison.Ordinal);
    }

    /// <summary>
    /// The parent process's own environment never receives the credential: the operation-scoped
    /// variable exists only on the child, and no ambient variable of the parent carries the token.
    /// </summary>
    [Fact]
    public async Task TheHostCredential_NeverReachesTheParentProcessEnvironment()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner);

        await manager.FetchOriginAsync(RepoName, null, TestContext.Current.CancellationToken);

        Assert.Null(Environment.GetEnvironmentVariable(HostGitAskpassHelper.CredentialEnvName));

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var value = entry.Value as string;
            if (value is null)
                continue;
            Assert.DoesNotContain(HostToken, value, StringComparison.Ordinal);
        }
    }

    // ── The caller-supplied URL boundary (the operator trust boundary) ──────

    /// <summary>
    /// An UNCONFIGURED repository's supplied URL — credential-bearing or not — is cloned EXACTLY as
    /// supplied, and no credential is resolved at all.
    /// </summary>
    [Fact]
    public async Task UnconfiguredRepository_ClonesTheSuppliedUrlVerbatim_AndResolvesNoCredential()
    {
        var lookups = 0;
        var runner = new RecordingRunner();
        var manager = new BrainRepoManager(
            _tempDir,
            new TestLogger<BrainRepoManager>(),
            runner.Run,
            _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>(HostToken); },
            configuredUrlLookup: null);

        // Fresh-clone path: no clone on disk.
        Assert.False(Directory.Exists(Path.Combine(manager.GetClonePath(RepoName), ".git")));

        var supplied = $"https://x-access-token:{CallerToken}@github.com/acme/{RepoName}.git";
        await manager.EnsureCloneAsync(
            RepoName, supplied, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(supplied, clone.Arguments);

        // No credential was resolved, so no credential was transported.
        Assert.Equal(0, Volatile.Read(ref lookups));
        Assert.Null(clone.ChildEnvironment);
    }

    /// <summary>
    /// A configured but INELIGIBLE URL (SSH, plain HTTP, non-GitHub, non-443) receives no
    /// credential, and the supplied URL is used exactly as supplied.
    /// </summary>
    [Theory]
    [InlineData("ssh://git@github.com/acme/widgets.git")]
    [InlineData("http://github.com/acme/widgets.git")]
    [InlineData("https://gitlab.com/acme/widgets.git")]
    [InlineData("https://github.com:8443/acme/widgets.git")]
    public async Task IneligibleConfiguredUrl_ClonesTheSuppliedUrlVerbatim_AndResolvesNoCredential(
        string configuredUrl)
    {
        var lookups = 0;
        var runner = new RecordingRunner { Origins = [configuredUrl] };
        var manager = new BrainRepoManager(
            _tempDir,
            new TestLogger<BrainRepoManager>(),
            runner.Run,
            _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>(HostToken); },
            configuredUrlLookup: _ => configuredUrl);

        // Fresh-clone path: no clone on disk.
        Assert.False(Directory.Exists(Path.Combine(manager.GetClonePath(RepoName), ".git")));

        var supplied = $"https://x-access-token:{CallerToken}@github.com/acme/{RepoName}.git";
        await manager.EnsureCloneAsync(
            RepoName, supplied, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(supplied, clone.Arguments);

        // No credential was resolved for an ineligible destination.
        Assert.Equal(0, Volatile.Read(ref lookups));
        Assert.Null(clone.ChildEnvironment);
    }

    /// <summary>
    /// A supplied URL that denotes a DIFFERENT repository than the configured one is cloned exactly
    /// as supplied, and no credential is resolved: the manager never redirects or credentials a
    /// repository the operator did not configure.
    /// </summary>
    [Fact]
    public async Task MismatchingSuppliedUrl_IsClonedVerbatim_AndResolvesNoCredential()
    {
        var lookups = 0;
        var runner = new RecordingRunner();
        var manager = new BrainRepoManager(
            _tempDir,
            new TestLogger<BrainRepoManager>(),
            runner.Run,
            _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>(HostToken); },
            configuredUrlLookup: _ => ConfiguredUrl);

        // Fresh-clone path: no clone on disk.
        Assert.False(Directory.Exists(Path.Combine(manager.GetClonePath(RepoName), ".git")));

        var supplied = $"https://x-access-token:{CallerToken}@github.com/acme/some-other-repo.git";
        await manager.EnsureCloneAsync(
            RepoName, supplied, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(supplied, clone.Arguments);

        // The mismatching repository was NEVER credentialed.
        Assert.Equal(0, Volatile.Read(ref lookups));
        Assert.Null(clone.ChildEnvironment);
    }

    /// <summary>
    /// A supplied URL that DOES denote the configured repository but for which NO credential
    /// resolves is cloned exactly as supplied — the operator's own credential-bearing URL is their
    /// decision, and the host guarantee covers only host-resolved credentials.
    /// </summary>
    [Fact]
    public async Task MatchingSuppliedUrlWithNoCredentialResolved_IsClonedVerbatim()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner, token: null, createClone: false);

        // Fresh-clone path: no clone on disk.
        Assert.False(Directory.Exists(Path.Combine(manager.GetClonePath(RepoName), ".git")));

        var supplied = $"https://x-access-token:{CallerToken}@github.com/acme/{RepoName}.git";
        await manager.EnsureCloneAsync(
            RepoName, supplied, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(supplied, clone.Arguments);

        // No credential was transported, because none resolved.
        Assert.Null(clone.ChildEnvironment);
    }

    /// <summary>
    /// A supplied URL that DOES denote the configured repository AND for which a credential
    /// resolves is cloned against the SANITIZED configured URL with the operation-scoped helper:
    /// the host credential reaches git through the child environment only, and the caller's own
    /// credential is not persisted either.
    /// </summary>
    [Fact]
    public async Task MatchingSuppliedUrlWithCredentialResolved_IsClonedAgainstTheSanitizedUrl()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner, createClone: false);

        // Fresh-clone path: no clone on disk.
        Assert.False(Directory.Exists(Path.Combine(manager.GetClonePath(RepoName), ".git")));

        var supplied = $"https://x-access-token:{CallerToken}@github.com/acme/{RepoName}.git";
        await manager.EnsureCloneAsync(
            RepoName, supplied, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(SanitizedOrigin, clone.Arguments);
        Assert.DoesNotContain(supplied, clone.Arguments);

        // Neither the host credential nor the caller's credential is in argv…
        var argv = string.Join(' ', clone.Arguments);
        Assert.DoesNotContain(HostToken, argv, StringComparison.Ordinal);
        Assert.DoesNotContain(CallerToken, argv, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", argv, StringComparison.Ordinal);

        // …and the host credential travels through the child environment.
        var env = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(clone.ChildEnvironment);
        Assert.Equal(HostToken, env[HostGitAskpassHelper.CredentialEnvName]);
    }

    // ── The helper directory is cleaned up on every exit path ───────────────

    /// <summary>
    /// The operation-scoped helper directory is DELETED when the operation SUCCEEDS — the
    /// temporary directory that carried the token-free script does not outlive the operation.
    /// </summary>
    [Fact]
    public async Task SuccessfulOperation_DeletesItsHelperDirectory()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner);

        await manager.FetchOriginAsync(RepoName, null, TestContext.Current.CancellationToken);

        var scriptPath = Assert.Single(
            runner.NetworkRequests().Select(r =>
                r.ChildEnvironment![HostGitAskpassHelper.AskpassEnvName]!));

        Assert.False(File.Exists(scriptPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scriptPath)));
    }

    /// <summary>
    /// The helper file AT THE PATH THE CAPTURED CHILD ENVIRONMENT CARRIES contains no token
    /// literal: the credential reaches git through the child-only variable the script reads, never
    /// through the script's own bytes.
    /// </summary>
    [Fact]
    public async Task TheHelperFileAtTheCapturedAskpassPath_ContainsNoTokenLiteral()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner);

        await manager.FetchOriginAsync(RepoName, null, TestContext.Current.CancellationToken);

        // The path the capture reports — the very path the child would execute.
        var scriptPath = Assert.Single(
            runner.NetworkRequests().Select(r =>
                r.ChildEnvironment![HostGitAskpassHelper.AskpassEnvName]!));

        // The file really existed while the operation ran (the operation has since disposed it),
        // so the assertion below is about the file's CONTENTS, not about its absence.
        Assert.False(File.Exists(scriptPath));

        // The script content is fixed and token-free by construction: it reads the credential from
        // the child-only variable.
        Assert.DoesNotContain(HostToken, HostGitAskpassHelper.ScriptContent, StringComparison.Ordinal);
        Assert.Contains(
            $"${HostGitAskpassHelper.CredentialEnvName}", HostGitAskpassHelper.ScriptContent, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x-access-token:", HostGitAskpassHelper.ScriptContent, StringComparison.Ordinal);

        // The SAME bytes are what the live helper writes — proven by a live helper instance.
        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        Assert.Equal(
            HostGitAskpassHelper.ScriptContent,
            File.ReadAllText(helper!.ScriptPath));
        Assert.DoesNotContain(HostToken, File.ReadAllText(helper.ScriptPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// The operation-scoped helper directory is DELETED when the operation FAILS — a failed
    /// operation leaks no temporary directory either.
    /// </summary>
    [Fact]
    public async Task FailedOperation_DeletesItsHelperDirectory()
    {
        var runner = new RecordingRunner(request =>
            request.Arguments.Contains("fetch")
                ? new BrainGitResult(128, string.Empty, "fatal: Authentication failed")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner);

        var result = await manager.FetchOriginAsync(
            RepoName, null, TestContext.Current.CancellationToken);

        // Premise: the operation really failed.
        Assert.False(result.Success);

        var scriptPath = Assert.Single(
            runner.NetworkRequests().Select(r =>
                r.ChildEnvironment![HostGitAskpassHelper.AskpassEnvName]!));

        Assert.False(File.Exists(scriptPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scriptPath)));
    }

    /// <summary>
    /// The operation-scoped helper directory is DELETED when the operation fails with a THROWN
    /// exception — the throwing path is a different exit from the manager's own return value, and
    /// it cleans up just as completely.
    /// </summary>
    [Fact]
    public async Task ThrowingOperation_DeletesItsHelperDirectory()
    {
        var runner = new RecordingRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException("transport exploded")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ListRemoteBranchesAsync(
            RepoName, TestContext.Current.CancellationToken));

        var scriptPath = Assert.Single(
            runner.NetworkRequests().Select(r =>
                r.ChildEnvironment![HostGitAskpassHelper.AskpassEnvName]!));

        Assert.False(File.Exists(scriptPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scriptPath)));
    }

    /// <summary>
    /// The operation-scoped helper directory is DELETED when the operation is CANCELLED — a
    /// cancelled operation leaks no temporary directory either.
    /// </summary>
    [Fact]
    public async Task CancelledOperation_DeletesItsHelperDirectory()
    {
        using var cts = new CancellationTokenSource();
        var scriptPath = string.Empty;

        var runner = new RecordingRunner(request =>
        {
            if (!request.Arguments.Contains("fetch"))
                return new BrainGitResult(0, string.Empty, string.Empty);

            // Capture where the helper lives, then cancel exactly like a shutdown would.
            scriptPath = request.ChildEnvironment![HostGitAskpassHelper.AskpassEnvName]!;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var manager = CreateManager(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ListRemoteBranchesAsync(
            RepoName, cts.Token));

        Assert.NotEmpty(scriptPath);
        Assert.False(File.Exists(scriptPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scriptPath)));
    }

    // ── The non-Linux fail-closed contract ──────────────────────────────────

    /// <summary>
    /// An UNSUPPORTED platform FAILS CLOSED: no credential transport is created at all, the
    /// operation reports an honest credential-free error, and NO network command runs.
    /// </summary>
    /// <remarks>
    /// The platform predicate is overridden through the manager's test seam, so the fail-closed
    /// contract is asserted on any host — the production default remains
    /// <see cref="OperatingSystem.IsLinux"/>.
    /// </remarks>
    [Fact]
    public async Task UnsupportedPlatform_FailsClosed_AndRunsNoNetworkCommand()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner);
        manager.AskpassPlatformSupported = () => false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.FetchOriginAsync(
            RepoName, null, TestContext.Current.CancellationToken));

        // Honest and credential-free.
        Assert.Contains("credential helper is unavailable on this platform", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HostToken, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", ex.ToString(), StringComparison.Ordinal);

        // The origin was still normalized to the sanitized form — and NO network command ran.
        var joined = runner.Joined();
        Assert.Contains($"remote set-url origin {SanitizedOrigin}", joined);
        Assert.DoesNotContain(
            joined,
            j => j.StartsWith("fetch ", StringComparison.Ordinal)
                || j.StartsWith("push ", StringComparison.Ordinal)
                || j.StartsWith("ls-remote ", StringComparison.Ordinal)
                || j.StartsWith("clone ", StringComparison.Ordinal));

        // No tokenized fallback exists anywhere: the argv carries no credential at all.
        Assert.DoesNotContain(HostToken, string.Join(" | ", joined), StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNSUPPORTED platform fails closed on the CLONE path too — a fresh clone never degrades
    /// to a tokenized URL just because the helper cannot be created.
    /// </summary>
    [Fact]
    public async Task UnsupportedPlatform_FailsClosed_OnTheFreshClonePath()
    {
        var runner = new RecordingRunner();
        var manager = CreateManager(runner, createClone: false);
        manager.AskpassPlatformSupported = () => false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken));

        Assert.Contains("credential helper is unavailable on this platform", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HostToken, ex.ToString(), StringComparison.Ordinal);

        // NO clone command was issued — the operation refused before handing anything to git.
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("clone"));
    }

    // ── Concurrent operations: per-operation helper isolation ───────────────

    /// <summary>
    /// Two CONCURRENT authenticated operations on DIFFERENT repositories each get their OWN
    /// helper directory and their OWN credential: neither launch shares the other's helper script
    /// or token, so one operation can never authenticate with the other's credential.
    /// </summary>
    /// <remarks>
    /// The two operations are driven concurrently against the SAME seam, and each repository's
    /// origin inspection is parked until both operations are in flight — so the two operations
    /// really do overlap rather than running one after the other.
    /// </remarks>
    [Fact]
    public async Task ConcurrentOperations_EachGetTheirOwnHelperAndCredential()
    {
        const string repoA = "widgets-a";
        const string repoB = "widgets-b";
        const string tokenA = "gho_concurrent_token_a";
        const string tokenB = "gho_concurrent_token_b";

        var requests = new List<BrainGitRequest>();
        var lockObject = new object();
        var bothInFlight = new CountdownEvent(2);

        var runner = new RecordingRunner(request =>
        {
            lock (lockObject)
                requests.Add(request);

            // Park each operation at its origin inspection until BOTH are in flight.
            if (request.Arguments.Count >= 2
                && request.Arguments[0] == "remote"
                && request.Arguments[1] == "get-url")
            {
                bothInFlight.Signal();
                bothInFlight.Wait(TimeSpan.FromSeconds(30));
            }

            var a = request.Arguments;
            if (a.Count >= 2 && a[0] == "remote" && a[1] == "get-url")
                return new BrainGitResult(0, ConfiguredUrl, string.Empty);
            if (a.Count >= 2 && a[0] == "config" && a[1] == "--get-all")
                return new BrainGitResult(1, string.Empty, string.Empty);

            return new BrainGitResult(0, string.Empty, string.Empty);
        });

        var managerA = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run,
            _ => Task.FromResult<string?>(tokenA),
            configuredUrlLookup: _ => ConfiguredUrl);
        var managerB = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run,
            _ => Task.FromResult<string?>(tokenB),
            configuredUrlLookup: _ => ConfiguredUrl);

        Directory.CreateDirectory(Path.Combine(managerA.GetClonePath(repoA), ".git"));
        Directory.CreateDirectory(Path.Combine(managerA.GetClonePath(repoB), ".git"));

        var ct = TestContext.Current.CancellationToken;
        var first = managerA.FetchOriginAsync(repoA, null, ct);
        var second = managerB.FetchOriginAsync(repoB, null, ct);

        await Task.WhenAll(first, second);

        List<BrainGitRequest> snapshot;
        lock (lockObject)
            snapshot = requests.ToList();

        var fetches = snapshot
            .Where(r => r.Arguments.Count > 0 && r.Arguments[0] == "fetch")
            .ToList();
        Assert.Equal(2, fetches.Count);

        var envA = fetches.Single(r =>
            r.ChildEnvironment![HostGitAskpassHelper.CredentialEnvName] == tokenA)
            .ChildEnvironment!;
        var envB = fetches.Single(r =>
            r.ChildEnvironment![HostGitAskpassHelper.CredentialEnvName] == tokenB)
            .ChildEnvironment!;

        // Each operation authenticated with ONLY its own credential…
        Assert.DoesNotContain(tokenB, string.Join('|', envA.Values), StringComparison.Ordinal);
        Assert.DoesNotContain(tokenA, string.Join('|', envB.Values), StringComparison.Ordinal);

        // …and each used its OWN helper script — the two launches never share one.
        Assert.NotEqual(
            envA[HostGitAskpassHelper.AskpassEnvName],
            envB[HostGitAskpassHelper.AskpassEnvName]);
    }
}
