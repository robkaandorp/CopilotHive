using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CopilotHive.Git;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests;

/// <summary>
/// The REAL-PROCESS half of the host credential-at-rest elimination contract for
/// <see cref="BrainRepoManager"/>: the claims that can only be proven against an actual
/// <c>git</c> process and the actual files it leaves on disk.
/// <para>
/// Every test here drives the manager WITHOUT the <see cref="BrainGitRequest"/> runner seam, so the
/// production process runner — including the operation environment it installs on the child — is
/// what runs. The assertions are made against
/// <list type="bullet">
///   <item><description>the REAL <c>.git/config</c> the clone/refresh left on disk,</description></item>
///   <item><description>the REAL credential-helper invocations a real <c>git</c> made (a recording
///   helper installed in real git configuration), and</description></item>
///   <item><description>the REAL child environment, observed from inside the child process
///   itself.</description></item>
/// </list>
/// The seam-based half of the contract lives in
/// <c>BrainRepoManagerCredentialTests</c> (raw argv, per-entry-point matrix) and in
/// <see cref="HostGitAskpassHelperTests"/> (the helper's own file-level contract).
/// </para>
/// <para>
/// The class serializes on <c>EnvVarMutation</c> because it mutates the process-wide environment
/// (<c>GIT_CONFIG_GLOBAL</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>): the child environment is
/// built from the parent environment, so the git configuration a test wants its child to see can
/// only be installed that way.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class BrainRepoManagerAskpassTransportTests : IDisposable
{
    /// <summary>The credential the host resolves for the operation.</summary>
    internal const string Token = "gho_real_process_transport_token";

    /// <summary>
    /// A credential ALREADY PERSISTED in a clone's origin before the operation — the stale
    /// tokenized origin the sanitized contract must strip (or, with no credential resolved,
    /// honestly leave alone).
    /// </summary>
    internal const string StalePersistedToken = "gho_stale_persisted_token";

    /// <summary>
    /// A distinctive repository name. Every URL this class configures is scoped to it, so the
    /// process-wide git configuration it installs can never match another test's repository.
    /// </summary>
    internal const string RepoName = "copilothive-transport-probe";

    /// <summary>The eligible, configured URL of the probe repository.</summary>
    internal const string ConfiguredUrl = $"https://github.com/acme/{RepoName}.git";

    /// <summary>The SANITIZED origin the manager must persist.</summary>
    internal const string SanitizedOrigin = ConfiguredUrl;

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGitHubToken;
    private readonly string? _originalGitConfigGlobal;

    public BrainRepoManagerAskpassTransportTests()
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

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-transport-{Guid.NewGuid():N}");
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
        File.WriteAllText(Path.Combine(seed, "README.md"), "probe\n");
        Git(seed, "add", "README.md");
        Git(seed, "commit", "-m", "Seed commit");
        Git(seed, "remote", "add", "origin", remoteDir);
        Git(seed, "push", "origin", "main");

        return remoteDir;
    }

    /// <summary>
    /// Creates a REAL clone whose <c>origin</c> is the supplied URL — the premise of every
    /// existing-clone test.
    /// </summary>
    private string CreateRealCloneWithOrigin(BrainRepoManager manager, string originUrl)
    {
        var clonePath = manager.GetClonePath(RepoName);
        Directory.CreateDirectory(clonePath);
        Git(clonePath, "init", "-b", "main");
        Git(clonePath, "config", "user.email", "probe@test.invalid");
        Git(clonePath, "config", "user.name", "Probe");
        File.WriteAllText(Path.Combine(clonePath, "seed.txt"), "seed\n");
        Git(clonePath, "add", "seed.txt");
        Git(clonePath, "commit", "-m", "Seed commit");
        Git(clonePath, "remote", "add", "origin", originUrl);
        return clonePath;
    }

    /// <summary>Reads the clone's REAL <c>.git/config</c>.</summary>
    private static string ReadGitConfig(string clonePath) =>
        File.ReadAllText(Path.Combine(clonePath, ".git", "config"));

    /// <summary>Builds a manager wired to the real process runner (no seam).</summary>
    private BrainRepoManager CreateRealManager(
        string? token,
        TestLogger<BrainRepoManager>? logger = null,
        string? configuredUrl = ConfiguredUrl)
    {
        return new BrainRepoManager(
            _tempDir,
            logger ?? new TestLogger<BrainRepoManager>(),
            gitRunner: null,
            tokenLookup: _ => Task.FromResult(token),
            configuredUrlLookup: _ => configuredUrl);
    }

    /// <summary>
    /// A git configuration that makes every https request to the probe URL fail IMMEDIATELY and
    /// LOCALLY: the proxy port is closed, so git fails in milliseconds instead of reaching the
    /// network. The URL prefix is scoped to the probe repository, so no other repository can match.
    /// </summary>
    private void ConfigureDeadProxy() =>
        SetGlobalGitConfig(
            $"[http \"{ConfiguredUrl}\"]\n\tproxy = http://127.0.0.1:1\n");

    // ── (a) The persisted origin never carries a host credential ─────────────

    /// <summary>
    /// A REAL clone of the configured, eligible URL persists the SANITIZED origin: the credential
    /// the host resolved is nowhere in the clone's <c>.git/config</c>.
    /// </summary>
    [Fact]
    public async Task RealGit_FreshClone_PersistsTheSanitizedOrigin_AndNoTokenInGitConfig()
    {
        var remoteDir = CreateLocalRemote();

        // The clone is redirected to the local remote by git's own URL rewriting — the manager
        // still hands the SANITIZED https URL to git, and git is what rewrites it.
        SetGlobalGitConfig(
            $"[url \"{remoteDir}\"]\n\tinsteadOf = {ConfiguredUrl}\n");

        var manager = CreateRealManager(Token);
        var path = await manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken);

        // Premise: the clone really happened against the local remote.
        Assert.True(Directory.Exists(Path.Combine(path, ".git")));
        Assert.Contains("probe", File.ReadAllText(Path.Combine(path, "README.md")), StringComparison.Ordinal);

        var config = ReadGitConfig(path);

        // The persisted origin is the SANITIZED configured URL…
        Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
        // …and carries NO credential of any kind.
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller-supplied CREDENTIAL-BEARING URL that denotes the configured repository is replaced
    /// by the sanitized configured origin: the caller's own token is not persisted either, and the
    /// host credential is delivered through the askpass helper instead.
    /// </summary>
    [Fact]
    public async Task RealGit_FreshClone_CallerSuppliedCredentialUrl_IsNotPersisted()
    {
        var remoteDir = CreateLocalRemote();
        SetGlobalGitConfig(
            $"[url \"{remoteDir}\"]\n\tinsteadOf = {ConfiguredUrl}\n");

        var manager = CreateRealManager(Token);
        const string SuppliedUrl = $"https://x-access-token:{StalePersistedToken}@github.com/acme/{RepoName}.git";

        var path = await manager.EnsureCloneAsync(
            RepoName, SuppliedUrl, "main", TestContext.Current.CancellationToken);

        var config = ReadGitConfig(path);
        Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REAL refresh over an existing clone whose persisted origin is TOKENIZED strips the
    /// credential: the origin is normalized to the sanitized configured URL before the network
    /// command, and it is still sanitized after that command FAILS.
    /// </summary>
    [Fact]
    public async Task RealGit_ExistingClone_TokenizedOrigin_IsNormalizedAndStaysSanitizedAfterFailure()
    {
        ConfigureDeadProxy();

        var manager = CreateRealManager(Token);
        var tokenizedOrigin = $"https://x-access-token:{StalePersistedToken}@github.com/acme/{RepoName}.git";
        var clonePath = CreateRealCloneWithOrigin(manager, tokenizedOrigin);

        // Premise: the persisted origin really was tokenized before the operation.
        Assert.Contains(StalePersistedToken, ReadGitConfig(clonePath), StringComparison.Ordinal);

        var result = await manager.FetchOriginAsync(
            RepoName, null, TestContext.Current.CancellationToken);

        // The network command failed — the local dead proxy makes that deterministic and offline.
        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        var config = ReadGitConfig(clonePath);
        Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, config, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);

        // The failure diagnostic carries no credential either.
        Assert.DoesNotContain(Token, result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, result.Error!, StringComparison.Ordinal);
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
        var tokenizedOrigin = $"https://x-access-token:{StalePersistedToken}@github.com/acme/{RepoName}.git";
        var clonePath = CreateRealCloneWithOrigin(manager, tokenizedOrigin);

        // Premise: the persisted origin really was tokenized before the operation.
        Assert.Contains(StalePersistedToken, ReadGitConfig(clonePath), StringComparison.Ordinal);

        var result = await manager.FetchOriginAsync(
            RepoName, null, TestContext.Current.CancellationToken);

        // The operation still ran (and failed on the dead proxy)…
        Assert.False(result.Success);

        // …and NOTHING was written: the stale credential survives, exactly as documented.
        var config = ReadGitConfig(clonePath);
        Assert.Contains(StalePersistedToken, config, StringComparison.Ordinal);
        Assert.Contains(tokenizedOrigin, config, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host credential never reaches the orchestrator's OWN (long-lived) environment: the
    /// operation-scoped variable exists only on the child, and the inherited token variables are
    /// untouched by the operation.
    /// </summary>
    [Fact]
    public async Task RealGit_Operation_NeverWritesTheCredentialIntoTheParentEnvironment()
    {
        var remoteDir = CreateLocalRemote();
        SetGlobalGitConfig(
            $"[url \"{remoteDir}\"]\n\tinsteadOf = {ConfiguredUrl}\n");

        var manager = CreateRealManager(Token);

        await manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken);

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

    // ── (b) The inherited credential helper is disabled for the invocation ───

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
            $"[url \"http://127.0.0.1:{server.Port}/{RepoName}.git\"]\n"
            + $"\tinsteadOf = {ConfiguredUrl}\n"
            + $"[credential]\n\thelper = {recorderPath}\n");

        var manager = CreateRealManager(Token);
        var managerClonePath = manager.GetClonePath(RepoName);

        // The operation FAILS (the server never authorizes), but the credential machinery ran.
        await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken));

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
            $"[url \"http://127.0.0.1:{server.Port}/{RepoName}.git\"]\n"
            + $"\tinsteadOf = {ConfiguredUrl}\n"
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
            "clone", ConfiguredUrl, $"control-{RepoName}");

        Assert.NotEqual(0, control.ExitCode);

        var controlText = ReadAllTextIfPresent(controlLog);
        Assert.Contains("CALLED get", controlText, StringComparison.Ordinal);
        // The recorder really does see the askpass token when the reset is absent.
        Assert.Contains(Token, controlText, StringComparison.Ordinal);

        // The failed control clone must not leave a repository behind that could confuse the
        // manager's fixture.
        var controlPath = Path.Combine(_tempDir, $"control-{RepoName}");
        if (Directory.Exists(controlPath))
            TestHelpers.ForceDeleteDirectory(controlPath);
        Assert.False(Directory.Exists(managerClonePath) && Directory.Exists(Path.Combine(managerClonePath, ".git")));
    }

    // ── (c) The real child environment and the askpass helper ───────────────

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

        var manager = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());
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
        // by the child itself, so this is the child's own view of the file. The script is
        // multi-line, so the body is everything between the opening and the closing bracket.
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
    /// The askpass script's bytes are IDENTICAL for every operation — the token is never written
    /// into the file, only into the child environment.
    /// </summary>
    [Fact]
    public void AskpassScript_IsByteIdenticalAcrossOperations_AndTokenFree()
    {
        using var first = HostGitAskpassHelper.TryCreate();
        using var second = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(first);
        Assert.NotNull(second);

        var firstBytes = File.ReadAllBytes(first!.ScriptPath);
        var secondBytes = File.ReadAllBytes(second!.ScriptPath);

        Assert.Equal(firstBytes, secondBytes);
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(firstBytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// The MERGE process — which is launched directly, bypassing the shared git runner — receives
    /// the operation environment too: the credential reaches it through the child-only variable,
    /// and the inherited token variables are absent.
    /// </summary>
    /// <remarks>
    /// The merge process is launched with its OWN <c>ProcessStartInfo</c> (it must be killable on
    /// cancellation), so it never passes through the shared runner. The evidence is produced by a
    /// real <c>git</c> wrapper on the PATH: it records each launch's OWN environment before
    /// delegating to the real git, so the observation is the launched process's own view — not the
    /// parent's.
    /// </remarks>
    [Fact]
    public async Task RealGit_MergeProcess_ReceivesTheOperationEnvironment()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The recording git wrapper is a POSIX shell script; covered on Linux.");
            return;
        }

        var remoteDir = CreateLocalRemote();

        // A feature branch on the remote, so the merge has something to merge.
        var staging = Path.Combine(_tempDir, "feature-staging");
        Git(_tempDir, "clone", remoteDir, staging);
        Git(staging, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(staging, "feature.txt"), "feature\n");
        Git(staging, "add", "feature.txt");
        Git(staging, "commit", "-m", "Feature commit");
        Git(staging, "push", "origin", "feature");

        // The wrapper records every launch's environment and rewrites the network remote onto
        // the local one, so the whole operation stays offline and deterministic.
        var wrapperDir = Path.Combine(_tempDir, "wrapper-bin");
        Directory.CreateDirectory(wrapperDir);
        var launchLog = Path.Combine(_tempDir, "git-launches.log");
        WriteRecordingGitWrapper(wrapperDir, launchLog, remoteDir);

        // The clone's origin is the CONFIGURED url, and the configured-URL lookup answers with it,
        // so the refresh's identity check passes.
        SetGlobalGitConfig(string.Empty);

        var manager = CreateRealManager(Token);
        var clonePath = manager.GetClonePath(RepoName);
        Directory.CreateDirectory(clonePath);
        Git(clonePath, "init", "-b", "main");
        Git(clonePath, "config", "user.email", "probe@test.invalid");
        Git(clonePath, "config", "user.name", "Probe");
        Git(clonePath, "remote", "add", "origin", ConfiguredUrl);

        // The clone starts with the remote's main commit checked out, so the merge has a real
        // base. The remote-tracking ref is created by the wrapper's explicit refspec.
        Git(clonePath, "fetch", remoteDir, "+refs/heads/main:refs/remotes/origin/main");
        Git(clonePath, "checkout", "-B", "main", "origin/main");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", wrapperDir + Path.PathSeparator + originalPath);
        try
        {
            // MergeBranchAsync is the entry point whose merge is launched DIRECTLY (it must be
            // killable on cancellation), so it bypasses the shared git runner — and therefore has
            // to install the operation environment itself.
            //
            // The merge's own OUTCOME is not what this test asserts (the local fixture's merge
            // base is deliberately synthetic); what it asserts is the ENVIRONMENT of the merge
            // launch. The operation may therefore fail — the launch still happened.
            try
            {
                await manager.MergeBranchAsync(
                    RepoName, "feature", "main", TestContext.Current.CancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The merge's outcome is not the subject of this test.
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        // Premise: the wrapper really observed the launches.
        var recorded = ReadAllTextIfPresent(launchLog);
        Assert.NotEmpty(recorded);

        // The MERGE launch is identified by its OWN argument list, so the observation is
        // specifically about the process the manager launches directly — not about the other
        // launches of the same operation (which go through the shared runner).
        var mergeLaunch = FindLaunchBlock(recorded, "merge");

        // The merge process received the operation credential through the child-only variable…
        Assert.Contains($"CRED=[{Token}]", mergeLaunch, StringComparison.Ordinal);
        // …pointed at the token-free askpass helper…
        Assert.Contains(HostGitAskpassHelper.ScriptFileName, mergeLaunch, StringComparison.Ordinal);
        // …never in its ARGUMENT LIST…
        Assert.DoesNotContain(
            Token,
            string.Join('|', mergeLaunch.Split('\n')
                .Where(l => l.StartsWith("ARGV[", StringComparison.Ordinal))),
            StringComparison.Ordinal);
        // …and the inherited token variable was scrubbed from the child.
        Assert.Contains("GH=[]", mergeLaunch, StringComparison.Ordinal);
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

    // ── (d) Clone residual normalization, against a REAL git process ─────────

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
            + $"[url \"{remoteDir}\"]\n\tinsteadOf = {ConfiguredUrl}\n");

        var manager = CreateRealManager(Token);
        var clonePath = manager.GetClonePath(RepoName);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken));

        // Premise: the hook really ran, so the clone really failed AFTER creating the repository.
        Assert.True(File.Exists(marker), "The failing post-checkout hook never ran.");
        Assert.True(Directory.Exists(Path.Combine(clonePath, ".git")));

        // The PRIMARY failure is what the caller observes — not a cleanup error.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, ex.ToString(), StringComparison.Ordinal);

        // The residual origin was normalized to the sanitized URL: no token survives on disk.
        var config = ReadGitConfig(clonePath);
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
            + $"[url \"{remoteDir}\"]\n\tinsteadOf = {ConfiguredUrl}\n");

        var manager = CreateRealManager(Token);
        var clonePath = manager.GetClonePath(RepoName);

        using var cts = new CancellationTokenSource();
        var run = manager.EnsureCloneAsync(RepoName, ConfiguredUrl, "main", cts.Token);

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
            Assert.True(Directory.Exists(Path.Combine(clonePath, ".git")));

            // …and its origin was normalized. The cleanup could only have done that with a token
            // that was NOT the caller's cancelled one: the launcher's own pre-launch check would
            // have aborted the command otherwise.
            var config = ReadGitConfig(clonePath);
            Assert.Contains(SanitizedOrigin, config, StringComparison.Ordinal);
            Assert.DoesNotContain(StalePersistedToken, config, StringComparison.Ordinal);
            Assert.DoesNotContain("x-access-token", config, StringComparison.Ordinal);
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
            + $"[url \"{remoteDir}\"]\n\tinsteadOf = {ConfiguredUrl}\n");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = CreateRealManager(Token, logger);
        var clonePath = manager.GetClonePath(RepoName);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken));

        // Premise: the hook really ran, so the clone really failed after creating the repository.
        Assert.True(File.Exists(marker), "The failing post-checkout hook never ran.");
        Assert.True(Directory.Exists(Path.Combine(clonePath, ".git")));

        // The PRIMARY failure propagates — the cleanup never replaces it.
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);

        // A diagnostic WAS emitted…
        var diagnostic = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("residual origin", StringComparison.Ordinal));

        // …and it is credential-free in its message AND in its exception payload.
        Assert.DoesNotContain(Token, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", diagnostic.Message, StringComparison.Ordinal);
        var payload = diagnostic.Exception?.ToString() ?? string.Empty;
        Assert.DoesNotContain(Token, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(StalePersistedToken, payload, StringComparison.Ordinal);
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
            + "git remote set-url origin \"https://x-access-token:" + credential + "@github.com/acme/"
            + RepoName + ".git\"\n"
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
    /// Writes a <c>post-merge</c> hook that records the merge process's OWN view of its
    /// environment — the credential variable, the askpass path and the ambient token variable.
    /// </summary>
    private static void WriteEnvRecordingPostMergeHook(string hooksDir, string outputPath)
    {
        var scriptPath = Path.Combine(hooksDir, "post-merge");
        File.WriteAllText(
            scriptPath,
            "#!/bin/sh\n"
            + "{ printf 'cred=[%s] gh=[%s] askpass=[%s]\\n' "
            + "\"$COPILOTHIVE_GIT_ASKPASS_CREDENTIAL\" \"$GH_TOKEN\" \"$GIT_ASKPASS\"; } "
            + "> \"" + outputPath + "\"\n");
        SetExecutable(scriptPath);
    }

    /// <summary>
    /// Writes a <c>git</c> WRAPPER on the PATH that records EVERY launch's own argument list and
    /// environment before delegating to the real git. The network remote is rewritten onto a local
    /// bare repository — with an EXPLICIT refspec, so the remote-tracking refs are created exactly
    /// as they would be against the real remote — so the whole operation stays offline and
    /// deterministic.
    /// </summary>
    private static void WriteRecordingGitWrapper(
        string wrapperDir, string logPath, string localRemoteDir)
    {
        var realGit = FindRealGitExecutable();
        var scriptPath = Path.Combine(wrapperDir, "git");
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
            + "    args=(\"" + localRemoteDir + "\")\n"
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
            + "  push|ls-remote|clone)\n"
            + "    args=()\n"
            + "    for a in \"$@\"; do\n"
            + "      if [ \"$a\" = \"origin\" ] || [ \"$a\" = \"https://github.com/acme/"
            + RepoName + ".git\" ]; then args+=(\"" + localRemoteDir + "\"); else args+=(\"$a\"); fi\n"
            + "    done\n"
            + "    set -- \"${args[@]}\"\n"
            + "    ;;\n"
            + "esac\n"
            + "exec \"" + realGit + "\" \"$@\"\n");
        SetExecutable(scriptPath);
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
