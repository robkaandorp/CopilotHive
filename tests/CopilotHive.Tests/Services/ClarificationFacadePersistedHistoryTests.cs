using System.Security.Claims;

using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE PERSISTED-HISTORY OPERATION of <see cref="ClarificationFacade"/> over the REAL store: the
/// production facade, the PRODUCTION <see cref="GoalStore"/> over a REAL in-memory SQLite
/// <see cref="CopilotHiveDbContext"/>, and the REAL <see cref="HivePrincipalResolver"/> /
/// <see cref="HivePolicy"/> pair over the REAL SQLite user directory — the same shape every other
/// facade authorization suite uses.
/// <para>
/// PINNED HERE:
/// </para>
/// <list type="bullet">
///   <item>the SharedRead role matrix (Viewer/Member/Operator/Admin all read);</item>
///   <item>the refusal shape for a disabled account, an unknown account and a signed-out caller —
///   exactly the sanitized <see cref="FacadeAuthorization"/> denial, never a leaked exception and
///   never a store read result;</item>
///   <item>the rows pass through UNCHANGED (goal ids, ordering, every field);</item>
///   <item>the facade forwards <c>limit: null</c> (a full history, not a truncated page);</item>
///   <item>a cancelled token CANCELS the operation — it is never converted into a denial and never
///   swallowed.</item>
/// </list>
/// </summary>
public sealed class ClarificationFacadePersistedHistoryTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "clhist-viewer";
    private const string MemberId = "clhist-member";
    private const string OperatorId = "clhist-operator";
    private const string AdminId = "clhist-admin";
    private const string DisabledId = "clhist-disabled";

    /// <summary>The exact, single-line sanitized denial every facade returns — no account detail.</summary>
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    private static GoalStore CreateStore(CopilotHiveDbContext db) =>
        new(db, NullLogger<GoalStore>.Instance);

    private ClarificationFacade NewFacade(GoalStore store, HiveAuthMode? mode = null) =>
        new(new ClarificationQueueService(),
            store,
            _auth.Resolver(mode ?? HiveAuthMode.AuthenticationEnabled),
            _auth.Policy,
            NullLogger<ClarificationFacade>.Instance);

    /// <summary>Seeds two goals carrying the given clarifications and returns the store.</summary>
    private static async Task<GoalStore> SeedHistoryAsync(CopilotHiveDbContext db, GoalStore store)
    {
        await store.CreateGoalAsync(
            new Goal { Id = "hist-goal-a", Description = "History goal A" }, Ct);
        await store.CreateGoalAsync(
            new Goal { Id = "hist-goal-b", Description = "History goal B" }, Ct);

        var t1 = new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2024, 3, 3, 10, 0, 0, DateTimeKind.Utc);

        await store.AddIterationAsync("hist-goal-a", new IterationSummary
        {
            Iteration = 1,
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = t1,
                    Phase = "Coding",
                    WorkerRole = "coder",
                    Question = "HIST-Q-ALPHA",
                    Answer = "HIST-A-ALPHA",
                    AnsweredBy = "brain",
                    Occurrence = 2,
                },
            ],
        }, Ct);

        await store.AddIterationAsync("hist-goal-b", new IterationSummary
        {
            Iteration = 1,
            Clarifications =
            [
                new PersistedClarification
                {
                    Timestamp = t3,
                    Phase = "Review",
                    WorkerRole = "reviewer",
                    Question = "HIST-Q-BETA",
                    Answer = "HIST-A-BETA",
                    AnsweredBy = "human",
                    Occurrence = 0,
                },
                new PersistedClarification
                {
                    Timestamp = t2,
                    Phase = "Testing",
                    WorkerRole = "tester",
                    Question = "HIST-Q-GAMMA",
                    Answer = "HIST-A-GAMMA",
                    AnsweredBy = "composer",
                    Occurrence = 1,
                },
            ],
        }, Ct);

        return store;
    }

    /// <summary>Asserts the store's own projection, field by field, without reference equality.</summary>
    private static void AssertSameClarification(
        PersistedClarification expected, PersistedClarification actual, string because)
    {
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.WorkerRole, actual.WorkerRole);
        Assert.Equal(expected.Question, actual.Question);
        Assert.Equal(expected.Answer, actual.Answer);
        Assert.Equal(expected.AnsweredBy, actual.AnsweredBy);
        Assert.Equal(expected.Occurrence, actual.Occurrence);
    }

    /// <summary>The seeded history, in the order the store yields it (timestamp descending).</summary>
    private static (string GoalId, PersistedClarification Clarification)[] ExpectedHistory() =>
    [
        ("hist-goal-b", new PersistedClarification
        {
            Timestamp = new DateTime(2024, 3, 3, 10, 0, 0, DateTimeKind.Utc),
            Phase = "Review",
            WorkerRole = "reviewer",
            Question = "HIST-Q-BETA",
            Answer = "HIST-A-BETA",
            AnsweredBy = "human",
            Occurrence = 0,
        }),
        ("hist-goal-b", new PersistedClarification
        {
            Timestamp = new DateTime(2024, 3, 2, 10, 0, 0, DateTimeKind.Utc),
            Phase = "Testing",
            WorkerRole = "tester",
            Question = "HIST-Q-GAMMA",
            Answer = "HIST-A-GAMMA",
            AnsweredBy = "composer",
            Occurrence = 1,
        }),
        ("hist-goal-a", new PersistedClarification
        {
            Timestamp = new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            Phase = "Coding",
            WorkerRole = "coder",
            Question = "HIST-Q-ALPHA",
            Answer = "HIST-A-ALPHA",
            AnsweredBy = "brain",
            Occurrence = 2,
        }),
    ];

    // ══════════════════════════════════════════════════════════════════════
    //  SHARED READ — every role that holds it is admitted
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(UserRole.Viewer, ViewerId)]
    [InlineData(UserRole.Member, MemberId)]
    [InlineData(UserRole.Operator, OperatorId)]
    [InlineData(UserRole.Admin, AdminId)]
    public async Task GetPersistedHistoryAsync_SharedReadRole_IsAdmitted_AndReturnsTheStoreProjection(
        UserRole role, string gitHubId)
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await SeedHistoryAsync(db, store);

        await _auth.SeedAsync(gitHubId, role);

        var facade = NewFacade(store);

        var result = await facade.GetPersistedHistoryAsync(Principal(gitHubId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Null(result.Error);
        Assert.NotNull(result.Value);

        var expected = ExpectedHistory();
        Assert.Equal(expected.Length, result.Value!.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GoalId, result.Value[i].GoalId);
            AssertSameClarification(expected[i].Clarification, result.Value[i].Clarification,
                $"row {i} was not projected unchanged");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REFUSALS — a disabled or unknown account never reaches the store
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPersistedHistoryAsync_DisabledAccount_IsForbidden_WithTheSanitizedMessage()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await SeedHistoryAsync(db, store);

        // The account EXISTS and once held a role that holds SharedRead — it is disabled instead.
        await _auth.SeedAsync(DisabledId, UserRole.Operator, enabled: false);

        var facade = NewFacade(store);

        var result = await facade.GetPersistedHistoryAsync(Principal(DisabledId), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Null(result.Value);
        // No account detail, no store detail: the message is the single sanitized line.
        Assert.DoesNotContain(DisabledId, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPersistedHistoryAsync_UnknownAccount_IsForbidden_WithTheSanitizedMessage()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await SeedHistoryAsync(db, store);

        var facade = NewFacade(store);

        var result = await facade.GetPersistedHistoryAsync(Principal("clhist-no-such-account"), Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Null(result.Value);
        Assert.DoesNotContain("clhist-no-such-account", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPersistedHistoryAsync_SignedOutCaller_IsForbidden_WithTheSanitizedMessage()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await SeedHistoryAsync(db, store);

        var facade = NewFacade(store);

        var result = await facade.GetPersistedHistoryAsync(null, Ct);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Null(result.Value);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  THE PROJECTION ITSELF
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPersistedHistoryAsync_EmptyStore_ReturnsAnEmptyHistory_NotAnError()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await store.CreateGoalAsync(new Goal { Id = "hist-empty-goal", Description = "No history" }, Ct);

        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var facade = NewFacade(store);

        var result = await facade.GetPersistedHistoryAsync(Principal(ViewerId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task GetPersistedHistoryAsync_ForwardsNoLimit_SoEveryRecordIsReturned()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);

        await store.CreateGoalAsync(new Goal { Id = "hist-many-goal", Description = "Many" }, Ct);
        var clarifications = new List<PersistedClarification>();
        var baseTime = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
        {
            clarifications.Add(new PersistedClarification
            {
                Timestamp = baseTime.AddMinutes(i),
                Phase = "Coding",
                WorkerRole = "coder",
                Question = $"MANY-Q-{i:D2}",
                Answer = $"MANY-A-{i:D2}",
                AnsweredBy = "brain",
                Occurrence = i,
            });
        }

        await store.AddIterationAsync("hist-many-goal",
            new IterationSummary { Iteration = 1, Clarifications = clarifications }, Ct);

        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var facade = NewFacade(store);

        var result = await facade.GetPersistedHistoryAsync(Principal(ViewerId), Ct);

        Assert.True(result.Success, result.Error);
        // 25 rows exceed any incidental page size: the facade must forward "no limit".
        Assert.Equal(25, result.Value!.Count);
        Assert.Contains(result.Value, row => row.Clarification.Question == "MANY-Q-00");
        Assert.Contains(result.Value, row => row.Clarification.Question == "MANY-Q-24");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CANCELLATION — never a denial, never swallowed
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPersistedHistoryAsync_CancelledToken_CancelsTheRead_AndIsNeverADenial()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await SeedHistoryAsync(db, store);

        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var facade = NewFacade(store);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The caller's cancellation is not an authorization answer: it propagates as
        // OperationCanceledException (or a derived type), never as a Forbidden result.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => facade.GetPersistedHistoryAsync(Principal(ViewerId), cts.Token));
    }

    [Fact]
    public async Task GetPersistedHistoryAsync_CancelledToken_ForADisabledAccount_IsNotADenialEither()
    {
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = CreateStore(db);
        await SeedHistoryAsync(db, store);

        await _auth.SeedAsync(DisabledId, UserRole.Operator, enabled: false);

        var facade = NewFacade(store);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancellation wins over the refusal: the resolver observes the caller's token BEFORE it can
        // answer "disabled", so the outcome is cancellation — never a fabricated "success" and never
        // a value result of any kind.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => facade.GetPersistedHistoryAsync(Principal(DisabledId), cts.Token));
    }

    public void Dispose() => _auth.Dispose();
}
