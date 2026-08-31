using System.Reflection;
using System.Text;

using CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Slice 2c-c-ii-a — the <c>WorkerService</c> PRODUCTION wiring of the merged
/// <c>ConfigRepoGitOperations</c> seam.
/// </summary>
/// <remarks>
/// <para>
/// These tests drive the REAL message loop through one assignment and assert the per-assignment
/// contract: the provisioner-selection state machine (null → the legacy, seam-free path;
/// non-null → the seam path), the EXACTLY-ONE eager <c>EnsureProvisionedAsync</c> call, the
/// askpass helper's ownership-transfer guard (deleted on every failure path, retained until the
/// seam's disposal on the success path), the helper script's exact bytes, the chmod conditional,
/// and the probe → clone → <c>agents/</c> preparation order.
/// </para>
/// <para>
/// Every gate is a TCS or an interlocked counter: the harness waits on the assignment's
/// <c>WorkerReady</c> (emitted on success AND on failure), never on a delay.
/// </para>
/// </remarks>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceConfigRepoWiringTests : IDisposable
{
    /// <summary>An ELIGIBLE (HTTPS github.com:443) config repo URL — its own sanitized form.</summary>
    private const string EligibleUrl = "https://github.com/org/config-repo.git";

    private readonly StringWriter _stdOut = new();
    private readonly StringWriter _stdErr = new();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalErr = Console.Error;
    private readonly string _root;

    public WorkerServiceConfigRepoWiringTests()
    {
        Console.SetOut(_stdOut);
        Console.SetError(_stdErr);

        _root = Path.Combine(Path.GetTempPath(), "copilothive-wiring-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>The config repo directory for one test — a child of the per-test root.</summary>
    private string ConfigRepoDir => Path.Combine(_root, "config-repo");

    // ==================================================================
    // 1. The provisioner-selection state machine.
    // ==================================================================

    /// <summary>
    /// NO provisioner (neither the <c>RunAsync</c>-assigned field nor the test override) SKIPS
    /// the entire preparation: no askpass helper, no probe, no clone, no seam — and the executor
    /// is built with the LEGACY public constructor. This is the path the existing direct-loop
    /// tests take.
    /// </summary>
    [Fact]
    public async Task NullProvisioner_SkipsAllPreparation_AndRunsLegacyExecutor()
    {
        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);

        var dirCreates = 0;
        var scriptWrites = 0;
        var chmods = 0;
        service.AskpassDirCreate = _ => Interlocked.Increment(ref dirCreates);
        service.AskpassScriptWrite = _ => Interlocked.Increment(ref scriptWrites);
        service.AskpassChmod = _ => Interlocked.Increment(ref chmods);

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-legacy", TestContext.Current.CancellationToken);

        Assert.Equal(1, runner.PromptCount);
        Assert.Equal(0, dirCreates);
        Assert.Equal(0, scriptWrites);
        Assert.Equal(0, chmods);

        // No seam existed, so no config-repo git subprocess was ever launched.
        Assert.Equal(0, launcher.CallCount);

        // And the unconditional agents/ guarantee belongs to the SEAM path only.
        Assert.False(Directory.Exists(Path.Combine(ConfigRepoDir, "agents")));
    }

    /// <summary>
    /// A <c>TestProvisioner</c> selects the SEAM path: the health probe runs before the agent is
    /// ever prompted, and the <c>agents/</c> directory exists afterwards.
    /// </summary>
    [Fact]
    public async Task TestProvisioner_HealthyRepo_ProbesBeforeExecutionAndSkipsClone()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var probedBeforePrompt = false;
        var agentsDirBeforePrompt = false;
        var runner = new CallbackAgentRunner(() =>
        {
            probedBeforePrompt = launcher.Saw("rev-parse", "--is-inside-work-tree");
            agentsDirBeforePrompt = Directory.Exists(Path.Combine(ConfigRepoDir, "agents"));
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        var harness = new ProvisionerHarness(configRepoUrl: EligibleUrl, ghToken: "ghp_test");
        service.TestProvisioner = harness.Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-healthy", TestContext.Current.CancellationToken);

        Assert.Equal(1, runner.PromptCount);

        // The preparation completed BEFORE ExecuteAsync reached the agent.
        Assert.True(probedBeforePrompt, "The health probe must run before the executor prompts.");
        Assert.True(agentsDirBeforePrompt, "agents/ must exist before the executor prompts.");

        // A healthy repo is never cloned.
        Assert.False(launcher.Saw("clone"));
        Assert.True(Directory.Exists(Path.Combine(ConfigRepoDir, "agents")));
    }

    /// <summary>
    /// The production field (assigned by <c>RunAsync</c> after an ACCEPTED registration) is
    /// consulted too — not only the test override. Deleting the <c>?? _provisioner</c> fallback
    /// would take production back to the legacy path and fail here.
    /// </summary>
    [Fact]
    public async Task ProductionProvisionerField_SelectsTheSeamPath()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);

        var harness = new ProvisionerHarness(configRepoUrl: EligibleUrl, ghToken: "ghp_test");
        typeof(WorkerService)
            .GetField("_provisioner", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, harness.Provisioner);

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-field", TestContext.Current.CancellationToken);

        Assert.True(launcher.Saw("rev-parse", "--is-inside-work-tree"));
        Assert.Equal(1, harness.FetchCount);
    }

    // ==================================================================
    // 2. The eager provisioning call.
    // ==================================================================

    /// <summary>
    /// <c>EnsureProvisionedAsync</c> is awaited EXACTLY ONCE per assignment, eagerly, before any
    /// seam-dependent work. Each call performs exactly one provisioning fetch, so the fetch count
    /// is the exact proxy — and the seam's own (lazy) resolver reads never add fetches.
    /// </summary>
    [Fact]
    public async Task EnsureProvisioned_IsCalledExactlyOncePerAssignment()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launchesAtFetch = -1;
        FakeGitLauncher? launcher = null;
        var harness = new ProvisionerHarness(
            configRepoUrl: EligibleUrl,
            ghToken: "ghp_test",
            onFetch: () => launchesAtFetch = launcher?.CallCount ?? -1);

        launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = harness.Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-once", TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.FetchCount);

        // The single call happened BEFORE any seam-dependent subprocess.
        Assert.Equal(0, launchesAtFetch);
        Assert.True(launcher.CallCount > 0, "The seam must have run after provisioning.");
    }

    // ==================================================================
    // 3. The askpass ownership-transfer guard.
    // ==================================================================

    /// <summary>
    /// A failure at the DIRECTORY-CREATE step still runs the guarded cleanup: the PARTIAL state
    /// the failing step had already produced is deleted, and the assignment fails without ever
    /// reaching the executor.
    /// </summary>
    /// <remarks>
    /// The fake deliberately MUTATES observable state before throwing — it creates the helper
    /// directory, a sentinel file and a nested child — so the cleanup has something real to
    /// remove. A fake that threw without creating anything would make
    /// <c>Assert.False(Directory.Exists(...))</c> vacuous: it would hold even if the guard's
    /// cleanup were deleted for this exception path.
    /// </remarks>
    [Fact]
    public async Task DirCreateFailure_GuardDeletesThePartialState_AndSkipsExecution()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        string? helperDir = null;
        string? sentinel = null;
        string? nestedChild = null;
        var partialStateExisted = false;

        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;

            // A PARTIAL create: the directory, a file inside it and a nested child — then the
            // failure. This is what the guarded cleanup must remove RECURSIVELY.
            Directory.CreateDirectory(dir);
            sentinel = Path.Combine(dir, "partial-sentinel.txt");
            File.WriteAllText(sentinel, "partial state");
            nestedChild = Path.Combine(dir, "nested");
            Directory.CreateDirectory(nestedChild);
            File.WriteAllText(Path.Combine(nestedChild, "deep.txt"), "deep");

            partialStateExisted = Directory.Exists(dir) && File.Exists(sentinel);
            throw new IOException("dir create failed after partial mutation");
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-dirfail", TestContext.Current.CancellationToken);

        Assert.True(partialStateExisted, "The fake must leave real partial state behind to clean up.");

        // The guard removed the directory AND everything inside it.
        Assert.NotNull(helperDir);
        Assert.False(Directory.Exists(helperDir), "The guard must delete the partially-created directory.");
        Assert.False(File.Exists(sentinel), "The guard's delete must be recursive.");
        Assert.False(Directory.Exists(nestedChild), "The guard's delete must be recursive.");

        // Ownership never transferred: no seam, no executor, no git.
        Assert.Equal(0, runner.PromptCount);
        Assert.Equal(0, launcher.CallCount);
    }

    /// <summary>
    /// A failure at the SCRIPT-WRITE step deletes the already-created helper directory,
    /// recursively — the partially-written script inside it goes with it.
    /// </summary>
    [Fact]
    public async Task ScriptWriteFailure_DeletesHelperDirectory()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        string? helperDir = null;
        string? partialScript = null;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };
        service.AskpassScriptWrite = path =>
        {
            // A PARTIAL write, so the directory is non-empty when the guard cleans it up.
            partialScript = path;
            File.WriteAllText(path, "#!/bin/sh\n# truncated");
            throw new IOException("script write failed");
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-writefail", TestContext.Current.CancellationToken);

        Assert.NotNull(helperDir);
        Assert.NotNull(partialScript);
        Assert.False(Directory.Exists(helperDir));
        Assert.False(File.Exists(partialScript), "The guard's delete must be recursive.");
        Assert.Equal(0, runner.PromptCount);
    }

    /// <summary>A failure at the CHMOD step deletes the helper directory AND its script.</summary>
    [Fact]
    public async Task ChmodFailure_DeletesHelperDirectory()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        string? helperDir = null;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };
        service.AskpassChmodPlatform = () => true;
        service.AskpassChmod = _ => throw new UnauthorizedAccessException("chmod failed");

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-chmodfail", TestContext.Current.CancellationToken);

        Assert.NotNull(helperDir);
        Assert.False(Directory.Exists(helperDir));
        Assert.False(File.Exists(Path.Combine(helperDir!, "askpass.sh")));
        Assert.Equal(0, runner.PromptCount);
    }

    /// <summary>
    /// A failure at the SEAM-CONSTRUCTION step — the last step inside the guard — also deletes
    /// the helper directory: ownership only transfers once construction has SUCCEEDED. A
    /// relative config repo directory is rejected by the seam's constructor.
    /// </summary>
    [Fact]
    public async Task SeamConstructionFailure_DeletesHelperDirectory()
    {
        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, "relative-config-repo");
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        string? helperDir = null;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };
        service.AskpassChmodPlatform = () => false;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-seamfail", TestContext.Current.CancellationToken);

        Assert.NotNull(helperDir);

        // The fully-written script was inside — the delete had to be recursive.
        Assert.False(Directory.Exists(helperDir));
        Assert.False(File.Exists(Path.Combine(helperDir!, "askpass.sh")));
        Assert.Equal(0, runner.PromptCount);
    }

    /// <summary>
    /// On the SUCCESS path ownership transfers to the seam: the helper directory and its script
    /// are still present WHILE the executor runs, and are gone once the seam has been disposed.
    /// </summary>
    [Fact]
    public async Task FullSuccess_RetainsHelperUntilSeamDisposal()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        var dirAliveDuringExecution = false;
        var scriptAliveDuringExecution = false;

        var runner = new CallbackAgentRunner(() =>
        {
            dirAliveDuringExecution = helperDir is not null && Directory.Exists(helperDir);
            scriptAliveDuringExecution =
                helperDir is not null && File.Exists(Path.Combine(helperDir, "askpass.sh"));
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-success", TestContext.Current.CancellationToken);

        Assert.NotNull(helperDir);
        Assert.True(dirAliveDuringExecution, "The helper directory must outlive the executor's run.");
        Assert.True(scriptAliveDuringExecution, "The helper script must outlive the executor's run.");

        // The seam's onDispose fired when the Task.Run body's `using` scope closed.
        Assert.False(Directory.Exists(helperDir));
    }

    // ==================================================================
    // 4. The helper script's exact bytes.
    // ==================================================================

    /// <summary>
    /// The REAL script write produces the EXACT expected bytes: UTF-8 with NO BOM (so
    /// <c>/bin/sh</c> sees <c>#!</c> as the first two bytes) and a trailing newline.
    /// </summary>
    [Fact]
    public async Task AskpassScript_HasExactBytes_Utf8NoBom_TrailingNewline()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        byte[]? scriptBytes = null;

        var runner = new CallbackAgentRunner(() =>
        {
            if (helperDir is null) return;
            var scriptPath = Path.Combine(helperDir, "askpass.sh");
            if (File.Exists(scriptPath))
                scriptBytes = File.ReadAllBytes(scriptPath);
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        // Only the DIRECTORY create is intercepted (to capture the path); the script write is
        // the REAL production implementation.
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-bytes", TestContext.Current.CancellationToken);

        Assert.NotNull(scriptBytes);

        const string expected =
            "#!/bin/sh\n"
            + "case \"$1\" in\n"
            + "  *sername*) printf '%s' \"x-access-token\" ;;\n"
            + "  *) printf '%s' \"$GITHUB_CONFIG_REPO_TOKEN\" ;;\n"
            + "esac\n";

        Assert.Equal(new UTF8Encoding(false).GetBytes(expected), scriptBytes);

        // Explicitly: no BOM, and the file ends with the newline.
        Assert.NotEqual(0xEF, scriptBytes![0]);
        Assert.Equal((byte)'#', scriptBytes[0]);
        Assert.Equal((byte)'\n', scriptBytes[^1]);
    }

    /// <summary>
    /// The helper directory name is the documented, high-entropy temp form so two concurrent
    /// assignments can never collide, and the script always lives inside it as
    /// <c>askpass.sh</c>.
    /// </summary>
    [Fact]
    public async Task AskpassHelper_UsesPerAssignmentTempDirectory()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? dirPath = null;
        string? scriptPath = null;

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            dirPath = dir;
            Directory.CreateDirectory(dir);
        };
        service.AskpassScriptWrite = path => scriptPath = path;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-paths", TestContext.Current.CancellationToken);

        Assert.NotNull(dirPath);
        Assert.NotNull(scriptPath);

        var leaf = Path.GetFileName(dirPath);
        Assert.StartsWith("copilothive-askpass-", leaf, StringComparison.Ordinal);
        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(Path.GetDirectoryName(dirPath)! + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(dirPath!, "askpass.sh"), scriptPath);
    }

    // ==================================================================
    // 5. The chmod conditional.
    // ==================================================================

    /// <summary>
    /// When the platform predicate is TRUE the chmod runs EXACTLY TWICE, in order: the SCRIPT
    /// path first, then the DIRECTORY path.
    /// </summary>
    [Fact]
    public async Task ChmodPlatformTrue_AppliesScriptThenDirectory_ExactlyTwice()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var chmodded = new List<string>();
        string? dirPath = null;

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            dirPath = dir;
            Directory.CreateDirectory(dir);
        };
        service.AskpassChmodPlatform = () => true;
        service.AskpassChmod = path => { lock (chmodded) chmodded.Add(path); };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-chmod-on", TestContext.Current.CancellationToken);

        Assert.NotNull(dirPath);
        Assert.Equal(
            [Path.Combine(dirPath!, "askpass.sh"), dirPath!],
            chmodded);
    }

    /// <summary>When the platform predicate is FALSE the chmod never runs.</summary>
    [Fact]
    public async Task ChmodPlatformFalse_NeverChmods()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var chmods = 0;

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassChmodPlatform = () => false;
        service.AskpassChmod = _ => Interlocked.Increment(ref chmods);

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-chmod-off", TestContext.Current.CancellationToken);

        Assert.Equal(0, chmods);
    }

    /// <summary>
    /// LINUX-GATED — the REAL chmod applies mode 0700 to BOTH the script and the directory, so
    /// no other account on the host can read the helper (or, once git runs it, probe the
    /// credential flow).
    /// </summary>
    [Fact]
    public async Task RealChmod_AppliesOwnerOnlyMode_Linux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Unix file modes are only meaningful on Linux/macOS.");

        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        string? helperDir = null;
        UnixFileMode scriptMode = default;
        UnixFileMode dirMode = default;

        var runner = new CallbackAgentRunner(() =>
        {
            if (helperDir is null || !OperatingSystem.IsLinux()) return;
            scriptMode = File.GetUnixFileMode(Path.Combine(helperDir, "askpass.sh"));
            dirMode = File.GetUnixFileMode(helperDir);
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;
        service.AskpassDirCreate = dir =>
        {
            helperDir = dir;
            Directory.CreateDirectory(dir);
        };

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-realchmod", TestContext.Current.CancellationToken);

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        Assert.Equal(ownerOnly, scriptMode);
        Assert.Equal(ownerOnly, dirMode);
    }

    // ==================================================================
    // 6. The preparation: probe → clone → agents/.
    // ==================================================================

    /// <summary>
    /// No repo present → the seam CLONES it, an INFO line is logged, and <c>agents/</c> exists.
    /// </summary>
    [Fact]
    public async Task NoRepo_ClonesAndLogsInfo_AndCreatesAgentsDirectory()
    {
        var launcher = new FakeGitLauncher(tokens => NoRepoHandler(tokens, cloneExitCode: 0));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-clone", TestContext.Current.CancellationToken);

        Assert.True(launcher.Saw("clone"), "An absent repo must be cloned.");
        Assert.Contains("Config repo cloned", _stdOut.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(ConfigRepoDir, "agents")));
    }

    /// <summary>
    /// A FAILED clone logs the EXACT warning shape — the fixed prefix plus the seam's already
    /// URL-redacted error, trimmed and control-character sanitized — and STILL creates
    /// <c>agents/</c>: the guarantee is unconditional.
    /// </summary>
    [Fact]
    public async Task CloneFailure_LogsExactWarning_AndStillCreatesAgentsDirectory()
    {
        var launcher = new FakeGitLauncher(tokens => NoRepoHandler(tokens, cloneExitCode: 128));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-clonefail", TestContext.Current.CancellationToken);

        var output = _stdOut.ToString();

        // The credential in the stderr URL is redacted, the trailing newline is trimmed, and the
        // embedded CR/LF pair is replaced by the log-sanitizer placeholders.
        Assert.Contains(
            "WARN: Config repo clone failed: fatal: could not read from "
            + "https://github.com/org/config-repo.git??second line",
            output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("ghp_secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Config repo cloned", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(ConfigRepoDir, "agents")));
    }

    /// <summary>
    /// The <c>agents/</c> creation is IDEMPOTENT: an existing directory is left alone (with its
    /// contents intact) rather than recreated.
    /// </summary>
    [Fact]
    public async Task ExistingAgentsDirectory_IsPreserved()
    {
        var agentsDir = Path.Combine(ConfigRepoDir, "agents");
        Directory.CreateDirectory(agentsDir);
        var marker = Path.Combine(agentsDir, "coder.agents.md");
        await File.WriteAllTextAsync(marker, "existing", TestContext.Current.CancellationToken);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-agents-idempotent", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(marker));
        Assert.Equal("existing", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
    }

    // ==================================================================
    // Handlers.
    // ==================================================================

    /// <summary>A repo that probes HEALTHY, with a matching, credential-free origin.</summary>
    private GitProcessResult HealthyRepoHandler(IReadOnlyList<string> tokens)
    {
        if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
            return new GitProcessResult(0, "true\n", "");

        if (Matches(tokens, "rev-parse", "--show-toplevel"))
            return new GitProcessResult(0, ConfigRepoDir + "\n", "");

        if (Matches(tokens, "remote", "get-url", "origin"))
            return new GitProcessResult(0, EligibleUrl + "\n", "");

        return new GitProcessResult(0, "", "");
    }

    /// <summary>
    /// A target that is NOT a git worktree, so the preparation proceeds to the clone. The clone's
    /// exit code is the parameter; its stderr deliberately carries a CREDENTIAL-BEARING URL and
    /// control characters so the log rendering can be asserted end to end.
    /// </summary>
    private static GitProcessResult NoRepoHandler(IReadOnlyList<string> tokens, int cloneExitCode)
    {
        if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
            return new GitProcessResult(128, "", "fatal: not a git repository");

        if (Matches(tokens, "clone"))
        {
            return cloneExitCode == 0
                ? new GitProcessResult(0, "", "")
                : new GitProcessResult(
                    cloneExitCode,
                    "",
                    "fatal: could not read from https://x-access-token:ghp_secret@github.com/org/config-repo.git\r\nsecond line\n");
        }

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
