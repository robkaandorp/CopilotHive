using System.Diagnostics;
using CopilotHive.Configuration;
using CopilotHive.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// The <see cref="ConfigRepoManager"/> half of the operation-scoped askpass lifecycle contract:
/// the helper DIRECTORY an authenticated operation created must not outlive that operation, and
/// two concurrent operations must never share a helper or a credential.
/// <para>
/// The Brain side of this contract is covered by
/// <c>BrainRepoManagerTransportContractTests</c> (SuccessfulOperation_DeletesItsHelperDirectory,
/// FailedOperation_..., ThrowingOperation_..., CancelledOperation_... and
/// ConcurrentOperations_EachGetTheirOwnHelperAndCredential). The ConfigRepoManager had NO
/// equivalent assertion, so nothing detected a leaked temporary directory there.
/// </para>
/// <para>
/// Every test drives the manager WITHOUT the <c>GitRunner</c> seam, so the production launch path
/// — and the production disposal in its <c>finally</c> — is what runs.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoManagerAskpassLifecycleTests : IDisposable
{
    /// <summary>The credential the host resolves for the first operation.</summary>
    internal const string Token = "gho_config_lifecycle_token";

    /// <summary>
    /// The credential the host resolves for the second, concurrent operation. It is deliberately
    /// NOT a superstring of <see cref="Token"/>, so a substring check cannot confuse the two.
    /// </summary>
    internal const string OtherToken = "gho_second_operation_credential";

    /// <summary>The eligible, configured URL of the config repository.</summary>
    internal const string EligibleUrl = "https://github.com/org/config.git";

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGitHubToken;
    private readonly string? _originalGitConfigGlobal;

    public ConfigRepoManagerAskpassLifecycleTests()
    {
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _originalGitConfigGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", null);

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-config-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGitHubToken);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", _originalGitConfigGlobal);

        TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    /// <summary>
    /// Records, per REAL launch, the askpass helper path and the child environment the launch
    /// would use. The observer runs inside the manager, before the launch, and sees exactly the
    /// environment the production runner installs on the child.
    /// </summary>
    private sealed class LaunchObserver
    {
        private readonly List<IReadOnlyDictionary<string, string?>?> _environments = [];
        private readonly List<string> _askpassPaths = [];

        /// <summary>
        /// The DISTINCT helper scripts the recorded launches pointed at — one per OPERATION, since
        /// every launch of one operation shares that operation's helper.
        /// </summary>
        public List<string> DistinctAskpassPaths()
        {
            lock (_environments)
                return [.. _askpassPaths.Distinct(StringComparer.Ordinal)];
        }

        /// <summary>The askpass helper path of every launch that carried one, in call order.</summary>
        public List<string> AskpassPaths
        {
            get { lock (_environments) return [.. _askpassPaths]; }
        }

        /// <summary>The child environment of every launch, in call order.</summary>
        public List<IReadOnlyDictionary<string, string?>?> Environments
        {
            get { lock (_environments) return [.. _environments]; }
        }

        /// <summary>The DISTINCT credentials the recorded child environments carried.</summary>
        public List<string> DistinctCredentials() =>
            Environments
                .Where(e => e is not null)
                .Select(e => e![HostGitAskpassHelper.CredentialEnvName])
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// The concatenation of every recorded child environment that carried
        /// <paramref name="credential"/>, used to prove one operation's credential never reached
        /// the other operation's child.
        /// </summary>
        public string EnvironmentsCarrying(string credential) =>
            string.Join('|', Environments
                .Where(e => e is not null
                    && e.TryGetValue(HostGitAskpassHelper.CredentialEnvName, out var v)
                    && v == credential)
                .Select(e => string.Join('|', e!.Values)));

        /// <summary>
        /// The content of the helper script as it was AT LAUNCH TIME — captured while the
        /// operation still held the file, so the observation is of the LIVE bytes.
        /// </summary>
        public string ScriptContentAtLaunch { get; private set; } = string.Empty;

        public void Observe(string[] argv, IReadOnlyDictionary<string, string?>? env)
        {
            lock (_environments)
            {
                _environments.Add(env);
                if (env is null)
                    return;

                var path = env[HostGitAskpassHelper.AskpassEnvName];
                if (path is null)
                    return;

                _askpassPaths.Add(path);
                try
                {
                    if (File.Exists(path))
                        ScriptContentAtLaunch = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    // The helper was already disposed; the constant-based assertion covers it.
                }
            }
        }
    }

    /// <summary>
    /// Builds a manager over a REAL git clone, wired to the real process runner.
    /// </summary>
    /// <remarks>
    /// The clone's history is the REMOTE's own, so the pull is a deterministic fast-forward and
    /// the fixture never depends on wall-clock timing. (Two commits with the same tree, message
    /// and identity created in the SAME SECOND get the same SHA, which would make a later pull
    /// either a fast-forward or a divergent merge depending on the clock.)
    /// </remarks>
    private ConfigRepoManager CreateManager(
        LaunchObserver observer, string? token = Token, string name = "clone", string? remoteDir = null)
    {
        string local;
        if (remoteDir is null)
        {
            local = Path.Combine(_tempDir, name);
            Directory.CreateDirectory(local);
            Git(local, "init", "-b", "main");
            Git(local, "config", "user.email", "probe@test.invalid");
            Git(local, "config", "user.name", "Probe");
            File.WriteAllText(Path.Combine(local, "hive-config.yaml"), "version: local-only\n");
            Git(local, "add", "hive-config.yaml");
            Git(local, "commit", "-m", "Local seed commit");
            Git(local, "remote", "add", "origin", EligibleUrl);
        }
        else
        {
            // `git clone` refuses to clone into a directory that already exists, so the
            // destination is created BY the clone inside a dedicated parent directory.
            var cloneParent = Path.Combine(_tempDir, name + "-parent");
            Directory.CreateDirectory(cloneParent);
            Git(cloneParent, "clone", remoteDir, name);
            local = Path.Combine(cloneParent, name);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "iter4-dbg.txt"),
                "REFS=" + string.Join(",", Directory.GetFiles(Path.Combine(local, ".git", "refs"), "*", SearchOption.AllDirectories))
                + "\nPACKED=" + (File.Exists(Path.Combine(local, ".git", "packed-refs")) ? File.ReadAllText(Path.Combine(local, ".git", "packed-refs")) : "none")
                + "\nCFG=" + File.ReadAllText(Path.Combine(local, ".git", "config")));
            Assert.True(
                File.Exists(Path.Combine(local, ".git", "config")),
                $"The fixture clone did not create a repository at '{local}'.");
        }

        Git(local, "remote", "set-url", "origin", EligibleUrl);
        Git(local, "config", "branch.main.remote", "origin");
        Git(local, "config", "branch.main.merge", "refs/heads/main");

        var manager = new ConfigRepoManager(EligibleUrl, local)
        {
            TokenResolver = _ => Task.FromResult(token),
        };
        manager.GitLaunchObserver = (_, argv, env) => observer.Observe(argv, env);
        return manager;
    }

    /// <summary>
    /// A SUCCESSFUL operation — one whose network command really succeeds — deletes its helper
    /// directory. The success is proven by the fixture's own remote-tracking ref being refreshed,
    /// so the cleanup assertion cannot pass because nothing ran.
    /// </summary>
    [Fact]
    public async Task SuccessfulOperation_AgainstALocalRemote_DeletesItsHelperDirectory()
    {
        var observer = new LaunchObserver();
        var remoteDir = CreateLocalRemote();

        // The clone is made FROM the remote, so its history is the remote's own and the pull is a
        // deterministic fast-forward (never a timing-dependent merge).
        var manager = CreateManager(observer, remoteDir: remoteDir);

        // The clone's origin is redirected to the LOCAL remote by git's own URL rewriting, so the
        // pull is a REAL, SUCCESSFUL network command with no credential needed.
        SetGlobalGitConfig($"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        // PREMISE: the origin really is the eligible URL (git rewrites it to the local remote for
        // the network call, but the PERSISTED origin is the eligible URL).
        Assert.Equal(EligibleUrl, Git(manager.LocalPath, "config", "--get", "remote.origin.url").Trim());
        Assert.Equal(
            Git(remoteDir, "rev-parse", "main").Trim(),
            Git(manager.LocalPath, "rev-parse", "HEAD").Trim());

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        // PREMISE: the pull really ran and really succeeded — the remote-tracking ref exists.
        Assert.Contains(
            "origin/main",
            Git(manager.LocalPath, "for-each-ref", "--format=%(refname:short)", "refs/remotes/origin/"),
            StringComparison.Ordinal);

        var scriptPath = Assert.Single(observer.DistinctAskpassPaths());
        Assert.False(File.Exists(scriptPath), "The helper script outlived the successful operation.");
        Assert.False(
            Directory.Exists(Path.GetDirectoryName(scriptPath)),
            "The helper directory outlived the successful operation.");
    }

    /// <summary>
    /// A FAILED operation also deletes its helper directory: a failed operation leaks no
    /// temporary directory either.
    /// </summary>
    [Fact]
    public async Task FailedOperation_DeletesItsHelperDirectory()
    {
        var observer = new LaunchObserver();
        var manager = CreateManager(observer);

        // The closed proxy port makes the pull fail immediately and offline.
        SetGlobalGitConfig($"[http \"{EligibleUrl}\"]\n\tproxy = http://127.0.0.1:1\n");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        var scriptPath = Assert.Single(observer.DistinctAskpassPaths());
        Assert.False(File.Exists(scriptPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scriptPath)));
    }

    /// <summary>
    /// A CANCELLED operation deletes the helper directory it had already created. The
    /// cancellation is injected from the launch observer — i.e. AFTER the credential was resolved
    /// and the operation-scoped helper existed — so the disposal really had to run.
    /// </summary>
    [Fact]
    public async Task CancelledOperation_DeletesItsHelperDirectory()
    {
        var observer = new LaunchObserver();
        using var cts = new CancellationTokenSource();

        var manager = new ConfigRepoManager(EligibleUrl, Path.Combine(_tempDir, "clone-c"))
        {
            TokenResolver = _ => Task.FromResult<string?>(Token),
        };
        manager.GitLaunchObserver = (_, argv, env) =>
        {
            observer.Observe(argv, env);

            // Cancel as soon as an AUTHENTICATED launch is about to happen: the helper exists
            // from here on, so its disposal is observable.
            if (env is not null && env.ContainsKey(HostGitAskpassHelper.CredentialEnvName))
                cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SyncRepoAsync(cts.Token));

        // PREMISE: the operation really did create a helper (the credential was resolved and the
        // helper was built before the launch).
        var scriptPath = Assert.Single(observer.DistinctAskpassPaths());

        // The helper the cancelled operation created is gone.
        Assert.False(File.Exists(scriptPath), "The helper script outlived the cancelled operation.");
        Assert.False(
            Directory.Exists(Path.GetDirectoryName(scriptPath)),
            "The helper directory outlived the cancelled operation.");
    }

    /// <summary>
    /// Two CONCURRENT authenticated operations each get their OWN helper directory and their OWN
    /// credential: neither launch shares the other's helper script or token, so one operation can
    /// never authenticate with the other's credential.
    /// </summary>
    /// <remarks>
    /// The two operations are driven concurrently and the seam is not used, so the REAL production
    /// launch path installs each operation's own environment on its own child.
    /// </remarks>
    [Fact]
    public async Task ConcurrentOperations_EachGetTheirOwnHelperAndCredential()
    {
        var observer = new LaunchObserver();
        var remoteDir = CreateLocalRemote();
        SetGlobalGitConfig($"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var managerA = CreateManager(observer, Token, "clone-a", remoteDir);
        var managerB = CreateManager(observer, OtherToken, "clone-b", remoteDir);

        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(managerA.SyncRepoAsync(ct), managerB.SyncRepoAsync(ct));

        // Two operations, two DISTINCT helper scripts (each operation's several launches share
        // that operation's ONE helper).
        var paths = observer.DistinctAskpassPaths();
        Assert.Equal(2, paths.Count);
        Assert.NotEqual(paths[0], paths[1]);

        // Each operation's child environment carried its OWN credential…
        var credentials = observer.DistinctCredentials();
        Assert.Contains(Token, credentials);
        Assert.Contains(OtherToken, credentials);

        // …and NEITHER operation's credential reached the other operation's child.
        Assert.DoesNotContain(
            OtherToken, observer.EnvironmentsCarrying(Token), StringComparison.Ordinal);
        Assert.DoesNotContain(
            Token, observer.EnvironmentsCarrying(OtherToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper SCRIPT of a concurrent operation is token-free: the credential travels only
    /// through the child environment, never through the file the helper executes.
    /// </summary>
    /// <remarks>
    /// The scripts are captured while the operation still holds them (the observer records the
    /// path at launch time and the content is read immediately), so this observes the LIVE content
    /// rather than a file that has already been deleted.
    /// </remarks>
    [Fact]
    public async Task ConcurrentOperations_EachHelperScriptIsTokenFree()
    {
        var observer = new LaunchObserver();
        var remoteDir = CreateLocalRemote();
        SetGlobalGitConfig($"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var managerA = CreateManager(observer, Token, "clone-a", remoteDir);
        var managerB = CreateManager(observer, OtherToken, "clone-b", remoteDir);

        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(managerA.SyncRepoAsync(ct), managerB.SyncRepoAsync(ct));

        // The scripts were DELETED when the operations ended (the lifecycle contract), so the
        // token-free content is asserted from the production script constant that produced them.
        Assert.Equal(HostGitAskpassHelper.ScriptContent, observer.ScriptContentAtLaunch);
        Assert.DoesNotContain(Token, HostGitAskpassHelper.ScriptContent, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherToken, HostGitAskpassHelper.ScriptContent, StringComparison.Ordinal);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>Writes the process-wide git configuration the child git processes will read.</summary>
    private void SetGlobalGitConfig(string content)
    {
        var path = Path.Combine(_tempDir, "gitconfig");
        File.WriteAllText(path, content);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", path);
    }

    /// <summary>Creates a bare remote carrying one commit on <c>main</c>.</summary>
    private string CreateLocalRemote()
    {
        var remoteDir = Path.Combine(_tempDir, "remote.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare", "-b", "main");

        var seed = Path.Combine(_tempDir, "seed");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.email", "probe@test.invalid");
        Git(seed, "config", "user.name", "Probe");
        File.WriteAllText(Path.Combine(seed, "hive-config.yaml"), "version: 1\n");
        Git(seed, "add", "hive-config.yaml");
        Git(seed, "commit", "-m", "Seed commit");
        Git(seed, "remote", "add", "origin", remoteDir);
        Git(seed, "push", "origin", "main");

        return remoteDir;
    }

    /// <summary>Runs a real <c>git</c> and returns its standard output.</summary>
    private static string Git(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Fixture git '{string.Join(' ', args)}' failed: {stderr}");
        return stdout;
    }
}
