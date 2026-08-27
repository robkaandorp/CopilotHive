using CopilotHive.Configuration;
using CopilotHive.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using System.Text.Json;
using System.Text.Json.Serialization;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Facade tests for the orchestrator/workers/composer settings operations of
/// <see cref="IConfigFacade"/> (step 4 of the Blazor loopback-HTTP removal). Proves every
/// outcome of the settings failure table — kind, exact message, and exception propagation —
/// is preserved relative to the pre-facade endpoint handlers:
/// <list type="bullet">
/// <item>null-<see cref="HiveConfigFile"/> reads → <see cref="FacadeErrorKind.NotFound"/> with
/// "Config repo not configured." (the endpoint's 404 <c>{error}</c> payload);</item>
/// <item>absent-<see cref="ConfigModelService"/> saves → <see cref="FacadeErrorKind.NotConfigured"/>
/// with "Config service is not configured." (the endpoint's 500 problem-details body);</item>
/// <item>SaveComposerAsync invalid notification mode/events → <see cref="FacadeErrorKind.BadRequest"/>
/// carrying the service's exact message (the endpoint's 400 <c>{error}</c> payload);</item>
/// <item>any other exception is RETHROWN — the facade catches ONLY the exception types the
/// pre-facade handlers caught.</item>
/// </list>
/// </summary>
/// <remarks>
/// Each test is removal-proof: it fails if the mapped kind, the exact message, or the
/// catch/propagate decision is removed or changed. All async coordination is deterministic
/// (faulted tasks / TCS gates); there are no timing-based waits.
/// </remarks>
[Collection("HiveIntegration")]
public class ConfigFacadeSettingsTests
{
    // ── Exact messages the pre-facade handlers produced ────────────────────

    private const string ConfigNotConfiguredMessage = "Config repo not configured.";
    private const string CrudNotConfiguredMessage = "Config service is not configured.";

    /// <summary>
    /// The endpoint wire JSON options — Web defaults plus the global snake_case enum converter
    /// registered in production by <c>Program.AddHiveJsonOptions</c>. Plain Web options would
    /// serialize enums numerically, which is NOT the wire contract.
    /// </summary>
    private static readonly JsonSerializerOptions ComposerWireJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new Program.GlobalStringEnumConverter() },
        };

    // ── Null-HiveConfigFile reads → NotFound ────────────────────────────────

    /// <summary>
    /// Null-HiveConfigFile GetOrchestrator → NotFound with the pre-facade handler's error body
    /// ("Config repo not configured." → the endpoint's 404 <c>{error}</c> payload).
    /// </summary>
    [Fact]
    public async Task GetOrchestrator_NullHiveConfigFile_ReturnsNotFound()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(hiveConfig: null);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetOrchestrator();

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal(ConfigNotConfiguredMessage, result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// Null-HiveConfigFile GetWorkers → NotFound with the pre-facade handler's error body.
    /// </summary>
    [Fact]
    public async Task GetWorkers_NullHiveConfigFile_ReturnsNotFound()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(hiveConfig: null);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetWorkers();

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal(ConfigNotConfiguredMessage, result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// Null-HiveConfigFile GetComposer → NotFound with the pre-facade handler's error body.
    /// </summary>
    [Fact]
    public async Task GetComposer_NullHiveConfigFile_ReturnsNotFound()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(hiveConfig: null);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetComposer();

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal(ConfigNotConfiguredMessage, result.Error);
        Assert.Null(result.Value);
    }

    // ── Absent-service saves → NotConfigured with the exact message ─────────

    /// <summary>
    /// Every settings save with its service absent → NotConfigured with the EXACT message the
    /// pre-facade handler emitted (rendered as a 500 problem-details body). Removing a guard,
    /// changing a message, or swapping the kind fails this test.
    /// </summary>
    [Fact]
    public async Task ServiceAbsent_AllSettingsSaves_ReturnNotConfiguredWithExactMessage()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        });
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var orchestrator = await facade.SaveOrchestratorAsync(
            new OrchestratorSettingsUpdate(5, null, null, null, null, null));
        Assert.False(orchestrator.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, orchestrator.Kind);
        Assert.Equal(CrudNotConfiguredMessage, orchestrator.Error);

        var workers = await facade.SaveWorkersAsync(new Dictionary<string, int> { ["coder"] = 50000 });
        Assert.False(workers.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, workers.Kind);
        Assert.Equal(CrudNotConfiguredMessage, workers.Error);

        var composer = await facade.SaveComposerAsync(
            new ComposerSettingsUpdate(MaxSteps: 75), TestContext.Current.CancellationToken);
        Assert.False(composer.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, composer.Kind);
        Assert.Equal(CrudNotConfiguredMessage, composer.Error);
    }

    // ── SaveComposerAsync invalid input → BadRequest with the service message ─

    /// <summary>
    /// SaveComposerAsync with an invalid notification mode → BadRequest carrying the real
    /// service's exact ArgumentException message (the endpoint's 400 <c>{error}</c> payload),
    /// and nothing is mutated.
    /// </summary>
    [Fact]
    public async Task SaveComposerAsync_InvalidMode_ReturnsBadRequestWithServiceMessage()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(EventNotificationsMode: "bogus"),
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
            Assert.Equal(
                "Invalid event notification mode 'bogus'. Valid values: passive, active, off.",
                result.Error);
            // Validation runs before mutation: the composer section stays untouched.
            Assert.Null(config.Composer);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// SaveComposerAsync with an invalid active event name → BadRequest carrying the real
    /// service's exact ArgumentException message, and nothing is mutated.
    /// </summary>
    [Fact]
    public async Task SaveComposerAsync_InvalidEvent_ReturnsBadRequestWithServiceMessage()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(EventNotificationsActiveEvents: ["not_an_event"]),
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
            Assert.Contains("Invalid active event 'not_an_event'.", result.Error);
            Assert.Null(config.Composer);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Rethrow: unexpected exceptions propagate (no catch-alls) ────────────

    /// <summary>
    /// The rethrow case on SaveOrchestratorAsync: an unexpected exception (neither
    /// ArgumentException nor InvalidOperationException) from the persistence layer propagates
    /// out of the facade unwrapped — the exact class of failure the pre-facade handler let
    /// bubble to the 500 middleware.
    /// </summary>
    [Fact]
    public async Task SaveOrchestratorAsync_UnexpectedPersistenceException_PropagatesUnwrapped()
    {
        var (config, _, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            var failingService = new ConfigModelService(
                config,
                new ThrowingConfigRepoManager("https://example.com/config.git", dir,
                    new IOException("simulated storage outage")),
                NullLogger<ConfigModelService>.Instance);

            using var factory = new ConfigFacadeTests.FacadeFactory(config, failingService);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var ex = await Assert.ThrowsAsync<IOException>(
                () => facade.SaveOrchestratorAsync(
                    new OrchestratorSettingsUpdate(5, null, null, null, null, null)));
            Assert.Equal("simulated storage outage", ex.Message);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// The rethrow case on SaveWorkersAsync: an unexpected exception from the persistence
    /// layer propagates out of the facade unwrapped.
    /// </summary>
    [Fact]
    public async Task SaveWorkersAsync_UnexpectedPersistenceException_PropagatesUnwrapped()
    {
        var (config, _, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            var failingService = new ConfigModelService(
                config,
                new ThrowingConfigRepoManager("https://example.com/config.git", dir,
                    new ApplicationException("unexpected storage outage")),
                NullLogger<ConfigModelService>.Instance);

            using var factory = new ConfigFacadeTests.FacadeFactory(config, failingService);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var ex = await Assert.ThrowsAsync<ApplicationException>(
                () => facade.SaveWorkersAsync(new Dictionary<string, int> { ["coder"] = 50000 }));
            Assert.Equal("unexpected storage outage", ex.Message);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// The rethrow case on SaveComposerAsync: an unexpected exception (neither
    /// ArgumentException) from the persistence layer propagates out of the facade unwrapped —
    /// the facade's composer catch catches ONLY ArgumentException.
    /// </summary>
    [Fact]
    public async Task SaveComposerAsync_UnexpectedPersistenceException_PropagatesUnwrapped()
    {
        var (config, _, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            var failingService = new ConfigModelService(
                config,
                new ThrowingConfigRepoManager("https://example.com/config.git", dir,
                    new InvalidOperationException("unexpected storage outage")),
                NullLogger<ConfigModelService>.Instance);

            using var factory = new ConfigFacadeTests.FacadeFactory(config, failingService);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => facade.SaveComposerAsync(
                    new ComposerSettingsUpdate(MaxSteps: 75), TestContext.Current.CancellationToken));
            Assert.Equal("unexpected storage outage", ex.Message);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Success paths against the real ConfigModelService ───────────────────

    /// <summary>
    /// GetOrchestrator with a registered config → success with every property projected
    /// field-for-field onto <see cref="OrchestratorConfigDto"/> (the pre-facade handler
    /// serialized the raw <see cref="OrchestratorConfig"/> object).
    /// </summary>
    [Fact]
    public async Task GetOrchestrator_RegisteredConfig_ReturnsFullyProjectedData()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig
            {
                Model = "copilot/gpt-5",
                MaxIterations = 7,
                MaxRetriesPerTask = 4,
                MaxParallelGoals = 2,
                VerboseLogging = true,
                BrainMaxSteps = 60,
                BranchCleanupDelayHours = 12,
                WorkerTaskTimeoutMinutes = 25,
                ReasoningEffort = "high",
            },
            Models = new ModelsConfig { AvailableModels = [] }
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetOrchestrator();

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        var dto = Assert.IsType<OrchestratorConfigDto>(result.Value);
        Assert.Equal("copilot/gpt-5", dto.Model);
        Assert.Equal(7, dto.MaxIterations);
        Assert.Equal(4, dto.MaxRetriesPerTask);
        Assert.Equal(2, dto.MaxParallelGoals);
        Assert.True(dto.VerboseLogging);
        Assert.Equal(60, dto.BrainMaxSteps);
        Assert.Equal(12, dto.BranchCleanupDelayHours);
        Assert.Equal(25, dto.WorkerTaskTimeoutMinutes);
        Assert.Equal("high", dto.ReasoningEffort);
    }

    /// <summary>
    /// GetWorkers with a registered config → success with the TOP-LEVEL role-keyed dictionary
    /// (model, premiumModel, contextWindow per role) — the exact shape the pre-facade handler
    /// produced.
    /// </summary>
    [Fact]
    public async Task GetWorkers_RegisteredConfig_ReturnsRoleKeyedDictionary()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "copilot/coder", PremiumModel = "copilot/coder-premium", ContextWindow = 128000 },
                ["tester"] = new() { Model = "copilot/tester" }
            }
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetWorkers();

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        var dto = Assert.IsType<WorkersConfigDto>(result.Value);
        Assert.Equal(2, dto.Count);
        var coder = dto["coder"];
        Assert.Equal("copilot/coder", coder.Model);
        Assert.Equal("copilot/coder-premium", coder.PremiumModel);
        Assert.Equal(128000, coder.ContextWindow);
        var tester = dto["tester"];
        Assert.Equal("copilot/tester", tester.Model);
        Assert.Null(tester.PremiumModel);
        Assert.Equal(0, tester.ContextWindow);
    }

    /// <summary>
    /// GetComposer with a registered config → success with the runtime-effective projection
    /// (model, maxSteps, reasoningEffort, typed eventNotifications). The stored active-event
    /// list is deliberately NONCANONICAL (<c>package_published</c> before
    /// <c>goal_completed</c>) so the assertion also proves the projection reorders rather than
    /// echoing storage order.
    /// </summary>
    [Fact]
    public async Task GetComposer_RegisteredConfig_ReturnsEffectiveProjection()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
            Composer = new ComposerConfig
            {
                Model = "copilot/composer",
                MaxSteps = 80,
                ReasoningEffort = "medium",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    // Stored out of canonical order on purpose.
                    ActiveEvents = ["package_published", "goal_completed"],
                    ThrottleSeconds = 60
                }
            }
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetComposer();

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        var dto = Assert.IsType<ComposerConfigDto>(result.Value);
        Assert.Equal("copilot/composer", dto.Model);
        Assert.Equal(80, dto.MaxSteps);
        Assert.Equal("medium", dto.ReasoningEffort);
        Assert.Equal("active", dto.EventNotifications.Mode);
        // Canonical whitelist order, NOT the stored order.
        Assert.Equal(["goal_completed", "package_published"], dto.EventNotifications.ActiveEvents);
        Assert.Equal(60, dto.EventNotifications.ThrottleSeconds);
    }

    /// <summary>
    /// The canonical event-ordering contract: the stored <c>activeEvents</c> list is supplied in
    /// FULLY REVERSED (i.e. maximally noncanonical) whitelist order, and GetComposer must project
    /// it back into the canonical nine-event whitelist order —
    /// <c>goal_completed, goal_failed, ci_failed, issue_raised, package_published, ci_succeeded,
    /// release_completed, goal_dispatched, issue_resolved</c> — exactly as the pre-facade
    /// ConfigHub projection did.
    /// <para>
    /// This test is removal-proof: dropping the canonicalization projection from
    /// <c>ConfigFacade.GetComposer</c> (echoing the stored list instead) yields the reversed
    /// order and fails, and permuting the canonical whitelist array changes the projected order
    /// and also fails. Both the DTO list and the serialized wire JSON are asserted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetComposer_NoncanonicalStoredEventOrder_ProjectsCanonicalWhitelistOrder()
    {
        // The canonical whitelist order enforced by the facade.
        string[] canonical =
        [
            "goal_completed", "goal_failed", "ci_failed", "issue_raised", "package_published",
            "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved",
        ];
        // Storage order = the exact reverse, so ANY order-preserving projection differs.
        var stored = canonical.Reverse().ToList();

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
            Composer = new ComposerConfig
            {
                Model = "copilot/composer",
                MaxSteps = 80,
                ReasoningEffort = "medium",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    ActiveEvents = stored,
                    ThrottleSeconds = 60
                }
            }
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetComposer();

        Assert.True(result.Success);
        var dto = Assert.IsType<ComposerConfigDto>(result.Value);
        Assert.Equal(canonical, dto.EventNotifications.ActiveEvents);
        // Guard against a vacuous assertion: the stored order really is different.
        Assert.NotEqual(stored, dto.EventNotifications.ActiveEvents);
        // The wire JSON carries the same canonical order.
        var json = JsonSerializer.Serialize(dto, ComposerWireJsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            canonical,
            document.RootElement.GetProperty("eventNotifications").GetProperty("activeEvents")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    /// <summary>
    /// A SUBSET of the whitelist, stored in noncanonical order, is projected into the canonical
    /// relative order of exactly those events (absent events are never invented). Complements the
    /// full-reversal case above: it proves the projection filters through the whitelist rather
    /// than sorting whatever it is given.
    /// </summary>
    [Fact]
    public async Task GetComposer_NoncanonicalStoredSubset_ProjectsCanonicalRelativeOrder()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
            Composer = new ComposerConfig
            {
                Model = "copilot/composer",
                MaxSteps = 80,
                ReasoningEffort = "medium",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    // Noncanonical: package_published (canonical index 4) stored first,
                    // ci_failed (index 2) second, goal_completed (index 0) last.
                    ActiveEvents = ["package_published", "ci_failed", "goal_completed"],
                    ThrottleSeconds = 60
                }
            }
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetComposer();

        Assert.True(result.Success);
        var dto = Assert.IsType<ComposerConfigDto>(result.Value);
        Assert.Equal(
            ["goal_completed", "ci_failed", "package_published"],
            dto.EventNotifications.ActiveEvents);
        // Only the stored three are present — the projection does not pad with defaults.
        Assert.Equal(3, dto.EventNotifications.ActiveEvents.Count);
    }

    /// <summary>
    /// GetComposer with a null Composer section → success with the effective defaults the
    /// pre-facade handler produced (null model, default max steps, null reasoning effort,
    /// passive mode, the four default events, nine valid events, throttle 30).
    /// </summary>
    [Fact]
    public async Task GetComposer_NullComposer_ReturnsEffectiveDefaults()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetComposer();

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        var dto = Assert.IsType<ComposerConfigDto>(result.Value);
        Assert.Null(dto.Model);
        Assert.Equal(50, dto.MaxSteps);
        Assert.Null(dto.ReasoningEffort);
        Assert.Equal("passive", dto.EventNotifications.Mode);
        Assert.Equal(["goal_completed", "goal_failed", "ci_failed", "issue_raised"], dto.EventNotifications.ActiveEvents);
        Assert.Equal(30, dto.EventNotifications.ThrottleSeconds);
    }

    // ── Save success paths against the real ConfigModelService ──────────────

    /// <summary>
    /// SaveOrchestratorAsync with the real service → success and the settings are persisted
    /// onto the live config.
    /// </summary>
    [Fact]
    public async Task SaveOrchestratorAsync_RealService_PersistsSettings()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.SaveOrchestratorAsync(
                new OrchestratorSettingsUpdate(MaxIterations: 9, MaxRetriesPerTask: 2, MaxParallelGoals: 3,
                    VerboseLogging: true, BrainMaxSteps: 70, BranchCleanupDelayHours: 6));

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);
            Assert.Equal(9, config.Orchestrator.MaxIterations);
            Assert.Equal(2, config.Orchestrator.MaxRetriesPerTask);
            Assert.Equal(3, config.Orchestrator.MaxParallelGoals);
            Assert.True(config.Orchestrator.VerboseLogging);
            Assert.Equal(70, config.Orchestrator.BrainMaxSteps);
            Assert.Equal(6, config.Orchestrator.BranchCleanupDelayHours);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// SaveWorkersAsync with the real service → success and the context windows are persisted
    /// onto the live config (creating missing worker entries).
    /// </summary>
    [Fact]
    public async Task SaveWorkersAsync_RealService_PersistsContextWindows()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.SaveWorkersAsync(
                new Dictionary<string, int> { ["coder"] = 50000, ["reviewer"] = 64000 });

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);
            Assert.Equal(50000, config.Workers["coder"].ContextWindow);
            Assert.Equal(64000, config.Workers["reviewer"].ContextWindow);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// SaveComposerAsync with the real service → success and the settings are persisted onto
    /// the live config (creating the composer section when absent).
    /// </summary>
    [Fact]
    public async Task SaveComposerAsync_RealService_PersistsSettings()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(
                    MaxSteps: 90,
                    EventNotificationsMode: "active",
                    EventNotificationsActiveEvents: ["goal_completed", "ci_failed"],
                    EventNotificationsThrottleSeconds: 45),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);
            Assert.NotNull(config.Composer);
            Assert.Equal(90, config.Composer!.MaxSteps);
            Assert.Equal("active", config.Composer.EventNotifications!.Mode);
            Assert.Equal(["goal_completed", "ci_failed"], config.Composer.EventNotifications.ActiveEvents);
            Assert.Equal(45, config.Composer.EventNotifications.ThrottleSeconds);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Cancellation propagation ─────────────────────────────────────────────

    /// <summary>
    /// SaveComposerAsync cancellation propagates: the facade forwards ct and must NOT swallow
    /// OperationCanceledException (the pre-facade handler never caught it either). The fake
    /// repo's CommitFileAsync parks on a TCS gate; cancelling the token deterministically
    /// unwinds the wait as OperationCanceledException. No timing-based waits.
    /// </summary>
    [Fact]
    public async Task SaveComposerAsync_CancellationDuringPersist_PropagatesOperationCanceled()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"copilothive-facade-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = new GatedConfigRepoManager("https://example.com/config.git", dir, gate);
        var service = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var saveCall = facade.SaveComposerAsync(
                new ComposerSettingsUpdate(MaxSteps: 75), cts.Token);

            // Deterministically unwind the gated write via cancellation (never a timing wait).
            cts.Cancel();
            gate.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saveCall);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }
}
