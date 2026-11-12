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
/// THE DEPLOYMENT CONTRACT — THE FINAL ONE: the host AUTHENTICATION MODE alone decides the live Composer
/// path, and in authenticated mode there is NO OFF PATH.
/// <para>
/// The retired variable <c>COMPOSER_PER_USER_RUNTIME</c> is read by nothing: unset, <c>"true"</c>,
/// <c>"false"</c> and every other value produce the SAME decision — the integrated per-user path when
/// authentication is enabled, the legacy singleton when the host is in open mode. The regression this
/// class exists to prevent is a split runtime: a live legacy singleton coexisting with per-user runtimes
/// in an authenticated installation.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerRuntimeFacadeGateTests : IDisposable
{
    /// <summary>Every value the retired variable used to be read with — plus a few it never was.</summary>
    public static TheoryData<string?> AllRetiredValues => new(
        null, "", "true", "TRUE", "True", "false", "FALSE", "0", "1", "yes", "enabled", "per-user");

    private readonly string? _gateBeforeTest = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);

    public void Dispose() => Assert.Equal(_gateBeforeTest,
        Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable));

    // ── The decision function: the mode, and ONLY the mode ───────────────────────────────────────

    /// <summary>
    /// THE AUTHENTICATION MODE ALONE DECIDES: an authenticated host is ALWAYS the integrated per-user
    /// path, an open host is ALWAYS the legacy singleton — independently of the retired variable.
    /// </summary>
    [Fact]
    public void TheDecision_IsDerivedFromTheAuthMode_Only()
    {
        Assert.True(ComposerRuntimeFacadeDeployment.ForAuthMode(HiveAuthMode.AuthenticationEnabled).PerUserRuntimeEnabled,
            "an authenticated installation has NO legacy-singleton path");
        Assert.False(ComposerRuntimeFacadeDeployment.ForAuthMode(HiveAuthMode.Open).PerUserRuntimeEnabled,
            "open mode is the legacy single-operator installation");

        Assert.True(ComposerRuntimeFacadeDeployment.PerUserRuntime.PerUserRuntimeEnabled);
        Assert.False(ComposerRuntimeFacadeDeployment.LegacyOnly.PerUserRuntimeEnabled);
    }

    /// <summary>
    /// THE RETIRED VARIABLE SELECTS NOTHING — IN EITHER MODE: for every value it has ever been given, the
    /// decision made from the environment equals the decision made from the OAuth predicate alone.
    /// <para>
    /// The rows are the WHOLE point of this slice: <c>"false"</c> in particular used to keep the legacy
    /// singleton live while authentication was enabled. It no longer can.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRetiredValues))]
    public void TheRetiredVariable_SelectsNothing_InEitherMode(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        var previousId = Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable);
        var previousSecret = Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, value);

            // AUTHENTICATED: every value — including "false" — selects the integrated per-user path.
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, "id");
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, "secret");
            Assert.True(
                ComposerRuntimeFacadeDeployment.FromEnvironment().PerUserRuntimeEnabled,
                $"'{value ?? "<unset>"}' must not create an OFF path while authentication is enabled");

            // OPEN: every value keeps the legacy singleton (byte-identical to before).
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, null);
            Assert.False(
                ComposerRuntimeFacadeDeployment.FromEnvironment().PerUserRuntimeEnabled,
                $"'{value ?? "<unset>"}' must not move open mode off the legacy singleton");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, previousId);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, previousSecret);
        }
    }

    /// <summary>
    /// AN UNSET VARIABLE IN OPEN MODE IS THE SHIPPED DEFAULT — read through the SAME seam
    /// <c>Program.cs</c> uses, so this row fails if the open-mode default ever flips.
    /// </summary>
    [Fact]
    public void TheUnsetVariable_InOpenMode_IsTheLegacyDefault()
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        var previousId = Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable);
        var previousSecret = Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, null);

            var deployment = ComposerRuntimeFacadeDeployment.FromEnvironment();

            Assert.False(deployment.PerUserRuntimeEnabled);
            Assert.Equal(ComposerRuntimeFacadeDeployment.LegacyOnly, deployment);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, previousId);
            Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, previousSecret);
        }
    }

    /// <summary>
    /// THE VARIABLE IS NOT EVEN READ: the production deployment source contains no
    /// <c>GetEnvironmentVariable</c> call for it, so no future edit can quietly reintroduce the split
    /// path through the environment. The name survives only as the retired identifier.
    /// </summary>
    [Fact]
    public void TheRetiredVariable_IsNotReadAnywhere()
    {
        var repoRoot = TestRepoRoot.Find();
        var source = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(repoRoot, "src", "CopilotHive", "Services", "ComposerRuntimeFacadeDeployment.cs"))
                .ReplaceLineEndings("\n"));

        Assert.DoesNotContain(
            $"GetEnvironmentVariable({ComposerRuntimeFacadeDeployment.EnvironmentVariable})", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"GetEnvironmentVariable(EnvironmentVariable)", source, StringComparison.Ordinal);

        // The decision really comes from the explicit mode.
        Assert.Contains("HiveAuthMode.FromEnvironment()", source, StringComparison.Ordinal);
        Assert.Contains("ForAuthMode(", source, StringComparison.Ordinal);
    }

    // ── The production registration seam: selection follows the mode ──────────────────────────────

    /// <summary>
    /// OPEN MODE: every scope resolves the SAME legacy <see cref="ComposerFacade"/> instance, and it is the
    /// very object the singleton registration produced — so the operation runs on the legacy singleton
    /// <see cref="Composer"/> exactly as before.
    /// </summary>
    [Fact]
    public async Task OpenMode_EveryScopeResolvesTheSameLegacyFacade()
    {
        await using var fixture = new RegistrationFixture(HiveAuthMode.Open, ComposerRuntimeFacadeDeployment.LegacyOnly);
        using var first = fixture.Provider.CreateScope();
        using var second = fixture.Provider.CreateScope();

        var fromFirst = first.ServiceProvider.GetRequiredService<IComposerFacade>();
        var fromSecond = second.ServiceProvider.GetRequiredService<IComposerFacade>();

        Assert.IsType<ComposerFacade>(fromFirst);
        // The SAME legacy instance in every scope: the legacy facade is itself the singleton, so the scoped
        // registration is a pass-through and the behaviour is byte-identical to the pre-slice host.
        Assert.Same(fromFirst, fromSecond);
        Assert.False(fromFirst is ComposerRuntimeFacade,
            "the per-user recovery facade must never be live in open mode");

        // ...and it really drives the LEGACY singleton Composer.
        var legacy = fixture.Provider.GetRequiredService<Composer>();
        Assert.Same(legacy, ComposerOfFacade(fromFirst));
    }

    /// <summary>
    /// WHY PRODUCTION MUST RESOLVE PER REQUEST: the facade is SCOPED now, so resolving it from the ROOT
    /// provider is REFUSED under scope validation. That is precisely the startup-closure capture the hub's
    /// per-request resolution removes — and it is demonstrated here rather than assumed.
    /// </summary>
    [Fact]
    public async Task ResolvingTheFacadeFromTheRootProvider_IsRefused()
    {
        await using var fixture = new RegistrationFixture(HiveAuthMode.Open, ComposerRuntimeFacadeDeployment.LegacyOnly);

        var error = Assert.Throws<InvalidOperationException>(
            () => fixture.Provider.GetRequiredService<IComposerFacade>());
        Assert.Contains("scoped service", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AUTHENTICATED MODE SELECTS THE RECOVERY FACADE, PER SCOPE: two scopes get DIFFERENT instances (each
    /// holding its OWN scoped runtime accessor), which is exactly why the registration can never be a
    /// singleton.
    /// </summary>
    [Fact]
    public async Task AuthenticatedMode_EachScopeResolvesItsOwnRecoveryFacade()
    {
        await using var fixture = new RegistrationFixture(
            HiveAuthMode.AuthenticationEnabled, ComposerRuntimeFacadeDeployment.PerUserRuntime);

        using var first = fixture.Provider.CreateScope();
        using var second = fixture.Provider.CreateScope();

        var fromFirst = first.ServiceProvider.GetRequiredService<IComposerFacade>();
        var fromSecond = second.ServiceProvider.GetRequiredService<IComposerFacade>();

        Assert.IsType<ComposerRuntimeFacade>(fromFirst);
        Assert.IsType<ComposerRuntimeFacade>(fromSecond);
        Assert.NotSame(fromFirst, fromSecond);
    }

    /// <summary>
    /// THE RETIRED GATE CANNOT REINSTATE THE SPLIT RUNTIME: declaring <see cref="ComposerRuntimeFacadeDeployment.LegacyOnly"/>
    /// in an AUTHENTICATED fixture is a NO-OP — the scope still resolves the recovery facade, and the
    /// declaration is recorded rather than honoured.
    /// <para>
    /// This is the row that fails if the deployment ever starts honouring a declared OFF path again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADefaultLegacyDeclaration_CannotKeepTheLegacySingletonLive_InAuthenticatedMode()
    {
        await using var fixture = new RegistrationFixture(
            HiveAuthMode.AuthenticationEnabled, ComposerRuntimeFacadeDeployment.LegacyOnly);

        using var scope = fixture.Provider.CreateScope();

        var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();

        Assert.IsType<ComposerRuntimeFacade>(facade);
        Assert.False(
            facade is ComposerFacade,
            "an authenticated installation must never fall back to the legacy singleton facade");
        Assert.True(
            scope.ServiceProvider.GetRequiredService<ComposerRuntimeFacadeDeployment>().PerUserRuntimeEnabled);
    }

    /// <summary>
    /// THE MODE DECIDES, NOT THE DECLARATION — in BOTH directions, so a declaration can never add a legacy
    /// path to an authenticated host either:
    /// <list type="bullet">
    /// <item>authenticated + declared legacy → per-user (the OFF path is a no-op);</item>
    /// <item>authenticated + declared per-user → per-user;</item>
    /// <item>open + declared legacy → legacy;</item>
    /// <item>open + declared per-user → legacy (open mode has no per-user runtime to select).</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TheAuthModeDecides_TheDeclarationNeverDoes(bool authEnabled, bool declaredPerUser)
    {
        await using var fixture = new RegistrationFixture(
            authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open,
            new ComposerRuntimeFacadeDeployment(declaredPerUser));

        using var scope = fixture.Provider.CreateScope();

        Assert.Equal(
            authEnabled ? typeof(ComposerRuntimeFacade) : typeof(ComposerFacade),
            scope.ServiceProvider.GetRequiredService<IComposerFacade>().GetType());
    }

    /// <summary>
    /// NO SINGLETON CAPTURES THE SCOPED ACCESSOR IN EITHER MODE: the registration never closes a singleton
    /// over it, so the per-circuit identity can never leak host-wide.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoRegistrationCapturesTheScopedAccessorInASingleton(bool authEnabled)
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

        // Sanity: the mode really reaches the fixture this row is named for.
        await using var fixture = new RegistrationFixture(
            authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open,
            ComposerRuntimeFacadeDeployment.ForAuthMode(authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open));
        using var scope = fixture.Provider.CreateScope();
        Assert.Equal(
            authEnabled ? typeof(ComposerRuntimeFacade) : typeof(ComposerFacade),
            scope.ServiceProvider.GetRequiredService<IComposerFacade>().GetType());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task RegistrationFixture_AwaitsTeardownAndObservesFailures_OnEveryExit(bool bodyFails, bool disposalFails) =>
        RuntimeFacadeFixtureDisposalProof.VerifyAsync(
            configure => new RegistrationFixture(
                HiveAuthMode.Open, ComposerRuntimeFacadeDeployment.LegacyOnly, configure),
            fixture => fixture.Provider, fixture => fixture.Manager, bodyFails, disposalFails);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RegistrationFixture_JoinsActualRuntimeBeforeProviderAndBackingResources(bool bodyFails) =>
        RuntimeFacadeFixtureDisposalProof.VerifyManagerAsync(
            (configure, client) => new RegistrationFixture(
                HiveAuthMode.Open, ComposerRuntimeFacadeDeployment.LegacyOnly, configure, client),
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

        public RegistrationFixture(
            HiveAuthMode mode,
            ComposerRuntimeFacadeDeployment deployment,
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
            services.AddSingleton(mode);
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
