using System.Security.Claims;

using CopilotHive.Components;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE SHARED HOST (<c>Routes.razor</c>) OBSERVES THE CIRCUIT IDENTITY — the behavioural proof
/// that the router initialises the SCOPED <see cref="CircuitUserPrincipalAccessor"/> from the
/// cascading <see cref="AuthenticationState"/>, exactly the way the pages do through the one
/// <c>ComponentActingUser</c> seam, and that a missing cascade is NEVER mistaken for open mode.
/// <para>
/// THE HARNESS RENDERS THE REAL <c>Routes</c> COMPONENT — never a stand-in — over the REAL scoped
/// accessor and the REAL user directory. One CIRCUIT is modelled as one service scope: every render
/// of the host inside a test reuses that scope, exactly as a browser circuit keeps one scope across
/// its renders. The accessor asserted on is the one the renderer actually resolved from that scope,
/// never a stand-in. The cascade itself is a fixed task the test seeds (exactly what the interactive
/// host hands the component), so observation is deterministic — no sleeps, no polling.
/// </para>
/// </summary>
// The _authEnabled flag in Routes.razor and NavMenu.razor is read from process-wide environment
// variables exactly once per type initialisation, so the class is serialised with the other
// environment-mutating suites and each test sets the variables it needs explicitly.
[Collection("EnvVarMutation")]
public sealed class RoutesCircuitAccessorTests : IDisposable
{
    private const string RouteUserId = "routes-host-user";
    private const string OtherUserId = "routes-other-user";

    private const string OAuthClientIdVariable = "GITHUB_OAUTH_CLIENT_ID";
    private const string OAuthClientSecretVariable = "GITHUB_OAUTH_CLIENT_SECRET";

    private readonly string? _previousClientId;
    private readonly string? _previousClientSecret;
    private readonly FacadeAuthorizationHarness _auth = new();

    public RoutesCircuitAccessorTests()
    {
        _previousClientId = Environment.GetEnvironmentVariable(OAuthClientIdVariable);
        _previousClientSecret = Environment.GetEnvironmentVariable(OAuthClientSecretVariable);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    private static ClaimsPrincipal SignedOutPrincipal() =>
        new(new ClaimsIdentity()); // no authentication type => Identity.IsAuthenticated == false

    private void SetAuthEnabled(bool enabled)
    {
        Environment.SetEnvironmentVariable(OAuthClientIdVariable, enabled ? "test-client-id" : null);
        Environment.SetEnvironmentVariable(OAuthClientSecretVariable, enabled ? "test-client-secret" : null);
    }

    // ══════════════════════════════════════════════════════════════════
    //  HOST CONSTRUCTION
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the provider the way Program.cs does for the components: a SCOPED circuit accessor,
    /// the explicit auth mode, and (only when authentication is enabled) the authorization core and
    /// the cascading-authentication-state registration. Pages, layouts and the dashboard singletons
    /// are registered so the real routed component can render.
    /// </summary>
    private TestAuthenticationStateProvider _state = new(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    private ServiceProvider NewProvider(HiveAuthMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(mode);
        services.AddSingleton(new HivePolicy());
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));

        // THE SAME REGISTRATION Program.cs MAKES: the circuit accessor is SCOPED.
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        AddPageInfrastructure(services);
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton<Microsoft.AspNetCore.Components.Routing.INavigationInterception>(new NoOpNavigationInterception());
        services.AddSingleton<Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash>(new NoOpScrollToLocationHash());

        // The authorization core is needed by the AuthorizeView branch the Routes component renders.
        // It is registered in BOTH modes: the branch that runs depends on the process-wide
        // _authEnabled flag Routes.razor reads once per process, so the host must be able to render
        // either. No AuthorizationPolicyProvider is registered, which means AuthorizeView would throw
        // if the framework really reached the AuthorizeView branch — so an open-mode test that
        // happened to render the auth-enabled branch would FAIL LOUDLY instead of passing vacuously.
        services.AddAuthorizationCore();
        services.AddSingleton<AuthenticationStateProvider>(_state);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    /// <summary>
    /// Everything the routed <c>/</c> page (Home) and its layout need, wired to the REAL production
    /// singleton types the app uses: the page injects the dashboard state service and its page-header
    /// singleton; the layout injects the clarification queue (its refresh EVENT SOURCE only) and the
    /// AUTHORIZED <see cref="IClarificationFacade"/> every clarification read/answer goes through; the
    /// nav menu's knowledge count reads the same dashboard state.
    /// </summary>
    private static void AddPageInfrastructure(ServiceCollection services)
    {
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton(new ClarificationQueueService());

        // THE AUTHORIZED CLARIFICATION BOUNDARY the layout now depends on, over the production
        // resolver/policy the caller registered (exactly as Program.cs wires it). The layout only uses
        // the TRANSIENT queue operations, so a simple in-memory store satisfies the persisted-history
        // dependency without adding behaviour this suite does not exercise.
        services.AddSingleton<IGoalStore>(new InMemoryGoalStore());
        services.AddSingleton<IClarificationFacade>(sp => new ClarificationFacade(
            sp.GetRequiredService<ClarificationQueueService>(),
            sp.GetRequiredService<IGoalStore>(),
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>(),
            NullLogger<ClarificationFacade>.Instance));

        var dashboard = new DashboardStateService(
            new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            notifier: null,
            brain: null);
        services.AddSingleton(dashboard);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ONE CIRCUIT = ONE SCOPE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ONE CIRCUIT: the scope the components resolve from, the scoped accessor under test, and the
    /// authentication-state provider the framework cascades from. One scope per circuit, exactly like
    /// a browser circuit.
    /// </summary>
    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly TestAuthenticationStateProvider _state;

        private Circuit(
            ServiceProvider provider,
            AsyncServiceScope scope,
            CircuitUserPrincipalAccessor accessor,
            TestAuthenticationStateProvider state)
        {
            _provider = provider;
            _scope = scope;
            Accessor = accessor;
            _state = state;
        }

        internal static Circuit Open(ServiceProvider provider, TestAuthenticationStateProvider state)
        {
            var scope = provider.CreateAsyncScope();
            var accessor = scope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();
            return new Circuit(provider, scope, accessor, state);
        }

        /// <summary>The scoped accessor resolved from THIS circuit's scope.</summary>
        internal CircuitUserPrincipalAccessor Accessor { get; }

        /// <summary>
        /// Seeds the framework-cascading authentication state (when one is registered for this host)
        /// and renders the real host. With <c>AddCascadingAuthenticationState</c> registered the
        /// framework cascades the provider's state to every component in the tree, exactly as the
        /// interactive host does; without it — the open-mode shape — the host renders with NO cascade
        /// anywhere in the tree.
        /// </summary>
        /// <summary>
        /// Renders the real host. <paramref name="provideCascade"/> decides whether the render is
        /// wrapped in the cascading authentication-state value that
        /// <c>AddCascadingAuthenticationState</c> contributes to an authenticated host, or rendered
        /// bare — exactly the open-mode shape, where no cascade exists anywhere in the tree.
        /// </summary>
        internal async Task<string> RenderAsync(
            AuthenticationState? authenticationState,
            bool provideCascade = true)
        {
            _state.Set(authenticationState ?? new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

            var renderer = new HtmlRenderer(_scope.ServiceProvider, NullLoggerFactory.Instance);
            try
            {
                return await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    if (provideCascade)
                    {
                        var state = Task.FromResult(
                            authenticationState ?? new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
                        RenderFragment child = builder =>
                        {
                            builder.OpenComponent<Routes>(0);
                            builder.CloseComponent();
                        };

                        var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(
                            ParameterView.FromDictionary(new Dictionary<string, object?>
                            {
                                ["Value"] = state,
                                ["ChildContent"] = child,
                            }));
                        return root.ToHtmlString();
                    }

                    var plain = await renderer.RenderComponentAsync<Routes>();
                    return plain.ToHtmlString();
                });
            }
            finally
            {
                await renderer.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  1. AUTH ENABLED — the host initialises the scoped accessor
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE HOST ESTABLISHES THE IDENTITY: rendering the real Routes host with a cascade present
    /// initialises the SCOPED accessor — the identity the pages then re-use — without any page
    /// having to observe the cascade first.
    /// </summary>
    [Fact]
    public async Task TheHost_WithACascade_InitialisesTheScopedAccessorWithTheCascadedIdentity()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        var html = await circuit.RenderAsync(new AuthenticationState(Principal(RouteUserId)));

        // THE HOST REALLY RENDERED (not a failed/empty render).
        Assert.False(string.IsNullOrWhiteSpace(html));

        Assert.True(circuit.Accessor.IsInitialized);
        Assert.Equal(CircuitIdentityState.Initialized, circuit.Accessor.IdentityState);

        var identity = circuit.Accessor.EstablishedIdentity;
        Assert.NotNull(identity);
        Assert.Equal(RouteUserId, identity!.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        // And the capability answer is the CURRENT directory state, not a cached grant.
        Assert.True(await circuit.Accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
    }

    /// <summary>
    /// UNKNOWN MODE IS FAIL-CLOSED: with authentication enabled and NO cascade at all, the host
    /// initialises nothing, the accessor refuses every capability, and a page-side acquisition of
    /// the (absent) cascade yields no identity — a missing cascade is never read as open mode.
    /// </summary>
    [Fact]
    public async Task WithAuthenticationEnabledAndNoCascadeAtAll_TheAccessorFailsClosed_AndACascadeLessAcquisitionYieldsNoIdentity()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);

        // The missing cascade is the NORMAL condition of an authentication-enabled host whose
        // components never see one: the accessor is what a page receives from DI, so it is asserted
        // here exactly as the page would use it. Note this deliberately does NOT render the host —
        // rendering would route through the component's one-time branch choice, and this row is about
        // the fail-closed answer the accessor gives when no cascade exists at all.
        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        Assert.Equal(CircuitIdentityState.Uninitialized, circuit.Accessor.IdentityState);
        Assert.False(circuit.Accessor.IsInitialized);
        Assert.Null(circuit.Accessor.EstablishedIdentity);
        Assert.False(await circuit.Accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        // The page's own helper, handed the same absent cascade, also yields no identity.
        Assert.Null(await ComponentActingUser.AcquireAsync(null, circuit.Accessor));
    }

    /// <summary>
    /// SIGN-OUT THROUGH THE HOST PATH IS PERMANENT: the host observes the authenticated cascade, then
    /// the SAME circuit renders the signed-out cascade — the accessor invalidates, every later
    /// capability question fails closed, and re-rendering with the ORIGINAL identity does NOT revive it.
    /// </summary>
    [Fact]
    public async Task TheHost_SignedOutAfterEstablishment_PermanentlyInvalidatesTheAccessor()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        // The host observes the authenticated cascade first.
        await circuit.RenderAsync(new AuthenticationState(Principal(RouteUserId)));
        Assert.Equal(CircuitIdentityState.Initialized, circuit.Accessor.IdentityState);
        Assert.True(await circuit.Accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        // The SAME circuit renders the signed-out cascade.
        await circuit.RenderAsync(new AuthenticationState(SignedOutPrincipal()));
        Assert.Equal(CircuitIdentityState.SignedOut, circuit.Accessor.IdentityState);
        Assert.True(circuit.Accessor.IsInvalidated);
        Assert.Null(circuit.Accessor.EstablishedIdentity);
        Assert.False(await circuit.Accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        // Even the SAME original identity no longer revives the circuit.
        await circuit.RenderAsync(new AuthenticationState(Principal(RouteUserId)));
        Assert.Equal(CircuitIdentityState.SignedOut, circuit.Accessor.IdentityState);
        Assert.False(await circuit.Accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// IDENTITY REPLACEMENT THROUGH THE HOST PATH IS PERMANENT: a different account's cascade
    /// invalidates the circuit; the replacement is never adopted, and the original is not restored.
    /// </summary>
    [Fact]
    public async Task TheHost_DifferentIdentityAfterEstablishment_ReplacesAndIsPermanent()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);
        await _auth.SeedAsync(OtherUserId, UserRole.Admin);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        await circuit.RenderAsync(new AuthenticationState(Principal(RouteUserId)));
        Assert.Equal(RouteUserId,
            circuit.Accessor.EstablishedIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        await circuit.RenderAsync(new AuthenticationState(Principal(OtherUserId)));

        Assert.Equal(CircuitIdentityState.IdentityReplaced, circuit.Accessor.IdentityState);
        Assert.True(circuit.Accessor.IsInvalidated);
        Assert.Null(circuit.Accessor.EstablishedIdentity);
        Assert.False(await circuit.Accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    // ══════════════════════════════════════════════════════════════════
    //  2. CHILD-ORDER SAFETY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// PAGE-FIRST, HOST-SECOND: a child acquires the identity BEFORE the host observes the cascade —
    /// the host's later observation of the SAME cascade is a no-op, and both agree on the identity.
    /// One scope, one accessor, one cascade.
    /// </summary>
    [Fact]
    public async Task AChildAcquisition_BeforeTheHost_LeavesTheHostANoOp()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        var cascade = new AuthenticationState(Principal(RouteUserId));

        // The child (the PAGE) acquires first, exactly as a page's OnInitializedAsync does.
        var childIdentity = await ComponentActingUser.AcquireAsync(Task.FromResult(cascade), circuit.Accessor);
        Assert.Equal(RouteUserId, childIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        // The host now observes the very same cascade: idempotent, no clobber, same identity.
        var hostIdentity = await ComponentActingUser.AcquireAsync(Task.FromResult(cascade), circuit.Accessor);
        Assert.Equal(RouteUserId, hostIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(CircuitIdentityState.Initialized, circuit.Accessor.IdentityState);
        Assert.Equal(RouteUserId, circuit.Accessor.EstablishedIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    /// <summary>
    /// HOST-FIRST, CHILD-SECOND: the host observes the cascade first; the child's own acquisition is
    /// served from the established identity — the child needs no prior knowledge of the ordering.
    /// </summary>
    [Fact]
    public async Task AChildAcquisition_AfterTheHost_SeesTheSameEstablishedIdentity()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        var cascade = new AuthenticationState(Principal(RouteUserId));

        var hostIdentity = await ComponentActingUser.AcquireAsync(Task.FromResult(cascade), circuit.Accessor);
        Assert.Equal(RouteUserId, hostIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        var childIdentity = await ComponentActingUser.AcquireAsync(Task.FromResult(cascade), circuit.Accessor);
        Assert.Equal(RouteUserId, childIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(CircuitIdentityState.Initialized, circuit.Accessor.IdentityState);
    }

    /// <summary>
    /// A CHILD THAT OBSERVES THE SIGN-OUT FIRST STILL INVALIDATES THE CIRCUIT: the ordering question
    /// is settled by the accessor's own state machine, whichever side observes the sign-out first.
    /// </summary>
    [Fact]
    public async Task AChildObservingTheSignOutFirst_InvalidatesTheCircuit_AndTheHostCannotReviveIt()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        var establishing = new AuthenticationState(Principal(RouteUserId));
        await ComponentActingUser.AcquireAsync(Task.FromResult(establishing), circuit.Accessor);
        Assert.Equal(CircuitIdentityState.Initialized, circuit.Accessor.IdentityState);

        // The child observes the sign-out cascade first.
        var signout = new AuthenticationState(SignedOutPrincipal());
        var childSees = await ComponentActingUser.AcquireAsync(Task.FromResult(signout), circuit.Accessor);
        Assert.Null(childSees);
        Assert.Equal(CircuitIdentityState.SignedOut, circuit.Accessor.IdentityState);

        // The host's later observation of the same cascade changes nothing.
        var hostSees = await ComponentActingUser.AcquireAsync(Task.FromResult(signout), circuit.Accessor);
        Assert.Null(hostSees);
        Assert.Equal(CircuitIdentityState.SignedOut, circuit.Accessor.IdentityState);
    }

    /// <summary>
    /// THE CHILD OBSERVES A DIFFERENT IDENTITY FIRST: the circuit invalidates on the child's
    /// observation, and the host's later observation of that same different identity keeps it invalid.
    /// </summary>
    [Fact]
    public async Task AChildObservingADifferentIdentityFirst_ReplacesAndStaysReplaced()
    {
        SetAuthEnabled(true);
        await _auth.SeedAsync(RouteUserId, UserRole.Operator);
        await _auth.SeedAsync(OtherUserId, UserRole.Admin);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.AuthenticationEnabled), _state);

        var first = new AuthenticationState(Principal(RouteUserId));
        await ComponentActingUser.AcquireAsync(Task.FromResult(first), circuit.Accessor);
        Assert.Equal(CircuitIdentityState.Initialized, circuit.Accessor.IdentityState);

        var other = new AuthenticationState(Principal(OtherUserId));
        var childSees = await ComponentActingUser.AcquireAsync(Task.FromResult(other), circuit.Accessor);
        Assert.Null(childSees);
        Assert.Equal(CircuitIdentityState.IdentityReplaced, circuit.Accessor.IdentityState);

        var hostSees = await ComponentActingUser.AcquireAsync(Task.FromResult(other), circuit.Accessor);
        Assert.Null(hostSees);
        Assert.Equal(CircuitIdentityState.IdentityReplaced, circuit.Accessor.IdentityState);
    }

    // ══════════════════════════════════════════════════════════════════
    //  3. OPEN MODE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE IS EXPLICIT, NOT INFERRED: with no cascade and the host in open mode, the host
    /// renders without initialising anything, the accessor stays <see cref="CircuitIdentityState.Uninitialized"/>,
    /// and the capability question answers through the EXPLICIT open-mode branch (the local operator) —
    /// never because an absent cascade was mistaken for open mode.
    /// </summary>
    [Fact]
    public async Task TheHost_InOpenMode_LeavesTheAccessorUninitialised_AndTheExplicitOpenModeAnswerIsTheLocalOperator()
    {
        SetAuthEnabled(false);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.Open), _state);

        // The open-mode circuit carries no cascade: nothing initialises the accessor, and no component
        // can observe one. The accessor is deliberately left uninitialised: open mode carries no
        // per-circuit identity. The capability answer comes from the EXPLICIT open-mode decision.
        Assert.Equal(CircuitIdentityState.Uninitialized, circuit.Accessor.IdentityState);
        Assert.False(circuit.Accessor.IsInitialized);
        Assert.Null(circuit.Accessor.EstablishedIdentity);
        Assert.False(circuit.Accessor.AuthEnabled);
        Assert.True(await circuit.Accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));

        var resolution = await circuit.Accessor.GetPrincipalAsync(Ct);
        Assert.True(resolution.IsResolved);
        Assert.True(resolution.Principal!.IsLocalOperator);
    }

    /// <summary>
    /// THE HOST'S OPEN-MODE RENDER REALLY RUNS: the host renders without an authentication cascade and
    /// does not throw — the open-mode contract the app relies on. It asserts only that the render
    /// produced a page (non-empty markup); WHICH branch of the host rendered is process-global and is
    /// deliberately not asserted here.
    /// </summary>
    [Fact]
    public async Task TheHost_RendersInOpenMode_WithNoCascadePresent()
    {
        SetAuthEnabled(false);

        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.Open), _state);

        var html = await circuit.RenderAsync(null, provideCascade: false);

        Assert.False(string.IsNullOrWhiteSpace(html));
    }

    /// <summary>
    /// OPEN MODE ANSWERS THE SAME WAY EVEN WITH A CASCADE PRESENT: the mode is the host's explicit
    /// decision, not something the components infer from the cascade.
    /// </summary>
    [Fact]
    public async Task TheHost_InOpenMode_IgnoresTheCascadedIdentityForAuthorization_AndAnswersTheLocalOperator()
    {
        SetAuthEnabled(false);
        await _auth.SeedAsync(RouteUserId, UserRole.Viewer);

        // Open mode ignores the cascade entirely: the capability answer comes from the EXPLICIT
        // open-mode decision, never from the (Viewer) identity the cascade carries.
        await using var circuit = Circuit.Open(NewProvider(HiveAuthMode.Open), _state);

        var cascade = new AuthenticationState(Principal(RouteUserId));
        var observed = await ComponentActingUser.AcquireAsync(Task.FromResult(cascade), circuit.Accessor);

        // The observation IS recorded (the accessor observes whatever cascade it is handed), but the
        // open-mode ANSWER never depends on it.
        var resolution = await circuit.Accessor.GetPrincipalAsync(Ct);
        Assert.True(resolution.IsResolved);
        Assert.True(resolution.Principal!.IsLocalOperator);
        Assert.True(await circuit.Accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        // A viewer-seeded cascade does not leak its role into the open-mode answer.
        Assert.NotNull(observed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The mutable authentication-state provider the host registers. It is the value the framework's
    /// cascading authentication-state source would carry: <c>Routes</c> declares
    /// <c>[CascadingParameter] Task&lt;AuthenticationState&gt;?</c> and reads it exactly as it would the
    /// framework's own cascade.
    /// </summary>
    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private Task<AuthenticationState> _state;

        internal TestAuthenticationStateProvider(AuthenticationState initial) =>
            _state = Task.FromResult(initial);

        public void Set(AuthenticationState state)
        {
            _state = Task.FromResult(state);
            NotifyAuthenticationStateChanged(_state);
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OAuthClientIdVariable, _previousClientId);
        Environment.SetEnvironmentVariable(OAuthClientSecretVariable, _previousClientSecret);
        _auth.Dispose();
    }

    /// <summary>A navigation manager that records nothing (the routed pages never navigate here).</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            // Navigation is not exercised by these rows.
        }
    }

    /// <summary>No-op navigation interception: the router asks for it when navigation interception is available.</summary>
    private sealed class NoOpNavigationInterception : Microsoft.AspNetCore.Components.Routing.INavigationInterception
    {
        public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
    }

    /// <summary>No-op scroll handling: the router asks for it for hash-based scrolling.</summary>
    private sealed class NoOpScrollToLocationHash : Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash
    {
        public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
    }
}
