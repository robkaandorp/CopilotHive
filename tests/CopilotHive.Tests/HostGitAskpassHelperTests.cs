using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CopilotHive.Git;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests;

/// <summary>
/// The HOST-side credential-at-rest elimination contract for
/// <see cref="HostGitAskpassHelper"/>: a per-operation private directory holding a
/// <b>TOKEN-FREE</b> git askpass script, plus the child-only environment that carries the resolved
/// credential into one launch.
/// <para>
/// Every assertion here is about what actually lands on disk and what a CHILD PROCESS actually
/// receives — no production-seam stubbing of the helper itself — so a helper that wrote the token
/// into the script, that skipped the inherited-helper reset, or that leaked its directory fails.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class HostGitAskpassHelperTests : IDisposable
{
    private const string Token = "ghp_host_askpass_secret";

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGitHubToken;

    public HostGitAskpassHelperTests()
    {
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-askpass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGitHubToken);
        TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    /// <summary>Runs a POSIX shell script and returns (exitCode, stdout, stderr).</summary>
    private static (int ExitCode, string Stdout, string Stderr) RunShell(
        string scriptPath, IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(scriptPath);
        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

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

    // ── Creation: private directory, token-free script, owner-only modes ──────

    /// <summary>
    /// The helper creates its OWN private directory and a script whose bytes are identical for
    /// every operation: a token passed to <c>BuildChildEnvironment</c> must never appear in the
    /// script FILE.
    /// </summary>
    [Fact]
    public void TryCreate_WritesATokenFreeScript()
    {
        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        Assert.True(Directory.Exists(helper!.DirectoryPath));
        Assert.True(File.Exists(helper.ScriptPath));
        Assert.Equal(Path.Combine(helper.DirectoryPath, HostGitAskpassHelper.ScriptFileName), helper.ScriptPath);

        // Build the environment with a credential, then prove the FILE still contains nothing.
        _ = helper.BuildChildEnvironment(Token);
        var bytes = File.ReadAllBytes(helper.ScriptPath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token:", text, StringComparison.Ordinal);

        // The script reads the credential from the CHILD-ONLY variable instead.
        Assert.Contains($"${HostGitAskpassHelper.CredentialEnvName}", text, StringComparison.Ordinal);

        // No BOM, and `#!` is the first two bytes so /bin/sh treats it as a script.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal((byte)'!', bytes[1]);
    }

    /// <summary>The helper directory and script are owner-only (0700) on Linux.</summary>
    [Fact]
    public void TryCreate_AppliesOwnerOnlyModes()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The helper is Linux-only; POSIX owner-only modes are asserted on Linux.");
            return;
        }

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(expected, File.GetUnixFileMode(helper!.ScriptPath));
        Assert.Equal(expected, File.GetUnixFileMode(helper.DirectoryPath));
    }

    // ── The script implements git's $1-prompt protocol ────────────────────────

    /// <summary>
    /// The script answers git's two prompt shapes: a USERNAME prompt with the fixed principal and
    /// every other prompt with the credential from the child-only variable.
    /// </summary>
    [Fact]
    public void Script_ImplementsGitsDollarOnePromptProtocol()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Executing the /bin/sh askpass script is a Linux-only contract.");
            return;
        }

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [HostGitAskpassHelper.CredentialEnvName] = Token,
        };

        var username = RunShell(helper!.ScriptPath, environment, "Username for 'https://github.com': ");
        Assert.Equal(0, username.ExitCode);
        Assert.Equal("x-access-token", username.Stdout);

        var password = RunShell(
            helper.ScriptPath, environment, "Password for 'https://x-access-token@github.com': ");
        Assert.Equal(0, password.ExitCode);
        Assert.Equal(Token, password.Stdout);
    }

    // ── The child environment ────────────────────────────────────────────────

    /// <summary>
    /// The child environment is the COMPLETE launch environment: the operation-scoped askpass
    /// wiring, prompt disabled, the credential in its own variable, and NOTHING of the inherited
    /// credential channels.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_ScrubsEveryInheritedCredentialChannel()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "inherited-gh");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "inherited-github");
        Environment.SetEnvironmentVariable("GIT_ASKPASS", "/tmp/inherited-askpass");
        Environment.SetEnvironmentVariable("GCM_INTERACTIVE", "always");
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "1");
        Environment.SetEnvironmentVariable("SSH_ASKPASS", "/tmp/inherited-ssh-askpass");
        Environment.SetEnvironmentVariable("GITHUB_CONFIG_REPO_TOKEN", "inherited-config-repo");

        try
        {
            using var helper = HostGitAskpassHelper.TryCreate();
            Assert.NotNull(helper);

            var environment = helper!.BuildChildEnvironment(Token);

            foreach (var scrubbed in new[]
                     {
                         "GH_TOKEN", "GITHUB_TOKEN", "GITHUB_CONFIG_REPO_TOKEN", "SSH_ASKPASS",
                     })
            {
                Assert.False(environment.ContainsKey(scrubbed), $"'{scrubbed}' survived the scrub.");
            }

            // The operation-scoped entries are the ONLY values for these names.
            Assert.Equal("0", environment[HostGitAskpassHelper.PromptDisabledEnvName]);
            // An inherited `always` cannot re-enable a credential prompt for this invocation.
            Assert.Equal(
                HostGitAskpassHelper.GcmInteractiveNever,
                environment[HostGitAskpassHelper.GcmInteractiveEnvName]);
            Assert.Equal(helper.ScriptPath, environment[HostGitAskpassHelper.AskpassEnvName]);
            Assert.Equal(Token, environment[HostGitAskpassHelper.CredentialEnvName]);

            // …and no inherited value leaked into ANY value.
            foreach (var (_, value) in environment)
            {
                Assert.DoesNotContain("inherited-gh", value);
                Assert.DoesNotContain("inherited-github", value);
                Assert.DoesNotContain("inherited-config-repo", value);
                Assert.DoesNotContain("inherited-askpass", value);
                Assert.DoesNotContain("inherited-ssh-askpass", value);
            }
        }
        finally
        {
            foreach (var name in new[]
                     {
                         "GH_TOKEN", "GITHUB_TOKEN", "GIT_ASKPASS", "GCM_INTERACTIVE",
                         "GIT_TERMINAL_PROMPT", "SSH_ASKPASS", "GITHUB_CONFIG_REPO_TOKEN",
                     })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    /// <summary>
    /// The operation-scoped git config resets are APPENDED after any inherited
    /// <c>GIT_CONFIG_COUNT</c> entries, so they win. The count and index numbering are exact.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_AppendsHelperResetsAfterInheritedConfigEntries()
    {
        var originalCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "credential.helper");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "/usr/bin/recording-helper");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "core.pager");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", "cat");

        try
        {
            using var helper = HostGitAskpassHelper.TryCreate();
            Assert.NotNull(helper);

            var environment = helper!.BuildChildEnvironment(Token);

            // Two inherited entries plus the two operation-scoped ones.
            Assert.Equal("4", environment[HostGitAskpassHelper.ConfigCountEnvName]);

            // Appended LAST — highest precedence for this invocation.
            Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_2"]);
            Assert.Equal(string.Empty, environment["GIT_CONFIG_VALUE_2"]);
            Assert.Equal("credential.interactive", environment["GIT_CONFIG_KEY_3"]);
            Assert.Equal("false", environment["GIT_CONFIG_VALUE_3"]);

            // The INHERITED entries are untouched; nothing was rewritten.
            Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_0"]);
            Assert.Equal("/usr/bin/recording-helper", environment["GIT_CONFIG_VALUE_0"]);
            Assert.Equal("core.pager", environment["GIT_CONFIG_KEY_1"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", originalCount);
            foreach (var name in new[]
                     {
                         "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1",
                         "GIT_CONFIG_VALUE_1",
                     })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    /// <summary>With no inherited config entries the resets start at index 0.</summary>
    [Fact]
    public void BuildChildEnvironment_WithoutInheritedConfig_StartsAtZero()
    {
        var originalCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);

        try
        {
            using var helper = HostGitAskpassHelper.TryCreate();
            Assert.NotNull(helper);

            var environment = helper!.BuildChildEnvironment(Token);

            Assert.Equal("2", environment[HostGitAskpassHelper.ConfigCountEnvName]);
            Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_0"]);
            Assert.Equal(string.Empty, environment["GIT_CONFIG_VALUE_0"]);
            Assert.Equal("credential.interactive", environment["GIT_CONFIG_KEY_1"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", originalCount);
        }
    }

    /// <summary>
    /// A recording credential helper installed in REAL git configuration is NOT reached by a child
    /// launched with this environment: neither its <c>get</c> (selection) nor its <c>store</c>
    /// (persistence) receives anything, and the token never reaches it.
    /// </summary>
    /// <remarks>
    /// The control below proves the recorder IS reachable without the reset, so this is a
    /// precedence assertion rather than a vacuous absence.
    /// </remarks>
    [Fact]
    public void RecordingCredentialHelper_CannotSelectOrPersistTheToken()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The recording helper is a POSIX shell script; covered on Linux.");
            return;
        }

        var recorderScript = Path.Combine(_tempDir, "recorder.sh");
        var recordLog = Path.Combine(_tempDir, "recorder.log");
        File.WriteAllText(
            recorderScript,
            "#!/bin/sh\n"
            + $"printf 'CALLED %s\\n' \"$1\" >> \"{recordLog}\"\n"
            + "cat > /dev/null\n"
            + "exit 0\n");

        // git executes the helper directly, so it must be executable for the CONTROL below to be
        // able to observe it at all.
        File.SetUnixFileMode(
            recorderScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var gitConfig = Path.Combine(_tempDir, "gitconfig");
        File.WriteAllText(gitConfig, $"[credential]\n\thelper = {recorderScript}\n");

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        var environment = new Dictionary<string, string?>(helper!.BuildChildEnvironment(Token))
        {
            ["GIT_CONFIG_GLOBAL"] = gitConfig,
            // Keep the real user config out of the picture for this run.
            ["HOME"] = _tempDir,
        };

        // ── FILL (get): the helper is asked for a credential ──────────────────
        var fill = RunGitWithStdin(
            environment,
            "protocol=https\nhost=github.com\n\n",
            "credential",
            "fill");

        Assert.Equal(0, fill.ExitCode);
        Assert.Contains($"password={Token}", fill.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLED get", ReadLog(recordLog), StringComparison.Ordinal);

        // ── APPROVE (store): git would persist the credential ─────────────────
        var approve = RunGitWithStdin(
            environment,
            $"protocol=https\nhost=github.com\nusername=x-access-token\npassword={Token}\n\n",
            "credential",
            "approve");

        Assert.Equal(0, approve.ExitCode);
        var log = ReadLog(recordLog);
        Assert.DoesNotContain("CALLED store", log, StringComparison.Ordinal);
        // The recorder never received the secret through ANY stream it could observe.
        Assert.DoesNotContain(Token, log, StringComparison.Ordinal);

        // ── CONTROL: the SAME recording helper without the reset IS reached ────
        var controlEnvironment = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        controlEnvironment.Remove(HostGitAskpassHelper.ConfigCountEnvName);
        controlEnvironment.Remove("GIT_CONFIG_KEY_0");
        controlEnvironment.Remove("GIT_CONFIG_VALUE_0");
        controlEnvironment.Remove("GIT_CONFIG_KEY_1");
        controlEnvironment.Remove("GIT_CONFIG_VALUE_1");
        // The operation also replaces GIT_CONFIG_PARAMETERS (the channel git evaluates last); the
        // control must drop THAT reset too, or its premise ("no operation-scoped reset") is false.
        controlEnvironment.Remove(HostGitAskpassHelper.ConfigParametersEnvName);

        var control = RunGitWithStdin(
            controlEnvironment,
            "protocol=https\nhost=github.com\n\n",
            "credential",
            "fill");

        Assert.Equal(0, control.ExitCode);
        Assert.Contains(
            "CALLED get",
            ReadLog(recordLog),
            StringComparison.Ordinal);
    }

    private static string ReadLog(string path)
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

    /// <summary>Runs <c>git</c> with an explicit environment and stdin, capturing both streams.</summary>
    private static (int ExitCode, string Stdout, string Stderr) RunGitWithStdin(
        IReadOnlyDictionary<string, string?> environment, string stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

        psi.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (value is not null)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    /// <summary>Disposal removes the private directory, and repeated disposal is safe.</summary>
    [Fact]
    public void Dispose_DeletesTheHelperDirectory_AndIsIdempotent()
    {
        var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        var directory = helper!.DirectoryPath;
        Assert.True(Directory.Exists(directory));

        helper.Dispose();
        Assert.False(Directory.Exists(directory));

        // A second disposal must not throw, and must not resurrect anything.
        helper.Dispose();
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// Disposal never throws even when the directory has already vanished underneath it, so a
    /// leaked/best-effort cleanup cannot fail the git operation that owned it.
    /// </summary>
    [Fact]
    public void Dispose_WhenDirectoryAlreadyGone_DoesNotThrow()
    {
        var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        Directory.Delete(helper!.DirectoryPath, recursive: true);

        helper.Dispose();
        Assert.False(Directory.Exists(helper.DirectoryPath));
    }

    // ── The environment is installed on the REAL child process ───────────────

    /// <summary>
    /// The REAL-process runner installs the operation environment on the actual child: a script
    /// launched through <c>RunGitForTestAsync</c> reports the operation's variables and the ABSENCE
    /// of the inherited ones, which no seam observation could prove on its own.
    /// </summary>
    /// <remarks>
    /// A <c>!&lt;shell&gt;</c> git alias prints the child's own view of the environment, so the output
    /// is produced by the child process, not by the parent.
    /// </remarks>
    [Fact]
    public async Task RealChildProcess_ReceivesTheOperationEnvironment()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The git alias probe is a POSIX shell command; covered on Linux.");
            return;
        }

        var manager = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());
        var workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        using var auth = new BrainGitAuth(Token, "https://github.com/acme/widgets.git", helper!);

        string[] probeArgs =
        [
            "-c",
            "alias.hiveenv=!printf 'cred=%s;askpass=%s;prompt=%s;gh=%s;github=%s;helper0=[%s]' "
            + "\"$COPILOTHIVE_GIT_ASKPASS_CREDENTIAL\" \"$GIT_ASKPASS\" \"$GIT_TERMINAL_PROMPT\" "
            + "\"$GH_TOKEN\" \"$GITHUB_TOKEN\" \"$GIT_CONFIG_VALUE_0\"",
            "hiveenv",
        ];

        var (exitCode, stdout, stderr) = await manager.RunGitForTestAsync(
            workDir, probeArgs, TestContext.Current.CancellationToken, auth);

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stderr), stderr);

        // The credential reached the CHILD through its own variable…
        Assert.Contains($"cred={Token}", stdout, StringComparison.Ordinal);
        // …the askpass path points at the token-free helper…
        Assert.Contains($"askpass={helper.ScriptPath}", stdout, StringComparison.Ordinal);
        // …prompting is off…
        Assert.Contains("prompt=0", stdout, StringComparison.Ordinal);
        // …the inherited token variables are ABSENT…
        Assert.Contains("gh=;github=", stdout, StringComparison.Ordinal);
        // …and the inherited credential helper list is RESET for this invocation.
        Assert.Contains("helper0=[]", stdout, StringComparison.Ordinal);
    }


    // ── GIT_CONFIG_PARAMETERS outranks the indexed reset: it must be REPLACED ─────────────

    /// <summary>Writes an executable recording credential helper; returns (script, log).</summary>
    private (string Script, string Log) WriteRecorder(string name)
    {
        var script = Path.Combine(_tempDir, $"{name}.sh");
        var log = Path.Combine(_tempDir, $"{name}.log");
        File.WriteAllText(
            script,
            "#!/bin/sh\n"
            + $"printf 'CALLED %s\\n' \"$1\" >> \"{log}\"\n"
            + $"cat >> \"{log}\"\n"
            + "exit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return (script, log);
    }

    /// <summary>
    /// A helper supplied through the INHERITED <c>GIT_CONFIG_PARAMETERS</c> (git evaluates it AFTER the
    /// indexed entries) is neither asked to SELECT a credential nor handed the token to PERSIST. The
    /// positive controls prove the recorder IS reached, and DOES receive the token, when the
    /// operation-scoped environment is not applied.
    /// </summary>
    [Theory]
    [InlineData("credential.helper")]
    [InlineData("credential.https://github.com.helper")]
    public void InheritedConfigParametersHelper_CannotSelectOrPersistTheToken(string key)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The recording helper is a POSIX shell script; covered on Linux.");
            return;
        }

        var (recorder, log) = WriteRecorder("params-recorder");
        var inherited = $"'{key}={recorder}'";

        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);

        // The OPERATION environment, built while the host's environment carries the helper in
        // GIT_CONFIG_PARAMETERS (the real inheritance path).
        var original = Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS");
        Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", inherited);
        IReadOnlyDictionary<string, string?> operationEnv;
        try
        {
            operationEnv = helper!.BuildChildEnvironment(Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", original);
        }

        var baseEnv = new Dictionary<string, string?>(operationEnv) { ["HOME"] = _tempDir };
        const string Fill = "protocol=https\nhost=github.com\n\n";
        var approveInput = $"protocol=https\nhost=github.com\nusername=x-access-token\npassword={Token}\n\n";

        // ── PRODUCTION: neither selection nor persistence reaches the recorder ──
        var fill = RunGitWithStdin(baseEnv, Fill, "credential", "fill");
        Assert.Equal(0, fill.ExitCode);
        Assert.Contains($"password={Token}", fill.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, RunGitWithStdin(baseEnv, approveInput, "credential", "approve").ExitCode);
        var produced = ReadLog(log);
        Assert.DoesNotContain("CALLED get", produced, StringComparison.Ordinal);
        Assert.DoesNotContain("CALLED store", produced, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, produced, StringComparison.Ordinal);

        // ── CONTROL 1 (selection): same helper, inherited channel only, NO operation reset → reached.
        var control = new Dictionary<string, string?>(baseEnv, StringComparer.Ordinal);
        foreach (var name in new[] { HostGitAskpassHelper.ConfigCountEnvName, "GIT_CONFIG_KEY_0",
                     "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1" })
            control.Remove(name);
        control[HostGitAskpassHelper.ConfigParametersEnvName] = inherited;

        Assert.Equal(0, RunGitWithStdin(control, Fill, "credential", "fill").ExitCode);
        Assert.Contains("CALLED get", ReadLog(log), StringComparison.Ordinal);

        // ── CONTROL 2 (persistence): the SAME recorder DOES receive the token on approve.
        Assert.Equal(0, RunGitWithStdin(control, approveInput, "credential", "approve").ExitCode);
        var persisted = ReadLog(log);
        Assert.Contains("CALLED store", persisted, StringComparison.Ordinal);
        Assert.Contains(Token, persisted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inherited <c>GIT_CONFIG_PARAMETERS</c> value is DISCARDED, not appended to: no inherited
    /// entry survives in the channel git evaluates last.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_ReplacesInheritedConfigParameters()
    {
        var original = Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS");
        Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", "'credential.helper=/evil/helper' 'core.pager=cat'");
        try
        {
            using var helper = HostGitAskpassHelper.TryCreate();
            var value = helper!.BuildChildEnvironment(Token)[HostGitAskpassHelper.ConfigParametersEnvName];

            Assert.DoesNotContain("/evil/helper", value, StringComparison.Ordinal);
            Assert.DoesNotContain("core.pager", value, StringComparison.Ordinal);
            Assert.Equal("'credential.helper=' 'credential.interactive=false'", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", original);
        }
    }

    // ── Credential RELEASE is bound to the eligible challenged host ───────────────────────

    private static (int ExitCode, string Stdout) RunScript(HostGitAskpassHelper helper, string prompt)
    {
        var env = new Dictionary<string, string?> { [HostGitAskpassHelper.CredentialEnvName] = Token };
        var (code, stdout, _) = RunShell(helper.ScriptPath, env, prompt);
        return (code, stdout);
    }

    /// <summary>Only a challenge whose URL host is exactly github.com is answered.</summary>
    [Theory]
    [InlineData("Username for 'https://github.com': ", "x-access-token")]
    [InlineData("Password for 'https://x-access-token@github.com': ", Token)]
    [InlineData("Password for 'https://github.com/org/repo.git': ", Token)]
    public void Script_AnswersChallengesForTheEligibleHost(string prompt, string expected)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only /bin/sh script."); return; }

        using var helper = HostGitAskpassHelper.TryCreate();
        var (code, stdout) = RunScript(helper!, prompt);

        Assert.Equal(0, code);
        Assert.Equal(expected, stdout);
    }

    /// <summary>Every OTHER host's challenge gets an EMPTY answer and a non-zero exit.</summary>
    [Theory]
    [InlineData("Username for 'https://gitlab.com': ")]
    [InlineData("Password for 'https://user@gitlab.com': ")]
    [InlineData("Password for 'https://github.com.evil.test': ")]
    [InlineData("Password for 'https://user@github.com.evil.test/x': ")]
    [InlineData("Password for 'https://evilgithub.com': ")]
    [InlineData("Password for 'https://github.com:8443': ")]
    [InlineData("Password for 'http://github.com': ")]
    [InlineData("Password for 'ssh://git@github.com': ")]
    [InlineData("Password for 'https://127.0.0.1:4242': ")]
    [InlineData("Password: ")]
    // Adversarial prompt forms (rendered when credential.sanitizePrompt=false): quote / slash forged
    // inside userinfo so the text merely LOOKS like the eligible host.
    [InlineData("Password for 'https://github.com'@gitlab.com': ")]
    [InlineData("Password for 'https://github.com%27@gitlab.com': ")]
    [InlineData("Password for 'https://github.com/@gitlab.com': ")]
    [InlineData("Password for 'https://github.com/x@gitlab.com': ")]
    [InlineData("Password for 'https://u@gitlab.com@github.com': ")]
    [InlineData("Username for 'https://github.com'@gitlab.com': ")]
    [InlineData("Password for 'https://github.com': trailing")]
    [InlineData("Password for \"https://github.com\": ")]
    public void Script_RefusesEveryOtherChallenge_WithNoOutput(string prompt)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only /bin/sh script."); return; }

        using var helper = HostGitAskpassHelper.TryCreate();
        var (code, stdout) = RunScript(helper!, prompt);

        Assert.NotEqual(0, code);
        Assert.Equal(string.Empty, stdout);
    }

    /// <summary>A server that answers every request 401 + Basic challenge and records Authorization headers.</summary>
    private sealed class RecordingUnauthorizedServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public List<string> Requests { get; } = [];
        public int Port { get; }

        public RecordingUnauthorizedServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[8192];
                            var read = await stream.ReadAsync(buffer);
                            if (read <= 0) return;
                            lock (Requests) Requests.Add(Encoding.ASCII.GetString(buffer, 0, read));
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"x\"\r\n"
                                + "Content-Length: 0\r\nConnection: close\r\n\r\n"));
                        }
                        catch { /* client went away */ }
                    }
                });
            }
        }

        public bool SawAuthorization { get { lock (Requests) return Requests.Any(r => r.Contains("Authorization:", StringComparison.OrdinalIgnoreCase)); } }

        public void Dispose() { _stop.Cancel(); try { _listener.Stop(); } catch { } _stop.Dispose(); }
    }

    private void InitRepoWithCommit(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var args in new[]
                 {
                     new[] { "init", "-b", "main" },
                     new[] { "config", "user.email", "t@t.invalid" },
                     new[] { "config", "user.name", "T" },
                     new[] { "commit", "--allow-empty", "-m", "seed" },
                 })
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        }
    }

    private (int ExitCode, string Stderr) RunOperationGit(
        HostGitAskpassHelper helper, string dir, params string[] args)
    {
        var env = new Dictionary<string, string?>(helper.BuildChildEnvironment(Token)) { ["HOME"] = _tempDir };
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env) if (v is not null) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }

    /// <summary>
    /// A REAL bare push whose effective destination (<c>remote.origin.pushurl</c>) is NONELIGIBLE —
    /// while the configured origin is the eligible github.com URL — still RUNS (it fails at the 401,
    /// exactly as with no credential) but NEVER sends the token: the server records no Authorization
    /// header, and the token appears nowhere in git's output.
    /// </summary>
    [Fact]
    public void RealPush_ThroughANonEligiblePushUrl_RunsButReleasesNoCredential()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only."); return; }

        using var server = new RecordingUnauthorizedServer();
        var repo = Path.Combine(_tempDir, "push-repo");
        InitRepoWithCommit(repo);
        RunOperationGitRaw(repo, "remote", "add", "origin", "https://github.com/org/config.git");
        RunOperationGitRaw(repo, "config", "remote.origin.pushurl", $"http://127.0.0.1:{server.Port}/o/r.git");

        using var helper = HostGitAskpassHelper.TryCreate();
        var (code, stderr) = RunOperationGit(helper!, repo, "push", "origin", "main");

        Assert.NotEqual(0, code);                         // it RAN and was refused, as with no credential
        Assert.NotEmpty(server.Requests);                 // the request really reached the noneligible host
        Assert.False(server.SawAuthorization, "The token was released to a noneligible push destination.");
        Assert.DoesNotContain(Token, stderr, StringComparison.Ordinal);
    }

    /// <summary>The same for an ALTERNATE tracking remote (a fetch, not a push).</summary>
    [Fact]
    public void RealFetch_FromAnAlternateNonEligibleRemote_RunsButReleasesNoCredential()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only."); return; }

        using var server = new RecordingUnauthorizedServer();
        var repo = Path.Combine(_tempDir, "fetch-repo");
        InitRepoWithCommit(repo);
        RunOperationGitRaw(repo, "remote", "add", "origin", "https://github.com/org/config.git");
        RunOperationGitRaw(repo, "remote", "add", "mirror", $"http://127.0.0.1:{server.Port}/o/r.git");

        using var helper = HostGitAskpassHelper.TryCreate();
        var (code, stderr) = RunOperationGit(helper!, repo, "fetch", "mirror");

        Assert.NotEqual(0, code);
        Assert.NotEmpty(server.Requests);
        Assert.False(server.SawAuthorization, "The token was released to an alternate noneligible remote.");
        Assert.DoesNotContain(Token, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// POSITIVE CONTROL on the REAL credential protocol: git's own <c>credential fill</c> for
    /// <c>https://github.com</c> IS answered by the script with the working principal + token, so the
    /// refusals above are not an artefact of a script that never releases anything. For the same
    /// helper, a non-github host's <c>fill</c> fails.
    /// </summary>
    [Fact]
    public void RealCredentialFill_ReleasesTheCredentialForGitHub_AndNotForAnotherHost()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only."); return; }

        using var helper = HostGitAskpassHelper.TryCreate();
        var env = new Dictionary<string, string?>(helper!.BuildChildEnvironment(Token)) { ["HOME"] = _tempDir };

        var eligible = RunGitWithStdin(env, "protocol=https\nhost=github.com\n\n", "credential", "fill");
        Assert.Equal(0, eligible.ExitCode);
        Assert.Contains("username=x-access-token", eligible.Stdout, StringComparison.Ordinal);
        Assert.Contains($"password={Token}", eligible.Stdout, StringComparison.Ordinal);

        var other = RunGitWithStdin(env, "protocol=https\nhost=gitlab.com\n\n", "credential", "fill");
        Assert.NotEqual(0, other.ExitCode);
        Assert.DoesNotContain(Token, other.Stdout, StringComparison.Ordinal);
    }


    /// <summary>
    /// Real <c>credential fill</c> with <c>credential.sanitizePrompt=false</c> (decoded userinfo is
    /// rendered into the prompt): usernames forging the eligible host at another host are refused with
    /// no token, while the genuine github.com fill is still answered — an eligible success control.
    /// </summary>
    [Theory]
    [InlineData("github.com/")]
    [InlineData("github.com'")]
    [InlineData("github.com/x")]
    public void RealCredentialFill_WithSanitizePromptOff_RefusesForgedUsernamesAtOtherHosts(string forgedUser)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only."); return; }

        File.WriteAllText(Path.Combine(_tempDir, ".gitconfig"), "[credential]\n\tsanitizePrompt = false\n");
        using var helper = HostGitAskpassHelper.TryCreate();
        var env = new Dictionary<string, string?>(helper!.BuildChildEnvironment(Token)) { ["HOME"] = _tempDir };

        var forged = RunGitWithStdin(env,
            $"protocol=https\nhost=gitlab.com\nusername={forgedUser}\n\n", "credential", "fill");
        Assert.NotEqual(0, forged.ExitCode);
        Assert.DoesNotContain(Token, forged.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, forged.Stderr, StringComparison.Ordinal);

        // ELIGIBLE CONTROLS under the same sanitizePrompt=false configuration.
        var eligible = RunGitWithStdin(env, "protocol=https\nhost=github.com\n\n", "credential", "fill");
        Assert.Equal(0, eligible.ExitCode);
        Assert.Contains($"password={Token}", eligible.Stdout, StringComparison.Ordinal);
        var withUser = RunGitWithStdin(env,
            "protocol=https\nhost=github.com\nusername=x-access-token\n\n", "credential", "fill");
        Assert.Equal(0, withUser.ExitCode);
        Assert.Contains($"password={Token}", withUser.Stdout, StringComparison.Ordinal);
    }

    private void RunOperationGitRaw(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
    }

    // ── Platform support is explicitly Linux-only and FAILS CLOSED ────────────
    /// <summary>
    /// On a platform the helper cannot serve, creation returns <c>null</c> — the fail-closed signal.
    /// No credential transport exists then, and there is no tokenized-URL fallback anywhere.
    /// </summary>
    [Fact]
    public void TryCreate_OnUnsupportedPlatform_FailsClosed()
    {
        var helper = HostGitAskpassHelper.TryCreate(platformSupported: () => false);

        Assert.Null(helper);

        // The REAL predicate is the default authority: on this (Linux) host it succeeds, which is
        // what makes the false case above meaningful rather than vacuous.
        if (OperatingSystem.IsLinux())
        {
            using var real = HostGitAskpassHelper.TryCreate();
            Assert.NotNull(real);
        }
    }
}
