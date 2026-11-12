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
/// THE CIRCUIT'S PER-OPERATION CONTRACT over the REAL production seam: the REAL
/// <see cref="ComposerRuntimeAccessor"/> (the singleton manager, the scoped circuit accessor, the
/// production resolver and policy) and the REAL <see cref="ComposerChat"/> page, rendered through
/// the framework's own <see cref="HtmlRenderer"/> with a REAL cascading
/// <see cref="AuthenticationState"/>.
/// <para>
/// Nothing about the RUNTIME SELECTION is scripted in the rows that can reach the production path:
/// a Viewer is refused by the real accessor, a demotion is observed by the real re-resolution, and
/// a deferred runtime is connected by the real manager. Only the rows that need an outcome the
/// production seam cannot produce locally (a manager-level refusal, a fault, a stale handle)
/// substitute the SELECTION answer — and then the assertion is about what the REAL page renders
/// from it.
/// </para>
/// </summary>
public sealed class ComposerCircuitOperationTests : IAsyncLifetime, IDisposable
{
    private const string Model = "circuit-op-model";
    private const string CopilotModel = "copilot/primary";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-circuit-op-{Guid.NewGuid():N}");

    private int _originalId;
    private int _memberId;
    private int _viewerId;

    public ComposerCircuitOperationTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The ORIGINAL identity is the lowest-id activated row: only it may claim legacy history.
        _originalId = (await _auth.SeedAsync("circuit-op-original", UserRole.Admin)).Id;
        _memberId = (await _auth.SeedAsync("circuit-op-member", UserRole.Member)).Id;
        _viewerId = (await _auth.SeedAsync("circuit-op-viewer", UserRole.Viewer)).Id;
    }

    // ── 1. The circuit identity: initialized ONCE, re-authorized EVERY operation ──────────────────

    /// <summary>
    /// THE CIRCUIT IDENTITY IS INITIALIZED ONCE, AND EVERY OPERATION RE-AUTHORIZES IT: the same
    /// <see cref="AuthenticationState"/> (never re-created, never re-supplied) yields an authorized
    /// access for the Member, and — after the DIRECTORY demotes that very account — the NEXT
    /// operation on the SAME initialized circuit is refused with no runtime.
    /// <para>
    /// This is the row that fails if the circuit identity were cached as authorization truth: the
    /// accessor's re-resolution reads the CURRENT stored role, so the demotion lands on the very
    /// next operation without any new <see cref="AuthenticationState"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheCircuitIdentity_IsInitializedOnce_AndADemotionLandsOnTheNextOperation()
    {
        await using var host = NewHost();
        using var scope = host.Provider.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();
        var circuitAccessor = scope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();

        // ONE cascading state, created ONCE and reused for every operation.
        var state = ComposerCircuitTesting.AuthenticatedState(
            FacadeAuthorizationHarness.Principal("circuit-op-member"));

        var admitted = await accessor.AcquireForCircuitAsync(state, Ct);
        Assert.True(admitted.IsAuthorized, "an activated Member must be authorized");
        Assert.NotNull(admitted.Runtime);
        Assert.Equal(_memberId, admitted.Runtime!.State.UserId);
        Assert.True(circuitAccessor.IsInitialized, "the circuit accessor must be initialised from the cascade");

        // The account is demoted in the DIRECTORY — the circuit is NOT re-created.
        await _auth.SetRoleAsync("circuit-op-member", UserRole.Viewer);
        try
        {
            var refused = await accessor.AcquireForCircuitAsync(state, Ct);

            Assert.False(refused.IsAuthorized, "a demoted account must be refused on the very next operation");
            Assert.Null(refused.Runtime);
            Assert.Null(refused.Result);
            // The circuit was never re-initialised: the refusal comes from the fresh re-resolution.
            Assert.True(circuitAccessor.IsInitialized);
        }
        finally
        {
            await _auth.SetRoleAsync("circuit-op-member", UserRole.Member);
        }
    }

    /// <summary>
    /// A VIEWER IS REFUSED BY THE REAL CIRCUIT PATH — and the manager never created a runtime for
    /// that account, so no runtime content could ever have been shown.
    /// </summary>
    [Fact]
    public async Task AViewer_IsRefused_AndNoRuntimeIsCreatedForIt()
    {
        await using var host = NewHost();
        using var scope = host.Provider.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();
        var state = ComposerCircuitTesting.AuthenticatedState(
            FacadeAuthorizationHarness.Principal("circuit-op-viewer"));

        var denied = await accessor.AcquireForCircuitAsync(state, Ct);

        Assert.False(denied.IsAuthorized);
        Assert.Null(denied.Runtime);
        Assert.DoesNotContain(_viewerId, ManagerEntries(host.Manager));
    }

    // ── 2. The page binds the caller's OWN runtime — and reads ITS history ───────────────────────

    /// <summary>
    /// THE PAGE IS BOUND TO THE CALLER'S OWN RUNTIME, AND ITS HISTORY COMES FROM THAT RUNTIME:
    /// the per-user session file of the Member carries a marker the legacy singleton never had, and
    /// the rendered page shows exactly that marker — never the legacy singleton's own history.
    /// <para>
    /// This is the discriminating UI row: a page still bound to the legacy singleton would render
    /// the LEGACY marker (or nothing at all), never the per-user one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRenderedPage_ShowsTheCallersOwnRuntimeHistory_AndNeverTheLegacySingletons()
    {
        await using var host = NewHost();

        // The LEGACY singleton's own history — present in the shared state directory, and loaded by
        // the legacy Composer itself.
        var legacySession = SharpCoder.AgentSession.Create("composer");
        legacySession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "legacy-singleton-only-marker"));
        await legacySession.SaveAsync(Path.Combine(host.StateDir, "composer-session.json"), Ct);

        // The MEMBER's own per-user session — the canonical per-user path.
        var memberState = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var perUserPath = ComposerUserState.SessionFilePath(host.StateDir, memberState);
        Directory.CreateDirectory(Path.GetDirectoryName(perUserPath)!);
        var perUserSession = SharpCoder.AgentSession.Create("composer");
        perUserSession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "per-user-history-marker"));
        await perUserSession.SaveAsync(perUserPath, Ct);

        // The legacy singleton really holds the legacy marker — so the row below is discriminating.
        var legacyComposer = host.Provider.GetRequiredService<Composer>();
        await legacyComposer.ConnectAsync(Ct);
        Assert.Contains(legacyComposer.GetChatHistory(), m => m.Content.Contains("legacy-singleton-only-marker"));

        await using var rendered = await RenderAsync(host, "circuit-op-member");
        var html = rendered.Html;

        // The page shows the CALLER'S OWN history...
        Assert.Contains("per-user-history-marker", html, StringComparison.Ordinal);
        // ...and NEVER the legacy singleton's.
        Assert.DoesNotContain("legacy-singleton-only-marker", html, StringComparison.Ordinal);
        // The empty-state welcome is gone: a thread was really loaded.
        Assert.DoesNotContain("Welcome to the Composer", html, StringComparison.Ordinal);

        // The bound Composer IS the manager's per-user runtime Composer — not the legacy singleton.
        var bound = BoundComposerOf(rendered.Page);
        Assert.NotNull(bound);
        Assert.NotSame(legacyComposer, bound);

        var access = await rendered.Scope.ServiceProvider
            .GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(
                ComposerCircuitTesting.AuthenticatedState(FacadeAuthorizationHarness.Principal("circuit-op-member")), Ct);
        Assert.Same(access.Runtime!.Composer, bound);

        // The per-user runtime really exists in the manager, under the Member's own id.
        Assert.Contains(_memberId, ManagerEntries(host.Manager));
    }

    // ── 3. Deferred → Connected, driven by the next operation ───────────────────────────────────

    /// <summary>
    /// A DEFERRED RUNTIME IS CONNECTED BY THE NEXT OPERATION — through the REAL accessor and the
    /// REAL manager, with no polling and no coordinator: the page renders the actionable deferred
    /// notice, and the deferred banner's own connect control drives
    /// <see cref="IComposerRuntimeAccessor.ConnectForCircuitAsync"/> until the runtime is connected.
    /// <para>
    /// The deferral is the production one: an authenticated installation whose Copilot-backed
    /// primary has no usable credential yet. The credential becomes available only BETWEEN the two
    /// operations, so the connect can only succeed if the operation really re-asks the manager.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADeferredRuntime_IsConnectedByTheNextOperation_ThroughTheRealAccessor()
    {
        var tokenAvailable = false;

        await using var host = NewHost(
            primaryModel: CopilotModel,
            providerOf: model => model.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local",
            isTokenAvailable: () => tokenAvailable);

        // The ORIGINAL account may use the Copilot-backed model (identity, not role); it is the
        // credential that is missing, so the runtime settles DEFERRED.
        await using var rendered = await RenderAsync(host, "circuit-op-original");
        var html = rendered.Html;

        Assert.Contains("This Composer is not connected yet.", html, StringComparison.Ordinal);
        Assert.Contains("Connect now", html, StringComparison.Ordinal);
        // A deferred runtime shows NO connected controls and NO thread.
        Assert.DoesNotContain("composer-input", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to the Composer", html, StringComparison.Ordinal);

        // The credential arrives; the NEXT operation (the deferred banner's own connect control)
        // must drive the connect.
        tokenAvailable = true;
        await rendered.ConnectRuntimeAsync();

        // The runtime is now live: the manager reports it as CONNECTED, and the page's own binding
        // is the connected runtime.
        var access = await rendered.Scope.ServiceProvider
            .GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(
                ComposerCircuitTesting.AuthenticatedState(FacadeAuthorizationHarness.Principal("circuit-op-original")), Ct);
        Assert.Equal(ComposerRuntimeStatus.Connected, access.Status);
        Assert.True(access.Runtime!.Composer.IsConnected);
        Assert.Contains(_originalId, ManagerEntries(host.Manager));

        // ...and the page itself now holds that connected runtime — the deferred notice is gone.
        var bound = BoundComposerOf(rendered.Page);
        Assert.NotNull(bound);
        Assert.True(bound!.IsConnected, "the connect control must have connected the bound runtime");
        Assert.Same(access.Runtime!.Composer, bound);
    }

    // ── 4. The fail-closed renders ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A MANAGER-LEVEL REFUSAL HIDES EVERYTHING: the page shows the forbidden view — no thread, no
    /// model picker, no send control — and never falls back to the legacy singleton Composer.
    /// <para>
    /// The selection answer is the production-shaped one the manager produces for a refusal; the
    /// assertion is about what the REAL page renders from it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARefusedSelection_HidesAllContent_AndNeverFallsBackToTheLegacySingleton()
    {
        await using var host = NewHost(selection: _ =>
            ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.UnknownAccount)));
        var legacyComposer = host.Provider.GetRequiredService<Composer>();

        await using var rendered = await RenderAsync(host, "circuit-op-member");
        var html = rendered.Html;

        Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-send", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to the Composer", html, StringComparison.Ordinal);
        // No login redirect: the identity is known — it simply may not use the Composer.
        Assert.DoesNotContain("/login", html, StringComparison.OrdinalIgnoreCase);

        // THE PAGE really CLASSIFIED the refusal as FORBIDDEN: the circuit binding exists and
        // carries that status. A page that threw while classifying (or that fell back to the
        // legacy singleton) would leave no binding at all — or a bound Composer.
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));

        // NOTHING is bound — in particular not the legacy singleton.
        Assert.Null(BoundComposerOf(rendered.Page));
        Assert.NotNull(legacyComposer);
    }

    /// <summary>
    /// A FAULTED RUNTIME SHOWS THE FIXED, SECRET-FREE ERROR AND NO CONTENT: the message is the
    /// recovery facade's own constant, so it can never name an account, model, provider or
    /// credential.
    /// </summary>
    [Fact]
    public async Task AFailedSelection_ShowsTheFixedError_AndNoContent()
    {
        await using var host = NewHost(selection: _ =>
            ComposerRuntimeAccess.Granted(
                ComposerRuntimeResult.Faulted(new InvalidOperationException("secret-token-abc123"))));

        await using var rendered = await RenderAsync(host, "circuit-op-member");
        var html = rendered.Html;

        // The message is HTML-ENCODED by the renderer, so the expected text is derived from the
        // production constant through the framework's own encoder — never hand-copied.
        Assert.Contains(
            System.Text.Encodings.Web.HtmlEncoder.Default.Encode(ComposerRuntimeFacade.RuntimeUnavailableError),
            html,
            StringComparison.Ordinal);
        // The fault's own text never reaches the page.
        Assert.DoesNotContain("secret-token-abc123", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-send", html, StringComparison.Ordinal);
        Assert.Null(BoundComposerOf(rendered.Page));
    }

    /// <summary>
    /// A STALE HANDLE SHOWS THE REMOVED ERROR AND NO CONTENT: the page re-queries the manager ONCE
    /// and, when the handle is still stale, reports the removal instead of retrying forever.
    /// </summary>
    [Fact]
    public async Task ARemovedSelection_ShowsTheRemovedError_AndNoContent()
    {
        await using var host = NewHost(selection: _ =>
            ComposerRuntimeAccess.Granted(ComposerRuntimeResult.RemovedResult()));

        await using var rendered = await RenderAsync(host, "circuit-op-member");
        var html = rendered.Html;

        Assert.Contains(ComposerRuntimeFacade.RuntimeRemovedError, html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-send", html, StringComparison.Ordinal);
        Assert.Null(BoundComposerOf(rendered.Page));
    }

    /// <summary>
    /// A VIEWER'S PAGE IS FORBIDDEN WITH NO CONTENT AND NO LOGIN REDIRECT: the REAL accessor refuses
    /// the Viewer (it holds no <see cref="HiveCapability.InteractiveMember"/>), so the page shows the
    /// forbidden view — and the manager never created a runtime for that account.
    /// </summary>
    [Fact]
    public async Task AViewersPage_IsForbidden_WithNoContentAndNoLoginRedirect()
    {
        await using var host = NewHost();

        await using var rendered = await RenderAsync(host, "circuit-op-viewer");
        var html = rendered.Html;

        Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/login", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-send", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Welcome to the Composer", html, StringComparison.Ordinal);
        Assert.Null(BoundComposerOf(rendered.Page));
        Assert.DoesNotContain(_viewerId, ManagerEntries(host.Manager));
    }

    // ── 5. The legacy migration, keyed on the IDENTITY ──────────────────────────────────────────

    /// <summary>
    /// THE ORIGINAL IDENTITY CLAIMS THE LEGACY HISTORY — REGARDLESS OF ITS CURRENT ROLE — AND EVERY
    /// OTHER ACCOUNT GETS ITS OWN: the original's runtime is legacy-claim eligible (and the
    /// migration evidence is recorded), while a second account's runtime is not, and its session
    /// lives under its own per-user path.
    /// <para>
    /// The row runs through the REAL accessor and the REAL manager, so the claim really is made by
    /// the production migration seam.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheOriginalIdentity_ClaimsTheLegacyHistory_AndOthersGetTheirOwn()
    {
        await using var host = NewHost();

        // The legacy session exists: only the original account may claim it.
        var legacySession = SharpCoder.AgentSession.Create("composer");
        legacySession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "legacy-history-marker"));
        await legacySession.SaveAsync(Path.Combine(host.StateDir, "composer-session.json"), Ct);

        using var scope = host.Provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();

        // The ORIGINAL account — demoted from Admin to Member, so its ROLE can never be the reason
        // it claims the history.
        await _auth.SetRoleAsync("circuit-op-original", UserRole.Member);
        try
        {
            var original = await accessor.AcquireForCircuitAsync(
                ComposerCircuitTesting.AuthenticatedState(
                    FacadeAuthorizationHarness.Principal("circuit-op-original")), Ct);

            Assert.True(original.IsAuthorized);
            Assert.NotNull(original.Runtime);
            Assert.True(original.Runtime!.State.LegacyClaimEligible,
                "the original identity claims the legacy history regardless of its current role");
            Assert.True(File.Exists(
                ComposerUserState.LegacyMigrationEvidencePath(host.StateDir, original.Runtime.State)),
                "the migration evidence must be recorded for the original identity");
        }
        finally
        {
            await _auth.SetRoleAsync("circuit-op-original", UserRole.Admin);
        }

        // A SECOND account of the very SAME role never claims it and gets its own history. It is a
        // DIFFERENT circuit (a different scope), because a circuit carries exactly ONE identity.
        using var otherScope = host.Provider.CreateScope();
        var otherAccessor = otherScope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();

        var second = await otherAccessor.AcquireForCircuitAsync(
            ComposerCircuitTesting.AuthenticatedState(
                FacadeAuthorizationHarness.Principal("circuit-op-member")), Ct);

        Assert.True(second.IsAuthorized);
        Assert.NotNull(second.Runtime);
        Assert.False(second.Runtime!.State.LegacyClaimEligible,
            "a non-original identity never claims the legacy history");
        Assert.False(File.Exists(
            ComposerUserState.LegacyMigrationEvidencePath(host.StateDir, second.Runtime.State)));

        // ...and its session lives under its OWN per-user path.
        var expected = Path.Combine(host.StateDir, "composer", "users", _memberId.ToString(), "session.json");
        Assert.Equal(
            expected,
            ComposerUserState.SessionFilePath(host.StateDir, second.Runtime.State),
            StringComparer.Ordinal);
    }

    // ── 6. Open mode: the legacy singleton ──────────────────────────────────────────────────────

    /// <summary>
    /// AN AUTHENTICATED LOCAL-OPERATOR REFUSAL IS STILL FORBIDDEN: the legacy fallback is gated on
    /// BOTH the open mode AND that typed refusal, so an authenticated installation never serves
    /// the legacy singleton — not even for the very refusal reason open mode uses.
    /// <para>
    /// This is the row that fails if the authentication mode stops gating the legacy fallback.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedLocalOperatorRefusal_IsForbidden_AndNeverServesTheLegacySingleton()
    {
        await using var host = NewHost(selection: _ =>
            ComposerRuntimeAccess.Granted(ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.LocalOperator)));
        var legacyComposer = host.Provider.GetRequiredService<Composer>();

        await using var rendered = await RenderAsync(host, "circuit-op-member");
        var html = rendered.Html;

        Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat-model-select", html, StringComparison.Ordinal);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));
        Assert.Null(BoundComposerOf(rendered.Page));
        Assert.NotNull(legacyComposer);
    }

    /// <summary>
    /// OPEN MODE BINDS THE LEGACY SINGLETON — AND ONLY THE LEGACY SINGLETON: the page renders the
    /// legacy Composer's own history (never a per-user runtime's), and the manager's cache stays
    /// EMPTY, because open mode is the legacy single-operator installation.
    /// <para>
    /// The selection answer is the production one: in open mode the resolver yields the synthetic
    /// local operator, which the manager refuses with
    /// <see cref="ComposerRuntimeRefusal.LocalOperator"/> — the ONE refusal the page serves from
    /// the legacy singleton.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InOpenMode_ThePageBindsTheLegacySingleton_AndNoPerUserRuntimeExists()
    {
        await using var host = NewHost(authEnabled: false);

        // The legacy singleton's own history.
        var legacySession = SharpCoder.AgentSession.Create("composer");
        legacySession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "open-mode-legacy-marker"));
        await legacySession.SaveAsync(Path.Combine(host.StateDir, "composer-session.json"), Ct);

        var legacyComposer = host.Provider.GetRequiredService<Composer>();
        await legacyComposer.ConnectAsync(Ct);

        await using var rendered = await RenderAsync(host, gitHubId: "open-mode-operator");
        var html = rendered.Html;

        // The forbidden view is NOT shown: open mode is the legacy installation.
        Assert.DoesNotContain("You do not have access to the Composer.", html, StringComparison.Ordinal);
        // The legacy Composer's own history is rendered.
        Assert.Contains("open-mode-legacy-marker", html, StringComparison.Ordinal);

        // The bound Composer IS the legacy singleton.
        var bound = BoundComposerOf(rendered.Page);
        Assert.NotNull(bound);
        Assert.Same(legacyComposer, bound);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, BoundStatusOf(rendered.Page));

        // ...and NO per-user runtime was ever created.
        Assert.Empty(ManagerEntries(host.Manager));
    }

    // ── 7. The covered UI operations run on the CALLER'S OWN runtime ────────────────────────────

    /// <summary>
    /// EVERY COVERED UI OPERATION RUNS ON THE CALLER'S OWN PER-USER RUNTIME — and on no other
    /// account's: the current-model read, the model catalog and the compaction all answer from the
    /// runtime the manager holds for THIS account.
    /// <para>
    /// The DISCRIMINATOR is the legacy singleton's own state: it is left DISCONNECTED, so it has no
    /// agent, no stats and (through the facade's current-model read) a NULL model — while the
    /// caller's own runtime is connected and reports its model.
    /// </para>
    /// <para>
    /// The rows are driven through the REAL <see cref="IComposerFacade"/> the page injects — the
    /// recovery facade over the REAL accessor and the REAL manager — so the routing is the
    /// production one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheCoveredUiOperations_RunOnTheCallersOwnRuntime()
    {
        await using var host = NewHost();

        // The legacy singleton is NEVER connected in this row: it has no agent, so it has no stats
        // and the facade's current-model read over it would answer with a null model.
        var legacyComposer = host.Provider.GetRequiredService<Composer>();
        Assert.False(legacyComposer.IsConnected);
        Assert.Null(legacyComposer.GetStats());

        using var scope = host.Provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();
        var facade = scope.ServiceProvider.GetRequiredService<IComposerFacade>();

        var principal = FacadeAuthorizationHarness.Principal("circuit-op-member");

        // The caller's own runtime really exists and is connected to the configured model.
        var access = await accessor.AcquireForCircuitAsync(
            ComposerCircuitTesting.AuthenticatedState(
                FacadeAuthorizationHarness.Principal("circuit-op-member")), Ct);
        Assert.True(access.IsAuthorized);
        Assert.NotNull(access.Runtime);
        Assert.True(access.Runtime!.Composer.IsConnected);
        Assert.NotNull(access.Runtime.Composer.GetStats());

        // The current-model read runs on the CALLER'S runtime (the legacy singleton would answer
        // with a null model).
        var current = await facade.GetCurrentModelAsync(principal, Ct);
        Assert.True(current.Success, current.Error ?? "the current-model read failed");
        Assert.Equal(Model, current.Value?.Model);

        // The catalog read runs on the CALLER'S runtime.
        var models = await facade.GetModelsAsync(principal, Ct);
        Assert.True(models.Success, models.Error ?? "the catalog read failed");
        Assert.NotNull(models.Value);
        Assert.Contains(Model, models.Value!.Models);

        // The compaction runs on the CALLER'S runtime — and the caller's runtime is the one the
        // manager holds for that account.
        var compact = await facade.CompactAsync(principal, Ct);
        Assert.NotNull(compact);

        // The manager really owns a runtime for the caller — and only for the caller.
        var entries = ManagerEntries(host.Manager).ToArray();
        Assert.Contains(_memberId, entries);
        Assert.DoesNotContain(_viewerId, entries);
    }

    /// <summary>
    /// THE RESET OPERATION MUTATES THE CALLER'S OWN RUNTIME — ITS OWN session, never the legacy
    /// singleton's: the per-user session file of the Member is the one the reset clears, and the
    /// legacy singleton's session file is untouched.
    /// </summary>
    [Fact]
    public async Task TheResetOperation_ClearsTheCallersOwnSession_AndNotTheLegacySingletons()
    {
        await using var host = NewHost();

        // The legacy singleton's session file.
        var legacyPath = Path.Combine(host.StateDir, "composer-session.json");
        var legacySession = SharpCoder.AgentSession.Create("composer");
        legacySession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "legacy-session-marker"));
        await legacySession.SaveAsync(legacyPath, Ct);

        // The Member's own per-user session file.
        var memberState = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var perUserPath = ComposerUserState.SessionFilePath(host.StateDir, memberState);
        Directory.CreateDirectory(Path.GetDirectoryName(perUserPath)!);
        var perUserSession = SharpCoder.AgentSession.Create("composer");
        perUserSession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "per-user-session-marker"));
        await perUserSession.SaveAsync(perUserPath, Ct);

        using var scope = host.Provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>();
        var access = await accessor.AcquireForCircuitAsync(
            ComposerCircuitTesting.AuthenticatedState(
                FacadeAuthorizationHarness.Principal("circuit-op-member")), Ct);
        Assert.True(access.IsAuthorized);
        Assert.NotNull(access.Runtime);

        // The caller's own runtime is the one that is reset.
        await access.Runtime!.Composer.ResetSessionAsync(Ct);

        // The per-user session file is gone; the legacy singleton's is NOT.
        Assert.False(File.Exists(perUserPath), "the reset must clear the caller's own session");
        Assert.True(File.Exists(legacyPath), "the reset must never touch the legacy singleton's session");
    }

    // ── 8. Cross-circuit isolation: two circuits, two identities ────────────────────────────────

    /// <summary>
    /// TWO CIRCUITS OF THE SAME USER SHARE ONE RUNTIME — AND TWO CIRCUITS OF DIFFERENT USERS NEVER
    /// DO: the Member's two circuits (two scopes, two scoped accessors) resolve the SAME Composer
    /// (the manager's shared runtime for that account), while a second account's circuit resolves a
    /// DIFFERENT one — with its own session path and its own attachment root.
    /// <para>
    /// This is the cross-circuit isolation row for the wiring: a per-circuit runtime (a second
    /// manager entry per circuit) or a shared singleton (both users on one Composer) would fail
    /// here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoCircuitsOfTheSameUser_ShareTheRuntime_AndDifferentUsersNeverDo()
    {
        await using var host = NewHost();

        // The two circuits of the SAME user.
        using var firstTab = host.Provider.CreateScope();
        using var secondTab = host.Provider.CreateScope();

        var firstAccess = await firstTab.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(
                ComposerCircuitTesting.AuthenticatedState(
                    FacadeAuthorizationHarness.Principal("circuit-op-member")), Ct);
        var secondAccess = await secondTab.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(
                ComposerCircuitTesting.AuthenticatedState(
                    FacadeAuthorizationHarness.Principal("circuit-op-member")), Ct);

        Assert.True(firstAccess.IsAuthorized);
        Assert.True(secondAccess.IsAuthorized);
        Assert.Same(firstAccess.Runtime!.Composer, secondAccess.Runtime!.Composer);
        Assert.Same(firstAccess.Runtime.Attachments, secondAccess.Runtime.Attachments);

        // A DIFFERENT user's circuit: a different runtime, a different session path and a different
        // attachment root. (The Viewer is refused, so a Member/Operator account is used for the
        // second identity.)
        var operatorId = (await _auth.SeedAsync("circuit-op-operator", UserRole.Operator)).Id;
        using var otherTab = host.Provider.CreateScope();
        var otherAccess = await otherTab.ServiceProvider.GetRequiredService<IComposerRuntimeAccessor>()
            .AcquireForCircuitAsync(
                ComposerCircuitTesting.AuthenticatedState(
                    FacadeAuthorizationHarness.Principal("circuit-op-operator")), Ct);

        Assert.True(otherAccess.IsAuthorized);
        Assert.NotSame(firstAccess.Runtime!.Composer, otherAccess.Runtime!.Composer);

        // ...and their state paths are disjoint.
        var memberRoot = Path.Combine(host.StateDir, "composer", "users", _memberId.ToString());
        var operatorRoot = Path.Combine(host.StateDir, "composer", "users", operatorId.ToString());
        Assert.NotEqual<string>(memberRoot, operatorRoot);
        Assert.NotEqual<string>(
            firstAccess.Runtime.Attachments.AttachmentsRootPath,
            otherAccess.Runtime!.Attachments.AttachmentsRootPath);

        // The manager holds exactly TWO runtimes: one per account, not one per circuit.
        var entries = ManagerEntries(host.Manager).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Contains(_memberId, entries);
        Assert.Contains(operatorId, entries);
    }

    // ── 9. A demotion between two page operations hides the content ─────────────────────────────

    /// <summary>
    /// A DEMOTION BETWEEN TWO PAGE OPERATIONS HIDES THE CONTENT — WITHOUT A NEW CIRCUIT: the page
    /// rendered the Member's own runtime (its history is on screen), the account is then demoted in
    /// the DIRECTORY, and the page's NEXT operation re-queries the accessor — so the content is
    /// gone and the forbidden view is rendered instead.
    /// <para>
    /// The second operation is the page's own deferred-banner connect control, which re-runs the
    /// whole selection; the assertion is about the RENDERED result of that operation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADemotionBetweenTwoPageOperations_HidesTheContent_AndRendersTheForbiddenView()
    {
        await using var host = NewHost();

        // The Member's own per-user session, so the first render really shows a thread.
        var memberState = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var perUserPath = ComposerUserState.SessionFilePath(host.StateDir, memberState);
        Directory.CreateDirectory(Path.GetDirectoryName(perUserPath)!);
        var perUserSession = SharpCoder.AgentSession.Create("composer");
        perUserSession.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "per-user-history-marker"));
        await perUserSession.SaveAsync(perUserPath, Ct);

        await using var rendered = await RenderAsync(host, "circuit-op-member");

        // The first render shows the Member's own thread.
        Assert.Contains("per-user-history-marker", rendered.Html, StringComparison.Ordinal);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, BoundStatusOf(rendered.Page));

        // The account is demoted in the DIRECTORY — the circuit is NOT re-created.
        await _auth.SetRoleAsync("circuit-op-member", UserRole.Viewer);
        try
        {
            // The page's next operation re-queries the accessor.
            await rendered.ConnectRuntimeAsync();

            // The content is GONE and the forbidden view is rendered.
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));
            Assert.Null(BoundComposerOf(rendered.Page));
        }
        finally
        {
            await _auth.SetRoleAsync("circuit-op-member", UserRole.Member);
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the REAL <see cref="ComposerChat"/> page over the production circuit seam for one
    /// circuit identity and returns the markup, the component instance and the scope it lives in.
    /// </summary>
    private async Task<Rendered> RenderAsync(
        Host host,
        string gitHubId)
    {
        var state = new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)],
                authenticationType: "Cookies")));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = Task.FromResult(state),
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<ComposerChat>(0);
                    builder.AddComponentReferenceCapture(1, captured => page = (ComposerChat)captured);
                    builder.CloseComponent();
                }),
            });

            var output = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            return output.ToHtmlString();
        });

        Assert.NotNull(page);
        return new Rendered(html, page!, scope, renderer);
    }

    // ── Host fixture ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A host over the PRODUCTION registration seam: the shared factory, the legacy singleton
    /// Composer, the singleton manager, the scoped circuit accessor, the scoped runtime accessor
    /// (and its interface alias) and both facades.
    /// </summary>
    private Host NewHost(
        string primaryModel = Model,
        Func<string, string>? providerOf = null,
        Func<bool>? isTokenAvailable = null,
        Func<int, ComposerRuntimeAccess>? selection = null,
        bool authEnabled = true)
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = Model }, new ModelEntry { Name = CopilotModel }],
            },
            Composer = new ComposerConfig { Model = primaryModel },
        });
        services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
        services.AddSingleton(authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
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
            isTokenAvailable: isTokenAvailable ?? (() => true),
            providerOf: providerOf ?? (_ => "ollama-local")));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));

        // The global configuration facade the page injects for the notification-mode toggle. It is
        // the production type over the production resolver/policy.
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        // THE PRODUCTION SEAM.
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);

        if (selection is not null)
        {
            // The scripted accessor replaces ONLY the selection; the page, the facades and the
            // legacy singleton stay the production ones. The LAST registration for the interface
            // wins, exactly as a host override behaves.
            services.AddScoped<IComposerRuntimeAccessor>(_ =>
                new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
                    (call, _) => Task.FromResult(selection(call)), authEnabled: true));
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), stateDir);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The per-user runtime ids currently cached by the REAL manager.</summary>
    private static IEnumerable<int> ManagerEntries(ComposerRuntimeManager manager)
    {
        var field = typeof(ComposerRuntimeManager)
            .GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)field.GetValue(manager)!;
        return entries.Keys.Cast<int>().ToArray();
    }

    /// <summary>The Composer the page is currently bound to, or <c>null</c> when nothing is bound.</summary>
    private static Composer? BoundComposerOf(ComposerChat page)
    {
        var binding = BindingOf(page);
        if (binding is null)
            return null;

        var composer = binding.GetType().GetProperty("Composer")!.GetValue(binding);
        return (Composer?)composer;
    }

    /// <summary>
    /// The circuit status the page is currently rendering, read from its own binding. It is the
    /// PRODUCTION record the markup branches on, so it is what "the page decided" really means.
    /// </summary>
    private static ComposerChat.ComposerCircuitStatus BoundStatusOf(ComposerChat page)
    {
        var binding = BindingOf(page);
        Assert.NotNull(binding);

        var status = binding!.GetType().GetProperty("Status")!.GetValue(binding);
        return (ComposerChat.ComposerCircuitStatus)status!;
    }

    /// <summary>The page's current circuit binding, or <c>null</c> when it has none yet.</summary>
    private static object? BindingOf(ComposerChat page)
    {
        var field = typeof(ComposerChat)
            .GetField("_circuit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(page);
    }

    /// <summary>A JS runtime that answers nothing — the page only calls it for paste handling.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>
    /// ONE RENDERED CIRCUIT: the markup, the page instance, the scope it lives in and the renderer
    /// that dispatches its events. Disposing it releases the renderer and the scope in that order.
    /// </summary>
    private sealed class Rendered(
        string html,
        ComposerChat page,
        IServiceScope scope,
        HtmlRenderer renderer) : IAsyncDisposable
    {
        public string Html { get; } = html;

        public ComposerChat Page { get; } = page;

        public IServiceScope Scope { get; } = scope;

        /// <summary>
        /// Invokes the page's own deferred-banner connect control through the renderer (so the
        /// component's re-render happens on its dispatcher) and returns the re-rendered markup.
        /// </summary>
        internal async Task ConnectRuntimeAsync()
        {
            var method = typeof(ComposerChat).GetMethod(
                "ConnectRuntimeAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);

            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var task = (Task?)method!.Invoke(Page, null);
                Assert.NotNull(task);
                await task!;
            });
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            Scope.Dispose();
        }
    }

    /// <summary>The provider, the manager and the state directory of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public string StateDir { get; } = stateDir;

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // ORDER IS THE CONTRACT: the manager's actual teardown is AWAITED before the provider
            // goes away; every step runs even if an earlier one failed.
            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Circuit host cleanup failed", failures);
        }
    }

    /// <summary>
    /// THE RUNNER-INVOKED CLEANUP: xUnit v3 calls <c>DisposeAsync</c> for an
    /// <see cref="IAsyncLifetime"/>, so every cleanup step lives there.
    /// </summary>
    public ValueTask DisposeAsync()
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

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
