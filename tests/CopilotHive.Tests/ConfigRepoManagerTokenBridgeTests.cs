using System.Collections.Concurrent;
using CopilotHive.Configuration;

namespace CopilotHive.Tests;

/// <summary>
/// Shared fake for the <c>ConfigRepoManager.GitRunner</c> seam: records every invocation in
/// order and answers from a caller-supplied exit-code/throw policy.
/// </summary>
internal sealed class RecordingGitRunner
{
    private readonly Func<string[], ConfigRepoManager.GitRunResult>? _policy;
    private readonly Action<string[]>? _thrower;

    public RecordingGitRunner(
        Func<string[], ConfigRepoManager.GitRunResult>? policy = null,
        Action<string[]>? thrower = null)
    {
        _policy = policy;
        _thrower = thrower;
    }

    /// <summary>Every invocation, joined as <c>"arg arg arg"</c>, in call order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>The working directory of every invocation, in call order.</summary>
    public List<string> WorkingDirs { get; } = [];

    public Task<ConfigRepoManager.GitRunResult> RunAsync(string workingDir, string[] args, CancellationToken ct)
    {
        _ = ct;
        Commands.Add(string.Join(' ', args));
        WorkingDirs.Add(workingDir);

        _thrower?.Invoke(args);

        var result = _policy?.Invoke(args) ?? new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty);
        return Task.FromResult(result);
    }

    /// <summary>Index of the first command starting with <paramref name="prefix"/>, or -1.</summary>
    public int IndexOfPrefix(string prefix) =>
        Commands.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));

    public int CountPrefix(string prefix) =>
        Commands.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>
/// OAuth token-bridge coverage for <see cref="ConfigRepoManager"/>: the token chain, the
/// per-operation origin refresh timing rule, one-resolution-per-operation, and the redaction
/// contract. All of these drive the internal <c>GitRunner</c> seam, so no real git runs.
/// <para>
/// The environment credentials are CLEARED for the duration of these tests so the class models
/// an OAuth-only deployment deterministically; hence the <c>EnvVarMutation</c> collection.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoManagerTokenBridgeTests : IDisposable
{
    private const string RepoUrl = "https://github.com/org/config.git";
    private const string OAuthToken = "oauth-db-token-abc123";
    private static string TokenUrl(string token) => $"https://x-access-token:{token}@github.com/org/config.git";
    private static string SetUrl(string token) => $"remote set-url origin {TokenUrl(token)}";

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGithubToken;

    public ConfigRepoManagerTokenBridgeTests()
    {
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-tokenbridge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGithubToken);

        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Creates a manager whose local path already looks like a clone (has .git).</summary>
    private ConfigRepoManager CreateClonedManager(RecordingGitRunner runner)
    {
        var local = Path.Combine(_tempDir, "clone");
        Directory.CreateDirectory(Path.Combine(local, ".git"));
        return new ConfigRepoManager(RepoUrl, local) { GitRunner = runner.RunAsync };
    }

    /// <summary>Creates a manager whose local path does NOT exist yet (clone path).</summary>
    private ConfigRepoManager CreateUnclonedManager(RecordingGitRunner runner)
    {
        var local = Path.Combine(_tempDir, "fresh", "cfg");
        return new ConfigRepoManager(RepoUrl, local) { GitRunner = runner.RunAsync };
    }

    /// <summary>
    /// The commands that reach the network. Everything else executed by this type
    /// (<c>add</c>, <c>rm</c>, <c>diff</c>, <c>commit</c>, <c>reset</c>, <c>merge --abort</c>,
    /// <c>rebase --abort</c>, <c>config</c>, and <c>remote set-url</c> itself) is LOCAL.
    /// </summary>
    private static readonly string[] NetworkCommandPrefixes = ["pull", "push", "fetch", "clone"];

    /// <summary>
    /// Enforces THE TIMING RULE positionally: the <c>remote set-url</c> refresh must sit at the
    /// index IMMEDIATELY before the operation's FIRST NETWORK command.
    /// <para>
    /// This is removal-proof in BOTH directions. Moving the set-url EARLIER (e.g. back before a
    /// local <c>commit</c>) leaves a local command between it and the network command, and moving
    /// it LATER puts a network command ahead of it — either way the adjacency check fails.
    /// </para>
    /// </summary>
    /// <param name="runner">The recording seam whose command log is inspected.</param>
    /// <param name="expectedFirstNetworkCommand">The command expected to reach the network first.</param>
    private static void AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(
        RecordingGitRunner runner, string expectedFirstNetworkCommand)
    {
        var firstNetwork = runner.Commands.FindIndex(
            c => NetworkCommandPrefixes.Any(p => c == p || c.StartsWith(p + " ", StringComparison.Ordinal)));

        Assert.True(firstNetwork >= 0, "Expected the operation to run at least one network command.");
        Assert.Equal(expectedFirstNetworkCommand, runner.Commands[firstNetwork]);

        // Exactly ONE refresh, and it is the command directly before the first network command.
        Assert.Equal(1, runner.CountPrefix("remote set-url"));
        Assert.True(
            firstNetwork > 0,
            $"The first network command '{runner.Commands[firstNetwork]}' ran with no preceding set-url.");
        Assert.StartsWith("remote set-url", runner.Commands[firstNetwork - 1], StringComparison.Ordinal);
    }

    // ── §1 The token chain + failure semantics ────────────────────────────────

    [Fact]
    public async Task TokenResolver_ReturnsToken_TokenIsFirstCandidateAndInjectedIntoOrigin()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SetUrl(OAuthToken), runner.Commands[0]);
    }

    [Fact]
    public async Task TokenResolver_ReturnsNull_FallsThroughToEnvironmentChain()
    {
        // GH_TOKEN handling is asserted in the env-mutating companion class; here the point is
        // that a null resolver result must not short-circuit the chain into "no credential".
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        var resolved = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolved);
            return Task.FromResult<string?>(null);
        };

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, resolved);
        // No OAuth token was injected — the chain fell through.
        Assert.DoesNotContain(runner.Commands, c => c.Contains(OAuthToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenResolver_ThrowsNonCancellation_FallsThroughToEnvironmentChainAndOperationContinues()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => throw new InvalidOperationException("db unavailable");

        // The operation must complete — a broken OAuth bridge never fails the git operation.
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Contains("pull", runner.Commands);
        Assert.DoesNotContain(runner.Commands, c => c.Contains(OAuthToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenResolver_CallerCancellation_IsRethrownAndNoGitRuns()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        using var cts = new CancellationTokenSource();
        manager.TokenResolver = _ =>
        {
            // The CALLER's token becomes cancelled, then the resolver reports it.
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SyncRepoAsync(cts.Token));

        // The cancellation must abort the operation — no set-url, no pull.
        Assert.Empty(runner.Commands);
    }

    // ── §2 The origin-refresh timing rule ─────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_ExistingClone_SetsUrlImmediatelyBeforeThePull()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetUrl(OAuthToken), "pull"], runner.Commands);
    }

    [Fact]
    public async Task SyncRepoAsync_ClonePath_InjectsTokenIntoCloneUrlAndNormalizesOriginAfterTheClone()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateUnclonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        // The clone is the FIRST network command and carries the token-bearing URL. The clone
        // path is EXEMPT from the pre-network set-url: the set-url runs right AFTER the clone
        // and normalizes origin back to the sanitized URL.
        Assert.Equal(
            [
                $"clone {TokenUrl(OAuthToken)} cfg",
                $"remote set-url origin {RepoUrl}",
                "config user.email copilothive@local",
                "config user.name CopilotHive",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task SyncRepoAsync_ClonePath_NoCredential_StillNormalizesOriginViaTheSameCodePath()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateUnclonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(null);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, runner.IndexOfPrefix("clone "));
        Assert.Equal(1, runner.IndexOfPrefix("remote set-url"));
        Assert.Equal($"remote set-url origin {RepoUrl}", runner.Commands[1]);
    }

    [Fact]
    public async Task CommitFileAsync_NoDiff_SetsUrlAfterTheLocalDiffCheckAndBeforeThePush()
    {
        // diff --cached --quiet exits 0 → "no diff" → PushOnlyAsync.
        var runner = new RecordingGitRunner(args =>
            new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.CommitFileAsync("hive-config.yaml", "msg", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["add hive-config.yaml", "diff --cached --quiet", SetUrl(OAuthToken), "push"],
            runner.Commands);
    }

    [Fact]
    public async Task CommitFileAsync_WithDiff_SetsUrlAfterTheLocalCommitAndImmediatelyBeforeThePull()
    {
        // THE TIMING RULE on the changed-diff path: `git commit -m` is LOCAL, so the refresh
        // runs AFTER it and immediately before the `pull` — the path's FIRST NETWORK command.
        var runner = new RecordingGitRunner(args =>
            args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.CommitFileAsync("hive-config.yaml", "msg", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["add hive-config.yaml", "diff --cached --quiet", "commit -m msg", SetUrl(OAuthToken), "pull", "push"],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    [Fact]
    public async Task DeleteFileAsync_SetsUrlAfterTheLocalStagingAndBeforeTheFirstNetworkCommand()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.DeleteFileAsync("a.md", "msg", TestContext.Current.CancellationToken);

        // No diff → the PUSH is this path's first network command.
        Assert.Equal(
            ["rm --cached --ignore-unmatch a.md", "diff --cached --quiet", SetUrl(OAuthToken), "push"],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "push");
    }

    [Fact]
    public async Task DeleteFileAsync_WithDiff_SetsUrlAfterTheLocalCommitAndImmediatelyBeforeThePull()
    {
        var runner = new RecordingGitRunner(args =>
            args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.DeleteFileAsync("a.md", "msg", TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "rm --cached --ignore-unmatch a.md",
                "diff --cached --quiet",
                "commit -m msg",
                SetUrl(OAuthToken),
                "pull",
                "push",
            ],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    [Fact]
    public async Task DeleteFilesAsync_SetsUrlAfterTheLocalStagingAndBeforeTheFirstNetworkCommand()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.DeleteFilesAsync(["a.md", "b.md"], "msg", TestContext.Current.CancellationToken);

        // No diff → the PUSH is this path's first network command.
        Assert.Equal(
            ["rm --cached --ignore-unmatch a.md b.md", "diff --cached --quiet", SetUrl(OAuthToken), "push"],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "push");
    }

    [Fact]
    public async Task DeleteFilesAsync_WithDiff_SetsUrlAfterTheLocalCommitAndImmediatelyBeforeThePull()
    {
        var runner = new RecordingGitRunner(args =>
            args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.DeleteFilesAsync(["a.md", "b.md"], "msg", TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "rm --cached --ignore-unmatch a.md b.md",
                "diff --cached --quiet",
                "commit -m msg",
                SetUrl(OAuthToken),
                "pull",
                "push",
            ],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    [Fact]
    public async Task CommitFileAsync_NoDiff_SetsUrlImmediatelyBeforeThePushOnlyNetworkCommand()
    {
        // The no-diff path is UNAFFECTED by the commit-path relocation: with no commit to run,
        // the push itself is the first network command, so the refresh stays directly before it.
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.CommitFileAsync("hive-config.yaml", "msg", TestContext.Current.CancellationToken);

        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "push");
    }

    [Fact]
    public async Task ResetToRemoteAsync_SetsUrlAfterTheLocalMergeAbortAndImmediatelyBeforeTheFetch()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["merge --abort", SetUrl(OAuthToken), "fetch origin", "reset --hard origin/HEAD"],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "fetch origin");
    }

    [Fact]
    public async Task SyncRepoAsync_ExistingClone_SetUrlImmediatelyPrecedesTheFirstNetworkCommand()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    [Fact]
    public async Task CommitFileAsync_LocalCommitFails_NoCredentialIsResolvedOrPersistedToOrigin()
    {
        // The operation never reaches the network, so it must never have written a
        // credential-bearing origin: deferring the refresh until after the local commit means a
        // failed commit leaves .git/config untouched.
        var runner = new RecordingGitRunner(args => args switch
        {
            ["diff", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty),
            ["commit", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, "nothing to commit"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.CommitFileAsync("hive-config.yaml", "msg", TestContext.Current.CancellationToken));

        Assert.Equal(0, resolutions);
        Assert.Equal(0, runner.CountPrefix("remote set-url"));
        Assert.Equal(["add hive-config.yaml", "diff --cached --quiet", "commit -m msg"], runner.Commands);
    }

    [Fact]
    public async Task CommitAllChangesAsync_LocalCommitFails_NoCredentialIsResolvedOrPersistedToOrigin()
    {
        var runner = new RecordingGitRunner(args => args switch
        {
            ["diff", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty),
            ["commit", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, "commit failed"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.CommitAllChangesAsync("msg", TestContext.Current.CancellationToken));

        Assert.Equal(0, resolutions);
        Assert.Equal(0, runner.CountPrefix("remote set-url"));
        Assert.Equal(["add --all", "diff --cached --quiet", "commit -m msg"], runner.Commands);
    }

    // ── §2 No-network early exits: NO resolution, NO set-url ──────────────────

    [Fact]
    public async Task DeleteFilesAsync_EmptyList_PerformsNoResolutionAndNoGitAtAll()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await manager.DeleteFilesAsync([], "msg", TestContext.Current.CancellationToken);

        Assert.Equal(0, resolutions);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task CommitAllChangesAsync_NoDiffReturn_PerformsNoResolutionAndNoSetUrl()
    {
        // Everything exits 0 → diff --cached --quiet reports "no diff" → early return.
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await manager.CommitAllChangesAsync("msg", TestContext.Current.CancellationToken);

        Assert.Equal(0, resolutions);
        Assert.Equal(["add --all", "diff --cached --quiet"], runner.Commands);
    }

    [Fact]
    public async Task CommitAllChangesAsync_WithDiff_ResolvesOnceAndSetsUrlAfterTheCommitBeforeThePull()
    {
        var runner = new RecordingGitRunner(args =>
            args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await manager.CommitAllChangesAsync("msg", TestContext.Current.CancellationToken);

        Assert.Equal(1, resolutions);
        Assert.Equal(
            ["add --all", "diff --cached --quiet", "commit -m msg", SetUrl(OAuthToken), "pull", "push"],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    // ── §2 One resolution per operation, conflict recovery included ───────────

    [Fact]
    public async Task CommitFileAsync_ConflictRecoveryPath_ResolvesExactlyOnceAndReusesTheCredential()
    {
        // diff → 1 (there IS a diff); pull and pull --rebase both fail → full recovery.
        var runner = new RecordingGitRunner(args => args switch
        {
            ["diff", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty),
            ["pull"] => new ConfigRepoManager.GitRunResult(1, string.Empty, "merge conflict"),
            ["pull", "--rebase"] => new ConfigRepoManager.GitRunResult(1, string.Empty, "rebase conflict"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await manager.CommitFileAsync("hive-config.yaml", "msg", TestContext.Current.CancellationToken);

        // EXACTLY one resolution and EXACTLY one set-url, even though the recovery path ran.
        Assert.Equal(1, resolutions);
        Assert.Equal(1, runner.CountPrefix("remote set-url"));
        Assert.Equal(
            [
                "add hive-config.yaml",
                "diff --cached --quiet",
                "commit -m msg",
                SetUrl(OAuthToken),
                "pull",
                "merge --abort",
                "reset --hard HEAD",
                "pull --rebase",
                "rebase --abort",
                "reset --hard HEAD",
                "push",
            ],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    [Fact]
    public async Task DeleteFileAsync_ConflictRecoveryPath_RefreshesBeforeNetworkAndResolvesExactlyOnce()
    {
        // diff → 1 (there IS a deletion); pull and pull --rebase both fail → full recovery.
        var runner = new RecordingGitRunner(args => args switch
        {
            ["diff", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty),
            ["pull"] => new ConfigRepoManager.GitRunResult(1, string.Empty, "merge conflict"),
            ["pull", "--rebase"] => new ConfigRepoManager.GitRunResult(1, string.Empty, "rebase conflict"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await manager.DeleteFileAsync("a.md", "remove a", TestContext.Current.CancellationToken);

        Assert.Equal(1, resolutions);
        Assert.Equal(1, runner.CountPrefix("remote set-url"));
        Assert.Equal(
            [
                "rm --cached --ignore-unmatch a.md",
                "diff --cached --quiet",
                "commit -m remove a",
                SetUrl(OAuthToken),
                "pull",
                "merge --abort",
                "reset --hard HEAD",
                "pull --rebase",
                "rebase --abort",
                "reset --hard HEAD",
                "push",
            ],
            runner.Commands);
        AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "pull");
    }

    [Fact]
    public async Task CredentialUsingPublicOperations_EachResolveExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var operations = new (string Name, bool ClonePath, Func<ConfigRepoManager, Task> Run)[]
        {
            ("sync pull", false, manager => manager.SyncRepoAsync(ct)),
            ("sync clone", true, manager => manager.SyncRepoAsync(ct)),
            ("commit file", false, manager => manager.CommitFileAsync("a.md", "commit", ct)),
            ("delete file", false, manager => manager.DeleteFileAsync("a.md", "delete", ct)),
            ("delete files", false, manager => manager.DeleteFilesAsync(["a.md", "b.md"], "delete batch", ct)),
            ("commit all", false, manager => manager.CommitAllChangesAsync("commit all", ct)),
            ("reset", false, manager => manager.ResetToRemoteAsync(ct)),
        };

        foreach (var operation in operations)
        {
            // A diff exit of 1 drives commit operations down their credential-using path.
            var runner = new RecordingGitRunner(args =>
                args is ["diff", ..]
                    ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                    : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty));
            var manager = operation.ClonePath
                ? CreateUnclonedManager(runner)
                : CreateClonedManager(runner);
            var resolutions = 0;
            manager.TokenResolver = _ =>
            {
                Interlocked.Increment(ref resolutions);
                return Task.FromResult<string?>(OAuthToken);
            };

            await operation.Run(manager);

            Assert.True(
                resolutions == 1,
                $"{operation.Name} resolved the credential {resolutions} times instead of exactly once.");
        }
    }

    [Fact]
    public async Task NoDiffPushPublicOperations_EachResolveExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var operations = new (string Name, Func<ConfigRepoManager, Task> Run)[]
        {
            ("commit file no-diff", manager => manager.CommitFileAsync("a.md", "commit", ct)),
            ("delete file no-diff", manager => manager.DeleteFileAsync("a.md", "delete", ct)),
            ("delete files no-diff", manager => manager.DeleteFilesAsync(["a.md", "b.md"], "delete batch", ct)),
        };

        foreach (var operation in operations)
        {
            // Exit 0 from diff --cached --quiet selects the push-only branch.
            var runner = new RecordingGitRunner();
            var manager = CreateClonedManager(runner);
            var resolutions = 0;
            manager.TokenResolver = _ =>
            {
                Interlocked.Increment(ref resolutions);
                return Task.FromResult<string?>(OAuthToken);
            };

            await operation.Run(manager);

            Assert.True(
                resolutions == 1,
                $"{operation.Name} resolved the credential {resolutions} times instead of exactly once.");
            Assert.Equal(1, runner.CountPrefix("remote set-url"));
            AssertSetUrlImmediatelyPrecedesFirstNetworkCommand(runner, "push");
        }
    }

    [Fact]
    public async Task SyncRepoAsync_PullFailsAndMergeAbortRuns_ResolvesExactlyOnce()
    {
        var runner = new RecordingGitRunner(args => args switch
        {
            ["pull"] => new ConfigRepoManager.GitRunResult(1, string.Empty, "boom"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        var resolutions = 0;
        manager.TokenResolver = _ =>
        {
            Interlocked.Increment(ref resolutions);
            return Task.FromResult<string?>(OAuthToken);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, resolutions);
        Assert.Equal([SetUrl(OAuthToken), "pull", "merge --abort"], runner.Commands);
    }

    [Fact]
    public async Task ConcurrentOperations_EachResolveExactlyOnce()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        var resolutions = new ConcurrentBag<int>();
        manager.TokenResolver = _ =>
        {
            resolutions.Add(1);
            return Task.FromResult<string?>(OAuthToken);
        };

        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            manager.ResetToRemoteAsync(ct),
            manager.ResetToRemoteAsync(ct),
            manager.ResetToRemoteAsync(ct));

        Assert.Equal(3, resolutions.Count);
    }

    // ── §3 The redaction contract ─────────────────────────────────────────────

    [Fact]
    public async Task NonZeroExit_TokenBearingUrlInOutput_IsRedactedFromTheExceptionMessage()
    {
        var runner = new RecordingGitRunner(args => args switch
        {
            ["pull"] => new ConfigRepoManager.GitRunResult(
                128,
                string.Empty,
                $"fatal: could not read from '{TokenUrl(OAuthToken)}'"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

        Assert.Contains("git exited with code 128", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthToken, ex.ToString(), StringComparison.Ordinal);
        // The URL survives, minus its userinfo.
        Assert.Contains("https://github.com/org/config.git", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroExit_BareTokenInOutput_IsReplacedByTheLiteralRedactionPass()
    {
        // A BARE token — no URL around it, so no URL scanner would ever see it. Only the
        // ordinal credential → "[redacted]" literal pass can catch this.
        var runner = new RecordingGitRunner(args => args switch
        {
            ["pull"] => new ConfigRepoManager.GitRunResult(
                128, string.Empty, $"fatal: authentication failed for token {OAuthToken} (expired)"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(OAuthToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthToken, ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunGitOptional_ExitGreaterThanOne_IsRedactedToo()
    {
        var runner = new RecordingGitRunner(args => args switch
        {
            ["diff", ..] => new ConfigRepoManager.GitRunResult(
                129, string.Empty, $"fatal: {OAuthToken} at {TokenUrl(OAuthToken)}"),
            _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
        });
        var manager = CreateClonedManager(runner);
        // The diff check is LOCAL and runs before any resolution, so no credential is known
        // there — the URL scanner must still strip the userinfo.
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.CommitFileAsync("hive-config.yaml", "msg", TestContext.Current.CancellationToken));

        Assert.Contains("git exited with code 129", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/org/config.git", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingSeam_NonCancellation_IsWrappedWithNoInnerExceptionAndTheOriginalTypeName()
    {
        var runner = new RecordingGitRunner(thrower: args =>
        {
            if (args is ["pull"])
                throw new TimeoutException($"timed out talking to {TokenUrl(OAuthToken)} using {OAuthToken}");
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

        // The wrapper carries NO unsanitized inner exception…
        Assert.Null(ex.InnerException);
        // …keeps the ORIGINAL exception type name as text…
        Assert.Contains(nameof(TimeoutException), ex.Message, StringComparison.Ordinal);
        // …and BOTH Message and ToString() are credential-free.
        Assert.DoesNotContain(OAuthToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthToken, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingSeam_UnrelatedOperationCanceled_TokenNotCancelled_IsWrappedAndRedacted()
    {
        var runner = new RecordingGitRunner(thrower: args =>
        {
            if (args is ["pull"])
                throw new OperationCanceledException($"inner timeout for {TokenUrl(OAuthToken)} / {OAuthToken}");
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

        Assert.Null(ex.InnerException);
        Assert.Contains(nameof(OperationCanceledException), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OAuthToken, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingSeam_CallerCancellation_PropagatesUnwrappedAndUnredacted()
    {
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the sync";
        var runner = new RecordingGitRunner(thrower: args =>
        {
            if (args is ["pull"])
            {
                cts.Cancel();
                throw new OperationCanceledException(Marker, cts.Token);
            }
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SyncRepoAsync(cts.Token));

        // Propagated UNWRAPPED and UNREDACTED — its message carries no git output by construction.
        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitFileAsync_PullCallerCancellation_PropagatesWithoutRecoveryOrPush()
    {
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the commit pull";
        var runner = new RecordingGitRunner(
            policy: args => args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
            thrower: args =>
            {
                if (args is ["pull"])
                {
                    cts.Cancel();
                    throw new OperationCanceledException(Marker, cts.Token);
                }
            });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CommitFileAsync("a.md", "commit", cts.Token));

        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["add a.md", "diff --cached --quiet", "commit -m commit", SetUrl(OAuthToken), "pull"],
            runner.Commands);
    }

    [Fact]
    public async Task CommitFileAsync_RebaseCallerCancellation_PropagatesWithoutAbortResetOrPush()
    {
        // The plain pull fails as a genuine conflict → recovery starts → the CALLER cancels
        // during `pull --rebase`. The nested catch must NOT classify that as a rebase conflict.
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the rebase";
        var runner = new RecordingGitRunner(
            policy: args => args switch
            {
                ["diff", ..] => new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty),
                ["pull"] => new ConfigRepoManager.GitRunResult(1, string.Empty, "merge conflict"),
                _ => new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
            },
            thrower: args =>
            {
                if (args is ["pull", "--rebase"])
                {
                    cts.Cancel();
                    throw new OperationCanceledException(Marker, cts.Token);
                }
            });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CommitFileAsync("a.md", "commit", cts.Token));

        // Propagated UNREDACTED and UNWRAPPED…
        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        // …and NOTHING ran after the cancelled `pull --rebase`: no rebase --abort, no reset,
        // no push.
        Assert.Equal(
            [
                "add a.md",
                "diff --cached --quiet",
                "commit -m commit",
                SetUrl(OAuthToken),
                "pull",
                "merge --abort",
                "reset --hard HEAD",
                "pull --rebase",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task SyncRepoAsync_PullCallerCancellation_PropagatesWithoutTheMergeAbort()
    {
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the sync pull";
        var runner = new RecordingGitRunner(thrower: args =>
        {
            if (args is ["pull"])
            {
                cts.Cancel();
                throw new OperationCanceledException(Marker, cts.Token);
            }
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SyncRepoAsync(cts.Token));

        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        // A cancelled pull runs NO cleanup command — `merge --abort` must not appear.
        Assert.Equal([SetUrl(OAuthToken), "pull"], runner.Commands);
    }

    [Fact]
    public async Task DeleteFileAsync_PullCallerCancellation_PropagatesWithoutRecoveryOrPush()
    {
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the delete pull";
        var runner = new RecordingGitRunner(
            policy: args => args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
            thrower: args =>
            {
                if (args is ["pull"])
                {
                    cts.Cancel();
                    throw new OperationCanceledException(Marker, cts.Token);
                }
            });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.DeleteFileAsync("a.md", "delete", cts.Token));

        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            [
                "rm --cached --ignore-unmatch a.md",
                "diff --cached --quiet",
                "commit -m delete",
                SetUrl(OAuthToken),
                "pull",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task DeleteFilesAsync_PullCallerCancellation_PropagatesWithoutRecoveryOrPush()
    {
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the batch delete pull";
        var runner = new RecordingGitRunner(
            policy: args => args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
            thrower: args =>
            {
                if (args is ["pull"])
                {
                    cts.Cancel();
                    throw new OperationCanceledException(Marker, cts.Token);
                }
            });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.DeleteFilesAsync(["a.md", "b.md"], "delete batch", cts.Token));

        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            [
                "rm --cached --ignore-unmatch a.md b.md",
                "diff --cached --quiet",
                "commit -m delete batch",
                SetUrl(OAuthToken),
                "pull",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task CommitAllChangesAsync_PullCallerCancellation_PropagatesWithoutRecoveryOrPush()
    {
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the commit-all pull";
        var runner = new RecordingGitRunner(
            policy: args => args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
            thrower: args =>
            {
                if (args is ["pull"])
                {
                    cts.Cancel();
                    throw new OperationCanceledException(Marker, cts.Token);
                }
            });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CommitAllChangesAsync("commit all", cts.Token));

        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["add --all", "diff --cached --quiet", "commit -m commit all", SetUrl(OAuthToken), "pull"],
            runner.Commands);
    }

    [Fact]
    public async Task ResetToRemoteAsync_MergeAbortCallerCancellation_PropagatesInsteadOfBeingSwallowed()
    {
        // TryAbortMergeAsync swallows best-effort failures — but a CALLER cancellation must
        // NOT be swallowed, otherwise a cancelled operation would carry on to the fetch/reset.
        using var cts = new CancellationTokenSource();
        const string Marker = "caller cancelled the merge abort";
        var runner = new RecordingGitRunner(thrower: args =>
        {
            if (args is ["merge", "--abort"])
            {
                cts.Cancel();
                throw new OperationCanceledException(Marker, cts.Token);
            }
        });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ResetToRemoteAsync(cts.Token));

        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        // No set-url, no fetch, no reset ran after the cancellation.
        Assert.Equal(["merge --abort"], runner.Commands);
    }

    [Fact]
    public async Task PushWithConflictRecovery_NonCancellationOperationCanceled_StillRunsRecovery()
    {
        // The exemption is precise: an OCE whose token is NOT cancelled is an ordinary failure
        // (it is wrapped by the runner), so the conflict recovery must still run.
        var runner = new RecordingGitRunner(
            policy: args => args is ["diff", ..]
                ? new ConfigRepoManager.GitRunResult(1, string.Empty, string.Empty)
                : new ConfigRepoManager.GitRunResult(0, string.Empty, string.Empty),
            thrower: args =>
            {
                if (args is ["pull"])
                    throw new OperationCanceledException("unrelated inner timeout");
            });
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(OAuthToken);

        await manager.CommitFileAsync("a.md", "commit", TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "add a.md",
                "diff --cached --quiet",
                "commit -m commit",
                SetUrl(OAuthToken),
                "pull",
                "merge --abort",
                "reset --hard HEAD",
                "pull --rebase",
                "push",
            ],
            runner.Commands);
    }
}

/// <summary>
/// The environment side of the credential chain: <c>GH_TOKEN</c> then <c>GITHUB_TOKEN</c>,
/// used whenever the OAuth bridge is absent, returns <c>null</c>, or fails.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoManagerEnvChainTests : IDisposable
{
    private const string RepoUrl = "https://github.com/org/config.git";

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGithubToken;

    public ConfigRepoManagerEnvChainTests()
    {
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-envchain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGithubToken);

        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private ConfigRepoManager CreateClonedManager(RecordingGitRunner runner)
    {
        var local = Path.Combine(_tempDir, "clone");
        Directory.CreateDirectory(Path.Combine(local, ".git"));
        return new ConfigRepoManager(RepoUrl, local) { GitRunner = runner.RunAsync };
    }

    private static string SetUrl(string token) =>
        $"remote set-url origin https://x-access-token:{token}@github.com/org/config.git";

    [Fact]
    public async Task NoTokenResolver_UsesGhTokenFromTheEnvironment()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);

        // TokenResolver is null → env-only chain.
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetUrl("env-gh-token"), "pull"], runner.Commands);
    }

    [Fact]
    public async Task NoTokenResolver_GhTokenAbsent_FallsBackToGithubToken()
    {
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-github-token");
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetUrl("env-github-token"), "pull"], runner.Commands);
    }

    [Fact]
    public async Task TokenResolverReturnsNull_FallsThroughToGhToken()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>(null);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetUrl("env-gh-token"), "pull"], runner.Commands);
    }

    [Fact]
    public async Task TokenResolverThrowsNonCancellation_FallsThroughToGhToken()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => throw new InvalidOperationException("db down");

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetUrl("env-gh-token"), "pull"], runner.Commands);
    }

    [Fact]
    public async Task TokenResolverWins_OverBothEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "env-gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "env-github-token");
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>("oauth-wins");

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetUrl("oauth-wins"), "pull"], runner.Commands);
    }

    [Fact]
    public async Task WhitespaceCandidates_AreTreatedAsAbsent_AndTheSelectedTokenIsNotTrimmed()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "   ");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", " padded-token ");
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);
        manager.TokenResolver = _ => Task.FromResult<string?>("\t");

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        // GitCredentialResolver returns the selected candidate UNCHANGED — never trimmed.
        Assert.Equal([SetUrl(" padded-token "), "pull"], runner.Commands);
    }

    [Fact]
    public async Task NoCredentialAnywhere_PerformsNoSetUrl_LeavingAnyPersistedOriginUnchanged()
    {
        // THE STALE-ORIGIN RULE: a null resolution must not strip a persisted credential.
        var runner = new RecordingGitRunner();
        var manager = CreateClonedManager(runner);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["pull"], runner.Commands);
    }
}
