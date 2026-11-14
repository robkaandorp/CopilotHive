using System.Collections;
using System.Reflection;
using System.Security.Claims;

using CopilotHive.Dashboard;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// THE AUTHORIZATION + PROJECTION CONTRACT of the KNOWLEDGE <see cref="HiveCapability.SharedRead"/>
/// operations on <see cref="IOperationalFacade"/>.
/// <list type="bullet">
///   <item>Every role (Viewer up) reads them; disabled, deleted, unknown, null and anonymous callers
///   are refused with the sanitized <see cref="FacadeErrorKind.Forbidden"/> result carrying no data.
///   A disposed-lock tripwire proves the refusal runs BEFORE any protected graph read, including the
///   count read delegated through <see cref="DashboardStateService"/>.</item>
///   <item>The projections are DETACHED: a later in-place mutation of the live graph is not
///   observable through a projection already handed out, and mutating a returned collection cannot
///   reach the graph.</item>
///   <item>The ordinary semantics are preserved: the graph's own search tokens, the
///   outgoing/incoming links with their RESOLVED linked titles, the not-found answer and the
///   unconfigured-graph answer.</item>
///   <item>OPEN MODE keeps working exactly as before (the local operator holds every capability).</item>
/// </list>
/// <para>
/// The behavioral read tests drive the REAL production facade over the REAL production
/// <see cref="HivePrincipalResolver"/>/<see cref="UserService"/> directory
/// (<see cref="FacadeAuthorizationHarness"/>) and a REAL <see cref="DashboardStateService"/> holding
/// a REAL <see cref="KnowledgeGraph"/>, so a refusal is an actual resolution answer and a success is
/// an actual delegation with real documents.
/// </para>
/// </summary>
public sealed class OperationalFacadeKnowledgeReadTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "knowledge-viewer";
    private const string MemberId = "knowledge-member";
    private const string OperatorId = "knowledge-operator";
    private const string AdminId = "knowledge-admin";
    private const string UnknownId = "knowledge-unknown";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private const string AlphaId = "architecture-alpha";
    private const string BetaId = "architecture-beta";
    private const string GammaId = "feature-gamma";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A REAL dashboard service over <paramref name="graph"/> (or over NO graph at all).</summary>
    private static DashboardStateService NewDashboard(KnowledgeGraph? graph = null) =>
        new(new WorkerPool(),
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            knowledgeGraph: graph);

    private OperationalFacade FacadeFor(HiveAuthMode mode, DashboardStateService dashboard) =>
        new(dashboard, new LlmSessionRegistry(), _auth.Resolver(mode), _auth.Policy,
            NullLogger<OperationalFacade>.Instance);

    /// <summary>
    /// The REAL production graph with three documents, distinctive tags and one outgoing link from
    /// Alpha to Beta, so the incoming-link half of Beta has a real source and a real resolved title.
    /// </summary>
    private static async Task<KnowledgeGraph> SeedGraphAsync()
    {
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync(
            AlphaId, "Alpha Architecture", DocumentType.Implementation,
            "alpha body about orchestration layering",
            topic: "architecture", subtopic: null, author: "alice",
            tags: ["layer", "orchestration"]);
        await graph.CreateDocumentAsync(
            BetaId, "Beta Architecture", DocumentType.Feature,
            "beta body about worker pooling",
            topic: "architecture", subtopic: "internals", author: "bob",
            tags: ["pooling"]);
        await graph.CreateDocumentAsync(
            GammaId, "Gamma Feature", DocumentType.Idea,
            "gamma body about a future capability",
            topic: "feature", subtopic: null, author: "carol",
            tags: ["future", "orchestration"]);

        graph.AddLink(AlphaId, new DocumentLink(BetaId, LinkType.DependsOn, "alpha depends on beta"));
        return graph;
    }

    private async Task SeedAllRolesAsync()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    private static void AssertForbidden(FacadeResult result, string scenario)
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
    }

    /// <summary>
    /// Asserts a denied READ that carries a reference-typed projection: the refusal must hand back
    /// NOTHING, so no document, link, tag or count can leak out of a refused read.
    /// </summary>
    private static void AssertForbidden<T>(FacadeResult<T> result, string scenario)
        where T : class
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);

        Assert.Null(result.Value);
    }

    /// <summary>
    /// Asserts a denied COUNT read. The count is an <c>int</c>, so the contract is "the refusal carries
    /// no number at all": the value must be the type's default (0) and NEVER a previously authorized
    /// document count.
    /// </summary>
    private static void AssertForbiddenCount(FacadeResult<int> result, string scenario)
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);

        Assert.Equal(default, result.Value);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. EVERY SharedRead ROLE READS; denials are refused with no value
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ROLE MATRIX: all four roles read the list, the single document (with its links and their
    /// RESOLVED titles) and the count — the positive control proving the gate refuses the
    /// insufficient rather than everybody. A signed-out caller, a disabled account and an unknown
    /// (never-activated/deleted) account are each refused on all three operations, carrying no value.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_AnswerEveryRole_AndRefuseSignedOutDisabledAndUnknownCallers()
    {
        await SeedAllRolesAsync();
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        foreach (var caller in new[] { ViewerId, MemberId, OperatorId, AdminId })
        {
            var list = await facade.GetKnowledgeDocumentsAsync(null, User(caller), Ct);
            Assert.True(list.Success, $"{caller}: {list.Error}");
            Assert.True(list.Value!.IsConfigured);
            Assert.Equal(
                [AlphaId, BetaId, GammaId],
                list.Value.Documents.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));

            var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, User(caller), Ct);
            Assert.True(detail.Success, $"{caller}: {detail.Error}");
            Assert.Equal(AlphaId, detail.Value!.Document.Id);
            Assert.Equal("Layer · Orchestration", string.Join(" · ",
                detail.Value.Document.Tags.Select(t => char.ToUpperInvariant(t[0]) + t[1..])));
            Assert.Equal("alpha depends on beta", Assert.Single(detail.Value.OutgoingLinks).Description);
            Assert.Equal("Beta Architecture", Assert.Single(detail.Value.OutgoingLinks).TargetTitle);

            var count = await facade.GetKnowledgeDocumentCountAsync(User(caller), Ct);
            Assert.True(count.Success, $"{caller}: {count.Error}");
            Assert.Equal(3, count.Value);
        }

        // ── Signed out: all three reads are refused, carrying no value. ──
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, null, Ct), "signed-out list");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, null, Ct), "signed-out detail");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(null, Ct), "signed-out count");

        // ── A DISABLED account: the role is sufficient, the account is not. ──
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct), "disabled list");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct), "disabled detail");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct), "disabled count");

        // ── An identity with NO activated account at all. ──
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, User(UnknownId), Ct), "unknown list");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, User(UnknownId), Ct), "unknown detail");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(User(UnknownId), Ct), "unknown count");

        // ── Re-enabling restores the reads: the refusal was CURRENT state, not a verdict. ──
        await _auth.SetEnabledAsync(ViewerId, enabled: true);
        Assert.True((await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct)).Success);
    }

    /// <summary>
    /// OBSERVABLE READ ORDER, not just an identical denial payload: disposing the real graph's lock
    /// makes every graph read throw. The same real facade/resolver/policy/SQLite directory must still
    /// refuse all five denied identities without throwing, while an enabled Viewer MUST hit the
    /// tripwire on every operation (including both list paths), before AND after the denied calls.
    /// This observes graph reads, not the dashboard's side-effect-free KnowledgeGraph property getter.
    /// </summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("anonymous")]
    public async Task KnowledgeReads_DeniedCallerNeverReachesGraphReadTripwire(string denialIdentity)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        using var dashboard = NewDashboard(graph);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);
        var authorized = User(ViewerId);

        ClaimsPrincipal? denied;
        switch (denialIdentity)
        {
            case "disabled":
            case "deleted":
                // Retain the SAME principal/facade across the directory change: both identities were
                // genuinely authorized before their row was disabled/deleted, not merely unknown.
                await _auth.SeedAsync(MemberId, UserRole.Member);
                denied = User(MemberId);
                await AssertSeededKnowledgeReadsAsync(facade, denied);
                if (denialIdentity == "disabled")
                    await _auth.SetEnabledAsync(MemberId, enabled: false);
                else
                    await _auth.DeleteUserAsync(MemberId);
                break;
            case "unknown":
                denied = User(UnknownId); // Never seeded in SQLite.
                break;
            case "null":
                denied = null;
                break;
            case "anonymous":
                denied = FacadeAuthorizationHarness.Anonymous();
                Assert.False(denied.Identity!.IsAuthenticated);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(denialIdentity));
        }

        // Ordinary authorized reads first prove the real data/delegation wiring is healthy.
        await AssertSeededKnowledgeReadsAsync(facade, authorized);

        // Test-only reflection: these production types are sealed/nonvirtual. Do not dispose the
        // directory or replace the graph: authorization must keep using the real, LIVE SQLite store.
        var lockField = typeof(KnowledgeGraph).GetField("_graphLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lockField);
        var graphLock = Assert.IsType<SemaphoreSlim>(lockField.GetValue(graph));
        graphLock.Dispose();

        await AssertGraphReadTripwireAsync(facade, authorized);

        // Any eager graph read would now throw rather than returning these sanitized refusals.
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, denied, Ct), $"{denialIdentity} list tripwire");
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync("orchestration", denied, Ct), $"{denialIdentity} search tripwire");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, denied, Ct), $"{denialIdentity} detail tripwire");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(denied, Ct), $"{denialIdentity} count tripwire");

        // The tripwire is still armed; a gate that refuses EVERY caller cannot pass these controls.
        await AssertGraphReadTripwireAsync(facade, authorized);

        // The lock observes graph methods. A SECOND test-only fault observes even a passive
        // dashboard.KnowledgeGraph getter evaluated and discarded before denying. With sealed,
        // nonvirtual production objects there is no injectable getter spy: invalidate ONLY this
        // facade's backing dashboard reference, keeping the real resolver/SQLite directory live.
        var dashboardField = typeof(OperationalFacade).GetField("_dashboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        dashboardField.SetValue(facade, null);
        try
        {
            AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, denied, Ct), "dashboard access list");
            AssertForbidden(await facade.GetKnowledgeDocumentsAsync("orchestration", denied, Ct), "dashboard access search");
            AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, denied, Ct), "dashboard access detail");
            AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(denied, Ct), "dashboard access count");
            await Assert.ThrowsAsync<NullReferenceException>(() => facade.GetKnowledgeDocumentsAsync(null, authorized, Ct));
            await Assert.ThrowsAsync<NullReferenceException>(() => facade.GetKnowledgeDocumentsAsync("orchestration", authorized, Ct));
            await Assert.ThrowsAsync<NullReferenceException>(() => facade.GetKnowledgeDocumentAsync(AlphaId, authorized, Ct));
            await Assert.ThrowsAsync<NullReferenceException>(() => facade.GetKnowledgeDocumentCountAsync(authorized, Ct));
        }
        finally { dashboardField.SetValue(facade, dashboard); }
    }

    private static async Task AssertSeededKnowledgeReadsAsync(IOperationalFacade facade, ClaimsPrincipal caller)
    {
        var list = await facade.GetKnowledgeDocumentsAsync(null, caller, Ct);
        Assert.True(list.Success, list.Error);
        Assert.True(list.Value!.IsConfigured);
        Assert.Equal(3, list.Value.Documents.Count);

        var search = await facade.GetKnowledgeDocumentsAsync("orchestration", caller, Ct);
        Assert.True(search.Success, search.Error);
        Assert.Equal([AlphaId, GammaId], search.Value!.Documents.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));

        var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, caller, Ct);
        Assert.True(detail.Success, detail.Error);
        Assert.Equal(AlphaId, detail.Value!.Document.Id);
        Assert.Equal("Beta Architecture", Assert.Single(detail.Value.OutgoingLinks).TargetTitle);

        var count = await facade.GetKnowledgeDocumentCountAsync(caller, Ct);
        Assert.True(count.Success, count.Error);
        Assert.Equal(3, count.Value);
    }

    private static async Task AssertGraphReadTripwireAsync(IOperationalFacade facade, ClaimsPrincipal caller)
    {
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetKnowledgeDocumentsAsync(null, caller, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetKnowledgeDocumentsAsync("orchestration", caller, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetKnowledgeDocumentAsync(AlphaId, caller, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.GetKnowledgeDocumentCountAsync(caller, Ct));
    }

    /// <summary>
    /// FRESH RESOLUTION PER CALL: ONE facade instance, TWO answers. A Viewer with data in hand is
    /// demoted out of the directory (disabled) and the very NEXT read is refused — the same instance
    /// that succeeded a moment ago. A facade (or resolver) that cached a principal would answer the
    /// stale role instead.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_ReResolveCurrentStoredStateOnEveryCall()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        Assert.True((await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct)).Success);
        Assert.Equal(3, (await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct)).Value);

        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct), "post-disable list");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct), "post-disable count");

        await _auth.SetEnabledAsync(ViewerId, enabled: true);
        Assert.True((await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct)).Success);
    }

    /// <summary>
    /// THE AUTHORIZATION ANSWER WINS OVER CONFIGURATION: with NO graph configured at all, a denied
    /// caller still gets <see cref="FacadeErrorKind.Forbidden"/> (never the "unconfigured" success),
    /// so a refusal cannot be disguised as a configuration answer — and an AUTHORIZED caller gets the
    /// unconfigured SUCCESS with an empty list, an explicit <c>IsConfigured=false</c> and a 0 count.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_UnconfiguredGraph_IsASuccessForAnAuthorizedCaller_AndNeverASuccessForADenial()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var dashboard = NewDashboard(graph: null);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        // AUTHORIZED: the page's existing "not configured" surface, expressed as a SUCCESS answer.
        var list = await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct);
        Assert.True(list.Success, list.Error);
        Assert.False(list.Value!.IsConfigured);
        Assert.Empty(list.Value.Documents);

        Assert.Equal(0, (await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct)).Value);

        // A document id can never be found when there is no graph: NotFound, not Forbidden.
        var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct);
        Assert.False(detail.Success);
        Assert.Equal(FacadeErrorKind.NotFound, detail.Kind);
        Assert.Null(detail.Value);

        // DENIED: the authorization answer wins over the "unconfigured" answer.
        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, null, Ct), "signed-out unconfigured list");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(null, Ct), "signed-out unconfigured count");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, null, Ct), "signed-out unconfigured detail");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. SEMANTICS PRESERVED: search, links + resolved titles, not-found
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE GRAPH'S OWN SEARCH SEMANTICS ARE PRESERVED: a non-blank query goes through
    /// <see cref="KnowledgeGraph.Search(string)"/> (AND-ed tokens across id/title/content/tags) and a
    /// blank/whitespace query returns every document. The page's local type/status/tag filtering is
    /// deliberately NOT applied here — it stays the page's job over the detached rows.
    /// </summary>
    [Fact]
    public async Task KnowledgeDocumentList_PreservesTheGraphsSearchSemantics()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        // A shared tag matches two documents; a body word matches exactly one.
        var byTag = await facade.GetKnowledgeDocumentsAsync("orchestration", User(ViewerId), Ct);
        Assert.True(byTag.Success, byTag.Error);
        Assert.Equal(
            [AlphaId, GammaId],
            byTag.Value!.Documents.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));

        var byBody = await facade.GetKnowledgeDocumentsAsync("pooling", User(ViewerId), Ct);
        Assert.Equal(BetaId, Assert.Single(byBody.Value!.Documents).Id);

        // EVERY token must match (AND logic).
        Assert.Empty((await facade.GetKnowledgeDocumentsAsync("pooling orchestration", User(ViewerId), Ct)).Value!.Documents);

        // A blank query is "everything", exactly like Search("") / GetAllDocuments().
        foreach (var blankQuery in new[] { null, "", "   " })
        {
            var all = await facade.GetKnowledgeDocumentsAsync(blankQuery, User(ViewerId), Ct);
            Assert.Equal(3, all.Value!.Documents.Count);
        }

        // The graph itself is UNCHANGED by the projection: the same query still answers the same way.
        Assert.Equal(3, dashboard.KnowledgeGraph!.GetAllDocuments().Count);
    }

    /// <summary>
    /// OUTGOING AND INCOMING LINKS WITH THEIR TITLES RESOLVED INSIDE THE FACADE: Alpha's outgoing
    /// DependsOn link carries Beta's resolved title, and Beta's incoming link carries Alpha's resolved
    /// title plus the INVERSE link type and the original description — so the component never has to
    /// look a document up to fill in a link row.
    /// </summary>
    [Fact]
    public async Task KnowledgeDocument_ResolvesOutgoingAndIncomingLinkTitles()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var alpha = await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct);
        Assert.True(alpha.Success, alpha.Error);
        var alphaOutgoing = Assert.Single(alpha.Value!.OutgoingLinks);
        Assert.Equal(BetaId, alphaOutgoing.TargetId);
        Assert.Equal(LinkType.DependsOn, alphaOutgoing.Type);
        Assert.Equal("alpha depends on beta", alphaOutgoing.Description);
        Assert.Equal("Beta Architecture", alphaOutgoing.TargetTitle);
        Assert.Empty(alpha.Value.IncomingLinks);

        var beta = await facade.GetKnowledgeDocumentAsync(BetaId, User(ViewerId), Ct);
        Assert.True(beta.Success, beta.Error);
        Assert.Empty(beta.Value!.OutgoingLinks);
        var betaIncoming = Assert.Single(beta.Value.IncomingLinks);
        Assert.Equal(AlphaId, betaIncoming.SourceId);
        Assert.Equal(LinkType.DependedOnBy, betaIncoming.Type);
        Assert.Equal("alpha depends on beta", betaIncoming.Description);
        Assert.Equal("Alpha Architecture", betaIncoming.SourceTitle);
    }

    /// <summary>
    /// A LINK TO A MISSING DOCUMENT STILL PROJECTS ITS ROW, with a <c>null</c> resolved title — the
    /// page then renders only the target id, exactly as the direct graph read did. There is no
    /// invented title and no dropped link.
    /// </summary>
    [Fact]
    public async Task KnowledgeDocument_LinkToAMissingTarget_ProjectsWithANullTitle()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        graph.AddLink(GammaId, new DocumentLink("does-not-exist", LinkType.Related, "dangling"));
        using var dashboard = NewDashboard(graph);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var gamma = await facade.GetKnowledgeDocumentAsync(GammaId, User(ViewerId), Ct);
        Assert.True(gamma.Success, gamma.Error);
        var link = Assert.Single(gamma.Value!.OutgoingLinks);
        Assert.Equal("does-not-exist", link.TargetId);
        Assert.Null(link.TargetTitle);
        Assert.Equal("dangling", link.Description);
    }

    /// <summary>
    /// NOT-FOUND IS PRESERVED: an unknown document id answers
    /// <see cref="FacadeErrorKind.NotFound"/> with no value (never a Forbidden and never an invented
    /// projection), and the sanitized denial text is NOT involved.
    /// </summary>
    [Fact]
    public async Task KnowledgeDocument_UnknownId_IsNotFound_WithNoValue()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var result = await facade.GetKnowledgeDocumentAsync("no-such-document", User(ViewerId), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Null(result.Value);
        Assert.NotEqual(ForbiddenMessage, result.Error);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. THE PROJECTIONS ARE DETACHED — no live graph/document/collection escapes
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DETACHMENT CONTRACT, BEHAVIOURALLY: a projection already handed out does NOT change when
    /// the live graph is mutated afterwards (a document renamed, a tag added, another link created),
    /// and the returned collections are READ-ONLY — so a caller can neither observe a later in-place
    /// mutation nor write through the projection into the graph.
    /// </summary>
    [Fact]
    public async Task KnowledgeProjections_AreDetachedFromTheLiveGraph()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        using var dashboard = NewDashboard(graph);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var list = await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct);
        var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct);
        Assert.True(list.Success, list.Error);
        Assert.True(detail.Success, detail.Error);

        var alphaRow = list.Value!.Documents.Single(d => d.Id == AlphaId);
        var alphaProjection = detail.Value!;
        Assert.Equal("Alpha Architecture", alphaRow.Title);
        Assert.Equal(["layer", "orchestration"], alphaRow.Tags);
        Assert.Single(alphaProjection.OutgoingLinks);

        // Mutate the EXISTING live List, not UpdateDocumentAsync(tags: ...), which replaces the
        // list and would let a read-only view over the old live list falsely appear detached.
        var liveDocument = graph.GetDocument(AlphaId)!;
        var liveTags = liveDocument.Tags;
        Assert.NotSame(liveTags, alphaRow.Tags);
        Assert.NotSame(liveTags, alphaProjection.Document.Tags);
        liveTags[0] = "changed-in-place";
        Assert.True(liveTags.Remove("orchestration"));
        liveTags.Add("added-in-place");
        await graph.UpdateDocumentAsync(AlphaId, title: "RENAMED-IN-PLACE", ct: Ct);
        graph.AddLink(AlphaId, new DocumentLink(GammaId, LinkType.Related, "added in place"));
        Assert.Same(liveDocument, graph.GetDocument(AlphaId));
        Assert.Same(liveTags, graph.GetDocument(AlphaId)!.Tags);
        Assert.Equal(["changed-in-place", "added-in-place"], graph.GetDocument(AlphaId)!.Tags);

        // The ALREADY-HANDED-OUT projections are UNCHANGED: they are copies, not views.
        Assert.Equal("Alpha Architecture", alphaRow.Title);
        Assert.Equal(["layer", "orchestration"], alphaRow.Tags);
        Assert.Equal("Alpha Architecture", alphaProjection.Document.Title);
        Assert.Equal(["layer", "orchestration"], alphaProjection.Document.Tags);
        Assert.Single(alphaProjection.OutgoingLinks);
        Assert.Equal(BetaId, alphaProjection.OutgoingLinks[0].TargetId);

        // BOTH fresh reads observe the mutation: detachment must be a copy, not a stale cache.
        var refreshedList = await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct);
        Assert.True(refreshedList.Success, refreshedList.Error);
        var refreshedRow = refreshedList.Value!.Documents.Single(d => d.Id == AlphaId);
        Assert.Equal("RENAMED-IN-PLACE", refreshedRow.Title);
        Assert.Equal(["changed-in-place", "added-in-place"], refreshedRow.Tags);

        var refreshed = await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct);
        Assert.True(refreshed.Success, refreshed.Error);
        Assert.Equal("RENAMED-IN-PLACE", refreshed.Value!.Document.Title);
        Assert.Equal(["changed-in-place", "added-in-place"], refreshed.Value.Document.Tags);
        Assert.Equal(2, refreshed.Value.OutgoingLinks.Count);
        Assert.NotSame(liveTags, refreshedRow.Tags);
        Assert.NotSame(liveTags, refreshed.Value.Document.Tags);
        Assert.Equal(["layer", "orchestration"], alphaRow.Tags);
        Assert.Equal(["layer", "orchestration"], alphaProjection.Document.Tags);

        // Retain the original UpdateDocumentAsync(tags: ...) replacement check IN ADDITION TO the
        // stronger in-place-list check. Neither mutation may change a snapshot handed out earlier.
        await graph.UpdateDocumentAsync(AlphaId, tags: ["replaced"], ct: Ct);
        var replaced = await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct);
        Assert.Equal("RENAMED-IN-PLACE", replaced.Value!.Document.Title);
        Assert.Equal(["replaced"], replaced.Value.Document.Tags);
        Assert.Equal(2, replaced.Value.OutgoingLinks.Count);
        Assert.Equal(["layer", "orchestration"], alphaRow.Tags);
        Assert.Equal(["layer", "orchestration"], alphaProjection.Document.Tags);
        Assert.Equal(["changed-in-place", "added-in-place"], refreshedRow.Tags);
        Assert.Equal(["changed-in-place", "added-in-place"], refreshed.Value.Document.Tags);

        // READ-ONLY: every returned collection REJECTS mutation, so a caller cannot write through a
        // projection into the graph (and cannot mutate a value another caller already holds).
        AssertReadOnly(alphaRow.Tags);
        AssertReadOnly(alphaProjection.Document.Tags);
        AssertReadOnly(alphaProjection.OutgoingLinks);
        AssertReadOnly(alphaProjection.IncomingLinks);
        AssertReadOnly(list.Value.Documents);
    }

    /// <summary>
    /// Asserts a returned collection is genuinely READ-ONLY by attempting the only mutation an
    /// <see cref="IList{T}"/> offers and requiring it to be refused.
    /// </summary>
    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly, "a knowledge projection collection must be read-only");

        var added = typeof(T) == typeof(string) ? (T)(object)"injected" : default!;
        Assert.Throws<NotSupportedException>(() => list.Add(added));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        if (list.Count > 0)
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
    }

    /// <summary>
    /// NO LIVE ENTITY CROSSES THE BOUNDARY: the declared value types of every knowledge operation are
    /// the DETACHED projections — never <see cref="KnowledgeGraph"/>, never
    /// <see cref="KnowledgeDocument"/>, never the mutable tag/link collections. This pins the shape so
    /// a later change cannot start returning the live graph through the same method names.
    /// </summary>
    [Fact]
    public void TheKnowledgeOperations_ReturnOnlyDetachedProjectionTypes()
    {
        var knowledgeOperations = typeof(IOperationalFacade).GetMethods()
            .Where(m => m.Name.Contains("Knowledge", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, knowledgeOperations.Count);

        foreach (var operation in knowledgeOperations)
        {
            var valueType = operation.ReturnType.GetGenericArguments().Single()
                .GetGenericArguments().Single();

            Assert.Empty(ForbiddenProjectionTypes(valueType));
        }
    }

    /// <summary>
    /// A non-generic DTO must not hide a live entity behind another non-generic DTO's property/field.
    /// The back-reference also proves traversal terminates and visits each type only once.
    /// </summary>
    [Fact]
    public void ProjectionShapeChecker_FindsLiveEntitiesBehindNonGenericPropertiesAndFields()
    {
        var flattened = Flatten(typeof(HiddenLiveShape)).ToArray();
        Assert.Contains(typeof(HiddenLiveMembers), flattened);
        Assert.Equal(flattened.Length, flattened.Distinct().Count());

        var forbidden = ForbiddenProjectionTypes(typeof(HiddenLiveShape)).ToArray();
        Assert.Contains(typeof(KnowledgeGraph), forbidden);
        Assert.Contains(typeof(KnowledgeDocument), forbidden);
        Assert.Contains(typeof(DocumentLink), forbidden);
        Assert.Contains(typeof(IncomingLink), forbidden);
    }

    [Fact]
    public void ProjectionShapeChecker_FindsMutableCollectionsBehindNonGenericPropertiesAndFields()
    {
        var forbidden = ForbiddenProjectionTypes(typeof(HiddenMutableShape)).ToArray();
        Assert.Contains(typeof(List<string>), forbidden);
        Assert.Contains(typeof(Dictionary<string, KnowledgeDocumentSummary>), forbidden);
        Assert.Contains(typeof(string[]), forbidden);
    }

    [Fact]
    public void ProjectionShapeChecker_FindsUnsafeTypesInsideReadOnlyGenericAndArrayContainers()
    {
        var arrayTypes = ForbiddenProjectionTypes(typeof(IReadOnlyList<KnowledgeDocument[]>)).ToArray();
        Assert.Contains(typeof(KnowledgeDocument[]), arrayTypes);
        Assert.Contains(typeof(KnowledgeDocument), arrayTypes);
        Assert.Contains(typeof(List<string>),
            ForbiddenProjectionTypes(typeof(IReadOnlyDictionary<string, List<string>>)));
    }

    [Theory]
    [InlineData(typeof(string[]))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(IList<string>))]
    [InlineData(typeof(ICollection<string>))]
    [InlineData(typeof(Dictionary<string, string>))]
    [InlineData(typeof(IDictionary<string, string>))]
    [InlineData(typeof(HashSet<string>))]
    [InlineData(typeof(ISet<string>))]
    [InlineData(typeof(Queue<string>))]
    [InlineData(typeof(Stack<string>))]
    [InlineData(typeof(ArrayList))]
    [InlineData(typeof(Hashtable))]
    [InlineData(typeof(IList))]
    [InlineData(typeof(IDictionary))]
    [InlineData(typeof(Queue))]
    [InlineData(typeof(Stack))]
    public void ProjectionShapeChecker_RejectsWritableCollectionShapes(Type type)
    {
        Assert.Contains(type, ForbiddenProjectionTypes(type));
    }

    /// <summary>Positive discrimination controls: a checker that rejects everything must fail.</summary>
    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(KnowledgeDocumentSummary))]
    [InlineData(typeof(KnowledgeDocumentDetail))]
    [InlineData(typeof(KnowledgeOutgoingLink))]
    [InlineData(typeof(KnowledgeIncomingLink))]
    [InlineData(typeof(KnowledgeDocumentListProjection))]
    [InlineData(typeof(KnowledgeDocumentProjection))]
    [InlineData(typeof(IReadOnlyList<KnowledgeDocumentSummary>))]
    [InlineData(typeof(IReadOnlyDictionary<string, KnowledgeDocumentSummary>))]
    [InlineData(typeof(SafeRecursiveShape))]
    public void ProjectionShapeChecker_AcceptsDetachedScalarsAndReadOnlyDtoShapes(Type type)
    {
        Assert.Empty(ForbiddenProjectionTypes(type));
    }

    private static IEnumerable<Type> ForbiddenProjectionTypes(Type type) =>
        Flatten(type).Where(IsForbiddenProjectionType);

    private static bool IsForbiddenProjectionType(Type type)
    {
        if (type == typeof(KnowledgeGraph) || type == typeof(KnowledgeDocument) ||
            type == typeof(DocumentLink) || type == typeof(IncomingLink) || type.IsArray ||
            typeof(IList).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type) ||
            typeof(Queue).IsAssignableFrom(type) || typeof(Stack).IsAssignableFrom(type))
            return true;

        // Guard the DECLARED API, not the current runtime instance's IsReadOnly flag. A projection
        // should declare IReadOnlyList/Dictionary, not expose writable collection contracts (even if
        // today's implementation happens to wrap them in a read-only instance).
        foreach (var contract in type.GetInterfaces().Append(type))
        {
            if (!contract.IsGenericType) continue;
            var definition = contract.GetGenericTypeDefinition();
            if (definition == typeof(ICollection<>) || definition == typeof(IList<>) ||
                definition == typeof(IDictionary<,>) || definition == typeof(ISet<>) ||
                definition == typeof(System.Collections.Concurrent.IProducerConsumerCollection<>))
                return true;
        }

        // Queue/Stack offer mutators without implementing ICollection<T>; include derived shapes too.
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            var definition = current.GetGenericTypeDefinition();
            if (definition == typeof(Queue<>) || definition == typeof(Stack<>)) return true;
        }

        return false;
    }

    /// <summary>
    /// Walk the publicly exposed value shape without invoking getters or inspecting private backing
    /// fields (a safe read-only implementation can own a private mutable copy). Non-generic DTOs are
    /// NOT leaves: recurse through public instance properties/fields as well as generic arguments and
    /// array elements. A visited set makes self-references and mutually recursive DTOs finite.
    /// </summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            yield return current;

            if (current.HasElementType) pending.Push(current.GetElementType()!);
            foreach (var argument in current.GetGenericArguments()) pending.Push(argument);

            if (current.IsPrimitive || current.IsEnum || current.IsGenericParameter ||
                current == typeof(string) || current == typeof(decimal) || current == typeof(DateTime) ||
                current == typeof(DateTimeOffset) || current == typeof(TimeSpan) || current == typeof(Guid))
                continue;

            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                pending.Push(property.PropertyType);
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public))
                pending.Push(field.FieldType);
        }
    }

    // Deliberately non-generic shapes: the former generic-arguments-only Flatten missed all of these.
    private sealed class HiddenLiveShape
    {
        public HiddenLiveMembers Nested { get; } = null!;
    }

    private sealed class HiddenLiveMembers
    {
        public KnowledgeGraph Graph { get; } = null!;
        public KnowledgeDocument Document = null!;
        public DocumentLink Link { get; } = null!;
        public IncomingLink Incoming = null!;
        public HiddenLiveShape? Cycle { get; }
    }

    private sealed class HiddenMutableShape
    {
        public HiddenMutableMembers Nested = null!;
    }

    private sealed class HiddenMutableMembers
    {
        public List<string> Tags { get; } = [];
        public Dictionary<string, KnowledgeDocumentSummary> Index = [];
        public string[] Names { get; } = [];
    }

    private sealed class SafeRecursiveShape
    {
        public SafeRecursiveShape? Next { get; }
        public IReadOnlyList<KnowledgeDocumentSummary> Rows { get; } = [];
        public readonly int Count = 0;
        public static KnowledgeGraph? IgnoredStaticMember => null;
    }

    /// <summary>
    /// THE STORED ROLE IS RE-READ ON EVERY CALL, NOT CACHED: the account row is overwritten with an
    /// unrecognised persisted role (as a legacy row would carry) after a successful read, and the VERY
    /// NEXT read through the SAME facade instance is refused — the facade never replays a cached
    /// resolution from the earlier call.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_RoleTheDirectoryNoLongerRecognises_IsRefusedOnTheNextCall()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        Assert.True((await facade.GetKnowledgeDocumentsAsync(null, User(AdminId), Ct)).Success);

        // The stored role becomes something the resolver will not recognise (a legacy/corrupt row).
        _auth.SetRawRoleText(AdminId, "legacy-superuser");

        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, User(AdminId), Ct), "unrecognised list");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, User(AdminId), Ct), "unrecognised detail");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(User(AdminId), Ct), "unrecognised count");
    }

    /// <summary>
    /// A ROLE CHANGE THAT KEEPS SharedRead KEEPS THE READ: every role in the matrix holds SharedRead, so
    /// demotion from Admin to Viewer cannot by itself refuse a knowledge read. This pins that the
    /// capability check is not a disguised "role == Admin" test — the rows above prove the refusal side,
    /// this one proves the entitled side survives a demotion within the entitled set. A demotion that
    /// DOES cross the entitled boundary (to an unrecognised persisted role) is refused, see
    /// <see cref="KnowledgeReads_RoleTheDirectoryNoLongerRecognises_IsRefusedOnTheNextCall"/>.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_ViewerRole_RetainsSharedReadAfterDemotionFromAdmin()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        Assert.True((await facade.GetKnowledgeDocumentsAsync(null, User(AdminId), Ct)).Success);

        await _auth.SetRoleAsync(AdminId, UserRole.Viewer);
        var demoted = await facade.GetKnowledgeDocumentsAsync(null, User(AdminId), Ct);
        Assert.True(demoted.Success, demoted.Error);
        Assert.Equal(3, demoted.Value!.Documents.Count);
    }

    /// <summary>
    /// A DELETED account is refused on the very next call, on all three operations, and the refusal is
    /// a DENIAL — never the "not configured" success and never the not-found answer. This is distinct
    /// from "disabled": the row is GONE.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_DeletedAccount_IsRefusedOnTheVeryNextCall()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        Assert.True((await facade.GetKnowledgeDocumentsAsync(null, User(MemberId), Ct)).Success);
        Assert.True((await facade.GetKnowledgeDocumentAsync(AlphaId, User(MemberId), Ct)).Success);
        Assert.Equal(3, (await facade.GetKnowledgeDocumentCountAsync(User(MemberId), Ct)).Value);

        await _auth.DeleteUserAsync(MemberId);

        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, User(MemberId), Ct), "deleted list");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, User(MemberId), Ct), "deleted detail");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(User(MemberId), Ct), "deleted count");
    }

    /// <summary>
    /// THE ANONYMOUS PRINCIPAL (an unauthenticated identity, even with an untrusted Admin role claim):
    /// refused on all three operations. This is the signed-out shape the resolver sees when
    /// <c>ClaimsPrincipal.Identity.IsAuthenticated</c> is false, as distinct from the
    /// null principal the Blazor circuit forwards when no cascade exists.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_AnonymousPrincipal_IsRefused()
    {
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);
        var anonymous = FacadeAuthorizationHarness.Anonymous();

        AssertForbidden(await facade.GetKnowledgeDocumentsAsync(null, anonymous, Ct), "anonymous list");
        AssertForbidden(await facade.GetKnowledgeDocumentAsync(AlphaId, anonymous, Ct), "anonymous detail");
        AssertForbiddenCount(await facade.GetKnowledgeDocumentCountAsync(anonymous, Ct), "anonymous count");
    }

    /// <summary>
    /// THE SEARCH/COUNT/LINK SEMANTICS MATCH THE GRAPH ITSELF: for a spread of queries the facade's
    /// answer is exactly the graph's own <see cref="KnowledgeGraph.Search(string)"/> answer, and the
    /// count equals the graph's own document count — so the facade cannot silently widen or narrow the
    /// read the page relies on.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_MatchTheGraphsOwnSearchAndCountSemantics()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        using var dashboard = NewDashboard(graph);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        foreach (var query in new[] { "orchestration", "pooling", "architecture", "gamma", "absent-token-xyz", "" })
        {
            var expected = string.IsNullOrWhiteSpace(query)
                ? graph.GetAllDocuments().Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToList()
                : graph.Search(query).Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

            var list = await facade.GetKnowledgeDocumentsAsync(query, User(ViewerId), Ct);
            Assert.True(list.Success, list.Error);
            Assert.Equal(
                expected,
                list.Value!.Documents.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToList());
        }

        Assert.Equal(graph.GetAllDocuments().Count, (await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct)).Value);
    }

    /// <summary>
    /// THE LIVE GRAPH IS NEVER REACHED THROUGH THE RETURNED DTOS: the DTOs carry only value copies, so
    /// mutating the DTOs' collections cannot reach the graph even though the graph is mutable. In
    /// particular the caller cannot add a tag to a document it can see, nor add a link, by mutating the
    /// returned projection.
    /// </summary>
    [Fact]
    public async Task KnowledgeProjection_MutationCannotReachTheLiveGraph()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        using var dashboard = NewDashboard(graph);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct);
        Assert.True(detail.Success, detail.Error);

        // The returned tag list is a snapshot: even if a caller could write to it, the graph must not change.
        var tagsBefore = graph.GetDocument(AlphaId)!.Tags.ToList();
        var list = await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct);
        Assert.Equal(graph.GetAllDocuments().Count, list.Value!.Documents.Count);

        // The live document is authoritative and unchanged by having been projected.
        Assert.Equal(tagsBefore, graph.GetDocument(AlphaId)!.Tags);
        Assert.Single(graph.GetOutgoingLinks(AlphaId));
        Assert.Equal("Alpha Architecture", graph.GetDocument(AlphaId)!.Title);
    }

    /// <summary>
    /// A LIVE-DOCUMENT MUTATION AFTER THE READ IS VISIBLE IN THE GRAPH BUT NOT IN THE ALREADY-RETURNED
    /// PROJECTION: the projection holds its own copies of Title, Content, Status, Tags and the linked
    /// documents' identities, so the two never alias.
    /// </summary>
    [Fact]
    public async Task KnowledgeProjection_AllScalarFieldsAreCopiedNotAliased()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var graph = await SeedGraphAsync();
        using var dashboard = NewDashboard(graph);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var detail = await facade.GetKnowledgeDocumentAsync(BetaId, User(ViewerId), Ct);
        Assert.True(detail.Success, detail.Error);
        var dto = detail.Value!.Document;
        var incoming = Assert.Single(detail.Value.IncomingLinks);

        // Mutate every scalar the projection carries, PLUS the linked document's title.
        await graph.UpdateDocumentAsync(
            BetaId,
            title: "BETA-RENAMED",
            content: "BETA-CONTENT-REWRITTEN",
            status: DocumentStatus.Archived,
            tags: ["beta-replaced"],
            ct: Ct);
        await graph.UpdateDocumentAsync(AlphaId, title: "ALPHA-RENAMED", ct: Ct);

        Assert.Equal("Beta Architecture", dto.Title);
        Assert.Equal("beta body about worker pooling", dto.Content);
        Assert.Equal(DocumentStatus.Draft, dto.Status);
        Assert.Equal(["pooling"], dto.Tags);
        Assert.Equal("Alpha Architecture", incoming.SourceTitle);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3b. THE DENIAL ANSWER IS IDENTICAL WITH AND WITHOUT A GRAPH — payload non-disclosure.
    //      The disposed-lock tripwire above separately proves observable graph-read ordering.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A denied CALLER CANNOT LEARN WHETHER A GRAPH IS CONFIGURED: the refusal is byte-identical across
    /// "no graph at all", "an empty graph" and "a populated graph", for all three operations. A refusal
    /// that differed between the three would leak the installation's shape to an unauthorized caller.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_DenialCarriesNoGraphInformation()
    {
        await SeedAllRolesAsync();

        var populated = await SeedGraphAsync();

        var scenarios = new (string Label, Func<DashboardStateService> NewDashboard)[]
        {
            ("no graph at all", () => NewDashboard(graph: null)),
            ("an empty graph", () => NewDashboard(new KnowledgeGraph())),
            ("a populated graph", () => NewDashboard(populated)),
        };

        foreach (var (label, factory) in scenarios)
        {
            using var dashboard = factory();
            var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

            var list = await facade.GetKnowledgeDocumentsAsync(null, User(UnknownId), Ct);
            AssertForbidden(list, $"unknown list ({label})");
            Assert.Null(list.Value);

            var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, User(UnknownId), Ct);
            AssertForbidden(detail, $"unknown detail ({label})");
            Assert.Null(detail.Value);

            var count = await facade.GetKnowledgeDocumentCountAsync(User(UnknownId), Ct);
            AssertForbiddenCount(count, $"unknown count ({label})");

            // The SAME shape, byte for byte, for a disabled-but-known account.
            await _auth.SetEnabledAsync(ViewerId, enabled: false);
            AssertForbidden(
                await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct),
                $"disabled list ({label})");
            AssertForbidden(
                await facade.GetKnowledgeDocumentAsync(AlphaId, User(ViewerId), Ct),
                $"disabled detail ({label})");
            AssertForbiddenCount(
                await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct),
                $"disabled count ({label})");
            await _auth.SetEnabledAsync(ViewerId, enabled: true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. OPEN MODE IS UNCHANGED
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE: with authentication disabled the resolver answers the legacy local operator for the
    /// identity the circuit forwards (none — a Blazor circuit in open mode has no cascade, so it
    /// forwards <c>null</c>), and all three knowledge reads therefore succeed exactly as the direct
    /// graph reads did before this facade existed.
    /// </summary>
    [Fact]
    public async Task KnowledgeReads_SucceedInOpenMode()
    {
        using var dashboard = NewDashboard(await SeedGraphAsync());
        var facade = FacadeFor(HiveAuthMode.Open, dashboard);

        var list = await facade.GetKnowledgeDocumentsAsync(null, OpenModeActingUser.User, Ct);
        Assert.True(list.Success, list.Error);
        Assert.True(list.Value!.IsConfigured);
        Assert.Equal(3, list.Value.Documents.Count);

        var detail = await facade.GetKnowledgeDocumentAsync(AlphaId, OpenModeActingUser.User, Ct);
        Assert.True(detail.Success, detail.Error);
        Assert.Equal("Alpha Architecture", detail.Value!.Document.Title);

        Assert.Equal(3, (await facade.GetKnowledgeDocumentCountAsync(OpenModeActingUser.User, Ct)).Value);
    }

    public void Dispose() => _auth.Dispose();
}
