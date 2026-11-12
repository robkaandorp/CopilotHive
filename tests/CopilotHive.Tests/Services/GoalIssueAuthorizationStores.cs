using CopilotHive.Goals;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE SHARED RECORDING STORES for the goal/issue authorization tests: in-memory
/// <see cref="IGoalStore"/> and <see cref="IIssueStore"/> implementations that RECORD every
/// read and write, so a refused operation can be proven to have performed NO store work at all
/// and a side effect can be proven to have happened EXACTLY once.
/// <para>
/// They are shared by the facade matrix, the page regression and the mutation battery so every
/// test family observes the same recorded-call surface.
/// </para>
/// </summary>
internal sealed class RecordingGoalStore : IGoalStore, IDisposable
{
    private readonly Dictionary<string, Goal> _goals = new();
    private readonly Dictionary<string, Release> _releases = new();

    /// <inheritdoc />
    public string Name => "recording-goal-store";

    /// <summary>Goal IDs passed to <see cref="GetGoalAsync"/>, in call order.</summary>
    public List<string> ReadCalls { get; } = [];

    /// <summary>Number of <see cref="GetAllGoalsAsync"/> calls.</summary>
    public int GetAllCalls { get; private set; }

    /// <summary>Number of <see cref="GetReleasesAsync"/> calls.</summary>
    public int ReleaseReads { get; private set; }

    /// <summary>Goal IDs passed to <see cref="DeleteGoalAsync"/>, in call order.</summary>
    public List<string> DeleteCalls { get; } = [];

    /// <summary>(goalId, status) pairs passed to <see cref="UpdateGoalStatusAsync"/>, in call order.</summary>
    public List<(string GoalId, GoalStatus Status)> StatusUpdates { get; } = [];

    /// <summary>Goal IDs passed to <see cref="ResetGoalIterationDataAsync"/>, in call order.</summary>
    public List<string> ResetCalls { get; } = [];

    /// <summary>Goals passed to <see cref="UpdateGoalAsync"/>, in call order.</summary>
    public List<Goal> GoalUpdates { get; } = [];

    /// <summary>Adds a goal to the store.</summary>
    public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

    /// <summary>Adds a release to the store.</summary>
    public void AddRelease(Release release) => _releases[release.Id] = release;

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
    {
        GetAllCalls++;
        return Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList());
    }

    /// <inheritdoc />
    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
    {
        ReadCalls.Add(goalId);
        return Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);
    }

    /// <inheritdoc />
    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    /// <inheritdoc />
    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        GoalUpdates.Add(goal);
        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
    {
        DeleteCalls.Add(goalId);
        return Task.FromResult(_goals.Remove(goalId));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
        string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(
            _goals.Values
                .Where(g => g.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Where(g => statusFilter is null || g.Status == statusFilter)
                .ToList());

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.Where(g => g.Status == status).ToList());

    /// <inheritdoc />
    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(
            _goals.TryGetValue(goalId, out var goal) ? goal.IterationSummaries : []);

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    /// <inheritdoc />
    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        StatusUpdates.Add((goalId, status));
        if (_goals.TryGetValue(goalId, out var goal))
            goal.Status = status;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
    {
        _releases[release.Id] = release;
        return Task.FromResult(release);
    }

    /// <inheritdoc />
    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(_releases.TryGetValue(releaseId, out var release) ? release : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        ReleaseReads++;
        return Task.FromResult<IReadOnlyList<Release>>(_releases.Values.ToList());
    }

    /// <inheritdoc />
    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
        string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    /// <inheritdoc />
    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
    {
        ResetCalls.Add(goalId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
        int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);

    /// <summary>No unmanaged state: the fake is a plain dictionary.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// The shared recording <see cref="IIssueStore"/>: every call is recorded, so a refused issue
/// operation can be proven to have performed no store work at all.
/// </summary>
internal sealed class RecordingIssueStore : IIssueStore, IDisposable
{
    private readonly Dictionary<string, Issue> _issues = new();

    /// <summary>Every filter tuple the facade queried, in call order.</summary>
    public List<(IssueStatus? Status, IssueType? Type, IssueSeverity? Severity, string? Repository,
        string? SourceGoalId, string? LinkedGoalId)> Queries { get; } = [];

    /// <summary>Every cancellation token the facade forwarded, in call order.</summary>
    public List<CancellationToken> Tokens { get; } = [];

    /// <summary>Adds an issue to the store.</summary>
    public void AddIssue(Issue issue) => _issues[issue.Id] = issue;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
    {
        Tokens.Add(ct);
        return Task.FromResult<IReadOnlyList<Issue>>(_issues.Values.ToList());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> GetIssuesAsync(
        IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
        string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
        CancellationToken ct = default)
    {
        Queries.Add((status, type, severity, repository, sourceGoalId, linkedGoalId));
        Tokens.Add(ct);

        IEnumerable<Issue> query = _issues.Values;
        if (status is not null) query = query.Where(i => i.Status == status);
        if (type is not null) query = query.Where(i => i.Type == type);
        if (severity is not null) query = query.Where(i => i.Severity == severity);
        if (repository is not null) query = query.Where(i => i.RepositoryNames.Contains(repository, StringComparer.OrdinalIgnoreCase));
        if (sourceGoalId is not null) query = query.Where(i => i.SourceGoalId == sourceGoalId);
        if (linkedGoalId is not null) query = query.Where(i => i.LinkedGoalId == linkedGoalId);
        return Task.FromResult<IReadOnlyList<Issue>>(query.ToList());
    }

    /// <inheritdoc />
    public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
    {
        Tokens.Add(ct);
        return Task.FromResult(_issues.TryGetValue(issueId, out var issue) ? issue : null);
    }

    /// <inheritdoc />
    public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
    {
        Tokens.Add(ct);
        if (_issues.ContainsKey(issue.Id))
            throw new InvalidOperationException($"Issue '{issue.Id}' already exists.");
        _issues[issue.Id] = issue;
        return Task.FromResult(issue);
    }

    /// <inheritdoc />
    public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
    {
        Tokens.Add(ct);
        if (!_issues.ContainsKey(issue.Id))
            throw new InvalidOperationException($"Issue '{issue.Id}' not found.");
        _issues[issue.Id] = issue;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
    {
        Tokens.Add(ct);
        return Task.FromResult(_issues.Remove(issueId));
    }

    /// <summary>No unmanaged state: the fake is a plain dictionary.</summary>
    public void Dispose()
    {
    }
}
