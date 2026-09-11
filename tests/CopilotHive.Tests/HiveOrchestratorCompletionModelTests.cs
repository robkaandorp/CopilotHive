using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Grpc.Core;

using Google.Protobuf;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using GrpcTaskComplete = CopilotHive.Shared.Grpc.TaskComplete;
using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Completion MODEL-PROVENANCE selection on the REAL transport path:
/// <c>HiveOrchestratorService.WorkStream</c> → production <c>HandleTaskComplete</c> →
/// <see cref="TaskCompletionNotifier.OnTaskCompleted"/>.
/// <para>
/// The selection rule is driven end to end: <c>TaskComplete.HasModel</c> (proto3 explicit
/// presence) is the ONLY trustworthy signal. A PRESENT value — including an EMPTY or
/// whitespace-only one — is the upgraded sender's ORIGINAL ASSIGNED model and is used
/// VERBATIM; it is never replaced by the volatile active-task queue. An ABSENT field is a
/// legacy sender and falls back to the queued active task's model, or to empty when no active
/// entry exists.
/// </para>
/// <para>
/// These tests call the production handler through the real stream, observe the real
/// notifier's emitted <see cref="TaskResult"/>, and assert the unchanged unrelated completion
/// behaviour (worker bookkeeping and active-task removal). No copied selection expression and
/// no direct <c>ApplyTaskCompletion</c>-only assertions.
/// </para>
/// <para>
/// Every test subscribes a TCS-backed handler to the transport notifier (the dispatcher gets
/// its OWN notifier so it is never double-subscribed), awaits the stream and the notification
/// with BOUNDED waits, and unregisters the handler in a <c>finally</c>. There are no timing
/// sleeps and no live LLM/network dependency.
/// </para>
/// </summary>
public sealed class HiveOrchestratorCompletionModelTests
{
    /// <summary>Bound applied to every await; a hang becomes a named failure, never a stall.</summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    private const string WorkerId = "model-selection-worker";
    private const string QueueModel = "queue-model-x";

    /// <summary>
    /// The model the ACTIVE-TASK QUEUE holds for the completed task, which must be used ONLY
    /// when field 7 is absent.
    /// </summary>
    private static WorkTask QueuedTask(string taskId) => new()
    {
        TaskId = taskId,
        GoalId = "goal-model-selection",
        GoalDescription = "select the completion model",
        Prompt = "do the work",
        Role = DomainWorkerRole.Tester,
        Model = QueueModel,
        Repositories = [],
    };

    // ── The five selection cells, driven through the real stream ──────────────

    /// <summary>
    /// EXPLICIT WIRE MODEL BEATS A DIFFERENT QUEUE MODEL. The upgraded sender's value wins
    /// unconditionally; the queue is not consulted.
    /// </summary>
    [Fact]
    public async Task HandleTaskComplete_PresentWireModel_BeatsDifferentQueueModel()
    {
        var result = await RunAsync(
            taskId: "task-wire-wins",
            queueModel: QueueModel,
            completeModel: "wire-model-y",
            present: true);

        Assert.Equal("wire-model-y", result.Model);
    }

    /// <summary>
    /// A PRESENT EMPTY model is "upgraded sender, assigned model unknown/empty" — it must NOT
    /// fall back to the queue's model.
    /// </summary>
    [Fact]
    public async Task HandleTaskComplete_PresentEmptyModel_DoesNotFallBackToQueue()
    {
        var result = await RunAsync(
            taskId: "task-empty-present",
            queueModel: QueueModel,
            completeModel: "",
            present: true);

        Assert.Equal("", result.Model);
    }

    /// <summary>
    /// A PRESENT WHITESPACE model is likewise never treated as absence and never replaced —
    /// not trimmed, not normalized, not substituted.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("  wire  ")]
    public async Task HandleTaskComplete_PresentWhitespaceModel_IsUsedVerbatim(string wireModel)
    {
        var result = await RunAsync(
            taskId: "task-ws-present",
            queueModel: QueueModel,
            completeModel: wireModel,
            present: true);

        Assert.Equal(wireModel, result.Model);
    }

    /// <summary>
    /// A LEGACY ABSENT field keeps the existing fallback: the queued active task's model.
    /// </summary>
    [Fact]
    public async Task HandleTaskComplete_AbsentLegacyModel_UsesQueuedModel()
    {
        var result = await RunAsync(
            taskId: "task-legacy-fallback",
            queueModel: QueueModel,
            completeModel: null,
            present: false);

        Assert.Equal(QueueModel, result.Model);
    }

    /// <summary>
    /// A PRESENT model survives with NO active queue entry at all — the completion is
    /// self-contained with respect to model provenance.
    /// </summary>
    [Fact]
    public async Task HandleTaskComplete_PresentModelWithMissingQueueEntry_Survives()
    {
        var result = await RunAsync(
            taskId: "task-no-queue-present",
            queueModel: null,
            completeModel: "wire-model-z",
            present: true);

        Assert.Equal("wire-model-z", result.Model);
    }

    /// <summary>
    /// ABSENT field with NO active queue entry yields empty — the legacy sender's unknown
    /// model, never a fabricated default.
    /// </summary>
    [Fact]
    public async Task HandleTaskComplete_AbsentModelWithMissingQueueEntry_YieldsEmpty()
    {
        var result = await RunAsync(
            taskId: "task-no-queue-absent",
            queueModel: null,
            completeModel: null,
            present: false);

        Assert.Equal("", result.Model);
    }

    /// <summary>
    /// UNCHANGED UNRELATED COMPLETION BEHAVIOUR. Alongside the selected model, the emitted
    /// domain result carries every other mapped field, the worker is marked idle with its
    /// current model cleared, the active queue entry is removed, and the notifier fires once.
    /// </summary>
    [Fact]
    public async Task HandleTaskComplete_SelectionLeavesUnrelatedCompletionBehaviourUnchanged()
    {
        var observed = await RunAsync(
            taskId: "task-behaviour",
            queueModel: QueueModel,
            completeModel: "wire-model-b",
            present: true,
            fullPayload: true);

        var result = observed.Result;
        Assert.Equal("task-behaviour", result.TaskId);
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal("completed output", result.Output);
        Assert.Equal("wire-model-b", result.Model);
        Assert.Equal("PASS", result.Metrics!.Verdict);
        Assert.Equal("summary-text", result.Metrics.Summary);
        Assert.Equal(3, result.GitStatus!.FilesChanged);
        Assert.True(result.GitStatus.Pushed);

        // Worker bookkeeping and active-task removal are untouched by the new selection.
        Assert.False(observed.Worker.IsBusy);
        Assert.Null(observed.Worker.CurrentTaskId);
        Assert.Null(observed.Worker.CurrentModel);
        Assert.Null(observed.ActiveTaskAfterCompletion);

        // Exactly ONE notification was emitted for this completion.
        Assert.Equal(1, observed.NotificationCount);
        Assert.Equal(1, observed.DashboardNotifications);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed record Observation(
        TaskResult Result,
        ConnectedWorker Worker,
        WorkTask? ActiveTaskAfterCompletion,
        int NotificationCount,
        int DashboardNotifications)
    {
        /// <summary>The model the production handler projected onto the emitted domain result.</summary>
        public string Model => Result.Model;
    }

    /// <summary>
    /// Drives ONE completion through the REAL <c>WorkStream</c> loop and returns the domain
    /// result the production handler emitted on the real notifier.
    /// </summary>
    /// <param name="taskId">The completing task's identifier.</param>
    /// <param name="queueModel">The model on the ACTIVE-TASK QUEUE entry, or null for none.</param>
    /// <param name="completeModel">The wire model value (ignored when <paramref name="present"/> is false).</param>
    /// <param name="present">Whether field 7 is PRESENT on the wire message.</param>
    /// <param name="fullPayload">Whether to also populate metrics/git status and assert them.</param>
    private static async Task<Observation> RunAsync(
        string taskId,
        string? queueModel,
        string? completeModel,
        bool present,
        bool fullPayload = false)
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var dashboard = new DashboardNotifier();

        // TWO notifiers: the dispatcher subscribes to its own (never fired here), so the
        // transport notifier carries exactly ONE subscriber — the test's TCS handler.
        var transportNotifier = new TaskCompletionNotifier();
        var dispatcherNotifier = new TaskCompletionNotifier();

        var dispatcher = new GoalDispatcher(
            new GoalManager(),
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            dispatcherNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            transportNotifier,
            dispatcher,
            NullLogger<HiveOrchestratorService>.Instance,
            dashboardNotifier: dashboard);

        var worker = pool.RegisterWorker(WorkerId, []);

        // Seed the ACTIVE-TASK QUEUE through the production assignment path when a queue model
        // is requested; otherwise leave the queue without an entry for this task.
        if (queueModel is not null)
        {
            var queued = QueuedTask(taskId) with { Model = queueModel };
            taskQueue.Enqueue(queued);
            var dequeued = taskQueue.TryDequeue(DomainWorkerRole.Unspecified);
            Assert.NotNull(dequeued);
            Assert.Same(queued, dequeued);
            service.ApplyTaskAssignment(worker, dequeued!);
        }

        var captured = new TaskCompletionSource<TaskResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        Func<TaskResult, Task> handler = result =>
        {
            Interlocked.Increment(ref notificationCount);
            captured.TrySetResult(result);
            return Task.CompletedTask;
        };
        transportNotifier.OnTaskCompleted += handler;

        var dashboardNotifications = 0;
        Action dashboardHandler = () => Interlocked.Increment(ref dashboardNotifications);
        dashboard.OnStateChanged += dashboardHandler;

        var complete = new GrpcTaskComplete
        {
            TaskId = taskId,
            Status = CopilotHive.Shared.Grpc.TaskStatus.Completed,
            Output = "completed output",
        };
        if (fullPayload)
        {
            complete.Metrics = new CopilotHive.Shared.Grpc.TaskMetrics
            {
                Verdict = "PASS",
                Summary = "summary-text",
            };
            complete.GitStatus = new GitStatus { FilesChanged = 3, Pushed = true };
        }

        if (present)
        {
            complete.Model = completeModel ?? "";
            Assert.True(complete.HasModel, "A present cell must build a message with explicit presence.");
        }
        else
        {
            Assert.False(complete.HasModel, "A legacy cell must build a message WITHOUT field 7.");
        }

        // Push the message exactly as a worker's bytes would decode on the server: serialize and
        // re-parse, so the presence bit each cell asserts on is the WIRE's, not a local artifact.
        var wireComplete = GrpcTaskComplete.Parser.ParseFrom(complete.ToByteArray());
        Assert.Equal(present, wireComplete.HasModel);

        var reader = new ChannelStreamReader();
        var streamTask = service.WorkStream(reader, new MockStreamWriter(), MockContext());

        try
        {
            // Isolate the COMPLETION's own dashboard notification: the setup above (assignment)
            // already notified once, and ending the stream will notify again on worker removal.
            Interlocked.Exchange(ref dashboardNotifications, 0);

            reader.Push(new WorkerMessage { WorkerId = WorkerId, Complete = wireComplete });

            // The notification is scheduled by the production handler on Task.Run, so awaiting
            // the stream alone would not prove the domain result was emitted.
            var result = await captured.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            // Snapshot the dashboard count BEFORE the stream ends: ending the stream removes the
            // worker, which itself notifies the dashboard. The completion's own notification is
            // issued synchronously before Task.Run, so this snapshot isolates it.
            var dashboardAtCompletion = Volatile.Read(ref dashboardNotifications);

            reader.Complete();
            await streamTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

            return new Observation(
                result,
                worker,
                taskQueue.GetActiveTask(taskId),
                Volatile.Read(ref notificationCount),
                dashboardAtCompletion);
        }
        finally
        {
            transportNotifier.OnTaskCompleted -= handler;
            dashboard.OnStateChanged -= dashboardHandler;
            reader.Complete();
            await ObserveStreamForTeardownAsync(streamTask);
        }
    }

    /// <summary>
    /// Joins the stream on teardown without masking an assertion failure with a stream fault,
    /// and never leaves the stream running past the test.
    /// </summary>
    private static async Task ObserveStreamForTeardownAsync(Task streamTask)
    {
        try
        {
            await streamTask.WaitAsync(BoundedWait, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected when the stream's own token is cancelled during teardown.
        }
        catch (TimeoutException)
        {
            // Bounded: a non-terminating stream is reported by the assertion that needed it.
        }
    }

    private static ServerCallContext MockContext() => new Mock<ServerCallContext>().Object;

    /// <summary>
    /// In-memory <see cref="IAsyncStreamReader{T}"/> backed by an unbounded channel: the test
    /// pushes the completion message and then ends the stream.
    /// </summary>
    private sealed class ChannelStreamReader : IAsyncStreamReader<WorkerMessage>
    {
        private readonly System.Threading.Channels.Channel<WorkerMessage> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<WorkerMessage>();

        public WorkerMessage Current { get; private set; } = new();

        public void Push(WorkerMessage message) => _channel.Writer.TryWrite(message);

        public void Complete() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var message))
                {
                    Current = message;
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class MockStreamWriter : IServerStreamWriter<OrchestratorMessage>
    {
        public WriteOptions? WriteOptions { get; set; }

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(OrchestratorMessage message)
            => Task.CompletedTask;

        Task IAsyncStreamWriter<OrchestratorMessage>.WriteAsync(
            OrchestratorMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
