using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE SCOPED RUNTIME ACCESSOR CONTRACT: lifetime safety (scoped, never captured by a singleton), the
/// per-operation <see cref="HiveCapability.InteractiveMember"/> re-check (a Viewer or a refusal yields NO
/// runtime), and the two identity paths — the per-request <see cref="ClaimsPrincipal"/> path (no circuit
/// initialisation required) and the optional-circuit path.
/// <para>
/// These rows run over the REAL <see cref="ComposerRuntimeManager"/>, the REAL production resolver/policy,
/// a REAL directory (SQLite over the production <see cref="UserService"/>) and a REAL per-user runtime, so
/// a claim like "a viewer gets no runtime" is observed on the object the manager actually hands out.
/// </para>
/// </summary>
public sealed class ComposerRuntimeAccessorTests : IAsyncLifetime, IDisposable
{
    private const string Model = "accessor-model";
    private const string MemberId = "accessor-member";
    private const string ViewerId = "accessor-viewer";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-accessor-{Guid.NewGuid():N}");
    private readonly ServiceProvider _provider;
    private readonly ComposerRuntimeManager _manager;
    private readonly ComposerFactory _factory;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy = new();
    private readonly List<Task> _requests = [];
    private int _memberId;
    private int _viewerId;

    public ComposerRuntimeAccessorTests()
    {
        Directory.CreateDirectory(_dir);

        var config = new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        };

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGoalStore>(new GoalStore(_goalDb, NullLogger<GoalStore>.Instance));
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton(_policy);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        _provider = services.BuildServiceProvider();
        _resolver = _provider.GetRequiredService<HivePrincipalResolver>();
        _factory = new ComposerFactory(
            _provider,
            _dir,
            ollamaApiKey: null,
            // An Ollama-style provider keeps the runtime eligible for a NON-original identity, so these rows
            // exercise the accessor (not the separate provider deferral gate).
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: () => true,
            providerOf: _ => "ollama-local");
        _manager = new ComposerRuntimeManager(_factory, _auth.Directory.Service, NullLogger<ComposerRuntimeManager>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Seeds the two real directory accounts at their real ids.</summary>
    public async ValueTask InitializeAsync()
    {
        _memberId = (await _auth.SeedAsync(MemberId, UserRole.Member)).Id;
        _viewerId = (await _auth.SeedAsync(ViewerId, UserRole.Viewer)).Id;
    }

    /// <summary>The accessor over the SAME scope's circuit accessor, exactly as the host composes it.</summary>
    private ComposerRuntimeAccessor NewAccessor() =>
        new(
            _manager,
            new CircuitUserPrincipalAccessor(_resolver, _policy),
            _resolver,
            _policy,
            NullLogger<ComposerRuntimeAccessor>.Instance);

    private Task<ComposerRuntimeAccess> Acquire(
        ComposerRuntimeAccessor accessor, string gitHubId, CancellationToken? ct = null)
    {
        var task = accessor.AcquireForRequestAsync(
            FacadeAuthorizationHarness.Principal(gitHubId), ct ?? Ct);
        _requests.Add(task);
        return task;
    }

    // ── The per-request identity path ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE REQUEST PATH NEEDS NO CIRCUIT INITIALISATION: the caller's <see cref="ClaimsPrincipal"/> is
    /// resolved directly and yields a real per-user runtime owned by the manager.
    /// </summary>
    [Fact]
    public async Task RequestPath_ResolvesTheCallerDirectly_AndReturnsTheManagersRuntime()
    {
        var accessor = NewAccessor();

        // The circuit accessor of this scope was NEVER initialised — the request path does not need it.
        var access = await Acquire(accessor, MemberId);

        Assert.True(access.IsAuthorized, "an activated Member must be authorized");
        Assert.Equal(ComposerRuntimeStatus.Connected, access.Status);
        Assert.NotNull(access.Runtime);
        Assert.Equal(_memberId, access.Runtime!.State.UserId);
        Assert.True(access.Runtime.Composer.IsConnected);
    }

    /// <summary>
    /// A VIEWER IS REFUSED WITH NO RUNTIME, and the manager never creates one: the SAME accessor asked for a
    /// Member afterwards still works, so the refusal is about the caller, not about the accessor's state.
    /// </summary>
    [Fact]
    public async Task Viewer_IsRefused_WithNoRuntime_AndNoRuntimeIsCreatedForThem()
    {
        var accessor = NewAccessor();

        var denied = await Acquire(accessor, ViewerId);
        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Runtime);
        Assert.Null(denied.Result);
        Assert.Equal(ComposerRuntimeStatus.Refused, denied.Status);

        // The viewer's identity never reached the manager: no entry exists for that account.
        Assert.DoesNotContain(ManagerEntries(), entry => Equals(entry.Key, _viewerId));

        // Control: the very same accessor still serves an authorized Member.
        var allowed = await Acquire(accessor, MemberId);
        Assert.True(allowed.IsAuthorized);
        Assert.NotNull(allowed.Runtime);
    }

    /// <summary>A signed-out caller is refused with no runtime (there is no anonymous principal to serve).</summary>
    [Fact]
    public async Task SignedOutCaller_IsRefused_WithNoRuntime()
    {
        var access = await NewAccessor().AcquireForRequestAsync(actingUser: null, Ct);
        _requests.Add(Task.CompletedTask);

        Assert.False(access.IsAuthorized);
        Assert.Null(access.Runtime);
    }

    /// <summary>
    /// A DEMOTION TAKES EFFECT ON THE VERY NEXT OPERATION: a Member admitted first, demoted to Viewer, and
    /// then refused — and the refusal carries NO runtime, even though one is already cached for the account.
    /// </summary>
    [Fact]
    public async Task DemotedBetweenTwoOperations_IsRefusedOnTheSecond_WithNoRuntime()
    {
        var accessor = NewAccessor();

        var admitted = await Acquire(accessor, MemberId);
        Assert.True(admitted.IsAuthorized);
        Assert.NotNull(admitted.Runtime);

        await _auth.SetRoleAsync(MemberId, UserRole.Viewer);

        var denied = await Acquire(accessor, MemberId);
        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Runtime);

        // Restore the stored role so the remaining rows (and cleanup) see the seeded state.
        await _auth.SetRoleAsync(MemberId, UserRole.Member);
    }

    /// <summary>
    /// A DISABLED ACCOUNT IS REFUSED ONCE ITS STATE IS RE-READ, with no runtime and no content.
    /// </summary>
    [Fact]
    public async Task DisabledAccount_IsRefused_WithNoRuntime()
    {
        var accessor = NewAccessor();
        Assert.True((await Acquire(accessor, MemberId)).IsAuthorized);

        await _auth.SetEnabledAsync(MemberId, enabled: false);

        var denied = await Acquire(accessor, MemberId);
        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Runtime);

        await _auth.SetEnabledAsync(MemberId, enabled: true);
    }

    // ── The optional circuit path ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CIRCUIT PATH INITIALISES THE SCOPED ACCESSOR FROM THE CASCADING STATE (once) and serves the same
    /// contract: an authorized circuit user gets the manager's runtime.
    /// </summary>
    [Fact]
    public async Task CircuitPath_InitialisesTheScopedAccessor_AndServesTheCircuitUser()
    {
        var circuitAccessor = new CircuitUserPrincipalAccessor(_resolver, _policy);
        var accessor = new ComposerRuntimeAccessor(
            _manager, circuitAccessor, _resolver, _policy, NullLogger<ComposerRuntimeAccessor>.Instance);

        Assert.False(circuitAccessor.IsInitialized);

        var state = AuthenticatedState(FacadeAuthorizationHarness.Principal(MemberId));
        var access = await accessor.AcquireForCircuitAsync(state, Ct);
        _requests.Add(Task.CompletedTask);

        Assert.True(circuitAccessor.IsInitialized, "the circuit accessor must be initialised from the cascade");
        Assert.True(access.IsAuthorized);
        Assert.NotNull(access.Runtime);
        Assert.Equal(_memberId, access.Runtime!.State.UserId);
    }

    /// <summary>
    /// A CIRCUIT VIEWER IS REFUSED WITH NO RUNTIME — the circuit path applies the SAME capability rule.
    /// </summary>
    [Fact]
    public async Task CircuitPath_RefusesAViewer_WithNoRuntime()
    {
        var accessor = NewAccessor();

        var access = await accessor.AcquireForCircuitAsync(
            AuthenticatedState(FacadeAuthorizationHarness.Principal(ViewerId)), Ct);
        _requests.Add(Task.CompletedTask);

        Assert.False(access.IsAuthorized);
        Assert.Null(access.Runtime);
    }

    /// <summary>
    /// A CIRCUIT WITH NO CASCADE (open mode) IS NOT SEEDED: the accessor stays un-initialised and the
    /// resolution fails closed while authentication is enabled — no runtime, and nothing fabricated.
    /// </summary>
    [Fact]
    public async Task CircuitPath_WithNoCascade_StaysUninitialised_AndFailsClosed()
    {
        var circuitAccessor = new CircuitUserPrincipalAccessor(_resolver, _policy);
        var accessor = new ComposerRuntimeAccessor(
            _manager, circuitAccessor, _resolver, _policy, NullLogger<ComposerRuntimeAccessor>.Instance);

        var access = await accessor.AcquireForCircuitAsync(authenticationState: null, Ct);
        _requests.Add(Task.CompletedTask);

        Assert.False(circuitAccessor.IsInitialized, "an absent cascade must never seed a fabricated identity");
        Assert.False(access.IsAuthorized);
        Assert.Null(access.Runtime);
    }

    // ── Lifetime / singleton safety ───────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ACCESSOR IS SCOPED AND NEVER CAPTURED BY A SINGLETON: the type holds the SINGLETON manager plus the
    /// SCOPED circuit accessor, and nothing in production registers it as a singleton or resolves it into a
    /// startup closure.
    /// </summary>
    [Fact]
    public void Accessor_IsScoped_AndNoSingletonRegistrationClosesOverIt()
    {
        var repoRoot = TestRepoRoot.Find();
        var program = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(repoRoot, "src", "CopilotHive", "Program.cs"))
                .ReplaceLineEndings("\n"));
        var registration = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(repoRoot, "src", "CopilotHive", "Services", "ComposerRuntimeFacadeDeployment.cs"))
                .ReplaceLineEndings("\n"));

        // No SINGLETON registration statement mentions either scoped type.
        foreach (var source in new[] { program, registration })
        {
            foreach (var statement in StatementsContaining(source, "AddSingleton"))
            {
                Assert.DoesNotContain("ComposerRuntimeAccessor", statement, StringComparison.Ordinal);
                Assert.DoesNotContain("CircuitUserPrincipalAccessor", statement, StringComparison.Ordinal);
            }
        }

        // The accessor's own registration is SCOPED, exactly once.
        var accessorRegistration = Assert.Single(
            StatementsContaining(registration, "new ComposerRuntimeAccessor("));
        Assert.Contains("AddScoped", accessorRegistration, StringComparison.Ordinal);

        // NO STARTUP CAPTURE: Program never resolves the scoped accessor (or the scoped facade) from
        // app.Services, which would capture a scope on the root provider for the process lifetime.
        Assert.DoesNotContain("GetRequiredService<ComposerRuntimeAccessor>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("GetService<ComposerRuntimeAccessor>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("app.Services.GetRequiredService<IComposerFacade>", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ACCESSOR AND THE CIRCUIT ACCESSOR ARE BOTH SCOPED AND DISTINCT PER SCOPE, while the manager is the
    /// SINGLETON every scope shares.
    /// </summary>
    [Fact]
    public void AccessorAndCircuitAccessor_AreScoped_TheManagerIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_manager);
        services.AddSingleton(_resolver);
        services.AddSingleton(_policy);
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));
        services.AddScoped(sp => new ComposerRuntimeAccessor(
            sp.GetRequiredService<ComposerRuntimeManager>(),
            sp.GetRequiredService<CircuitUserPrincipalAccessor>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<ComposerRuntimeAccessor>.Instance));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        // Resolving from the ROOT provider is refused (scoped validation), which is exactly why production
        // must never do it — the guard is demonstrated, not assumed.
        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ComposerRuntimeAccessor>());

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            second.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>(),
            second.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>());

        // ...and one accessor per scope, with the SAME singleton manager behind it.
        Assert.Same(
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(),
            first.ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>());
        Assert.Same(_manager, provider.GetRequiredService<ComposerRuntimeManager>());
    }

    /// <summary>
    /// THE ACCESSOR NEVER HOLDS A PRINCIPAL OR A CREDENTIAL-BEARING ENTITY: it is safe to keep per scope and
    /// cannot replay a stale identity.
    /// </summary>
    [Fact]
    public void Accessor_HoldsNoPrincipalAndNoCredentialEntity()
    {
        var fieldTypes = typeof(ComposerRuntimeAccessor)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(ActingUserPrincipal), fieldTypes);
        Assert.DoesNotContain(typeof(System.Security.Claims.ClaimsPrincipal), fieldTypes);
        Assert.DoesNotContain(typeof(CopilotHive.Persistence.Entities.UserEntity), fieldTypes);
        // It DOES hold the singleton manager and the scoped circuit accessor (that is its job).
        Assert.Contains(typeof(ComposerRuntimeManager), fieldTypes);
        Assert.Contains(typeof(CircuitUserPrincipalAccessor), fieldTypes);
        // ...and never an HttpContext.
        Assert.DoesNotContain(typeof(Microsoft.AspNetCore.Http.HttpContext), fieldTypes);
    }

    /// <summary>The manager's cache, read for the "no runtime was created" assertions.</summary>
    private Dictionary<int, object> ManagerEntries()
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(_manager)!;
        return entries.Keys.Cast<int>().ToDictionary(id => id, id => entries[id]!);
    }

    private static Task<AuthenticationState> AuthenticatedState(ClaimsPrincipal principal) =>
        Task.FromResult(new AuthenticationState(principal));

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

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();

        async Task Step(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { failures.Add(ex); }
        }

        // Join every owned request, then shut the manager down (it is the sole disposer of runtimes).
        foreach (var request in _requests.ToArray())
            await Step(() => request.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        await Step(_manager.ShutdownAsync);
        await Step(() => _provider.DisposeAsync().AsTask());
        await Step(() => { _goalDb.Dispose(); return Task.CompletedTask; });

        if (failures.Count > 0)
            throw new AggregateException("Accessor test cleanup failed", failures);
    }

    public void Dispose()
    {
        _auth.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated state directory.
        }
    }
}
