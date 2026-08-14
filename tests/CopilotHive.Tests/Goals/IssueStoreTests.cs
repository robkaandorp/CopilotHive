using CopilotHive.Goals;
using CopilotHive.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Goals;

public sealed class IssueStoreTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly IssueStore _store;

    public IssueStoreTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new IssueStore(_dbContext, NullLogger<IssueStore>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private static Issue MakeIssue(
        string id = "test-issue",
        IssueType type = IssueType.Bug,
        string title = "Test issue",
        string description = "Detailed description",
        IssueSeverity severity = IssueSeverity.Medium,
        IssueStatus status = IssueStatus.Open,
        string? sourceGoalId = null,
        string? sourceRole = null,
        int? sourceIteration = null,
        DateTime? createdAt = null)
        => new()
        {
            Id = id,
            Type = type,
            Title = title,
            Description = description,
            Severity = severity,
            Status = status,
            SourceGoalId = sourceGoalId,
            SourceRole = sourceRole,
            SourceIteration = sourceIteration,
            CreatedAt = createdAt ?? new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
        };

    // ── Create ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIssue_PersistsAllFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(
            id: "create-all-fields",
            type: IssueType.CodeQuality,
            title: "Naming inconsistency",
            description: "The method names are inconsistent.",
            severity: IssueSeverity.High,
            status: IssueStatus.Triaged,
            sourceGoalId: "goal-1",
            sourceRole: "reviewer",
            sourceIteration: 2);
        issue.RepositoryNames.Add("CopilotHive");
        issue.RepositoryNames.Add("CopilotHive-Config");
        issue.LinkedGoalId = "goal-2";

        var result = await _store.CreateIssueAsync(issue, ct);

        Assert.Equal("create-all-fields", result.Id);

        var fetched = await _store.GetIssueAsync("create-all-fields", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueType.CodeQuality, fetched!.Type);
        Assert.Equal("Naming inconsistency", fetched.Title);
        Assert.Equal("The method names are inconsistent.", fetched.Description);
        Assert.Equal(IssueSeverity.High, fetched.Severity);
        Assert.Equal(IssueStatus.Triaged, fetched.Status);
        Assert.Equal(2, fetched.RepositoryNames.Count);
        Assert.Contains("CopilotHive", fetched.RepositoryNames);
        Assert.Contains("CopilotHive-Config", fetched.RepositoryNames);
        Assert.Equal("goal-1", fetched.SourceGoalId);
        Assert.Equal("reviewer", fetched.SourceRole);
        Assert.Equal(2, fetched.SourceIteration);
        Assert.Equal("goal-2", fetched.LinkedGoalId);
        Assert.Equal(new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc), fetched.CreatedAt);
    }

    [Fact]
    public async Task CreateIssue_DuplicateId_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue(), ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.CreateIssueAsync(MakeIssue(), ct));
    }

    [Fact]
    public async Task CreateIssue_EmptyId_ThrowsArgumentException()
    {
        var issue = MakeIssue(id: "");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.CreateIssueAsync(issue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateIssue_NullId_ThrowsArgumentException()
    {
        var issue = MakeIssue(id: null!);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.CreateIssueAsync(issue, TestContext.Current.CancellationToken));
    }

    // ── Get ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetIssue_Existing_ReturnsIssue()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue(), ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal("test-issue", fetched!.Id);
    }

    [Fact]
    public async Task GetIssue_NonExistent_ReturnsNull()
    {
        var result = await _store.GetIssueAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // ── GetAll ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllIssues_OrderedByCreatedAtDescending()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue("issue-oldest", createdAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)), ct);
        await _store.CreateIssueAsync(MakeIssue("issue-newest", createdAt: new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)), ct);
        await _store.CreateIssueAsync(MakeIssue("issue-middle", createdAt: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)), ct);

        var all = await _store.GetAllIssuesAsync(ct);

        Assert.Equal(3, all.Count);
        Assert.Equal("issue-newest", all[0].Id);
        Assert.Equal("issue-middle", all[1].Id);
        Assert.Equal("issue-oldest", all[2].Id);
    }

    // ── GetIssues filters ──────────────────────────────────────────────────

    [Fact]
    public async Task GetIssues_FilterByStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue("open-1", status: IssueStatus.Open), ct);
        await _store.CreateIssueAsync(MakeIssue("resolved-1", status: IssueStatus.Resolved), ct);
        await _store.CreateIssueAsync(MakeIssue("open-2", status: IssueStatus.Open), ct);

        var open = await _store.GetIssuesAsync(status: IssueStatus.Open, ct: ct);

        Assert.Equal(2, open.Count);
        Assert.All(open, i => Assert.Equal(IssueStatus.Open, i.Status));
    }

    [Fact]
    public async Task GetIssues_FilterByType()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue("bug-1", type: IssueType.Bug), ct);
        await _store.CreateIssueAsync(MakeIssue("sugg-1", type: IssueType.Suggestion), ct);
        await _store.CreateIssueAsync(MakeIssue("bug-2", type: IssueType.Bug), ct);

        var bugs = await _store.GetIssuesAsync(type: IssueType.Bug, ct: ct);

        Assert.Equal(2, bugs.Count);
        Assert.All(bugs, i => Assert.Equal(IssueType.Bug, i.Type));
    }

    [Fact]
    public async Task GetIssues_FilterBySeverity()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue("high-1", severity: IssueSeverity.High), ct);
        await _store.CreateIssueAsync(MakeIssue("low-1", severity: IssueSeverity.Low), ct);
        await _store.CreateIssueAsync(MakeIssue("high-2", severity: IssueSeverity.High), ct);

        var high = await _store.GetIssuesAsync(severity: IssueSeverity.High, ct: ct);

        Assert.Equal(2, high.Count);
        Assert.All(high, i => Assert.Equal(IssueSeverity.High, i.Severity));
    }

    [Fact]
    public async Task GetIssues_FilterBySourceGoalId()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue("g1-issue-1", sourceGoalId: "goal-1"), ct);
        await _store.CreateIssueAsync(MakeIssue("g2-issue-1", sourceGoalId: "goal-2"), ct);
        await _store.CreateIssueAsync(MakeIssue("g1-issue-2", sourceGoalId: "goal-1"), ct);

        var goal1 = await _store.GetIssuesAsync(sourceGoalId: "goal-1", ct: ct);

        Assert.Equal(2, goal1.Count);
        Assert.All(goal1, i => Assert.Equal("goal-1", i.SourceGoalId));
    }

    [Fact]
    public async Task GetIssues_FilterByRepository_CaseInsensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue1 = MakeIssue("repo-issue-1");
        issue1.RepositoryNames.Add("CopilotHive");
        await _store.CreateIssueAsync(issue1, ct);

        var issue2 = MakeIssue("repo-issue-2");
        issue2.RepositoryNames.Add("OtherRepo");
        await _store.CreateIssueAsync(issue2, ct);

        var issue3 = MakeIssue("repo-issue-3");
        issue3.RepositoryNames.Add("copilothive");
        issue3.RepositoryNames.Add("AnotherRepo");
        await _store.CreateIssueAsync(issue3, ct);

        var matches = await _store.GetIssuesAsync(repository: "copilothive", ct: ct);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, i => i.Id == "repo-issue-1");
        Assert.Contains(matches, i => i.Id == "repo-issue-3");
    }

    [Fact]
    public async Task GetIssues_CombinedFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue1 = MakeIssue("combined-1", type: IssueType.Bug, severity: IssueSeverity.High, status: IssueStatus.Open, sourceGoalId: "goal-1");
        issue1.RepositoryNames.Add("RepoA");
        await _store.CreateIssueAsync(issue1, ct);

        var issue2 = MakeIssue("combined-2", type: IssueType.Bug, severity: IssueSeverity.High, status: IssueStatus.Open, sourceGoalId: "goal-1");
        issue2.RepositoryNames.Add("RepoB");
        await _store.CreateIssueAsync(issue2, ct);

        var issue3 = MakeIssue("combined-3", type: IssueType.Suggestion, severity: IssueSeverity.High, status: IssueStatus.Open, sourceGoalId: "goal-1");
        issue3.RepositoryNames.Add("RepoA");
        await _store.CreateIssueAsync(issue3, ct);

        var matches = await _store.GetIssuesAsync(
            status: IssueStatus.Open,
            type: IssueType.Bug,
            severity: IssueSeverity.High,
            repository: "repoa",
            sourceGoalId: "goal-1",
            ct: ct);

        Assert.Single(matches);
        Assert.Equal("combined-1", matches[0].Id);
    }

    [Fact]
    public async Task GetIssues_NoFilters_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue("all-1"), ct);
        await _store.CreateIssueAsync(MakeIssue("all-2"), ct);
        await _store.CreateIssueAsync(MakeIssue("all-3"), ct);

        var all = await _store.GetIssuesAsync(ct: ct);

        Assert.Equal(3, all.Count);
    }

    // ── Update ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateIssue_MutableFieldsUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue();
        await _store.CreateIssueAsync(issue, ct);

        issue.Type = IssueType.Suggestion;
        issue.Title = "Updated title";
        issue.Description = "Updated description";
        issue.Severity = IssueSeverity.High;
        issue.Status = IssueStatus.InProgress;
        issue.RepositoryNames.Add("NewRepo");
        issue.LinkedGoalId = "linked-goal";
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueType.Suggestion, fetched!.Type);
        Assert.Equal("Updated title", fetched.Title);
        Assert.Equal("Updated description", fetched.Description);
        Assert.Equal(IssueSeverity.High, fetched.Severity);
        Assert.Equal(IssueStatus.InProgress, fetched.Status);
        Assert.Contains("NewRepo", fetched.RepositoryNames);
        Assert.Equal("linked-goal", fetched.LinkedGoalId);
    }

    [Fact]
    public async Task UpdateIssue_ImmutableFieldsPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(
            id: "immutable-test",
            sourceGoalId: "original-goal",
            sourceRole: "reviewer",
            sourceIteration: 3,
            createdAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _store.CreateIssueAsync(issue, ct);

        // Attempt to change immutable fields on the incoming instance.
        var incoming = new Issue
        {
            Id = "immutable-test",
            Type = IssueType.Bug,
            Title = "New title",
            Description = "New description",
            Severity = IssueSeverity.Low,
            Status = IssueStatus.Open,
            SourceGoalId = "hacked-goal",
            SourceRole = "hacked-role",
            SourceIteration = 99,
            CreatedAt = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        await _store.UpdateIssueAsync(incoming, ct);

        var fetched = await _store.GetIssueAsync("immutable-test", ct);
        Assert.NotNull(fetched);
        Assert.Equal("immutable-test", fetched!.Id);
        Assert.Equal("original-goal", fetched.SourceGoalId);
        Assert.Equal("reviewer", fetched.SourceRole);
        Assert.Equal(3, fetched.SourceIteration);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), fetched.CreatedAt);
    }

    [Fact]
    public async Task UpdateIssue_UpdatedAtSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue();
        await _store.CreateIssueAsync(issue, ct);

        issue.Title = "Changed title";
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.NotNull(fetched!.UpdatedAt);
        Assert.True(fetched.UpdatedAt!.Value > new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateIssue_ResolvedAtSet_OnNewResolve()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(status: IssueStatus.Open);
        await _store.CreateIssueAsync(issue, ct);

        issue.Status = IssueStatus.Resolved;
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueStatus.Resolved, fetched!.Status);
        Assert.NotNull(fetched.ResolvedAt);
    }

    [Fact]
    public async Task UpdateIssue_ResolvedAtSet_OnNewClose()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(status: IssueStatus.Open);
        await _store.CreateIssueAsync(issue, ct);

        // Open → Closed: ResolvedAt must be set (non-terminal → terminal).
        issue.Status = IssueStatus.Closed;
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueStatus.Closed, fetched!.Status);
        Assert.NotNull(fetched.ResolvedAt);
    }

    [Fact]
    public async Task UpdateIssue_ResolvedAtCleared_OnReopenFromClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(status: IssueStatus.Open);
        await _store.CreateIssueAsync(issue, ct);

        issue.Status = IssueStatus.Closed;
        await _store.UpdateIssueAsync(issue, ct);

        var resolvedAt = (await _store.GetIssueAsync("test-issue", ct))!.ResolvedAt;
        Assert.NotNull(resolvedAt);

        // Closed → Open: ResolvedAt must be cleared (terminal → non-terminal).
        issue.Status = IssueStatus.Open;
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueStatus.Open, fetched!.Status);
        Assert.Null(fetched.ResolvedAt);
    }

    [Fact]
    public async Task UpdateIssue_ResolvedAtPreserved_OnTerminalToTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(status: IssueStatus.Open);
        await _store.CreateIssueAsync(issue, ct);

        issue.Status = IssueStatus.Resolved;
        await _store.UpdateIssueAsync(issue, ct);

        var resolvedAt = (await _store.GetIssueAsync("test-issue", ct))!.ResolvedAt;
        Assert.NotNull(resolvedAt);

        // Resolved → Closed: ResolvedAt must be preserved, not reset.
        issue.Status = IssueStatus.Closed;
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueStatus.Closed, fetched!.Status);
        Assert.Equal(resolvedAt, fetched.ResolvedAt);
    }

    [Fact]
    public async Task UpdateIssue_ResolvedAtCleared_OnReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(status: IssueStatus.Open);
        await _store.CreateIssueAsync(issue, ct);

        issue.Status = IssueStatus.Resolved;
        await _store.UpdateIssueAsync(issue, ct);

        var resolvedAt = (await _store.GetIssueAsync("test-issue", ct))!.ResolvedAt;
        Assert.NotNull(resolvedAt);

        // Resolved → Open: ResolvedAt must be cleared.
        issue.Status = IssueStatus.Open;
        await _store.UpdateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.NotNull(fetched);
        Assert.Equal(IssueStatus.Open, fetched!.Status);
        Assert.Null(fetched.ResolvedAt);
    }

    [Fact]
    public async Task UpdateIssue_NotFound_ThrowsInvalidOperationException()
    {
        var issue = MakeIssue(id: "ghost");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.UpdateIssueAsync(issue, TestContext.Current.CancellationToken));
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteIssue_ReturnsTrue_OnSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.CreateIssueAsync(MakeIssue(), ct);

        var deleted = await _store.DeleteIssueAsync("test-issue", ct);
        Assert.True(deleted);

        var fetched = await _store.GetIssueAsync("test-issue", ct);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteIssue_ReturnsFalse_OnNotFound()
    {
        var deleted = await _store.DeleteIssueAsync("nonexistent", TestContext.Current.CancellationToken);
        Assert.False(deleted);
    }

    // ── Enum persistence ───────────────────────────────────────────────────

    [Fact]
    public async Task Enums_StoredAsLowercaseStrings()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue(
            id: "enum-check",
            type: IssueType.CodeQuality,
            severity: IssueSeverity.High,
            status: IssueStatus.InProgress);
        await _store.CreateIssueAsync(issue, ct);

        using var cmd = (SqliteCommand)_dbContext.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT type, severity, status FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", "enum-check");
        if (cmd.Connection?.State != System.Data.ConnectionState.Open)
            cmd.Connection?.Open();

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("codequality", reader.GetString(0));
        Assert.Equal("high", reader.GetString(1));
        Assert.Equal("inprogress", reader.GetString(2));
    }

    // ── JSON conversion ────────────────────────────────────────────────────

    [Fact]
    public async Task RepositoryNames_MultipleEntries_PersistedAndLoaded()
    {
        var ct = TestContext.Current.CancellationToken;
        var issue = MakeIssue("json-repos");
        issue.RepositoryNames.Add("Repo-A");
        issue.RepositoryNames.Add("Repo-B");
        issue.RepositoryNames.Add("Repo-C");
        await _store.CreateIssueAsync(issue, ct);

        var fetched = await _store.GetIssueAsync("json-repos", ct);
        Assert.NotNull(fetched);
        Assert.Equal(3, fetched!.RepositoryNames.Count);
        Assert.Contains("Repo-A", fetched.RepositoryNames);
        Assert.Contains("Repo-B", fetched.RepositoryNames);
        Assert.Contains("Repo-C", fetched.RepositoryNames);
    }
}
