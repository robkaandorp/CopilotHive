using System.Diagnostics;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Slice 2 config-driven Brain registration tests (Program.cs): with a real config repo
/// (<c>--config-repo</c>) the Brain is registered ONLY when <c>orchestrator.model</c> is
/// non-blank; its model / context-window / max-steps come from the parsed config (never
/// <c>BRAIN_MODEL</c> / <c>BRAIN_CONTEXT_WINDOW</c> / <c>BRAIN_MAX_STEPS</c>); and the startup
/// connect block connects the registered Brain (offline-safe — no SharpCoder/Copilot network
/// I/O at connect) or skips cleanly when unconfigured.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class BrainRegistrationTests : IDisposable
{
    private const string BrainEnabledLog = "Brain enabled — model:";
    private const string BrainDisabledLog = "Brain disabled — no brain model configured in hive-config.yaml";

    private readonly string? _previousBrainModel;
    private readonly string? _previousBrainContextWindow;
    private readonly string? _previousBrainMaxSteps;
    private readonly List<string> _tempRoots = [];

    public BrainRegistrationTests()
    {
        // Every test runs with the legacy env vars SET to prove they have NO effect on the
        // Brain (Slice 2: config is the sole authority).
        _previousBrainModel = Environment.GetEnvironmentVariable("BRAIN_MODEL");
        _previousBrainContextWindow = Environment.GetEnvironmentVariable("BRAIN_CONTEXT_WINDOW");
        _previousBrainMaxSteps = Environment.GetEnvironmentVariable("BRAIN_MAX_STEPS");
        Environment.SetEnvironmentVariable("BRAIN_MODEL", "env-brain-model");
        Environment.SetEnvironmentVariable("BRAIN_CONTEXT_WINDOW", "999999");
        Environment.SetEnvironmentVariable("BRAIN_MAX_STEPS", "424242");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BRAIN_MODEL", _previousBrainModel);
        Environment.SetEnvironmentVariable("BRAIN_CONTEXT_WINDOW", _previousBrainContextWindow);
        Environment.SetEnvironmentVariable("BRAIN_MAX_STEPS", _previousBrainMaxSteps);

        foreach (var root in _tempRoots)
            TestHelpers.ForceDeleteDirectory(root);
    }

    // ── Brain registered + connecting when orchestrator.model is configured ────

    /// <summary>
    /// A config repo whose hive-config.yaml declares <c>orchestrator.model</c>: the Brain IS
    /// registered, its model is the parsed config value (BRAIN_MODEL env is set but ignored),
    /// and the real startup path connects it — offline-safe, since DistributedBrain.ConnectAsync
    /// only persists session files (chat clients are created lazily on goal fork).
    /// </summary>
    [Fact]
    public async Task ConfigRepo_BrainRegistered_ModelFromConfig_ConnectsAtStartup()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-brain
              reasoning_effort: high
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.NotNull(brain);
        Assert.IsType<DistributedBrain>(brain);

        // Startup connect completed: stats are live and carry the CONFIG model, not BRAIN_MODEL.
        var stats = brain.GetStats();
        Assert.NotNull(stats);
        Assert.True(stats.IsConnected, "The Brain must be connected after Program startup.");
        Assert.Equal("copilot/test-brain", stats.Model);
        Assert.Equal(Constants.DefaultBrainContextWindow, stats.MaxContextTokens);

        var logs = StartupLogs(factory);
        Assert.Contains(logs, m => m.Contains($"{BrainEnabledLog} copilot/test-brain", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("Connecting Brain", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("Brain connected.", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, m => m.Contains("mechanical mode", StringComparison.Ordinal));
    }

    /// <summary>
    /// Context window comes from <see cref="HiveConfigFile.TryGetContextWindowForModel"/> when the
    /// model is in the global <c>available_models</c> catalog — even with BRAIN_CONTEXT_WINDOW env set.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_BrainRegistered_ContextWindowFromCatalog_EnvHasNoEffect()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/catalog-model
              reasoning_effort: high
            models:
              available_models:
                - name: copilot/catalog-model
                  context_window: 123456
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.NotNull(brain);
        var stats = brain.GetStats();
        Assert.NotNull(stats);
        // 123456 from the catalog wins over BRAIN_CONTEXT_WINDOW=999999.
        Assert.Equal(123456, stats.MaxContextTokens);
    }

    /// <summary>
    /// Context window falls back to <see cref="Constants.DefaultBrainContextWindow"/> when the model
    /// is absent from the catalog — BRAIN_CONTEXT_WINDOW env has no effect.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_BrainRegistered_ContextWindowDefault_WhenModelAbsentFromCatalog()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/uncatalogued-model
              reasoning_effort: high
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.NotNull(brain);
        var stats = brain.GetStats();
        Assert.NotNull(stats);
        // No catalog entry → default constant, NOT BRAIN_CONTEXT_WINDOW=999999.
        Assert.Equal(Constants.DefaultBrainContextWindow, stats.MaxContextTokens);
    }

    /// <summary>
    /// Max steps come directly from <c>orchestrator.brain_max_steps</c> (a non-nullable int) —
    /// BRAIN_MAX_STEPS env has no effect.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_BrainRegistered_MaxStepsFromConfig_EnvHasNoEffect()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/steps-model
              reasoning_effort: high
              brain_max_steps: 7
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.NotNull(brain);
        var stats = brain.GetStats();
        Assert.NotNull(stats);
        // 7 from config wins over BRAIN_MAX_STEPS=424242.
        Assert.Equal(7, stats.MaxSteps);
    }

    /// <summary>
    /// A parsed config that OMITS <c>orchestrator: model:</c> leaves <see cref="OrchestratorConfig.Model"/>
    /// at <c>null</c> (UNCONFIGURED — Slice 3a parse-time normalization), so the Brain is NOT
    /// registered. BRAIN_MODEL env (set) must NOT seed the model.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_ModelOmitted_BrainNotRegistered_GetServiceNull()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              reasoning_effort: low
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.Null(brain);

        var logs = StartupLogs(factory);
        Assert.Contains(logs, m => m.Contains(BrainDisabledLog, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, m => m.Contains(BrainEnabledLog, StringComparison.Ordinal));
    }

    // ── Brain NOT registered when orchestrator.model is blank/whitespace ──────

    /// <summary>
    /// A parsed config whose <c>orchestrator.model</c> is whitespace-only is treated as NOT
    /// configured: the Brain descriptor is absent (GetService returns null), no crash, and the
    /// startup log reports the config-driven disable message. BRAIN_MODEL env is set and ignored.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_BlankModel_BrainNotRegistered_GetServiceNull_EnvHasNoEffect()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: "   "
              reasoning_effort: low
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.Null(brain);

        // Consumers degrade: the host started and core singletons resolve with a null Brain.
        Assert.NotNull(factory.Services.GetService<GoalDispatcher>());

        var logs = StartupLogs(factory);
        Assert.Contains(logs, m => m.Contains(BrainDisabledLog, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, m => m.Contains(BrainEnabledLog, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, m => m.Contains("BRAIN_MODEL", StringComparison.Ordinal));
    }

    // ── Reasoning validation is NON-FATAL at startup (Slice 3c) ─────────────

    /// <summary>
    /// A config repo whose hive-config.yaml has reasoning-effort validation problems
    /// (orchestrator model set without reasoning_effort, worker model set without
    /// reasoning_effort) must NOT abort startup: the app starts, the errors are logged,
    /// and <see cref="HiveConfigFile.ValidateReasoningEffort"/> still returns them.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_ReasoningInvalid_StartupContinues_ErrorsLogged()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-brain
            workers:
              coder:
                model: copilot/coder-model
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);

        // The app started: core singletons resolve (no startup abort).
        Assert.NotNull(factory.Services.GetService<GoalDispatcher>());

        // The validation method still returns the errors.
        var config = factory.Services.GetRequiredService<HiveConfigFile>();
        var errors = config.ValidateReasoningEffort();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("orchestrator", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("workers.coder", StringComparison.Ordinal));

        // The errors are logged for the operator (and the Models tab) to fix.
        var logs = StartupLogs(factory);
        Assert.Contains(logs, m => m.Contains("Config validation error —", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("orchestrator.reasoning_effort", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("workers.coder.reasoning_effort", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains(
            "Reasoning-effort validation found 2 problem(s); continuing startup", StringComparison.Ordinal));
    }

    /// <summary>
    /// Stronger non-fatal proof: the host actually serves HTTP requests even when
    /// reasoning-effort validation fails. The <c>/health</c> endpoint returns 200 OK,
    /// proving the full ASP.NET pipeline (middleware + endpoint routing) started —
    /// not just DI service resolution.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_ReasoningInvalid_HostServesHttpRequests()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-brain
            workers:
              coder:
                model: copilot/coder-model
            """;

        await using var factory = await BootWithConfigRepoAsync(yaml);
        using var client = factory.CreateClient();

        // If startup had thrown (the old fatal behavior), CreateClient/GetAsync would
        // propagate the InvalidOperationException. A 200 OK proves the host fully started.
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    // ── Registration seam: startup connect uses the registered Brain ───────────

    /// <summary>
    /// The startup connect block (<c>GetService&lt;IDistributedBrain&gt;()</c> + ConnectAsync) drives
    /// whatever instance is registered. Replacing Program's factory with a stub proves the block
    /// invokes ConnectAsync on the registered Brain — no real SharpCoder/Copilot network connect.
    /// </summary>
    [Fact]
    public async Task ConfigRepo_BrainReplacedWithStub_StartupConnectsRegisteredBrain()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/stub-model
              reasoning_effort: high
            """;

        var stub = new RecordingBrain();
        await using var factory = await BootWithConfigRepoAsync(yaml, brainOverride: stub);
        var brain = factory.Services.GetService<IDistributedBrain>();

        Assert.Same(stub, brain);
        Assert.Equal(1, stub.ConnectCount);

        var logs = StartupLogs(factory);
        Assert.Contains(logs, m => m.Contains($"{BrainEnabledLog} copilot/stub-model", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("Connecting Brain", StringComparison.Ordinal));
    }

    // ── Test infrastructure ─────────────────────────────────────────────────────

    private static IReadOnlyList<string> StartupLogs(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<DashboardLogSink>()
            .GetRecent(int.MaxValue)
            .Select(e => e.Message)
            .ToList();

    /// <summary>
    /// Creates a local git config repo containing the given hive-config.yaml, then boots the REAL
    /// Program with <c>--config-repo</c> / <c>--config-repo-path</c> (passed through
    /// <see cref="IWebHostBuilder.UseSetting"/>, which WebApplicationFactory converts into
    /// <c>--key=value</c> args for Program.Main).
    /// </summary>
    private async Task<ConfigRepoBrainFactory> BootWithConfigRepoAsync(
        string yaml, IDistributedBrain? brainOverride = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"copilothive-brainreg-{Guid.NewGuid():N}");
        _tempRoots.Add(root);
        Directory.CreateDirectory(root);

        var sourceRepo = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(sourceRepo);
        await RunGitAsync(sourceRepo, ["init"]);
        await RunGitAsync(sourceRepo, ["config", "user.email", "test@test.com"]);
        await RunGitAsync(sourceRepo, ["config", "user.name", "BrainRegistrationTests"]);
        await File.WriteAllTextAsync(Path.Combine(sourceRepo, "hive-config.yaml"), yaml);
        await RunGitAsync(sourceRepo, ["add", "hive-config.yaml"]);
        await RunGitAsync(sourceRepo, ["commit", "-m", "initial config"]);

        var clonePath = Path.Combine(root, "clone");
        return new ConfigRepoBrainFactory(sourceRepo, clonePath, brainOverride);
    }

    private static async Task RunGitAsync(string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Force LF line endings regardless of the host's global/system git config.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        // Disable commit signing: a host with commit.gpgsign=true globally configured can
        // make git commit contend for the GPG agent and intermittently fail.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git exited with code {proc.ExitCode}: {stdoutTask.Result}\n{stderrTask.Result}".Trim());
        }
    }
}

/// <summary>
/// Boots the real CopilotHive application with a local config repo via <c>--config-repo</c>.
/// An optional <see cref="IDistributedBrain"/> stub replaces Program's real registration
/// (registration seam: the test services are replayed onto the host builder AFTER Program's own
/// registrations, so removal + re-add wins).
/// </summary>
internal sealed class ConfigRepoBrainFactory : WebApplicationFactory<Program>
{
    private readonly string _repoUrl;
    private readonly string _clonePath;
    private readonly string _stateDir;
    private readonly string? _previousStateDir;
    private readonly IDistributedBrain? _brainOverride;

    public ConfigRepoBrainFactory(string repoUrl, string clonePath, IDistributedBrain? brainOverride = null)
    {
        _repoUrl = repoUrl;
        _clonePath = clonePath;
        _brainOverride = brainOverride;
        _stateDir = Path.Combine(Path.GetTempPath(), $"copilothive-brainreg-state-{Guid.NewGuid():N}");
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
        Directory.CreateDirectory(_stateDir);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // WebApplicationFactory converts these host settings into --config-repo=... and
        // --config-repo-path=... args for Program.Main, driving the with-repo registration branch.
        builder.UseSetting("config-repo", _repoUrl);
        builder.UseSetting("config-repo-path", _clonePath);

        if (_brainOverride is not null)
        {
            builder.ConfigureServices(services =>
            {
                // Remove Program's real Brain registration, replace with the stub (last wins).
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IDistributedBrain));
                if (existing is not null)
                    services.Remove(existing);
                services.AddSingleton(_brainOverride);
            });
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);

        if (!disposing || !Directory.Exists(_stateDir))
            return;

        try { Directory.Delete(_stateDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Minimal <see cref="IDistributedBrain"/> stub that records startup connect invocations.
/// </summary>
file sealed class RecordingBrain : IDistributedBrain
{
    public int ConnectCount { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        ConnectCount++;
        return Task.CompletedTask;
    }

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
        => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct)
        => Task.CompletedTask;

    public BrainStats? GetStats() => null;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
        => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task ResetSessionAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
