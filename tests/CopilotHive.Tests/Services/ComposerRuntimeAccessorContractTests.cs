using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
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
/// THE ACCESSOR CONTRACT: the circuit identity is observed through the shared
/// <see cref="ComponentActingUser.AcquireAsync"/> helper and never re-adopted, the
/// <see cref="HiveCapability.InteractiveMember"/> check is RE-RUN on every operation (including the
/// connect path), and the accessor never owns — let alone disposes — a runtime.
/// <para>
/// Every row runs over the REAL <see cref="ComposerRuntimeManager"/>, the REAL production
/// resolver/policy and a REAL directory, so "a viewer gets no runtime" is observed on the object
/// the manager actually hands out.
/// </para>
/// </summary>
public sealed class ComposerRuntimeAccessorContractTests : IAsyncLifetime
{
    private const string Model = "accessor-contract-model";
    private const string OriginalId = "accessor-original";
    private const string MemberId = "accessor-member";
    private const string ViewerId = "accessor-viewer";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-accessor-contract-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<ComposerRuntimeManager> _managers = [];
    private readonly List<Task> _requests = [];

    private HivePrincipalResolver _resolver = null!;
    private HivePolicy _policy = null!;
    private int _originalId;
    private int _memberId;
    private int _viewerId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ComposerRuntimeAccessorContractTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public async ValueTask InitializeAsync()
    {
        // The ORIGINAL account is seeded FIRST (lowest id) so the identity-keyed behaviour is
        // observable; the second account is an ordinary member.
        _originalId = (await _auth.SeedAsync(OriginalId, UserRole.Member)).Id;
        _memberId = (await _auth.SeedAsync(MemberId, UserRole.Member)).Id;
        _viewerId = (await _auth.SeedAsync(ViewerId, UserRole.Viewer)).Id;
    }

    // ── The circuit identity: observed, permanently invalidated on replacement ───────────────

    /// <summary>
    /// A LATER, DIFFERENT IDENTITY IS NEVER ADOPTED AND INVALIDATES THE CIRCUIT: the second circuit
    /// operation carries a DIFFERENT cascading identity (B) and is denied with no runtime, and so is
    /// every later operation — even one carrying the original identity A again.
    /// </summary>
    [Fact]
    public async Task CircuitIdentity_ALaterIdentityIsNeverAdopted_AndPermanentlyInvalidates()
    {
        var (manager, accessor) = Build(officialProvider: "ollama-local");

        var first = await Acquire(accessor, CircuitState(OriginalId));
        Assert.True(first.IsAuthorized, "the seeded original account must be authorized");
        Assert.NotNull(first.Runtime);
        Assert.Equal(_originalId, first.Runtime!.State.UserId);

        // The SAME identity again is idempotent and served the same shared runtime.
        var same = await Acquire(accessor, CircuitState(OriginalId));
        Assert.Same(first.Runtime, same.Runtime);

        // A DIFFERENT identity arrives: denied, no runtime, nothing adopted.
        var second = await Acquire(accessor, CircuitState(MemberId));
        Assert.False(second.IsAuthorized);
        Assert.Null(second.Runtime);
        Assert.DoesNotContain(_memberId, ManagerEntries(manager));

        // Permanent: A is not restored, on acquire OR connect, and a missing cascade does not help.
        foreach (var state in new Task<AuthenticationState>?[] { CircuitState(OriginalId), CircuitState(MemberId), null })
        {
            var acquire = await Acquire(accessor, state);
            Assert.False(acquire.IsAuthorized);
            Assert.Null(acquire.Runtime);
            Assert.Null(acquire.Result);
            var connect = await ConnectCircuit(accessor, state);
            Assert.False(connect.IsAuthorized);
            Assert.Null(connect.Runtime);
            Assert.Null(connect.Result);
        }
    }

    /// <summary>
    /// THE TRANSITIONS THAT INVALIDATE THE CIRCUIT deny runtime acquisition AND connection afterwards
    /// (no principal, no manager outcome): A→sign-out→missing, A→sign-out→A, A→sign-out→B,
    /// A→B→missing and A→B→A.
    /// </summary>
    [Theory]
    [InlineData("A>signout>missing")]
    [InlineData("A>signout>A")]
    [InlineData("A>signout>B")]
    [InlineData("A>B>missing")]
    [InlineData("A>B>A")]
    public async Task CircuitIdentity_AfterInvalidation_RuntimeAcquisitionAndConnectionDeny(string transition)
    {
        var circuit = NewCircuitAccessor();
        var (manager, accessor) = Build(officialProvider: "ollama-local", circuit);
        var steps = transition.Split('>');

        Task<AuthenticationState>? StepState(string step) => step switch
        {
            "A" => CircuitState(OriginalId),
            "B" => CircuitState(MemberId),
            "signout" => Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Anonymous())),
            "missing" => null,
            _ => throw new InvalidOperationException(step),
        };

        var first = await Acquire(accessor, StepState(steps[0]));
        Assert.True(first.IsAuthorized);
        Assert.Equal(_originalId, first.Runtime!.State.UserId);
        var connected = await ConnectCircuit(accessor, StepState(steps[0]));
        Assert.True(connected.IsAuthorized);
        Assert.Same(first.Runtime, connected.Runtime);
        Assert.True(connected.Runtime!.Composer.IsConnected);
        Assert.True(await ComponentActingUser.IsAllowedAsync(StepState("A"), circuit, HiveCapability.InteractiveMember, Ct));
        Assert.Equal(OriginalId, (await circuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);

        // The middle step is the observed invalidation itself: already denied.
        Assert.False((await Acquire(accessor, StepState(steps[1]))).IsAuthorized);
        var terminal = steps[1] == "signout" ? CircuitIdentityState.SignedOut : CircuitIdentityState.IdentityReplaced;
        Assert.Equal(terminal, circuit.IdentityState);

        // Every surface uses this SAME circuit, and every row pins stale A AND replacement B replay.
        foreach (var step in new[] { steps[2], "A", "B", "missing" })
        {
            Assert.Null(await ComponentActingUser.AcquireAsync(StepState(step), circuit));
            foreach (var capability in Enum.GetValues<HiveCapability>())
            {
                Assert.False(await ComponentActingUser.IsAllowedAsync(StepState(step), circuit, capability, Ct));
                Assert.False(await circuit.IsAllowedAsync(capability, Ct));
            }
            var principal = await circuit.GetPrincipalAsync(Ct);
            Assert.Null(principal.Principal);
            Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, principal.RefusalReason);
            AssertDenied(await Acquire(accessor, StepState(step)));
            AssertDenied(await ConnectCircuit(accessor, StepState(step)));
            Assert.Equal(terminal, circuit.IdentityState);
            Assert.Null(circuit.EstablishedIdentity);
            Assert.Equal(new[] { _originalId }, ManagerEntries(manager));
        }
    }

    /// <summary>
    /// A MISSING CASCADE NEVER INITIALISES: with authentication enabled and no identity ever
    /// established, the circuit path denies (no runtime) and the accessor stays uninitialised; a later
    /// cascade still establishes normally (child-before-parent ordering is safe).
    /// </summary>
    [Fact]
    public async Task CircuitPath_MissingCascadeWithNoEstablishedIdentity_Denies_AndNeverInitialises()
    {
        var circuit = NewCircuitAccessor();
        var (manager, accessor) = Build(officialProvider: "ollama-local", circuit);

        var denied = await Acquire(accessor, (Task<AuthenticationState>?)null);
        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Runtime);
        Assert.Equal(CircuitIdentityState.Uninitialized, circuit.IdentityState);
        Assert.Empty(ManagerEntries(manager));

        var parent = await Acquire(accessor, CircuitState(OriginalId));
        Assert.True(parent.IsAuthorized);
    }

    /// <summary>
    /// AN ESTABLISHED IDENTITY MAY SERVE A CASCADE-LESS ACQUISITION only without an observed
    /// invalidation and only while a FRESH directory resolution still succeeds: a demotion/disablement
    /// still denies, and a sign-out denies.
    /// </summary>
    [Fact]
    public async Task CircuitPath_EstablishedIdentityServesACascadeLessAcquisition_OnlyWhileFreshAndNotInvalidated()
    {
        var (_, accessor) = Build(officialProvider: "ollama-local");

        var first = await Acquire(accessor, CircuitState(OriginalId));
        var cascadeLess = await Acquire(accessor, (Task<AuthenticationState>?)null);
        Assert.True(cascadeLess.IsAuthorized);
        Assert.Same(first.Runtime, cascadeLess.Runtime);

        // Fresh resolution still governs: a demoted account is refused on the cascade-less call.
        await _auth.SetRoleAsync(OriginalId, UserRole.Viewer);
        Assert.False((await Acquire(accessor, (Task<AuthenticationState>?)null)).IsAuthorized);
        await _auth.SetRoleAsync(OriginalId, UserRole.Member);
        Assert.True((await Acquire(accessor, (Task<AuthenticationState>?)null)).IsAuthorized);

        // Sign-out is observed: the cascade-less call no longer rides on the established identity.
        var signedOut = Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Anonymous()));
        Assert.False((await Acquire(accessor, signedOut)).IsAuthorized);
        Assert.False((await Acquire(accessor, (Task<AuthenticationState>?)null)).IsAuthorized);
    }

    /// <summary>
    /// OUT-OF-ORDER GUARD AT THE RUNTIME LEVEL: an acquisition gated on a cascading state for A resumes
    /// AFTER a sign-out was observed and must be denied — no runtime, no restored identity. The gate is a
    /// <see cref="TaskCompletionSource{TResult}"/> released only after the sign-out (no sleeps).
    /// </summary>
    [Fact]
    public async Task CircuitPath_AStaleAcquisitionResumingAfterSignOut_IsDenied()
    {
        var circuit = NewCircuitAccessor();
        var (manager, accessor) = Build(officialProvider: "ollama-local", circuit);

        var gate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = Acquire(accessor, gate.Task);
        try
        {
            Assert.False(stale.IsCompleted);
            // Another call establishes A and then observes the sign-out while the stale task is parked.
            Assert.True((await Acquire(accessor, CircuitState(OriginalId))).IsAuthorized);
            var signOut = Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Anonymous()));
            AssertDenied(await Acquire(accessor, signOut));
            Assert.Equal(CircuitIdentityState.SignedOut, circuit.IdentityState);

            gate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            AssertDenied(await stale.WaitAsync(Ct));
            Assert.Equal(CircuitIdentityState.SignedOut, circuit.IdentityState);
            Assert.Null(circuit.EstablishedIdentity);
            Assert.Null(await ComponentActingUser.AcquireAsync(CircuitState(OriginalId), circuit));
            Assert.Null((await circuit.GetPrincipalAsync(Ct)).Principal);
            Assert.False(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
            Assert.False(await ComponentActingUser.IsAllowedAsync(CircuitState(OriginalId), circuit, HiveCapability.InteractiveMember, Ct));
            AssertDenied(await Acquire(accessor, (Task<AuthenticationState>?)null));
            AssertDenied(await ConnectCircuit(accessor, CircuitState(OriginalId)));
            Assert.Equal(new[] { _originalId }, ManagerEntries(manager)); // only pre-sign-out runtime exists
        }
        finally
        {
            gate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            await stale;
        }
    }

    /// <summary>
    /// THE INITIALISATION USES THE SHARED HELPER, so its "seed once" rule is the one that applies:
    /// the source of the circuit path must call <see cref="ComponentActingUser.AcquireAsync"/>.
    /// </summary>
    [Fact]
    public void CircuitPath_InitialisesThroughTheSharedAcquireHelper()
    {
        var source = TestSourceScanner.StripNonCode(File.ReadAllText(
            Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Services", "ComposerRuntimeAccessor.cs"))
            .ReplaceLineEndings("\n"));

        Assert.Contains("ComponentActingUser.AcquireAsync(", source, StringComparison.Ordinal);
        // ...and the accessor never seeds the accessor ITSELF (no second initialisation path).
        Assert.DoesNotContain("_circuitAccessor.Initialize(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE POLICY IS RE-READ ON EVERY CIRCUIT OPERATION: a demotion between two operations refuses
    /// the SECOND one with no runtime — the circuit path applies the same fresh rule as the request
    /// path.
    /// </summary>
    [Fact]
    public async Task CircuitPath_ReReadsTheRoleOnEveryOperation()
    {
        var (manager, accessor) = Build(officialProvider: "ollama-local");

        var first = await Acquire(accessor, CircuitState(OriginalId));
        Assert.True(first.IsAuthorized);
        Assert.NotNull(first.Runtime);

        await _auth.SetRoleAsync(OriginalId, UserRole.Viewer);

        var second = await Acquire(accessor, CircuitState(OriginalId));
        Assert.False(second.IsAuthorized, "a demoted circuit user must be refused on the next operation");
        Assert.Null(second.Runtime);

        // Restore the seeded state for the remaining rows.
        await _auth.SetRoleAsync(OriginalId, UserRole.Member);
        Assert.NotNull(manager);
    }

    // ── The connect path ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CONNECT PATH APPLIES THE SAME CAPABILITY RULE: a Viewer is refused with NO runtime, and
    /// the manager is never asked for one.
    /// </summary>
    [Fact]
    public async Task ConnectPath_RefusesAViewer_WithNoRuntime()
    {
        var (manager, accessor) = Build(officialProvider: "ollama-local");

        var access = await Connect(accessor, FacadeAuthorizationHarness.Principal(ViewerId));

        Assert.False(access.IsAuthorized);
        Assert.Null(access.Runtime);
        Assert.Null(access.Result);
        Assert.Equal(ComposerRuntimeStatus.Refused, access.Status);
        Assert.Empty(ManagerEntries(manager));
    }

    /// <summary>
    /// A CONNECT FOR AN ACCOUNT WITH NO CACHED RUNTIME IS THE MANAGER'S TYPED REFUSAL
    /// (<see cref="ComposerRuntimeRefusal.NoRuntime"/>) — never an exception and never a
    /// fabricated runtime.
    /// </summary>
    [Fact]
    public async Task ConnectPath_ForAnAccountWithoutACachedRuntime_RefusesWithNoRuntime()
    {
        var (manager, accessor) = Build(officialProvider: "ollama-local");

        var access = await Connect(accessor, FacadeAuthorizationHarness.Principal(MemberId));

        Assert.True(access.IsAuthorized, "the member itself is authorized; only the connect is refused");
        Assert.NotNull(access.Result);
        Assert.Equal(ComposerRuntimeStatus.Refused, access.Status);
        Assert.Equal(ComposerRuntimeRefusal.NoRuntime, access.Result!.Refusal);
        Assert.Null(access.Runtime);
        Assert.Empty(ManagerEntries(manager));
    }

    /// <summary>
    /// THE CONNECT PATH DRIVES THE MANAGER'S DEFERRED CONNECT: a runtime whose credential is not
    /// available stays <see cref="ComposerRuntimeStatus.Deferred"/> with a runtime, and once the
    /// credential arrives the SAME runtime connects and is reported
    /// <see cref="ComposerRuntimeStatus.Connected"/>.
    /// </summary>
    [Fact]
    public async Task ConnectPath_ForADeferredRuntime_ReportsDeferredUntilTheCredentialIsAvailable()
    {
        var tokenAvailable = false;
        var (manager, accessor) = Build(officialProvider: "copilot", tokenAvailable: () => tokenAvailable);

        // The first acquisition settles the runtime DEFERRED (a Copilot-backed primary without a
        // usable credential).
        var acquired = await Acquire(accessor, FacadeAuthorizationHarness.Principal(OriginalId));
        Assert.True(acquired.IsAuthorized);
        Assert.Equal(ComposerRuntimeStatus.Deferred, acquired.Status);
        Assert.NotNull(acquired.Runtime);

        // The credential is still missing: the connect is refused as deferred, and the SAME runtime
        // is kept.
        var stillDeferred = await Connect(accessor, FacadeAuthorizationHarness.Principal(OriginalId));
        Assert.True(stillDeferred.IsAuthorized);
        Assert.Equal(ComposerRuntimeStatus.Deferred, stillDeferred.Status);
        Assert.Same(acquired.Runtime, stillDeferred.Runtime);
        Assert.False(stillDeferred.Runtime!.Composer.IsConnected);

        // The credential arrives: the connect now completes.
        tokenAvailable = true;
        var connected = await Connect(accessor, FacadeAuthorizationHarness.Principal(OriginalId));
        Assert.True(connected.IsAuthorized);
        Assert.Equal(ComposerRuntimeStatus.Connected, connected.Status);
        Assert.Same(acquired.Runtime, connected.Runtime);
        Assert.True(connected.Runtime!.Composer.IsConnected);
        Assert.NotNull(manager);
    }

    // ── Freshness on the request path ──────────────────────────────────────────────────────────

    /// <summary>
    /// A REFUSAL IS NEVER CACHED: a Viewer is refused, and the SAME accessor serves the very same
    /// account as soon as it is promoted — so the capability check really is re-read per operation.
    /// </summary>
    [Fact]
    public async Task RequestPath_APromotedViewer_GainsARuntimeOnTheNextOperation()
    {
        var (manager, accessor) = Build(officialProvider: "ollama-local");

        var denied = await Acquire(accessor, FacadeAuthorizationHarness.Principal(ViewerId));
        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Runtime);
        Assert.Empty(ManagerEntries(manager));

        await _auth.SetRoleAsync(ViewerId, UserRole.Member);

        var allowed = await Acquire(accessor, FacadeAuthorizationHarness.Principal(ViewerId));
        Assert.True(allowed.IsAuthorized, "a promoted account must be served on the very next operation");
        Assert.NotNull(allowed.Runtime);
        Assert.Equal(_viewerId, allowed.Runtime!.State.UserId);

        // Restore the seeded state.
        await _auth.SetRoleAsync(ViewerId, UserRole.Viewer);
    }

    /// <summary>
    /// THE ACCESSOR OWNS NO RUNTIME: it is not disposable, holds no runtime handle of its own and
    /// never disposes one. The manager is the sole owner and disposer, so a runtime survives the
    /// accessor.
    /// </summary>
    [Fact]
    public async Task Accessor_NeverOwnsOrDisposesARuntime()
    {
        var (manager, accessor) = Build(officialProvider: "ollama-local");

        var access = await Acquire(accessor, FacadeAuthorizationHarness.Principal(MemberId));
        Assert.NotNull(access.Runtime);

        // The accessor is not a disposal seam at all.
        Assert.IsNotAssignableFrom<IDisposable>(accessor);
        Assert.IsNotAssignableFrom<IAsyncDisposable>(accessor);

        // The runtime is still cached by the MANAGER and still alive: the accessor disposed nothing.
        Assert.Contains(_memberId, ManagerEntries(manager));
        Assert.True(access.Runtime!.Composer.IsConnected);
    }

    /// <summary>
    /// THE ACCESSOR IS NEVER CAPTURED BY A SINGLETON OR BY A CONSTRUCTOR: no registration builds a
    /// singleton over the scoped accessor, and no type's constructor takes one as a dependency — so
    /// a singleton can never hold one circuit's identity for the process lifetime.
    /// </summary>
    [Fact]
    public void TheScopedAccessor_IsNeverCapturedByASingletonOrAConstructor()
    {
        var repoRoot = TestRepoRoot.Find();
        var sources = new[]
        {
            Path.Combine(repoRoot, "src", "CopilotHive", "Program.cs"),
            Path.Combine(repoRoot, "src", "CopilotHive", "Services", "ComposerRuntimeFacadeDeployment.cs"),
        };

        foreach (var path in sources)
        {
            var source = TestSourceScanner.StripNonCode(File.ReadAllText(path).ReplaceLineEndings("\n"));

            // No SINGLETON registration statement mentions the scoped accessor.
            foreach (var statement in StatementsContaining(source, "AddSingleton"))
            {
                Assert.DoesNotContain("ComposerRuntimeAccessor", statement, StringComparison.Ordinal);
                Assert.DoesNotContain("CircuitUserPrincipalAccessor", statement, StringComparison.Ordinal);
            }
        }

        // NO CONSTRUCTOR CAPTURE: no PRODUCTION type declares a CONSTRUCTOR parameter of the scoped
        // accessor type — so no long-lived (singleton) object can hold one. The accessor itself
        // legitimately HOLDS the scoped circuit accessor, because that is its job and because the
        // accessor is scoped too.
        //
        // The check is REFLECTION-based (not a source scan), so it observes the real constructor
        // signatures and cannot be satisfied by an unrelated method parameter.
        var productionRoot = Path.Combine(repoRoot, "src", "CopilotHive");
        var assembly = typeof(ComposerRuntimeAccessor).Assembly;
        var offending = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract)
                continue;
            if (type == typeof(ComposerRuntimeAccessor))
                continue;
            if (type.Namespace?.StartsWith("CopilotHive.Tests", StringComparison.Ordinal) == true)
                continue;

            foreach (var constructor in type.GetConstructors(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.ParameterType == typeof(ComposerRuntimeAccessor)
                        || parameter.ParameterType == typeof(CircuitUserPrincipalAccessor))
                    {
                        offending.Add($"{type.FullName}.{constructor.Name}({parameter.ParameterType.Name})");
                    }
                }
            }
        }

        Assert.True(
            offending.Count == 0,
            "no production constructor may capture a scoped accessor: " + string.Join(", ", offending));

        Assert.NotNull(productionRoot);
    }

    /// <summary>
    /// Counts the statements containing <paramref name="fragment"/> (semicolon-delimited, code-only).
    /// </summary>
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

    private static void AssertDenied(ComposerRuntimeAccess access)
    {
        Assert.False(access.IsAuthorized);
        Assert.Null(access.Runtime);
        Assert.Null(access.Result);
        Assert.Equal(ComposerRuntimeStatus.Refused, access.Status);
    }

    [Theory]
    [InlineData("demoted")]
    [InlineData("disabled")]
    public async Task CircuitPath_CascadeLessDirectoryChange_NextPrincipalCapabilityAcquireAndConnectAreFresh(string change)
    {
        var circuit = NewCircuitAccessor();
        var (manager, accessor) = Build(officialProvider: "ollama-local", circuit);
        var first = await Acquire(accessor, CircuitState(OriginalId));
        Assert.True(first.IsAuthorized);
        Assert.NotNull(first.Runtime);
        Assert.Equal(_originalId, first.Runtime.State.UserId);
        Assert.Same(first.Runtime, (await Acquire(accessor, (Task<AuthenticationState>?)null)).Runtime);
        var connected = await ConnectCircuit(accessor, null);
        Assert.True(connected.IsAuthorized);
        Assert.Same(first.Runtime, connected.Runtime);
        Assert.True(connected.Runtime!.Composer.IsConnected);
        Assert.True(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        var identity = circuit.EstablishedIdentity;
        Assert.NotNull(identity);

        if (change == "demoted") await _auth.SetRoleAsync(OriginalId, UserRole.Viewer);
        else if (change == "disabled") await _auth.SetEnabledAsync(OriginalId, false);
        else throw new InvalidOperationException(change);

        // Claims are identity-only: unchanged claims are NOT an authorization grant.
        Assert.Same(identity, await ComponentActingUser.AcquireAsync(null, circuit));
        var fresh = await circuit.GetPrincipalAsync(Ct);
        if (change == "demoted")
        {
            Assert.True(fresh.IsResolved);
            Assert.Equal(UserRole.Viewer, fresh.Principal!.Role);
            Assert.Equal(OriginalId, fresh.Principal.GitHubId);
        }
        else
        {
            Assert.Null(fresh.Principal);
            Assert.Equal(PrincipalRefusalReason.DisabledAccount, fresh.RefusalReason);
        }
        Assert.False(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        Assert.False(await ComponentActingUser.IsAllowedAsync(CircuitState(OriginalId), circuit, HiveCapability.InteractiveMember, Ct));
        AssertDenied(await Acquire(accessor, (Task<AuthenticationState>?)null));
        AssertDenied(await ConnectCircuit(accessor, null));
        Assert.Equal(CircuitIdentityState.Initialized, circuit.IdentityState);
        Assert.Equal(new[] { _originalId }, ManagerEntries(manager));

        // Directory changes are transient; unlike sign-out, re-enablement can recover in this scope.
        await _auth.SetRoleAsync(OriginalId, UserRole.Member);
        await _auth.SetEnabledAsync(OriginalId, true);
        Assert.True(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        Assert.Same(first.Runtime, (await Acquire(accessor, (Task<AuthenticationState>?)null)).Runtime);
        Assert.Same(first.Runtime, (await ConnectCircuit(accessor, null)).Runtime);
    }

    [Theory]
    [InlineData("A>signout>missing", "demoted")]
    [InlineData("A>signout>A", "demoted")]
    [InlineData("A>signout>B", "demoted")]
    [InlineData("A>B>missing", "demoted")]
    [InlineData("A>B>A", "demoted")]
    [InlineData("A>signout>missing", "disabled")]
    [InlineData("A>signout>A", "disabled")]
    [InlineData("A>signout>B", "disabled")]
    [InlineData("A>B>missing", "disabled")]
    [InlineData("A>B>A", "disabled")]
    public async Task CircuitPath_DirectoryChangeAfterTerminalTransition_NeverReplaysCachedAuthorization(string transition, string change)
    {
        var circuit = NewCircuitAccessor();
        var (_, accessor) = Build(officialProvider: "ollama-local", circuit);
        Assert.True((await Acquire(accessor, CircuitState(OriginalId))).IsAuthorized);
        Assert.True(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        Assert.Equal(OriginalId, (await circuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);

        var steps = transition.Split('>');
        var invalidate = steps[1] == "signout"
            ? Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Anonymous()))
            : CircuitState(MemberId);
        AssertDenied(await Acquire(accessor, invalidate));
        var terminal = circuit.IdentityState;
        Assert.True(circuit.IsInvalidated);

        foreach (var id in new[] { OriginalId, MemberId })
        {
            if (change == "demoted") await _auth.SetRoleAsync(id, UserRole.Viewer);
            else if (change == "disabled") await _auth.SetEnabledAsync(id, false);
            else throw new InvalidOperationException(change);
        }
        var last = steps[2] == "missing" ? null : CircuitState(steps[2] == "A" ? OriginalId : MemberId);
        Assert.Null(await ComponentActingUser.AcquireAsync(last, circuit));
        Assert.Null((await circuit.GetPrincipalAsync(Ct)).Principal);
        Assert.False(await circuit.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False(await ComponentActingUser.IsAllowedAsync(last, circuit, HiveCapability.InteractiveMember, Ct));
        AssertDenied(await Acquire(accessor, last));
        AssertDenied(await ConnectCircuit(accessor, last));

        // Re-enabling/promoting both accounts must not turn terminal invalidation into transient denial.
        foreach (var id in new[] { OriginalId, MemberId })
        {
            await _auth.SetRoleAsync(id, UserRole.Member);
            await _auth.SetEnabledAsync(id, true);
        }
        foreach (var state in new Task<AuthenticationState>?[] { CircuitState(OriginalId), CircuitState(MemberId), null })
        {
            Assert.Null(await ComponentActingUser.AcquireAsync(state, circuit));
            Assert.Null((await circuit.GetPrincipalAsync(Ct)).Principal);
            Assert.False(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
            Assert.False(await ComponentActingUser.IsAllowedAsync(state, circuit, HiveCapability.InteractiveMember, Ct));
            AssertDenied(await Acquire(accessor, state));
            AssertDenied(await ConnectCircuit(accessor, state));
            Assert.Equal(terminal, circuit.IdentityState);
        }
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the REAL manager/accessor pair: the production factory over a real provider, the
    /// production manager, and the production accessor over a scoped circuit accessor.
    /// </summary>
    private (ComposerRuntimeManager Manager, ComposerRuntimeAccessor Accessor) Build(
        string officialProvider, Func<bool>? tokenAvailable = null)
        => Build(officialProvider, circuitAccessor: null, tokenAvailable);

    private CircuitUserPrincipalAccessor NewCircuitAccessor() =>
        new(_auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);

    private (ComposerRuntimeManager Manager, ComposerRuntimeAccessor Accessor) Build(
        string officialProvider, CircuitUserPrincipalAccessor? circuitAccessor, Func<bool>? tokenAvailable = null)
    {
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
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton<LlmSessionRegistry>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        _resolver = provider.GetRequiredService<HivePrincipalResolver>();
        _policy = provider.GetRequiredService<HivePolicy>();

        var factory = new ComposerFactory(
            provider,
            _dir,
            ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: tokenAvailable ?? (() => true),
            providerOf: _ => officialProvider);

        var manager = new ComposerRuntimeManager(
            factory, _auth.Directory.Service, NullLogger<ComposerRuntimeManager>.Instance);
        _managers.Add(manager);

        var accessor = new ComposerRuntimeAccessor(
            manager,
            circuitAccessor ?? new CircuitUserPrincipalAccessor(_resolver, _policy),
            _resolver,
            _policy,
            NullLogger<ComposerRuntimeAccessor>.Instance);

        return (manager, accessor);
    }

    private Task<ComposerRuntimeAccess> Acquire(ComposerRuntimeAccessor accessor, ClaimsPrincipal principal)
    {
        var task = accessor.AcquireForRequestAsync(principal, Ct);
        _requests.Add(task);
        return task;
    }

    private Task<ComposerRuntimeAccess> ConnectCircuit(
        ComposerRuntimeAccessor accessor, Task<AuthenticationState>? state)
    {
        var task = accessor.ConnectForCircuitAsync(state, Ct);
        _requests.Add(task);
        return task;
    }

    private Task<ComposerRuntimeAccess> Acquire(
        ComposerRuntimeAccessor accessor, Task<AuthenticationState>? state)
    {
        var task = accessor.AcquireForCircuitAsync(state, Ct);
        _requests.Add(task);
        return task;
    }

    private Task<ComposerRuntimeAccess> Connect(ComposerRuntimeAccessor accessor, ClaimsPrincipal principal)
    {
        var task = accessor.ConnectForRequestAsync(principal, Ct);
        _requests.Add(task);
        return task;
    }

    private static Task<AuthenticationState> CircuitState(string gitHubId) =>
        Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(gitHubId)));

    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();

        async Task Step(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { failures.Add(ex); }
        }

        foreach (var request in _requests.ToArray())
            await Step(() => request.ContinueWith(
                _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));

        // The manager is the sole disposer: shut every one down before the providers.
        foreach (var manager in _managers)
            await Step(manager.ShutdownAsync);

        foreach (var provider in _providers)
            await Step(() => provider.DisposeAsync().AsTask());

        await Step(() => { _goalDb.Dispose(); return Task.CompletedTask; });
        // xUnit invokes DisposeAsync, not the synchronous Dispose, when both exist.
        await Step(() => { _auth.Dispose(); return Task.CompletedTask; });
        await Step(() => { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); return Task.CompletedTask; });

        if (failures.Count > 0)
            throw new AggregateException("Accessor contract test cleanup failed", failures);
    }

}
