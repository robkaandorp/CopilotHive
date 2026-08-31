using System.Diagnostics;
using System.Reflection;
using System.Text;

using CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Slice 2c-c-ii-a — the <c>WorkerService</c> askpass helper: the FULL chmod conditional (order,
/// exact count, the real 0o700 mode and the failure branch of the ownership-transfer guard), the
/// script's <c>$1</c>-prompt protocol proven by RUNNING the actual written file, the end-to-end
/// secrecy of the helper, and the idempotence of the seam's <c>onDispose</c> deletion.
/// </summary>
/// <remarks>
/// The chmod capture is an ordered, lock-guarded list plus an interlocked counter, so both the
/// COUNT and the ORDER are asserted. Tests that need real <c>sh</c> or real Unix modes are
/// Linux-gated so the Windows CI stays green. No delays, no polling, no timing assertions.
/// </remarks>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceAskpassHelperTests : IDisposable
{
    /// <summary>An ELIGIBLE (HTTPS github.com:443) config repo URL — its own sanitized form.</summary>
    private const string EligibleUrl = "https://github.com/org/config-repo.git";

    /// <summary>The env variable the helper script reads the credential from.</summary>
    private const string CredentialEnvName = "GITHUB_CONFIG_REPO_TOKEN";

    /// <summary>The fixed principal a USERNAME prompt must be answered with.</summary>
    private const string UsernamePrincipal = "x-access-token";

    /// <summary>The helper script's file name inside its own private directory.</summary>
    private const string ScriptName = "askpass.sh";

    /// <summary>Mode 0o700 — the only mode the helper and its directory may carry.</summary>
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// The EXACT expected helper-script text: git's <c>$1</c>-prompt protocol, TOKEN-FREE (the
    /// password branch references the environment variable rather than embedding a secret).
    /// </summary>
    private const string ExpectedScriptText =
        "#!/bin/sh\n"
        + "case \"$1\" in\n"
        + "  *sername*) printf '%s' \"x-access-token\" ;;\n"
        + "  *) printf '%s' \"$GITHUB_CONFIG_REPO_TOKEN\" ;;\n"
        + "esac\n";

    private readonly StringWriter _stdOut = new();
    private readonly StringWriter _stdErr = new();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalErr = Console.Error;
    private readonly string _root;

    public WorkerServiceAskpassHelperTests()
    {
        Console.SetOut(_stdOut);
        Console.SetError(_stdErr);

        _root = Path.Combine(Path.GetTempPath(), "copilothive-askpass-t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
        _stdOut.Dispose();
        _stdErr.Dispose();

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigRepoDir => Path.Combine(_root, "config-repo");

    // ==================================================================
    // 1. The FULL chmod conditional.
    // ==================================================================

    /// <summary>
    /// Predicate TRUE → the chmod runs EXACTLY TWICE and in the required order: the SCRIPT path
    /// first, then the DIRECTORY path. Both the interlocked count and the ordered capture are
    /// asserted, so swapping the two calls or adding/removing one fails this test.
    /// </summary>
    [Fact]
    public async Task ChmodPredicateTrue_RunsExactlyTwice_ScriptThenDirectory()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var capture = new OrderedChmodCapture();
        string? helperDir = null;

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };
        service.AskpassChmodPlatform = () => true;
        service.AskpassChmod = capture.Record;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-chmod-order", TestContext.Current.CancellationToken);

        Assert.NotNull(helperDir);

        // The COUNT, from the interlocked counter …
        Assert.Equal(2, capture.Count);

        // … and the exact ORDER, from the lock-guarded list.
        Assert.Equal(
            [Path.Combine(helperDir!, ScriptName), helperDir!],
            capture.Paths);

        // Stated separately so a reversed implementation fails on an explicit assertion too.
        Assert.EndsWith(ScriptName, capture.Paths[0], StringComparison.Ordinal);
        Assert.Equal(helperDir, capture.Paths[1]);
        Assert.NotEqual(capture.Paths[0], capture.Paths[1]);
    }

    /// <summary>Predicate FALSE → ZERO chmod calls, and the assignment still completes normally.</summary>
    [Fact]
    public async Task ChmodPredicateFalse_RunsZeroTimes()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var capture = new OrderedChmodCapture();

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassChmodPlatform = () => false;
        service.AskpassChmod = capture.Record;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-chmod-none", TestContext.Current.CancellationToken);

        Assert.Equal(0, capture.Count);
        Assert.Empty(capture.Paths);

        // The skip is not a failure: the assignment ran to completion.
        Assert.Equal(1, runner.PromptCount);
    }

    /// <summary>
    /// LINUX-GATED — the REAL chmod implementation (no <c>AskpassChmod</c> seam installed)
    /// applies 0o700 to BOTH the script and the directory, so nothing outside the owning account
    /// can read the helper or enumerate its directory.
    /// </summary>
    [Fact]
    public async Task RealChmod_AppliesOwnerOnly0700_ToScriptAndDirectory_Linux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Unix file modes are only meaningful on Linux/macOS.");

        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        UnixFileMode scriptMode = default;
        UnixFileMode dirMode = default;

        // Observed WHILE the executor runs: the seam still owns the helper at that point.
        var runner = new CallbackAgentRunner(() =>
        {
            if (helperDir is null || !OperatingSystem.IsLinux()) return;
            scriptMode = File.GetUnixFileMode(Path.Combine(helperDir, ScriptName));
            dirMode = File.GetUnixFileMode(helperDir);
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        // Only the directory create is intercepted (to capture the path); the write and BOTH
        // chmods are the real production implementations.
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-realmode", TestContext.Current.CancellationToken);

        Assert.Equal(OwnerOnly, scriptMode);
        Assert.Equal(OwnerOnly, dirMode);

        // Explicitly: no group or other bits at all.
        Assert.Equal(default, scriptMode & ~OwnerOnly);
        Assert.Equal(default, dirMode & ~OwnerOnly);
    }

    /// <summary>
    /// A chmod failure under a TRUE predicate takes the ownership-transfer guard's FAILURE
    /// branch, at EITHER chmod call site. <paramref name="failingCallIndex"/> selects which one
    /// throws: 0 is the SCRIPT chmod (the first call), 1 is the DIRECTORY chmod (the second) —
    /// the case where the first chmod already SUCCEEDED, so the failure lands mid-sequence.
    /// </summary>
    /// <remarks>
    /// In both cases <c>helperOwned</c> never becomes true, so ownership never transfers: the
    /// guarded cleanup deletes the directory RECURSIVELY (the script and a sentinel inside it go
    /// with it), and the seam and executor are never constructed.
    /// </remarks>
    [Theory]
    [InlineData(0)] // the SCRIPT chmod throws — nothing succeeded before it
    [InlineData(1)] // the DIRECTORY chmod throws — the script chmod ALREADY succeeded
    public async Task ChmodFailure_AtEitherCallSite_TakesTheGuardFailureBranch(int failingCallIndex)
    {
        var configRepoDir = Path.Combine(_root, "repo-chmodfail-" + failingCallIndex);
        Directory.CreateDirectory(configRepoDir);

        var launcher = new FakeGitLauncher(tokens => HealthyHandlerFor(configRepoDir, tokens));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        string? sentinel = null;
        var capture = new OrderedChmodCapture();
        var scriptExistedBeforeChmod = false;

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, configRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);

            // An extra entry inside the directory, so the cleanup must be RECURSIVE.
            sentinel = Path.Combine(dir, "sentinel.txt");
            File.WriteAllText(sentinel, "helper-scoped state");
        };
        service.AskpassChmodPlatform = () => true;
        service.AskpassChmod = path =>
        {
            // The script really was written before the chmod step, so the directory being
            // deleted below is genuinely non-empty.
            scriptExistedBeforeChmod |= File.Exists(Path.Combine(helperDir!, ScriptName));

            var index = capture.Count; // 0 for the script call, 1 for the directory call
            capture.Record(path);

            if (index == failingCallIndex)
                throw new UnauthorizedAccessException("chmod refused for " + Path.GetFileName(path));
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-chmod-fail-" + failingCallIndex, TestContext.Current.CancellationToken);

        Assert.True(scriptExistedBeforeChmod, "The script must be written before the chmod step.");

        // The failure landed at the intended call site: index 0 aborts after ONE call, index 1
        // only after the script chmod had already SUCCEEDED.
        Assert.Equal(failingCallIndex + 1, capture.Count);
        Assert.EndsWith(ScriptName, capture.Paths[0], StringComparison.Ordinal);
        if (failingCallIndex == 1)
        {
            Assert.Equal(helperDir, capture.Paths[1]);
            Assert.NotEqual(capture.Paths[0], capture.Paths[1]);
        }

        // The guard's failure branch ran: the directory and EVERYTHING in it are gone.
        Assert.NotNull(helperDir);
        Assert.False(Directory.Exists(helperDir), "The guard must delete the unowned helper directory.");
        Assert.False(File.Exists(Path.Combine(helperDir!, ScriptName)));
        Assert.NotNull(sentinel);
        Assert.False(File.Exists(sentinel), "The guard's delete must be recursive.");

        // Ownership never transferred, so the seam was never constructed and nothing ran.
        Assert.Equal(0, runner.PromptCount);
        Assert.Equal(0, launcher.CallCount);
    }

    // ==================================================================
    // 2. The $1-prompt protocol, proven by RUNNING the real script.
    // ==================================================================

    /// <summary>
    /// LINUX-GATED — the ACTUAL written script, executed by <c>/bin/sh</c>, implements git's
    /// <c>$1</c>-prompt protocol: a USERNAME prompt is answered with the fixed
    /// <c>x-access-token</c> principal and a PASSWORD prompt with the value supplied through the
    /// <c>GITHUB_CONFIG_REPO_TOKEN</c> CHILD environment variable — with no trailing newline in
    /// either answer (git reads the raw stdout).
    /// </summary>
    /// <remarks>
    /// The script is run IN PLACE, while the seam still owns it, so this asserts the real bytes
    /// at the real path — not a copy. The token is passed only in the child's environment block,
    /// so the test never mutates the process environment.
    /// </remarks>
    [Fact]
    public async Task RealScript_AnswersUsernameAndPasswordPrompts_Linux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("The $1-prompt protocol test needs a real POSIX shell.");

        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        const string token = "ghp_protocol_token_value";
        string? helperDir = null;
        ShellAnswer? usernameAnswer = null;
        ShellAnswer? passwordAnswer = null;
        ShellAnswer? emptyPromptAnswer = null;

        var runner = new CallbackAgentRunner(() =>
        {
            if (helperDir is null) return;
            var scriptPath = Path.Combine(helperDir, ScriptName);

            usernameAnswer = RunScript(scriptPath, "Username for 'https://github.com': ", token);
            passwordAnswer = RunScript(scriptPath, "Password for 'https://github.com': ", token);
            emptyPromptAnswer = RunScript(scriptPath, "", token);
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-protocol", TestContext.Current.CancellationToken);

        Assert.NotNull(usernameAnswer);
        Assert.NotNull(passwordAnswer);
        Assert.NotNull(emptyPromptAnswer);

        // The USERNAME branch — the `*sername*` case, matching both "Username" and "username".
        Assert.Equal(0, usernameAnswer!.ExitCode);
        Assert.Equal(UsernamePrincipal, usernameAnswer.Stdout);

        // The PASSWORD branch — the catch-all case, answering with the CHILD env value.
        Assert.Equal(0, passwordAnswer!.ExitCode);
        Assert.Equal(token, passwordAnswer.Stdout);

        // The two branches are genuinely different answers.
        Assert.NotEqual(usernameAnswer.Stdout, passwordAnswer.Stdout);

        // Any other prompt (including an empty one) falls to the same catch-all.
        Assert.Equal(token, emptyPromptAnswer!.Stdout);
    }

    /// <summary>
    /// LINUX-GATED — the password branch reads the credential LIVE from the environment: with no
    /// <c>GITHUB_CONFIG_REPO_TOKEN</c> in the child environment the script answers with the empty
    /// string, which proves the value is not baked into the file.
    /// </summary>
    [Fact]
    public async Task RealScript_WithoutCredentialEnv_AnswersEmpty_Linux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("The $1-prompt protocol test needs a real POSIX shell.");

        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        ShellAnswer? passwordAnswer = null;
        ShellAnswer? usernameAnswer = null;

        var runner = new CallbackAgentRunner(() =>
        {
            if (helperDir is null) return;
            var scriptPath = Path.Combine(helperDir, ScriptName);
            passwordAnswer = RunScript(scriptPath, "Password for 'https://github.com': ", token: null);
            usernameAnswer = RunScript(scriptPath, "Username for 'https://github.com': ", token: null);
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-protocol-noenv", TestContext.Current.CancellationToken);

        Assert.NotNull(passwordAnswer);
        Assert.Equal(0, passwordAnswer!.ExitCode);
        Assert.Equal("", passwordAnswer.Stdout);

        // The USERNAME branch is env-independent — its answer is the fixed principal.
        Assert.NotNull(usernameAnswer);
        Assert.Equal(UsernamePrincipal, usernameAnswer!.Stdout);
    }

    // ==================================================================
    // 3. End-to-end secrecy of the helper.
    // ==================================================================

    /// <summary>
    /// The written helper is TOKEN-FREE: the resolved credential appears NOWHERE in the file's
    /// bytes (nor anywhere else in the helper directory), and the password branch carries only
    /// the ENV-VARIABLE REFERENCE.
    /// </summary>
    [Fact]
    public async Task WrittenScript_ContainsNoCredentialLiteral_OnlyTheEnvReference()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        // A highly distinctive credential, so a leak anywhere is unmistakable.
        const string token = "ghp_SECRET_LITERAL_MUST_NEVER_BE_WRITTEN";

        string? helperDir = null;
        byte[]? scriptBytes = null;
        string[] helperEntries = [];

        var runner = new CallbackAgentRunner(() =>
        {
            if (helperDir is null) return;
            scriptBytes = File.ReadAllBytes(Path.Combine(helperDir, ScriptName));
            helperEntries = Directory.GetFileSystemEntries(helperDir);
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, token).Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-secrecy", TestContext.Current.CancellationToken);

        Assert.NotNull(scriptBytes);

        var text = Encoding.UTF8.GetString(scriptBytes!);

        // No credential literal, in any form.
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", text, StringComparison.Ordinal);

        // The password branch references the ENVIRONMENT instead — the un-expanded form.
        Assert.Contains("\"$" + CredentialEnvName + "\"", text, StringComparison.Ordinal);

        // The username branch carries the fixed, non-secret principal.
        Assert.Contains(UsernamePrincipal, text, StringComparison.Ordinal);

        // The helper directory holds the script and NOTHING else — no side files to leak into.
        Assert.Equal([Path.Combine(helperDir!, ScriptName)], helperEntries);

        // Nor did the credential reach the process output.
        Assert.DoesNotContain(token, _stdOut.ToString() + _stdErr.ToString(), StringComparison.Ordinal);
    }

    // ==================================================================
    // 4. End-to-end secrecy THROUGH THE REAL PRODUCTION WIRING.
    // ==================================================================

    /// <summary>
    /// The credential-carrying launch is inspected AS PRODUCTION BUILT IT. An absent repo drives
    /// the REAL <c>ConfigRepoGitOperations.CloneAsync</c> against an ELIGIBLE URL, so the seam
    /// resolves both the credential and the helper path through the production resolvers that
    /// <c>WorkerService</c> wired up. The COMPLETE request is captured, and the three pairing
    /// facts are asserted together:
    /// <list type="number">
    ///   <item><description><c>GIT_ASKPASS</c> equals the EXACT path of the helper file
    ///   <c>WorkerService</c> generated — not merely "some path".</description></item>
    ///   <item><description>That file's BYTES, read at launch time through the captured
    ///   <c>GIT_ASKPASS</c> path itself, hold no token literal — only the
    ///   <c>GITHUB_CONFIG_REPO_TOKEN</c> reference.</description></item>
    ///   <item><description>The provisioner credential appears ONLY in the injected
    ///   <c>GITHUB_CONFIG_REPO_TOKEN</c> variable of that ONE launch — never in an argument,
    ///   never in the origin URL, never in another request, never in a log line.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// This closes the gap a token-only capture leaves: that would still pass if the service
    /// handed the seam the WRONG helper path, or if the credential and the generated
    /// <c>GIT_ASKPASS</c> path were never paired on the same launch. Here the helper is read
    /// THROUGH the very path production injected, so a mismatch cannot go unnoticed.
    /// </remarks>
    [Fact]
    public async Task EligibleClone_PairsTheGeneratedAskpassPathWithTheProvisionerCredential()
    {
        // The credential is supplied ONLY through the provisioner — the seam reads it via the
        // production resolver chain, exactly as it does in the container.
        const string credential = "ghp_end_to_end_credential_value";

        // A directory that does NOT exist yet, so the health probe fails and the CLONE runs.
        var configRepoDir = Path.Combine(_root, "clone-target");

        // Captured from the LAUNCH ITSELF: the env the seam built for the clone, and the bytes
        // read back through the GIT_ASKPASS path that env carried.
        string? askpassFromLaunch = null;
        byte[]? helperBytesAtLaunch = null;

        FakeGitLauncher? launcher = null;
        launcher = new FakeGitLauncher(tokens =>
        {
            if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(128, "", "fatal: not a git repository");

            if (Matches(tokens, "clone"))
            {
                // The launcher records the request BEFORE invoking this handler, so the live
                // env of THIS launch is already available.
                askpassFromLaunch = launcher!.Launches[^1].EnvValue("GIT_ASKPASS");
                if (askpassFromLaunch is not null && File.Exists(askpassFromLaunch))
                    helperBytesAtLaunch = File.ReadAllBytes(askpassFromLaunch);
            }

            return new GitProcessResult(0, "", "");
        });

        using var _restore = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, configRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, credential).Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-e2e-secrecy", TestContext.Current.CancellationToken);

        Assert.NotNull(helperDir);
        var expectedAskpass = Path.Combine(helperDir!, ScriptName);

        // ── (1) GIT_ASKPASS points at the EXACT generated helper file ──────────────────
        var clone = launcher.Single("clone");
        Assert.Equal(expectedAskpass, clone.EnvValue("GIT_ASKPASS"));
        Assert.Equal(expectedAskpass, askpassFromLaunch);

        // The clone really is the credential-carrying, eligible launch.
        Assert.Equal(credential, clone.EnvValue(CredentialEnvName));

        // ── (2) The helper file's BYTES carry no token, only the env reference ─────────
        //        They were read THROUGH the injected GIT_ASKPASS path, so this also proves the
        //        path production supplied actually resolves to the generated helper.
        Assert.NotNull(helperBytesAtLaunch);
        var helperText = Encoding.UTF8.GetString(helperBytesAtLaunch!);
        Assert.Equal(ExpectedScriptText, helperText);
        Assert.DoesNotContain(credential, helperText, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", helperText, StringComparison.Ordinal);
        Assert.Contains("\"$" + CredentialEnvName + "\"", helperText, StringComparison.Ordinal);

        // ── (3) The credential appears ONLY in GITHUB_CONFIG_REPO_TOKEN, on that ONE launch ──
        Assert.Equal([CredentialEnvName], clone.EnvNamesContaining(credential));

        // Not in ANY argument of the clone …
        Assert.False(clone.AnyTokenContains(credential), "No argument may carry the credential.");

        // … and specifically not in the origin URL the clone writes: the tokens carry the
        // SANITIZED, credential-free URL.
        Assert.Contains(EligibleUrl, clone.Tokens);
        Assert.DoesNotContain(clone.Tokens, t => t.Contains("@github.com", StringComparison.Ordinal));

        // No OTHER launch carries the credential — or the helper — anywhere.
        foreach (var other in launcher.Launches.Where(l => !l.StartsWith("clone")))
        {
            Assert.False(
                other.AnyTokenContains(credential),
                $"Launch [{string.Join(' ', other.Tokens)}] must not carry the credential in its args.");
            Assert.Empty(other.EnvNamesContaining(credential));
            Assert.Null(other.EnvValue(CredentialEnvName));
            Assert.Null(other.EnvValue("GIT_ASKPASS"));
        }

        // Exactly ONE launch in the whole assignment carried the credential.
        Assert.Single(launcher.Launches, l => l.EnvNamesContaining(credential).Count > 0);

        // ── And nothing leaked into the logs ──────────────────────────────────────────
        var output = _stdOut.ToString() + _stdErr.ToString();
        Assert.DoesNotContain(credential, output, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// LINUX-GATED — the pairing is proven FUNCTIONALLY: the helper path and the credential env
    /// captured from the REAL clone request are fed to <c>/bin/sh</c> exactly as git would, and
    /// the password prompt answers with the PROVISIONER's credential. This ties the generated
    /// script, the injected <c>GIT_ASKPASS</c> path and the injected token together in one
    /// end-to-end execution.
    /// </summary>
    [Fact]
    public async Task EligibleClone_CapturedAskpassAndTokenAnswerThePasswordPrompt_Linux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("The $1-prompt protocol test needs a real POSIX shell.");

        const string credential = "ghp_paired_runtime_credential";
        var configRepoDir = Path.Combine(_root, "clone-target-exec");

        ShellAnswer? passwordAnswer = null;
        ShellAnswer? usernameAnswer = null;

        FakeGitLauncher? launcher = null;
        launcher = new FakeGitLauncher(tokens =>
        {
            if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(128, "", "fatal: not a git repository");

            if (Matches(tokens, "clone"))
            {
                // Replay git's own behaviour: run the program named by GIT_ASKPASS, with the
                // credential taken from the SAME captured environment block.
                var launch = launcher!.Launches[^1];
                var askpass = launch.EnvValue("GIT_ASKPASS");
                var token = launch.EnvValue(CredentialEnvName);

                if (askpass is not null && File.Exists(askpass))
                {
                    passwordAnswer = RunScript(askpass, "Password for 'https://github.com': ", token);
                    usernameAnswer = RunScript(askpass, "Username for 'https://github.com': ", token);
                }
            }

            return new GitProcessResult(0, "", "");
        });

        using var _restore = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, configRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, credential).Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-e2e-exec", TestContext.Current.CancellationToken);

        // The helper named by the production GIT_ASKPASS answered the prompts …
        Assert.NotNull(passwordAnswer);
        Assert.Equal(0, passwordAnswer!.ExitCode);

        // … with the PROVISIONER's credential for the password prompt …
        Assert.Equal(credential, passwordAnswer.Stdout);

        // … and the fixed principal for the username prompt.
        Assert.NotNull(usernameAnswer);
        Assert.Equal(UsernamePrincipal, usernameAnswer!.Stdout);

        // The credential never reached the arguments or the logs.
        var clone = launcher.Single("clone");
        Assert.False(clone.AnyTokenContains(credential));
        Assert.DoesNotContain(credential, _stdOut.ToString() + _stdErr.ToString(), StringComparison.Ordinal);
    }

    // ==================================================================
    // 5. The onDispose deletion is idempotent.
    // ==================================================================

    /// <summary>
    /// The <c>onDispose</c> action deletes the helper directory EXACTLY ONCE: a second invocation
    /// is a no-op that neither throws nor deletes a directory that has since reappeared at the
    /// same path.
    /// </summary>
    /// <remarks>
    /// Re-creating the directory between the two invocations is what makes this removal-proof:
    /// without the interlocked guard the second call would delete the re-created directory, and
    /// the final assertion would fail.
    /// </remarks>
    [Fact]
    public void OnDisposeCleanup_DeletesOnce_SecondInvocationIsNoOp()
    {
        var helperDir = Path.Combine(_root, "cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDir);
        File.WriteAllText(Path.Combine(helperDir, ScriptName), "#!/bin/sh\n");

        var cleanup = BuildHelperDirCleanup(helperDir);

        // First invocation — deletes the directory and its contents.
        cleanup();
        Assert.False(Directory.Exists(helperDir));

        // A directory reappears at the SAME path (a later assignment could reuse it).
        Directory.CreateDirectory(helperDir);
        var sentinel = Path.Combine(helperDir, "sentinel.txt");
        File.WriteAllText(sentinel, "later owner");

        // Second invocation — a NO-OP: it must not throw and must not touch the new directory.
        cleanup();

        Assert.True(Directory.Exists(helperDir), "The second invocation must not delete again.");
        Assert.True(File.Exists(sentinel));
        Assert.Equal("later owner", File.ReadAllText(sentinel));
    }

    /// <summary>
    /// The cleanup is BEST-EFFORT: invoking it for a directory that never existed completes
    /// silently rather than surfacing a double-delete style exception.
    /// </summary>
    [Fact]
    public void OnDisposeCleanup_MissingDirectory_DoesNotThrow()
    {
        var missing = Path.Combine(_root, "never-created-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        var cleanup = BuildHelperDirCleanup(missing);

        cleanup();
        cleanup();

        Assert.False(Directory.Exists(missing));
    }

    /// <summary>
    /// End to end: after a FULL-SUCCESS construction the seam's disposal removes the helper
    /// directory, and no double-delete fault ever surfaces on the assignment's error path.
    /// </summary>
    [Fact]
    public async Task FullSuccess_SeamDisposalRemovesHelper_WithNoFaultLogged()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        var aliveDuringExecution = false;

        var runner = new CallbackAgentRunner(() =>
            aliveDuringExecution = helperDir is not null && Directory.Exists(helperDir));

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-dispose", TestContext.Current.CancellationToken);

        Assert.True(aliveDuringExecution, "The helper must outlive the executor's run.");
        Assert.NotNull(helperDir);
        Assert.False(Directory.Exists(helperDir), "Disposal must remove the helper directory.");

        // The assignment completed through the SUCCESS path — no generic failure was logged.
        Assert.DoesNotContain(
            "Task execution failed", _stdErr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Task drain observed a fault", _stdErr.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, runner.PromptCount);
    }

    // ==================================================================
    // Helpers.
    // ==================================================================

    /// <summary>
    /// Invokes the production <c>WorkerService.BuildHelperDirCleanup</c> factory and returns the
    /// <c>onDispose</c> action it hands to the seam — the exact delegate production installs.
    /// </summary>
    private static Action BuildHelperDirCleanup(string helperDir)
    {
        var method = typeof(WorkerService).GetMethod(
            "BuildHelperDirCleanup", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Action)method.Invoke(null, [helperDir])!;
    }

    /// <summary>The stdout and exit code of one helper-script invocation.</summary>
    private sealed record ShellAnswer(int ExitCode, string Stdout);

    /// <summary>
    /// Runs the helper script through <c>/bin/sh</c> with <paramref name="prompt"/> as
    /// <c>$1</c>, supplying <paramref name="token"/> (when non-null) ONLY in the child's
    /// environment block. Synchronous and fully bounded — it waits for the real exit.
    /// </summary>
    private static ShellAnswer RunScript(string scriptPath, string prompt, string? token)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(prompt);

        // The credential is a CHILD-ONLY variable: the test process environment is never mutated.
        psi.Environment.Remove(CredentialEnvName);
        if (token is not null)
            psi.Environment[CredentialEnvName] = token;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ShellAnswer(process.ExitCode, stdout);
    }

    /// <summary>
    /// Records chmod call paths in ORDER under a lock, alongside an interlocked counter, so the
    /// count and the sequence can be asserted independently.
    /// </summary>
    private sealed class OrderedChmodCapture
    {
        private readonly object _gate = new();
        private readonly List<string> _paths = [];
        private int _count;

        /// <summary>The number of calls, from the interlocked counter.</summary>
        internal int Count => Volatile.Read(ref _count);

        /// <summary>The call paths, in invocation order.</summary>
        internal IReadOnlyList<string> Paths
        {
            get { lock (_gate) return [.. _paths]; }
        }

        internal void Record(string path)
        {
            Interlocked.Increment(ref _count);
            lock (_gate) _paths.Add(path);
        }
    }

    /// <summary>A repo that probes HEALTHY, with a matching, credential-free origin.</summary>
    private GitProcessResult HealthyRepoHandler(IReadOnlyList<string> tokens) =>
        HealthyHandlerFor(ConfigRepoDir, tokens);

    /// <summary>The healthy-probe handler for an arbitrary config repo directory.</summary>
    private static GitProcessResult HealthyHandlerFor(string repoDir, IReadOnlyList<string> tokens)
    {
        if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
            return new GitProcessResult(0, "true\n", "");

        if (Matches(tokens, "rev-parse", "--show-toplevel"))
            return new GitProcessResult(0, repoDir + "\n", "");

        if (Matches(tokens, "remote", "get-url", "origin"))
            return new GitProcessResult(0, EligibleUrl + "\n", "");

        return new GitProcessResult(0, "", "");
    }

    private static bool Matches(IReadOnlyList<string> tokens, params string[] prefix)
    {
        if (tokens.Count < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
