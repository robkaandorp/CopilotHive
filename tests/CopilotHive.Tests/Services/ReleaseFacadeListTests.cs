using System.Security.Claims;

using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE FOCUSED CONTRACT for <see cref="IReleaseFacade.GetReleasesAsync"/> — the AUTHORIZED release-LIST
/// read that replaces the Releases page's old, documented direct
/// <see cref="IGoalStore.GetReleasesAsync"/> exception.
/// <para>
/// Every row runs the REAL production <see cref="ReleaseFacade"/> over the REAL production
/// <see cref="UserService"/> directory and the REAL <see cref="HivePrincipalResolver"/> /
/// <see cref="HivePolicy"/> pair, so a role decision is never a stub: a demoted or disabled account is
/// refused because the DIRECTORY says so.
/// </para>
/// <para>
/// THE SHARED-READ MATRIX. The read requires <see cref="HiveCapability.SharedRead"/> (Viewer), so EVERY
/// role from Viewer upward succeeds — each is a SUCCESS row, which is what makes the refusal rows
/// meaningful: a facade that refused everybody would fail them. The refusal rows are a SIGNED-OUT
/// caller, a DISABLED account and an UNKNOWN account (no persisted row), each paired with the assertion
/// that the STORE WAS NEVER READ — a refusal before the read, not a filtered result.
/// </para>
/// <para>
/// DETACHMENT is asserted on the physical object graph, not on a value the store happens to hold: the
/// returned <see cref="ReleaseDto.RepositoryNames"/> is a DIFFERENT list instance from the store
/// entity's mutable <see cref="Release.RepositoryNames"/>, and neither side observes the other's later
/// mutation.
/// </para>
/// </summary>
public sealed class ReleaseFacadeListTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "list-viewer";
    private const string MemberId = "list-member";
    private const string OperatorId = "list-operator";
    private const string AdminId = "list-admin";
    private const string DisabledId = "list-disabled";
    private const string UnknownId = "list-unknown";

    /// <summary>The exact sanitized denial text every refusal carries.</summary>
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Host plumbing ───────────────────────────────────────────────────────

    /// <summary>
    /// The production facade over a chosen store: the resolver/policy pair of this harness for an
    /// EXPLICIT authentication mode. Every optional dependency is absent, exactly as the release READ
    /// surface needs none of them.
    /// </summary>
    private ReleaseFacade Facade(IGoalStore store, HiveAuthMode? mode = null) =>
        new(
            store,
            new DashboardNotifier(),
            NullLogger<ReleaseFacade>.Instance,
            _auth.Resolver(mode ?? HiveAuthMode.AuthenticationEnabled),
            _auth.Policy,
            executionService: null,
            eventBus: null,
            nuGetMonitor: null,
            hiveConfig: null,
            appLifetime: null,
            docCleanup: null);

    /// <summary>A real SQLite-in-memory goal store: the store's OWN ordering is what a read returns.</summary>
    private sealed class RealStore : IAsyncDisposable
    {
        private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();

        internal GoalStore Store { get; }

        internal RealStore() => Store = new GoalStore(_db, NullLogger<GoalStore>.Instance);

        public ValueTask DisposeAsync() => _db.DisposeAsync();
    }

    private static Release NewRelease(
        string id, string tag, DateTime createdAt, ReleaseStatus status = ReleaseStatus.Planning,
        List<string>? repositories = null) => new()
        {
            Id = id,
            Tag = tag,
            Status = status,
            CreatedAt = createdAt,
            RepositoryNames = repositories ?? [],
        };

    // ══════════════════════════════════════════════════════════════════════════
    //  1. SharedRead succeeds for EVERY role that holds it
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY ROLE THAT HOLDS <see cref="HiveCapability.SharedRead"/> CAN LIST RELEASES: a Viewer,
    /// Member, Operator and Admin all receive the releases. These are the success controls that make the
    /// refusal rows below discriminating — the read is not admin- or operator-gated.
    /// </summary>
    [Theory]
    [InlineData(ViewerId, UserRole.Viewer)]
    [InlineData(MemberId, UserRole.Member)]
    [InlineData(OperatorId, UserRole.Operator)]
    [InlineData(AdminId, UserRole.Admin)]
    public async Task GetReleasesAsync_EveryRoleHoldingSharedRead_ReceivesTheReleases(
        string gitHubId, UserRole role)
    {
        await _auth.SeedAsync(gitHubId, role);
        await using var real = new RealStore();
        await real.Store.CreateReleaseAsync(NewRelease("rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct);

        var result = await Facade(real.Store).GetReleasesAsync(Principal(gitHubId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Null(result.Error);
        var release = Assert.Single(result.Value!);
        Assert.Equal("rel-1", release.Id);
        Assert.Equal("v1.0.0", release.Tag);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. Signed out / disabled / unknown are refused BEFORE the store read
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A SIGNED-OUT caller is refused with the sanitized denial text and the store is NEVER read: the
    /// gate runs before <see cref="IGoalStore.GetReleasesAsync"/>, which is what
    /// <see cref="RecordingGoalStore.AllCalls"/> proves.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_SignedOut_IsForbidden_AndNeverReadsTheStore()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var store = new RecordingGoalStore();
        store.AddRelease(NewRelease("rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await Facade(store).GetReleasesAsync(actingUser: null, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Null(result.Value);
        Assert.Empty(store.AllCalls);
        Assert.Equal(0, store.ReleaseReads);
    }

    /// <summary>
    /// A DISABLED account is refused — a disablement needs no role change, and the account's stored
    /// Viewer role would otherwise be enough. The refusal again performs NO store read.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_DisabledAccount_IsForbidden_AndNeverReadsTheStore()
    {
        await _auth.SeedAsync(DisabledId, UserRole.Viewer);
        await _auth.SetEnabledAsync(DisabledId, enabled: false);

        using var store = new RecordingGoalStore();
        store.AddRelease(NewRelease("rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await Facade(store).GetReleasesAsync(Principal(DisabledId), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Null(result.Value);
        Assert.Empty(store.AllCalls);
        Assert.Equal(0, store.ReleaseReads);
    }

    /// <summary>
    /// An UNKNOWN account (an authenticated principal with NO persisted row) is refused fail-closed,
    /// also without any store read.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_UnknownAccount_IsForbidden_AndNeverReadsTheStore()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        using var store = new RecordingGoalStore();
        store.AddRelease(NewRelease("rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await Facade(store).GetReleasesAsync(Principal(UnknownId), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Empty(store.AllCalls);
        Assert.Equal(0, store.ReleaseReads);
    }

    /// <summary>
    /// THE REFUSAL FOLLOWS THE ACCOUNT'S CURRENT STATE, not the identity: the SAME principal is served
    /// while it is a Viewer and refused on its very NEXT call after the directory disables it. No
    /// resolution is cached between calls, and the refused call performs no store read.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_FollowsTheAccountsCurrentState_NotTheIdentity()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var store = new RecordingGoalStore();
        store.AddRelease(NewRelease("rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var facade = Facade(store);
        var user = Principal(ViewerId);

        // AUTHORIZED — and the store really was read.
        var allowed = await facade.GetReleasesAsync(user, Ct);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(1, store.ReleaseReads);

        // DISABLED in the directory: the very NEXT call on the SAME principal is refused.
        await _auth.SetEnabledAsync(ViewerId, enabled: false);

        var denied = await facade.GetReleasesAsync(user, Ct);
        Assert.False(denied.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, denied.Kind);
        Assert.Equal(1, store.ReleaseReads); // no second store read on the denial path
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. Open mode and the empty list
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE keeps its previous behaviour exactly: the resolver answers the explicit open-mode local
    /// operator (which holds every capability) WITHOUT consulting the directory, so a <c>null</c>
    /// identity still lists the releases. This is the legacy single-operator installation, unchanged.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_OpenMode_ListsReleasesForTheLocalOperator()
    {
        await using var real = new RealStore();
        await real.Store.CreateReleaseAsync(NewRelease("rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct);

        var result = await Facade(real.Store, HiveAuthMode.Open).GetReleasesAsync(OpenModeActingUser.User, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Value!);
    }

    /// <summary>
    /// AN EMPTY STORE IS A SUCCESS CARRYING AN EMPTY LIST — never a failure — so the page can tell
    /// "there are no releases" apart from "the read was refused".
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_NoReleases_IsAnEmptySuccessfulList()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var real = new RealStore();

        var result = await Facade(real.Store).GetReleasesAsync(Principal(ViewerId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Empty(result.Value!);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. The store's ORDER is preserved
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE STORE'S OWN ORDER IS PRESERVED EXACTLY: the facade neither re-sorts nor filters, so the list
    /// it returns is the store's list, entry for entry and position for position. The store's order is
    /// read through the store itself in the same row, so an accidental re-sort by either side is caught.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_PreservesTheStoresOrder_EntryForEntry()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await using var real = new RealStore();

        // Deliberately seeded out of CreatedAt order, with a distinct creation date each.
        await real.Store.CreateReleaseAsync(NewRelease("rel-oldest", "v0.1.0", new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct);
        await real.Store.CreateReleaseAsync(NewRelease("rel-newest", "v0.3.0", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct);
        await real.Store.CreateReleaseAsync(NewRelease("rel-middle", "v0.2.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Ct);

        var storeOrder = (await real.Store.GetReleasesAsync(Ct)).Select(r => r.Id).ToList();
        var result = await Facade(real.Store).GetReleasesAsync(Principal(ViewerId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(storeOrder, result.Value!.Select(r => r.Id).ToList());

        // The ordering is a real, non-trivial one (newest-created first), not a coincidence of seeding.
        Assert.Equal(["rel-newest", "rel-middle", "rel-oldest"], storeOrder);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5. Every DTO is DETACHED from the store's mutable state
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="ReleaseDto.RepositoryNames"/> IS A COPY, NOT AN ALIAS: it is a different list instance
    /// from the store entity's mutable <see cref="Release.RepositoryNames"/>, and it is not even the same
    /// mutable list TYPE. Detachment holds in BOTH directions — a caller cannot reach store state through
    /// the DTO (its list refuses writes), and a later store mutation cannot change a DTO the caller
    /// already holds.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_CopiesRepositoryNames_SoNeitherSideObservesTheOther()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var store = new RecordingGoalStore();
        var entity = NewRelease(
            "rel-1", "v1.0.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            repositories: ["repo-a", "repo-b"]);
        store.AddRelease(entity);

        var result = await Facade(store).GetReleasesAsync(Principal(ViewerId), Ct);

        Assert.True(result.Success, result.Error);
        var dto = Assert.Single(result.Value!);

        // Same VALUES (detachment must not drop anything) but a DIFFERENT instance of a DIFFERENT type:
        // the store's mutable list was copied, not aliased.
        Assert.Equal(["repo-a", "repo-b"], dto.RepositoryNames);
        Assert.NotSame(entity.RepositoryNames, dto.RepositoryNames);
        Assert.IsNotType<List<string>>(dto.RepositoryNames);

        // Caller → store: the DTO's list refuses a write, so a caller can never reach the entity.
        var asMutable = Assert.IsAssignableFrom<IList<string>>(dto.RepositoryNames);
        Assert.Throws<NotSupportedException>(() => asMutable.Add("repo-injected"));
        Assert.Equal(["repo-a", "repo-b"], entity.RepositoryNames);
        Assert.Equal(["repo-a", "repo-b"], dto.RepositoryNames);

        // Store → DTO: a later mutation of the entity's list is not visible through the held DTO.
        entity.RepositoryNames.Add("repo-later");
        Assert.Equal(["repo-a", "repo-b"], dto.RepositoryNames);
    }

    /// <summary>
    /// The DTO is a SNAPSHOT of the entity's fields: a later store-side change to the release (tag and
    /// status) is not visible through a DTO the caller already received, so a page can hold the read's
    /// result without racing the store.
    /// </summary>
    [Fact]
    public async Task GetReleasesAsync_IsASnapshot_OfEveryDtoField()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        using var store = new RecordingGoalStore();
        var createdAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entity = NewRelease("rel-1", "v1.0.0", createdAt, repositories: ["repo-a"]);
        store.AddRelease(entity);

        var result = await Facade(store).GetReleasesAsync(Principal(ViewerId), Ct);
        var dto = Assert.Single(result.Value!);

        entity.Tag = "v2.0.0";
        entity.Status = ReleaseStatus.Released;
        entity.ReleasedAt = createdAt.AddDays(3);
        entity.ExecutionState = ReleaseExecutionState.Completed;
        entity.Notes = "later notes";

        Assert.Equal("v1.0.0", dto.Tag);
        Assert.Equal("rel-1", dto.Id);
        Assert.Equal(ReleaseStatus.Planning, dto.Status);
        Assert.Equal(createdAt, dto.CreatedAt);
        Assert.Null(dto.ReleasedAt);
        Assert.Equal(ReleaseExecutionState.None, dto.ExecutionState);
        Assert.Null(dto.Notes);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  6. The interface carries NO identity-free overload
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE NO-BYPASS SURFACE CONTRACT for the release list: <see cref="IReleaseFacade"/> declares
    /// <c>GetReleasesAsync</c> EXACTLY ONCE, and that single overload REQUIRES a
    /// <see cref="ClaimsPrincipal"/> (it may be explicitly <c>null</c> — the signed-out answer, which a
    /// refusal — but never omitted or defaulted). Re-introducing an identity-free overload, or an
    /// optional identity, is a compile-visible API change that fails here.
    /// </summary>
    [Fact]
    public void GetReleasesAsync_HasExactlyOneOverload_AndItRequiresAnIdentityParameter()
    {
        var overloads = typeof(IReleaseFacade)
            .GetMethods()
            .Where(m => m.Name == nameof(IReleaseFacade.GetReleasesAsync))
            .ToList();

        var only = Assert.Single(overloads);
        var identity = Assert.Single(
            only.GetParameters(),
            parameter => parameter.ParameterType == typeof(ClaimsPrincipal));
        Assert.False(
            identity.IsOptional,
            "The acting identity must be REQUIRED, never defaulted — an optional identity is an identity-free overload.");
        Assert.False(
            identity.HasDefaultValue,
            "The acting identity must carry no default value.");

        // The token parameter stays required too, so a caller cannot silently drop cancellation.
        var token = Assert.Single(
            only.GetParameters(),
            parameter => parameter.ParameterType == typeof(CancellationToken));
        Assert.False(token.IsOptional);
    }

    public void Dispose() => _auth.Dispose();
}
