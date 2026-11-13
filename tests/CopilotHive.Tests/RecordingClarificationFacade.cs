using System.Security.Claims;

using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// A recording <see cref="IClarificationFacade"/> for the page-behaviour rows: it records WHO the
/// page asked on behalf of (the identity it passed, its occurrence count and the cancellation token)
/// and answers with a scripted outcome. It never touches the queue or the store — the identity
/// reaching the facade and the page's reaction to the answer are what these rows assert.
/// </summary>
internal sealed class RecordingClarificationFacade : IClarificationFacade
{
    private readonly Func<ClaimsPrincipal?, CancellationToken, FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>>? _history;

    /// <summary>Creates a recorder that always answers with <paramref name="history"/>.</summary>
    internal RecordingClarificationFacade(
        IReadOnlyList<(string GoalId, PersistedClarification Clarification)>? history = null)
        : this((_, _) => new FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(
            true,
            history ?? [],
            null,
            FacadeErrorKind.None))
    {
    }

    /// <summary>Creates a recorder whose answer is scripted per call.</summary>
    internal RecordingClarificationFacade(
        Func<ClaimsPrincipal?, CancellationToken, FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>> history)
        => _history = history;

    /// <summary>Every identity the page passed, in call order.</summary>
    internal List<ClaimsPrincipal?> ActingUsers { get; } = [];

    /// <summary>How many times the page called the persisted-history operation.</summary>
    internal int HistoryCallCount => ActingUsers.Count;

    public Task<FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>> GetPersistedHistoryAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        ActingUsers.Add(actingUser);
        return Task.FromResult(_history!(actingUser, ct));
    }

    public Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetAllRequestsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct) =>
        Task.FromResult(new FacadeResult<IReadOnlyList<ClarificationRequest>>(
            true, [], null, FacadeErrorKind.None));

    public Task<FacadeResult<IReadOnlyList<ClarificationRequest>>> GetPendingHumanRequestsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct) =>
        Task.FromResult(new FacadeResult<IReadOnlyList<ClarificationRequest>>(
            true, [], null, FacadeErrorKind.None));

    public Task<FacadeResult<int>> GetPendingHumanCountAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct) =>
        Task.FromResult(new FacadeResult<int>(true, 0, null, FacadeErrorKind.None));

    public Task<FacadeResult<ClarificationAnswerResult>> SubmitAnswerAsync(
        string id, string answer, ClaimsPrincipal? actingUser, CancellationToken ct) =>
        Task.FromResult(new FacadeResult<ClarificationAnswerResult>(
            false, null, "not used by the page", FacadeErrorKind.NotFound));

    /// <summary>The shared denial answer the real facades produce.</summary>
    internal static FacadeResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> Denied() =>
        new(false, default, CopilotHive.Security.FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

    /// <summary>A one-row history carrying the distinctive <paramref name="marker"/>.</summary>
    internal static IReadOnlyList<(string GoalId, PersistedClarification Clarification)> History(string marker) =>
    [
        ("hist-goal-marker", new PersistedClarification
        {
            Timestamp = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Phase = "Coding",
            WorkerRole = "coder",
            Question = marker,
            Answer = "recorded",
            AnsweredBy = "brain",
        }),
    ];
}
