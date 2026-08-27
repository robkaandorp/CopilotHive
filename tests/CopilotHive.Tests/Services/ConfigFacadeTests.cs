using System.Net;
using System.Text;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Integration-style tests for <see cref="ConfigFacade"/> resolved from the real DI container
/// (<see cref="WebApplicationFactory{Program}"/> booting <c>Program.cs</c>), with the REAL sealed
/// <see cref="ConfigModelService"/> over a seeded <see cref="HiveConfigFile"/> and a
/// <see cref="FakeConfigRepoManager"/> (no real git). Proves every outcome of the facade's
/// failure table — kind, exact message, and exception propagation — is preserved relative to
/// the pre-facade endpoint handlers.
/// </summary>
/// <remarks>
/// Each test is removal-proof: it fails if the mapped kind, the exact message, or the
/// catch/propagate decision is removed or changed. All async coordination is deterministic
/// (TCS gates / cancelled tokens); there are no timing-based waits.
/// </remarks>
[Collection("HiveIntegration")]
public class ConfigFacadeTests
{
    // ── Exact problem-details messages the pre-facade handlers produced ─────

    private const string SaveNotConfiguredMessage =
        "Config repo is not configured — model changes cannot be persisted.";
    private const string DiscoverNotConfiguredMessage = "Model discovery service is not configured.";
    private const string CrudNotConfiguredMessage = "Config service is not configured.";
    private const string RemoveNotFoundMessageFormat = "Model '{0}' not found.";

    // ── DI-container harness ─────────────────────────────────────────────────

    /// <summary>
    /// Boots the real application (Testing environment, no <c>--config-repo</c>) and replaces
    /// the app's own <see cref="IConfigFacade"/> registration with one wired to this
    /// scenario's (possibly absent) optional dependencies — mirroring the production factory's
    /// <c>sp.GetService&lt;T&gt;</c> optional-resolution pattern.
    /// </summary>
    internal sealed class FacadeFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir;
        private readonly string? _previousStateDir;
        private readonly HiveConfigFile? _hiveConfig;
        private readonly ConfigModelService? _configModel;
        private readonly ModelDiscoveryService? _discovery;
        private readonly IBrainRepoManager? _repoManager;

        public FacadeFactory(
            HiveConfigFile? hiveConfig,
            ConfigModelService? configModel = null,
            ModelDiscoveryService? discovery = null,
            IBrainRepoManager? repoManager = null)
        {
            _hiveConfig = hiveConfig;
            _configModel = configModel;
            _discovery = discovery;
            _repoManager = repoManager;
            _stateDir = Path.Combine(Path.GetTempPath(), $"copilothive-facade-{Guid.NewGuid():N}");
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Directory.CreateDirectory(_stateDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            try { Directory.Delete(_stateDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConfigFacade>();
                services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
                    _hiveConfig,
                    _configModel,
                    _discovery,
                    sp.GetRequiredService<ILogger<ConfigFacade>>(),
                    _repoManager));

                if (_configModel is not null)
                {
                    services.RemoveAll<ConfigModelService>();
                    services.AddSingleton(_configModel);
                }
                if (_discovery is not null)
                {
                    services.RemoveAll<ModelDiscoveryService>();
                    services.AddSingleton(_discovery);
                }
            });
        }
    }

    // ── Shared builders ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a seeded config + the real <see cref="ConfigModelService"/> over a
    /// <see cref="FakeConfigRepoManager"/> (WriteConfigAsync writes the file;
    /// CommitFileAsync is a recorded no-op, so no real git runs).
    /// </summary>
    internal static (HiveConfigFile Config, ConfigModelService Service, string Dir) CreateRealService(
        Action<HiveConfigFile>? seed = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"copilothive-facade-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        seed?.Invoke(config);
        var repo = new FakeConfigRepoManager("https://example.com/config.git", dir);
        var service = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        return (config, service, dir);
    }

    internal static void CleanupDir(string? dir)
    {
        if (dir is null || !Directory.Exists(dir))
            return;
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── GetModels ────────────────────────────────────────────────────────────

    /// <summary>
    /// Null-HiveConfigFile GetModels → NotFound with the pre-facade handler's error body
    /// ("Config repo not configured." → the endpoint's 404 <c>{error}</c> payload).
    /// </summary>
    [Fact]
    public async Task GetModels_NullHiveConfigFile_ReturnsNotFound()
    {
        using var factory = new FacadeFactory(hiveConfig: null);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetModels();

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Config repo not configured.", result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// GetModels with a registered config → success with the full projection. Proves the null
    /// check is the ONLY NotFound path and that every DTO field is projected through
    /// ParseLenient / entry-wise mapping as before the facade.
    /// </summary>
    [Fact]
    public async Task GetModels_RegisteredConfig_ReturnsFullyProjectedData()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "copilot/gpt-5", ReasoningEffort = "High" },
            Composer = new ComposerConfig { Model = "copilot/gpt-5-mini" },
            Models = new ModelsConfig
            {
                CompactionModel = "copilot/compact",
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/gpt-5", ContextWindow = 128000, Description = "Main", SupportsVision = true }
                ],
                SubAgentModels =
                [
                    new ModelEntry { Name = "copilot/sub", ContextWindow = 64000, ReasoningEffort = "extra_high", Description = "Sub" }
                ]
            },
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "copilot/coder", PremiumModel = "copilot/coder-premium" }
            }
        };
        using var factory = new FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetModels();

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        var dto = Assert.IsType<ModelsConfigDto>(result.Value);
        Assert.Equal("copilot/gpt-5", dto.Orchestrator);
        Assert.Equal("copilot/gpt-5-mini", dto.Composer);
        Assert.Equal("copilot/compact", dto.Compaction);
        // ParseLenient normalization of a non-canonical stored spelling → the enum value.
        Assert.Equal(ReasoningEffort.High, dto.OrchestratorReasoningEffort);
        Assert.Equal("copilot/coder", dto.Workers["coder"].Model);
        Assert.Equal("copilot/coder-premium", dto.Workers["coder"].PremiumModel);
        var available = Assert.Single(dto.AvailableModels!);
        Assert.Equal("copilot/gpt-5", available.Name);
        Assert.Equal(128000, available.ContextWindow);
        Assert.Equal("Main", available.Description);
        Assert.True(available.SupportsVision);
        var sub = Assert.Single(dto.SubAgentModels!);
        Assert.Equal("copilot/sub", sub.Name);
        Assert.Equal(64000, sub.ContextWindow);
        Assert.Equal(ReasoningEffort.ExtraHigh, sub.ReasoningEffort);
        Assert.Equal("Sub", sub.Description);
        Assert.Equal(ReasoningEffort.ExtraHigh, dto.SubAgentModelReasoning!["copilot/sub"]);
    }

    // ── Service-absent → NotConfigured with the exact problem-details messages ──

    /// <summary>
    /// Every persistence/discovery operation with its service absent → NotConfigured with the
    /// EXACT message the pre-facade handler emitted (rendered as a 500 problem-details body).
    /// Removing a guard, changing a message, or swapping the kind fails this test.
    /// </summary>
    [Fact]
    public async Task ServiceAbsent_AllPersistenceAndDiscoveryOps_ReturnNotConfiguredWithExactMessages()
    {
        using var factory = new FacadeFactory(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        });
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var save = await facade.SaveModelsAsync(
            new ModelConfigUpdate(OrchestratorModel: "copilot/x", ComposerModel: null,
                WorkerModels: null, PremiumWorkerModels: null, CompactionModel: null),
            TestContext.Current.CancellationToken);
        Assert.False(save.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, save.Kind);
        Assert.Equal(SaveNotConfiguredMessage, save.Error);

        var discover = await facade.DiscoverModelsAsync();
        Assert.False(discover.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, discover.Kind);
        Assert.Equal(DiscoverNotConfiguredMessage, discover.Error);

        var addAvail = await facade.AddAvailableModelAsync(new AvailableModelRequest("m", null));
        Assert.False(addAvail.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, addAvail.Kind);
        Assert.Equal(CrudNotConfiguredMessage, addAvail.Error);

        var updateAvail = await facade.UpdateAvailableModelAsync("m", new AvailableModelRequest("m", null));
        Assert.False(updateAvail.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, updateAvail.Kind);
        Assert.Equal(CrudNotConfiguredMessage, updateAvail.Error);

        var removeAvail = await facade.RemoveAvailableModelAsync("m");
        Assert.False(removeAvail.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, removeAvail.Kind);
        Assert.Equal(CrudNotConfiguredMessage, removeAvail.Error);

        var addSub = await facade.AddSubAgentModelAsync(new SubAgentModelRequest("m", null, null));
        Assert.False(addSub.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, addSub.Kind);
        Assert.Equal(CrudNotConfiguredMessage, addSub.Error);

        var updateSub = await facade.UpdateSubAgentModelAsync("m", new SubAgentModelRequest("m", null, null));
        Assert.False(updateSub.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, updateSub.Kind);
        Assert.Equal(CrudNotConfiguredMessage, updateSub.Error);

        var removeSub = await facade.RemoveSubAgentModelAsync("m");
        Assert.False(removeSub.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, removeSub.Kind);
        Assert.Equal(CrudNotConfiguredMessage, removeSub.Error);
    }

    // ── Available-model CRUD against the real ConfigModelService ─────────────

    [Fact]
    public async Task AddAvailableModelAsync_DuplicateName_ReturnsConflictWithServiceMessage()
    {
        var (config, service, dir) = CreateRealService(cfg =>
            cfg.Models!.AvailableModels = [new ModelEntry { Name = "dup-model" }]);
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.AddAvailableModelAsync(
                new AvailableModelRequest("DUP-MODEL", null));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
            // The exact InvalidOperationException message from the real service.
            Assert.Equal("Model 'DUP-MODEL' already exists in available_models", result.Error);
            Assert.Null(result.Value);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_MissingName_ReturnsNotFoundWithServiceMessage()
    {
        var (config, service, dir) = CreateRealService();
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.UpdateAvailableModelAsync(
                "missing-model", new AvailableModelRequest("missing-model", null));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Model 'missing-model' not found in available_models", result.Error);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task RemoveAvailableModelAsync_MissingName_ReturnsNotFoundWithFacadeMessage()
    {
        var (config, service, dir) = CreateRealService();
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.RemoveAvailableModelAsync("no-such-model");

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal(string.Format(RemoveNotFoundMessageFormat, "no-such-model"), result.Error);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task RemoveAvailableModelAsync_ExistingName_ReturnsSuccessAndRemoves()
    {
        var (config, service, dir) = CreateRealService(cfg =>
            cfg.Models!.AvailableModels = [new ModelEntry { Name = "gone-model" }]);
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.RemoveAvailableModelAsync("gone-model");

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Removed);
            // The removal actually happened against the real service.
            Assert.DoesNotContain(config.Models!.AvailableModels!, m => m.Name == "gone-model");
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ── Sub-agent-model CRUD against the real ConfigModelService ─────────────

    [Fact]
    public async Task AddSubAgentModelAsync_DuplicateName_ReturnsConflictWithServiceMessage()
    {
        var (config, service, dir) = CreateRealService(cfg =>
            cfg.Models!.SubAgentModels = [new ModelEntry { Name = "sa-dup" }]);
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.AddSubAgentModelAsync(
                new SubAgentModelRequest("SA-DUP", null, null));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
            Assert.Equal("Model 'SA-DUP' already exists in sub_agent_models", result.Error);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task UpdateSubAgentModelAsync_MissingName_ReturnsNotFoundWithServiceMessage()
    {
        var (config, service, dir) = CreateRealService();
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.UpdateSubAgentModelAsync(
                "sa-missing", new SubAgentModelRequest("sa-missing", null, null));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Model 'sa-missing' not found in sub_agent_models", result.Error);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public async Task RemoveSubAgentModelAsync_MissingName_ReturnsNotFoundWithFacadeMessage()
    {
        var (config, service, dir) = CreateRealService();
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.RemoveSubAgentModelAsync("sa-no-such");

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal(string.Format(RemoveNotFoundMessageFormat, "sa-no-such"), result.Error);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ── SaveModelsAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// A case-insensitively duplicated reasoning-effort key is the structural validation
    /// failure the real service raises as ArgumentException → the facade maps it to BadRequest
    /// carrying the service's exact message, and nothing is mutated.
    /// </summary>
    [Fact]
    public async Task SaveModelsAsync_DuplicateCaseInsensitiveReasoningKeys_ReturnsBadRequest()
    {
        var (config, service, dir) = CreateRealService(cfg =>
            cfg.Models!.SubAgentModels = [new ModelEntry { Name = "sa-save" }]);
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var update = new ModelConfigUpdate(
                OrchestratorModel: null, ComposerModel: null, WorkerModels: null,
                PremiumWorkerModels: null, CompactionModel: null,
                SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?>
                {
                    ["sa-save"] = ReasoningEffort.High,
                    ["SA-SAVE"] = ReasoningEffort.Low
                });

            var result = await facade.SaveModelsAsync(update, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
            Assert.Equal(
                "subAgentModelReasoning contains duplicate case-insensitive key: 'sa-save'/'SA-SAVE'.",
                result.Error);
            // Validation runs before mutation: the entry's reasoning effort is untouched (null).
            Assert.Null(config.Models!.SubAgentModels!.Single(m => m.Name == "sa-save").ReasoningEffort);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    /// <summary>
    /// SaveModelsAsync cancellation propagates: the facade forwards ct and must NOT swallow
    /// OperationCanceledException (the pre-facade handler never caught it either). The fake
    /// repo's WriteConfigAsync parks on a TCS gate; cancelling the token deterministically
    /// unwinds the wait as OperationCanceledException. No timing-based waits.
    /// </summary>
    [Fact]
    public async Task SaveModelsAsync_CancellationDuringPersist_PropagatesOperationCanceled()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"copilothive-facade-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "copilot/gpt-5" },
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = new GatedConfigRepoManager("https://example.com/config.git", dir, gate);
        var service = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var update = new ModelConfigUpdate(
                OrchestratorModel: "copilot/new-model", ComposerModel: null, WorkerModels: null,
                PremiumWorkerModels: null, CompactionModel: null);

            var saveCall = facade.SaveModelsAsync(update, cts.Token);

            // Deterministically unwind the gated write via cancellation (never a timing wait).
            cts.Cancel();
            gate.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saveCall);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    /// <summary>
    /// The rethrow case: an unexpected exception (neither ArgumentException nor
    /// InvalidOperationException) from the persistence layer propagates out of the facade
    /// unwrapped rather than being swallowed or converted into a FacadeResult — the exact
    /// class of failure the pre-facade handler let bubble to the 500 middleware.
    /// </summary>
    [Fact]
    public async Task SaveModelsAsync_UnexpectedPersistenceException_PropagatesUnwrapped()
    {
        var (config, _, dir) = CreateRealService();
        try
        {
            var failingService = new ConfigModelService(
                config,
                new ThrowingConfigRepoManager("https://example.com/config.git", dir,
                    new IOException("simulated storage outage")),
                NullLogger<ConfigModelService>.Instance);

            using var factory = new FacadeFactory(config, failingService);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var update = new ModelConfigUpdate(
                OrchestratorModel: "copilot/y", ComposerModel: null, WorkerModels: null,
                PremiumWorkerModels: null, CompactionModel: null);

            var ex = await Assert.ThrowsAsync<IOException>(
                () => facade.SaveModelsAsync(update, TestContext.Current.CancellationToken));
            Assert.Equal("simulated storage outage", ex.Message);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    /// <summary>
    /// The same rethrow rule on a CRUD path: an unexpected exception from the service layer is
    /// never converted into a FacadeResult — the facade's CRUD catch catches ONLY
    /// InvalidOperationException. Driven through the DI-registered facade with a repo fake
    /// whose CommitFileAsync throws an arbitrary non-InvalidOperationException failure
    /// (AddSubAgentModelAsync awaits the same commit seam), proving the facade does not
    /// broaden its catch on the CRUD operations either.
    /// </summary>
    [Fact]
    public async Task AddSubAgentModelAsync_UnexpectedPersistenceException_PropagatesUnwrapped()
    {
        var (config, _, dir) = CreateRealService();
        try
        {
            var failingService = new ConfigModelService(
                config,
                new ThrowingConfigRepoManager("https://example.com/config.git", dir,
                    new ApplicationException("unexpected storage outage")),
                NullLogger<ConfigModelService>.Instance);

            using var factory = new FacadeFactory(config, failingService);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var ex = await Assert.ThrowsAsync<ApplicationException>(
                () => facade.AddSubAgentModelAsync(
                    new SubAgentModelRequest("copilot/rethrow", null, null)));
            Assert.Equal("unexpected storage outage", ex.Message);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ── End-to-end round trip through the real service ───────────────────────

    [Fact]
    public async Task AddSubAgentModelThenGetModels_RoundTripsThroughRealService()
    {
        var (config, service, dir) = CreateRealService();
        try
        {
            using var factory = new FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.AddSubAgentModelAsync(
                new SubAgentModelRequest("copilot/round-trip", 128000, ReasoningEffort.High,
                    Description: "Round trip", SupportsVision: true));

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);

            var models = facade.GetModels();
            Assert.True(models.Success);
            var sub = Assert.Single(models.Value!.SubAgentModels!);
            Assert.Equal("copilot/round-trip", sub.Name);
            Assert.Equal(128000, sub.ContextWindow);
            Assert.Equal(ReasoningEffort.High, sub.ReasoningEffort);
            Assert.Equal("Round trip", sub.Description);
            Assert.True(sub.SupportsVision);
            Assert.Equal(ReasoningEffort.High, models.Value.SubAgentModelReasoning!["copilot/round-trip"]);
        }
        finally
        {
            CleanupDir(dir);
        }
    }
}

/// <summary>
/// A config-repo fake whose <see cref="ConfigRepoManager.CommitFileAsync"/> parks on a TCS
/// gate so a test can cancel the token before the commit completes — the deterministic,
/// timing-free way to drive ct propagation through the facade. CommitFileAsync is the same
/// virtual persistence seam the production <c>ConfigModelService</c> awaits inside its
/// validate → mutate → write → commit transaction (and whose OperationCanceledException it
/// rethrows), so cancelling there exercises the identical persistence path.
/// </summary>
internal sealed class GatedConfigRepoManager(string url, string path, TaskCompletionSource gate)
    : ConfigRepoManager(url, path)
{
    public override async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        // Park until the test releases the gate; WaitAsync(ct) throws
        // OperationCanceledException as soon as the token is cancelled.
        await gate.Task.WaitAsync(ct);
        await base.CommitFileAsync(filePath, commitMessage, ct);
    }
}

/// <summary>A config-repo fake whose CommitFileAsync always throws the given exception.</summary>
internal sealed class ThrowingConfigRepoManager(string url, string path, Exception failure)
    : ConfigRepoManager(url, path)
{
    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
        => Task.FromException(failure);
}

/// <summary>
/// Discovery-path facade tests. These mutate provider environment variables
/// (GH_TOKEN / GITHUB_TOKEN), so they are serialized in the <c>EnvVarMutation</c>
/// collection and always restore the originals.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigFacadeDiscoveryTests
{
    /// <summary>
    /// DiscoverModelsAsync with the real <see cref="ModelDiscoveryService"/> and no provider
    /// credentials → success with an empty list: both providers report unconfigured, so the
    /// facade's success path maps an empty DTO list.
    /// </summary>
    [Fact]
    public async Task DiscoverModelsAsync_NoProviderCredentials_ReturnsEmptySuccess()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        var originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var originalOllamaKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        var originalOllamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Environment.SetEnvironmentVariable("OLLAMA_URL", null);

            var discovery = new ModelDiscoveryService(NullLogger<ModelDiscoveryService>.Instance);
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service, discovery);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.DiscoverModelsAsync();

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.NotNull(result.Value);
            Assert.Empty(result.Value!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGhToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithubToken);
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", originalOllamaKey);
            Environment.SetEnvironmentVariable("OLLAMA_URL", originalOllamaUrl);
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// The facade maps every discovered model field-for-field (Id/Name/Vendor/ContextWindow/
    /// Enabled) onto <see cref="DiscoveredModelDto"/> — proven with a local HTTP handler stub
    /// against the real <see cref="ModelDiscoveryService"/> (no network, no timing waits).
    /// </summary>
    [Fact]
    public async Task DiscoverModelsAsync_MapsAllDiscoveredModelFields()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        var originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalGithubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var originalOllamaKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        var originalOllamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", "test-token");
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Environment.SetEnvironmentVariable("OLLAMA_URL", null);

            const string body = """
                {
                    "data": [
                        {
                            "id": "claude-test-1",
                            "name": "Claude Test 1",
                            "vendor": "Anthropic",
                            "capabilities": { "limits": { "max_context_window_tokens": 264000 } },
                            "policy": { "state": "enabled" }
                        },
                        {
                            "id": "disabled-model",
                            "name": "Disabled Model",
                            "vendor": "Anthropic",
                            "policy": { "state": "disabled" }
                        }
                    ]
                }
                """;
            var discovery = new ModelDiscoveryService(
                NullLogger<ModelDiscoveryService>.Instance,
                new StaticHttpClientFactory(
                    new HttpClient(new StubHttpHandler(body), disposeHandler: false)));

            using var factory = new ConfigFacadeTests.FacadeFactory(config, service, discovery);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.DiscoverModelsAsync();

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            var models = result.Value!;
            Assert.Equal(2, models.Count);

            var enabled = models.Single(m => m.Id == "copilot/claude-test-1");
            Assert.Equal("Claude Test 1", enabled.Name);
            Assert.Equal("Anthropic", enabled.Vendor);
            Assert.Equal(264000, enabled.ContextWindow);
            Assert.True(enabled.Enabled);

            var disabled = models.Single(m => m.Id == "copilot/disabled-model");
            Assert.Equal("Disabled Model", disabled.Name);
            Assert.Equal("Anthropic", disabled.Vendor);
            Assert.Null(disabled.ContextWindow);
            Assert.False(disabled.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGhToken);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalGithubToken);
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", originalOllamaKey);
            Environment.SetEnvironmentVariable("OLLAMA_URL", originalOllamaUrl);
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>Serves a fixed JSON body for every request (no network).</summary>
    private sealed class StubHttpHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>An <see cref="IHttpClientFactory"/> returning one pre-built client.</summary>
    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}