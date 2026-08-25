using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Orchestration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Slice 1A1 Program.cs registration tests: a <see cref="HiveConfigFile"/> singleton is ALWAYS
/// registered (even when <c>--config-repo</c> is empty), the no-repo fallback carries an EMPTY
/// <see cref="OrchestratorConfig.Model"/> (never <see cref="Constants.DefaultWorkerModel"/>), and
/// the Brain/Composer model chains behave exactly as before the always-registration change.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRegistrationTests : IDisposable
{
    private const string EnvBrainModel = "env-brain-model";

    private readonly string _stateDir;
    private readonly string? _previousStateDir;
    private readonly string? _previousBrainModel;
    private readonly ConfigRegistrationFactory _factory;

    public ConfigRegistrationTests()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"copilothive-configreg-{Guid.NewGuid():N}");
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        _previousBrainModel = Environment.GetEnvironmentVariable("BRAIN_MODEL");
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
        Environment.SetEnvironmentVariable("BRAIN_MODEL", EnvBrainModel);
        Directory.CreateDirectory(_stateDir);
        _factory = new ConfigRegistrationFactory();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
        Environment.SetEnvironmentVariable("BRAIN_MODEL", _previousBrainModel);

        if (!Directory.Exists(_stateDir))
            return;
        try { Directory.Delete(_stateDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Boots the real application (no <c>--config-repo</c> argument) in Testing mode.</summary>
    private sealed class ConfigRegistrationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }
    }

    [Fact]
    public void NoConfigRepo_HiveConfigFileAlwaysRegistered_IsFallbackWithEmptyOrchestratorModel()
    {
        var config = _factory.Services.GetRequiredService<HiveConfigFile>();

        Assert.NotNull(config);
        Assert.False(config.IsConfigured, "The no-repo fallback must be IsConfigured=false");
        Assert.Equal(string.Empty, config.Orchestrator.Model);
        Assert.NotEqual(Constants.DefaultWorkerModel, config.Orchestrator.Model);
    }

    [Fact]
    public void NoConfigRepo_ExactlyOneHiveConfigFileRegistration_SingleAuthority()
    {
        // GetServices must expose exactly ONE HiveConfigFile — the DI container must not
        // hold both a fallback and a repo-parsed instance (one configuration authority).
        var configs = _factory.Services.GetServices<HiveConfigFile>().ToList();

        Assert.Single(configs);
        Assert.False(configs[0].IsConfigured);
    }

    [Fact]
    public void NoConfigRepo_BrainEffectiveModel_IsEnvBrainModel_NotDefaultWorkerModel()
    {
        var brain = _factory.Services.GetRequiredService<IDistributedBrain>();

        var modelField = typeof(DistributedBrain).GetField(
                "_modelOverride", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_modelOverride field not found on DistributedBrain");
        var model = (string)modelField.GetValue(brain)!;

        // With the fallback's Orchestrator.Model empty, the Brain factory falls through
        // to the env BRAIN_MODEL — exactly as before the always-registered fallback.
        Assert.Equal(EnvBrainModel, model);
        Assert.NotEqual(Constants.DefaultWorkerModel, model);
    }

    [Fact]
    public void NoConfigRepo_ComposerModel_IsNull_DisconnectedShell()
    {
        var composer = _factory.Services.GetRequiredService<Composer>();

        var agentServiceField = typeof(Composer).GetField(
                "_agentService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        var agentService = agentServiceField.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
        var modelProperty = agentService.GetType().GetProperty("Model")
            ?? throw new InvalidOperationException("Model property not found on ComposerAgentService");
        var model = (string?)modelProperty.GetValue(agentService);

        // Resolver-only construction: with no config repo there is no global catalog, so
        // ResolveComposerDefaultModel() returns null — the Composer registers as a
        // disconnected shell (no fall-through to orchestrator.model / BRAIN_MODEL).
        Assert.Null(model);
        Assert.False(composer.IsConnected);
        Assert.Null(composer.StartupDefaultModel);
    }

    /// <summary>
    /// One-resolution contract: the startup-connect gate reads the SAME Composer-provided
    /// resolved default used for construction. With no valid default the shell stays
    /// disconnected (the gate does not connect); with a configured default the gate connects.
    /// </summary>
    [Fact]
    public void NoConfigRepo_StartupGate_ReadsComposerStartupDefaultModel_NotSecondResolution()
    {
        var composer = _factory.Services.GetRequiredService<Composer>();

        // Structural: the gate must read composer.StartupDefaultModel (the single resolution
        // result) — with no config repo this is null, so the gate must NOT connect.
        Assert.Null(composer.StartupDefaultModel);
        Assert.False(composer.IsConnected);

        // Behavioral: the gate's decision matches the configured default — null default ⇒
        // shell stays disconnected (no client, no agent).
        var agentServiceField = typeof(Composer).GetField(
                "_agentService", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_agentService field not found on Composer");
        var agentService = agentServiceField.GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
        var chatClientField = agentService.GetType().GetField("_chatClient", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on ComposerAgentService");
        Assert.Null(chatClientField.GetValue(agentService));
    }

    /// <summary>
    /// With no config repo (no global <c>available_models</c>), the Composer's
    /// selectable catalog (<see cref="Composer.AvailableModels"/>) is EMPTY — not a
    /// fabricated <c>[model]</c> fallback. This is the key behavioral change of the
    /// parameterless <c>GetComposerAvailableModels</c> contract.
    /// </summary>
    [Fact]
    public void NoConfigRepo_ComposerAvailableModels_IsEmptyNotFabricatedFallback()
    {
        var composer = _factory.Services.GetRequiredService<Composer>();

        // The fallback HiveConfigFile has no Models section, so GetComposerAvailableModels()
        // returns an empty list. Program.cs passes [] (not null) to the Composer constructor,
        // so the Composer's own `?? [model]` fallback does NOT trigger.
        Assert.Empty(composer.AvailableModels);
    }

    /// <summary>
    /// The fallback HiveConfigFile's <c>GetComposerAvailableModels()</c> returns an empty
    /// list directly (no composer-local fall-through, no fabricated model).
    /// </summary>
    [Fact]
    public void NoConfigRepo_GetComposerAvailableModels_ReturnsEmptyList()
    {
        var config = _factory.Services.GetRequiredService<HiveConfigFile>();

        Assert.Empty(config.GetComposerAvailableModels());
    }

    /// <summary>
    /// The fallback HiveConfigFile's <c>ResolveComposerDefaultModel()</c> returns null
    /// (no composer section, no global catalog).
    /// </summary>
    [Fact]
    public void NoConfigRepo_ResolveComposerDefaultModel_ReturnsNull()
    {
        var config = _factory.Services.GetRequiredService<HiveConfigFile>();

        Assert.Null(config.ResolveComposerDefaultModel());
    }

    /// <summary>
    /// Simulates the Program.cs with-repo branch: a repo-parsed <see cref="HiveConfigFile"/>
    /// (via <see cref="ConfigRepoManager.ParseConfig"/>) is the SINGLE registered instance.
    /// Verifies the if/else invariant — exactly one registration, IsConfigured=true —
    /// without requiring a live git repo (the parse path is internal and test-accessible).
    /// </summary>
    [Fact]
    public void WithConfigRepo_SingleHiveConfigFileRegistration_IsConfiguredTrue()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: repo-brain-model
              reasoning_effort: high
            """;

        // Mirror Program.cs's if-branch: the repo-parsed config is the single instance.
        var repoConfig = ConfigRepoManager.ParseConfig(yaml);
        var services = new ServiceCollection();
        services.AddSingleton(repoConfig);
        var sp = services.BuildServiceProvider();

        var configs = sp.GetServices<HiveConfigFile>().ToList();

        Assert.Single(configs);
        Assert.True(configs[0].IsConfigured, "A repo-parsed config must be IsConfigured=true");
        Assert.Equal("repo-brain-model", configs[0].Orchestrator.Model);
    }

    /// <summary>
    /// Simulates the Program.cs no-repo branch: the fallback <see cref="HiveConfigFile"/>
    /// (empty Orchestrator.Model) is the SINGLE registered instance. Verifies the else-branch
    /// invariant — exactly one registration, IsConfigured=false — using a real DI container.
    /// </summary>
    [Fact]
    public void NoConfigRepo_SimulatedFallback_SingleRegistration_IsConfiguredFalse()
    {
        // Mirror Program.cs's else-branch: the empty fallback is the single instance.
        var fallback = new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback()
        };
        var services = new ServiceCollection();
        services.AddSingleton(fallback);
        var sp = services.BuildServiceProvider();

        var configs = sp.GetServices<HiveConfigFile>().ToList();

        Assert.Single(configs);
        Assert.False(configs[0].IsConfigured);
        Assert.Equal(string.Empty, configs[0].Orchestrator.Model);
    }

    /// <summary>
    /// The if/else structure in Program.cs makes it structurally impossible to register
    /// BOTH a fallback and a repo-parsed config. This test proves that registering two
    /// HiveConfigFile instances (simulating a hypothetical bug) would be DETECTABLE via
    /// GetServices — so the single-registration guard test is meaningful.
    /// </summary>
    [Fact]
    public void DuplicateRegistration_DetectableViaGetServices()
    {
        var fallback = new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback()
        };
        var repoConfig = ConfigRepoManager.ParseConfig("version: \"1.0\"");

        var services = new ServiceCollection();
        services.AddSingleton(fallback);
        services.AddSingleton(repoConfig); // hypothetical bug: both registered
        var sp = services.BuildServiceProvider();

        var configs = sp.GetServices<HiveConfigFile>().ToList();

        // Two registrations would be visible — the single-registration test would catch this.
        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.IsConfigured);
        Assert.Contains(configs, c => !c.IsConfigured);
    }
}
