using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE FINAL DEPLOYMENT CONTRACT, exercised through the REAL DI REGISTRATION PATH: the host's
/// explicit authentication mode — and nothing else — selects the live Composer runtime.
/// <para>
/// The rows here are the ones the retired environment variable could break, so they run against
/// the production registration seam (<see cref="ComposerFacadeRegistration.AddComposerRuntimeFacades"/>)
/// with the deployment derived the way <c>Program.cs</c> derives it: the OAuth environment
/// predicate (<see cref="HiveAuthMode.FromValues"/>) decides the mode, and
/// <see cref="ComposerRuntimeFacadeDeployment.ForAuthMode"/> turns that mode into the deployment.
/// The retired variable's value is carried through the whole environment (unset / <c>"true"</c> /
/// <c>"false"</c>) and the SELECTED facade and the manager's cache are observed, so a split runtime
/// (a live legacy singleton coexisting with per-user runtimes) is impossible to hide.
/// </para>
/// <para>
/// ENVIRONMENT SCOPE: this class mutates ONLY the retired
/// <see cref="ComposerRuntimeFacadeDeployment.EnvironmentVariable"/> — the variable whose value
/// this contract is about — and it never touches the OAuth variables (which decide the HOST's
/// mode and are read by other collections' hosts). The mode is therefore computed through
/// <see cref="HiveAuthMode.FromValues"/>, the very predicate <c>Program.cs</c> applies to the
/// environment, so the decision under test is the production one without a process-wide race.
/// </para>
/// <para>
/// The class mutates a process-wide environment variable, so it is serialized on the
/// <c>EnvVarMutation</c> collection.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerDeploymentRegistrationTests : IDisposable
{
    private const string Model = "deployment-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-deployment-{Guid.NewGuid():N}");

    public ComposerDeploymentRegistrationTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The OAuth values that make the host authentication-ENABLED.</summary>
    private const string OAuthClientId = "test-client-id";
    private const string OAuthClientSecret = "test-secret";

    // ── The env-var matrix over the real registration path ───────────────────────────────────────

    /// <summary>
    /// THE RETIRED VARIABLE SELECTS NOTHING IN AUTHENTICATED MODE — for EVERY value it has ever been
    /// given: the scope resolves the PER-USER recovery facade, never the legacy singleton, so the
    /// variable can no longer create a split runtime (the <c>"false"</c> row is the regression this
    /// goal closes: it used to keep the legacy singleton live while authentication was enabled).
    /// <para>
    /// The deployment handed to the registration is the one the HOST derives — the same
    /// <see cref="ComposerRuntimeFacadeDeployment.FromEnvironment"/> result <c>Program.cs</c> uses,
    /// computed from the same OAuth predicate — so the decision here is the production one.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("TRUE")]
    [InlineData("0")]
    [InlineData("yes")]
    public async Task InAuthenticatedMode_EveryRetiredVariableValue_SelectsThePerUserFacade(string? retiredValue)
    {
        using var environment = new ScopedEnvironment();
        environment.Set(ComposerRuntimeFacadeDeployment.EnvironmentVariable, retiredValue);

        // THE HOST'S OWN DERIVATION — the exact chain Program.cs runs: the OAuth predicate decides
        // the mode, and the mode decides the deployment.
        var mode = HiveAuthMode.FromValues(OAuthClientId, OAuthClientSecret);
        var deployment = ComposerRuntimeFacadeDeployment.ForAuthMode(mode);
        Assert.True(deployment.PerUserRuntimeEnabled,
            $"'{retiredValue ?? "<unset>"}' must not create an OFF path while authentication is enabled");

        await using var host = NewHost(mode, deployment);

        using var first = host.Provider.CreateScope();
        using var second = host.Provider.CreateScope();

        // The per-user recovery facade in EVERY scope...
        Assert.IsType<ComposerRuntimeFacade>(first.ServiceProvider.GetRequiredService<IComposerFacade>());
        Assert.IsType<ComposerRuntimeFacade>(second.ServiceProvider.GetRequiredService<IComposerFacade>());
        // ...and NEVER the legacy singleton facade.
        Assert.IsNotType<ComposerFacade>(first.ServiceProvider.GetRequiredService<IComposerFacade>());

        // The registered (effective) deployment is the per-user one, whatever the variable said.
        Assert.True(first.ServiceProvider
            .GetRequiredService<ComposerRuntimeFacadeDeployment>().PerUserRuntimeEnabled);

        // AND THE OPERATION REALLY RUNS ON THE CALLER'S OWN RUNTIME: the manager owns a runtime for
        // that account, and the legacy singleton facade was never consulted.
        var seeded = await _auth.SeedAsync("deployment-member", UserRole.Member);
        var read = await first.ServiceProvider.GetRequiredService<IComposerFacade>().GetModelsAsync(
            FacadeAuthorizationHarness.Principal("deployment-member"), Ct);
        Assert.True(read.Success, read.Error ?? "the authenticated read failed");
        Assert.Contains(seeded.Id, ManagerEntries(host.Manager));
    }

    /// <summary>
    /// OPEN MODE IS THE LEGACY SINGLETON, FOR EVERY VALUE OF THE RETIRED VARIABLE: the scope
    /// resolves the legacy <see cref="ComposerFacade"/> (the very same instance in every scope), the
    /// operation runs on the legacy singleton Composer, and the per-user manager's cache stays EMPTY
    /// — so an open installation never shows a per-user runtime.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task InOpenMode_EveryRetiredVariableValue_SelectsTheLegacySingleton(string? retiredValue)
    {
        using var environment = new ScopedEnvironment();
        environment.Set(ComposerRuntimeFacadeDeployment.EnvironmentVariable, retiredValue);

        // OPEN MODE: the OAuth predicate says "no OAuth" — the same explicit host decision
        // Program.cs makes when the variables are unset.
        var mode = HiveAuthMode.FromValues(oauthClientId: null, oauthClientSecret: null);
        var deployment = ComposerRuntimeFacadeDeployment.ForAuthMode(mode);
        Assert.False(deployment.PerUserRuntimeEnabled,
            $"'{retiredValue ?? "<unset>"}' must not move open mode onto per-user runtimes");

        await using var host = NewHost(mode, deployment);

        using var first = host.Provider.CreateScope();
        using var second = host.Provider.CreateScope();

        var legacyFromFirst = first.ServiceProvider.GetRequiredService<IComposerFacade>();
        var legacyFromSecond = second.ServiceProvider.GetRequiredService<IComposerFacade>();
        Assert.IsType<ComposerFacade>(legacyFromFirst);
        Assert.Same(legacyFromFirst, legacyFromSecond);

        // The operation runs on the LEGACY singleton Composer and creates NO per-user runtime.
        var read = await legacyFromFirst.GetModelsAsync(actingUser: null, Ct);
        Assert.True(read.Success, read.Error ?? "the open-mode read failed");
        Assert.Empty(ManagerEntries(host.Manager));
    }

    /// <summary>
    /// THE RETIRED VARIABLE IS RESTORED ON EVERY EXIT PATH: the class mutates a process-wide
    /// environment variable, so a leaked value would corrupt every later test that reads it. The
    /// overlay restores it in its own <c>Dispose</c>, which xUnit v3 always calls.
    /// </summary>
    [Fact]
    public void TheRetiredVariable_IsRestoredOnEveryExitPath()
    {
        var repoRoot = TestRepoRoot.Find();
        var source = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(
                repoRoot, "tests", "CopilotHive.Tests", "Services", "ComposerDeploymentRegistrationTests.cs"))
                .ReplaceLineEndings("\n"));

        // The overlay saves the previous value and restores it.
        Assert.Contains("_previous[name] = Environment.GetEnvironmentVariable(name);", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(name, value);", source, StringComparison.Ordinal);
        // ...and the restore runs from Dispose (the runner-invoked cleanup path).
        Assert.Contains("public void Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var (name, value) in _previous)", source, StringComparison.Ordinal);

        // The class belongs to the SERIALIZED collection, so it cannot race another mutator. The
        // attribute is read from the TYPE (the authoritative source), not from the text.
        var attr = typeof(ComposerDeploymentRegistrationTests)
            .GetCustomAttributes(typeof(CollectionAttribute), inherit: false)
            .Cast<CollectionAttribute>()
            .SingleOrDefault();
        Assert.NotNull(attr);
        Assert.Equal("EnvVarMutation", attr!.Name);
    }

    // ── The interface alias ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE PRODUCTION REGISTRATION MAKES <see cref="IComposerRuntimeAccessor"/> AN ALIAS OF THE VERY
    /// SAME SCOPED <see cref="ComposerRuntimeAccessor"/> INSTANCE — registered EXACTLY ONCE, with
    /// the SAME scoped lifetime, and resolving the identical object.
    /// <para>
    /// The descriptor-level half is what makes the row discriminating against a DUPLICATE lifetime:
    /// a second <c>AddScoped</c> for the interface with its own factory would still be "scoped", but
    /// it would not be an alias — so the instance-identity assertion below catches it while the
    /// descriptor assertions catch a second registration of the concrete type.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheInterfaceAlias_IsRegisteredOnce_AndResolvesTheSameScopedInstance()
    {
        await using var host = NewHost(
            HiveAuthMode.AuthenticationEnabled,
            ComposerRuntimeFacadeDeployment.PerUserRuntime);

        // EXACTLY ONE registration of each type — no duplicate, no shadowing descriptor.
        var concrete = host.Descriptors.Where(d => d.ServiceType == typeof(ComposerRuntimeAccessor)).ToArray();
        var alias = host.Descriptors.Where(d => d.ServiceType == typeof(IComposerRuntimeAccessor)).ToArray();
        Assert.Single(concrete);
        Assert.Single(alias);
        Assert.Equal(ServiceLifetime.Scoped, concrete[0].Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, alias[0].Lifetime);

        using var first = host.Provider.CreateScope();
        using var second = host.Provider.CreateScope();

        // Within ONE scope the interface and the concrete type are the SAME object.
        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());
        Assert.Same(
            second.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // ACROSS scopes a DIFFERENT accessor per scope — the alias is scoped, never a singleton.
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // The root provider refuses it, exactly like the concrete scoped type.
        Assert.Throws<InvalidOperationException>(
            () => host.Provider.GetRequiredService<IComposerRuntimeAccessor>());
        Assert.Throws<InvalidOperationException>(
            () => host.Provider.GetRequiredService<ComposerRuntimeAccessor>());

        // The recovery facade the request receives holds THE SAME instance the circuit resolved:
        // one per-circuit accessor, never two with divergent circuit state.
        var facade = (ComposerRuntimeFacade)first.ServiceProvider.GetRequiredService<IComposerFacade>();
        var facadeAccessor = (IComposerRuntimeAccessor)typeof(ComposerRuntimeFacade)
            .GetField("_accessor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(facade)!;
        Assert.Same(first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(), facadeAccessor);
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a host over the PRODUCTION registration seam with the given explicit authentication
    /// mode and the given declared deployment, and returns the provider, the manager and the
    /// service descriptors of the collection the registration produced.
    /// </summary>
    private Host NewHost(HiveAuthMode mode, ComposerRuntimeFacadeDeployment deployment)
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        });
        services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(mode);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: () => true,
            providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));

        // THE PRODUCTION SEAM.
        services.AddComposerRuntimeFacades(deployment);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), services, stateDir);
    }

    /// <summary>The per-user runtime ids currently cached by the REAL manager.</summary>
    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    /// <summary>The provider, the manager and the registration descriptors of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        IServiceCollection descriptors,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public IReadOnlyList<ServiceDescriptor> Descriptors { get; } = descriptors.ToArray();

        public string StateDir { get; } = stateDir;

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // ORDER IS THE CONTRACT: the manager's actual teardown is AWAITED before the provider and
            // the backing resources go away; every step runs even if an earlier one failed.
            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Deployment host cleanup failed", failures);
        }
    }

    /// <summary>
    /// A process-wide environment overlay that restores every variable it touched, on every exit
    /// path — so a failing row can never leak a value into the next one.
    /// </summary>
    private sealed class ScopedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        internal void Set(string name, string? value)
        {
            if (!_previous.ContainsKey(name))
                _previous[name] = Environment.GetEnvironmentVariable(name);

            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// THE RUNNER-INVOKED CLEANUP: xUnit v3 calls <c>Dispose</c> for a class that implements only
    /// <c>IDisposable</c>, so every cleanup step lives here.
    /// </summary>
    public void Dispose()
    {
        _db.Dispose();
        _auth.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                TestHelpers.ForceDeleteDirectory(_dir);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated state directory.
        }
    }
}
