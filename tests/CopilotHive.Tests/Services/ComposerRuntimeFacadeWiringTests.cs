using System.Security.Claims;
using System.Runtime.ExceptionServices;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE LIFETIME WIRING of the per-user runtime path, over the PRODUCTION registration seam
/// (<see cref="ComposerFacadeRegistration.AddComposerRuntimeFacades"/>):
/// <list type="bullet">
/// <item>the manager is a SINGLETON and the runtime accessor and the facade are SCOPED;</item>
/// <item>the open-mode synthetic local operator falls back to the LEGACY singleton Composer;</item>
/// <item>an AUTHENTICATED refusal NEVER falls back to the legacy Composer;</item>
/// <item>the legacy-history migration is keyed on the IDENTITY (the original account), never on
/// the role;</item>
/// <item>the non-original provider gate is unchanged.</item>
/// </list>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ComposerRuntimeFacadeWiringTests : IAsyncLifetime
{
    private readonly string? _gateBeforeTest = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
    private const string Model = "wiring-model";
    private const string OtherModel = "wiring-other-model";
    private const string OriginalId = "wiring-original";
    private const string SecondId = "wiring-second";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-wiring-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<ComposerRuntimeManager> _managers = [];
    private readonly List<Composer> _composers = [];

    private int _originalId;
    private int _secondId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _originalId = (await _auth.SeedAsync(OriginalId, UserRole.Member)).Id;
        _secondId = (await _auth.SeedAsync(SecondId, UserRole.Member)).Id;
    }

    // ── 1. The lifetimes ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE MANAGER IS A SINGLETON AND THE ACCESSOR IS SCOPED: two scopes share the one manager
    /// but never share an accessor — which is exactly why a facade may hold a scoped accessor
    /// without any singleton capturing it.
    /// </summary>
    [Fact]
    public async Task TheManagerIsASingleton_TheAccessorIsScoped()
    {
        await using var fixture = new WiringFixture(authEnabled: true, users: _auth.Directory.Service);
        using var first = fixture.Provider.CreateScope();
        using var second = fixture.Provider.CreateScope();

        // The manager is the ONE owner of every runtime: the same instance in every scope.
        Assert.Same(
            fixture.Provider.GetRequiredService<ComposerRuntimeManager>(),
            first.ServiceProvider.GetRequiredService<ComposerRuntimeManager>());
        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeManager>(),
            second.ServiceProvider.GetRequiredService<ComposerRuntimeManager>());

        // The accessor is SCOPED: one per scope.
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>());
    }

    /// <summary>
    /// THE FACADE'S LIFETIME FOLLOWS THE AUTHENTICATION MODE: in open mode every scope resolves the
    /// LEGACY SINGLETON facade (the pre-slice behaviour, byte-identical), and in authenticated mode each
    /// scope resolves its OWN recovery facade — the scoped lifetime that lets it hold the scoped
    /// accessor.
    /// </summary>
    [Fact]
    public async Task TheFacadesLifetimeFollowsTheAuthenticationMode()
    {
        await using var open = new WiringFixture(authEnabled: false, users: _auth.Directory.Service);
        using var openFirst = open.Provider.CreateScope();
        using var openSecond = open.Provider.CreateScope();

        // OPEN MODE: the legacy singleton facade in every scope.
        var legacyFromFirst = openFirst.ServiceProvider.GetRequiredService<IComposerFacade>();
        var legacyFromSecond = openSecond.ServiceProvider.GetRequiredService<IComposerFacade>();
        Assert.IsType<ComposerFacade>(legacyFromFirst);
        Assert.Same(legacyFromFirst, legacyFromSecond);

        await using var authenticated = new WiringFixture(authEnabled: true, users: _auth.Directory.Service);
        using var authFirst = authenticated.Provider.CreateScope();
        using var authSecond = authenticated.Provider.CreateScope();

        // AUTHENTICATED MODE: a per-scope recovery facade, each holding its OWN scoped accessor.
        var recoveryFromFirst = authFirst.ServiceProvider.GetRequiredService<IComposerFacade>();
        var recoveryFromSecond = authSecond.ServiceProvider.GetRequiredService<IComposerFacade>();
        Assert.IsType<ComposerRuntimeFacade>(recoveryFromFirst);
        Assert.NotSame(recoveryFromFirst, recoveryFromSecond);
    }

    /// <summary>
    /// THE LEGACY COMPOSER IS THE SINGLETON THE OPEN-MODE FALLBACK SERVES: the fallback target of
    /// the recovery facade is the very legacy singleton the host registered.
    /// </summary>
    [Fact]
    public async Task TheLegacyComposerIsTheSingletonTheOpenModeFallbackServes()
    {
        await using var fixture = new WiringFixture(authEnabled: false, users: _auth.Directory.Service);

        var legacyComposer = fixture.Provider.GetRequiredService<Composer>();
        var legacyFacade = fixture.Provider.GetRequiredService<ComposerFacade>();

        Assert.Same(legacyComposer, ComposerOf(legacyFacade));
    }

    // ── 2. The open-mode fallback over the REAL manager ────────────────────────────────────────

    /// <summary>
    /// THE OPEN-MODE SYNTHETIC LOCAL OPERATOR FALLS BACK TO THE LEGACY SINGLETON COMPOSER: with
    /// authentication disabled the resolver yields the synthetic operator, the REAL manager refuses
    /// it (<see cref="ComposerRuntimeRefusal.LocalOperator"/>), and the recovery facade serves the
    /// operation from the LEGACY Composer — which is therefore connected by the operation itself.
    /// </summary>
    [Fact]
    public async Task OpenModeLocalOperator_FallsBackToTheLegacySingletonComposer()
    {
        await using var fixture = new WiringFixture(authEnabled: false, users: _auth.Directory.Service);

        using var scope = fixture.Provider.CreateScope();
        var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();

        // The gate is ON for this row: the recovery facade is the one under test.
        var recovery = new ComposerRuntimeFacade(
            scope.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            scope.ServiceProvider.GetRequiredService<ComposerFacade>(),
            NullLogger<ComposerRuntimeFacade>.Instance);

        // The accessor really reports open mode and really gets the local-operator refusal.
        var access = await scope.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>()
            .AcquireForRequestAsync(actingUser: null, Ct);
        Assert.True(access.IsAuthorized, "the local operator holds every capability");
        Assert.Equal(ComposerRuntimeStatus.Refused, access.Status);
        Assert.Equal(ComposerRuntimeRefusal.LocalOperator, access.Result!.Refusal);
        Assert.Null(access.Runtime);

        // The fallback serves the operation from the LEGACY Composer.
        var switched = await recovery.SwitchModelAsync(
            actingUser: null, OtherModel, "medium", Ct);

        Assert.True(switched.Success, switched.Error ?? "open-mode fallback failed");
        Assert.Equal(OtherModel, switched.Value!.Model);

        var legacy = fixture.Provider.GetRequiredService<Composer>();
        Assert.Equal(OtherModel, legacy.GetStats()?.Model);
        // The per-user manager created NOTHING: the local operator never gets a runtime.
        Assert.Empty(ManagerEntries(fixture.Manager));
    }

    /// <summary>
    /// AN AUTHENTICATED REFUSAL NEVER FALLS BACK TO THE LEGACY COMPOSER: the SAME refusal in
    /// authentication-enabled mode is a 403, and the legacy Composer is never touched — so a
    /// refused account can never inherit the original admin's runtime.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedRefusal_NeverFallsBackToTheLegacyComposer()
    {
        await using var fixture = new WiringFixture(authEnabled: true, users: _auth.Directory.Service);

        using var scope = fixture.Provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>();
        var recovery = new ComposerRuntimeFacade(
            accessor,
            scope.ServiceProvider.GetRequiredService<ComposerFacade>(),
            NullLogger<ComposerRuntimeFacade>.Instance);

        // A DISABLED account is refused by the REAL resolver (never reaches the manager as a
        // runtime), so the facade answers the authorization denial.
        await _auth.SetEnabledAsync(SecondId, enabled: false);
        try
        {
            var result = await recovery.SwitchModelAsync(
                FacadeAuthorizationHarness.Principal(SecondId), OtherModel, "medium", Ct);

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);

            // The legacy Composer was never touched: no model was applied to it.
            var legacy = fixture.Provider.GetRequiredService<Composer>();
            Assert.NotEqual(OtherModel, legacy.GetStats()?.Model);
            Assert.False(legacy.IsConnected);
        }
        finally
        {
            await _auth.SetEnabledAsync(SecondId, enabled: true);
        }
    }

    // ── 3. The identity-keyed migration ─────────────────────────────────────────────────────────

    /// <summary>
    /// THE LEGACY-HISTORY MIGRATION IS KEYED ON THE IDENTITY, NEVER ON THE ROLE: the SECOND
    /// account (a plain Member, exactly the same role as the original) never claims the legacy
    /// session, while the ORIGINAL account does — and the original's ROLE is irrelevant to that
    /// decision.
    /// </summary>
    [Fact]
    public async Task TheLegacyMigration_IsKeyedOnTheIdentity_NeverOnTheRole()
    {
        await using var fixture = new WiringFixture(
            authEnabled: true, users: _auth.Directory.Service, stateDir: _dir);

        // The legacy session exists: only the original account may claim it.
        var legacyPath = Path.Combine(_dir, "composer-session.json");
        Directory.CreateDirectory(_dir);
        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "legacy-history-marker"));
        await session.SaveAsync(legacyPath, Ct);

        // The SECOND account — the SAME role as the original — never claims it.
        var second = await fixture.Manager.GetOrCreateAsync(
            await ResolveAsync(_secondId), Ct);
        Assert.True(second.IsGranted);
        Assert.False(second.Runtime!.State.LegacyClaimEligible);
        Assert.False(File.Exists(ComposerUserState.LegacyMigrationEvidencePath(_dir, second.Runtime.State)));

        // The ORIGINAL account does claim it.
        var original = await fixture.Manager.GetOrCreateAsync(
            await ResolveAsync(_originalId), Ct);
        Assert.True(original.IsGranted);
        Assert.True(original.Runtime!.State.LegacyClaimEligible);
        Assert.True(File.Exists(ComposerUserState.LegacyMigrationEvidencePath(_dir, original.Runtime.State)));

        // ...and the original's ROLE is irrelevant: demoting it to Viewer keeps the claim keyed
        // to the identity.
        await _auth.SetRoleAsync(OriginalId, UserRole.Viewer);
        try
        {
            var demoted = await fixture.Manager.GetOrCreateAsync(
                await ResolveAsync(_originalId), Ct);
            Assert.True(demoted.IsGranted);
            Assert.True(demoted.Runtime!.State.LegacyClaimEligible);
        }
        finally
        {
            await _auth.SetRoleAsync(OriginalId, UserRole.Member);
        }
    }

    /// <summary>
    /// THE NON-ORIGINAL PROVIDER GATE IS UNCHANGED BY THIS SLICE: a non-original account whose
    /// primary model is Copilot-backed settles DEFERRED — it never reaches the original admin's
    /// token — while an Ollama-backed selection connects.
    /// </summary>
    [Fact]
    public async Task TheNonOriginalProviderGate_IsUnchanged()
    {
        // The SECOND account is NOT the original (the original is the lowest-id activated row),
        // so the identity-bound provider gate applies to it.
        Assert.NotEqual(_originalId, _secondId);

        // A Copilot-backed primary for the NON-original account: the gate defers it.
        await using var copilot = new WiringFixture(
            authEnabled: true, users: _auth.Directory.Service);
        copilot.SetPrimaryModel("copilot/primary");
        var deferred = await copilot.Manager.GetOrCreateAsync(
            await ResolveAsync(_secondId), Ct);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        Assert.False(deferred.Runtime!.Composer.IsConnected);

        // An Ollama-backed primary for the SAME non-original account: it connects.
        await using var ollama = new WiringFixture(
            authEnabled: true, users: _auth.Directory.Service);
        ollama.SetPrimaryModel("ollama-local/other");
        var connected = await ollama.Manager.GetOrCreateAsync(
            await ResolveAsync(_secondId), Ct);
        Assert.Equal(ComposerRuntimeStatus.Connected, connected.Status);
        Assert.True(connected.Runtime!.Composer.IsConnected);
    }

    // ── 4. The deployment contract: the auth mode, and ONLY the auth mode ──────────────────────

    /// <summary>
    /// OPEN MODE IS THE LEGACY SINGLETON, BEHAVIOURALLY: the scope resolves the LEGACY facade, the
    /// operation runs on the legacy Composer, and the per-user manager's cache stays EMPTY — so no
    /// per-user runtime can be observed anywhere in an open installation. The retired variable is set
    /// to <c>"true"</c> for this row, so the row also proves it can no longer move OPEN mode onto
    /// per-user runtimes.
    /// </summary>
    [Fact]
    public async Task OpenMode_IsTheLegacySingleton_AndNoPerUserRuntimeIsCreated()
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, "true");

            Assert.False(ComposerRuntimeFacadeDeployment.ForAuthMode(HiveAuthMode.Open).PerUserRuntimeEnabled);

            await using var fixture = new WiringFixture(authEnabled: false, users: _auth.Directory.Service);
            using var scope = fixture.Provider.CreateScope();

            var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();
            Assert.IsType<ComposerFacade>(facade);
            Assert.NotNull(ComposerOf(facade));

            // The operation runs on the LEGACY Composer and creates NO per-user runtime.
            var read = await facade.GetModelsAsync(
                FacadeAuthorizationHarness.Principal(SecondId), Ct);
            Assert.True(read.Success, read.Error ?? "open-mode read failed");
            Assert.Empty(ManagerEntries(fixture.Manager));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// AUTHENTICATED MODE IS THE PER-USER PATH FOR EVERY VALUE OF THE RETIRED VARIABLE: with the variable
    /// <c>null</c>, <c>"true"</c> AND <c>"false"</c> the scope resolves the recovery facade, the operation
    /// really drives the per-user runtime, and the manager — not the facade — owns it.
    /// <para>
    /// The <c>"false"</c> row is the regression this goal exists to close: it used to keep the legacy
    /// singleton live in an authenticated installation.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public async Task AuthenticatedMode_IsThePerUserPath_ForEveryRetiredVariableValue(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, value);

            // The SAME production seam Program.cs uses: the deployment derived from the auth mode.
            var deployment = ComposerRuntimeFacadeDeployment.ForAuthMode(HiveAuthMode.AuthenticationEnabled);
            Assert.True(deployment.PerUserRuntimeEnabled,
                $"'{value ?? "<unset>"}' must not remove the authenticated per-user path");

            await using var fixture = new WiringFixture(
                authEnabled: true, users: _auth.Directory.Service, declaredDeployment: deployment);
            using var scope = fixture.Provider.CreateScope();

            var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();
            Assert.IsType<ComposerRuntimeFacade>(facade);

            var read = await facade.GetModelsAsync(
                FacadeAuthorizationHarness.Principal(SecondId), Ct);

            Assert.True(read.Success, read.Error ?? "authenticated read failed");
            // The per-user runtime was really created — by the manager, under the caller's identity.
            Assert.Contains(_secondId, ManagerEntries(fixture.Manager));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
        }
    }

    // ── 5. The accessor alias: ONE scoped instance behind both types ───────────────────────────

    /// <summary>
    /// THE PRODUCTION REGISTRATION MAKES <see cref="IComposerRuntimeAccessor"/> AN ALIAS OF THE VERY SAME
    /// SCOPED <see cref="ComposerRuntimeAccessor"/> INSTANCE: the ComposerChat circuit (which injects the
    /// interface) and the recovery facade (which is built from the concrete type) therefore share ONE
    /// per-circuit identity — never two accessors with divergent circuit state.
    /// <para>
    /// The row is discriminating in both directions: it fails if the interface is registered with its own
    /// factory (a second instance), with a different lifetime, or resolved from a different scope.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRuntimeAccessorInterface_IsAnAliasOfTheSameScopedInstance(bool authEnabled)
    {
        await using var fixture = new WiringFixture(authEnabled: authEnabled, users: _auth.Directory.Service);

        using var first = fixture.Provider.CreateScope();
        using var second = fixture.Provider.CreateScope();

        // Within ONE scope: the interface and the concrete type are the SAME object.
        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());
        Assert.Same(
            second.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // ACROSS scopes: a DIFFERENT accessor per scope — the interface is scoped, never a singleton.
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>());

        // ...and the ROOT provider refuses it, exactly like the concrete scoped type.
        Assert.Throws<InvalidOperationException>(
            () => fixture.Provider.GetRequiredService<IComposerRuntimeAccessor>());

        // The recovery facade handed to a request really holds the SAME instance the circuit resolved.
        if (authEnabled)
        {
            _ = first.ServiceProvider.GetRequiredService<IComposerFacade>();
            var facadeAccessor = (IComposerRuntimeAccessor)typeof(ComposerRuntimeFacade)
                .GetField("_accessor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(first.ServiceProvider.GetRequiredService<IComposerFacade>())!;
            Assert.Same(first.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>(), facadeAccessor);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task WiringFixture_AwaitsTeardownAndObservesFailures_OnEveryExit(bool bodyFails, bool disposalFails) =>
        RuntimeFacadeFixtureDisposalProof.VerifyAsync(
            configure => new WiringFixture(
                users: _auth.Directory.Service, configureServices: configure),
            fixture => fixture.Provider, fixture => fixture.Manager, bodyFails, disposalFails);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WiringFixture_JoinsActualRuntimeBeforeProviderAndBackingResources(bool bodyFails) =>
        RuntimeFacadeFixtureDisposalProof.VerifyManagerAsync(
            (configure, client) => new WiringFixture(
                configureServices: configure, chatClientFactory: client),
            fixture => fixture.Provider, fixture => fixture.Manager, bodyFails);

    /// <summary>
    /// Exercise this xUnit CLASS's real DisposeAsync, not the inner WiringFixture's cleanup.
    /// A failed restoration check must be retained while actual manager/provider joins and all
    /// backing-resource cleanup still run. The matching no-leak row proves ordinary cleanup too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassCleanup_GateCheckFailureStillJoinsAndDisposesEveryResource(bool leakGate)
    {
        var previous = Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable);
        var subject = new ComposerRuntimeFacadeWiringTests();
        await using var hold = new RuntimeAsyncDisposalHold();
        var providerDisposal = new ClassCleanupRendezvous();
        WiringFixture? owned = null;
        Task? cleanup = null;
        Composer? runtimeComposer = null;
        Composer? legacy = null;
        ExceptionDispatchInfo? primary = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            await subject.InitializeAsync();
            using (var directoryDb = subject._auth.Directory.CreateDbContext())
            using (var siblingDb = subject._auth.Directory.CreateDbContext())
            {
                Assert.Equal(2, directoryDb.Users.Count());

                // The directory never shares one connection instance between contexts: each context
                // OWNS its own connection (that is what lets two contexts initialise concurrently),
                // while both still read the SAME live in-memory database.
                Assert.NotSame(
                    directoryDb.Database.GetDbConnection(),
                    siblingDb.Database.GetDbConnection());
                Assert.Equal(2, siblingDb.Users.Count());
            }
            owned = new WiringFixture(
                users: subject._auth.Directory.Service, stateDir: subject._dir,
                configureServices: services => services.AddSingleton(_ => providerDisposal),
                chatClientFactory: _ => hold);
            subject._managers.Add(owned.Manager);
            subject._providers.Add(owned.Provider);
            var runtime = await owned.Manager.GetOrCreateAsync(
                await subject.ResolveAsync(subject._originalId), Ct);
            Assert.Equal(ComposerRuntimeStatus.Connected, runtime.Status);
            runtimeComposer = runtime.Runtime!.Composer;
            legacy = new Composer(Model, NullLogger<Composer>.Instance,
                new GoalStore(subject._goalDb, NullLogger<GoalStore>.Instance), stateDir: subject._dir,
                availableModels: [Model], chatClientFactory: _ => new Mock<IChatClient>().Object);
            subject._composers.Add(legacy);
            await legacy.ConnectAsync(Ct);

            // Resolved LAST: provider disposal cannot enter until the explicit manager join completes.
            _ = owned.Provider.GetRequiredService<ClassCleanupRendezvous>();
            var marker = Path.Combine(subject._dir, "class-cleanup.marker");
            await File.WriteAllTextAsync(marker, "alive through async cleanup", Ct);
            hold.Arm();
            if (leakGate)
                Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable,
                    previous == "true" ? "false" : "true");

            cleanup = subject.DisposeAsync().AsTask();
            var first = await Task.WhenAny(hold.Entered.Task, providerDisposal.Entered.Task, cleanup)
                .WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Same(hold.Entered.Task, first);
            Assert.False(cleanup.IsCompleted, "the class must join the actual manager even after a failed gate check");
            Assert.False(runtimeComposer.TeardownCompletion.IsCompleted);
            Assert.False(providerDisposal.Entered.Task.IsCompleted);
            Assert.True(subject._goalDb.Database.CanConnect());
            Assert.True(File.Exists(marker));

            hold.Release();
            await providerDisposal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(runtimeComposer.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(legacy.TeardownCompletion.IsCompletedSuccessfully);
            Assert.False(cleanup.IsCompleted, "the class must await the provider before disposing backing resources");
            Assert.True(subject._goalDb.Database.CanConnect());
            Assert.True(File.Exists(marker));
            providerDisposal.Release();

            var error = await Record.ExceptionAsync(() => cleanup.WaitAsync(TimeSpan.FromSeconds(30), Ct));
            if (leakGate)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                Assert.StartsWith("Wiring test cleanup failed", aggregate.Message, StringComparison.Ordinal);
                var restorationError = Assert.Single(aggregate.InnerExceptions);
                Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(restorationError);
                Assert.Contains("Assert.Equal() Failure", restorationError.Message, StringComparison.Ordinal);
                Assert.NotEqual(previous,
                    Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable));
            }
            else
            {
                Assert.Null(error);
            }

            Assert.True(providerDisposal.Completed.Task.IsCompletedSuccessfully);
            Assert.Throws<ObjectDisposedException>(() => owned.Provider.GetRequiredService<UserService>());
            Assert.Throws<ObjectDisposedException>(() => subject._goalDb.Users.Count());
            // The directory is CLOSED once cleanup ran: a fresh context is REJECTED instead of quietly
            // reopening (with a named shared-cache database a new connection would create a fresh,
            // empty database, so a post-disposal read would answer "no such account").
            Assert.Throws<ObjectDisposedException>(() => subject._auth.Directory.CreateDbContext());
            Assert.False(Directory.Exists(subject._dir), "class state cleanup must run after a failed gate check");
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            // Restore the test process BEFORE this test's own runner cleanup. All deliberate failures
            // above belong to the independently owned subject; none may leak into neighboring tests.
            Environment.SetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable, previous);
            hold.Release();
            providerDisposal.Release();
            async Task Step(Func<Task> action)
            {
                try { await action().WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
            if (cleanup is not null)
                await Step(() => cleanup.ContinueWith(_ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            await Step(() => subject.DisposeAsync().AsTask());
            if (owned is not null) await Step(() => owned.DisposeAsync().AsTask());
            if (runtimeComposer is not null) await Step(() => runtimeComposer.TeardownCompletion);
            if (legacy is not null) await Step(() => legacy.TeardownCompletion);
            // Failure-safe fallback for probes that intentionally break the subject's cleanup.
            await Step(() => subject._goalDb.DisposeAsync().AsTask());
            await Step(() => { subject._auth.Dispose(); return Task.CompletedTask; });
            await Step(() => { TestHelpers.ForceDeleteDirectory(subject._dir); return Task.CompletedTask; });
        }
        if (primary is not null)
        {
            if (cleanupFailures.Count > 0) primary.SourceException.Data["CleanupFailures"] = cleanupFailures;
            primary.Throw();
        }
        Assert.Empty(cleanupFailures);
        hold.AssertHealthy();
    }

    private sealed class ClassCleanupRendezvous : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public async ValueTask DisposeAsync()
        {
            try
            {
                Entered.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            finally { Completed.TrySetResult(); }
        }
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Resolves the CURRENT principal of an account through the production resolver.</summary>
    private async Task<ActingUserPrincipal> ResolveAsync(int userId)
    {
        var resolution = await _auth.Resolver(HiveAuthMode.AuthenticationEnabled)
            .ResolveByUserIdAsync(userId, Ct);
        Assert.True(resolution.IsResolved, resolution.RefusalReason.ToString());
        return resolution.Principal!;
    }

    private static Composer? ComposerOf(IComposerFacade facade) =>
        (Composer?)typeof(ComposerFacade)
            .GetField("_composer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(facade);

    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    /// <summary>
    /// A minimal container using the PRODUCTION registration seam: the legacy Composer, the manager,
    /// the scoped circuit accessor and the security primitives, then
    /// <see cref="ComposerFacadeRegistration.AddComposerRuntimeFacades"/>.
    /// </summary>
    private sealed class WiringFixture : IAsyncDisposable
    {
        private readonly CopilotHiveDbContext _db;

        public WiringFixture(
            bool authEnabled = true,
            UserService? users = null,
            string? stateDir = null,
            Action<IServiceCollection>? configureServices = null,
            Func<string, IChatClient>? chatClientFactory = null,
            ComposerRuntimeFacadeDeployment? declaredDeployment = null)
        {
            _db = CopilotHiveDbContext.CreateInMemory();
            var directory = users ?? new UserService(new NullDbFactory(_db), NullLogger<UserService>.Instance);
            StateDir = stateDir ?? Path.Combine(Path.GetTempPath(), $"composer-wiring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(StateDir);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<HiveConfigFile>(new HiveConfigFile
            {
                Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = Model },
                        new ModelEntry { Name = OtherModel },
                        new ModelEntry { Name = "copilot/primary" },
                        new ModelEntry { Name = "ollama-local/other" },
                    ],
                },
                Composer = new ComposerConfig { Model = Model },
            });
            services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
            services.AddSingleton<HiveAuthMode>(authEnabled
                ? HiveAuthMode.AuthenticationEnabled
                : HiveAuthMode.Open);
            services.AddSingleton<HivePolicy>();
            services.AddSingleton(sp => new HivePrincipalResolver(
                sp.GetRequiredService<UserService>(),
                sp.GetRequiredService<HiveAuthMode>(),
                NullLogger<HivePrincipalResolver>.Instance));
            services.AddSingleton<UserService>(directory);
            services.AddSingleton<LlmSessionRegistry>();
            services.AddSingleton(sp => new ComposerFactory(
                sp,
                StateDir,
                ollamaApiKey: null,
                chatClientFactory: chatClientFactory ?? (_ => new Mock<IChatClient>().Object),
                isTokenAvailable: () => true,
                providerOf: model => model.StartsWith("copilot/", StringComparison.Ordinal)
                    ? "copilot"
                    : "ollama-local"));
            services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
            services.AddSingleton(sp => new ComposerRuntimeManager(
                sp.GetRequiredService<ComposerFactory>(),
                sp.GetRequiredService<UserService>(),
                NullLogger<ComposerRuntimeManager>.Instance));
            services.AddScoped(sp => new CircuitUserPrincipalAccessor(
                sp.GetRequiredService<HivePrincipalResolver>(),
                sp.GetRequiredService<HivePolicy>()));

            // THE PRODUCTION SEAM — the deployment is DERIVED FROM THE AUTH MODE, exactly as Program.cs does,
            // unless a row deliberately declares something else.
            services.AddComposerRuntimeFacades(
                declaredDeployment ?? ComposerRuntimeFacadeDeployment.ForAuthMode(
                    authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open));

            configureServices?.Invoke(services);
            Provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });

            Manager = Provider.GetRequiredService<ComposerRuntimeManager>();
        }

        /// <summary>The isolated state directory of this fixture.</summary>
        public string StateDir { get; }

        public ServiceProvider Provider { get; }

        public ComposerRuntimeManager Manager { get; }

        /// <summary>Repoints the configured primary model (before the runtime is created).</summary>
        public void SetPrimaryModel(string model) =>
            Provider.GetRequiredService<HiveConfigFile>().Composer!.Model = model;

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
                TestHelpers.ForceDeleteDirectory(StateDir);
                return Task.CompletedTask;
            });

            if (failures.Count > 0)
                throw new AggregateException("Wiring fixture cleanup failed", failures);
        }

        /// <summary>An <see cref="IDbContextFactory{TContext}"/> over the fixture's context.</summary>
        private sealed class NullDbFactory(CopilotHiveDbContext context)
            : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext() => context;
        }
    }

    /// <summary>
    /// THE ONLY CLEANUP ENTRY THE RUNNER INVOKES: xUnit v3 calls <see cref="DisposeAsync"/> for an
    /// <see cref="IAsyncLifetime"/> class and never falls back to a synchronous <c>Dispose</c>, so EVERY
    /// cleanup step — including the <c>COMPOSER_PER_USER_RUNTIME</c> restoration check — lives here (the class
    /// deliberately implements no <see cref="IDisposable"/>, so nothing can become dead code).
    /// Every step runs even when an earlier one (or the environment check) fails; failures are collected
    /// and surfaced together, never swallowed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();

        async Task Step(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { failures.Add(ex); }
        }

        // The per-test restoration proof: a test that left the gate set would leak into every other
        // class. Checked FIRST so the observed value is the one the test body left behind, and recorded
        // as a failure rather than thrown so the remaining teardown still runs.
        await Step(() =>
        {
            Assert.Equal(_gateBeforeTest,
                Environment.GetEnvironmentVariable(ComposerRuntimeFacadeDeployment.EnvironmentVariable));
            return Task.CompletedTask;
        });

        foreach (var manager in _managers)
            await Step(manager.ShutdownAsync);

        foreach (var composer in _composers)
            await Step(() => composer.DisposeAsync().AsTask());

        foreach (var provider in _providers)
            await Step(() => provider.DisposeAsync().AsTask());

        await Step(() => { _goalDb.Dispose(); return Task.CompletedTask; });
        await Step(() => { _auth.Dispose(); return Task.CompletedTask; });
        await Step(() =>
        {
            TestHelpers.ForceDeleteDirectory(_dir);
            return Task.CompletedTask;
        });

        if (failures.Count > 0)
            throw new AggregateException("Wiring test cleanup failed", failures);
    }
}
