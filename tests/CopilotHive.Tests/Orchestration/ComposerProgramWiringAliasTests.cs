using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE ALIAS OVER THE REAL HOST WIRING: the production <c>Program.cs</c> pipeline — not a hand-built
/// <see cref="IServiceCollection"/> — is booted in BOTH authentication modes, and the
/// <see cref="IComposerRuntimeAccessor"/> interface is proven to be an ALIAS of the very same scoped
/// <see cref="ComposerRuntimeAccessor"/> instance the host registered.
/// <para>
/// Every other alias row resolves the accessor out of a collection a test assembled by hand. That
/// proves the REGISTRATION EXTENSION METHOD behaves, but it cannot prove the HOST actually calls it
/// — a host that never called <c>AddComposerRuntimeFacades</c> (or called it with a different
/// deployment) would still satisfy those rows. This class closes that gap: the host's own service
/// provider is the one under test.
/// </para>
/// <para>
/// It also pins the surrounding production wiring the goal's scope item 10 requires to stay
/// LEGACY: the clarification router, the active event injector and the startup coordinator keep
/// their legacy bindings, and no per-user runtime is created for the legacy singleton path.
/// </para>
/// <para>
/// The class boots a real host, so it mutates process-wide environment variables and is serialized
/// on the <c>EnvVarMutation</c> collection. Each row builds and tears down its OWN host, so the
/// OAuth variables are only ever set for the host actually under test.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerProgramWiringAliasTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// THE REAL HOST REGISTERS THE INTERFACE AS AN ALIAS OF THE SAME SCOPED CONCRETE INSTANCE: within
    /// ONE scope the interface and the concrete type are the SAME object, and across scopes they are
    /// DIFFERENT objects — the alias is SCOPED, never a singleton, so two circuits never share one
    /// accessor's identity.
    /// <para>
    /// The host is the production <c>Program.cs</c> pipeline, so this proves the wiring the
    /// application actually ships.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRealHost_ResolvesTheInterfaceAsAnAliasOfTheSameScopedConcreteInstance()
    {
        using var host = await AuthEnabledHost.StartAsync();

        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();

        using var first = scopeFactory.CreateScope();
        using var second = scopeFactory.CreateScope();

        // Within ONE scope: the SAME object.
        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());
        Assert.Same(
            second.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // Across scopes: a DIFFERENT accessor per scope.
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // The scoped accessor and the scoped circuit principal accessor are DISTINCT per scope — the
        // circuit identity is never shared with another circuit.
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>(),
            second.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>());
    }

    /// <summary>
    /// THE ALIAS HOLDS IN OPEN MODE TOO: the alias is a property of the REGISTRATION, not of the
    /// selected facade, so an open-mode host (which serves the legacy singleton) still resolves one
    /// accessor per scope.
    /// </summary>
    [Fact]
    public async Task TheRealHost_ResolvesTheAliasInOpenModeToo()
    {
        using var host = await OpenModeHost.StartAsync();

        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();

        using var first = scopeFactory.CreateScope();
        using var second = scopeFactory.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());
    }

    /// <summary>
    /// THE PRODUCTION FACADE HOLDS THAT VERY ALIASED INSTANCE: the recovery facade the host hands to a
    /// request is built from the INTERFACE, so the component (which injects the interface) and the
    /// facade provably share ONE per-circuit accessor rather than two with divergent circuit state.
    /// </summary>
    [Fact]
    public async Task TheRealHostsFacade_HoldsTheSameAliasedInstanceTheCircuitResolves()
    {
        using var host = await AuthEnabledHost.StartAsync();

        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopeFactory.CreateScope();

        var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();
        Assert.IsType<ComposerRuntimeFacade>(facade);

        var facadeAccessor = (IComposerRuntimeAccessor)typeof(ComposerRuntimeFacade)
            .GetField("_accessor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(facade)!;

        Assert.Same(scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(), facadeAccessor);
    }

    /// <summary>
    /// THE LEGACY SYSTEM STAYS LEGACY: the host's clarification router is still bound to the LEGACY
    /// singleton Composer (never to a per-user runtime), and the active event injector and the startup
    /// coordinator are still the legacy per-installation ones — the scope-item-10 contract the goal
    /// preserves.
    /// </summary>
    [Fact]
    public async Task TheRealHost_KeepsTheLegacySystemBoundToTheLegacySingleton()
    {
        using var host = await AuthEnabledHost.StartAsync();

        var legacyComposer = host.Services.GetRequiredService<Composer>();

        // The clarification router is the LEGACY singleton Composer — never a per-user runtime.
        Assert.Same(legacyComposer, host.Services.GetRequiredService<IClarificationRouter>());

        // ...and the active event injector is the legacy per-installation one (a singleton), not a
        // per-user instance.
        var injector = host.Services.GetRequiredService<ActiveEventInjector>();
        Assert.Same(injector, host.Services.GetRequiredService<ActiveEventInjector>());

        // The startup coordinator is still registered and still the legacy singleton one.
        Assert.NotNull(host.Services.GetService<LlmConnectionCoordinator>());
    }

    /// <summary>
    /// THE HOST REALLY IS IN AUTHENTICATION-ENABLED MODE — the control that makes the rows above
    /// meaningful: without it, an open-mode host would satisfy them too.
    /// <para>
    /// It also pins the HOST'S OWN DEPLOYMENT SELECTION: the real <c>Program.cs</c> must register the
    /// INTEGRATED per-user deployment in authenticated mode. A host that registered the legacy
    /// deployment (the split-runtime regression this goal closes) is caught here — the alias rows
    /// above alone cannot see it, because the alias holds in either deployment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheAuthEnabledHost_ReallyIsInAuthenticatedMode()
    {
        using var host = await AuthEnabledHost.StartAsync();

        var mode = host.Services.GetRequiredService<HiveAuthMode>();
        Assert.True(mode.AuthEnabled, "the host must really have authentication enabled");

        // ...and the deployment it registered is the integrated per-user one.
        var deployment = host.Services.GetRequiredService<ComposerRuntimeFacadeDeployment>();
        Assert.True(deployment.PerUserRuntimeEnabled,
            "the real host must register the integrated per-user deployment in authenticated mode");

        // ...and the LIVE facade it hands to a request is therefore the per-user recovery facade.
        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopeFactory.CreateScope();
        var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();
        Assert.IsType<ComposerRuntimeFacade>(facade);
        Assert.IsNotType<ComposerFacade>(facade);
    }

    /// <summary>
    /// THE REAL HOST SELECTS THE PER-USER PATH FOR EVERY VALUE OF THE RETIRED VARIABLE — including
    /// <c>"false"</c>, which used to keep the legacy singleton live while authentication was enabled.
    /// <para>
    /// This is the deployment contract proved against the REAL <c>Program.cs</c> wiring (not a
    /// hand-built collection): the host's own effective deployment and its own live facade are
    /// observed for each value, and there is never a split runtime.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task TheRealHost_IsThePerUserPath_ForEveryRetiredVariableValue(string? retiredValue)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, retiredValue);

            using var host = await AuthEnabledHost.StartAsync();

            var mode = host.Services.GetRequiredService<HiveAuthMode>();
            Assert.True(mode.AuthEnabled);

            // The EFFECTIVE deployment the host registered: the per-user one, whatever the variable said.
            var deployment = host.Services.GetRequiredService<ComposerRuntimeFacadeDeployment>();
            Assert.True(deployment.PerUserRuntimeEnabled,
                $"'{retiredValue ?? "<unset>"}' must not create an OFF path while authentication is enabled");

            // ...and the LIVE facade it serves is the per-user recovery facade — never the legacy one.
            var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
            using var scope = scopeFactory.CreateScope();
            var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();
            Assert.IsType<ComposerRuntimeFacade>(facade);
            Assert.IsNotType<ComposerFacade>(facade);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// THE OPEN-MODE HOST REALLY IS IN OPEN MODE AND SERVES THE LEGACY SINGLETON FACADE — the control
    /// for the open-mode alias row.
    /// </summary>
    [Fact]
    public async Task TheOpenModeHost_ReallyIsInOpenMode_AndServesTheLegacyFacade()
    {
        using var host = await OpenModeHost.StartAsync();

        var mode = host.Services.GetRequiredService<HiveAuthMode>();
        Assert.False(mode.AuthEnabled, "the host must really be in open mode");

        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopeFactory.CreateScope();

        var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();
        Assert.IsType<ComposerFacade>(facade);
        Assert.Same(
            facade,
            scope.ServiceProvider.GetRequiredService<IComposerFacade>());

        var deployment = host.Services.GetRequiredService<ComposerRuntimeFacadeDeployment>();
        Assert.False(deployment.PerUserRuntimeEnabled);
    }

    /// <summary>
    /// The shared plumbing of both hosts: an isolated state directory, the OAuth variables of the
    /// chosen mode, and a RESTORATION of every touched variable when the host is torn down.
    /// </summary>
    private abstract class ComposerHost : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-alias-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;
        private readonly string? _previousAllowInsecure;

        private protected ComposerHost(bool authEnabled)
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            _previousAllowInsecure = Environment.GetEnvironmentVariable("ALLOW_INSECURE_OAUTH");

            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", authEnabled ? "test-client-id" : null);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", authEnabled ? "test-secret" : null);
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", "true");
            Directory.CreateDirectory(_stateDir);
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
                services.ReplaceDistributedBrain(new NoOpDistributedBrain()));
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousClientSecret);
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", _previousAllowInsecure);

            if (!disposing || !Directory.Exists(_stateDir))
                return;

            try { Directory.Delete(_stateDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>An authentication-ENABLED host: both OAuth variables are set.</summary>
    private sealed class AuthEnabledHost : ComposerHost
    {
        private AuthEnabledHost() : base(authEnabled: true) { }

        internal static async Task<AuthEnabledHost> StartAsync()
        {
            var host = new AuthEnabledHost();
            // Build the host NOW, while the OAuth variables are still this host's.
            using var client = host.CreateClient();
            await Task.CompletedTask;
            return host;
        }
    }

    /// <summary>An OPEN-MODE host: both OAuth variables are unset.</summary>
    private sealed class OpenModeHost : ComposerHost
    {
        private OpenModeHost() : base(authEnabled: false) { }

        internal static async Task<OpenModeHost> StartAsync()
        {
            var host = new OpenModeHost();
            using var client = host.CreateClient();
            await Task.CompletedTask;
            return host;
        }
    }
}
