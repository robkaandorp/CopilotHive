using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using SharpCoder;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="EventBusStartupScanner"/>: reconstructing and publishing events
/// for state changes (goals completed/failed, issues raised/resolved, releases completed)
/// that occurred while the orchestrator was down.
/// </summary>
public sealed class EventBusStartupScannerTests
{
    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];
        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    private sealed class FakeGoalStore : IGoalStore
    {
        public string Name => "fake";
        public List<Goal> Goals { get; } = [];
        public List<Release> Releases { get; } = [];

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Goals.ToList().AsReadOnly());

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult(Goals.FirstOrDefault(g => g.Id == goalId));

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            Goals.Add(goal);
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            var idx = Goals.FindIndex(g => g.Id == goal.Id);
            if (idx >= 0) Goals[idx] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult(Goals.RemoveAll(g => g.Id == goalId) > 0);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Goals.Where(g => g.Status == status).ToList().AsReadOnly());

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
        {
            Releases.Add(release);
            return Task.FromResult(release);
        }

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(Releases.FirstOrDefault(r => r.Id == releaseId));

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>(Releases.ToList().AsReadOnly());

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
        {
            var idx = Releases.FindIndex(r => r.Id == release.Id);
            if (idx >= 0) Releases[idx] = release;
            return Task.CompletedTask;
        }

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(Releases.RemoveAll(r => r.Id == releaseId) > 0);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Goals.Where(g => g.ReleaseId == releaseId).ToList().AsReadOnly());

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>(Array.Empty<(string, PersistedClarification)>());

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Goals.Where(g => g.Status == GoalStatus.Pending).ToList().AsReadOnly());

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeIssueStore : IIssueStore
    {
        public List<Issue> Issues { get; } = [];

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.ToList().AsReadOnly());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.ToList().AsReadOnly());

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.FirstOrDefault(i => i.Id == issueId));

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Issues.Add(issue);
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            var idx = Issues.FindIndex(i => i.Id == issue.Id);
            if (idx >= 0) Issues[idx] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.RemoveAll(i => i.Id == issueId) > 0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures every log entry so tests can assert that a specific warning was actually
    /// emitted. Using <see cref="NullLogger{T}"/> instead would let a deleted
    /// <c>LogWarning</c> call keep the suite green.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception), exception));
        }

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Snapshot()
        {
            lock (_entries) return _entries.ToList();
        }

        /// <summary>Whether a <see cref="LogLevel.Warning"/> entry containing <paramref name="fragment"/> was logged.</summary>
        public bool HasWarningContaining(string fragment) =>
            Snapshot().Any(e => e.Level == LogLevel.Warning
                && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A logger that throws from <see cref="Log{TState}"/> — but only for messages containing a
    /// given fragment, and only while armed. This makes <c>ILogger.Log</c> observably fallible so
    /// a test can prove that state resets happen BEFORE the first logging call rather than after
    /// it. Arming is deferred so a preceding successful operation can log normally, and
    /// <see cref="Disarm"/> restores normal behaviour for cleanup.
    /// </summary>
    private sealed class ArmableThrowingLogger<T>(string throwOnFragment) : ILogger<T>
    {
        /// <summary>Message of the exception thrown by an armed logger.</summary>
        public const string FailureMessage = "simulated logger failure";

        private readonly string _throwOnFragment = throwOnFragment;
        private volatile bool _armed;
        private volatile bool _fired;

        /// <summary>Whether the logger has actually thrown, proving the failure point was reached.</summary>
        public bool Fired => _fired;

        /// <summary>Makes subsequent matching log calls throw.</summary>
        public void Arm() => _armed = true;

        /// <summary>Restores normal (non-throwing) behaviour.</summary>
        public void Disarm() => _armed = false;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_armed)
                return;

            if (!formatter(state, exception).Contains(_throwOnFragment, StringComparison.Ordinal))
                return;

            _fired = true;
            throw new InvalidOperationException(FailureMessage);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"event-bus-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Creates a connected Composer. When <paramref name="lastActivityAt"/> is non-null,
    /// a session file is written to disk first so the session is loaded from disk on connect.
    /// </summary>
    private static async Task<Composer> CreateConnectedComposerAsync(
        string stateDir, IGoalStore goalStore, DateTimeOffset? lastActivityAt = null)
    {
        if (lastActivityAt.HasValue)
        {
            var session = AgentSession.Create("composer");
            session.LastActivityAt = lastActivityAt.Value;
            await session.SaveAsync(Path.Combine(stateDir, "composer-session.json"), CancellationToken.None);
        }

        var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            goalStore,
            stateDir: stateDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await composer.ConnectAsync();
        return composer;
    }

    /// <summary>Creates a Composer that has NOT been connected (fresh session, no disk load).</summary>
    private static Composer CreateUnconnectedComposer(string stateDir, IGoalStore goalStore)
        => new(
            "test-model",
            NullLogger<Composer>.Instance,
            goalStore,
            stateDir: stateDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

    private static EventBusStartupScanner CreateScanner(
        IGoalStore? goalStore, IIssueStore? issueStore, IEventBus eventBus, Composer composer,
        ILogger<EventBusStartupScanner>? logger = null)
        => new(goalStore, issueStore, eventBus, composer,
            logger ?? NullLogger<EventBusStartupScanner>.Instance);

    private static async Task DisposeComposerAsync(Composer? composer)
    {
        if (composer is null) return;
        try { await composer.DisposeAsync(); }
        catch { /* disposal failures must not mask the test result */ }
    }

    // ── Goal events ───────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_GoalCompletedAfterCutoff_PublishesGoalCompleted()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var completedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-completed",
            Description = "Test goal",
            Status = GoalStatus.Completed,
            CompletedAt = completedAt,
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.GoalCompleted, evt.Type);
            Assert.Equal("goal-completed", evt.GoalId);
            Assert.Equal("Goal merged successfully", evt.Message);
            Assert.Equal(completedAt, evt.Timestamp);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_GoalFailedAfterCutoff_PublishesGoalFailedWithFailureReason()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var completedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-failed",
            Description = "Test goal",
            Status = GoalStatus.Failed,
            CompletedAt = completedAt,
            FailureReason = "build broke",
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.GoalFailed, evt.Type);
            Assert.Equal("goal-failed", evt.GoalId);
            Assert.Equal("build broke", evt.Message);
            Assert.Equal(completedAt, evt.Timestamp);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_GoalFailedWithoutFailureReason_UsesDefaultMessage()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-failed-no-reason",
            Description = "Test goal",
            Status = GoalStatus.Failed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromHours(1),
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.GoalFailed, evt.Type);
            Assert.Equal("Goal failed", evt.Message);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_GoalCompletedBeforeCutoff_NotPublished()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-old",
            Description = "Test goal",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before 2h cutoff
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_NonTerminalGoalStatus_NotPublished()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-pending",
            Description = "Test goal",
            Status = GoalStatus.Pending,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromHours(1),
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Issue events ──────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_IssueRaisedAfterCutoff_PublishesIssueRaised()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var createdAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-1",
            Title = "Parser bug",
            Description = "The parser crashes",
            CreatedAt = createdAt,
            SourceGoalId = "goal-1",
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.IssueRaised, evt.Type);
            Assert.Equal("issue-1", evt.IssueId);
            Assert.Equal("Parser bug", evt.Message);
            Assert.Equal("goal-1", evt.GoalId);
            Assert.Equal(createdAt, evt.Timestamp);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_IssueResolvedAfterCutoff_PublishesIssueResolved()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var resolvedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-2",
            Title = "Fixed bug",
            Description = "Fixed",
            Status = IssueStatus.Resolved,
            CreatedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before cutoff — only IssueResolved
            ResolvedAt = resolvedAt,
            LinkedGoalId = "goal-2",
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.IssueResolved, evt.Type);
            Assert.Equal("issue-2", evt.IssueId);
            Assert.Equal("Issue 'issue-2' marked as Resolved", evt.Message);
            Assert.Equal("goal-2", evt.GoalId);
            Assert.Equal(resolvedAt, evt.Timestamp);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_IssueClosedAfterCutoff_PublishesIssueResolvedWithClosedStatus()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var resolvedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-3",
            Title = "Closed bug",
            Description = "Closed",
            Status = IssueStatus.Closed,
            CreatedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before cutoff — only IssueResolved
            ResolvedAt = resolvedAt,
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.IssueResolved, evt.Type);
            Assert.Equal("Issue 'issue-3' marked as Closed", evt.Message);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_IssueRaisedBeforeCutoff_NotPublished()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-old",
            Title = "Old issue",
            Description = "Old",
            CreatedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before 2h cutoff
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_IssueResolvedBeforeCutoff_NotPublished()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-old-resolved",
            Title = "Old resolved",
            Description = "Old",
            Status = IssueStatus.Resolved,
            CreatedAt = DateTime.UtcNow - TimeSpan.FromHours(4), // before cutoff
            ResolvedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before 2h cutoff
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Release events ────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_ReleaseCompletedAfterCutoff_PublishesReleaseCompleted()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var releasedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        goalStore.Releases.Add(new Release
        {
            Id = "v1.2.0",
            Tag = "v1.2.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = releasedAt,
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.ReleaseCompleted, evt.Type);
            Assert.Equal("v1.2.0", evt.ReleaseId);
            Assert.Equal("Release 'v1.2.0' marked as Released", evt.Message);
            Assert.Equal(releasedAt, evt.Timestamp);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_ReleasePlanningStatus_NotPublished()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        goalStore.Releases.Add(new Release
        {
            Id = "v1.3.0",
            Tag = "v1.3.0",
            Status = ReleaseStatus.Planning,
            ReleasedAt = DateTime.UtcNow - TimeSpan.FromHours(1),
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_ReleaseReleasedBeforeCutoff_NotPublished()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        goalStore.Releases.Add(new Release
        {
            Id = "v0.9.0",
            Tag = "v0.9.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before 2h cutoff
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Ordering and fidelity ─────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_MultipleEvents_PublishedInChronologicalOrder()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();

        var t1 = DateTime.UtcNow - TimeSpan.FromMinutes(50);
        var t2 = DateTime.UtcNow - TimeSpan.FromMinutes(40);
        var t3 = DateTime.UtcNow - TimeSpan.FromMinutes(30);
        var t4 = DateTime.UtcNow - TimeSpan.FromMinutes(20);

        goalStore.Goals.Add(new Goal
        {
            Id = "goal-a",
            Description = "A",
            Status = GoalStatus.Completed,
            CompletedAt = t1,
        });
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-a",
            Title = "Issue A",
            Description = "A",
            CreatedAt = t2,
        });
        goalStore.Releases.Add(new Release
        {
            Id = "rel-a",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = t3,
        });
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-b",
            Title = "Issue B",
            Description = "B",
            Status = IssueStatus.Resolved,
            CreatedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before cutoff — only IssueResolved
            ResolvedAt = t4,
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, eventBus.Published.Count);
            Assert.Equal(t1, eventBus.Published[0].Timestamp);
            Assert.Equal(t2, eventBus.Published[1].Timestamp);
            Assert.Equal(t3, eventBus.Published[2].Timestamp);
            Assert.Equal(t4, eventBus.Published[3].Timestamp);

            Assert.Equal(EventType.GoalCompleted, eventBus.Published[0].Type);
            Assert.Equal(EventType.IssueRaised, eventBus.Published[1].Type);
            Assert.Equal(EventType.ReleaseCompleted, eventBus.Published[2].Type);
            Assert.Equal(EventType.IssueResolved, eventBus.Published[3].Type);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_EventsCarryOriginalTimestampsAndFullEntityIds()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();

        var goalCompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(45);
        var issueCreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(35);
        var releaseReleasedAt = DateTime.UtcNow - TimeSpan.FromMinutes(25);
        var issueResolvedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15);

        goalStore.Goals.Add(new Goal
        {
            Id = "full-goal-id-123",
            Description = "Full ID goal",
            Status = GoalStatus.Completed,
            CompletedAt = goalCompletedAt,
        });
        issueStore.Issues.Add(new Issue
        {
            Id = "full-issue-id-456",
            Title = "Full ID issue",
            Description = "Full ID",
            CreatedAt = issueCreatedAt,
            SourceGoalId = "source-goal-789",
        });
        goalStore.Releases.Add(new Release
        {
            Id = "full-release-id-101",
            Tag = "v2.0.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = releaseReleasedAt,
        });
        issueStore.Issues.Add(new Issue
        {
            Id = "full-issue-id-202",
            Title = "Resolved issue",
            Description = "Resolved",
            Status = IssueStatus.Resolved,
            CreatedAt = DateTime.UtcNow - TimeSpan.FromHours(3), // before cutoff — only IssueResolved
            ResolvedAt = issueResolvedAt,
            LinkedGoalId = "linked-goal-303",
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Equal(4, eventBus.Published.Count);

            var goalEvt = eventBus.Published[0];
            Assert.Equal(EventType.GoalCompleted, goalEvt.Type);
            Assert.Equal("full-goal-id-123", goalEvt.GoalId);
            Assert.Equal(goalCompletedAt, goalEvt.Timestamp);

            var issueRaisedEvt = eventBus.Published[1];
            Assert.Equal(EventType.IssueRaised, issueRaisedEvt.Type);
            Assert.Equal("full-issue-id-456", issueRaisedEvt.IssueId);
            Assert.Equal("source-goal-789", issueRaisedEvt.GoalId);
            Assert.Equal(issueCreatedAt, issueRaisedEvt.Timestamp);

            var releaseEvt = eventBus.Published[2];
            Assert.Equal(EventType.ReleaseCompleted, releaseEvt.Type);
            Assert.Equal("full-release-id-101", releaseEvt.ReleaseId);
            Assert.Equal(releaseReleasedAt, releaseEvt.Timestamp);

            var issueResolvedEvt = eventBus.Published[3];
            Assert.Equal(EventType.IssueResolved, issueResolvedEvt.Type);
            Assert.Equal("full-issue-id-202", issueResolvedEvt.IssueId);
            Assert.Equal("linked-goal-303", issueResolvedEvt.GoalId);
            Assert.Equal(issueResolvedAt, issueResolvedEvt.Timestamp);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Cutoff computation ────────────────────────────────────────────────

    [Fact]
    public async Task ScanAsync_SessionLoadedFromDiskWithValidLastActivityAt_UsesLastActivityAsCutoff()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();

        // LastActivityAt is 2 hours ago. A goal completed 90 minutes ago is AFTER the
        // session-activity cutoff but BEFORE the 60-minute fallback — proving the cutoff
        // is LastActivityAt, not the fallback.
        var lastActivity = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        var completedAt = DateTime.UtcNow - TimeSpan.FromMinutes(90);
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-between-cutoffs",
            Description = "Between cutoffs",
            Status = GoalStatus.Completed,
            CompletedAt = completedAt,
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(stateDir, goalStore, lastActivity);
            Assert.True(composer.SessionLoadedFromDisk);
            Assert.NotNull(composer.GetLastSessionActivity());

            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.GoalCompleted, evt.Type);
            Assert.Equal("goal-between-cutoffs", evt.GoalId);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_SessionNotLoadedFromDisk_UsesSixtyMinuteFallback()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();

        // Fresh (unconnected) Composer: SessionLoadedFromDisk = false → 60-minute fallback.
        // A goal completed 30 minutes ago is inside the fallback window → published.
        // A goal completed 90 minutes ago is outside → not published.
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-recent",
            Description = "Recent",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        });
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-stale",
            Description = "Stale",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(90),
        });

        Composer? composer = null;
        try
        {
            composer = CreateUnconnectedComposer(stateDir, goalStore);
            Assert.False(composer.SessionLoadedFromDisk);
            Assert.Null(composer.GetLastSessionActivity());

            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal("goal-recent", evt.GoalId);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_SessionLoadedFromDiskWithMinValueLastActivity_UsesSixtyMinuteFallback()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();

        // Session loaded from disk but LastActivityAt = MinValue → 60-minute fallback.
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-recent-min",
            Description = "Recent",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        });
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-stale-min",
            Description = "Stale",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(90),
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.MinValue);
            Assert.True(composer.SessionLoadedFromDisk);
            Assert.NotNull(composer.GetLastSessionActivity());
            Assert.Equal(DateTimeOffset.MinValue, composer.GetLastSessionActivity());

            var scanner = CreateScanner(goalStore, issueStore, eventBus, composer);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            var evt = Assert.Single(eventBus.Published);
            Assert.Equal("goal-recent-min", evt.GoalId);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Null stores ────────────────────────────────────────────────────────
    //
    // These use RecordingLogger (not NullLogger) so the warning assertions are
    // removal-proof: deleting the LogWarning call in EventBusStartupScanner fails the test.

    [Fact]
    public async Task ScanAsync_NullGoalStore_LogsWarningAndSkipsGoalsAndReleases_StillScansIssues()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<EventBusStartupScanner>();

        // Seeded into the (non-null) store instance that is NOT handed to the scanner: if the
        // scanner ever reached a goal store these would surface as extra published events.
        goalStore.Goals.Add(new Goal
        {
            Id = "goal-must-not-be-scanned",
            Description = "Never scanned",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        });
        goalStore.Releases.Add(new Release
        {
            Id = "rel-must-not-be-scanned",
            Tag = "v9.9.9",
            Status = ReleaseStatus.Released,
            ReleasedAt = DateTime.UtcNow - TimeSpan.FromMinutes(25),
        });
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-only",
            Title = "Only issue",
            Description = "Only",
            CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore: null, issueStore, eventBus, composer, logger);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            // Issues are still scanned…
            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.IssueRaised, evt.Type);
            Assert.Equal("issue-only", evt.IssueId);

            // …and goals/releases are skipped entirely.
            Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.GoalCompleted);
            Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.ReleaseCompleted);

            // The skip must be reported, not silent.
            Assert.True(logger.HasWarningContaining("no goal store available"),
                "A null IGoalStore must emit a warning naming the missing goal store. "
                + $"Logged entries: [{string.Join(" | ", logger.Snapshot().Select(e => $"{e.Level}: {e.Message}"))}]");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_NullIssueStore_LogsWarningAndSkipsIssues_StillScansGoalsAndReleases()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var issueStore = new FakeIssueStore();
        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<EventBusStartupScanner>();

        goalStore.Goals.Add(new Goal
        {
            Id = "goal-only",
            Description = "Only goal",
            Status = GoalStatus.Completed,
            CompletedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        });
        goalStore.Releases.Add(new Release
        {
            Id = "rel-only",
            Tag = "v1.0.0",
            Status = ReleaseStatus.Released,
            ReleasedAt = DateTime.UtcNow - TimeSpan.FromMinutes(20),
        });

        // Seeded into the store instance that is NOT handed to the scanner: reaching an issue
        // store would surface as extra published events.
        issueStore.Issues.Add(new Issue
        {
            Id = "issue-must-not-be-scanned",
            Title = "Never scanned",
            Description = "Never",
            CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30),
        });

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore, issueStore: null, eventBus, composer, logger);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            // Goals and releases are still scanned…
            Assert.Equal(2, eventBus.Published.Count);
            Assert.Equal(EventType.GoalCompleted, eventBus.Published[0].Type);
            Assert.Equal(EventType.ReleaseCompleted, eventBus.Published[1].Type);

            // …and issues are skipped entirely.
            Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.IssueRaised);
            Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.IssueResolved);

            Assert.True(logger.HasWarningContaining("no issue store available"),
                "A null IIssueStore must emit a warning naming the missing issue store. "
                + $"Logged entries: [{string.Join(" | ", logger.Snapshot().Select(e => $"{e.Level}: {e.Message}"))}]");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task ScanAsync_BothStoresNull_PublishesNoEventsAndLogsBothWarnings()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var eventBus = new RecordingEventBus();
        var logger = new RecordingLogger<EventBusStartupScanner>();

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
            var scanner = CreateScanner(goalStore: null, issueStore: null, eventBus, composer, logger);

            await scanner.ScanAsync(TestContext.Current.CancellationToken);

            Assert.Empty(eventBus.Published);

            // Both skips must be reported — one warning per missing store.
            Assert.True(logger.HasWarningContaining("no goal store available"),
                "A null IGoalStore must emit its own warning even when both stores are null.");
            Assert.True(logger.HasWarningContaining("no issue store available"),
                "A null IIssueStore must emit its own warning even when both stores are null.");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Composer.SessionLoadedFromDisk connection contract ────────────────
    //
    // The flag means "session was loaded from disk AND the connection succeeded". These tests
    // are removal-proof for that contract: each fails if the reset is moved or deleted.

    [Fact]
    public async Task SessionLoadedFromDisk_AfterSuccessfulDiskLoadedConnection_IsTrue()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(
                stateDir, goalStore, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

            Assert.True(composer.IsConnected);
            Assert.True(composer.SessionLoadedFromDisk,
                "A successfully loaded composer-session.json plus a successful connection must set the flag.");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task SessionLoadedFromDisk_NoSessionFile_IsFalseAfterSuccessfulConnection()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();

        Composer? composer = null;
        try
        {
            // No session file written → fresh session, nothing loaded from disk.
            composer = await CreateConnectedComposerAsync(stateDir, goalStore, lastActivityAt: null);

            Assert.True(composer.IsConnected);
            Assert.False(composer.SessionLoadedFromDisk,
                "With no composer-session.json on disk the flag must stay false.");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// The session file loads successfully, then <c>RecreateAgentAsync</c> fails (the chat-client
    /// factory hands back <c>null</c>). The flag must NOT survive the failed connection.
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_ConnectFailsAfterSuccessfulLoad_IsResetToFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();

        // A valid session file so the disk load succeeds and would otherwise set the flag.
        var session = AgentSession.Create("composer");
        session.LastActivityAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await session.SaveAsync(Path.Combine(stateDir, "composer-session.json"), ct);

        Composer? composer = null;
        try
        {
            // Returning null leaves _chatClient null, so RecreateAgentAsync throws
            // "Composer not connected" AFTER the session file has been loaded.
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                goalStore,
                stateDir: stateDir,
                chatClientFactory: _ => null!);

            await Assert.ThrowsAsync<InvalidOperationException>(() => composer.ConnectAsync(ct));

            Assert.False(composer.IsConnected);
            Assert.False(composer.SessionLoadedFromDisk,
                "A connection that failed after the disk load must leave the flag false — "
                + "the flag means 'loaded AND connected'.");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// The exact regression the flag ordering guards: a first connection succeeds with a
    /// disk-loaded session (flag <c>true</c>), then a reconnect throws during the teardown that
    /// runs before any new state is built. If the reset were placed AFTER
    /// <c>DisposeClientsAndClearStateAsync</c>, the failed reconnect would exit with a stale
    /// <c>true</c> and the startup scan would use a cutoff from a session that is no longer live.
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_ReconnectThrowsDuringTeardown_IsResetToFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();

        var session = AgentSession.Create("composer");
        session.LastActivityAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await session.SaveAsync(Path.Combine(stateDir, "composer-session.json"), ct);

        // Disposal throws, so the SECOND ConnectAsync fails inside the teardown that runs
        // before the session file is even looked at. The FIRST connection is unaffected:
        // there is no client to dispose yet.
        var throwingClient = new Mock<IChatClient>();
        throwingClient.Setup(c => c.Dispose())
            .Throws(new InvalidOperationException("simulated client disposal failure"));

        Composer? composer = null;
        try
        {
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                goalStore,
                stateDir: stateDir,
                chatClientFactory: _ => throwingClient.Object);

            await composer.ConnectAsync(ct);
            Assert.True(composer.SessionLoadedFromDisk,
                "Precondition: the first connection must load the session from disk.");

            // Reconnect: teardown clears the connection state and then throws.
            await Assert.ThrowsAnyAsync<Exception>(() => composer.ConnectAsync(ct));

            Assert.False(composer.IsConnected);
            Assert.False(composer.SessionLoadedFromDisk,
                "A reconnect that threw during teardown must not leave the flag true from the "
                + "previous successful connection.");
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    /// <summary>
    /// The reset must precede the very first fallible operation in <c>ConnectAsync</c> — which
    /// is the opening <c>_logger.LogInformation</c> call, not the teardown. <c>ILogger.Log</c>
    /// is fallible (a logger or provider may throw), so if the reset sat after it, a reconnect
    /// that failed on that log call would exit with a stale <c>true</c> from the previous
    /// successful disk-loaded connection. This test fails if the reset is moved below the log.
    /// </summary>
    [Fact]
    public async Task SessionLoadedFromDisk_ReconnectThrowsOnFirstLogCall_IsResetToFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();

        var session = AgentSession.Create("composer");
        session.LastActivityAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await session.SaveAsync(Path.Combine(stateDir, "composer-session.json"), ct);

        // Armed only for the reconnect, so the first connection completes normally and genuinely
        // sets the flag. Once armed it throws on the opening "Composer connecting" log — the very
        // first fallible operation of ConnectAsync — and then disarms so test cleanup can log.
        var logger = new ArmableThrowingLogger<Composer>("Composer connecting with model");

        Composer? composer = null;
        try
        {
            composer = new Composer(
                "test-model",
                logger,
                goalStore,
                stateDir: stateDir,
                chatClientFactory: _ => new Mock<IChatClient>().Object);

            await composer.ConnectAsync(ct);
            Assert.True(composer.SessionLoadedFromDisk,
                "Precondition: the first connection must load the session from disk.");

            logger.Arm();

            // Reconnect fails on the opening log call, before teardown or anything else runs.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => composer.ConnectAsync(ct));
            Assert.Equal(ArmableThrowingLogger<Composer>.FailureMessage, ex.Message);

            Assert.True(logger.Fired,
                "Precondition: the throw must have come from the opening log call.");

            Assert.False(composer.SessionLoadedFromDisk,
                "The reset must be the literal first statement of ConnectAsync: a reconnect that "
                + "threw on the opening log call must not leave the flag true from the previous "
                + "successful connection.");
        }
        finally
        {
            logger.Disarm();
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    // ── Composer.GetLastSessionActivity ────────────────────────────────────

    [Fact]
    public async Task GetLastSessionActivity_WhenConnected_ReturnsLastActivityAt()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();
        var lastActivity = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);

        Composer? composer = null;
        try
        {
            composer = await CreateConnectedComposerAsync(stateDir, goalStore, lastActivity);

            var result = composer.GetLastSessionActivity();
            Assert.NotNull(result);
            Assert.Equal(lastActivity, result);
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }

    [Fact]
    public async Task GetLastSessionActivity_WhenNotConnected_ReturnsNull()
    {
        var stateDir = CreateTempDir();
        var goalStore = new FakeGoalStore();

        Composer? composer = null;
        try
        {
            composer = CreateUnconnectedComposer(stateDir, goalStore);

            Assert.Null(composer.GetLastSessionActivity());
        }
        finally
        {
            await DisposeComposerAsync(composer);
            TryDeleteDir(stateDir);
        }
    }
}
