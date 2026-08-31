using CopilotHive.Services;
using CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Slice 2c-c-ii-a — the <c>WorkerService.PrepareConfigRepoAsync</c> matrix: the probe → clone
/// decision, the EXACT clone-failure WARN rendering, the UNCONDITIONAL <c>agents/</c> guarantee
/// across every non-cancelled outcome, and the propagation of a failing directory create.
/// </summary>
/// <remarks>
/// Every git subprocess is answered by the <see cref="GitOperations.ProcessRunner"/> seam, so no
/// real git ever runs; the assignment's <c>WorkerReady</c> is the single completion gate (the
/// body emits it on success AND on failure). No delays, no polling, no timing assertions.
/// </remarks>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceConfigRepoPreparationTests : IDisposable
{
    /// <summary>An ELIGIBLE (HTTPS github.com:443) config repo URL — its own sanitized form.</summary>
    private const string EligibleUrl = "https://github.com/org/config-repo.git";

    /// <summary>The fixed prefix of the clone-failure warning.</summary>
    private const string CloneFailedPrefix = "Config repo clone failed: ";

    /// <summary>The fixed informational message emitted after a successful clone.</summary>
    private const string ClonedInfo = "Config repo cloned";

    private readonly StringWriter _stdOut = new();
    private readonly StringWriter _stdErr = new();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalErr = Console.Error;
    private readonly string _root;

    public WorkerServiceConfigRepoPreparationTests()
    {
        Console.SetOut(_stdOut);
        Console.SetError(_stdErr);

        _root = Path.Combine(Path.GetTempPath(), "copilothive-prep-" + Guid.NewGuid().ToString("N"));
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

    private string AgentsDir => Path.Combine(ConfigRepoDir, "agents");

    // ==================================================================
    // 1. The probe → clone decision.
    // ==================================================================

    /// <summary>
    /// An ABSENT repo (the probe reports no worktree) is CLONED, and the fixed INFO line is
    /// logged exactly once.
    /// </summary>
    [Fact]
    public async Task RepoAbsent_ClonesAndLogsClonedInfoExactlyOnce()
    {
        var launcher = new FakeGitLauncher(tokens => AbsentRepoHandler(tokens, cloneExitCode: 0));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-absent", TestContext.Current.CancellationToken);

        Assert.True(launcher.Saw("clone"), "An absent repo must be cloned.");

        var lines = Lines();
        Assert.Equal(1, lines.Count(l => l == "[Worker] " + ClonedInfo));

        // A successful clone never emits the failure warning.
        Assert.DoesNotContain(lines, l => l.Contains(CloneFailedPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// A PRESENT repo (<c>HasRepo=true</c>) is NEVER cloned: the probe short-circuits the clone,
    /// and neither the INFO nor the WARN clone line is emitted.
    /// </summary>
    [Fact]
    public async Task RepoPresent_ProbeReportsHasRepo_NoCloneCall()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-present", TestContext.Current.CancellationToken);

        // The probe DID run …
        Assert.True(launcher.Saw("rev-parse", "--is-inside-work-tree"));

        // … and decided against a clone. Not one launch carries the `clone` subcommand.
        Assert.False(launcher.Saw("clone"), "A healthy repo must never be cloned.");
        Assert.DoesNotContain(launcher.Calls, call => call.Contains("clone"));

        var lines = Lines();
        Assert.DoesNotContain(lines, l => l.Contains(ClonedInfo, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(CloneFailedPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// A FAILED clone logs the warning BYTE-EXACTLY: the logger prefix, the fixed
    /// <c>Config repo clone failed: </c> prefix, and the seam's error after
    /// <c>Trim</c> → <c>GitUrlRedactor.Redact</c> → <c>LogSanitizer.SanitizeText</c>.
    /// </summary>
    /// <remarks>
    /// The raw stderr deliberately carries a CREDENTIAL-BEARING URL, a CR/LF pair and trailing
    /// whitespace, so the expected literal exercises every stage of the rendering: the redactor
    /// strips <c>x-access-token:ghp_secret@</c>, the seam's own mapping trims the trailing
    /// whitespace, and the sanitizer replaces the CR and the LF with the <c>?</c> placeholder.
    /// </remarks>
    [Fact]
    public async Task CloneFailure_LogsByteExactSanitizedWarning()
    {
        var launcher = new FakeGitLauncher(tokens => AbsentRepoHandler(tokens, cloneExitCode: 128));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-clonefail-exact", TestContext.Current.CancellationToken);

        // The ONE expected line, spelled out in full — no substring matching. The two
        // placeholders stand for the CR and the LF the sanitizer replaced.
        var placeholder = LogSanitizer.Placeholder;
        var expected =
            "[Worker] WARN: Config repo clone failed: "
            + "fatal: could not read from https://github.com/org/config-repo.git"
            + placeholder + placeholder
            + "remote: line two";

        var warnings = Lines()
            .Where(l => l.Contains(CloneFailedPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Single(warnings);
        Assert.Equal(expected, warnings[0]);

        // And the failure path never claims success.
        Assert.DoesNotContain(Lines(), l => l.Contains(ClonedInfo, StringComparison.Ordinal));
    }

    /// <summary>
    /// The rendering is a real TRANSFORM, not a passthrough: the credential material present in
    /// the raw stderr appears NOWHERE in the process output, and the raw (unsanitized) form is
    /// not logged either.
    /// </summary>
    [Fact]
    public async Task CloneFailure_LogContainsNoCredentialMaterial()
    {
        var launcher = new FakeGitLauncher(tokens => AbsentRepoHandler(tokens, cloneExitCode: 128));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-clonefail-secrecy", TestContext.Current.CancellationToken);

        var output = _stdOut.ToString() + _stdErr.ToString();

        Assert.DoesNotContain("ghp_secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("@github.com", output, StringComparison.Ordinal);

        // The RAW error still contained the CR/LF, which the sanitizer must have replaced —
        // proving the sanitization stage ran and did not merely pass the value through.
        Assert.DoesNotContain(
            "config-repo.git\rremote", output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "config-repo.git\nremote", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe reports <c>HasRepo=true</c> with a FOREIGN top level (a containment mismatch):
    /// the repository exists, so NO clone is attempted, and — because the seam abandons the
    /// reconciliation/identity stages for a mismatched root — the target is never repaired or
    /// mutated. The <c>agents/</c> guarantee still holds.
    /// </summary>
    /// <remarks>
    /// This is the decisive present-repo case: <c>HasRepo</c> stays true while every other health
    /// field is cleared, so a preparation that keyed the clone off anything other than
    /// <c>HasRepo</c> (the reported directories, or the absence of a reason) would clone over a
    /// live repository. The mismatch also short-circuits the seam BEFORE its origin
    /// reconciliation, so no <c>remote add</c>/<c>set-url</c> and no <c>config user.*</c> command
    /// may appear.
    /// </remarks>
    [Fact]
    public async Task ProbeHasRepoButForeignTopLevel_NoClone_NoRepair_StillCreatesAgents()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        // A sentinel proves the existing target is left exactly as it was.
        var sentinel = Path.Combine(ConfigRepoDir, "existing-content.txt");
        await File.WriteAllTextAsync(sentinel, "untouched", TestContext.Current.CancellationToken);

        var foreignTopLevel = Path.Combine(_root, "somewhere-else");
        Directory.CreateDirectory(foreignTopLevel);

        var launcher = new FakeGitLauncher(tokens =>
        {
            if (Matches(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(0, "true\n", "");

            // The worktree root is NOT the configured config repo directory.
            if (Matches(tokens, "rev-parse", "--show-toplevel"))
                return new GitProcessResult(0, foreignTopLevel + "\n", "");

            return new GitProcessResult(0, "", "");
        });
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-toplevel-mismatch", TestContext.Current.CancellationToken);

        // The probe ran and reported a repository …
        Assert.True(launcher.Saw("rev-parse", "--is-inside-work-tree"));
        Assert.True(launcher.Saw("rev-parse", "--show-toplevel"));

        // … so NOTHING was cloned over it.
        Assert.False(launcher.Saw("clone"), "A present repo must never be cloned, mismatch or not.");
        Assert.DoesNotContain(launcher.Calls, call => call.Contains("clone"));

        // … and the mismatch stopped the seam before any repairing/mutating command.
        Assert.False(launcher.Saw("remote", "add"), "A mismatched root must not be repaired.");
        Assert.False(launcher.Saw("remote", "set-url"), "A mismatched root must not be repaired.");
        Assert.False(launcher.Saw("config"), "A mismatched root must not have its identity rewritten.");

        // The existing target is byte-for-byte untouched.
        Assert.True(File.Exists(sentinel));
        Assert.Equal(
            "untouched",
            await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));

        // The unconditional guarantee still holds.
        Assert.True(Directory.Exists(AgentsDir));

        // Neither clone log line was emitted — the clone branch was never entered.
        var lines = Lines();
        Assert.DoesNotContain(lines, l => l.Contains(ClonedInfo, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(CloneFailedPrefix, StringComparison.Ordinal));
    }

    // ==================================================================
    // 2. The UNCONDITIONAL agents/ guarantee.
    // ==================================================================

    /// <summary>
    /// A HEALTHY repo that lacks <c>agents/</c> gets the directory created — the guarantee is
    /// not conditional on a clone having run.
    /// </summary>
    [Fact]
    public async Task HealthyRepoWithoutAgentsDirectory_CreatesIt()
    {
        Directory.CreateDirectory(ConfigRepoDir);
        Assert.False(Directory.Exists(AgentsDir));

        var launcher = new FakeGitLauncher(HealthyRepoHandler);
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-agents-healthy", TestContext.Current.CancellationToken);

        Assert.False(launcher.Saw("clone"));
        Assert.True(Directory.Exists(AgentsDir));
    }

    /// <summary>
    /// The <c>agents/</c> create is UNCONDITIONAL: it happens after a healthy probe, after a
    /// SUCCESSFUL clone and after a FAILED clone alike. Each outcome is exercised over its own
    /// worker instance and its own config-repo directory.
    /// </summary>
    [Theory]
    [InlineData(PreparationOutcome.ProbeHealthy)]
    [InlineData(PreparationOutcome.CloneSucceeded)]
    [InlineData(PreparationOutcome.CloneFailed)]
    public async Task AgentsDirectory_ExistsAfterEveryNonCancelledOutcome(PreparationOutcome outcome)
    {
        var configRepoDir = Path.Combine(_root, "repo-" + outcome);
        var agentsDir = Path.Combine(configRepoDir, "agents");

        if (outcome == PreparationOutcome.ProbeHealthy)
            Directory.CreateDirectory(configRepoDir);

        var launcher = new FakeGitLauncher(tokens => outcome switch
        {
            PreparationOutcome.ProbeHealthy => HealthyHandlerFor(configRepoDir, tokens),
            PreparationOutcome.CloneSucceeded => AbsentRepoHandler(tokens, cloneExitCode: 0),
            PreparationOutcome.CloneFailed => AbsentRepoHandler(tokens, cloneExitCode: 128),
            _ => throw new InvalidOperationException($"Unhandled outcome '{outcome}'."),
        });
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var runner = new CallbackAgentRunner();
        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, configRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-agents-" + outcome, TestContext.Current.CancellationToken);

        Assert.True(
            Directory.Exists(agentsDir),
            $"agents/ must exist after the '{outcome}' outcome.");

        // The outcome really was the one under test.
        switch (outcome)
        {
            case PreparationOutcome.ProbeHealthy:
                Assert.False(launcher.Saw("clone"));
                break;
            case PreparationOutcome.CloneSucceeded:
                Assert.True(launcher.Saw("clone"));
                Assert.Contains(Lines(), l => l.Contains(ClonedInfo, StringComparison.Ordinal));
                break;
            case PreparationOutcome.CloneFailed:
                Assert.True(launcher.Saw("clone"));
                Assert.Contains(Lines(), l => l.Contains(CloneFailedPrefix, StringComparison.Ordinal));
                break;
            default:
                throw new InvalidOperationException($"Unhandled outcome '{outcome}'.");
        }
    }

    /// <summary>The three non-cancelled preparation outcomes the agents/ guarantee spans.</summary>
    public enum PreparationOutcome
    {
        /// <summary>The probe reported a healthy worktree, so no clone ran.</summary>
        ProbeHealthy,

        /// <summary>No repo was present and the clone succeeded.</summary>
        CloneSucceeded,

        /// <summary>No repo was present and the clone FAILED (a warning was logged).</summary>
        CloneFailed,
    }

    /// <summary>
    /// The preparation runs BEFORE the executor: <c>agents/</c> already exists when the agent is
    /// prompted, so the improver can never observe a half-prepared config repo.
    /// </summary>
    [Fact]
    public async Task AgentsDirectory_ExistsBeforeTheExecutorRuns()
    {
        var launcher = new FakeGitLauncher(tokens => AbsentRepoHandler(tokens, cloneExitCode: 0));
        using var _ = WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);

        var agentsExistedAtPrompt = false;
        var cloneSeenAtPrompt = false;
        var runner = new CallbackAgentRunner(() =>
        {
            agentsExistedAtPrompt = Directory.Exists(AgentsDir);
            cloneSeenAtPrompt = launcher.Saw("clone");
        });

        using var service = WorkerServiceConfigRepoHarness.BuildService(runner, ConfigRepoDir);
        service.TestProvisioner = new ProvisionerHarness(EligibleUrl, "ghp_test").Provisioner;

        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-order", TestContext.Current.CancellationToken);

        Assert.Equal(1, runner.PromptCount);
        Assert.True(cloneSeenAtPrompt, "The clone must complete before the executor runs.");
        Assert.True(agentsExistedAtPrompt, "agents/ must exist before the executor runs.");
    }

    // ==================================================================
    // 3. A failing directory create PROPAGATES.
    // ==================================================================

    /// <summary>
    /// A <c>Directory.CreateDirectory</c> failure PROPAGATES into the assignment body's existing
    /// generic failure handler: the assignment fails through the normal error path (a sanitized
    /// ERROR line, the Ready claim still honoured) rather than crashing the loop or silently
    /// continuing into the executor.
    /// </summary>
    /// <remarks>
    /// The failure is provoked WITHOUT a production seam: an ordinary FILE named <c>agents</c>
    /// occupies the path, which makes <see cref="Directory.CreateDirectory(string)"/> throw
    /// <see cref="IOException"/>. That keeps the test honest about the real call the production
    /// code makes.
    /// </remarks>
    [Fact]
    public async Task AgentsCreateFailure_PropagatesToTheGenericFailureHandler()
    {
        Directory.CreateDirectory(ConfigRepoDir);

        // An ordinary file where the directory must go.
        await File.WriteAllTextAsync(AgentsDir, "not a directory", TestContext.Current.CancellationToken);

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

        // The Ready gate inside the harness is what proves the body unwound through its normal
        // path: a crash escaping Task.Run would never emit it.
        await WorkerServiceConfigRepoHarness.RunOneAssignmentAsync(
            service, "task-agentsfail", TestContext.Current.CancellationToken);

        // The generic handler logged the sanitized failure …
        Assert.Contains(
            Lines(_stdErr),
            l => l.StartsWith("[Worker] ERROR: Task execution failed", StringComparison.Ordinal));

        // … the executor was NEVER reached (the failure preceded its construction) …
        Assert.Equal(0, runner.PromptCount);

        // … and the seam's disposal still cleaned the helper up on the way out.
        Assert.NotNull(helperDir);
        Assert.False(Directory.Exists(helperDir));

        // The file is untouched — nothing "repaired" the corrupt target.
        Assert.True(File.Exists(AgentsDir));
        Assert.Equal(
            "not a directory",
            await File.ReadAllTextAsync(AgentsDir, TestContext.Current.CancellationToken));
    }

    // ==================================================================
    // Handlers and helpers.
    // ==================================================================

    /// <summary>The process output as individual lines, with the trailing blank removed.</summary>
    private string[] Lines() => Lines(_stdOut);

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    /// <summary>A repo that probes HEALTHY, with a matching, credential-free origin.</summary>
    private GitProcessResult HealthyRepoHandler(IReadOnlyList<string> tokens) =>
        HealthyHandlerFor(ConfigRepoDir, tokens);

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

    /// <summary>
    /// A target that is NOT a git worktree, so the preparation proceeds to the clone. The failing
    /// clone's stderr deliberately carries a CREDENTIAL-BEARING URL, a CR/LF pair and trailing
    /// whitespace, so the byte-exact WARN assertion exercises every rendering stage.
    /// </summary>
    private static GitProcessResult AbsentRepoHandler(IReadOnlyList<string> tokens, int cloneExitCode)
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
                    "fatal: could not read from "
                    + "https://x-access-token:ghp_secret@github.com/org/config-repo.git\r\n"
                    + "remote: line two \n");
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
