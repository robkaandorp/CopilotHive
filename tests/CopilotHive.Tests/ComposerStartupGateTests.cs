using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Slice 1A2a production-startup tests: boots the REAL <see cref="Program"/> host through
/// <see cref="WebApplicationFactory{TEntryPoint}"/> and verifies the startup-connect gate in
/// <c>Program.cs</c> — the Composer connects ONLY when the Slice 1A1 resolver returns a
/// non-null default (<see cref="Composer.StartupDefaultModel"/>), and otherwise registers as a
/// disconnected shell while the host still starts.
/// <para>
/// The assertions are <b>removal-proof</b>: they read the host's captured startup log
/// (<see cref="DashboardLogSink"/>) so a regression that drops the gate and always calls
/// <c>ConnectAsync</c> is detected. Merely asserting <c>IsConnected == false</c> would NOT
/// catch that, because a gate-less connect on a no-model Composer throws
/// <c>"no model configured"</c> which <c>Program.cs</c> catches — the shell would still read
/// as disconnected. The "Connecting Composer…" log line is emitted exclusively inside the
/// gated branch, so its ABSENCE proves the connect was never attempted.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerStartupGateTests
{
    /// <summary>Log message emitted by Program.cs immediately inside the gated connect branch.</summary>
    private const string ConnectAttemptedLog = "Connecting Composer";

    /// <summary>Log message emitted by Program.cs when the gate rejects (no resolved default).</summary>
    private const string ShellRegisteredLog = "Composer has no configured model";

    /// <summary>Log message emitted by Program.cs after a successful startup connect.</summary>
    private const string ConnectedLog = "Composer connected.";

    /// <summary>
    /// A model identifier whose client can be constructed offline (no network, no token), so a
    /// genuine production startup connect can complete inside a test host.
    /// </summary>
    private const string OfflineModel = "ollama-local/startup-gate-model";

    private static IReadOnlyList<string> StartupLogMessages(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<DashboardLogSink>()
            .GetRecent(int.MaxValue)
            .Select(e => e.Message)
            .ToList();

    private static bool HasLog(IReadOnlyList<string> messages, string fragment) =>
        messages.Any(m => m.Contains(fragment, StringComparison.Ordinal));

    /// <summary>
    /// Reads the Composer's private chat client so "connected" is proven at the LLM-client level,
    /// not only through the public flag.
    /// </summary>
    private static object? ChatClientOf(Composer composer)
    {
        var agentService = typeof(Composer)
            .GetField("_agentService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;
        return agentService.GetType()
            .GetField("_chatClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agentService);
    }

    // ── Valid default → the startup gate CONNECTS ──

    /// <summary>
    /// Production DI with <c>composer.model</c> present (normalized) in the global
    /// <c>models.available_models</c> catalog: the resolver returns non-null, the gate opens,
    /// and the real startup path connects the Composer.
    /// </summary>
    [Fact]
    public async Task ProductionStartup_ValidCatalogBackedDefault_GateConnectsComposer()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = OfflineModel },
                    new ModelEntry { Name = "ollama-local/other-model" },
                ]
            },
            Composer = new ComposerConfig { Model = OfflineModel }
        };

        await using var factory = new ComposerStartupGateFactory(config);
        var composer = factory.Services.GetRequiredService<Composer>();

        // The single resolution result is exposed on the Composer and drives the gate.
        Assert.Equal(OfflineModel, composer.StartupDefaultModel);

        // The gate opened and the production startup path connected.
        Assert.True(composer.IsConnected,
            "A catalog-backed composer.model must connect during Program startup.");
        Assert.NotNull(ChatClientOf(composer));

        var logs = StartupLogMessages(factory);
        Assert.True(HasLog(logs, ConnectAttemptedLog),
            "Program startup must ENTER the gated connect branch when a valid default exists.");
        Assert.True(HasLog(logs, ConnectedLog),
            "Program startup must report a successful Composer connection.");
        Assert.False(HasLog(logs, ShellRegisteredLog),
            "The disconnected-shell branch must NOT run when a valid default exists.");
    }

    /// <summary>
    /// Whitespace around the configured model and a differing case still resolve to the
    /// catalog's canonical entry, so the gate connects with the trimmed canonical name.
    /// </summary>
    [Fact]
    public async Task ProductionStartup_ModelNormalizesToCatalogEntry_GateConnects()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = OfflineModel }]
            },
            Composer = new ComposerConfig { Model = $"  {OfflineModel.ToUpperInvariant()}  " }
        };

        await using var factory = new ComposerStartupGateFactory(config);
        var composer = factory.Services.GetRequiredService<Composer>();

        // Resolved to the catalog's canonical (trimmed, original-cased) entry.
        Assert.Equal(OfflineModel, composer.StartupDefaultModel);
        Assert.True(composer.IsConnected);
        Assert.True(HasLog(StartupLogMessages(factory), ConnectAttemptedLog));
    }

    // ── No valid default → the startup gate SKIPS the connect ──

    /// <summary>
    /// <c>composer.model</c> unset (no composer section at all): the resolver returns null, the
    /// gate never attempts a connect, and the host still starts with a disconnected shell.
    /// </summary>
    [Fact]
    public async Task ProductionStartup_ComposerModelUnset_ShellDisconnected_NoConnectAttempted()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = OfflineModel }]
            }
            // No Composer section → no default.
        };

        await using var factory = new ComposerStartupGateFactory(config);

        // The host started: an arbitrary singleton resolves and the Composer is registered.
        var composer = factory.Services.GetRequiredService<Composer>();

        Assert.Null(composer.StartupDefaultModel);
        Assert.False(composer.IsConnected);
        Assert.Null(ChatClientOf(composer));

        var logs = StartupLogMessages(factory);
        Assert.False(HasLog(logs, ConnectAttemptedLog),
            "The gate must NOT attempt a startup connect when the resolver returns null.");
        Assert.True(HasLog(logs, ShellRegisteredLog),
            "Program startup must record the disconnected-shell registration.");
    }

    /// <summary>
    /// <c>composer.model</c> SET but ABSENT from the global catalog: the resolver returns null
    /// (the global list is the sole authority), so the Composer stays a disconnected shell.
    /// </summary>
    [Fact]
    public async Task ProductionStartup_ComposerModelSetButAbsentFromCatalog_ShellDisconnected()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = OfflineModel }]
            },
            Composer = new ComposerConfig { Model = "ollama-local/not-in-catalog" }
        };

        await using var factory = new ComposerStartupGateFactory(config);
        var composer = factory.Services.GetRequiredService<Composer>();

        Assert.Null(composer.StartupDefaultModel);
        Assert.False(composer.IsConnected);
        Assert.Null(ChatClientOf(composer));

        // The catalog itself is still exposed — only the DEFAULT is absent.
        Assert.Equal([OfflineModel], composer.AvailableModels);

        var logs = StartupLogMessages(factory);
        Assert.False(HasLog(logs, ConnectAttemptedLog),
            "A set-but-absent composer.model must NOT trigger a startup connect.");
        Assert.True(HasLog(logs, ShellRegisteredLog));
    }

    /// <summary>
    /// <c>composer.model</c> whitespace-only: normalized to null by the resolver, so the shell
    /// stays disconnected and no connect is attempted.
    /// </summary>
    [Fact]
    public async Task ProductionStartup_ComposerModelWhitespaceOnly_ShellDisconnected()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = OfflineModel }]
            },
            Composer = new ComposerConfig { Model = "   " }
        };

        await using var factory = new ComposerStartupGateFactory(config);
        var composer = factory.Services.GetRequiredService<Composer>();

        Assert.Null(composer.StartupDefaultModel);
        Assert.False(composer.IsConnected);
        Assert.Null(ChatClientOf(composer));

        var logs = StartupLogMessages(factory);
        Assert.False(HasLog(logs, ConnectAttemptedLog),
            "A whitespace-only composer.model must NOT trigger a startup connect.");
        Assert.True(HasLog(logs, ShellRegisteredLog));
    }

    /// <summary>
    /// No global catalog at all (empty <c>models.available_models</c>) with a configured
    /// <c>composer.model</c>: the resolver has nothing to match against, so the shell stays
    /// disconnected and the process still starts.
    /// </summary>
    [Fact]
    public async Task ProductionStartup_EmptyGlobalCatalog_ShellDisconnected_HostStillStarts()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [] },
            Composer = new ComposerConfig { Model = OfflineModel }
        };

        await using var factory = new ComposerStartupGateFactory(config);
        var composer = factory.Services.GetRequiredService<Composer>();

        Assert.Null(composer.StartupDefaultModel);
        Assert.False(composer.IsConnected);
        Assert.Empty(composer.AvailableModels);

        // The host is alive: an HTTP request against the started server succeeds.
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode,
            "The host must start normally even with no valid Composer default.");

        Assert.False(HasLog(StartupLogMessages(factory), ConnectAttemptedLog));
    }
}

/// <summary>
/// Boots the real CopilotHive application with a caller-supplied <see cref="HiveConfigFile"/>
/// registered LAST, so <c>GetRequiredService&lt;HiveConfigFile&gt;()</c> in the Composer DI
/// factory resolves it instead of the no-config fallback. Nothing about the Composer
/// registration or the startup-connect gate is overridden — the production code path under
/// test runs verbatim.
/// </summary>
internal sealed class ComposerStartupGateFactory : WebApplicationFactory<Program>
{
    private readonly HiveConfigFile _config;
    private readonly string _stateDir;
    private readonly string? _previousStateDir;

    public ComposerStartupGateFactory(HiveConfigFile config)
    {
        _config = config;
        _stateDir = Path.Combine(Path.GetTempPath(), $"copilothive-startupgate-{Guid.NewGuid():N}");
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
        Directory.CreateDirectory(_stateDir);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Registered after Program's own registration: the LAST singleton wins for
        // GetRequiredService, so the Composer factory resolves this config.
        builder.ConfigureServices(services => services.AddSingleton(_config));
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
