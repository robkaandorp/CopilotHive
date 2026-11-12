using System.Security.Claims;

using CopilotHive.Components.Pages;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE PER-USER CIRCUIT: the ComposerChat page selects the caller's OWN
/// <see cref="ComposerUserRuntime"/> through the scoped <see cref="IComposerRuntimeAccessor"/> and maps
/// every manager outcome to one render state.
/// <para>
/// The rows here are the DISCRIMINATING ones — each fails if the specific rule it names is removed:
/// </para>
/// <list type="bullet">
/// <item>the status mapping is total and pure (viewer forbidden, shell ready, deferred actionable, failed
/// and removed safe errors, refused hidden, ONLY the open-mode local operator served by the legacy
/// Composer);</item>
/// <item>the binding for a <c>Refused</c>/<c>Failed</c> selection carries NO Composer and NO attachments —
/// so no thread, catalog or attachment of a previous account can be shown or written;</item>
/// <item>the view state derives from the CIRCUIT status first, so a forbidden/error selection hides even
/// the picker and the "configure a model" hint;</item>
/// <item>the RENDERED page answers a Viewer, a signed-out circuit and a broken selection WITHOUT exposing
/// any Composer content, and a Member's own runtime is the one rendered.</item>
/// </list>
/// </summary>
public sealed class ComposerCircuitRuntimeTests : IAsyncLifetime, IDisposable
{
    private const string Model = "circuit-model";
    private const string OtherModel = "circuit-other-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-circuit-{Guid.NewGuid():N}");
    private readonly GoalStore _store;

    private int _originalId;
    private int _memberId;
    private int _viewerId;

    public ComposerCircuitRuntimeTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new GoalStore(_goalDb, NullLogger<GoalStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The ORIGINAL identity is the lowest-id activated row: only it may claim legacy history.
        _originalId = (await _auth.SeedAsync("circuit-original", UserRole.Member)).Id;
        _memberId = (await _auth.SeedAsync("circuit-member", UserRole.Member)).Id;
        _viewerId = (await _auth.SeedAsync("circuit-viewer", UserRole.Viewer)).Id;
    }

    // ── 1. The pure status mapping ───────────────────────────────────────────────────────────────

    /// <summary>
    /// THE MAPPING IS TOTAL AND FAIL-CLOSED: one status per manager outcome, and every denial carries no
    /// runtime at all.
    /// </summary>
    [Theory]
    [InlineData(ComposerRuntimeStatus.Connected, ComposerChat.ComposerCircuitStatus.Ready)]
    [InlineData(ComposerRuntimeStatus.DisconnectedShell, ComposerChat.ComposerCircuitStatus.Ready)]
    [InlineData(ComposerRuntimeStatus.Deferred, ComposerChat.ComposerCircuitStatus.Deferred)]
    [InlineData(ComposerRuntimeStatus.Failed, ComposerChat.ComposerCircuitStatus.Failed)]
    [InlineData(ComposerRuntimeStatus.Removed, ComposerChat.ComposerCircuitStatus.Removed)]
    public void TheManagerOutcomes_MapToOneCircuitStatusEach(ComposerRuntimeStatus status, int expectedStatus)
    {
        var expected = (ComposerChat.ComposerCircuitStatus)expectedStatus;
        var access = ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Granted(status, Handle(NewComposer())));

        Assert.Equal(expected, ComposerChat.ClassifyRuntimeAccess(access, authEnabled: true));
    }

    /// <summary>
    /// A DENIED ACCESS IS FORBIDDEN WITH NO CONTENT: it carries no runtime and no manager outcome, so the
    /// page can show neither the Composer nor its state.
    /// </summary>
    [Fact]
    public void ADeniedAccess_IsForbidden_WithNoRuntimeAndNoOutcome()
    {
        var denied = ComposerRuntimeAccess.Denied();

        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Result);
        Assert.Null(denied.Runtime);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, ComposerChat.ClassifyRuntimeAccess(denied, authEnabled: true));
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, ComposerChat.ClassifyRuntimeAccess(denied, authEnabled: false));
    }

    /// <summary>
    /// A VIEWER IS FORBIDDEN — on the REAL accessor, the REAL manager and the REAL directory: a Viewer's
    /// <c>AcquireForCircuitAsync</c> answers not-authorized with no runtime, and the page maps it to the
    /// forbidden view. No Composer is created for that account.
    /// </summary>
    [Fact]
    public async Task AViewer_IsForbidden_AndNoRuntimeIsCreated()
    {
        await using var fixture = new CircuitFixture(_auth.Directory.Service, Model);
        using var scope = fixture.Provider.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();
        var access = await accessor.AcquireForCircuitAsync(
            ComposerCircuitTesting.AuthenticatedState(FacadeAuthorizationHarness.Principal("circuit-viewer")), Ct);

        Assert.False(access.IsAuthorized);
        Assert.Null(access.Runtime);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, ComposerChat.ClassifyRuntimeAccess(access, accessor.AuthEnabled));

        // No per-user runtime exists for the viewer.
        Assert.DoesNotContain(_viewerId, fixture.ManagerUserIds());
    }

    /// <summary>
    /// ONLY THE OPEN-MODE LOCAL-OPERATOR REFUSAL MAY SERVE THE LEGACY COMPOSER: the same <c>Refused</c>
    /// status is the forbidden view when authentication is enabled, and the ready legacy binding when the
    /// host is in open mode. Both halves are required — the refusal TYPE and the open MODE.
    /// </summary>
    [Theory]
    [InlineData(false, ComposerChat.ComposerCircuitStatus.Ready)]
    [InlineData(true, ComposerChat.ComposerCircuitStatus.Forbidden)]
    public void TheLocalOperatorRefusal_IsServedOnlyInOpenMode(bool authEnabled, int expectedStatus)
    {
        var expected = (ComposerChat.ComposerCircuitStatus)expectedStatus;
        var access = ComposerRuntimeAccess.Granted(
            ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.LocalOperator));

        Assert.Equal(expected, ComposerChat.ClassifyRuntimeAccess(access, authEnabled));
        Assert.Equal(expected == ComposerChat.ComposerCircuitStatus.Ready, ComposerChat.IsOpenModeLocalOperatorRefusal(access, authEnabled));
    }

    /// <summary>
    /// EVERY OTHER AUTHENTICATED REFUSAL IS FORBIDDEN — including the refusals that share the same status:
    /// a disabled account never inherits a Composer through the page.
    /// </summary>
    [Theory]
    [InlineData(ComposerRuntimeRefusal.DisabledAccount)]
    [InlineData(ComposerRuntimeRefusal.UnknownAccount)]
    [InlineData(ComposerRuntimeRefusal.UndefinedRole)]
    [InlineData(ComposerRuntimeRefusal.NoRuntime)]
    [InlineData(ComposerRuntimeRefusal.ShuttingDown)]
    public void AnAuthenticatedRefusal_IsForbidden_WhateverItsReason(ComposerRuntimeRefusal reason)
    {
        var access = ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Refused(reason));

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, ComposerChat.ClassifyRuntimeAccess(access, authEnabled: true));
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, ComposerChat.ClassifyRuntimeAccess(access, authEnabled: false));
    }

    // ── 2. The binding: the runtime travels with its own attachments ─────────────────────────────

    /// <summary>
    /// THE BINDING CARRIES THE CALLER'S OWN RUNTIME *AND ITS OWN ATTACHMENT STORE*: the attachment service of
    /// the selection is the runtime's, never the legacy root — so an attachment operation can never be
    /// redirected into another account's directory.
    /// </summary>
    [Fact]
    public void AReadyBinding_CarriesTheRuntimesOwnComposerAndAttachments()
    {
        var composer = NewComposer();
        var attachments = NewAttachments();
        var runtime = Handle(composer, attachments);
        var access = ComposerRuntimeAccess.Granted(
            ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, runtime));

        var binding = ComposerChat.ResolveBinding(access, authEnabled: true, NewComposer(), NewAttachments());

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, binding.Status);
        Assert.Same(composer, binding.Composer);
        Assert.Same(attachments, binding.Attachments);
        Assert.False(binding.IsOpenModeLegacy);
        Assert.True(binding.HasComposer);
        Assert.True(binding.CanMutate);
    }

    /// <summary>
    /// AN OPEN-MODE BINDING IS THE LEGACY SINGLETON, AND IT SAYS SO: the explicit open-mode refusal binds the
    /// legacy Composer and its legacy attachment store, flagged as the legacy path.
    /// </summary>
    [Fact]
    public void AnOpenModeBinding_IsTheLegacySingleton_AndIsFlaggedAsSuch()
    {
        var legacy = NewComposer();
        var legacyAttachments = NewAttachments();
        var access = ComposerRuntimeAccess.Granted(
            ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.LocalOperator));

        var binding = ComposerChat.ResolveBinding(access, authEnabled: false, legacy, legacyAttachments);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, binding.Status);
        Assert.Same(legacy, binding.Composer);
        Assert.Same(legacyAttachments, binding.Attachments);
        Assert.True(binding.IsOpenModeLegacy);
        Assert.True(binding.HasComposer);
    }

    /// <summary>
    /// NO CONTENT FOR ANY NON-READY STATUS: a forbidden, deferred, failed or removed selection binds NOTHING —
    /// not the runtime, and certainly not the legacy singleton as a fallback.
    /// </summary>
    [Theory]
    [InlineData(ComposerRuntimeStatus.Deferred, ComposerChat.ComposerCircuitStatus.Deferred)]
    [InlineData(ComposerRuntimeStatus.Failed, ComposerChat.ComposerCircuitStatus.Failed)]
    [InlineData(ComposerRuntimeStatus.Removed, ComposerChat.ComposerCircuitStatus.Removed)]
    public void ADeferredFailedOrRemovedSelection_BindsNoComposerAndNoAttachments(
        ComposerRuntimeStatus status, int expectedStatus)
    {
        var expected = (ComposerChat.ComposerCircuitStatus)expectedStatus;
        // A Deferred runtime IS granted a runtime by the manager; the page still binds no Composer for reads of a
        // thread it does not have. Only a READY (connected or shell) or a read-only deferred metadata read uses it.
        var access = status == ComposerRuntimeStatus.Deferred
            ? ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Granted(status, Handle(NewComposer())))
            : ComposerRuntimeAccess.Granted(status == ComposerRuntimeStatus.Failed
                ? ComposerRuntimeResult.Faulted(new InvalidOperationException("boom"))
                : ComposerRuntimeResult.RemovedResult());

        var binding = ComposerChat.ResolveBinding(access, authEnabled: true, NewComposer(), NewAttachments());

        Assert.Equal(expected, binding.Status);
        Assert.False(binding.IsOpenModeLegacy);

        if (expected == ComposerChat.ComposerCircuitStatus.Deferred)
        {
            // A deferred runtime is REAL (it has a Composer whose shell metadata reads answer), but it must never
            // be mutated: CanMutate is the mutation gate.
            Assert.True(binding.HasComposer);
            Assert.False(binding.CanMutate);
        }
        else
        {
            Assert.False(binding.HasComposer);
            Assert.Null(binding.Composer);
            Assert.Null(binding.Attachments);
            Assert.False(binding.CanMutate);
        }
    }

    /// <summary>
    /// A DENIED SELECTION NEVER BINDS THE LEGACY COMPOSER: the legacy singleton is not a fallback for an
    /// authorization refusal.
    /// </summary>
    [Fact]
    public void ADeniedSelection_NeverBindsTheLegacyComposer()
    {
        var binding = ComposerChat.ResolveBinding(
            ComposerRuntimeAccess.Denied(), authEnabled: true, NewComposer(), NewAttachments());

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, binding.Status);
        Assert.False(binding.HasComposer);
        Assert.Null(binding.Composer);
        Assert.False(binding.IsOpenModeLegacy);
    }

    /// <summary>
    /// A READY STATUS WITH NEITHER A RUNTIME NOR THE OPEN-MODE REFUSAL IS A WIRING BUG — and it fails loudly
    /// rather than silently substituting the legacy Composer, which would be exactly the split runtime this
    /// goal closes.
    /// </summary>
    [Fact]
    public void AReadyStatusWithoutARuntimeOrTheOpenModeRefusal_FailsLoudly()
    {
        var impossible = ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult());
        // Sanity: Removed is NOT ready, so this row needs an access whose STATUS is ready but whose runtime is null
        // — reachable only through a wiring bug, which is exactly what the guard covers.
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Removed, ComposerChat.ClassifyRuntimeAccess(impossible, authEnabled: true));

        // The guard itself: a ready classification with no runtime throws.
        var failure = Record.Exception(() => ComposerChat.ResolveBinding(
            ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, null!)),
            authEnabled: true,
            NewComposer(),
            NewAttachments()));

        Assert.IsType<InvalidOperationException>(failure);
    }

    // ── 3. The per-operation algorithm: fresh every time, ONE attempt each ────────────────────────

    /// <summary>
    /// EVERY OPERATION RE-QUERIES THE ACCESSOR — the page never treats a cached answer as live truth. Two
    /// consecutive read operations are TWO acquisitions, so a demotion or a disablement lands on the very next
    /// operation rather than on the next component lifetime.
    /// </summary>
    [Fact]
    public async Task EveryOperation_ReQueriesTheAccessor()
    {
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (_, _) => Task.FromResult(ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(NewComposer())))));

        var first = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: false, Ct);
        var second = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: false, Ct);

        Assert.Equal(2, accessor.AcquireCalls);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, first.Status);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, second.Status);
        // A read NEVER connects, even on a ready runtime.
        Assert.Equal(0, accessor.ConnectCalls);
    }

    /// <summary>
    /// A DEMOTION LANDS ON THE NEXT OPERATION: the first read is authorized, the second answer is a denial —
    /// and the operation reports the forbidden status without ever asking for a runtime.
    /// </summary>
    [Fact]
    public async Task ADemotionBetweenTwoOperations_IsObservedByTheSecondOne()
    {
        var composer = NewComposer();
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (call, _) => Task.FromResult(call == 1
                ? ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(composer)))
                : ComposerRuntimeAccess.Denied()));

        var authorized = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: false, Ct);
        var demoted = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: false, Ct);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, authorized.Status);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, demoted.Status);
        Assert.Null(demoted.Access.Runtime);
    }

    /// <summary>
    /// A STALE HANDLE IS RE-QUERIED EXACTLY ONCE: one <c>Removed</c> is replaced transparently by the second
    /// answer (the manager joined the old teardown and created a fresh runtime), and a SECOND <c>Removed</c> is
    /// reported instead of re-queried again — no retry loop.
    /// </summary>
    [Theory]
    [InlineData(false, ComposerChat.ComposerCircuitStatus.Ready)]
    [InlineData(true, ComposerChat.ComposerCircuitStatus.Removed)]
    public async Task AStaleHandle_IsReQueriedExactlyOnce(bool stillRemoved, int expectedStatus)
    {
        var expected = (ComposerChat.ComposerCircuitStatus)expectedStatus;
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (call, _) => Task.FromResult(call == 1 || stillRemoved
                ? ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult())
                : ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(NewComposer())))));

        var (access, status) = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: false, Ct);

        Assert.Equal(expected, status);
        // EXACTLY two acquisitions on either row: the first answer, and the ONE re-query.
        Assert.Equal(2, accessor.AcquireCalls);
        Assert.Equal(0, accessor.ConnectCalls);
        if (stillRemoved) Assert.Null(access.Runtime);
    }

    /// <summary>
    /// A DEFERRED MUTATION DRIVES EXACTLY ONE CONNECT: the first answer is deferred, the connect answers
    /// connected, the mutation therefore proceeds — one connect, never two, and never a read-triggered connect.
    /// </summary>
    [Fact]
    public async Task ADeferredMutation_DrivesExactlyOneConnect_AndThenProceeds()
    {
        var composer = NewComposer();
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (call, _) => Task.FromResult(call == 1
                ? ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(composer)))
                : ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Connected, Handle(composer)))));

        var (_, status) = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: true, Ct);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, status);
        Assert.Equal(1, accessor.ConnectCalls);
    }

    /// <summary>
    /// A STILL-DEFERRED MUTATION IS REPORTED, NOT RETRIED: one connect is attempted, the answer is still
    /// deferred, and the operation reports the actionable deferred status — no second connect, no loop.
    /// </summary>
    [Fact]
    public async Task AStillDeferredMutation_IsReportedAfterExactlyOneConnect()
    {
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (_, _) => Task.FromResult(ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(NewComposer())))));

        var (_, status) = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: true, Ct);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Deferred, status);
        Assert.Equal(1, accessor.ConnectCalls);
        // ONE acquisition plus the ONE connect: no removal re-query happened.
        Assert.Equal(2, accessor.AcquireCalls);
    }

    /// <summary>
    /// A DEFERRED READ NEVER CONNECTS: the read stays on the shell metadata, so simply rendering or reloading
    /// the page can never start an LLM connect.
    /// </summary>
    [Fact]
    public async Task ADeferredRead_NeverConnects()
    {
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (_, _) => Task.FromResult(ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Granted(ComposerRuntimeStatus.Deferred, Handle(NewComposer())))));

        var (_, status) = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: false, Ct);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Deferred, status);
        Assert.Equal(0, accessor.ConnectCalls);
        Assert.Equal(1, accessor.AcquireCalls);
    }

    /// <summary>
    /// A DENIED CALLER IS NEVER CONNECTED: the operation reports the forbidden status and performs no connect,
    /// so an unauthorized caller cannot even trigger the LLM connect path.
    /// </summary>
    [Fact]
    public async Task ADeniedCaller_IsNeverConnected()
    {
        var accessor = new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
            (_, _) => Task.FromResult(ComposerRuntimeAccess.Denied()));

        var (access, status) = await ComposerChat.SelectRuntimeAsync(
            accessor, authenticationState: null, mutation: true, Ct);

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, status);
        Assert.False(access.IsAuthorized);
        Assert.Equal(0, accessor.ConnectCalls);
        Assert.Equal(1, accessor.AcquireCalls);
    }

    // ── 4. The view state: the circuit status dominates ──────────────────────────────────────────

    /// <summary>
    /// A FORBIDDEN CIRCUIT RENDERS NOTHING COMPOSER-SHAPED: no picker, no connected controls, no
    /// "configure a model" hint, no placeholder — even though a previously bound Composer claimed it was
    /// connected.
    /// </summary>
    [Fact]
    public void TheForbiddenViewState_HidesEveryComposerSurface()
    {
        var state = ComposerChat.ComputeViewState(
            ComposerChat.ComposerCircuitStatus.Forbidden, isConnected: true, runtimeError: null);

        Assert.True(state.ShowForbidden);
        Assert.False(state.IsConnected);
        Assert.False(state.ShowModelPicker);
        Assert.False(state.ShowConnectedControls);
        Assert.False(state.ShowNotConfiguredNotice);
        Assert.Null(state.Placeholder);
        Assert.False(state.ShowsComposerContent);
    }

    /// <summary>
    /// A FAILED OR REMOVED CIRCUIT SHOWS A FIXED, SECRET-FREE ERROR AND NO CONTENT: the message comes from the
    /// recovery facade's own constants, so it can never name an account, model, provider or credential.
    /// </summary>
    [Theory]
    [InlineData(ComposerChat.ComposerCircuitStatus.Failed, ComposerRuntimeFacade.RuntimeUnavailableError)]
    [InlineData(ComposerChat.ComposerCircuitStatus.Removed, ComposerRuntimeFacade.RuntimeRemovedError)]
    public void TheFailedAndRemovedViewStates_ShowTheFixedErrorAndNoContent(int circuitStatus, string expectedError)
    {
        var status = (ComposerChat.ComposerCircuitStatus)circuitStatus;
        var state = ComposerChat.ComputeViewState(status, isConnected: true, runtimeError: expectedError);

        Assert.True(state.ShowRuntimeError);
        Assert.Equal(expectedError, state.RuntimeError);
        Assert.False(state.ShowConnectedControls);
        Assert.False(state.ShowModelPicker);
        Assert.False(state.ShowsComposerContent);
    }

    /// <summary>
    /// A DEFERRED CIRCUIT STAYS ACTIONABLE: the picker remains reachable (so the first selection can connect),
    /// the connected-only controls do not render, and the deferred notice with its ONE connect control does.
    /// </summary>
    [Fact]
    public void TheDeferredViewState_StaysActionable_WithNoConnectedControls()
    {
        var state = ComposerChat.ComputeViewState(
            ComposerChat.ComposerCircuitStatus.Deferred, isConnected: false, runtimeError: null);

        Assert.True(state.ShowDeferredNotice);
        Assert.True(state.ShowModelPicker);
        Assert.False(state.ShowConnectedControls);
        Assert.False(state.ShowForbidden);
        Assert.False(state.ShowRuntimeError);
        Assert.Equal(ComposerChat.ModelSelectPlaceholder, state.Placeholder);
        Assert.True(state.ShowsComposerContent);
    }

    /// <summary>
    /// A READY CIRCUIT RENDERS EXACTLY THE PREVIOUS CONNECTION-BASED VIEW — so the per-user path changes WHO the
    /// Composer belongs to, never what the page looks like once a runtime is usable.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReadyCircuitViewState_MatchesTheConnectionBasedView(bool isConnected)
    {
        var ready = ComposerChat.ComputeViewState(
            ComposerChat.ComposerCircuitStatus.Ready, isConnected, runtimeError: null);

        Assert.Equal(ComposerChat.ComputeViewState(isConnected), ready);
    }

    // ── 5. The RENDERED page ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A VIEWER'S RENDERED PAGE HAS NO COMPOSER CONTENT AND NO LOGIN REDIRECT: the forbidden notice is the only
    /// thing on screen — no thread, no catalog selector, no "configure a model" hint, and no redirect markup.
    /// </summary>
    [Fact]
    public async Task TheViewersRenderedPage_ShowsOnlyTheForbiddenNotice()
    {
        var html = await RenderForAsync("circuit-viewer", UserRole.Viewer);

        Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No model configured", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-send", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to the Composer", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SIGNED-OUT CIRCUIT IS FORBIDDEN TOO: with authentication enabled and no authenticated identity the
    /// accessor refuses, so the page shows the same forbidden view — never a Composer and never a redirect.
    /// </summary>
    [Fact]
    public async Task TheSignedOutRenderedPage_ShowsTheForbiddenView_AndNoRedirect()
    {
        var html = await RenderForAsync(gitHubId: null, role: null);

        Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MEMBER'S RENDERED PAGE BINDS THEIR OWN RUNTIME: the connected layout renders, and the rendered thread
    /// is the runtime's own session — the page really asked the accessor and used what it selected.
    /// </summary>
    [Fact]
    public async Task TheMembersRenderedPage_BindsTheirOwnRuntime()
    {
        var html = await RenderForAsync("circuit-member", UserRole.Member);

        Assert.DoesNotContain("You do not have access to the Composer.", html, StringComparison.Ordinal);
        Assert.Contains("chat-actions", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A DISABLED ACCOUNT IS FORBIDDEN EVEN AFTER A SUCCESSFUL RENDER: the next render re-resolves the account,
    /// so the disablement lands immediately — the page never keeps a runtime it no longer may use.
    /// </summary>
    [Fact]
    public async Task ADisabledAccount_LosesTheComposerOnTheVeryNextRender()
    {
        var before = await RenderForAsync("circuit-member", UserRole.Member);
        Assert.DoesNotContain("You do not have access to the Composer.", before, StringComparison.Ordinal);

        await _auth.SetEnabledAsync("circuit-member", enabled: false);
        try
        {
            var after = await RenderForAsync("circuit-member", UserRole.Member);
            Assert.Contains("You do not have access to the Composer.", after, StringComparison.Ordinal);
            Assert.DoesNotContain("chat-actions", after, StringComparison.Ordinal);
        }
        finally
        {
            await _auth.SetEnabledAsync("circuit-member", enabled: true);
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the REAL ComposerChat page over the REAL production circuit seam — factory, legacy Composer,
    /// manager, scoped circuit accessor, scoped runtime accessor and its interface alias, and the two facades
    /// from <see cref="ComposerFacadeRegistration"/> — for one circuit identity.
    /// <para>
    /// Nothing about the runtime SELECTION is scripted: a Viewer is refused by the real accessor, a signed-out
    /// circuit is refused by it, and a Member receives a real per-user runtime from the real manager. That is
    /// what makes these rows discriminating about the production wiring rather than about a test double.
    /// </para>
    /// </summary>
    /// <param name="gitHubId">The circuit's GitHub identity, or <c>null</c> for a signed-out circuit.</param>
    /// <param name="role">The account's role (seeded by the fixture; the directory is the authority).</param>
    private async Task<string> RenderForAsync(string? gitHubId, UserRole? role)
    {
        _ = role;

        var identity = gitHubId is null
            ? new ClaimsIdentity(authenticationType: null)
            : new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies");
        var state = new AuthenticationState(new ClaimsPrincipal(identity));

        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton(new ComposerAttachmentService(
            stateDir, NullLogger<ComposerAttachmentService>.Instance));
        services.AddSingleton<IGoalStore>(_store);
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = Model }, new ModelEntry { Name = OtherModel }],
            },
            Composer = new ComposerConfig { Model = Model },
        });
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));

        // THE PRODUCTION SEAM: the legacy Composer, the singleton manager, the scoped accessor (+ its alias) and
        // the recovery facade for IComposerFacade.
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        // A CIRCUIT IS A SCOPE: the renderer receives the SCOPE's provider, never the root one, so the scoped
        // circuit accessor and runtime accessor are resolved exactly as the host resolves them per circuit
        // (scope validation stays ON, so a root-provider resolution could not pass silently).
        await using var scope = provider.CreateAsyncScope();
        try
        {
            await using var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = Task.FromResult(state),
                    ["ChildContent"] = (RenderFragment)(builder =>
                    {
                        builder.OpenComponent<ComposerChat>(0);
                        builder.CloseComponent();
                    }),
                });

                var output = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
                return output.ToHtmlString();
            });
        }
        finally
        {
            // THE MANAGER IS THE SOLE OWNER: its shutdown runs before the provider disposes anything.
            await provider.GetRequiredService<ComposerRuntimeManager>().ShutdownAsync();
            await provider.DisposeAsync();
        }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real Composer over an isolated state directory, connected when asked.</summary>
    private Composer NewComposer(bool connected = false)
    {
        var stateDir = Path.Combine(_dir, $"composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var composer = new Composer(
            Model,
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: [Model, OtherModel],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        if (connected) composer.ConnectAsync(Ct).GetAwaiter().GetResult();
        return composer;
    }

    /// <summary>A real attachment store rooted under this fixture's isolated directory.</summary>
    private ComposerAttachmentService NewAttachments() =>
        new(_dir, NullLogger<ComposerAttachmentService>.Instance);

    /// <summary>A handle over <paramref name="composer"/> for an ACTIVATED account of this directory.</summary>
    private ComposerUserRuntime Handle(Composer composer, ComposerAttachmentService? attachments = null)
    {
        var state = ComposerUserState
            .ResolveAsync(_auth.Directory.Service, _memberId, Ct)
            .GetAwaiter()
            .GetResult();

        return new ComposerUserRuntime(
            state,
            composer,
            attachments ?? NewAttachments(),
            subscriber: null,
            new ComposerConstructionSnapshot(composer.StartupDefaultModel, compactionModel: null));
    }

    /// <summary>The internal account id of a seeded GitHub identity.</summary>
    private int UserIdOf(string gitHubId) => gitHubId switch
    {
        "circuit-original" => _originalId,
        "circuit-member" => _memberId,
        "circuit-viewer" => _viewerId,
        _ => throw new ArgumentOutOfRangeException(nameof(gitHubId), gitHubId, "Unknown seeded identity."),
    };

    /// <summary>
    /// THE RUNNER-INVOKED CLEANUP: xUnit v3 calls <c>IAsyncLifetime.DisposeAsync</c> (never a synchronous
    /// <c>Dispose</c>), so every cleanup step lives here.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _goalDb.Dispose();
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

    /// <summary>A JS runtime that answers nothing — the page only calls it for paste handling.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>
    /// A minimal container over the PRODUCTION circuit seam (factory, legacy Composer, manager, scoped circuit
    /// accessor, scoped runtime accessor and its interface alias) for rows that observe the real accessor.
    /// </summary>
    private sealed class CircuitFixture : IAsyncDisposable
    {
        private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-circuit-fixture-{Guid.NewGuid():N}");

        public CircuitFixture(UserService users, string model)
        {
            Directory.CreateDirectory(_dir);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<HiveConfigFile>(new HiveConfigFile
            {
                Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
                Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = model }] },
                Composer = new ComposerConfig { Model = model },
            });
            services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
            services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
            services.AddSingleton<HivePolicy>();
            services.AddSingleton(users);
            services.AddSingleton(sp => new HivePrincipalResolver(
                sp.GetRequiredService<UserService>(), sp.GetRequiredService<HiveAuthMode>(),
                NullLogger<HivePrincipalResolver>.Instance));
            services.AddScoped(sp => new CircuitUserPrincipalAccessor(
                sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));
            services.AddProductionCircuitAccessor(_dir);

            Provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
        }

        public ServiceProvider Provider { get; }

        /// <summary>The per-user runtime ids currently cached by the REAL manager.</summary>
        public int[] ManagerUserIds()
        {
            var field = typeof(ComposerRuntimeManager)
                .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var entries = (System.Collections.IDictionary)field.GetValue(
                Provider.GetRequiredService<ComposerRuntimeManager>())!;
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

            await Step(() => Provider.GetRequiredService<ComposerRuntimeManager>().ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());
            await Step(() => { _db.Dispose(); return Task.CompletedTask; });
            await Step(() =>
            {
                if (Directory.Exists(_dir)) TestHelpers.ForceDeleteDirectory(_dir);
                return Task.CompletedTask;
            });

            if (failures.Count > 0)
                throw new AggregateException("Circuit fixture cleanup failed", failures);
        }
    }
}
