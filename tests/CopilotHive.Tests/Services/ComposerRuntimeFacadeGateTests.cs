using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE DEPLOYMENT GATE — THE STAGING-SAFETY CONTRACT: the live facade path stays on the LEGACY singleton
/// <see cref="Composer"/> unless the gate is explicitly enabled, so this slice changes no live behaviour.
/// <para>
/// With the gate OFF (the default everywhere), resolving <see cref="IComposerFacade"/> from any scope answers
/// the SAME legacy facade instance the host has always served — never a per-user recovery facade, so no
/// split-runtime behaviour can be observed. With it ON, the scope resolves a
/// <see cref="ComposerRuntimeFacade"/>; the per-user path therefore cannot become live by accident.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerRuntimeFacadeGateTests : IDisposable
{
    private readonly string? _gateBeforeTest = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);

    public void Dispose() => Assert.Equal(_gateBeforeTest,
        Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable));

    /// <summary>Every value other than an explicit <c>true</c> keeps the legacy singleton.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("enabled")]
    [InlineData("per-user")]
    public void TheGate_DefaultsToLegacy_ForEverythingExceptAnExplicitTrue(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, value);

            var deployment = ComposerRuntimeFacadeDeployment.FromEnvironment();

            Assert.False(deployment.PerUserRuntimeEnabled,
                $"'{value}' must not enable the per-user live path");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// AN UNSET VARIABLE IS THE SHIPPED DEFAULT — read through the SAME seam <c>Program.cs</c> uses, so this row
    /// fails if the production default ever flips.
    /// </summary>
    [Fact]
    public void TheGate_WithNoEnvironmentValue_IsTheLegacyDefault()
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, null);

            var deployment = ComposerRuntimeFacadeDeployment.FromEnvironment();

            Assert.False(deployment.PerUserRuntimeEnabled);
            Assert.False(ComposerRuntimeFacadeDeployment.LegacyOnly.PerUserRuntimeEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    /// <summary>Only the exact value <c>true</c> (case-insensitive) opens the per-user live path.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void TheGate_OpensOnlyForAnExplicitTrue(string value)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, value);

            Assert.True(ComposerRuntimeFacadeDeployment.FromEnvironment().PerUserRuntimeEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// THE GATE OFF MEANS THE LEGACY SINGLETON, BEHAVIOURALLY: every scope resolves the SAME
    /// <see cref="ComposerFacade"/> instance, and it is the very object the singleton registration produced — so
    /// the operation runs on the legacy singleton <see cref="Composer"/> exactly as before.
    /// </summary>
    [Fact]
    public async Task GateOff_EveryScopeResolvesTheSameLegacyFacade()
    {
        await using var fixture = new RegistrationFixture(ComposerRuntimeFacadeDeployment.LegacyOnly);
        using var first = fixture.Provider.CreateScope();
        using var second = fixture.Provider.CreateScope();

        var fromFirst = first.ServiceProvider.GetRequiredService<IComposerFacade>();
        var fromSecond = second.ServiceProvider.GetRequiredService<IComposerFacade>();

        Assert.IsType<ComposerFacade>(fromFirst);
        // The SAME legacy instance in every scope: the legacy facade is itself the singleton, so the scoped
        // registration is a pass-through and the behaviour is byte-identical to the pre-slice host.
        Assert.Same(fromFirst, fromSecond);
        Assert.False(fromFirst is ComposerRuntimeFacade,
            "the per-user recovery facade must never be live while the gate is off");

        // ...and it really drives the LEGACY singleton Composer.
        var legacy = fixture.Provider.GetRequiredService<Composer>();
        Assert.Same(legacy, ComposerOfFacade(fromFirst));
    }

    /// <summary>
    /// WHY PRODUCTION MUST RESOLVE PER REQUEST: the facade is SCOPED now, so resolving it from the ROOT provider
    /// is REFUSED under scope validation. That is precisely the startup-closure capture the hub's per-request
    /// resolution removes — and it is demonstrated here rather than assumed.
    /// </summary>
    [Fact]
    public async Task ResolvingTheFacadeFromTheRootProvider_IsRefused()
    {
        await using var fixture = new RegistrationFixture(ComposerRuntimeFacadeDeployment.LegacyOnly);

        var error = Assert.Throws<InvalidOperationException>(
            () => fixture.Provider.GetRequiredService<IComposerFacade>());
        Assert.Contains("scoped service", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE GATE ON SELECTS THE RECOVERY FACADE, PER SCOPE: two scopes get DIFFERENT instances (each holding its
    /// OWN scoped runtime accessor), which is exactly why the registration can never be a singleton.
    /// </summary>
    [Fact]
    public async Task GateOn_EachScopeResolvesItsOwnRecoveryFacade()
    {
        await using var fixture = new RegistrationFixture(
            new ComposerRuntimeFacadeDeployment(PerUserRuntimeEnabled: true));

        using var first = fixture.Provider.CreateScope();
        using var second = fixture.Provider.CreateScope();

        var fromFirst = first.ServiceProvider.GetRequiredService<IComposerFacade>();
        var fromSecond = second.ServiceProvider.GetRequiredService<IComposerFacade>();

        Assert.IsType<ComposerRuntimeFacade>(fromFirst);
        Assert.IsType<ComposerRuntimeFacade>(fromSecond);
        Assert.NotSame(fromFirst, fromSecond);
    }

    /// <summary>
    /// NO SINGLETON CAPTURES THE SCOPED ACCESSOR IN EITHER MODE: the registration never closes a singleton over
    /// it, so the per-circuit identity can never leak host-wide.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoRegistrationCapturesTheScopedAccessorInASingleton(bool gateEnabled)
    {
        var repoRoot = TestRepoRoot.Find();
        var registration = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(repoRoot, "src", "CopilotHive", "Services", "ComposerRuntimeFacadeDeployment.cs"))
                .ReplaceLineEndings("\n"));

        foreach (var statement in StatementsContaining(registration, "AddSingleton"))
        {
            Assert.DoesNotContain("ComposerRuntimeAccessor", statement, StringComparison.Ordinal);
            Assert.DoesNotContain("CircuitUserPrincipalAccessor", statement, StringComparison.Ordinal);
        }

        // ...and the SCOPED facade registration is the only place the recovery facade is built: it is
        // registered with AddScoped<IComposerFacade>, never with AddSingleton.
        Assert.Contains("AddScoped<IComposerFacade>", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<IComposerFacade>", registration, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(registration, "new ComposerRuntimeFacade(") == 1,
            "the recovery facade is constructed in exactly ONE registration");

        // Sanity: the flag really reaches the fixture this row is named for.
        await using var fixture = new RegistrationFixture(
            new ComposerRuntimeFacadeDeployment(PerUserRuntimeEnabled: gateEnabled));
        using var scope = fixture.Provider.CreateScope();
        Assert.Equal(
            gateEnabled ? typeof(ComposerRuntimeFacade) : typeof(ComposerFacade),
            scope.ServiceProvider.GetRequiredService<IComposerFacade>().GetType());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task RegistrationFixture_AwaitsTeardownAndObservesFailures_OnEveryExit(bool bodyFails, bool disposalFails) =>
        RuntimeFacadeFixtureDisposalProof.VerifyAsync(
            configure => new RegistrationFixture(ComposerRuntimeFacadeDeployment.LegacyOnly, configure),
            fixture => fixture.Provider, fixture => fixture.Manager, bodyFails, disposalFails);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RegistrationFixture_JoinsActualRuntimeBeforeProviderAndBackingResources(bool bodyFails) =>
        RuntimeFacadeFixtureDisposalProof.VerifyManagerAsync(
            (configure, client) => new RegistrationFixture(ComposerRuntimeFacadeDeployment.LegacyOnly, configure, client),
            fixture => fixture.Provider, fixture => fixture.Manager, bodyFails);

    /// <summary>The legacy Composer a legacy facade drives (it holds exactly that instance).</summary>
    private static Composer? ComposerOfFacade(IComposerFacade facade) =>
        (Composer?)typeof(ComposerFacade)
            .GetField("_composer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(facade);

    /// <summary>Counts the non-overlapping occurrences of a literal substring.</summary>
    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Counts the statements containing <paramref name="fragment"/> (semicolon-delimited, code-only).</summary>
    private static IEnumerable<string> StatementsContaining(string source, string fragment)
    {
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != ';')
                continue;

            var statement = source.Substring(start, i - start + 1);
            if (statement.Contains(fragment, StringComparison.Ordinal))
                yield return statement.Length > 400 ? statement[^400..] : statement;

            start = i + 1;
        }
    }

    /// <summary>
    /// A minimal container using the PRODUCTION registration seam: the legacy Composer, the manager, the scoped
    /// circuit accessor and the security primitives, then <see cref="ComposerFacadeRegistration"/>.
    /// </summary>
    private sealed class RegistrationFixture : IAsyncDisposable
    {
        private readonly CopilotHiveDbContext _db;
        private readonly string _dir;

        public RegistrationFixture(ComposerRuntimeFacadeDeployment deployment,
            Action<IServiceCollection>? configureServices = null,
            Func<string, IChatClient>? chatClientFactory = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), $"composer-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = CopilotHiveDbContext.CreateInMemory();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<HiveConfigFile>(new HiveConfigFile
            {
                Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
                Models = new ModelsConfig { AvailableModels = [] },
            });
            services.AddSingleton<IGoalStore>(new GoalStore(
                _db, NullLogger<GoalStore>.Instance));
            services.AddSingleton<HiveAuthMode>(HiveAuthMode.Open);
            services.AddSingleton<HivePolicy>();
            services.AddSingleton(sp => new HivePrincipalResolver(
                sp.GetRequiredService<UserService>(),
                sp.GetRequiredService<HiveAuthMode>(),
                NullLogger<HivePrincipalResolver>.Instance));
            services.AddSingleton<UserService>(sp => new UserService(
                new NullDbFactory(_db),
                NullLogger<UserService>.Instance));
            services.AddSingleton(sp => new ComposerFactory(sp, _dir, ollamaApiKey: null,
                chatClientFactory: chatClientFactory, providerOf: _ => "ollama-local"));
            services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
            services.AddSingleton(sp => new ComposerRuntimeManager(
                sp.GetRequiredService<ComposerFactory>(),
                sp.GetRequiredService<UserService>(),
                NullLogger<ComposerRuntimeManager>.Instance));
            services.AddScoped(sp => new CircuitUserPrincipalAccessor(
                sp.GetRequiredService<HivePrincipalResolver>(),
                sp.GetRequiredService<HivePolicy>()));

            // THE PRODUCTION SEAM.
            services.AddComposerRuntimeFacades(deployment);

            configureServices?.Invoke(services);
            Provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });

            Manager = Provider.GetRequiredService<ComposerRuntimeManager>();
        }

        public ServiceProvider Provider { get; }

        public ComposerRuntimeManager Manager { get; }

        public async ValueTask DisposeAsync()
        {
            // ORDER IS THE CONTRACT: the manager's actual teardown and the provider's disposal of every
            // Composer actor are AWAITED to completion BEFORE the backing database and the state
            // directory go away, and each step runs even if an earlier one failed. Failures are
            // collected and surfaced, never swallowed.
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());
            await Step(() => { _db.Dispose(); return Task.CompletedTask; });
            await Step(() =>
            {
                TestHelpers.ForceDeleteDirectory(_dir);
                return Task.CompletedTask;
            });

            if (failures.Count > 0)
                throw new AggregateException("Gate fixture cleanup failed", failures);
        }

        /// <summary>An <see cref="IDbContextFactory{TContext}"/> over the fixture's context.</summary>
        private sealed class NullDbFactory(CopilotHiveDbContext context)
            : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext() => context;
        }
    }
}
