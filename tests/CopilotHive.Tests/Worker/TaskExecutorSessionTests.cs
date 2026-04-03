extern alias WorkerAssembly;

using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;
using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for <see cref="TaskExecutor"/> session management: loading a session before
/// task execution and saving it afterward. Verifies graceful error handling when the
/// session client fails.
/// </summary>
public sealed class TaskExecutorSessionTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="ISessionClient"/> that stores sessions in-memory.
    /// Supports optional fault injection.
    /// </summary>
    private sealed class FakeSessionClient : ISessionClient
    {
        private readonly Dictionary<string, string> _store = new();

        /// <summary>If set to true, <see cref="GetSessionAsync"/> throws an exception.</summary>
        public bool GetShouldFail { get; set; }

        /// <summary>If set to true, <see cref="SaveSessionAsync"/> throws an exception.</summary>
        public bool SaveShouldFail { get; set; }

        /// <summary>If set to true, <see cref="GetSessionAsync"/> throws <see cref="OperationCanceledException"/>.</summary>
        public bool GetShouldThrowCancelled { get; set; }

        /// <summary>If set to true, <see cref="SaveSessionAsync"/> throws <see cref="OperationCanceledException"/>.</summary>
        public bool SaveShouldThrowCancelled { get; set; }

        /// <summary>Number of times <see cref="GetSessionAsync"/> was called.</summary>
        public int GetCallCount { get; private set; }

        /// <summary>Number of times <see cref="SaveSessionAsync"/> was called.</summary>
        public int SaveCallCount { get; private set; }

        /// <summary>Last session ID passed to <see cref="SaveSessionAsync"/>.</summary>
        public string? LastSavedSessionId { get; private set; }

        /// <summary>Last session JSON passed to <see cref="SaveSessionAsync"/>.</summary>
        public string? LastSavedJson { get; private set; }

        /// <summary>Seeds the store with pre-existing session JSON for the given session ID.</summary>
        public void Seed(string sessionId, string json) => _store[sessionId] = json;

        /// <inheritdoc />
        public Task<string?> GetSessionAsync(string sessionId, CancellationToken ct)
        {
            GetCallCount++;
            if (GetShouldThrowCancelled) throw new OperationCanceledException("Simulated cancellation during GetSession");
            if (GetShouldFail) throw new InvalidOperationException("Simulated GetSession failure");
            _store.TryGetValue(sessionId, out var json);
            return Task.FromResult<string?>(json);
        }

        /// <inheritdoc />
        public Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct)
        {
            SaveCallCount++;
            if (SaveShouldThrowCancelled) throw new OperationCanceledException("Simulated cancellation during SaveSession");
            if (SaveShouldFail) throw new InvalidOperationException("Simulated SaveSession failure");
            _store[sessionId] = sessionJson;
            LastSavedSessionId = sessionId;
            LastSavedJson = sessionJson;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An <see cref="IAgentRunner"/> that tracks <see cref="SetSession"/> / <see cref="GetSession"/>
    /// calls and exposes the session for test assertions.
    /// </summary>
    private sealed class SessionTrackingAgentRunner : IAgentRunner
    {
        public TestResultReport? LastTestReport { get; private set; }
        public WorkerReport? LastWorkerReport { get; private set; }

        private object? _session;

        /// <summary>Session that was passed to <see cref="SetSession"/>.</summary>
        public object? SessionSetTo { get; private set; }

        /// <summary>Whether <see cref="SetSession"/> was called at least once.</summary>
        public bool SetSessionCalled { get; private set; }

        public void ClearTestReport() => LastTestReport = null;
        public void ClearWorkerReport() => LastWorkerReport = null;
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentTaskId(string? taskId) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(WorkerRole role, string agentsMdContent) { }

        public void SetSession(object? session)
        {
            SetSessionCalled = true;
            SessionSetTo = session;
            _session = session;
        }

        public object? GetSession() => _session;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetSessionAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            // Simulate the runner updating the session after execution
            if (_session is AgentSession existingSession)
            {
                // session already set; no-op (in-place update would happen via CodingAgent in real code)
            }
            else
            {
                // Create a new session as SharpCoderRunner would
                _session = AgentSession.Create("runner-created-" + Guid.NewGuid().ToString("N")[..8]);
            }
            return Task.FromResult("Mock response");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Minimal <see cref="IGitOperations"/> that does nothing.</summary>
    private sealed class NoOpGitOperations : IGitOperations
    {
        public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct) => Task.CompletedTask;
        public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct) => Task.CompletedTask;
        public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct) => Task.CompletedTask;
        public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
            => Task.FromResult(new GitChangeSummary { FilesChanged = 1 });
        public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
            => Task.FromResult<string?>("abc123def456789012345678");
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(string workDir, string args, CancellationToken ct)
            => Task.FromResult((0, "", ""));
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => Task.CompletedTask;
    }

    /// <summary>Builds a minimal <see cref="WorkTask"/> with the given session ID.</summary>
    private static WorkTask BuildTask(string sessionId = "") => new()
    {
        TaskId = "task-session-test",
        GoalId = "goal-session-test",
        GoalDescription = "Test goal",
        Prompt = "Test prompt",
        Role = WorkerRole.Coder,
        SessionId = sessionId,
        Repositories =
        [
            new TargetRepository
            {
                Name = "test-repo",
                Url = "https://github.com/test/test.git",
                DefaultBranch = "main"
            }
        ],
        BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feat" }
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When a non-empty <see cref="WorkTask.SessionId"/> is provided and the session client
    /// has a stored session, <see cref="IAgentRunner.SetSession"/> must be called with a
    /// non-null <see cref="AgentSession"/> before the prompt is sent.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithStoredSession_LoadsSessionIntoRunner()
    {
        // Arrange
        var existingSession = AgentSession.Create("stored-session");
        var existingJson = JsonSerializer.Serialize(existingSession, AIJsonUtilities.DefaultOptions);

        var sessionClient = new FakeSessionClient();
        sessionClient.Seed("goal-1:Coder", existingJson);

        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-1:Coder");

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(agentRunner.SetSessionCalled, "SetSession should have been called");
        Assert.NotNull(agentRunner.SessionSetTo);
        Assert.IsType<AgentSession>(agentRunner.SessionSetTo);
        Assert.Equal(1, sessionClient.GetCallCount);
    }

    /// <summary>
    /// After successful execution, the session returned by <see cref="IAgentRunner.GetSession"/>
    /// must be serialized and saved via the session client.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AfterSuccess_SavesSessionToClient()
    {
        // Arrange
        var sessionClient = new FakeSessionClient();
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-2:Coder");

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, sessionClient.SaveCallCount);
        Assert.Equal("goal-2:Coder", sessionClient.LastSavedSessionId);
        Assert.NotNull(sessionClient.LastSavedJson);
        Assert.False(string.IsNullOrEmpty(sessionClient.LastSavedJson));
    }

    /// <summary>
    /// When the session client does not have a stored session for the given ID,
    /// <see cref="IAgentRunner.SetSession"/> must be called with <c>null</c>
    /// (fresh session fallback).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNoStoredSession_SetSessionWithNull()
    {
        // Arrange
        var sessionClient = new FakeSessionClient(); // empty — no stored session
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-3:Coder");

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(agentRunner.SetSessionCalled, "SetSession should have been called");
        Assert.Null(agentRunner.SessionSetTo); // null = start fresh
    }

    /// <summary>
    /// When <see cref="ISessionClient.GetSessionAsync"/> throws, the task must still succeed
    /// and <see cref="IAgentRunner.SetSession"/> must be called with <c>null</c> (fresh session).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenGetSessionFails_FallsBackToFreshSession()
    {
        // Arrange
        var sessionClient = new FakeSessionClient { GetShouldFail = true };
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-4:Coder");

        // Act — must NOT throw
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(agentRunner.SetSessionCalled, "SetSession should still be called (with null)");
        Assert.Null(agentRunner.SessionSetTo);
    }

    /// <summary>
    /// When <see cref="ISessionClient.SaveSessionAsync"/> throws, the task must still complete
    /// successfully — the failure is swallowed and only logged as a warning.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveSessionFails_TaskStillCompletes()
    {
        // Arrange
        var sessionClient = new FakeSessionClient { SaveShouldFail = true };
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-5:Coder");

        // Act — must NOT throw even though save fails
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal(1, sessionClient.SaveCallCount); // save was attempted
    }

    /// <summary>
    /// When <see cref="WorkTask.SessionId"/> is empty, session load/save must not be attempted.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptySessionId_DoesNotCallSessionClient()
    {
        // Arrange
        var sessionClient = new FakeSessionClient();
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask(""); // no session ID

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, sessionClient.GetCallCount);
        Assert.Equal(0, sessionClient.SaveCallCount);
        Assert.False(agentRunner.SetSessionCalled, "SetSession should NOT be called when SessionId is empty");
    }

    /// <summary>
    /// When no session client is provided (<c>null</c>), the executor must operate normally
    /// without attempting any session operations.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNullSessionClient_SkipsSessionOperations()
    {
        // Arrange — no session client
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: null);
        var task = BuildTask("goal-6:Coder");

        // Act
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.False(agentRunner.SetSessionCalled, "SetSession must not be called without a session client");
    }

    /// <summary>
    /// The saved session JSON must be valid and deserializable back to <see cref="AgentSession"/>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SessionRoundTrip_SavedJsonIsDeserializable()
    {
        // Arrange
        var sessionClient = new FakeSessionClient();
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-7:Coder");

        // Act
        await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — saved JSON round-trips correctly
        Assert.NotNull(sessionClient.LastSavedJson);
        var restored = JsonSerializer.Deserialize<AgentSession>(sessionClient.LastSavedJson!, AIJsonUtilities.DefaultOptions);
        Assert.NotNull(restored);
    }

    /// <summary>
    /// When <see cref="ISessionClient.GetSessionAsync"/> throws <see cref="OperationCanceledException"/>,
    /// the exception must propagate instead of being swallowed by the graceful fallback handler.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenGetSessionThrowsCancelled_PropagatesCancellation()
    {
        // Arrange
        var sessionClient = new FakeSessionClient { GetShouldThrowCancelled = true };
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-8:Coder");

        // Act & Assert — OperationCanceledException must propagate
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(task, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// When <see cref="ISessionClient.SaveSessionAsync"/> throws <see cref="OperationCanceledException"/>,
    /// the exception must be swallowed by the best-effort save handler and the task must still complete
    /// successfully — session save is infrastructure and must never fail the goal.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveSessionThrowsCancelled_StillCompletes()
    {
        // Arrange
        var sessionClient = new FakeSessionClient { SaveShouldThrowCancelled = true };
        var agentRunner = new SessionTrackingAgentRunner();
        var executor = new TaskExecutor(agentRunner, gitOperations: new NoOpGitOperations(), sessionClient: sessionClient);
        var task = BuildTask("goal-9:Coder");

        // Act — SaveSessionAsync throws OCE; the best-effort catch swallows it
        var result = await executor.ExecuteAsync(task, TestContext.Current.CancellationToken);

        // Assert — task must complete successfully; session save failure is non-fatal
        Assert.Equal(TaskOutcome.Completed, result.Status);
    }
}
