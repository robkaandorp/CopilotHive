using System.Reflection;
using System.Security.Claims;

using CopilotHive.Components;
using CopilotHive.Components.Layout;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Components.Rendering;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE REAL-SIBLING CIRCUIT-IDENTITY ORDERING CONTRACT, observed on ONE retained render tree and ONE
/// scoped <see cref="CircuitUserPrincipalAccessor"/>: the REAL <see cref="Routes"/> host, the REAL
/// <see cref="NavMenu"/> and the REAL routed <see cref="CopilotHive.Components.Pages.Knowledge"/> page,
/// over the REAL resolver/policy and a REAL user directory.
/// <para>
/// Routes, NavMenu and Knowledge acquire identity through their own parameter lifecycle and the
/// shared <see cref="ComponentActingUser"/> seam. The ordinary routing rows use <see cref="CascadeHost"/>;
/// the held-page rows keep the real NavMenu and Knowledge mounted under <see cref="SiblingBlockedHost"/>.
/// </para>
/// <para>
/// WHICH SIBLING OBSERVES WHAT: while the cascading task is PENDING the authenticated host's
/// <c>AuthorizeRouteView</c> awaits it itself and renders no routed page, so a pending window is observed
/// on the RETAINED siblings (<see cref="NavMenu"/> and the layout); the routed <see cref="CopilotHive.Components.Pages.Knowledge"/>
/// page participates as soon as the CURRENT task resolves, and in the current-terminal control. Every row
/// names the sibling it drives in its own summary.
/// </para>
/// <para>
/// DETERMINISM: every ordering step is a <see cref="TaskCompletionSource{TResult}"/> gate, released in a
/// <c>finally</c>, and every rendezvous is an event — the component's façade recorder — never a sleep.
/// All lifecycle/framework work the dispatcher owns is tracked and joined with a bounded,
/// cancellation-independent wait before the scope and renderer are torn down.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class CircuitAuthObservationOrderingTests : IAsyncLifetime
{
    private const string AdminId = "circuit-order-admin";
    private const string MemberId = "circuit-order-member";
    private const string DocTitle = "CIRCUIT-ORDER-DOC-MARKER";
    private const string FreshDocTitle = "CIRCUIT-ORDER-FRESH-DOC-MARKER";

    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(15);
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly List<RetainedTree> _trees = [];
    private readonly string? _clientId = Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable);
    private readonly string? _clientSecret = Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>Routes.razor</c> reads the OAuth environment ONCE per type initialisation, into a static
    /// readonly field. Another suite in the SAME test process may have initialised the type under
    /// different values, so the COMPILED flag — not the current environment — is the actual branch the
    /// real host renders. The routed-behaviour row reads this flag and asserts the correct behaviour of
    /// whichever branch was compiled, instead of silently assuming one.
    /// </summary>
    private static bool RoutesAuthEnabled => (bool)(typeof(Routes).GetField(
        "_authEnabled", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Routes._authEnabled was not found in the compiled component."))
        .GetValue(null)!;

    public async ValueTask InitializeAsync()
    {
        // Routes.razor and NavMenu.razor choose their AUTHENTICATED branch from the OAuth environment,
        // read ONCE per type initialisation. The collection serialises every environment-mutating suite,
        // and this class restores the previous values on dispose.
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, "circuit-order-client-id");
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, "circuit-order-client-secret");
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(MemberId, UserRole.Member);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var tree in _trees)
        {
            try { await tree.DisposeAsync(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        try { _auth.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable, _clientId);
        Environment.SetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable, _clientSecret);
        if (failures.Count != 0) throw new AggregateException("Retained circuit-ordering roots failed to clean up.", failures);
    }

    private static ClaimsPrincipal Principal(string id) => FacadeAuthorizationHarness.Principal(id);

    private static AuthenticationState StateOf(string id) => new(Principal(id));

    private static AuthenticationState SignedOut() => new(FacadeAuthorizationHarness.Anonymous());

    private static TaskCompletionSource<AuthenticationState> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? IdentityKeyOf(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // ══════════════════════════════════════════════════════════════════════════
    //  One façade call, as the REAL production façade answered it
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE THREE REAL FACADE BOUNDARIES these siblings reach, each named so a release can be attributed
    /// SEPARATELY: the routed Knowledge page's document list, the nav menu's knowledge-document count, and
    /// the nav menu's pending-clarification count. They are distinct operations on distinct decorators, so
    /// a denial at one must never be allowed to stand in for a denial (or a success) at another.
    /// </summary>
    internal enum FacadeBoundary
    {
        /// <summary><c>IOperationalFacade.GetKnowledgeDocumentsAsync</c> — the Knowledge page's read.</summary>
        KnowledgeDocuments,

        /// <summary><c>IOperationalFacade.GetKnowledgeDocumentCountAsync</c> — the nav badge count.</summary>
        NavDocumentCount,

        /// <summary><c>IClarificationFacade.GetPendingHumanCountAsync</c> — the nav clarifications count.</summary>
        NavClarificationCount,
    }

    /// <summary>
    /// One recorded delegation: the boundary it crossed, the ACTUAL principal argument recorded as an
    /// EXPLICIT NULLNESS FLAG plus a key (never a collapsed string — an anonymous principal is a NON-NULL
    /// principal and must not be conflated with the <c>null</c> a superseded completion forwards), and the
    /// ACTUAL production result with the protected value the caller obtained.
    /// </summary>
    internal sealed record FacadeCall(
        FacadeBoundary Boundary,
        bool ForwardedNullPrincipal,
        string? ActingIdentity,
        bool ForwardedAnonymousPrincipal,
        bool Success,
        FacadeErrorKind Kind,
        string? SearchQuery = null,
        int DocumentCount = 0,
        int CountValue = 0,
        KnowledgeDocumentListProjection? DocumentValue = null)
    {
        /// <summary>Whether the call carried a principal that could name an identity.</summary>
        internal bool ForwardedAuthenticatedIdentity => !ForwardedNullPrincipal && ActingIdentity is not null;
    }

    /// <summary>
    /// Records every delegation of the façade decorators over the PRODUCTION façades — the ACTUAL argument
    /// (including its nullness and whether it was a non-null ANONYMOUS principal) and the ACTUAL result with
    /// its protected values — and owns the event-driven rendezvous for the real component lifecycles.
    /// Every query is scoped by <see cref="FacadeBoundary"/> and call INDEX, so a baseline read, a separate
    /// <see cref="Routes"/>-branch read and a per-release read can never be substituted for one another.
    /// </summary>
    private sealed class CallRecorder
    {
        private readonly object _lock = new();
        private readonly List<FacadeCall> _calls = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IReadOnlyList<FacadeCall> Calls
        {
            get { lock (_lock) return [.. _calls]; }
        }

        internal void Record(FacadeCall call)
        {
            TaskCompletionSource changed;
            lock (_lock)
            {
                _calls.Add(call);
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            changed.TrySetResult();
        }

        /// <summary>Count of recorded calls matching <paramref name="predicate"/> right now.</summary>
        internal int Count(Func<FacadeCall, bool> predicate)
        {
            lock (_lock) return _calls.Count(predicate);
        }

        /// <summary>Count of calls recorded at <paramref name="boundary"/> so far (the watermark cursor).</summary>
        internal int CountAt(FacadeBoundary boundary) =>
            Count(call => call.Boundary == boundary);

        /// <summary>
        /// The <paramref name="boundary"/> calls recorded at or after <paramref name="fromIndex"/>, in
        /// order. This is how ONE release is attributed: take the watermark before releasing, then read
        /// only what landed at that boundary afterwards.
        /// </summary>
        internal IReadOnlyList<FacadeCall> Since(FacadeBoundary boundary, int fromIndex)
        {
            lock (_lock)
            {
                var seen = 0;
                var result = new List<FacadeCall>();
                foreach (var call in _calls)
                {
                    if (call.Boundary != boundary) continue;
                    if (seen++ < fromIndex) continue;
                    result.Add(call);
                }
                return result;
            }
        }

        /// <summary>Waits (bounded, event-driven) for the first recorded call matching the predicate.</summary>
        internal async Task<FacadeCall> WaitForAsync(Func<FacadeCall, bool> predicate)
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    foreach (var call in _calls)
                        if (predicate(call)) return call;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(JoinTimeout, Ct);
            }
        }
    }

    /// <summary>
    /// The production operational façade, recorded on the circuit's one recorder. The identity argument is
    /// classified ONCE, here, into an explicit nullness flag plus the identity key, so every row asserts the
    /// real shape instead of inferring it from a collapsed string.
    /// </summary>
    private sealed class RecordingOperational(IOperationalFacade production, CallRecorder recorder) : IOperationalFacade
    {
        internal CallRecorder Recorder => recorder;
        internal IOperationalFacade Production => production;

        public async Task<FacadeResult<KnowledgeDocumentListProjection>> GetKnowledgeDocumentsAsync(
            string? searchQuery, ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            var result = await production.GetKnowledgeDocumentsAsync(searchQuery, actingUser, ct).ConfigureAwait(false);
            Recorder.Record(new FacadeCall(
                FacadeBoundary.KnowledgeDocuments,
                ForwardedNullPrincipal: actingUser is null,
                ActingIdentity: IdentityKeyOf(actingUser),
                ForwardedAnonymousPrincipal: actingUser is not null && actingUser.Identity?.IsAuthenticated != true,
                Success: result.Success,
                Kind: result.Kind,
                SearchQuery: searchQuery,
                DocumentCount: result.Value?.Documents.Count ?? 0, DocumentValue: result.Value));
            return result;
        }

        public async Task<FacadeResult<int>> GetKnowledgeDocumentCountAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            var result = await production.GetKnowledgeDocumentCountAsync(actingUser, ct).ConfigureAwait(false);
            Recorder.Record(new FacadeCall(
                FacadeBoundary.NavDocumentCount,
                ForwardedNullPrincipal: actingUser is null,
                ActingIdentity: IdentityKeyOf(actingUser),
                ForwardedAnonymousPrincipal: actingUser is not null && actingUser.Identity?.IsAuthenticated != true,
                Success: result.Success,
                Kind: result.Kind,
                CountValue: result.Value));
            return result;
        }

        public Task<FacadeResult> ResetBrainSessionAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.ResetBrainSessionAsync(actingUser, ct);

        public Task<FacadeResult<BrainStats>> GetBrainStatsAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetBrainStatsAsync(actingUser, ct);

        public Task<FacadeResult<OrchestratorInfo>> GetOrchestratorInfoAsync(ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetOrchestratorInfoAsync(actingUser, ct);

        public Task<FacadeResult<IReadOnlyList<LlmSessionInfo>>> GetSharedLlmSessionsAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetSharedLlmSessionsAsync(actingUser, ct);

        public Task<FacadeResult<KnowledgeDocumentProjection>> GetKnowledgeDocumentAsync(
            string documentId, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetKnowledgeDocumentAsync(documentId, actingUser, ct);
    }

    /// <summary>The production clarification façade, recorded — the nav menu's OWN clarification boundary.</summary>
    private sealed class RecordingClarification(IClarificationFacade production, CallRecorder recorder) : IClarificationFacade
    {
        internal CallRecorder Recorder => recorder;

        public Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetPendingHumanRequestsAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetPendingHumanRequestsAsync(actingUser, ct);

        public async Task<FacadeResult<int>> GetPendingHumanCountAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
        {
            var result = await production.GetPendingHumanCountAsync(actingUser, ct).ConfigureAwait(false);
            Recorder.Record(new FacadeCall(
                FacadeBoundary.NavClarificationCount,
                ForwardedNullPrincipal: actingUser is null,
                ActingIdentity: IdentityKeyOf(actingUser),
                ForwardedAnonymousPrincipal: actingUser is not null && actingUser.Identity?.IsAuthenticated != true,
                Success: result.Success,
                Kind: result.Kind,
                CountValue: result.Value));
            return result;
        }

        public Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetAllRequestsAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetAllRequestsAsync(actingUser, ct);

        public Task<FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>> GetPersistedHistoryAsync(
            ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.GetPersistedHistoryAsync(actingUser, ct);

        public Task<FacadeResult<ClarificationAnswerResult>> SubmitAnswerAsync(
            string id, string answer, ClaimsPrincipal? actingUser, CancellationToken ct) =>
            production.SubmitAnswerAsync(id, answer, actingUser, ct);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The retained tree
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The retained root that hands a MUTABLE cascade task to the REAL <see cref="Routes"/> host. A new
    /// task object is what the framework would deliver on a sign-out/identity change: the very same
    /// object — by reference — reaches every sibling observer in the tree.
    /// </summary>
    public sealed class CascadeHost : ComponentBase
    {
        [Parameter] public Task<AuthenticationState> Initial { get; set; } = null!;
        [Parameter] public Action<CascadeHost> Capture { get; set; } = null!;
        private Task<AuthenticationState> _state = null!;

        protected override void OnInitialized()
        {
            _state = Initial;
            Capture(this);
        }

        /// <summary>The EXACT task object last delivered by this real cascade host.</summary>
        internal Task<AuthenticationState> Current => _state;

        internal void Set(Task<AuthenticationState> state)
        {
            _state = state;
            StateHasChanged();
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", _state);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(content =>
            {
                content.OpenComponent<Routes>(0);
                content.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    /// <summary>
    /// TEST-ONLY CASCADE HOST for the blocked interval. It supplies ONE already-completed authentication
    /// cascade to the REAL <see cref="Routes"/> tree, so the authenticated <c>AuthorizeRouteView</c> does
    /// NOT remove its routed page, and a SECOND unnamed cascade carries the EXACT ordering task to a
    /// SECOND branch holding the REAL <see cref="NavMenu"/> and the REAL
    /// <see cref="CopilotHive.Components.Pages.Knowledge"/> page. Every one of those production
    /// components calls the REAL <see cref="ComponentActingUser.AcquireAsync"/> with the exact task on
    /// the SAME scoped accessor through its own lifecycle. No production source is modified and no
    /// lifecycle method is invoked reflectively.
    /// </summary>
    public sealed class SiblingBlockedHost : ComponentBase
    {
        [Parameter] public Task<AuthenticationState> InitialAuthentication { get; set; } = null!;
        [Parameter] public Task<AuthenticationState> InitialOrdering { get; set; } = null!;
        [Parameter] public Action<SiblingBlockedHost> Capture { get; set; } = null!;

        private Task<AuthenticationState> _authentication = null!;
        private Task<AuthenticationState> _ordering = null!;

        protected override void OnInitialized()
        {
            _authentication = InitialAuthentication;
            _ordering = InitialOrdering;
            Capture(this);
        }

        /// <summary>The EXACT ordering task last delivered through this real cascade update.</summary>
        internal Task<AuthenticationState> Ordering => _ordering;

        internal void SetOrdering(Task<AuthenticationState> ordering)
        {
            _ordering = ordering;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            // BRANCH 1: the REAL Routes host gets an ALREADY-COMPLETED auth cascade, so its
            // AuthorizeRouteView keeps the real routed page mounted. This is the REAL host in the same
            // render tree, the same scope and the same scoped accessor.
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", _authentication);
            b.AddAttribute(2, "IsFixed", true);
            b.AddAttribute(3, "ChildContent", RoutesContent);
            b.CloseComponent();

            // BRANCH 2: the REAL NavMenu and the REAL Knowledge page are RETAINED SIBLINGS under the
            // exact ORDERING cascade. They are production components with production lifecycles: each
            // OnParametersSetAsync receives the exact Task<AuthenticationState> reference delivered by
            // the real cascade update, parks on ComponentActingUser.AcquireAsync and only then reaches
            // its own production facade. The SECTION marker lets a row read this branch's markup.
            b.OpenElement(1, "section");
            b.AddAttribute(2, "id", SiblingBranchMarker);
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(3);
            b.AddAttribute(1, "Value", _ordering);
            b.AddAttribute(2, "ChildContent", (RenderFragment)(content =>
            {
                content.OpenComponent<NavMenu>(0);
                content.AddComponentReferenceCapture(1, captured => SiblingNav = (NavMenu)captured);
                content.CloseComponent();
                content.OpenComponent<CopilotHive.Components.Pages.Knowledge>(2);
                content.AddComponentReferenceCapture(3, captured => SiblingPage = (CopilotHive.Components.Pages.Knowledge)captured);
                content.CloseComponent();
            }));
            b.CloseComponent();
            b.CloseElement();
        }

        private static readonly RenderFragment RoutesContent = content =>
        {
            content.OpenComponent<Routes>(0);
            content.CloseComponent();
        };

        /// <summary>The LIVE real NavMenu of the ordering branch, captured by the renderer.</summary>
        internal NavMenu? SiblingNav { get; private set; }

        /// <summary>The LIVE real Knowledge page of the ordering branch, captured by the renderer.</summary>
        internal CopilotHive.Components.Pages.Knowledge? SiblingPage { get; private set; }
    }

    /// <summary>Marker used to extract the ORDERING branch's real rendered markup from the root HTML.</summary>
    internal const string SiblingBranchMarker = "sibling-ordering-branch";

    /// <summary>
    /// ONE browser circuit: one provider, one scope (therefore one scoped
    /// <see cref="CircuitUserPrincipalAccessor"/>), one renderer and one retained tree. Every task the
    /// dispatcher or a component lifecycle starts is TRACKED and joined with a bounded,
    /// cancellation-independent wait; every gate is released BEFORE any join.
    /// </summary>
    private sealed class RetainedTree : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly HtmlRenderer _renderer;
        private readonly Func<string> _html;
        private readonly CircuitObservationTestWork _work = new(JoinTimeout);

        internal RetainedTree(
            ServiceProvider provider,
            AsyncServiceScope scope,
            HtmlRenderer renderer,
            Func<string> html,
            CircuitUserPrincipalAccessor accessor,
            CallRecorder facadeRecorder,
            RecordingOperational operational,
            RecordingClarification clarification,
            KnowledgeGraph graph,
            ClarificationRequest freshClarification,
            SiblingBlockedHost? siblingHost = null)
        {
            _provider = provider;
            _scope = scope;
            _renderer = renderer;
            _html = html;
            Accessor = accessor;
            FacadeRecorder = facadeRecorder;
            Operational = operational;
            Clarification = clarification;
            Graph = graph;
            FreshClarification = freshClarification;
            SiblingHost = siblingHost;
        }

        /// <summary>The test-only blocked-interval cascade host, when this tree has one.</summary>
        internal SiblingBlockedHost? SiblingHost { get; set; }

        /// <summary>The EXACT ordering task last delivered by the active host.</summary>
        internal Task<AuthenticationState>? Ordering =>
            SiblingHost?.Ordering ?? Host?.Current;

        /// <summary>The LIVE real NavMenu sibling of the ordering branch.</summary>
        internal NavMenu OrderingNav => SiblingHost?.SiblingNav
            ?? throw new InvalidOperationException("This tree has no sibling-blocked host.");

        /// <summary>The LIVE real Knowledge page sibling of the ordering branch.</summary>
        internal CopilotHive.Components.Pages.Knowledge OrderingPage => SiblingHost?.SiblingPage
            ?? throw new InvalidOperationException("This tree has no sibling-blocked host.");

        /// <summary>The ONE scoped accessor the whole retained tree resolves.</summary>
        internal CircuitUserPrincipalAccessor Accessor { get; }

        /// <summary>The retained parent that delivers cascading updates to the real host.</summary>
        internal CascadeHost Host { get; set; } = null!;

        internal RecordingOperational Operational { get; }

        /// <summary>The live knowledge graph, for a row to seed a DISTINCTIVE fresh document.</summary>
        internal KnowledgeGraph Graph { get; }
        internal ClarificationRequest FreshClarification { get; }

        /// <summary>The single facade recorder for BOTH decorators of this circuit.</summary>
        internal CallRecorder FacadeRecorder { get; }

        /// <summary>The clarification façade decorator (the NAV MENU's own boundary).</summary>
        internal RecordingClarification Clarification { get; }

        /// <summary>A gate released during cleanup, before any join, whatever the scenario outcome.</summary>
        internal void ReleaseBeforeJoin(Action release) => _work.ReleaseBeforeJoin(release);
        internal T Track<T>(T task) where T : Task => _work.Track(task);
        internal Task StartAsync(Func<Task> start) => _work.Start(start);

        private Task<T> InspectAsync<T>(Func<T> read) => _work.Start(() => _renderer.Dispatcher.InvokeAsync(read))
            .WaitAsync(JoinTimeout, CancellationToken.None);

        internal Task<string> HtmlAsync() => InspectAsync(_html);

        internal async Task<string> SiblingHtmlAsync()
        {
            var html = await HtmlAsync();
            var start = html.IndexOf($"""<section id="{SiblingBranchMarker}">""", StringComparison.Ordinal);
            Assert.True(start >= 0, "The test-only sibling section must be rendered.");
            var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
            Assert.True(end > start);
            return html[start..end];
        }

        internal Task<T> PropertyAsync<T>(object component, string property) => InspectAsync(() =>
            (T)component.GetType().GetProperty(property, NonPublicInstance)!.GetValue(component)!);

        /// <summary>
        /// Finds the REAL sibling component of the ORDERING branch by its retained identity. The
        /// renderer's component map is enumerated on the dispatcher; the returned object is the live
        /// production component, never a test double.
        /// </summary>
        internal Task<IReadOnlyList<object>> FindAllAsync(string typeName) => InspectAsync(() =>
        {
#pragma warning disable BL0006
            var inner = typeof(HtmlRenderer).GetField("_passiveHtmlRenderer", NonPublicInstance)!.GetValue(_renderer)!;
            var found = new List<object>();
            if (typeof(Renderer).GetField("_componentStateById", NonPublicInstance)?.GetValue(inner)
                is not System.Collections.IDictionary map)
                return (IReadOnlyList<object>)found;
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var component = entry.Value?.GetType()
                    .GetProperty("Component", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(entry.Value);
                if (component?.GetType().Name == typeName) found.Add(component);
            }
            return (IReadOnlyList<object>)found;
#pragma warning restore BL0006
        });

        /// <summary>
        /// Delivers a NEW cascading task through the REAL parent and returns the framework's own
        /// lifecycle work for that delivery. The pending-task collector is installed only around the
        /// synchronous delivery, so the newer render can finish without joining an older held operation
        /// (the two lifecycles are then joined separately by the caller).
        /// </summary>
        internal Task<Task> SetCascadeAsync(CascadeHost host, Task<AuthenticationState> state) =>
            DeliverAsync(() => host.Set(state));

        internal Task<Task> SetSiblingCascadeAsync(Task<AuthenticationState> state) =>
            DeliverAsync(() => (SiblingHost ?? throw new InvalidOperationException("No sibling host.")).SetOrdering(state));

        private Task<Task> DeliverAsync(Action change)
        {
#pragma warning disable BL0006 // Capture the framework lifecycle tasks, not a test-invoked callback.
            var inner = typeof(HtmlRenderer).GetField("_passiveHtmlRenderer", NonPublicInstance)!.GetValue(_renderer)!;
            var pendingField = typeof(Renderer).GetField("_pendingTasks", NonPublicInstance)!;
            var delivery = _work.Start(() => _renderer.Dispatcher.InvokeAsync<Task>(() =>
            {
                var previous = pendingField.GetValue(inner);
                var pending = new List<Task>();
                pendingField.SetValue(inner, pending);
                Exception? fault = null;
                try { change(); }
                catch (Exception ex) { fault = ex; }
                finally { pendingField.SetValue(inner, previous); }
                if (fault is not null) pending.Add(Task.FromException(fault));
                return Task.WhenAll(pending);
            }), static queued => queued.Unwrap());
#pragma warning restore BL0006
            // The unwrapped operation covers even a delivery still queued when cleanup closes admission.
            return delivery.WaitAsync(JoinTimeout, CancellationToken.None);
        }

        internal Task DrainAsync() => _work.Start(() => _renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask))
            .WaitAsync(JoinTimeout, CancellationToken.None);

        /// <summary>
        /// Reads a component's private field ON THE DISPATCHER, so the read observes exactly the state
        /// the component's own lifecycle writes.
        /// </summary>
        internal Task<T> FieldAsync<T>(object component, string name) => InspectAsync(() =>
            (T)component.GetType().GetField(name, NonPublicInstance)!.GetValue(component)!);

        internal Task<object?> FindComponentAsync(string typeName) => InspectAsync(() =>
        {
#pragma warning disable BL0006
            var inner = typeof(HtmlRenderer).GetField("_passiveHtmlRenderer", NonPublicInstance)!.GetValue(_renderer)!;
            if (typeof(Renderer).GetField("_componentStateById", NonPublicInstance)?.GetValue(inner)
                is not System.Collections.IDictionary map)
                return null;
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                var component = entry.Value?.GetType()
                    .GetProperty("Component", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(entry.Value);
                if (component?.GetType().Name == typeName) return component;
            }
            return null;
#pragma warning restore BL0006
        });

        private Func<Task>[] DisposeSteps =>
            [() => _renderer.DisposeAsync().AsTask(), () => _scope.DisposeAsync().AsTask(),
             () => _provider.DisposeAsync().AsTask()];

        internal Task RunAndDisposeAsync(Func<Task> scenario) => _work.RunAsync(scenario, DisposeSteps);

        public ValueTask DisposeAsync() => new(_work.CompleteAsync(null, DisposeSteps));
    }

    private sealed class NullJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult<TValue>(default!);
    }

    private sealed class TestNavigation : NavigationManager
    {
        internal TestNavigation() => Initialize("http://localhost/", "http://localhost/knowledge");
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class NoOpInterception : Microsoft.AspNetCore.Components.Routing.INavigationInterception
    {
        public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
    }

    private sealed class NoOpScrolling : Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash
    {
        public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
    }

    private sealed class FixedStateProvider : AuthenticationStateProvider
    {
        private readonly AuthenticationState _state;
        internal FixedStateProvider(AuthenticationState state) => _state = state;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);
    }

    /// <summary>
    /// Builds the ONE retained tree: the real host, layout, nav menu and routed /knowledge page over the
    /// real resolver/policy/directory and the real scoped accessor, with the real façades decorated so
    /// their inputs and outcomes are recorded. The initial cascade carries <paramref name="initialId"/>.
    /// </summary>
    private async Task<RetainedTree> RenderTreeAsync(string initialId, bool siblingBlocked = false)
    {
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync(
            "circuit-order-doc", DocTitle, DocumentType.Implementation, "body",
            topic: "ordering", subtopic: null, author: "author", tags: ["ordering-tag"], ct: Ct);

        var dashboard = new DashboardStateService(
            new WorkerPool(), new GoalPipelineManager(), new DashboardLogSink(), new ProgressLog(),
            knowledgeGraph: graph);
        var queue = new ClarificationQueueService();
        var mode = HiveAuthMode.AuthenticationEnabled;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton(queue);
        services.AddSingleton(dashboard);
        services.AddSingleton(graph);
        services.AddSingleton<IGoalStore>(new RecordingGoalStore());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJs());
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddSingleton<Microsoft.AspNetCore.Components.Routing.INavigationInterception>(new NoOpInterception());
        services.AddSingleton<Microsoft.AspNetCore.Components.Routing.IScrollToLocationHash>(new NoOpScrolling());
        services.AddSingleton(mode);
        services.AddSingleton(_auth.Policy);
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));

        // THE SAME SCOPED REGISTRATION Program.cs MAKES: one accessor per circuit.
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));

        var registry = new LlmSessionRegistry();
        services.AddSingleton(registry);
        var facadeRecorder = new CallRecorder();
        var operational = new RecordingOperational(
            new OperationalFacade(
                dashboard, registry, _auth.Resolver(mode), _auth.Policy, NullLogger<OperationalFacade>.Instance),
            facadeRecorder);
        services.AddSingleton<IOperationalFacade>(operational);
        var clarification = new RecordingClarification(
            new ClarificationFacade(
                queue, new RecordingGoalStore(), _auth.Resolver(mode), _auth.Policy,
                NullLogger<ClarificationFacade>.Instance),
            facadeRecorder);
        services.AddSingleton<IClarificationFacade>(clarification);

        // DISTINCTIVE PROTECTED DATA that only a healthy, CURRENT identity can reach through the real
        // production facades: one knowledge document and one pending human clarification. The queue is
        // populated BEFORE any component subscribes, so no OnChanged refresh races the initial render.
        queue.Enqueue(new ClarificationRequest
        {
            Id = "circuit-order-clarification",
            GoalId = "circuit-order-goal",
            WorkerRole = "tester",
            Question = "SIBLING-CLARIFICATION-MARKER-9F2",
            Status = ClarificationStatus.AwaitingHuman,
        });

        var freshClarification = new ClarificationRequest { Id = "fresh-awaiting-human" };
        queue.Enqueue(freshClarification);

        // Authorization core is registered because the authenticated branch of the host renders an
        // AuthorizeRouteView. The cascade VALUE itself is supplied by the retained parent.
        services.AddAuthorizationCore();
        services.AddSingleton<AuthenticationStateProvider>(
            new FixedStateProvider(StateOf(initialId)));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        var scope = provider.CreateAsyncScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        var accessor = scope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();

        CascadeHost? host = null;
        SiblingBlockedHost? siblingHost = null;
        Func<string> html = () => "";
        var tree = new RetainedTree(provider, scope, renderer, () => html(), accessor, facadeRecorder,
            operational, clarification, graph, freshClarification);
        _trees.Add(tree);
        var initial = Task.FromResult(StateOf(initialId));
        await tree.Track(renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (siblingBlocked)
            {
                var root = await renderer.RenderComponentAsync<SiblingBlockedHost>(ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        ["InitialAuthentication"] = initial,
                        ["InitialOrdering"] = initial,
                        ["Capture"] = (Action<SiblingBlockedHost>)(captured => siblingHost = captured),
                    }));
                html = root.ToHtmlString;
            }
            else
            {
                var root = await renderer.RenderComponentAsync<CascadeHost>(ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        ["Initial"] = initial,
                        ["Capture"] = (Action<CascadeHost>)(captured => host = captured),
                    }));
                html = root.ToHtmlString;
            }
        })).WaitAsync(JoinTimeout, CancellationToken.None);
        tree.SiblingHost = siblingHost;
        await tree.DrainAsync();
        Assert.NotNull(await tree.FindComponentAsync(nameof(Routes)));
        Assert.NotNull(await tree.FindComponentAsync(nameof(NavMenu)));
        Assert.NotNull(await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge)));
        if (siblingBlocked)
        {
            Assert.NotNull(siblingHost);
            Assert.Equal(2, (await tree.FindAllAsync(nameof(NavMenu))).Count);
            Assert.Equal(2, (await tree.FindAllAsync(nameof(CopilotHive.Components.Pages.Knowledge))).Count);
        }
        else
        {
            Assert.NotNull(host);
            tree.Host = host!;
        }
        return tree;
    }

    private Task<RetainedTree> RenderSiblingTreeAsync(string initialId) => RenderTreeAsync(initialId, siblingBlocked: true);

    // ══════════════════════════════════════════════════════════════════════════
    //  Real siblings: ordering of two overlapping cascading updates
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RetainedTreeCleanup_CanceledScenarioAndEarlyFault_ClosesAdmissionAndJoinsBothDeliveries()
    {
        var tree = await RenderSiblingTreeAsync(AdminId);
        var older = Gate();
        var newer = Gate();
        var releaseFault = new InvalidOperationException("first release failed");
        var lastJoinFault = new InvalidOperationException("last join was attempted");
        var primary = new InvalidOperationException("primary scenario failure");
        using var scenario = new CancellationTokenSource();
        Task? olderLifecycle = null;
        Task? newerLifecycle = null;
        tree.ReleaseBeforeJoin(() => throw releaseFault);
        tree.ReleaseBeforeJoin(() => Assert.Throws<InvalidOperationException>(() =>
        {
            _ = tree.StartAsync(() => throw new Xunit.Sdk.XunitException("work started after admission closed"));
        }));
        tree.ReleaseBeforeJoin(() => older.TrySetResult(StateOf(MemberId)));
        tree.ReleaseBeforeJoin(() => newer.TrySetResult(StateOf(AdminId)));

        var error = await Assert.ThrowsAsync<AggregateException>(() => tree.RunAndDisposeAsync(async () =>
        {
            olderLifecycle = await tree.SetSiblingCascadeAsync(older.Task);
            newerLifecycle = await tree.SetSiblingCascadeAsync(newer.Task);
            await tree.DrainAsync();
            _ = tree.Track(Task.FromException(lastJoinFault));
            Assert.False(olderLifecycle.IsCompleted);
            Assert.False(newerLifecycle.IsCompleted);
            scenario.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newerLifecycle.WaitAsync(scenario.Token));
            throw primary;
        }));

        Assert.Collection(error.InnerExceptions, ex => Assert.Same(primary, ex),
            ex => Assert.Same(releaseFault, ex), ex => Assert.Same(lastJoinFault, ex));
        Assert.True(olderLifecycle!.IsCompletedSuccessfully);
        Assert.True(newerLifecycle!.IsCompletedSuccessfully);
        Assert.True(older.Task.IsCompletedSuccessfully);
        Assert.True(newer.Task.IsCompletedSuccessfully);
    }

    /// <summary>
    /// THE ONE PER-RELEASE, PER-BOUNDARY PROOF used by the four overlap rows.
    /// <para>
    /// A pooled "any call with a null identity was denied" assertion is not proof: it lets a denial from
    /// one operation hide a success at another, and a collapsed identity string conflates the <c>null</c> a
    /// superseded completion forwards with a NON-NULL anonymous principal. This helper instead attributes
    /// ONE release at ONE boundary by the call INDEX watermark taken immediately before that release, and
    /// asserts, for that release only:
    /// </para>
    /// <list type="bullet">
    /// <item><b>T1 (superseded)</b> forwarded EXACTLY <c>null</c> (not an anonymous principal, not the
    /// recorded identity), received <see cref="FacadeErrorKind.Forbidden"/>, obtained NO protected value,
    /// and produced no further authorized read at that boundary;</item>
    /// <item><b>T2 (current)</b> forwarded the EXPECTED AUTHENTICATED identity and obtained a FRESH
    /// successful result at that same boundary.</item>
    /// </list>
    /// </summary>
    private sealed class BoundaryProof(CallRecorder recorder)
    {
        /// <summary>The call index at this boundary right now — the watermark for one release.</summary>
        internal int Mark(FacadeBoundary boundary) => recorder.CountAt(boundary);

        /// <summary>
        /// Asserts the SUPERSEDED release at <paramref name="boundary"/> produced exactly one call since
        /// <paramref name="mark"/>, that it forwarded exactly <c>null</c>, and that it was refused with no
        /// protected value.
        /// </summary>
        /// <param name="boundary">The boundary this release reached.</param>
        /// <param name="mark">The watermark taken immediately before the release.</param>
        /// <param name="label">Row label used in failure messages.</param>
        /// <returns>The single superseded call, for further per-boundary assertions.</returns>
        internal FacadeCall AssertSupersededDenied(FacadeBoundary boundary, int mark, string label)
        {
            var since = recorder.Since(boundary, mark);
            var call = Assert.Single(since);
            Assert.True(call.ForwardedNullPrincipal,
                $"{label}/{boundary}: T1 must forward EXACTLY null — a non-null principal (anonymous or otherwise) is not superseded");
            Assert.False(call.ForwardedAnonymousPrincipal,
                $"{label}/{boundary}: T1 must not forward a non-null ANONYMOUS principal");
            Assert.Null(call.ActingIdentity);
            Assert.False(call.Success, $"{label}/{boundary}: T1 must not obtain protected data");
            Assert.Equal(FacadeErrorKind.Forbidden, call.Kind);
            Assert.Null(call.DocumentValue);
            Assert.Equal(0, call.DocumentCount);
            Assert.Equal(0, call.CountValue);

            // NO ADDITIONAL AUTHORIZED READ at this boundary for this release.
            Assert.DoesNotContain(since, other => other.Success);
            return call;
        }

        /// <summary>
        /// Asserts the CURRENT release at <paramref name="boundary"/> produced exactly one call since
        /// <paramref name="mark"/>, forwarding the expected authenticated identity and succeeding.
        /// </summary>
        /// <param name="boundary">The boundary this release reached.</param>
        /// <param name="mark">The watermark taken immediately before the release.</param>
        /// <param name="expectedIdentity">The authenticated identity the current task must forward.</param>
        /// <param name="label">Row label used in failure messages.</param>
        /// <returns>The successful current call.</returns>
        internal FacadeCall AssertCurrentAuthorized(
            FacadeBoundary boundary, int mark, string expectedIdentity, string label)
        {
            var call = Assert.Single(recorder.Since(boundary, mark));
            Assert.True(call.Success, $"{label}/{boundary}: T2 must obtain a successful authorized read");
            Assert.False(call.ForwardedNullPrincipal,
                $"{label}/{boundary}: T2 must forward a principal (it is the current registration)");
            Assert.False(call.ForwardedAnonymousPrincipal);
            Assert.Equal(expectedIdentity, call.ActingIdentity);
            Assert.Equal(FacadeErrorKind.None, call.Kind);
            return call;
        }

    }

    /// <summary>
    /// OLDER COMPLETES WHILE THE NEWER IS STILL PENDING: the retained tree receives UPDATE 1 (an OLDER
    /// cascade task) and UPDATE 2 (a NEWER one), both PENDING. The retained <see cref="NavMenu"/> reaches
    /// its own <c>OnParametersSetAsync</c> await for each delivery, so its refresh generation advances
    /// twice while no façade call has happened. Completing the OLDER task FIRST makes its parked lifecycle
    /// resume and resolve as SUPERSEDED: the façade call carries the NULL identity (the claims the stale
    /// task was gated on are never forwarded), it is refused, and the circuit stays initialized with the
    /// account it had established. Releasing the NEWER task then authorizes a FRESH read and the routed
    /// <see cref="CopilotHive.Components.Pages.Knowledge"/> page really renders its protected document.
    /// <para>
    /// ROUTED-PAGE PARTICIPATION DURING A PENDING WINDOW: the authenticated branch of the host is an
    /// <c>AuthorizeRouteView</c>, which awaits the cascading task ITSELF and therefore renders no routed
    /// page while the cascade is pending. The pending window is consequently observed on the siblings that
    /// ARE retained (<c>NavMenu</c> and the layout), and the routed page participates as soon as the current
    /// task resolves — which is exactly the "current task still authorizes a fresh read" half below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OlderCompletingWhileTheNewerIsPending_IsDenied_AndTheCurrentTaskStillAuthorizesAFreshRead()
    {
        var tree = await RenderTreeAsync(AdminId);
        await tree.RunAndDisposeAsync(async () =>
        {
            var nav = (await tree.FindComponentAsync(nameof(NavMenu)))!;

            // ── AUTHORIZED BASELINE through the real tree (non-vacuity). ──
            Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
            Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
            Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
            Assert.True((await tree.Operational.Recorder.WaitForAsync(call =>
                call.Boundary == FacadeBoundary.KnowledgeDocuments && call.ActingIdentity == AdminId)).Success);

            var older = Gate();
            var newer = Gate();
            try
            {
                tree.ReleaseBeforeJoin(() => older.TrySetResult(StateOf(MemberId)));
                tree.ReleaseBeforeJoin(() => newer.TrySetResult(StateOf(AdminId)));

                // ── UPDATE 1 (OLDER = a DIFFERENT authenticated account), still PENDING. ──
                var olderLifecycle = await tree.SetCascadeAsync(tree.Host, older.Task);
                await tree.DrainAsync();
                var navGenerationAfterOlder = await tree.FieldAsync<int>(nav, "_refreshGeneration");
                Assert.Same(nav, await tree.FindComponentAsync(nameof(NavMenu)));
                Assert.Equal(0, tree.Operational.Recorder.Count(call => call.ForwardedNullPrincipal));

                // ── UPDATE 2 (NEWER = the established admin), ALSO PENDING: supersedes the older now. ──
                var newerLifecycle = await tree.SetCascadeAsync(tree.Host, newer.Task);
                await tree.DrainAsync();
                Assert.True(await tree.FieldAsync<int>(nav, "_refreshGeneration") > navGenerationAfterOlder,
                    "the retained nav menu's own lifecycle must have run for the newer cascade");
                Assert.False(newer.Task.IsCompleted);

                // ── THE OLDER TASK COMPLETES WHILE THE NEWER IS STILL PENDING. ──
                older.TrySetResult(StateOf(MemberId));
                await olderLifecycle.WaitAsync(JoinTimeout, Ct);

                // The older nav lifecycle reached the real facade with no principal and was refused.
                var staleNav = await tree.Clarification.Recorder.WaitForAsync(call => call.Boundary == FacadeBoundary.NavClarificationCount && call.ForwardedNullPrincipal);
                Assert.False(staleNav.Success);
                Assert.Equal(FacadeErrorKind.Forbidden, staleNav.Kind);
                await tree.DrainAsync();
                Assert.Equal(0, tree.Operational.Recorder.Count(call =>
                    call.ForwardedNullPrincipal && call.Success));
                Assert.Equal(0, tree.Clarification.Recorder.Count(call =>
                    call.ActingIdentity is null && call.Success));

                // The circuit is UNCHANGED by the superseded completion.
                Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                Assert.False(tree.Accessor.IsInvalidated);
                Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));

                // ── THE NEWER TASK WINS: a FRESH authorized read reaches the ROUTED page. ──
                newer.TrySetResult(StateOf(AdminId));
                await newerLifecycle.WaitAsync(JoinTimeout, Ct);
                await tree.DrainAsync();
                var page = await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge));
                Assert.NotNull(page);
                Assert.True(await tree.FieldAsync<bool>(page!, "_loaded"));
                Assert.False(await tree.FieldAsync<bool>(page!, "_denied"));
                Assert.NotEmpty(await tree.FieldAsync<IReadOnlyList<KnowledgeDocumentSummary>>(page!, "_allDocs"));
                Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
                Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
                Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
            }
            finally
            {
                older.TrySetResult(StateOf(MemberId));
                newer.TrySetResult(StateOf(AdminId));
            }
        });
    }

    /// <summary>
    /// NEWER COMPLETES FIRST, THEN THE OLDER: the SAME two pending deliveries, but the NEWER task is
    /// released FIRST. The routed page appears and publishes its protected document, the nav badge is
    /// republished, and the circuit stays initialized. The OLDER task is released LAST and is SUPERSEDED:
    /// it observes nothing, so the newer publication stands untouched and its own façade call still carries
    /// the NULL identity — the recorded identity is never substituted for a superseded completion.
    /// </summary>
    [Fact]
    public async Task NewerCompletesFirst_ThenOlder_KeepsTheNewerPublication_AndNeverForwardsStaleClaims()
    {
        var tree = await RenderTreeAsync(AdminId);
        await tree.RunAndDisposeAsync(async () =>
        {
            var nav = (await tree.FindComponentAsync(nameof(NavMenu)))!;
            Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));
            Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);

            var older = Gate();
            var newer = Gate();
            try
            {
                tree.ReleaseBeforeJoin(() => older.TrySetResult(StateOf(AdminId)));
                tree.ReleaseBeforeJoin(() => newer.TrySetResult(StateOf(AdminId)));
                var olderLifecycle = await tree.SetCascadeAsync(tree.Host, older.Task);
                await tree.DrainAsync();
                var generationAfterOlder = await tree.FieldAsync<int>(nav, "_refreshGeneration");
                var newerLifecycle = await tree.SetCascadeAsync(tree.Host, newer.Task);
                await tree.DrainAsync();
                Assert.True(await tree.FieldAsync<int>(nav, "_refreshGeneration") > generationAfterOlder,
                    "the retained nav menu's own lifecycle must have run for the newer cascade");

                // ── THE NEWER TASK WINS FIRST: current, so the routed page really publishes. ──
                newer.TrySetResult(StateOf(AdminId));
                await newerLifecycle.WaitAsync(JoinTimeout, Ct);
                await tree.DrainAsync();
                var page = await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge));
                Assert.NotNull(page);
                Assert.True(await tree.FieldAsync<bool>(page!, "_loaded"));
                Assert.False(await tree.FieldAsync<bool>(page!, "_denied"));
                Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
                Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
                var publishedGeneration = await tree.FieldAsync<int>(page!, "_refreshGeneration");

                // ── THE OLDER TASK COMPLETES AFTER the newer one already won. ──
                older.TrySetResult(StateOf(AdminId));
                await olderLifecycle.WaitAsync(JoinTimeout, Ct);
                var stale = await tree.Clarification.Recorder.WaitForAsync(call => call.Boundary == FacadeBoundary.NavClarificationCount && call.ForwardedNullPrincipal);
                Assert.False(stale.Success);
                Assert.Equal(FacadeErrorKind.Forbidden, stale.Kind);
                await tree.DrainAsync();

                // The newer publication is untouched: the superseded completion published nothing.
                Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));
                Assert.Equal(0, tree.Operational.Recorder.Count(call =>
                    call.ForwardedNullPrincipal && call.Success));
                Assert.Equal(0, tree.Clarification.Recorder.Count(call =>
                    call.ActingIdentity is null && call.Success));
                var pageAfter = await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge));
                Assert.NotNull(pageAfter);
                Assert.Same(page, pageAfter);
                Assert.True(await tree.FieldAsync<bool>(pageAfter!, "_loaded"));
                Assert.False(await tree.FieldAsync<bool>(pageAfter!, "_denied"));
                Assert.True(await tree.FieldAsync<int>(pageAfter!, "_refreshGeneration") >= publishedGeneration);
                Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
                Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
            }
            finally
            {
                older.TrySetResult(StateOf(AdminId));
                newer.TrySetResult(StateOf(AdminId));
            }
        });
    }

    /// <summary>
    /// SEPARATE NORMAL-ROUTING CONTROL: verifies the ACTUAL compiled <see cref="Routes"/> branch via its
    /// <c>_authEnabled</c> static rather than assuming this test process initialised it from the
    /// environment. In the AUTHENTICATED branch the <c>AuthorizeRouteView</c> awaits the pending cascade
    /// itself and REMOVES the routed page for that interval; in the OPEN branch the plain <c>RouteView</c>
    /// retains it. In BOTH branches the real <see cref="Routes"/> and <see cref="NavMenu"/> stay mounted,
    /// the enclosing lifecycle is still in flight and the pending task has not completed. Those pending
    /// intervals are NEVER counted as retained-page evidence — the sibling-blocked harness keeps the real
    /// siblings mounted explicitly for the overlap rows.
    /// </summary>
    [Fact]
    public async Task AuthenticatedAuthorizeRouteView_RemovesTheRoutedPageWhileTheCascadeIsPending()
    {
        var tree = await RenderTreeAsync(AdminId);
        await tree.RunAndDisposeAsync(async () =>
        {
            var routes = (await tree.FindComponentAsync(nameof(Routes)))!;
            var nav = (await tree.FindComponentAsync(nameof(NavMenu)))!;
            Assert.NotNull(await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge)));
            Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);

            var pending = Gate();
            Task lifecycle = Task.CompletedTask;
            try
            {
                tree.ReleaseBeforeJoin(() => pending.TrySetResult(StateOf(AdminId)));
                lifecycle = await tree.SetCascadeAsync(tree.Host, pending.Task);
                await tree.DrainAsync();
                Assert.Same(routes, await tree.FindComponentAsync(nameof(Routes)));
                Assert.Same(nav, await tree.FindComponentAsync(nameof(NavMenu)));
                if (RoutesAuthEnabled)
                {
                    // AUTHENTICATED branch: AuthorizeRouteView awaits the pending cascade itself and
                    // removes the routed page. Never treat this interval as retained-page evidence.
                    Assert.Null(await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge)));
                }
                else
                {
                    // OPEN branch: the plain RouteView retains it. The sibling proof does not depend on
                    // this branch; it uses the test-only blocked-interval host.
                    Assert.NotNull(await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge)));
                }
                Assert.False(lifecycle.IsCompleted, "the retained sibling lifecycle must still be in flight");
                Assert.False(pending.Task.IsCompleted);
                Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));

                pending.SetResult(StateOf(AdminId));
                await lifecycle.WaitAsync(JoinTimeout, CancellationToken.None);
                await tree.DrainAsync();
                Assert.NotNull(await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge)));
                Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
            }
            finally
            {
                pending.TrySetResult(StateOf(AdminId));
            }
        });
    }

    /// <summary>
    /// SIBLING IN-FLIGHT PROOF, ALL FOUR OVERLAP CELLS (newer pending or newer completes first × stale
    /// sign-out or replacement), with the REAL <see cref="Routes"/> branch and a SECOND branch holding the
    /// REAL <see cref="NavMenu"/> and the REAL <see cref="CopilotHive.Components.Pages.Knowledge"/> page
    /// under the EXACT ordering cascade — ONE renderer, ONE scope, ONE shared scoped
    /// <see cref="CircuitUserPrincipalAccessor"/>.
    /// <para>
    /// EACH RELEASE IS ATTRIBUTED SEPARATELY AT ALL THREE REAL BOUNDARIES (the Knowledge page's document
    /// read, the nav menu's document count, the nav menu's clarification count) using the call-index
    /// watermark taken immediately before that release. At every boundary T1 must forward EXACTLY
    /// <c>null</c>, receive <see cref="FacadeErrorKind.Forbidden"/>, obtain no protected value and produce
    /// no additional authorized read; T2 must forward the expected AUTHENTICATED identity and obtain a
    /// FRESH, distinctive result. Because the per-release watermarks are used, a baseline read, a separate
    /// <see cref="Routes"/>-branch read, a denial at one operation and an old nav success can never satisfy
    /// another release's proof.
    /// </para>
    /// <para>
    /// A DISTINCTIVE SECOND DOCUMENT is seeded while both tasks are pending, so T2's successful page read
    /// is proved FRESH rather than a replay. The <c>AuthorizeRouteView</c> pending interval is NOT counted
    /// as retained-page evidence: its removal is pinned separately by
    /// <see cref="AuthenticatedAuthorizeRouteView_RemovesTheRoutedPageWhileTheCascadeIsPending"/>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("pending-newer", "sign-out")]
    [InlineData("pending-newer", "replacement")]
    [InlineData("newer-completes-first", "sign-out")]
    [InlineData("newer-completes-first", "replacement")]
    public async Task RealSiblings_BothHeldLifecycles_StaleSignOutOrReplacement_NeverCrossAFacadeBoundary(
        string completionOrder, string staleOutcome)
    {
        var tree = await RenderSiblingTreeAsync(AdminId);
        var proof = new BoundaryProof(tree.FacadeRecorder);
        await tree.RunAndDisposeAsync(async () =>
        {
            var nav = tree.OrderingNav;
            var page = tree.OrderingPage;
            var routes = (await tree.FindComponentAsync(nameof(Routes)))!;
            var label = $"{completionOrder}/{staleOutcome}";
            Assert.Same(tree.Accessor, await tree.PropertyAsync<CircuitUserPrincipalAccessor>(routes, "CircuitPrincipal"));
            Assert.Same(tree.Accessor, await tree.PropertyAsync<CircuitUserPrincipalAccessor>(nav, "CircuitPrincipal"));
            Assert.Same(tree.Accessor, await tree.PropertyAsync<CircuitUserPrincipalAccessor>(page, "CircuitPrincipal"));
            var originalIdentity = tree.Accessor.EstablishedIdentity;
            Assert.NotNull(originalIdentity);
            Assert.Equal(2, (await tree.FindAllAsync(nameof(NavMenu))).Count);
            Assert.Equal(2, (await tree.FindAllAsync(nameof(CopilotHive.Components.Pages.Knowledge))).Count);

            // ── THE BASELINE IS ISOLATED: its watermarks are taken here and never reused below. ──
            Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
            Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
            Assert.Equal(1, await tree.FieldAsync<int>(nav, "_pendingClarifications"));
            Assert.NotEmpty(await tree.FieldAsync<IReadOnlyList<KnowledgeDocumentSummary>>(page, "_allDocs"));
            var baselinePage = await tree.FacadeRecorder.WaitForAsync(call =>
                call.Boundary == FacadeBoundary.KnowledgeDocuments && call.Success
                && call.ActingIdentity == AdminId);
            Assert.True(baselinePage.DocumentCount >= 1);
            Assert.Equal("", baselinePage.SearchQuery);
            Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));
            var baselineDocCount = baselinePage.DocumentCount;

            var baselineCalls = tree.FacadeRecorder.Calls.Count;
            var navGeneration = await tree.FieldAsync<int>(nav, "_refreshGeneration");
            var pageGeneration = await tree.FieldAsync<int>(page, "_refreshGeneration");

            var older = Gate();
            var newer = Gate();
            Task olderLifecycle = Task.CompletedTask;
            Task newerLifecycle = Task.CompletedTask;
            try
            {
                var staleState = staleOutcome == "sign-out" ? SignedOut() : StateOf(MemberId);
                tree.ReleaseBeforeJoin(() => older.TrySetResult(staleState));
                tree.ReleaseBeforeJoin(() => newer.TrySetResult(StateOf(AdminId)));

                // ── EXACT T1, delivered through a REAL cascade update, BOTH siblings in flight. ──
                olderLifecycle = await tree.SetSiblingCascadeAsync(older.Task);
                await tree.DrainAsync();
                Assert.Same(older.Task, tree.Ordering);
                Assert.Same(older.Task, await tree.PropertyAsync<Task<AuthenticationState>>(nav, "AuthenticationState"));
                Assert.Same(older.Task, await tree.PropertyAsync<Task<AuthenticationState>>(page, "AuthenticationState"));
                Assert.Same(routes, await tree.FindComponentAsync(nameof(Routes)));
                Assert.Same(nav, tree.OrderingNav);
                Assert.Same(page, tree.OrderingPage);
                Assert.True(await tree.FieldAsync<int>(nav, "_refreshGeneration") > navGeneration,
                    "the real nav sibling's own lifecycle must have started for the exact T1 task");
                Assert.True(await tree.FieldAsync<int>(page, "_refreshGeneration") > pageGeneration,
                    "the real page sibling's own lifecycle must have started for the exact T1 task");
                Assert.False(older.Task.IsCompleted);
                Assert.False(olderLifecycle.IsCompleted, "the T1 sibling lifecycles must still be in flight");

                // ── EXACT T2, STILL PENDING: supersedes T1 immediately, BOTH siblings in flight. ──
                var t1NavGeneration = await tree.FieldAsync<int>(nav, "_refreshGeneration");
                var t1PageGeneration = await tree.FieldAsync<int>(page, "_refreshGeneration");
                newerLifecycle = await tree.SetSiblingCascadeAsync(newer.Task);
                await tree.DrainAsync();
                Assert.Same(newer.Task, tree.Ordering);
                Assert.Same(newer.Task, await tree.PropertyAsync<Task<AuthenticationState>>(nav, "AuthenticationState"));
                Assert.Same(newer.Task, await tree.PropertyAsync<Task<AuthenticationState>>(page, "AuthenticationState"));
                Assert.True(await tree.FieldAsync<int>(nav, "_refreshGeneration") > t1NavGeneration,
                    "the real nav sibling's own lifecycle must have started for the exact T2 task");
                Assert.True(await tree.FieldAsync<int>(page, "_refreshGeneration") > t1PageGeneration,
                    "the real page sibling's own lifecycle must have started for the exact T2 task");
                Assert.False(newer.Task.IsCompleted);
                Assert.False(newerLifecycle.IsCompleted, "the T2 sibling lifecycles must still be in flight");
                Assert.False(olderLifecycle.IsCompleted);
                Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));

                Assert.Equal(baselineCalls, tree.FacadeRecorder.Calls.Count);

                // Seed the DISTINCTIVE fresh document while BOTH tasks are still pending: only a FRESH
                // successful page read performed after this point can observe it.
                await tree.Graph.CreateDocumentAsync(
                    "sibling-order-fresh-doc", FreshDocTitle, DocumentType.Implementation,
                    "fresh body", topic: "sibling-fresh-topic", subtopic: null, author: "author",
                    tags: ["sibling-fresh-tag"], ct: Ct);
                var freshDocumentTotal = baselineDocCount + 1;
                // The queue holds this object by reference. Change its status without raising a
                // queue notification: only the two cascade-owned refreshes may read the new count.
                tree.FreshClarification.Status = ClarificationStatus.AwaitingHuman;

                // ── WATERMARKS ARE TAKEN PER RELEASE, never shared across releases. ──
                var markDocForT1 = proof.Mark(FacadeBoundary.KnowledgeDocuments);
                var markNavDocForT1 = proof.Mark(FacadeBoundary.NavDocumentCount);
                var markNavClarForT1 = proof.Mark(FacadeBoundary.NavClarificationCount);
                var markDocForT2 = markDocForT1;
                var markNavDocForT2 = markNavDocForT1;
                var markNavClarForT2 = markNavClarForT1;

                if (completionOrder == "pending-newer")
                {
                    // ORDER 1: T1 completes while T2 is STILL PENDING.
                    older.SetResult(staleState);
                    await olderLifecycle.WaitAsync(JoinTimeout, Ct);
                    Assert.False(newer.Task.IsCompleted, "T2 must still be pending when T1 was denied");
                    Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                    Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));

                    // ── T1 ATTRIBUTED AT ALL THREE BOUNDARIES, BEFORE T2 IS RELEASED. ──
                    Assert.Multiple(
                        () => proof.AssertSupersededDenied(FacadeBoundary.KnowledgeDocuments, markDocForT1, label),
                        () => proof.AssertSupersededDenied(FacadeBoundary.NavDocumentCount, markNavDocForT1, label),
                        () => proof.AssertSupersededDenied(FacadeBoundary.NavClarificationCount, markNavClarForT1, label));
                    await tree.DrainAsync();

                    // T2's watermarks are taken AFTER T1's calls landed, so T1 can never satisfy T2.
                    markDocForT2 = proof.Mark(FacadeBoundary.KnowledgeDocuments);
                    markNavDocForT2 = proof.Mark(FacadeBoundary.NavDocumentCount);
                    markNavClarForT2 = proof.Mark(FacadeBoundary.NavClarificationCount);
                    newer.SetResult(StateOf(AdminId));
                }
                else
                {
                    // ORDER 2: T2 wins FIRST. Open T1's window only after T2's reads are joined.
                    newer.SetResult(StateOf(AdminId));
                    await newerLifecycle.WaitAsync(JoinTimeout, Ct);
                    Assert.False(older.Task.IsCompleted);
                    Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                    Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));

                    // ── T2 ATTRIBUTED AT ALL THREE BOUNDARIES. ──
                    var currentDoc = proof.AssertCurrentAuthorized(
                        FacadeBoundary.KnowledgeDocuments, markDocForT2, AdminId, label);
                    Assert.Equal(freshDocumentTotal, proof.AssertCurrentAuthorized(
                        FacadeBoundary.NavDocumentCount, markNavDocForT2, AdminId, label).CountValue);
                    Assert.Equal(2, proof.AssertCurrentAuthorized(
                        FacadeBoundary.NavClarificationCount, markNavClarForT2, AdminId, label).CountValue);
                    Assert.Equal(freshDocumentTotal, currentDoc.DocumentCount);
                    Assert.Equal("", currentDoc.SearchQuery);
                    Assert.Contains(currentDoc.DocumentValue!.Documents, doc => doc.Title == FreshDocTitle);
                    await tree.DrainAsync();

                    // T1's window is re-opened at the CURRENT watermark so only T1's own calls count.
                    markDocForT1 = proof.Mark(FacadeBoundary.KnowledgeDocuments);
                    markNavDocForT1 = proof.Mark(FacadeBoundary.NavDocumentCount);
                    markNavClarForT1 = proof.Mark(FacadeBoundary.NavClarificationCount);
                    older.SetResult(staleState);
                }

                await olderLifecycle.WaitAsync(JoinTimeout, Ct);
                await newerLifecycle.WaitAsync(JoinTimeout, Ct);
                await tree.DrainAsync();

                if (completionOrder == "pending-newer")
                {
                    // ── T2 ATTRIBUTED AT ALL THREE BOUNDARIES (its window was opened at T1's release). ──
                    var currentDoc = proof.AssertCurrentAuthorized(
                        FacadeBoundary.KnowledgeDocuments, markDocForT2, AdminId, label);
                    Assert.Equal(freshDocumentTotal, proof.AssertCurrentAuthorized(
                        FacadeBoundary.NavDocumentCount, markNavDocForT2, AdminId, label).CountValue);
                    Assert.Equal(2, proof.AssertCurrentAuthorized(
                        FacadeBoundary.NavClarificationCount, markNavClarForT2, AdminId, label).CountValue);
                    Assert.Equal(freshDocumentTotal, currentDoc.DocumentCount);
                    Assert.Equal("", currentDoc.SearchQuery);
                    Assert.Contains(currentDoc.DocumentValue!.Documents, doc => doc.Title == FreshDocTitle);
                }
                else
                {
                    // ── T1 ATTRIBUTED AT ALL THREE BOUNDARIES, released AFTER T2 had already won. ──
                    Assert.Multiple(
                        () => proof.AssertSupersededDenied(FacadeBoundary.KnowledgeDocuments, markDocForT1, label),
                        () => proof.AssertSupersededDenied(FacadeBoundary.NavDocumentCount, markNavDocForT1, label),
                        () => proof.AssertSupersededDenied(FacadeBoundary.NavClarificationCount, markNavClarForT1, label));
                }

                // ACCESSOR STATE + RENDERED MARKERS: only T2's fresh publication is visible.
                Assert.Equal(CircuitIdentityState.Initialized, tree.Accessor.IdentityState);
                Assert.False(tree.Accessor.IsInvalidated);
                Assert.Equal(AdminId, IdentityKeyOf(tree.Accessor.EstablishedIdentity));
                Assert.Same(originalIdentity, tree.Accessor.EstablishedIdentity);
                var siblingHtml = await tree.SiblingHtmlAsync();
                Assert.Contains(FreshDocTitle, siblingHtml, StringComparison.Ordinal);
                Assert.Contains("2 pending clarification(s)", siblingHtml, StringComparison.Ordinal);
                Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") > baselineDocCount);
                Assert.Equal(2, await tree.FieldAsync<int>(nav, "_pendingClarifications"));
                Assert.NotEmpty(await tree.FieldAsync<IReadOnlyList<KnowledgeDocumentSummary>>(page, "_allDocs"));
                Assert.Contains(await tree.FieldAsync<IReadOnlyList<KnowledgeDocumentSummary>>(page, "_allDocs"),
                    doc => doc.Title == FreshDocTitle);
                Assert.False(await tree.FieldAsync<bool>(page, "_denied"));
                Assert.Same(routes, await tree.FindComponentAsync(nameof(Routes)));
                Assert.Same(nav, tree.OrderingNav);
                Assert.Same(page, tree.OrderingPage);
                Assert.Same(newer.Task, tree.Ordering);
            }
            finally
            {
                // Release first. The outer owner attempts every join and preserves the primary failure.
                older.TrySetResult(staleOutcome == "sign-out" ? SignedOut() : StateOf(MemberId));
                newer.TrySetResult(StateOf(AdminId));
            }
        });
    }

    /// <summary>
    /// CURRENT TERMINAL CONTROL for the sibling-blocked harness: a NEW current sign-out task is observed
    /// by BOTH real siblings while they remain mounted. The exact <see cref="CircuitIdentityState.SignedOut"/>
    /// state, all cleared state/markup, and the permanent refusal of a FRESH task carrying the ORIGINAL
    /// account are asserted.
    /// <para>
    /// POST-SIGN-OUT VERSUS THE EARLIER ROUTES-BRANCH MARKER: this is not a claim about the
    /// <see cref="Routes"/> branch. The separate
    /// <see cref="AuthenticatedAuthorizeRouteView_RemovesTheRoutedPageWhileTheCascadeIsPending"/> row pins
    /// the real host branch's own behaviour, and the replay rows in
    /// <c>ComponentActingUserTests</c> pin exact-task re-registration. Here the ORDERING branch is the
    /// subject: after the terminal sign-out, a FRESH original-account task is a genuinely NEW registration,
    /// so it IS observed — and the terminal state still refuses it. That is the post-sign-out behaviour the
    /// previous replay mutant did NOT cover: it authorized a re-delivered initial task BEFORE the sign-out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RealSiblings_CurrentTerminalSignOut_IsPermanent_AndAFreshOriginalTaskRestoresNothing()
    {
        var tree = await RenderSiblingTreeAsync(AdminId);
        var proof = new BoundaryProof(tree.FacadeRecorder);
        await tree.RunAndDisposeAsync(async () =>
        {
            var nav = tree.OrderingNav;
            var page = tree.OrderingPage;
            Assert.Equal(2, (await tree.FindAllAsync(nameof(NavMenu))).Count);
            Assert.Equal(2, (await tree.FindAllAsync(nameof(CopilotHive.Components.Pages.Knowledge))).Count);
            Assert.Contains(DocTitle, await tree.SiblingHtmlAsync(), StringComparison.Ordinal);
            Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
            Assert.Equal(1, await tree.FieldAsync<int>(nav, "_pendingClarifications"));

            var terminalMarks = Enum.GetValues<FacadeBoundary>().ToDictionary(boundary => boundary, proof.Mark);
            var terminal = Task.FromResult(SignedOut());
            var terminalLifecycle = await tree.SetSiblingCascadeAsync(terminal);
            await terminalLifecycle.WaitAsync(JoinTimeout, Ct);
            await tree.DrainAsync();
            Assert.Same(terminal, tree.Ordering);
            Assert.Equal(CircuitIdentityState.SignedOut, tree.Accessor.IdentityState);
            Assert.True(tree.Accessor.IsInvalidated);
            Assert.Null(tree.Accessor.EstablishedIdentity);

            foreach (var (boundary, mark) in terminalMarks)
                proof.AssertSupersededDenied(boundary, mark, "current-sign-out");

            Assert.False(await tree.FieldAsync<bool>(page, "_loaded"));
            Assert.True(await tree.FieldAsync<bool>(page, "_denied"));
            Assert.Empty(await tree.FieldAsync<IReadOnlyList<KnowledgeDocumentSummary>>(page, "_allDocs"));
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_knowledgeDocCount"));
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_pendingClarifications"));
            var deniedHtml = await tree.SiblingHtmlAsync();
            Assert.DoesNotContain(DocTitle, deniedHtml, StringComparison.Ordinal);
            Assert.Contains(FacadeAuthorization.ForbiddenMessage, deniedHtml, StringComparison.Ordinal);


            // ── POST-SIGN-OUT: a FRESH task carrying the ORIGINAL account is a NEW registration, so it IS
            // observed — and the TERMINAL state still refuses it, so nothing is restored. ──
            var recovery = Task.FromResult(StateOf(AdminId));
            var markDoc = proof.Mark(FacadeBoundary.KnowledgeDocuments);
            var markNavDoc = proof.Mark(FacadeBoundary.NavDocumentCount);
            var markNavClar = proof.Mark(FacadeBoundary.NavClarificationCount);
            var recoveryLifecycle = await tree.SetSiblingCascadeAsync(recovery);
            await recoveryLifecycle.WaitAsync(JoinTimeout, Ct);
            await tree.DrainAsync();
            Assert.Same(recovery, tree.Ordering);
            Assert.Equal(CircuitIdentityState.SignedOut, tree.Accessor.IdentityState);
            Assert.Null(tree.Accessor.EstablishedIdentity);
            Assert.False(await tree.Accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

            // The recovery attempt is OBSERVED (it is current) and REFUSED by the terminal state: it
            // forwards exactly null at every boundary and obtains nothing.
            foreach (var (boundary, mark) in new[]
            {
                (FacadeBoundary.KnowledgeDocuments, markDoc),
                (FacadeBoundary.NavDocumentCount, markNavDoc),
                (FacadeBoundary.NavClarificationCount, markNavClar),
            })
            {
                proof.AssertSupersededDenied(boundary, mark, "terminal-recovery");
            }

            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_knowledgeDocCount"));
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_pendingClarifications"));
            Assert.False(await tree.FieldAsync<bool>(page, "_loaded"));
            Assert.True(await tree.FieldAsync<bool>(page, "_denied"));
            Assert.DoesNotContain(DocTitle, await tree.SiblingHtmlAsync(), StringComparison.Ordinal);

        });
    }

    /// <summary>
    /// CURRENT TERMINAL CONTROL on the plain retained tree: a task that IS current carries the sign-out, so
    /// every sibling observes it — including the routed page, whose own <c>OnParametersSetAsync</c> runs
    /// again on the same scoped accessor, clears every document and renders its safe denial state — and the
    /// circuit is PERMANENTLY invalidated. No later task, not even one carrying the original account,
    /// restores access. This keeps the superseded rows honest: a genuine current sign-out still invalidates;
    /// only a SUPERSEDED one cannot.
    /// </summary>
    [Fact]
    public async Task CurrentSignOut_ThroughTheRealTree_PermanentlyInvalidates_AndClearsTheNavCounters()
    {
        var tree = await RenderTreeAsync(AdminId);
        await tree.RunAndDisposeAsync(async () =>
        {
            var nav = (await tree.FindComponentAsync(nameof(NavMenu)))!;
            var page = (await tree.FindComponentAsync(nameof(CopilotHive.Components.Pages.Knowledge)))!;
            Assert.Contains(DocTitle, await tree.HtmlAsync(), StringComparison.Ordinal);
            Assert.True(await tree.FieldAsync<int>(nav, "_knowledgeDocCount") >= 1);
            Assert.True(await tree.FieldAsync<bool>(page, "_loaded"));
            Assert.False(await tree.FieldAsync<bool>(page, "_denied"));

            // THE CURRENT TASK CARRIES THE SIGN-OUT: observed by every sibling.
            await tree.SetCascadeAsync(tree.Host, Task.FromResult(SignedOut()));
            foreach (var boundary in new[]
            {
                FacadeBoundary.KnowledgeDocuments, FacadeBoundary.NavDocumentCount,
                FacadeBoundary.NavClarificationCount,
            })
            {
                var call = await tree.FacadeRecorder.WaitForAsync(candidate =>
                    candidate.Boundary == boundary && candidate.ForwardedNullPrincipal && !candidate.Success);
                Assert.Equal(FacadeErrorKind.Forbidden, call.Kind);
                Assert.Null(call.DocumentValue);
            Assert.Equal(0, call.DocumentCount);
                Assert.Equal(0, call.CountValue);
            }
            await tree.DrainAsync();

            Assert.Equal(CircuitIdentityState.SignedOut, tree.Accessor.IdentityState);
            Assert.True(tree.Accessor.IsInvalidated);
            Assert.Null(tree.Accessor.EstablishedIdentity);
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_knowledgeDocCount"));
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_pendingClarifications"));

            Assert.False(await tree.FieldAsync<bool>(page, "_loaded"));
            Assert.True(await tree.FieldAsync<bool>(page, "_denied"));
            Assert.Empty(await tree.FieldAsync<IReadOnlyList<KnowledgeDocumentSummary>>(page, "_allDocs"));
            var signedOutHtml = await tree.HtmlAsync();
            Assert.DoesNotContain(DocTitle, signedOutHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("document(s)", signedOutHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("badge-clarification", signedOutHtml, StringComparison.Ordinal);
            Assert.Contains(FacadeAuthorization.ForbiddenMessage, signedOutHtml, StringComparison.Ordinal);

            // NOTHING restores it: a fresh CURRENT task carrying the ORIGINAL account is observed and the
            // terminal state still refuses it, so no counter and no identity come back.
            await tree.SetCascadeAsync(tree.Host, Task.FromResult(StateOf(AdminId)));
            await tree.DrainAsync();
            Assert.Equal(CircuitIdentityState.SignedOut, tree.Accessor.IdentityState);
            Assert.Null(tree.Accessor.EstablishedIdentity);
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_knowledgeDocCount"));
            Assert.Equal(0, await tree.FieldAsync<int>(nav, "_pendingClarifications"));
            Assert.DoesNotContain("document(s)", await tree.HtmlAsync(), StringComparison.Ordinal);
            Assert.Null(await ComponentActingUser.AcquireAsync(Task.FromResult(StateOf(AdminId)), tree.Accessor));
            Assert.False(await tree.Accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        });
    }
}
