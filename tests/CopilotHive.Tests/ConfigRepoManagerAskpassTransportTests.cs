using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CopilotHive.Configuration;
using CopilotHive.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// The REAL-PROCESS half of the <see cref="ConfigRepoManager"/> credential-transport contract: the
/// claims that can only be proven against an actual <c>git</c> process and the actual files it
/// leaves on disk.
/// <para>
/// Every test here drives the manager WITHOUT the <c>GitRunner</c> seam, so the production process
/// runner — including the operation environment it installs on the child — is what runs. The
/// assertions are made against
/// <list type="bullet">
///   <item><description>the REAL <c>.git/config</c> the clone/pull/refresh left on disk,</description></item>
///   <item><description>the REAL credential-helper invocations a real <c>git</c> made (a recording
///   helper installed in real git configuration), and</description></item>
///   <item><description>the REAL child environment, observed from inside the child process
///   itself.</description></item>
/// </list>
/// The seam-based half of the contract lives in
/// <see cref="ConfigRepoManagerEligibilityTests"/> and <see cref="ConfigRepoManagerEnvChainTests"/>.
/// </para>
/// <para>
/// The class serializes on <c>EnvVarMutation</c> because it mutates the process-wide environment
/// (<c>GIT_CONFIG_GLOBAL</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>): the child environment is
/// built from the parent environment, so the git configuration a test wants its child to see can
/// only be installed that way.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoManagerAskpassTransportTests : IDisposable
{
    /// <summary>The credential the host resolves for the operation.</summary>
    internal const string Token = "gho_config_repo_transport_token";

    /// <summary>
    /// A credential ALREADY PERSISTED in the clone's origin before the operation — the stale
    /// tokenized origin the sanitized contract must strip (or, with no credential resolved,
    /// honestly leave alone).
    /// </summary>
    internal const string StalePersistedToken = "gho_config_repo_stale_token";

    /// <summary>The eligible, configured URL of the config repository.</summary>
    internal const string EligibleUrl = "https://github.com/org/config.git";

    /// <summary>The SANITIZED origin the manager must persist.</summary>
    internal const string SanitizedOrigin = EligibleUrl;

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGitHubToken;
    private readonly string? _originalGitConfigGlobal;

    public ConfigRepoManagerAskpassTransportTests()
    {
        // The ambient environment on a build agent carries a real GitHub token. It would be picked
        // up by the credential chain whenever a test asks for "no credential", so it is cleared
        // for the duration of the class and restored on dispose.
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _originalGitConfigGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");

        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", null);

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-config-transport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGitHubToken);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", _originalGitConfigGlobal);

        TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>Runs a real <c>git</c> and returns (exitCode, stdout, stderr).</summary>
    private static (int ExitCode, string Stdout, string Stderr) Git(
        string workingDir, params string[] args)
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
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

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

    /// <summary>
    /// Creates a REAL clone whose <c>origin</c> is the supplied URL — the premise of every
    /// existing-clone test.
    /// </summary>
    /// <remarks>
    /// The clone's history is INDEPENDENT of any remote, so it can never accidentally share a
    /// commit with one. (Two commits created with the same tree, message and identity within the
    /// SAME SECOND get the same SHA — which would make a later pull a fast-forward by accident
    /// and make the fixture's outcome depend on wall-clock timing. The distinct content below
    /// removes that coupling entirely.)
    /// </remarks>
    private string CreateRealCloneWithOrigin(ConfigRepoManager manager, string originUrl)
    {
        var clonePath = manager.LocalPath;
        Directory.CreateDirectory(clonePath);
        Git(clonePath, "init", "-b", "main");
        Git(clonePath, "config", "user.email", "probe@test.invalid");
        Git(clonePath, "config", "user.name", "Probe");
        File.WriteAllText(Path.Combine(clonePath, "hive-config.yaml"), "version: local-only\n");
        Git(clonePath, "add", "hive-config.yaml");
        Git(clonePath, "commit", "-m", "Local seed commit");
        Git(clonePath, "remote", "add", "origin", originUrl);
        return clonePath;
    }

    /// <summary>
    /// Creates a REAL clone whose history is IDENTICAL to <paramref name="remoteDir"/>'s (so the
    /// pull is a deterministic fast-forward, never a timing-dependent merge) but whose
    /// <c>origin</c> is the supplied URL.
    /// </summary>
    private string CreateRealCloneTracking(ConfigRepoManager manager, string remoteDir, string originUrl)
    {
        var clonePath = manager.LocalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(clonePath)!);
        Git(Path.GetDirectoryName(clonePath)!, "clone", remoteDir, Path.GetFileName(clonePath));
        Git(clonePath, "remote", "set-url", "origin", originUrl);
        return clonePath;
    }

    /// <summary>Reads the clone's REAL <c>.git/config</c>.</summary>
    private static string ReadGitConfig(string clonePath) =>
        File.ReadAllText(Path.Combine(clonePath, ".git", "config"));

    /// <summary>Builds a manager wired to the real process runner (no seam).</summary>
    private ConfigRepoManager CreateRealManager(string? token, string repoUrl = EligibleUrl)
    {
        var manager = new ConfigRepoManager(repoUrl, Path.Combine(_tempDir, "clone"));
        manager.TokenResolver = _ => Task.FromResult(token);
        return manager;
    }

    /// <summary>
    /// A git configuration that makes every https request to the eligible URL fail IMMEDIATELY and
    /// LOCALLY: the proxy port is closed, so git fails in milliseconds instead of reaching the
    /// network.
    /// </summary>
    private void ConfigureDeadProxy() =>
        SetGlobalGitConfig(
            $"[http \"{EligibleUrl}\"]\n\tproxy = http://127.0.0.1:1\n");

    // ── (1) The persisted origin never carries a host credential ──────────────

    /// <summary>
    /// A REAL clone of the eligible, configured URL persists the SANITIZED origin: the credential
    /// the host resolved is nowhere in the clone's <c>.git/config</c>.
    /// </summary>
    [Fact]
    public async Task RealGit_FreshClone_PersistsTheSanitizedOrigin_AndNoTokenInGitConfig()
    {
        var remoteDir = CreateLocalRemote();

        // The clone is redirected to the local remote by git's own URL rewriting — the manager
        // still hands the SANITIZED https URL to git, and git is what rewrites it.
        SetGlobalGitConfig(
            $"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var manager = CreateRealManager(Token);
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        // Premise: the clone really happened against the local remote.
        Assert.True(Directory.Exists(Path.Combine(manager.LocalPath, ".git")));
        Assert.True(File.Exists(Path.Combine(manager.LocalPath, "hive-config.yaml")));

        var config = ReadGitConfig(manager.LocalPath);

        // The persisted origin is the SANITIZED configured URL…
        Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
        // …and carries NO credential of any kind.
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REAL pull over an existing clone whose persisted origin is TOKENIZED strips the credential:
    /// the origin is normalized to the sanitized configured URL before the pull, and it is still
    /// sanitized after the pull FAILS.
    /// </summary>
    [Fact]
    public async Task RealGit_ExistingClone_TokenizedOrigin_IsNormalizedAndStaysSanitizedAfterFailure()
    {
        ConfigureDeadProxy();

        var manager = CreateRealManager(Token);
        var tokenizedOrigin = $"https://x-access-token:{StalePersistedToken}@github.com/org/config.git";
        var clonePath = CreateRealCloneWithOrigin(manager, tokenizedOrigin);

        // Premise: the persisted origin really was tokenized before the operation.
        Assert.Contains(StalePersistedToken, ReadGitConfig(clonePath), StringComparison.Ordinal);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        // PREMISE for the network command: it really ran and really failed against the closed
        // local proxy port — not for some unrelated reason.
        Assert.Contains("127.0.0.1", ex.Message, StringComparison.Ordinal);

        var config = ReadGitConfig(clonePath);
        Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// The NO-CREDENTIAL stale-origin rule, proven on the REAL <c>.git/config</c>: when nothing
    /// resolves, the manager performs NO write at all, so a persisted tokenized origin survives.
    /// This is the documented, accepted consequence — asserted honestly rather than papered over.
    /// </summary>
    [Fact]
    public async Task RealGit_ExistingClone_NoCredentialResolved_LeavesTheTokenizedOriginUntouched()
    {
        ConfigureDeadProxy();

        var manager = CreateRealManager(null);
        var tokenizedOrigin = $"https://x-access-token:{StalePersistedToken}@github.com/org/config.git";
        var clonePath = CreateRealCloneWithOrigin(manager, tokenizedOrigin);

        // Premise: the persisted origin really was tokenized before the operation.
        Assert.Contains(StalePersistedToken, ReadGitConfig(clonePath), StringComparison.Ordinal);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        // NOTHING was written: the stale credential survives, exactly as documented.
        var config = ReadGitConfig(clonePath);
        Assert.Contains(StalePersistedToken, config, StringComparison.Ordinal);
        Assert.Contains(tokenizedOrigin, config, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host credential never reaches the orchestrator's OWN (long-lived) environment: the
    /// operation-scoped variable exists only on the child.
    /// </summary>
    [Fact]
    public async Task RealGit_Operation_NeverWritesTheCredentialIntoTheParentEnvironment()
    {
        var remoteDir = CreateLocalRemote();
        SetGlobalGitConfig(
            $"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var manager = CreateRealManager(Token);
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        // The operation-scoped credential variable is a CHILD-only channel.
        Assert.Null(Environment.GetEnvironmentVariable(HostGitAskpassHelper.CredentialEnvName));

        // No environment variable of the parent carries the credential.
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var value = entry.Value as string;
            if (value is null)
                continue;
            Assert.DoesNotContain(Token, value, StringComparison.Ordinal);
        }
    }

    // ── (1) The eligibility boundary ─────────────────────────────────────────

    /// <summary>
    /// A NONELIGIBLE destination receives NO credential in any form: no askpass environment on any
    /// launch, no token anywhere in argv, and the origin keeps the configured URL VERBATIM.
    /// </summary>
    /// <remarks>
    /// The premise is asserted explicitly: the destination is genuinely noneligible under the
    /// current rule (an <c>https://github.com/</c> PREFIX), so the absence below is a property of
    /// the implementation and not of the fixture.
    /// </remarks>
    [Theory]
    [InlineData("ssh://git@github.com/org/config.git")]
    [InlineData("git@github.com:org/config.git")]
    [InlineData("http://github.com/org/config.git")]
    [InlineData("https://gitlab.com/org/config.git")]
    [InlineData("https://github.com.example.test/org/config.git")]
    [InlineData("https://github.com:8443/org/config.git")]
    [InlineData("/srv/local/config.git")]
    public async Task RealGit_NonEligibleDestination_ReceivesNoCredentialInAnyForm(string repoUrl)
    {
        // PREMISE: the destination is genuinely noneligible under the current rule.
        Assert.False(
            repoUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase),
            $"The fixture's destination '{repoUrl}' IS eligible — the premise is broken.");

        var manager = CreateRealManager(Token, repoUrl);
        var clonePath = CreateRealCloneWithOrigin(manager, repoUrl);

        // The pull fails offline (the destination does not exist), but the credential question is
        // about what the launch carried.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        // The origin keeps the configured URL VERBATIM — the pre-transport behaviour.
        var config = ReadGitConfig(clonePath);
        Assert.Contains(repoUrl, config, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
    }

    // ── (1) The askpass helper file and the child environment ────────────────

    /// <summary>
    /// The REAL child process receives the operation environment, and the askpass script it is
    /// pointed at contains NO token literal — the token travels only through the child-only
    /// variable the script reads.
    /// </summary>
    /// <remarks>
    /// The probe is a git alias, so the values printed are produced by the CHILD process itself —
    /// not by the parent that launched it.
    /// </remarks>
    [Fact]
    public async Task RealGit_ChildProcess_ReceivesTheTokenFreeAskpassScriptAndTheChildOnlyCredential()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The git alias probe is a POSIX shell command; covered on Linux.");
            return;
        }

        var manager = new ConfigRepoManager(EligibleUrl, Path.Combine(_tempDir, "probe-clone"));
        var workDir = Path.Combine(_tempDir, "probe-work");
        Directory.CreateDirectory(workDir);

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        using var auth = new BrainGitAuth(Token, SanitizedOrigin, helper!);

        // The script's contents are read BY THE CHILD, so the assertion is about the bytes the
        // child would actually execute.
        string[] probeArgs =
        [
            "-c",
            "alias.hiveprobe=!printf 'cred=[%s]\\naskpass=[%s]\\n' "
            + "\"$COPILOTHIVE_GIT_ASKPASS_CREDENTIAL\" \"$GIT_ASKPASS\"; "
            + "printf 'script=['; cat \"$GIT_ASKPASS\"; printf ']\\n'",
            "hiveprobe",
        ];

        var (exitCode, stdout, stderr) = await manager.RunGitForTestAsync(
            workDir, probeArgs, TestContext.Current.CancellationToken, auth);

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stderr), stderr);

        // The credential reached the child through its own variable…
        Assert.Contains($"cred=[{Token}]", stdout, StringComparison.Ordinal);
        // …the askpass path points at the token-free helper…
        Assert.Contains($"askpass=[{helper!.ScriptPath}]", stdout, StringComparison.Ordinal);

        // …and the SCRIPT the child would execute carries NO token literal. The script is printed
        // by the child itself, so this is the child's own view of the file.
        const string ScriptPrefix = "script=[";
        var scriptStart = stdout.IndexOf(ScriptPrefix, StringComparison.Ordinal);
        Assert.True(scriptStart >= 0, $"The child did not print the askpass script. Output: {stdout}");
        var scriptEnd = stdout.LastIndexOf(']');
        Assert.True(scriptEnd > scriptStart, $"The child's script output was not delimited. Output: {stdout}");
        var scriptBody = stdout[(scriptStart + ScriptPrefix.Length)..scriptEnd];

        Assert.DoesNotContain(Token, scriptBody, StringComparison.Ordinal);
        Assert.Contains("COPILOTHIVE_GIT_ASKPASS_CREDENTIAL", scriptBody, StringComparison.Ordinal);
        Assert.Contains("#!/bin/sh", scriptBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The REAL child environment SCRUBS the inherited credential variables and RESETS the
    /// inherited credential-helper list — the two properties that make an inherited helper
    /// unable to select or persist the askpass token.
    /// </summary>
    [Fact]
    public async Task RealGit_ChildEnvironment_ScrubsTheInheritedTokenVariablesAndResetsTheHelperList()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The git alias probe is a POSIX shell command; covered on Linux.");
            return;
        }

        // The ambient token variables are set so their ABSENCE from the child is a discriminating
        // observation: an inherited GH_TOKEN/GITHUB_TOKEN would be visible in the child if the
        // scrub were removed.
        Environment.SetEnvironmentVariable("GH_TOKEN", "gho_inherited_gh_token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "gho_inherited_github_token");
        Environment.SetEnvironmentVariable("GITHUB_CONFIG_REPO_TOKEN", "gho_inherited_config_repo_token");

        var manager = new ConfigRepoManager(EligibleUrl, Path.Combine(_tempDir, "probe-clone"));
        var workDir = Path.Combine(_tempDir, "probe-work");
        Directory.CreateDirectory(workDir);

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        using var auth = new BrainGitAuth(Token, SanitizedOrigin, helper!);

        string[] probeArgs =
        [
            "-c",
            "alias.hiveprobe=!printf 'gh=[%s]\\ngithub=[%s]\\ncfg=[%s]\\n' "
            + "\"$GH_TOKEN\" \"$GITHUB_TOKEN\" \"$GIT_CONFIG_VALUE_0\"",
            "hiveprobe",
        ];

        var (exitCode, stdout, stderr) = await manager.RunGitForTestAsync(
            workDir, probeArgs, TestContext.Current.CancellationToken, auth);

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stderr), stderr);

        // The inherited token variables are ABSENT from the child…
        Assert.Contains("gh=[]", stdout, StringComparison.Ordinal);
        Assert.Contains("github=[]", stdout, StringComparison.Ordinal);
        // …and the inherited credential-helper list is RESET for this invocation only.
        Assert.Contains("cfg=[]", stdout, StringComparison.Ordinal);

        // The ambient values the test itself set are restored.
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_CONFIG_REPO_TOKEN", null);
    }

    // ── (1) The REAL child environment of an actual network launch ──────────

    /// <summary>
    /// The REAL <c>git pull</c> process of an eligible destination receives the operation
    /// environment: the credential reaches it through the child-only variable, the askpass helper
    /// is the token-free script, and the inherited token variable is absent.
    /// </summary>
    /// <remarks>
    /// The observation is produced by a real <c>git</c> WRAPPER on the PATH: it records each
    /// launch's OWN environment before delegating to the real git, so the observation is the
    /// launched process's own view — not the parent's.
    /// </remarks>
    [Fact]
    public async Task RealGit_PullLaunch_ReceivesTheOperationEnvironment()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The recording git wrapper is a POSIX shell script; covered on Linux.");
            return;
        }

        var remoteDir = CreateLocalRemote();

        var wrapperDir = Path.Combine(_tempDir, "wrapper-bin");
        Directory.CreateDirectory(wrapperDir);
        var launchLog = Path.Combine(_tempDir, "git-launches.log");
        WriteRecordingGitWrapper(wrapperDir, launchLog, remoteDir);

        // The clone is redirected to the local remote by git's own URL rewriting, so the pull is a
        // REAL network command that succeeds offline.
        SetGlobalGitConfig(
            $"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var manager = CreateRealManager(Token);
        var clonePath = CreateRealCloneTracking(manager, remoteDir, EligibleUrl);

        // The clone's history IS the remote's, so the pull is a deterministic fast-forward — the
        // fixture's outcome never depends on wall-clock timing.
        Git(clonePath, "config", "branch.main.remote", "origin");
        Git(clonePath, "config", "branch.main.merge", "refs/heads/main");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", wrapperDir + Path.PathSeparator + originalPath);
        try
        {
            // The pull is a REAL network command that succeeds offline (the clone tracks the
            // remote's own history, so it is a deterministic fast-forward). An exception here
            // WOULD fail the test — the successful pull is the premise of the observation below.
            await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

            // The pull really ran against the sanitized origin.
            Assert.Contains(
                SanitizedOrigin,
                ReadGitConfig(manager.LocalPath),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        // Premise: the wrapper really observed the launches.
        var recorded = ReadAllTextIfPresent(launchLog);
        Assert.NotEmpty(recorded);

        // The PULL launch is identified by its OWN argument list, so the observation is
        // specifically about the network command the manager authenticated.
        var pullLaunch = FindLaunchBlock(recorded, "pull");

        // The pull process received the operation credential through the child-only variable…
        Assert.Contains($"CRED=[{Token}]", pullLaunch, StringComparison.Ordinal);
        // …pointed at the token-free askpass helper…
        Assert.Contains(HostGitAskpassHelper.ScriptFileName, pullLaunch, StringComparison.Ordinal);
        // …never in its ARGUMENT LIST…
        Assert.DoesNotContain(
            Token,
            string.Join('|', pullLaunch.Split('\n')
                .Where(l => l.StartsWith("ARGV[", StringComparison.Ordinal))),
            StringComparison.Ordinal);
        // …and the inherited token variable was scrubbed from the child.
        Assert.Contains("GH=[]", pullLaunch, StringComparison.Ordinal);
    }

    /// <summary>
    /// The REAL <c>git pull</c> process of a NONELIGIBLE destination receives NO credential
    /// transport at all: no askpass path, no child-only credential variable — the launch simply
    /// inherits the parent environment, exactly as it did before the transport existed.
    /// </summary>
    [Fact]
    public async Task RealGit_NonEligiblePullLaunch_CarriesNoCredentialTransportAtAll()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The recording git wrapper is a POSIX shell script; covered on Linux.");
            return;
        }

        const string NonEligible = "ssh://git@github.com/org/config.git";

        // PREMISE: the destination is genuinely noneligible under the current rule.
        Assert.False(
            NonEligible.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase),
            "The fixture's destination IS eligible — the premise is broken.");

        var wrapperDir = Path.Combine(_tempDir, "wrapper-bin");
        Directory.CreateDirectory(wrapperDir);
        var launchLog = Path.Combine(_tempDir, "git-launches.log");
        WriteRecordingGitWrapper(wrapperDir, launchLog, NonEligible);

        var manager = CreateRealManager(Token, NonEligible);
        CreateRealCloneWithOrigin(manager, NonEligible);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", wrapperDir + Path.PathSeparator + originalPath);
        try
        {
            // The pull fails (the ssh destination does not exist), but the launch happened.
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.SyncRepoAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        var recorded = ReadAllTextIfPresent(launchLog);
        Assert.NotEmpty(recorded);

        var pullLaunch = FindLaunchBlock(recorded, "pull");

        // NO credential transport: the askpass script is absent and the child-only credential
        // variable is EMPTY (the wrapper prints it whatever it is).
        Assert.DoesNotContain(HostGitAskpassHelper.ScriptFileName, pullLaunch, StringComparison.Ordinal);
        Assert.Contains("CRED=[]", pullLaunch, StringComparison.Ordinal);
        Assert.Contains("ASKPASS=[]", pullLaunch, StringComparison.Ordinal);
        // And the token appears NOWHERE in the launch — not in argv either.
        Assert.DoesNotContain(Token, pullLaunch, StringComparison.Ordinal);
    }

    // ── (1) Credential-helper precedence, with a REAL recording helper ───────

    /// <summary>
    /// A REAL git operation with a RECORDING credential helper installed in real git
    /// configuration: the helper is never invoked at all, so neither its <c>get</c> (selection)
    /// nor its <c>store</c>/<c>erase</c> (persistence) can receive the askpass token.
    /// </summary>
    /// <remarks>
    /// The server answers every request with <c>401</c> and a <c>WWW-Authenticate</c> challenge,
    /// which is exactly what makes git consult its credential machinery. The CONTROL below runs the
    /// SAME command with the SAME configuration and the SAME askpass script but WITHOUT the
    /// operation-scoped helper reset, and proves the recorder really is reachable — and that it
    /// really does receive the token — so the production assertion is not vacuous.
    /// </remarks>
    [Fact]
    public async Task RealGit_RecordingCredentialHelper_IsNeverInvoked_AndNeverReceivesTheToken()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The recording credential helper is a POSIX shell script; covered on Linux.");
            return;
        }

        using var server = new UnauthorizedGitServer();
        var recorderPath = Path.Combine(_tempDir, "recorder.sh");
        var recorderLog = Path.Combine(_tempDir, "recorder.log");
        WriteRecordingHelper(recorderPath, recorderLog);

        var askpassPath = Path.Combine(_tempDir, "askpass.sh");
        WriteAskpassScript(askpassPath, Token);

        SetGlobalGitConfig(
            $"[url \"http://127.0.0.1:{server.Port}/org/config.git\"]\n"
            + $"\tinsteadOf = {EligibleUrl}\n"
            + $"[credential]\n\thelper = {recorderPath}\n");

        var manager = CreateRealManager(Token);

        // The operation FAILS (the server never authorizes), but the credential machinery ran.
        await Assert.ThrowsAnyAsync<Exception>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        // The recording helper was NEVER invoked — not for selection, not for persistence.
        var log = ReadAllTextIfPresent(recorderLog);
        Assert.DoesNotContain("CALLED get", log, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLED store", log, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLED erase", log, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLED approve", log, StringComparison.Ordinal);
        // …and no token material reached it through ANY stream it could observe.
        Assert.DoesNotContain(Token, log, StringComparison.Ordinal);

        // ── CONTROL ───────────────────────────────────────────────────────────
        // The SAME git command with the SAME configuration and the SAME askpass script, but
        // WITHOUT the operation-scoped helper reset: the recorder IS reached, and it DOES receive
        // the token. This is what makes the assertion above meaningful.
        var controlLog = Path.Combine(_tempDir, "recorder-control.log");
        var controlRecorder = Path.Combine(_tempDir, "recorder-control.sh");
        WriteRecordingHelper(controlRecorder, controlLog);

        SetGlobalGitConfig(
            $"[url \"http://127.0.0.1:{server.Port}/org/config.git\"]\n"
            + $"\tinsteadOf = {EligibleUrl}\n"
            + $"[credential]\n\thelper = {controlRecorder}\n");

        var controlEnv = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_GLOBAL"] = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"),
            ["GIT_ASKPASS"] = askpassPath,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = _tempDir,
        };

        var control = RunGitWithEnv(controlEnv, _tempDir,
            "clone", EligibleUrl, $"control-config-{Guid.NewGuid():N}");

        Assert.NotEqual(0, control.ExitCode);

        var controlText = ReadAllTextIfPresent(controlLog);
        Assert.Contains("CALLED get", controlText, StringComparison.Ordinal);
        // The recorder really does see the askpass token when the reset is absent.
        Assert.Contains(Token, controlText, StringComparison.Ordinal);
    }

    // ── (1) The clone residual path ──────────────────────────────────────────

    /// <summary>
    /// A REAL clone that FAILS after creating a usable repository has its residual origin
    /// normalized to the sanitized URL, and the PRIMARY failure is what the caller observes.
    /// </summary>
    /// <remarks>
    /// The clone is redirected to a LOCAL remote by git's own URL rewriting — so the clone really
    /// transfers objects — and the <c>post-checkout</c> hook rewrites the clone's origin to a
    /// credential-bearing URL and then fails the clone. That reproduces exactly the state the
    /// residual cleanup exists for: a usable repository left behind by a failed clone, carrying a
    /// tokenized origin.
    /// </remarks>
    [Fact]
    public async Task RealGit_FailedClone_WithResidualRepo_NormalizesTheOriginAndPreservesThePrimaryFailure()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The failing post-checkout hook is a POSIX shell script; covered on Linux.");
            return;
        }

        var remoteDir = CreateLocalRemote();

        var hooksDir = Path.Combine(_tempDir, "hooks");
        Directory.CreateDirectory(hooksDir);
        var marker = Path.Combine(_tempDir, "hook-ran.marker");
        WriteFailingPostCheckoutHook(hooksDir, marker, StalePersistedToken, sleepForever: false);

        SetGlobalGitConfig(
            $"[core]\n\thooksPath = {hooksDir}\n"
            + $"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var manager = CreateRealManager(Token);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        // Premise: the hook really ran, so the clone really failed AFTER creating the repository.
        Assert.True(File.Exists(marker), "The failing post-checkout hook never ran.");
        Assert.True(Directory.Exists(Path.Combine(manager.LocalPath, ".git")));

        // The PRIMARY failure is what the caller observes — not a cleanup error.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, ex.ToString(), StringComparison.Ordinal);

        // The residual origin was normalized to the sanitized URL: no token survives on disk.
        var config = ReadGitConfig(manager.LocalPath);
        Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CANCELLED clone that left a usable repository still normalizes its origin — the cleanup
    /// runs under its OWN token, independent of the caller's cancelled one — and the cancellation
    /// is what the caller observes.
    /// </summary>
    [Fact]
    public async Task RealGit_CancelledClone_WithResidualRepo_NormalizesTheOriginAndPropagatesTheCancellation()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The blocking post-checkout hook is a POSIX shell script; covered on Linux.");
            return;
        }

        var remoteDir = CreateLocalRemote();

        var hooksDir = Path.Combine(_tempDir, "hooks");
        Directory.CreateDirectory(hooksDir);
        var marker = Path.Combine(_tempDir, "hook-ran.marker");
        WriteFailingPostCheckoutHook(hooksDir, marker, StalePersistedToken, sleepForever: true);

        SetGlobalGitConfig(
            $"[core]\n\thooksPath = {hooksDir}\n"
            + $"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var manager = CreateRealManager(Token);
        var diagnostics = new List<string>();
        manager.DiagnosticSink = diagnostics.Add;

        using var cts = new CancellationTokenSource();
        var run = manager.SyncRepoAsync(cts.Token);

        try
        {
            // The hook blocks inside the clone; its marker file proves the clone is in flight.
            Assert.True(
                await WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(20), cts.Token),
                "The blocking post-checkout hook never ran.");

            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            // The cancellation is what the caller observes.
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(StalePersistedToken, ex.ToString(), StringComparison.Ordinal);

            // The residual repository exists…
            Assert.True(Directory.Exists(Path.Combine(manager.LocalPath, ".git")));

            // …and its origin was normalized. The cleanup could only have done that with a token
            // that was NOT the caller's cancelled one: the launcher's own pre-launch check would
            // have aborted the command otherwise.
            var config = ReadGitConfig(manager.LocalPath);
            Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
            Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
            Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);

            // The DECISIVE observation that the cleanup ran under its OWN token: it COMPLETED, so
            // no "could not normalize" diagnostic was emitted. A cleanup that had used the
            // caller's cancelled token would have been aborted and WOULD have written one — even
            // though the `remote set-url` process it started may still have written the config.
            Assert.DoesNotContain(
                diagnostics,
                d => d.Contains("residual", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            cts.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            }
            catch (Exception)
            {
                // The body asserts the outcome.
            }
        }
    }

    /// <summary>
    /// When the residual cleanup CANNOT complete (the clone's config is locked), the PRIMARY
    /// exception still propagates and the emitted diagnostic is CREDENTIAL-FREE.
    /// </summary>
    [Fact]
    public async Task RealGit_ResidualCleanupCannotComplete_PrimaryFailurePropagates_AndTheDiagnosticIsCredentialFree()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The failing post-checkout hook is a POSIX shell script; covered on Linux.");
            return;
        }

        var remoteDir = CreateLocalRemote();

        var hooksDir = Path.Combine(_tempDir, "hooks");
        Directory.CreateDirectory(hooksDir);
        var marker = Path.Combine(_tempDir, "hook-ran.marker");
        WriteFailingPostCheckoutHook(
            hooksDir, marker, StalePersistedToken, sleepForever: false, lockConfig: true);

        SetGlobalGitConfig(
            $"[core]\n\thooksPath = {hooksDir}\n"
            + $"[url \"{remoteDir}\"]\n\tinsteadOf = {EligibleUrl}\n");

        var diagnostics = new List<string>();
        var manager = CreateRealManager(Token);
        manager.DiagnosticSink = diagnostics.Add;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.SyncRepoAsync(
            TestContext.Current.CancellationToken));

        // Premise: the hook really ran, so the clone really failed after creating the repository.
        Assert.True(File.Exists(marker), "The failing post-checkout hook never ran.");
        Assert.True(Directory.Exists(Path.Combine(manager.LocalPath, ".git")));

        // The PRIMARY failure propagates — the cleanup never replaces it.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);

        // A diagnostic WAS emitted…
        var diagnostic = Assert.Single(
            diagnostics, d => d.Contains("residual", StringComparison.OrdinalIgnoreCase));

        // …and it is credential-free.
        Assert.DoesNotContain(Token, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", diagnostic, StringComparison.Ordinal);
    }

    // ── Fixture script writers ──────────────────────────────────────────────

    /// <summary>
    /// Writes a credential helper that RECORDS every invocation (with its standard input) into
    /// <paramref name="logPath"/>. It never produces a credential, so git's own fallback (the
    /// askpass script) is what supplies one.
    /// </summary>
    private static void WriteRecordingHelper(string scriptPath, string logPath)
    {
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n"
            + "{\n"
            + "  printf 'CALLED %s\\n' \"$1\"\n"
            + "  cat\n"
            + "  printf '\\n'\n"
            + "} >> \"" + logPath + "\"\n"
            + "exit 0\n");
        SetExecutable(scriptPath);
    }

    /// <summary>Writes an askpass script that answers exactly like the production helper.</summary>
    private static void WriteAskpassScript(string scriptPath, string credential)
    {
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n"
            + "case \"$1\" in\n"
            + "  *sername*) printf '%s' \"x-access-token\" ;;\n"
            + $"  *) printf '%s' \"{credential}\" ;;\n"
            + "esac\n");
        SetExecutable(scriptPath);
    }

    /// <summary>
    /// Writes a <c>post-checkout</c> hook that rewrites the clone's origin to a CREDENTIAL-BEARING
    /// URL, records that it ran, and then fails the clone — optionally blocking forever, and
    /// optionally locking the clone's config so the residual cleanup cannot complete.
    /// </summary>
    private static void WriteFailingPostCheckoutHook(
        string hooksDir,
        string markerPath,
        string credential,
        bool sleepForever,
        bool lockConfig = false)
    {
        var scriptPath = Path.Combine(hooksDir, "post-checkout");
        var lockCommand = lockConfig
            ? $"mkdir \"$(git rev-parse --git-dir)/config.lock\" 2>/dev/null || true\n"
            : string.Empty;

        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n"
            + "git remote set-url origin \"https://x-access-token:" + credential + "@github.com/org/config.git\"\n"
            + lockCommand
            + "printf 'ran\\n' > \"" + markerPath + "\"\n"
            + (sleepForever ? "sleep 120\n" : "exit 7\n"));

        SetExecutable(scriptPath);
    }

    /// <summary>
    /// Makes a script executable. POSIX modes do not exist on Windows, so the call is guarded —
    /// every test that uses a script asserts the Linux-only contract first.
    /// </summary>
    private static void SetExecutable(string scriptPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Writes a <c>git</c> WRAPPER on the PATH that records EVERY launch's own argument list and
    /// environment before delegating to the real git. The network remote is rewritten onto a local
    /// bare repository — with an EXPLICIT refspec for a fetch, so the remote-tracking refs are
    /// created exactly as they would be against the real remote — so the whole operation stays
    /// offline and deterministic.
    /// </summary>
    /// <param name="wrapperDir">The directory the wrapper is written into (prepended to PATH).</param>
    /// <param name="logPath">The file every launch appends its observation to.</param>
    /// <param name="remoteTarget">
    /// What the network remote is rewritten to: a local bare repository path, or a noneligible URL
    /// that is left untouched (so the launch really fails, proving the launch happened).
    /// </param>
    private static void WriteRecordingGitWrapper(
        string wrapperDir, string logPath, string remoteTarget)
    {
        var realGit = FindRealGitExecutable();
        var scriptPath = Path.Combine(wrapperDir, "git");

        // A local bare repository is rewritten onto; a URL is left alone.
        var isLocalPath = !remoteTarget.Contains("://", StringComparison.Ordinal)
            && !remoteTarget.Contains(':', StringComparison.Ordinal);

        var rewrite = isLocalPath
            ? $"  if [ \"$a\" = \"origin\" ]; then args+=(\"{remoteTarget}\"); else args+=(\"$a\"); fi\n"
            : $"  if [ \"$a\" = \"origin\" ]; then args+=(\"{remoteTarget}\"); else args+=(\"$a\"); fi\n";

        File.WriteAllText(
            scriptPath,
            "#!/bin/bash\n"
            + "{\n"
            + "  printf '=== CALL ===\\n'\n"
            + "  i=0\n"
            + "  for a in \"$@\"; do printf 'ARGV[%s]=%s\\n' \"$i\" \"$a\"; i=$((i+1)); done\n"
            + "  printf 'CRED=[%s]\\n' \"$COPILOTHIVE_GIT_ASKPASS_CREDENTIAL\"\n"
            + "  printf 'ASKPASS=[%s]\\n' \"$GIT_ASKPASS\"\n"
            + "  printf 'GH=[%s]\\n' \"$GH_TOKEN\"\n"
            + "  printf 'CFG_VALUE0=[%s]\\n' \"$GIT_CONFIG_VALUE_0\"\n"
            + "} >> \"" + logPath + "\"\n"
            + "case \"$1\" in\n"
            + "  fetch)\n"
            + "    shift\n"
            + "    args=(\"" + (isLocalPath ? remoteTarget : "origin") + "\")\n"
            + "    saw_refspec=0\n"
            + "    for a in \"$@\"; do\n"
            + "      case \"$a\" in\n"
            + "        origin) ;;\n"
            + "        +refs/heads/*) args+=(\"$a\"); saw_refspec=1 ;;\n"
            + "        *) args+=(\"+refs/heads/$a:refs/remotes/origin/$a\"); saw_refspec=1 ;;\n"
            + "      esac\n"
            + "    done\n"
            + "    if [ \"$saw_refspec\" = \"0\" ]; then args+=(\"+refs/heads/*:refs/remotes/origin/*\"); fi\n"
            + "    set -- fetch \"${args[@]}\"\n"
            + "    ;;\n"
            + "  pull|push|ls-remote|clone)\n"
            + "    args=()\n"
            + "    for a in \"$@\"; do\n"
            + rewrite
            + "    done\n"
            + "    set -- \"${args[@]}\"\n"
            + "    ;;\n"
            + "esac\n"
            + "exec \"" + realGit + "\" \"$@\"\n");
        SetExecutable(scriptPath);
    }

    /// <summary>
    /// Returns the recorded launch block whose ARGUMENT LIST contains <paramref name="verb"/>.
    /// </summary>
    private static string FindLaunchBlock(string recorded, string verb)
    {
        var blocks = recorded.Split("=== CALL ===", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            foreach (var line in block.Split('\n'))
            {
                if (!line.StartsWith("ARGV[", StringComparison.Ordinal))
                    continue;

                var value = line[(line.IndexOf('=') + 1)..];
                if (value == verb)
                    return block;
            }
        }

        throw new InvalidOperationException(
            $"No recorded git launch used '{verb}'. Recorded launches: {recorded}");
    }

    /// <summary>
    /// Locates the REAL git executable, skipping the recording wrapper this fixture installs.
    /// </summary>
    private static string FindRealGitExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "git");
            if (!File.Exists(candidate))
                continue;

            // The wrapper is identified by its recording marker.
            try
            {
                if (File.ReadAllText(candidate).Contains("=== CALL ===", StringComparison.Ordinal))
                    continue;
            }
            catch (IOException)
            {
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException("Could not locate the real git executable on PATH.");
    }

    // ── Small process/IO helpers ────────────────────────────────────────────

    /// <summary>Runs a real <c>git</c> with an EXPLICIT environment.</summary>
    private static (int ExitCode, string Stdout, string Stderr) RunGitWithEnv(
        IReadOnlyDictionary<string, string?> environment, string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        psi.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (value is not null)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Reads a file, tolerating its absence.</summary>
    private static string ReadAllTextIfPresent(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Bounded polling wait for an observable condition. The deadline is purely protective — the
    /// failure is asserted by the caller, and no conclusion is drawn from how long the poll took.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition, TimeSpan deadline, CancellationToken testToken)
    {
        if (condition())
            return true;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadlineCts.CancelAfter(deadline);

        try
        {
            while (await timer.WaitForNextTickAsync(deadlineCts.Token))
            {
                if (condition())
                    return true;
            }
        }
        catch (OperationCanceledException) when (!testToken.IsCancellationRequested)
        {
            // Protective deadline expired — the caller asserts the condition.
        }

        return condition();
    }

    /// <summary>
    /// A minimal HTTP server that answers EVERY git request with <c>401 Unauthorized</c> and a
    /// <c>WWW-Authenticate</c> challenge — which is precisely what makes git consult its
    /// credential machinery (helper <c>get</c>, then askpass, then helper <c>store</c>/<c>erase</c>).
    /// </summary>
    private sealed class UnauthorizedGitServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();

        public UnauthorizedGitServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        /// <summary>The port the server listens on.</summary>
        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    // Read the request headers (git's request has no body).
                    var read = await stream.ReadAsync(buffer);
                    if (read <= 0)
                        return;

                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 401 Unauthorized\r\n"
                        + "WWW-Authenticate: Basic realm=\"GitHub\"\r\n"
                        + "Content-Length: 0\r\n"
                        + "Connection: close\r\n"
                        + "\r\n");
                    await stream.WriteAsync(response);
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                    // The client went away — nothing to do.
                }
                catch (ObjectDisposedException)
                {
                    // The server was disposed.
                }
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
                // Already stopped.
            }
            _shutdown.Dispose();
        }
    }
}
