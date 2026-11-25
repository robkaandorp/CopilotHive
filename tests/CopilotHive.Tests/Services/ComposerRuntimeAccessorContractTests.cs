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
    // OUTER FIXTURE OWNERSHIP. Every Build() creates one GROUP (provider + manager) and every
    // Acquire/Connect registers its request against the group whose accessor it used, so teardown knows
    // which resources a still-pending request depends on. Admission is closed by DisposeAsync.
    private readonly object _lifecycleGate = new();
    private readonly List<FixtureGroup> _groups = [];
    private readonly Dictionary<ComposerRuntimeAccessor, FixtureGroup> _groupOf = [];
    private readonly List<string> _cleanupLog = [];
    private readonly List<Exception> _deferredFailures = [];
    private bool _requestsClosed;
    private bool _disposeStarted;
    private TaskCompletionSource _cleanupChanged = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, TimeProvider> OuterClockForStep { get; init; } = _ => TimeProvider.System;

    /// <summary>
    /// Test-only observation point on the REAL deadline continuation: invoked after a step's own deadline
    /// has won and before that step's outcome is classified. A test can hold it to let the work settle
    /// first. Null is inert in production use of the fixture; a throwing hook never changes teardown.
    /// </summary>
    private Func<string, Task, Task>? OnDeadlineWon { get; init; }
    private static readonly TimeSpan ProbeGuard = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Bound for EVERY outer teardown wait (request joins, manager shutdown, provider disposal, deferred
    /// steps). It is independent of the scenario/test cancellation token, which never reaches teardown.
    /// </summary>
    private TimeSpan OuterJoinTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Completes when every cleanup step the bounded <see cref="DisposeAsync"/> had to DEFER (because
    /// unfinished work still depended on it) has run. <see cref="Task.CompletedTask"/> when nothing was
    /// deferred. Observed by the regression; never awaited by xUnit.
    /// </summary>
    internal Task DeferredCleanup { get; private set; } = Task.CompletedTask;

    /// <summary>Ordered evidence of every teardown step attempted, deferred, timed out or completed.</summary>
    internal IReadOnlyList<string> CleanupLog
    {
        get { lock (_lifecycleGate) return [.. _cleanupLog]; }
    }

    /// <summary>Faults of the deferred (post-quiescence) cleanup, surfaced for observation.</summary>
    internal IReadOnlyList<Exception> DeferredFailures
    {
        get { lock (_lifecycleGate) return [.. _deferredFailures]; }
    }

    private sealed class FixtureGroup(string name, ServiceProvider provider, ComposerRuntimeManager manager)
    {
        internal string Name { get; } = name;
        internal ServiceProvider Provider { get; } = provider;
        internal ComposerRuntimeManager Manager { get; } = manager;
        internal List<Task> Requests { get; } = [];
        internal List<Task> Unfinished { get; } = [];
        // The ORIGINAL requests whose outer join timed out. Unfinished holds only the observation
        // continuation (which completes successfully even when the request faults), so the request
        // itself is retained here to report a LATE fault exactly once after it settles.
        internal List<Task> LateRequests { get; } = [];
        internal Task? Shutdown { get; set; }
        internal Task? Disposal { get; set; }
    }

    private void Log(string entry)
    {
        lock (_lifecycleGate)
        {
            _cleanupLog.Add(entry);
            _cleanupChanged.TrySetResult();
            _cleanupChanged = NewSignal();
        }
    }

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
        var work = new CircuitObservationTestWork(ParkedJoinTimeout);
        work.ReleaseBeforeJoin(() => gate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
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
        });
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

    /// <summary>Bounded, cancellation-INDEPENDENT join budget for releasing/joining parked operations.</summary>
    private static readonly TimeSpan ParkedJoinTimeout = TimeSpan.FromSeconds(15);

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

    // ── Stale acquisitions: capability authorization and the Composer circuit path ──────────────

    /// <summary>
    /// A SUPERSEDED CAPABILITY QUESTION IS DENIED, WHILE THE CURRENT TASK STILL AUTHORIZES. The exact
    /// older task is held while a newer one is registered; the older task is released carrying the
    /// ADMIN account. Because it is superseded it forwards no claims, so the capability question denies
    /// — and the healthiest possible identity on the stale task cannot change that. The CURRENT task is
    /// then released and really answers <c>true</c>, and the accessor still resolves the admin.
    /// </summary>
    [Fact]
    public async Task StaleCapabilityQuestion_Denies_WhileTheCurrentTaskStillAuthorizes()
    {
        var circuit = NewCircuitAccessor();
        Assert.True(await ComponentActingUser.IsAllowedAsync(
            CircuitState(OriginalId), circuit, HiveCapability.InteractiveMember, Ct));
        Assert.Equal(OriginalId, (await circuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);

        var olderGate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerGate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = ComponentActingUser.IsAllowedAsync(
            olderGate.Task, circuit, HiveCapability.InteractiveMember, Ct).AsTask();
        var current = ComponentActingUser.IsAllowedAsync(
            newerGate.Task, circuit, HiveCapability.InteractiveMember, Ct).AsTask();
        var work = new CircuitObservationTestWork(ParkedJoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        work.ReleaseBeforeJoin(() => newerGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        _ = work.Track(stale);
        _ = work.Track(current);
        await work.RunAsync(async () =>
        {
            Assert.False(stale.IsCompleted);
            Assert.False(current.IsCompleted);

            // THE OLDER, HEALTHY IDENTITY COMPLETES WHILE THE NEWER IS STILL PENDING: denied.
            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            Assert.False(await stale.WaitAsync(Ct));
            Assert.Equal(CircuitIdentityState.Initialized, circuit.IdentityState);
            Assert.Equal(OriginalId, (await circuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);

            // THE CURRENT TASK REALLY AUTHORIZES — the denial above was supersession, not a broken path.
            newerGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            Assert.True(await current.WaitAsync(Ct));
            Assert.True(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        });
    }

    /// <summary>
    /// A SUPERSEDED CAPABILITY QUESTION IS DENIED EVEN AFTER THE NEWER TASK ALREADY WON — so the rule
    /// does not depend on the newer task still being in flight.
    /// </summary>
    [Fact]
    public async Task StaleCapabilityQuestion_AfterTheNewerTaskWon_StillDenies()
    {
        var circuit = NewCircuitAccessor();
        Assert.True(await ComponentActingUser.IsAllowedAsync(
            CircuitState(OriginalId), circuit, HiveCapability.InteractiveMember, Ct));

        var olderGate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = ComponentActingUser.IsAllowedAsync(
            olderGate.Task, circuit, HiveCapability.InteractiveMember, Ct).AsTask();
        var work = new CircuitObservationTestWork(ParkedJoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
        {
            Assert.True(await ComponentActingUser.IsAllowedAsync(
                CircuitState(OriginalId), circuit, HiveCapability.InteractiveMember, Ct));

            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            Assert.False(await stale.WaitAsync(Ct));
            Assert.Equal(CircuitIdentityState.Initialized, circuit.IdentityState);
            Assert.True(await circuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        });
    }

    /// <summary>
    /// A SUPERSEDED COMPOSER CIRCUIT ACQUIRE NEVER REACHES THE RUNTIME PATH, WHILE THE CURRENT TASK DOES.
    /// <para>
    /// <see cref="ComposerRuntimeAccessor.AcquireForCircuitAsync"/> inherits the shared ordering fix
    /// without any caller change: the stale task is superseded, so the identity it would have carried is
    /// never forwarded, no runtime is created for it, and the manager's entry table stays EMPTY for it. A
    /// stale call that passes <c>null</c> downstream and receives <c>Forbidden</c> is the CORRECT outcome
    /// — zero Composer calls is not required. The CURRENT task then really connects a runtime, so the
    /// stale denial is discriminating rather than a broken path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StaleComposerCircuitAcquire_IsDeniedWithoutARuntime_AndTheCurrentTaskReachesTheRuntime()
    {
        var circuit = NewCircuitAccessor();
        var (manager, accessor) = Build(officialProvider: "ollama-local", circuit);

        var olderGate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = Acquire(accessor, olderGate.Task);
        var work = new CircuitObservationTestWork(ParkedJoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
        {
            Assert.False(stale.IsCompleted);

            // The NEWER registration supersedes the older one immediately. It is the CURRENT task.
            var current = await Acquire(accessor, CircuitState(OriginalId));
            Assert.True(current.IsAuthorized, "the current circuit task must reach the runtime path");
            Assert.NotNull(current.Runtime);
            Assert.Equal(_originalId, current.Runtime!.State.UserId);
            Assert.False(circuit.IsInvalidated);

            // THE SUPERSEDED ACQUIRE COMPLETES WITH THE HEALTHY ADMIN IDENTITY: still denied, no runtime.
            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            AssertDenied(await stale.WaitAsync(Ct));
            Assert.Equal(new[] { _originalId }, ManagerEntries(manager));
            Assert.Equal(CircuitIdentityState.Initialized, circuit.IdentityState);
            Assert.Equal(OriginalId, (await circuit.GetPrincipalAsync(Ct)).Principal!.GitHubId);
        });
    }

    /// <summary>
    /// A SUPERSEDED COMPOSER CIRCUIT CONNECT IS DENIED AND CONNECTS NOTHING, WHILE THE CURRENT TASK
    /// CONNECTS THE RUNTIME it created. The runtime's <see cref="ComposerUserRuntime.Composer"/> is the
    /// REAL object the manager owns: a stale connect that had reached the manager would have flipped its
    /// <c>Composer.IsConnected</c> flag, so that flag is the positive control.
    /// </summary>
    [Fact]
    public async Task StaleComposerCircuitConnect_IsDeniedAndConnectsNothing_AndTheCurrentTaskConnects()
    {
        var circuit = NewCircuitAccessor();
        var (_, accessor) = Build(officialProvider: "ollama-local", circuit);

        // A runtime exists for the account, created through the CURRENT circuit task.
        var acquired = await Acquire(accessor, CircuitState(OriginalId));
        Assert.True(acquired.IsAuthorized);
        Assert.NotNull(acquired.Runtime);

        var olderGate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = ConnectCircuit(accessor, olderGate.Task);
        var work = new CircuitObservationTestWork(ParkedJoinTimeout);
        work.ReleaseBeforeJoin(() => olderGate.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        _ = work.Track(stale);
        await work.RunAsync(async () =>
        {
            Assert.False(stale.IsCompleted);

            // THE NEWER REGISTRATION is current and really connects the runtime.
            var connected = await ConnectCircuit(accessor, CircuitState(OriginalId));
            Assert.True(connected.IsAuthorized);
            Assert.Same(acquired.Runtime, connected.Runtime);
            Assert.True(connected.Runtime!.Composer.IsConnected);

            olderGate.SetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId)));
            AssertDenied(await stale.WaitAsync(Ct));

            // The stale connect changed nothing: the runtime is still the one the current call connected.
            Assert.Same(acquired.Runtime, connected.Runtime);
            Assert.True(acquired.Runtime!.Composer.IsConnected);
            Assert.Equal(CircuitIdentityState.Initialized, circuit.IdentityState);
        });
    }

    [Fact]
    public async Task ComposerCleanup_CanceledScenarioAndFirstJoinFault_StillReleasesAndJoinsSecondAcquire()
    {
        var (_, accessor) = Build(officialProvider: "ollama-local", NewCircuitAccessor());
        var work = new CircuitObservationTestWork(ParkedJoinTimeout);
        var first = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFault = new InvalidOperationException("first acquire failed during cleanup");
        var lastFault = new InvalidOperationException("last join was attempted");
        using var scenario = new CancellationTokenSource();
        var primary = new InvalidOperationException("primary assertion failure");
        work.ReleaseBeforeJoin(() => first.TrySetException(firstFault));
        work.ReleaseBeforeJoin(() => second.TrySetResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId))));
        var firstAcquire = work.Track(Acquire(accessor, first.Task));
        var secondAcquire = work.Track(Acquire(accessor, second.Task));
        _ = work.Track(Task.FromException(lastFault));

        var error = await Assert.ThrowsAsync<AggregateException>(() => work.RunAsync(async () =>
        {
            Assert.False(firstAcquire.IsCompleted);
            Assert.False(secondAcquire.IsCompleted);
            scenario.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondAcquire.WaitAsync(scenario.Token));
            throw primary;
        }));

        Assert.Collection(error.InnerExceptions, ex => Assert.Same(primary, ex),
            ex => Assert.Same(firstFault, ex), ex => Assert.Same(lastFault, ex));
        Assert.True(secondAcquire.IsCompletedSuccessfully);
        Assert.True((await secondAcquire).IsAuthorized);
    }

    // ── OUTER FIXTURE TEARDOWN (the exact DisposeAsync path xUnit invokes) ─────────────────────

    /// <summary>The provider of the group <paramref name="accessor"/> was built in (test-support).</summary>
    private ServiceProvider ProviderOf(ComposerRuntimeAccessor accessor)
    {
        lock (_lifecycleGate) return _groupOf[accessor].Provider;
    }

    private static bool IsDisposed(ServiceProvider provider)
    {
        try { _ = provider.GetRequiredService<HivePolicy>(); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static bool IsDisposed(CopilotHiveDbContext db)
    {
        try { _ = db.Model; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static Task<T> Bounded<T>(Task<T> task) => task.WaitAsync(ProbeGuard, CancellationToken.None);

    private sealed class ProviderDisposalProbe(Exception? failure = null, Action? onEnter = null) : IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Release { get; } = NewSignal();
        internal int Calls;
        internal bool Exited { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Calls);
            onEnter?.Invoke();
            Entered.TrySetResult();
            await Release.Task;
            Exited = true;
            if (failure is not null) throw failure;
        }
    }

    private sealed class ManagerShutdownProbe
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource Release { get; } = NewSignal();

        // The real manager invokes this on its teardown worker, before it can retire the entry.
        internal void HoldJoin(int _, Task __)
        {
            Entered.TrySetResult();
            Release.Task.WaitAsync(TimeSpan.FromSeconds(45), CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>The actual task observed by the held deadline handler, not merely a stage-entry signal.</summary>
    private sealed class DeadlineHold
    {
        internal TaskCompletionSource<Task> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = NewSignal();
    }

    private sealed class OuterDisposalScenario
    {
        private readonly CircuitObservationTestWork _owner = new(ProbeGuard);
        private AggregateException? _observedOuter;
        private IReadOnlyList<Exception> _observedDeferred = [];
        private Task? _outer;
        // Separate clocks prevent an earlier stage's timer from satisfying the next rendezvous.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ControlledTimeProvider> _clocks = new();
        private ControlledTimeProvider ClockFor(string step) => _clocks.GetOrAdd(step, _ => new());
        internal ComposerRuntimeAccessorContractTests Fixture { get; }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeadlineHold> _holds = new();

        internal OuterDisposalScenario() => Fixture = new() { OuterClockForStep = ClockFor, OnDeadlineWon = HoldAsync };

        /// <summary>Holds the REAL deadline continuation of <paramref name="step"/>; the rescue always releases it.</summary>
        internal DeadlineHold HoldDeadline(string step)
        {
            var hold = new DeadlineHold();
            _holds[step] = hold;
            RescueWith(() => hold.Release.TrySetResult());
            return hold;
        }

        private async Task HoldAsync(string step, Task work)
        {
            if (!_holds.TryGetValue(step, out var hold)) return;
            hold.Held.TrySetResult(work);
            await hold.Release.Task.WaitAsync(TimeSpan.FromSeconds(45), CancellationToken.None);
        }
        internal T Track<T>(T task) where T : Task => _owner.Track(task);
        internal void RescueWith(Action release) => _owner.ReleaseBeforeJoin(release);
        internal Task Begin() => _outer ??= ((IAsyncDisposable)Fixture).DisposeAsync().AsTask();

        /// <summary>Rendezvous on the fixture arming its own bounded wait for <paramref name="step"/>, WITHOUT advancing the clock.</summary>
        internal async Task WaitArmedAsync(string step)
        {
            try { await ClockFor(step).WaitForPendingTimerAsync(Fixture.OuterJoinTimeout, CancellationToken.None); }
            catch (TimeoutException) { Assert.Fail($"{step}: the fixture must arm its own bounded wait."); }
        }

        internal async Task ExpireAsync(string step)
        {
            var clock = ClockFor(step);
            try { await clock.WaitForPendingTimerAsync(Fixture.OuterJoinTimeout, CancellationToken.None); }
            catch (TimeoutException)
            {
                Assert.Fail($"{step}: the fixture must arm its own bounded wait before rescue.");
            }
            clock.Advance(Fixture.OuterJoinTimeout);
        }

        internal async Task<Task> ExpireAndIdentifyAsync(string step, DeadlineHold hold, Func<Task?> owned)
        {
            await ExpireAsync(step);
            // Timer arming/advancement is only a trigger. The real winner branch publishes the task
            // it was waiting on, after the fixture has assigned the group's Shutdown or Disposal.
            var work = await Bounded(hold.Held.Task);
            Assert.Same(Assert.IsAssignableFrom<Task>(owned()), work);
            Assert.False(work.IsCompleted, $"{step}: the actual owned operation must still be pending after its deadline wins");
            Assert.False(hold.Release.Task.IsCompleted, $"{step}: the deadline handler must still be held");
            return work;
        }

        internal async Task ExpectTimeoutAsync(string step)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => Begin().WaitAsync(ProbeGuard, CancellationToken.None));
            _observedOuter = error;
            var named = Assert.IsType<TimeoutException>(Assert.Single(error.InnerExceptions));
            Assert.Contains($"'{step}'", named.Message, StringComparison.Ordinal);
            Assert.Contains("NOT stopped", named.Message, StringComparison.Ordinal);
        }

        internal async Task ExpectFaultAsync(Exception fault)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => Begin().WaitAsync(ProbeGuard, CancellationToken.None));
            _observedOuter = error;
            Assert.Same(fault, Assert.Single(error.InnerExceptions));
        }

        internal async Task JoinDeferredAsync(params Action<Exception>[] expected)
        {
            await Fixture.DeferredCleanup.WaitAsync(ProbeGuard, CancellationToken.None);
            var failures = Fixture.DeferredFailures;
            Assert.Collection(failures, expected);
            _observedDeferred = failures;
        }

        // CircuitObservationTestWork performs the rescue in its finally, then attempts every
        // bounded join, even if a prior assertion or join fails. Do not swallow diagnostics that
        // the scenario did not already observe. Always join the retained continuation after outer.
        internal Task RunAsync(Func<Task> scenario) => _owner.RunAsync(scenario, async () =>
        {
            try { await Begin(); }
            catch (AggregateException ex) when (ReferenceEquals(ex, _observedOuter)) { }
        }, async () =>
        {
            await Fixture.DeferredCleanup;
            var unobserved = Fixture.DeferredFailures.Except(_observedDeferred).ToArray();
            if (unobserved.Length != 0) throw new AggregateException("Deferred fixture teardown failed.", unobserved);
        });
    }

    private async Task WaitForCleanupEntryAsync(string entry)
    {
        async Task ObserveAsync(CancellationToken guard)
        {
            while (true)
            {
                Task changed;
                lock (_lifecycleGate)
                {
                    if (_cleanupLog.Contains(entry)) return;
                    changed = _cleanupChanged.Task;
                }
                await changed.WaitAsync(guard);
            }
        }

        // This is only a test hang guard; the fixture's expiry is driven by the controlled clock.
        using var timeout = new CancellationTokenSource(ProbeGuard);
        try { await ObserveAsync(timeout.Token); }
        catch (OperationCanceledException) { Assert.Fail($"Expected cleanup milestone '{entry}' before rescue."); }
    }

    private static void AssertSharedResourcesAlive(ComposerRuntimeAccessorContractTests fixture)
    {
        Assert.False(IsDisposed(fixture._goalDb), "Dependent goal db must stay available until actual quiescence.");
        using var stillAvailable = fixture._auth.Directory.CreateDbContext();
        Assert.True(Directory.Exists(fixture._dir));
        Assert.DoesNotContain(fixture.CleanupLog, entry => entry.StartsWith("goal-db:", StringComparison.Ordinal));
        Assert.False(fixture.DeferredCleanup.IsCompleted);
    }

    private static void AssertSharedResourcesDisposed(ComposerRuntimeAccessorContractTests fixture)
    {
        foreach (var entry in new[] { "goal-db:done", "auth-harness:done", "directory:done" })
            Assert.Contains(entry, fixture.CleanupLog);
        Assert.True(IsDisposed(fixture._goalDb));
        Assert.Throws<ObjectDisposedException>(() => { using var _ = fixture._auth.Directory.CreateDbContext(); });
        Assert.False(Directory.Exists(fixture._dir));
    }

    private static void AssertManagerFinished(ComposerRuntimeManager manager, ComposerUserRuntime runtime)
    {
        Assert.Empty(ManagerEntries(manager));
        Assert.True(runtime.Composer.TeardownCompletion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OuterFixtureDisposal_WithAStalledTrackedRequest_TimesOutByName_DefersDependentCleanup_AndFinishesAfterRescue()
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var gate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId));
        run.RescueWith(() => gate.TrySetResult(auth));
        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (stalledManager, stalledAccessor) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var (finishedManager, finishedAccessor) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var finished = await Bounded(run.Track(sut.Acquire(finishedAccessor, CircuitState(OriginalId))));
            Assert.True(finished.IsAuthorized);
            var stalled = run.Track(sut.Acquire(stalledAccessor, gate.Task));
            Assert.False(stalled.IsCompleted);

            using var scenario = new CancellationTokenSource();
            scenario.Cancel();
            var primary = new OperationCanceledException("original scenario canceled", scenario.Token);
            var work = new CircuitObservationTestWork(TimeSpan.Zero);
            _ = work.Track(stalled);
            var inner = await Assert.ThrowsAsync<AggregateException>(() => work.RunAsync(() => Task.FromException(primary)));
            Assert.Collection(inner.InnerExceptions, ex => Assert.Same(primary, ex), ex => Assert.IsType<TimeoutException>(ex));
            Assert.True(scenario.IsCancellationRequested);
            Assert.False(stalled.IsCompleted, "the inner timeout did not stop the request");

            var disposal = run.Begin();
            Assert.False(stalled.IsCompleted, "the real tracked request is pending on entry to outer disposal");
            Assert.False(disposal.IsCompleted);
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = sut.Register(stalledAccessor, () => throw new Xunit.Sdk.XunitException("request started after final sweep began"));
            });
            Assert.Throws<InvalidOperationException>(() => { _ = sut.Acquire(finishedAccessor, CircuitState(OriginalId)); });
            await run.ExpireAsync("request-join:group-0");
            await run.ExpectTimeoutAsync("request-join:group-0");

            Assert.False(stalled.IsCompleted);
            Assert.Contains("request-join:group-0:timed-out", sut.CleanupLog);
            Assert.Contains("manager-shutdown:group-1:done", sut.CleanupLog);
            Assert.Contains("provider-dispose:group-1:done", sut.CleanupLog);
            AssertManagerFinished(finishedManager, finished.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(finishedAccessor)), "the finished group's provider must really be disposed");
            Assert.Contains("deferred:group-0", sut.CleanupLog);
            Assert.Contains("deferred:shared-resources", sut.CleanupLog);
            Assert.DoesNotContain(sut.CleanupLog, entry => entry.Contains("provider-dispose:group-0", StringComparison.Ordinal));
            Assert.False(IsDisposed(sut.ProviderOf(stalledAccessor)), "the pending request's provider must stay available");
            AssertSharedResourcesAlive(sut);

            gate.SetResult(auth);
            var rescued = await Bounded(stalled);
            Assert.True(rescued.IsAuthorized);
            await run.JoinDeferredAsync();
            var final = sut.CleanupLog.ToList();
            Assert.Contains("deferred:quiesced", final);
            Assert.Contains("deferred-provider-dispose:group-0:done", final);
            Assert.True(final.IndexOf("deferred:quiesced") < final.IndexOf("deferred-provider-dispose:group-0:done"));
            Assert.Contains("deferred:complete", final);
            AssertManagerFinished(stalledManager, rescued.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(stalledAccessor)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    // The scenario asserts this particular injected failure. The finally join must still report any
    // unexpected failure, including when an earlier assertion prevented the scenario from reaching its join.
    private static async Task JoinExpectedFaultAsync(Task work, Exception? expected)
    {
        try { await work; }
        catch (Exception ex) when (ReferenceEquals(ex, expected)) { }
    }

    /// <summary>
    /// THE HELD-REQUEST PATH shared by the late-fault and late-success cases. A REAL tracked Acquire stays
    /// pending through the original scenario error, the inner timeout and the NAMED outer timeout; only then
    /// is it rescued (with <paramref name="lateFault"/>, or successfully when it is <c>null</c>). The rescue
    /// is also registered with the bounded, cancellation-independent owner, so a failed assertion cannot
    /// leave the request running.
    /// </summary>
    private static async Task RunHeldRequestAsync(Exception? lateFault)
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var gate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId));
        run.RescueWith(() =>
        {
            if (lateFault is null) gate.TrySetResult(auth);
            else gate.TrySetException(lateFault);
        });
        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            Task<ComposerRuntimeAccess>? registeredRequest = null;
            var settledAtProviderDispose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dependentProvider = new ProviderDisposalProbe(onEnter: () =>
                settledAtProviderDispose.TrySetResult(registeredRequest is { IsCompleted: true }));
            // Observe the real DI disposal entry, but do not introduce a second held operation.
            dependentProvider.Release.SetResult();
            var (manager, accessor) = sut.Build("ollama-local", sut.NewCircuitAccessor(), disposalProbe: dependentProvider);
            var (safeManager, safeAccessor) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var active = await Bounded(run.Track(sut.Acquire(accessor, CircuitState(OriginalId))));
            var safe = await Bounded(run.Track(sut.Acquire(safeAccessor, CircuitState(OriginalId))));
            Assert.True(active.IsAuthorized);
            Assert.True(safe.IsAuthorized);
            var activeRuntime = Assert.IsType<ComposerUserRuntime>(active.Runtime);
            var held = registeredRequest = sut.Acquire(accessor, gate.Task);
            _ = run.Track(JoinExpectedFaultAsync(held, lateFault));
            Assert.False(held.IsCompleted);

            // ORIGINAL scenario error + INNER timeout, with the request still pending.
            using var scenario = new CancellationTokenSource();
            scenario.Cancel();
            var primary = new OperationCanceledException("original scenario canceled", scenario.Token);
            var inner = new CircuitObservationTestWork(TimeSpan.Zero);
            _ = inner.Track(held);
            var innerError = await Assert.ThrowsAsync<AggregateException>(() => inner.RunAsync(() => Task.FromException(primary)));
            Assert.Collection(innerError.InnerExceptions, ex => Assert.Same(primary, ex), ex => Assert.IsType<TimeoutException>(ex));
            Assert.False(held.IsCompleted, "the inner timeout did not stop the request");

            // The NAMED OUTER timeout, still before any rescue.
            Assert.True(scenario.IsCancellationRequested);
            var outer = run.Begin();
            Assert.False(outer.IsCompleted);
            Assert.False(held.IsCompleted, "the actual request is pending at entry to fixture disposal");
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = sut.Register(accessor, () => throw new Xunit.Sdk.XunitException("request started after final sweep began"));
            });
            await run.ExpireAsync("request-join:group-0");
            await run.ExpectTimeoutAsync("request-join:group-0");
            Assert.False(held.IsCompleted, "the request is still pending when the outer timeout is reported");
            Assert.Empty(sut.DeferredFailures);
            AssertManagerFinished(safeManager, safe.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(safeAccessor)), "the later safe group must finish before rescue");
            Assert.False(activeRuntime.Composer.TeardownCompletion.IsCompleted);
            Assert.False(dependentProvider.Entered.Task.IsCompleted);
            Assert.DoesNotContain(sut.CleanupLog, entry => entry.Contains("provider-dispose:group-0", StringComparison.Ordinal));
            Assert.False(IsDisposed(sut.ProviderOf(accessor)));
            AssertSharedResourcesAlive(sut);

            // Only now rescue. The success and fault rows otherwise take exactly the same path.
            if (lateFault is null)
            {
                gate.SetResult(auth);
                var access = await Bounded(held);
                Assert.True(access.IsAuthorized);
                Assert.Same(activeRuntime, access.Runtime);
                await run.JoinDeferredAsync();
                Assert.Empty(sut.DeferredFailures);
            }
            else
            {
                gate.SetException(lateFault);
                var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Bounded(held));
                Assert.Same(lateFault, thrown);
                await run.JoinDeferredAsync(reported => Assert.Same(lateFault, reported));
                Assert.Equal(1, sut.DeferredFailures.Count(f => ReferenceEquals(f, lateFault)));
            }

            Assert.True(await Bounded(settledAtProviderDispose.Task), "the real provider disposal entered only after the request settled");
            AssertManagerFinished(manager, activeRuntime);
            var final = sut.CleanupLog.ToList();
            Assert.Contains("deferred:quiesced", final);
            Assert.Contains("deferred-provider-dispose:group-0:done", final);
            Assert.True(final.IndexOf("deferred:quiesced") < final.IndexOf("deferred-provider-dispose:group-0:done"));
            Assert.Contains("deferred:complete", final);
            Assert.True(IsDisposed(sut.ProviderOf(accessor)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    /// <summary>LATE FAULT: a request that faults only AFTER the named outer timeout is reported exactly once.</summary>
    [Fact]
    public Task OuterFixtureDisposal_LateRequestFault_AfterNamedTimeout_IsReportedOnceInDeferredDiagnostics() =>
        RunHeldRequestAsync(new InvalidOperationException("distinct late request fault"));

    /// <summary>SUCCESSFUL RESCUE control on the same held-request path: no deferred fault is reported.</summary>
    [Fact]
    public Task OuterFixtureDisposal_LateRequestSuccess_AfterNamedTimeout_ReportsNoDeferredFault() =>
        RunHeldRequestAsync(null);

    /// <summary>
    /// ORDINARY FAULT: the scenario observes the actual request fault BEFORE fixture teardown. Teardown then
    /// succeeds and does not report that exception a second time (no timeout, nothing deferred).
    /// </summary>
    [Fact]
    public async Task OuterFixtureDisposal_OrdinaryRequestFault_ObservedByTheScenario_IsNotReportedByTeardown()
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var fault = new InvalidOperationException("ordinary request fault handled by the scenario");
        var gate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.RescueWith(() => gate.TrySetException(fault));
        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (manager, accessor) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var active = await Bounded(run.Track(sut.Acquire(accessor, CircuitState(OriginalId))));
            Assert.True(active.IsAuthorized);
            var request = sut.Acquire(accessor, gate.Task);
            _ = run.Track(JoinExpectedFaultAsync(request, fault));
            Assert.False(request.IsCompleted);
            gate.SetException(fault);
            Assert.Same(fault, await Assert.ThrowsAsync<InvalidOperationException>(() => Bounded(request)));

            Assert.True(request.IsFaulted, "the scenario handled the actual request fault before disposal");
            Assert.False(IsDisposed(sut.ProviderOf(accessor)));
            await run.Begin().WaitAsync(ProbeGuard, CancellationToken.None);

            Assert.DoesNotContain(sut.CleanupLog, entry => entry.Contains("timed-out", StringComparison.Ordinal)
                                                         || entry.StartsWith("deferred", StringComparison.Ordinal));
            Assert.Empty(sut.DeferredFailures);
            Assert.True(sut.DeferredCleanup.IsCompletedSuccessfully);
            AssertManagerFinished(manager, active.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(accessor)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    [Fact]
    public async Task OuterFixtureDisposal_WithoutAStalledRequest_CompletesManagerProviderAndFixtureResourceTeardown()
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (firstManager, first) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var (secondManager, second) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var firstAccess = await Bounded(run.Track(sut.Acquire(first, CircuitState(OriginalId))));
            var secondAccess = await Bounded(run.Track(sut.Acquire(second, CircuitState(OriginalId))));
            Assert.True(firstAccess.IsAuthorized);
            Assert.True(secondAccess.IsAuthorized);
            await run.Begin().WaitAsync(ProbeGuard, CancellationToken.None);

            var log = sut.CleanupLog;
            foreach (var group in new[] { "group-0", "group-1" })
            {
                Assert.Contains($"manager-shutdown:{group}:done", log);
                Assert.Contains($"provider-dispose:{group}:done", log);
            }
            Assert.DoesNotContain(log, entry => entry.Contains("timed-out", StringComparison.Ordinal)
                                               || entry.StartsWith("deferred", StringComparison.Ordinal));
            Assert.True(sut.DeferredCleanup.IsCompletedSuccessfully);
            AssertManagerFinished(firstManager, firstAccess.Runtime!);
            AssertManagerFinished(secondManager, secondAccess.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(first)));
            Assert.True(IsDisposed(sut.ProviderOf(second)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    [Theory]
    [InlineData("manager-shutdown", false)]
    [InlineData("provider-dispose", false)]
    [InlineData("manager-shutdown", true)]
    [InlineData("provider-dispose", true)]
    public async Task OuterFixtureDisposal_HeldCleanupStage_RetainsDependentsUntilActualQuiescence(string stage, bool deferred)
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var auth = new AuthenticationState(FacadeAuthorizationHarness.Principal(OriginalId));
        var requestGate = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new ManagerShutdownProbe();
        var providerFault = new InvalidOperationException("late DI-owned disposal fault");
        var holdManager = stage == "manager-shutdown";
        var provider = new ProviderDisposalProbe(holdManager ? null : providerFault);
        var step = $"{(deferred ? "deferred-" : "")}{stage}:group-0";
        run.RescueWith(() => requestGate.TrySetResult(auth));
        run.RescueWith(() => shutdown.Release.TrySetResult());
        run.RescueWith(() => provider.Release.TrySetResult());
        var hold = run.HoldDeadline(step);

        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (manager, accessor) = sut.Build("ollama-local", sut.NewCircuitAccessor(), disposalProbe: provider);
            var (safeManager, safeAccessor) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            var active = await Bounded(run.Track(sut.Acquire(accessor, CircuitState(OriginalId))));
            var safe = await Bounded(run.Track(sut.Acquire(safeAccessor, CircuitState(OriginalId))));
            Assert.True(active.IsAuthorized);
            Assert.True(safe.IsAuthorized);
            if (holdManager)
            {
                manager.OnJoiningOperation = shutdown.HoldJoin;
                provider.Release.SetResult();
            }
            var request = deferred ? run.Track(sut.Acquire(accessor, requestGate.Task)) : null;
            _ = run.Begin();
            if (request is not null)
            {
                Assert.False(request.IsCompleted);
                await run.ExpireAsync("request-join:group-0");
                await run.ExpectTimeoutAsync("request-join:group-0");
                Assert.False(request.IsCompleted);
                AssertManagerFinished(safeManager, safe.Runtime!);
                Assert.True(IsDisposed(sut.ProviderOf(safeAccessor)));
                requestGate.SetResult(auth);
                Assert.True((await Bounded(request)).IsAuthorized);
            }

            await (holdManager ? shutdown.Entered.Task : provider.Entered.Task).WaitAsync(ProbeGuard, CancellationToken.None);
            var group = sut._groupOf[accessor];
            var retained = await run.ExpireAndIdentifyAsync(step, hold, () => holdManager ? group.Shutdown : group.Disposal);
            _ = run.Track(JoinExpectedFaultAsync(retained, holdManager ? null : providerFault));
            hold.Release.SetResult(); // handler runs before the still-held manager or provider is released
            if (deferred)
                await sut.WaitForCleanupEntryAsync($"{step}:retained");
            else
                await run.ExpectTimeoutAsync(step);

            Assert.Contains($"{step}:timed-out", sut.CleanupLog);
            AssertManagerFinished(safeManager, safe.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(safeAccessor)), "the later independent group must really be disposed before rescue");
            AssertSharedResourcesAlive(sut);
            Assert.False(retained.IsCompleted, $"{step} remains owned after its waiter expires");
            if (holdManager)
            {
                Assert.False(active.Runtime!.Composer.TeardownCompletion.IsCompleted);
                Assert.False(provider.Entered.Task.IsCompleted, "provider disposal cannot start while its manager owns live work");
                Assert.False(IsDisposed(sut.ProviderOf(accessor)));
                shutdown.Release.SetResult();
            }
            else
            {
                AssertManagerFinished(manager, active.Runtime!);
                Assert.False(provider.Exited);
                provider.Release.SetResult();
            }

            var expected = new List<Action<Exception>>();
            if (deferred) expected.Add(ex => Assert.Contains($"'{step}'", Assert.IsType<TimeoutException>(ex).Message, StringComparison.Ordinal));
            if (!holdManager) expected.Add(ex => Assert.Same(providerFault, ex));
            await run.JoinDeferredAsync([.. expected]);
            Assert.Same(retained, holdManager ? group.Shutdown : group.Disposal);
            Assert.True(retained.IsCompleted);
            AssertManagerFinished(manager, active.Runtime!);
            Assert.True(provider.Exited);
            Assert.Equal(1, provider.Calls);
            Assert.True(IsDisposed(sut.ProviderOf(accessor)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    // Deadline wins while the REAL operation is pending; the real deadline continuation is held; the SAME
    // operation then completes (success or fault) BEFORE that continuation is released.
    private static async Task RunDeadlineWonThenWorkSettledAsync(string stage, bool workFaults)
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var holdManager = stage == "manager-shutdown";
        var shutdown = new ManagerShutdownProbe();
        var fault = new InvalidOperationException("work fault after its deadline won");
        var provider = new ProviderDisposalProbe(workFaults ? fault : null);
        var step = $"{stage}:group-0";
        run.RescueWith(() => shutdown.Release.TrySetResult());
        run.RescueWith(() => provider.Release.TrySetResult());
        var hold = run.HoldDeadline(step);

        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (manager, accessor) = sut.Build("ollama-local", sut.NewCircuitAccessor(), disposalProbe: provider);
            var active = await Bounded(run.Track(sut.Acquire(accessor, CircuitState(OriginalId))));
            Assert.True(active.IsAuthorized);
            if (holdManager)
            {
                manager.OnJoiningOperation = shutdown.HoldJoin;
                provider.Release.SetResult();
            }

            _ = run.Begin();
            await (holdManager ? shutdown.Entered.Task : provider.Entered.Task).WaitAsync(ProbeGuard, CancellationToken.None);
            var group = sut._groupOf[accessor];
            // Provider.Entered can run before the call returns the task to the fixture. Wait for
            // the actual deadline winner to identify the owned task before either work release.
            var work = await run.ExpireAndIdentifyAsync(step, hold, () => holdManager ? group.Shutdown : group.Disposal);
            _ = run.Track(JoinExpectedFaultAsync(work, workFaults ? fault : null));
            if (holdManager)
            {
                Assert.False(active.Runtime!.Composer.TeardownCompletion.IsCompleted);
                Assert.NotEmpty(ManagerEntries(manager));
                Assert.False(provider.Entered.Task.IsCompleted, "provider disposal must not start while its manager is held");
                Assert.False(IsDisposed(sut.ProviderOf(accessor)));
            }
            else
            {
                AssertManagerFinished(manager, active.Runtime!);
                Assert.False(provider.Exited);
            }
            Assert.DoesNotContain($"{step}:timed-out", sut.CleanupLog);
            Assert.False(IsDisposed(sut._goalDb));

            // The SAME operation completes BEFORE the held handler resumes.
            if (holdManager) shutdown.Release.SetResult(); else provider.Release.SetResult();
            if (workFaults)
            {
                var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => work.WaitAsync(ProbeGuard, CancellationToken.None));
                Assert.Same(fault, actual);
            }
            else
            {
                await work.WaitAsync(ProbeGuard, CancellationToken.None);
            }
            Assert.True(work.IsCompleted);
            Assert.Equal(workFaults ? TaskStatus.Faulted : TaskStatus.RanToCompletion, work.Status);
            Assert.DoesNotContain($"{step}:timed-out", sut.CleanupLog);
            Assert.DoesNotContain($"{step}:done", sut.CleanupLog);
            Assert.False(run.Begin().IsCompleted, "the held deadline handler keeps outer disposal in progress");
            Assert.False(hold.Release.Task.IsCompleted);
            Assert.False(IsDisposed(sut._goalDb));
            if (holdManager)
                Assert.False(provider.Entered.Task.IsCompleted, "provider disposal must not pass the held deadline handler");

            hold.Release.SetResult();

            // The NAMED timeout remains visible although the work has already completed.
            await run.ExpectTimeoutAsync(step);
            Assert.Single(sut.CleanupLog, entry => entry == $"{step}:timed-out");
            Assert.DoesNotContain($"{step}:done", sut.CleanupLog);
            Assert.DoesNotContain($"{step}:faulted", sut.CleanupLog);

            if (workFaults) await run.JoinDeferredAsync(ex => Assert.Same(fault, ex));
            else await run.JoinDeferredAsync();
            Assert.Equal(workFaults ? 1 : 0, sut.DeferredFailures.Count(f => ReferenceEquals(f, fault)));
            Assert.Same(work, holdManager ? group.Shutdown : group.Disposal);
            Assert.Equal(1, provider.Calls);
            var final = sut.CleanupLog.ToList();
            Assert.True(final.IndexOf("deferred:quiesced") >= 0);
            Assert.Contains("deferred:complete", final);
            AssertManagerFinished(manager, active.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(accessor)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    // Work wins before its deadline; the deadline continuation must never be reached.
    private static async Task RunWorkWonBeforeDeadlineAsync(string stage, Exception? workFault)
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var holdManager = stage == "manager-shutdown";
        var shutdown = new ManagerShutdownProbe();
        var provider = new ProviderDisposalProbe(workFault);
        var step = $"{stage}:group-0";
        run.RescueWith(() => shutdown.Release.TrySetResult());
        run.RescueWith(() => provider.Release.TrySetResult());
        var hold = run.HoldDeadline(step);

        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (manager, accessor) = sut.Build("ollama-local", sut.NewCircuitAccessor(), disposalProbe: provider);
            var active = await Bounded(run.Track(sut.Acquire(accessor, CircuitState(OriginalId))));
            Assert.True(active.IsAuthorized);
            if (holdManager)
            {
                manager.OnJoiningOperation = shutdown.HoldJoin;
                provider.Release.SetResult();
            }

            _ = run.Begin();
            await (holdManager ? shutdown.Entered.Task : provider.Entered.Task).WaitAsync(ProbeGuard, CancellationToken.None);
            await run.WaitArmedAsync(step);
            var group = sut._groupOf[accessor];
            var work = Assert.IsAssignableFrom<Task>(holdManager ? group.Shutdown : group.Disposal);
            _ = run.Track(JoinExpectedFaultAsync(work, workFault));
            Assert.False(work.IsCompleted);

            // The work finishes while the deadline has NOT fired.
            if (holdManager) shutdown.Release.SetResult(); else provider.Release.SetResult();

            if (workFault is null)
            {
                await work.WaitAsync(ProbeGuard, CancellationToken.None);
                Assert.True(work.IsCompletedSuccessfully);
                await run.Begin().WaitAsync(ProbeGuard, CancellationToken.None);
                Assert.Contains($"{step}:done", sut.CleanupLog);
                Assert.True(sut.DeferredCleanup.IsCompletedSuccessfully);
            }
            else
            {
                var reported = await Assert.ThrowsAsync<TimeoutException>(() => work.WaitAsync(ProbeGuard, CancellationToken.None));
                Assert.Same(workFault, reported);
                await run.ExpectFaultAsync(workFault);
                Assert.DoesNotContain("Outer fixture teardown step", reported.Message, StringComparison.Ordinal);
                Assert.Contains($"{step}:faulted", sut.CleanupLog);
            }

            Assert.Same(work, holdManager ? group.Shutdown : group.Disposal);
            Assert.False(hold.Held.Task.IsCompleted, "the fixture deadline never won");
            Assert.DoesNotContain(sut.CleanupLog, entry => entry.Contains("timed-out", StringComparison.Ordinal)
                                                         || entry.StartsWith("deferred", StringComparison.Ordinal));
            Assert.Empty(sut.DeferredFailures);
            Assert.True(sut.DeferredCleanup.IsCompletedSuccessfully);
            Assert.Equal(1, provider.Calls);
            AssertManagerFinished(manager, active.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(accessor)));
            AssertSharedResourcesDisposed(sut);
        });
    }

    /// <summary>DEADLINE WINS; the held handler resumes after the work SUCCEEDED. The named timeout stays visible.</summary>
    [Theory]
    [InlineData("manager-shutdown")]
    [InlineData("provider-dispose")]
    public Task OuterFixtureDisposal_DeadlineWins_HandlerResumesAfterWorkSucceeds_StillReportsNamedTimeout(string stage) =>
        RunDeadlineWonThenWorkSettledAsync(stage, workFaults: false);

    /// <summary>DEADLINE WINS; the held handler resumes after the work FAULTED. Both are reported, once each.</summary>
    [Fact]
    public Task OuterFixtureDisposal_DeadlineWins_HandlerResumesAfterWorkFaults_ReportsNamedTimeoutAndExactFaultOnce() =>
        RunDeadlineWonThenWorkSettledAsync("provider-dispose", workFaults: true);

    /// <summary>WORK WINS before the deadline with success: no fixture deadline diagnostic.</summary>
    [Theory]
    [InlineData("manager-shutdown")]
    [InlineData("provider-dispose")]
    public Task OuterFixtureDisposal_WorkWinsBeforeDeadline_Success_ProducesNoDeadlineDiagnostic(string stage) =>
        RunWorkWonBeforeDeadlineAsync(stage, workFault: null);

    /// <summary>WORK WINS with its OWN TimeoutException: reported as the work's fault, not as a fixture deadline.</summary>
    [Fact]
    public Task OuterFixtureDisposal_WorkWinsBeforeDeadline_WorkOriginatedTimeoutException_IsNotMistakenForTheFixtureDeadline() =>
        RunWorkWonBeforeDeadlineAsync("provider-dispose", new TimeoutException("work-originated timeout"));

    [Fact]
    public async Task OuterFixtureDisposal_FaultedProvider_KeepsFaultAndStillDisposesLaterGroupAndSharedResources()
    {
        var run = new OuterDisposalScenario();
        var sut = run.Fixture;
        var fault = new InvalidOperationException("DI-owned disposal failed");
        var provider = new ProviderDisposalProbe(fault);
        run.RescueWith(() => provider.Release.TrySetResult());
        await run.RunAsync(async () =>
        {
            await sut.InitializeAsync();
            var (_, first) = sut.Build("ollama-local", sut.NewCircuitAccessor(), disposalProbe: provider);
            var (laterManager, later) = sut.Build("ollama-local", sut.NewCircuitAccessor());
            Assert.True((await Bounded(run.Track(sut.Acquire(first, CircuitState(OriginalId))))).IsAuthorized);
            var laterAccess = await Bounded(run.Track(sut.Acquire(later, CircuitState(OriginalId))));
            Assert.True(laterAccess.IsAuthorized);
            _ = run.Begin();
            await provider.Entered.Task.WaitAsync(ProbeGuard, CancellationToken.None);
            Assert.False(IsDisposed(sut.ProviderOf(later)));
            Assert.NotEmpty(ManagerEntries(laterManager));
            provider.Release.SetResult();
            await run.ExpectFaultAsync(fault);
            Assert.Contains("provider-dispose:group-0:faulted", sut.CleanupLog);
            AssertManagerFinished(laterManager, laterAccess.Runtime!);
            Assert.True(IsDisposed(sut.ProviderOf(later)));
            Assert.True(sut.DeferredCleanup.IsCompletedSuccessfully);
            AssertSharedResourcesDisposed(sut);
        });
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
        string officialProvider, CircuitUserPrincipalAccessor? circuitAccessor, Func<bool>? tokenAvailable = null,
        ProviderDisposalProbe? disposalProbe = null)
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
        if (disposalProbe is not null)
            services.AddSingleton(_ => disposalProbe); // factory-created instances are owned by DI

        var provider = services.BuildServiceProvider();
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

        var accessor = new ComposerRuntimeAccessor(
            manager,
            circuitAccessor ?? new CircuitUserPrincipalAccessor(_resolver, _policy),
            _resolver,
            _policy,
            NullLogger<ComposerRuntimeAccessor>.Instance);

        lock (_lifecycleGate)
        {
            var group = new FixtureGroup($"group-{_groups.Count}", provider, manager);
            _groups.Add(group);
            _groupOf[accessor] = group;
        }

        if (disposalProbe is not null)
            _ = provider.GetRequiredService<ProviderDisposalProbe>();
        return (manager, accessor);
    }

    /// <summary>
    /// STARTS and REGISTERS one request atomically against the group whose accessor it uses. Admission is
    /// checked BEFORE the request starts, so once teardown has closed admission no request can begin
    /// unowned.
    /// </summary>
    private Task<ComposerRuntimeAccess> Register(
        ComposerRuntimeAccessor accessor, Func<Task<ComposerRuntimeAccess>> start)
    {
        lock (_lifecycleGate)
        {
            if (_requestsClosed)
                throw new InvalidOperationException("Composer fixture request admission is closed.");
            var task = start();
            _groupOf[accessor].Requests.Add(task);
            return task;
        }
    }

    private Task<ComposerRuntimeAccess> Acquire(ComposerRuntimeAccessor accessor, ClaimsPrincipal principal) =>
        Register(accessor, () => accessor.AcquireForRequestAsync(principal, Ct));

    private Task<ComposerRuntimeAccess> ConnectCircuit(
        ComposerRuntimeAccessor accessor, Task<AuthenticationState>? state) =>
        Register(accessor, () => accessor.ConnectForCircuitAsync(state, Ct));

    private Task<ComposerRuntimeAccess> Acquire(
        ComposerRuntimeAccessor accessor, Task<AuthenticationState>? state) =>
        Register(accessor, () => accessor.AcquireForCircuitAsync(state, Ct));

    private Task<ComposerRuntimeAccess> Connect(ComposerRuntimeAccessor accessor, ClaimsPrincipal principal) =>
        Register(accessor, () => accessor.ConnectForRequestAsync(principal, Ct));

    private static Task<AuthenticationState> CircuitState(string gitHubId) =>
        Task.FromResult(new AuthenticationState(FacadeAuthorizationHarness.Principal(gitHubId)));

    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    /// <summary>
    /// THE FIXTURE TEARDOWN xUnit invokes (it calls <c>DisposeAsync</c>, not the synchronous
    /// <c>Dispose</c>, when both exist). Every wait is BOUNDED by <see cref="OuterJoinTimeout"/> and none
    /// observes a scenario token.
    /// <list type="number">
    /// <item>Request admission is CLOSED first.</item>
    /// <item>Each group's requests are joined with a bound. A request that does not finish makes the
    /// timeout a NAMED fault, RETAINS the unfinished work and marks its group BLOCKED — a wait expiring
    /// does not mean the work stopped.</item>
    /// <item>Groups with no unfinished work still get their manager shut down and provider disposed, and
    /// later steps run even after an earlier timeout or fault.</item>
    /// <item>Anything a blocked group still depends on (its manager/provider and the shared goal db,
    /// user directory and temp directory) is DEFERRED to <see cref="DeferredCleanup"/>, which runs only
    /// after the retained work quiesces, so disposal can never race live work.</item>
    /// </list>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        FixtureGroup[] groups;
        lock (_lifecycleGate)
        {
            if (_disposeStarted) return;
            _disposeStarted = true;
            _requestsClosed = true;
            groups = [.. _groups];
        }

        var failures = new List<Exception>();

        // Request assertion failures belong to the scenario. The outer sweep observes them without
        // duplicating them, but its own timeout must be reported even if the request later succeeds.
        foreach (var group in groups)
        {
            foreach (var request in group.Requests)
            {
                var observed = request.ContinueWith(
                    task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (!await AwaitBoundedAsync($"request-join:{group.Name}", observed, failures))
                {
                    group.Unfinished.Add(observed);
                    group.LateRequests.Add(request);
                }
            }
        }

        // Start each operation once. If it outlives the deadline, the eventual continuation joins
        // that exact task instead of invoking disposal twice or assuming a timeout stopped the work.
        foreach (var group in groups.Where(g => g.Unfinished.Count == 0))
        {
            group.Shutdown = Start(group.Manager.ShutdownAsync);
            if (!await AwaitBoundedAsync($"manager-shutdown:{group.Name}", group.Shutdown, failures))
            {
                group.Unfinished.Add(group.Shutdown);
                continue;
            }

            group.Disposal = Start(() => group.Provider.DisposeAsync().AsTask());
            if (!await AwaitBoundedAsync($"provider-dispose:{group.Name}", group.Disposal, failures))
                group.Unfinished.Add(group.Disposal);
        }

        var blocked = groups.Where(g => g.Unfinished.Count != 0).ToArray();
        if (blocked.Length == 0)
        {
            DisposeShared(failures);
        }
        else
        {
            foreach (var group in blocked) Log($"deferred:{group.Name}");
            Log("deferred:shared-resources");
            DeferredCleanup = RunDeferredAsync(blocked);
        }

        if (failures.Count > 0)
            throw new AggregateException("Accessor contract test cleanup failed", failures);
    }

    private static Task Start(Func<Task> action)
    {
        try { return action(); }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    /// <summary>
    /// Bounds one teardown step and PRESERVES WHICH SIGNAL ACTUALLY WON. The winner is decided by
    /// <see cref="Task.WhenAny(Task, Task)"/> between the work and the step's own deadline, so it is
    /// independent of what the work task looks like when this continuation later resumes:
    /// <list type="bullet">
    /// <item><b>Work wins</b>: its success, or its ORIGINAL exception (even a <see cref="TimeoutException"/>
    /// the work itself threw), is reported as the work's own outcome; no fixture deadline is invented.</item>
    /// <item><b>Deadline wins</b>: the NAMED timeout is reported even if the work has completed by the time
    /// this continuation resumes. A fault of that work is NOT reported here — the caller retains the work
    /// and reports its exact exception once after quiescence — and actual quiescence alone decides when
    /// dependent cleanup is safe (the caller treats the step as unfinished).</item>
    /// </list>
    /// </summary>
    private async Task<bool> AwaitBoundedAsync(string step, Task work, List<Exception> sink)
    {
        using var stopDeadline = new CancellationTokenSource();
        var deadline = Task.Delay(OuterJoinTimeout, OuterClockForStep(step), stopDeadline.Token);
        var winner = await Task.WhenAny(work, deadline);

        if (!ReferenceEquals(winner, deadline))
        {
            stopDeadline.Cancel();
            try
            {
                await work;
                Log($"{step}:done");
            }
            catch (Exception ex)
            {
                sink.Add(ex);
                Log($"{step}:faulted");
            }
            return true;
        }

        if (OnDeadlineWon is { } onDeadline)
        {
            try { await onDeadline(step, work); }
            catch (Exception) { /* observation must never change teardown */ }
        }

        sink.Add(new TimeoutException(
            $"Outer fixture teardown step '{step}' timed out after {OuterJoinTimeout}; " +
            "the work was NOT stopped, is retained, and dependent cleanup is deferred."));
        Log($"{step}:timed-out");
        return false;
    }

    // The shared fixture resources. Each step is attempted even when an earlier one faults.
    private void DisposeShared(List<Exception> sink)
    {
        void Step(string name, Action action)
        {
            try { action(); Log($"{name}:done"); }
            catch (Exception ex) { sink.Add(ex); Log($"{name}:faulted"); }
        }

        Step("goal-db", _goalDb.Dispose);
        // xUnit invokes DisposeAsync, not the synchronous Dispose, when both exist.
        Step("auth-harness", _auth.Dispose);
        Step("directory", () => { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); });
    }

    // This retained continuation is not an outer xUnit wait. It advances ONLY after the actual
    // task settles; otherwise resources stay owned by the continuation. Independent groups can
    // finish while one group is still parked, but shared cleanup is behind all groups.
    private async Task RunDeferredAsync(FixtureGroup[] blocked)
    {
        await Task.WhenAll(blocked.Select(ResumeGroupAsync));
        var sink = new List<Exception>();
        DisposeShared(sink);
        lock (_lifecycleGate) _deferredFailures.AddRange(sink);
        Log("deferred:complete");
    }

    private async Task ResumeGroupAsync(FixtureGroup group)
    {
        foreach (var work in group.Unfinished)
            await AfterQuiescenceAsync(work);
        // Only a request still pending at its outer timeout reaches here, so a fault the scenario already
        // observed before teardown is never reported a second time. The exact exception is added once.
        foreach (var request in group.LateRequests)
            await AfterQuiescenceAsync(request);
        Log("deferred:quiesced");

        if (group.Shutdown is null)
        {
            group.Shutdown = Start(group.Manager.ShutdownAsync);
            await ObserveDeferredStepAsync($"deferred-manager-shutdown:{group.Name}", group.Shutdown);
        }
        if (group.Disposal is null)
        {
            group.Disposal = Start(() => group.Provider.DisposeAsync().AsTask());
            await ObserveDeferredStepAsync($"deferred-provider-dispose:{group.Name}", group.Disposal);
        }
    }

    private async Task ObserveDeferredStepAsync(string step, Task work)
    {
        var sink = new List<Exception>();
        var finished = await AwaitBoundedAsync(step, work, sink);
        lock (_lifecycleGate) _deferredFailures.AddRange(sink);
        if (!finished)
        {
            Log($"{step}:retained");
            // Reporting the deadline is bounded. The continuation still owns the underlying task
            // and must not advance to dependent disposal until that actual task has settled.
            await AfterQuiescenceAsync(work);
            Log($"{step}:quiesced");
        }
    }

    private async Task AfterQuiescenceAsync(Task work)
    {
        try { await work; }
        catch (Exception ex)
        {
            lock (_lifecycleGate)
            {
                if (!_deferredFailures.Any(existing => ReferenceEquals(existing, ex))) _deferredFailures.Add(ex);
            }
        }
    }
}
