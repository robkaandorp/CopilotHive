using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;

using Google.Protobuf;

using Microsoft.Extensions.AI;

using System.Reflection;
using System.Threading.Channels;

using DomainTaskMetrics = CopilotHive.Services.TaskMetrics;
using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcWorkerRole = CopilotHive.Shared.Grpc.WorkerRole;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Characterizations of the REAL <c>WorkerService.ProcessMessagesAsync</c> loop's assignment
/// OWNERSHIP SLOT: the retained-assignment state the loop installs on assignment, retains
/// through body completion (never cleared on completion), and clears only on replacement, a
/// matching cancel or loop teardown.
/// <para>
/// These tests reuse the existing loop-invocation doubles — the channel-backed
/// <see cref="ChannelResponseReader"/> response reader (from
/// <c>WorkerStreamTestDoubles.cs</c>, unchanged) and a gated fake runner with an
/// immediate-recording request writer — and gate purely on
/// <see cref="TaskCompletionSource"/>; there are no sleeps and no polling. A local
/// fault-injecting reader covers the reader-fault teardown trigger without touching the
/// frozen shared doubles.
/// </para>
/// <para>
/// Assertions observe ACTUAL handler completion (a counted <c>WorkerReady</c> write) or a
/// subsequent message boundary the loop must consume — message-delivery and prompt-finished
/// signals alone are never sufficient evidence.
/// </para>
/// <para>
/// CLEANUP CONTRACT: every test keeps its started loop(s) and reader(s) in variables
/// reachable by its <c>finally</c>, and the finally releases all runner gates,
/// completes/faults the reader as the scenario requires, and OBSERVES every started loop and
/// producer BEFORE the service's using-disposal runs — even when an assertion fails.
/// </para>
/// </summary>
[Collection("ConsoleOutput")]
public sealed class WorkerServiceAssignmentOwnershipTests
{
    /// <summary>
    /// Generous failsafe bound for awaitable observations. Never an ordering device: tests
    /// order themselves through gates, and this bound only converts a regression (a Ready
    /// that never comes) into a named failure instead of a hung run.
    /// </summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Token cancellation held at the unwind: after prompt entry the loop's token is
    /// cancelled, the body OBSERVES the cancellation, and the loop CANNOT finish — its
    /// teardown is draining the retained body, which the fake holds inside its unwind gate.
    /// Only when the unwind is released can the drain complete and the loop join. The
    /// ownership slot is then empty and the heartbeat state cleared.
    /// </summary>
    [Fact]
    public async Task TokenCancellationWhileBodyDraining_LoopCannotFinishUntilUnwindReleases()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", loopCts.Token);
        try
        {
            // Assign A and park its body inside the prompt.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");

            // Cancel the LOOP token: the reader unwinds, the loop's finally cancels the
            // retained assignment, and the body observes the cancellation.
            await loopCts.CancelAsync();
            await runner.CancelObserved("task-A");

            // The body is parked in its unwind gate, so the teardown drain cannot finish and
            // the loop CANNOT be complete. This is deterministic: the drain awaits the body
            // task, which cannot complete before the gate is released.
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            // Release the unwind: the drain completes, the loop joins.
            runner.ReleaseUnwind();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);

            // Ownership slot empty after loop cleanup and heartbeat state cleared. The body's
            // Ready was written with the (now cancelled) loop token, so no Ready is observable.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(0, requests.ReadyCount);
        }
        finally
        {
            // Teardown even after an assertion failure: RELEASE every gate and end the reader
            // FIRST, then join every original task, so the service is never disposed with live work.
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// EOF held at the unwind: with the body parked inside the prompt, the reader stream is
    /// COMPLETED (EOF), so the loop exits its iteration, cancels and drains the RETAINED,
    /// STILL-RUNNING assignment, and CANNOT finish while the body is held in the unwind gate.
    /// Only when the unwind is released does the drain complete and the loop join. The
    /// ownership slot is then empty and the heartbeat state cleared.
    /// </summary>
    [Fact]
    public async Task EofWhileBodyDraining_LoopCannotFinishUntilUnwindReleases()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // Assign A and park its body inside the prompt: a RETAINED, running assignment.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");

            // EOF: complete the reader stream while the body is still running.
            responses.TryComplete();

            // The loop's finally cancels the retained assignment; the body observes it.
            await runner.CancelObserved("task-A");

            // The body is parked in its unwind gate, so the teardown drain cannot finish and
            // the loop CANNOT be complete — deterministic, since the drain awaits the body.
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            // Release the unwind: the drain completes, the loop joins.
            runner.ReleaseUnwind();
            await loop;

            // Ownership slot empty after loop cleanup and heartbeat state cleared. Exactly ONE
            // Ready: the stream token is still live under EOF, so the drained body's
            // single-flight claim emits its own Ready during unwind (teardown never claims).
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(1, requests.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// Reader fault held at the unwind: with the body parked inside the prompt, the reader
    /// throws the ORIGINAL exception, the loop cancels and drains the RETAINED, STILL-RUNNING
    /// assignment, and CANNOT finish while the body is held in the unwind gate. Once the
    /// unwind is released, the loop propagates the ORIGINAL reader exception identity out of
    /// the join (consistent with the loop's reader-fault behavior), and the ownership slot is
    /// empty with the heartbeat state cleared.
    /// </summary>
    [Fact]
    public async Task ReaderFaultWhileBodyDraining_LoopCannotFinishUntilUnwindReleases()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var original = new InvalidOperationException("reader fault");
        var responses = new FaultingResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // Assign A and park its body inside the prompt: a RETAINED, running assignment.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");

            // Reader fault: the next MoveNext throws the original exception.
            responses.ArmFault(original);

            // The loop's finally cancels the retained assignment; the body observes it.
            await runner.CancelObserved("task-A");

            // The body is parked in its unwind gate, so the teardown drain cannot finish and
            // the loop CANNOT be complete — deterministic, since the drain awaits the body.
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            // Release the unwind: the drain completes, the loop joins and surfaces the fault.
            runner.ReleaseUnwind();
            var propagated = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => loop);
            Assert.Same(original, propagated);

            // Ownership slot empty after loop cleanup and heartbeat state cleared. Exactly ONE
            // Ready: the stream token is still live (only the reader faulted), so the drained
            // body's single-flight claim emits its own Ready during unwind.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(1, requests.ReadyCount);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// A COMPLETED assignment stays retained: a subsequent matching cancel finds it in the
    /// slot and drains it WITHOUT emitting a duplicate Ready (the body already claimed and
    /// wrote its own), and the following idle cancel — the slot now empty — emits exactly one
    /// Ready of its own. Total: exactly two Ready messages.
    /// </summary>
    [Fact]
    public async Task CompletedAssignmentRetained_MatchingCancelNoDuplicateReady_IdleCancelEmitsReady()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // 1. Assign A and let it FINISH normally, writing its own Ready — it stays retained.
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");
            runner.Release("task-A");
            await runner.PromptFinished("task-A");
            await requests.WaitForReadyCountAsync(1);

            // 2. The matching cancel for the still-retained A must not duplicate the Ready.
            responses.Push(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-A", Reason = "matching" },
            });

            // 3. The idle cancel — the slot was cleared by the matching cancel — emits its own.
            responses.Push(new OrchestratorMessage
            {
                Cancel = new CancelTask { TaskId = "task-idle", Reason = "idle" },
            });

            // 4. A boundary message proves both cancels were fully processed.
            responses.Push(new OrchestratorMessage
            {
                ToolResponse = new ToolCallResponse { RequestId = "probe", Success = true, ResultJson = "{}" },
            });
            await responses.Consumed(4);
            await requests.WaitForReadyCountAsync(2);

            responses.TryComplete();
            await loop;

            Assert.Equal(2, requests.ReadyCount);
            Assert.Equal(0, GetSlotOccupancy(service));
        }
        finally
        {
            // Release gates, let the reader end, and OBSERVE the loop before the service's
            // using-disposal runs — even after an assertion failure.
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// The ownership slot is per-INVOCATION state left EMPTY after loop exit: a second
    /// sequential private-loop invocation on the same service instance starts clean, takes the
    /// idle-cancel branch directly, and completes normally. (A test seam only — NOT a
    /// supported RunAsync reconnect contract.)
    /// <para>
    /// Both started loops and both readers are retained in variables reachable by the
    /// test's <c>finally</c>, which releases gates, completes both readers and observes both
    /// loops BEFORE the service's using-disposal runs — even when an assertion fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SecondSequentialLoopInvocation_StartsWithEmptySlotAndCompletes()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        // Hoisted so the finally can release and join EVERY started producer, even after an
        // assertion failure mid-way through either sequential block.
        var firstResponses = new ChannelResponseReader();
        var secondResponses = new ChannelResponseReader();
        var firstLoop = Task.CompletedTask;
        var secondLoop = Task.CompletedTask;

        try
        {
            // First loop: one full assignment cycle, then EOF.
            {
                var requests = new RecordingRequestStream();
                var stream = BuildStream(requests, firstResponses);

                firstLoop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

                firstResponses.Push(Assignment("task-A"));
                await runner.PromptStarted("task-A");
                runner.Release("task-A");
                await runner.PromptFinished("task-A");
                await requests.WaitForReadyCountAsync(1);

                firstResponses.TryComplete();
                await firstLoop;

                Assert.Equal(0, GetSlotOccupancy(service));
            }

            // Second loop on the SAME instance: the slot is empty, so an idle cancel takes the
            // no-assignment branch and emits Ready directly.
            {
                var requests = new RecordingRequestStream();
                var stream = BuildStream(requests, secondResponses);

                secondLoop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

                secondResponses.Push(new OrchestratorMessage
                {
                    Cancel = new CancelTask { TaskId = "task-idle", Reason = "second-loop" },
                });
                secondResponses.Push(new OrchestratorMessage
                {
                    ToolResponse = new ToolCallResponse { RequestId = "probe", Success = true, ResultJson = "{}" },
                });
                await secondResponses.Consumed(2);
                await requests.WaitForReadyCountAsync(1);

                secondResponses.TryComplete();
                await secondLoop;

                Assert.Equal(0, GetSlotOccupancy(service));
            }
        }
        finally
        {
            // Release gates, end both readers, and observe BOTH loops before the service's
            // using-disposal runs — even when an assertion failed inside either block.
            runner.ReleaseAll();
            firstResponses.TryComplete();
            secondResponses.TryComplete();
            await JoinAllForTeardownAsync(service, ("first loop", firstLoop), ("second loop", secondLoop));
        }
    }

    /// <summary>
    /// A ResetSessionAsync failure fails the loop BEFORE any install: the ownership slot stays
    /// empty, the heartbeat state is cleared by the loop's finally, no Ready is written (the
    /// body never started), and the loop task faults with the reset failure.
    /// </summary>
    [Fact]
    public async Task ResetFailure_NoAssignmentInstalledAndHeartbeatStateCleared()
    {
        var runner = new GatedPromptRunner(resetFails: true);
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            // An assignment whose session reset fails — before the CTS, claim and body exist.
            responses.Push(Assignment("task-A"));
            await runner.ResetAttempted();

            // The reset failure propagates out of the message loop; nothing was installed.
            var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => loop);

            // No installed assignment, cleared heartbeat state, and no Ready (nothing ran).
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Equal(0, requests.ReadyCount);
            Assert.IsType<InvalidOperationException>(failure);
        }
        finally
        {
            // Release gates, let the reader end, and OBSERVE the (already faulted) loop before
            // the service's using-disposal runs — even after an assertion failure. The loop
            // has already been awaited in the try, so this join is instantaneous.
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// Runs the real legacy and provisioned executor branches across EVERY domain outcome the
    /// executor can return — Completed, Failed and Cancelled.  The Complete write is held after
    /// entry, proving publication happened before transport completion.  The exact terminal
    /// payload for that outcome (output byte-for-byte, verdict, issues, counts and git summary)
    /// must be present both in the retained domain result and in the byte-for-byte pre-existing
    /// wire mapping.  A successful report and body exit do not clear the owner; only the later
    /// matching-cancel drain does.
    /// <para>
    /// The OUTCOME dimension is what stops a status-conditional publication (for example one that
    /// only retains <see cref="TaskOutcome.Completed"/> results) from surviving: the Failed and
    /// Cancelled cells find an EMPTY holder and fail by name.  Each outcome is produced by the
    /// REAL <c>TaskExecutor</c> chain, never by a hand-built result: Failed comes from an agent
    /// exception, Cancelled from the assignment token being cancelled while the prompt is parked.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, RetainedOutcome.Completed)]
    [InlineData(false, RetainedOutcome.Failed)]
    [InlineData(false, RetainedOutcome.Cancelled)]
    [InlineData(true, RetainedOutcome.Completed)]
    [InlineData(true, RetainedOutcome.Failed)]
    [InlineData(true, RetainedOutcome.Cancelled)]
    public async Task CompleteWriteGated_BothExecutorBranches_RetainFullResultUntilExplicitDrain(
        bool provisioned,
        RetainedOutcome outcome)
    {
        var taskId = $"task-{(provisioned ? "provisioned" : "legacy")}-{outcome}";
        var runner = new RetentionRunner(id => outcome == RetainedOutcome.Failed
            ? throw new RetentionInjectedFailureException(
                new RpcException(new Status(StatusCode.ResourceExhausted, InjectedFailureSecret)))
            : LongOutput(id));
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        using var processRunner = InstallHealthyGit(configRepoDir);
        var service = BuildService(runner, configRepoDir);
        if (provisioned)
            service.TestProvisioner = new ProvisionerHarness(EligibleConfigUrl, "ghp_retention").Provisioner;

        var stream = BuildStream(requests, responses);
        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The body reaches the prompt before Task.Run necessarily returns.  A following
            // CONSUMED message proves the sequential loop finished the assignment handler — and
            // therefore installed the fully-built owner — before any reflection below.
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Drive the REAL executor to the outcome under test.  Cancelled is produced by
            // cancelling the assignment's own token while the prompt is parked on it, so
            // TaskExecutor's requested-cancellation boundary returns a genuine Cancelled result.
            if (outcome == RetainedOutcome.Cancelled)
                await CancelOwnerTokenAsync(service);
            else
                runner.Release(taskId);

            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retainedBeforeWrite = AssertFullRetainedResult(service, taskId, outcome);
            AssertWirePayload(requests.Completes[0].Complete, taskId, outcome);
            Assert.False(GetActiveExecution(service).IsCompleted);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);

            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Successful transport is not an acknowledgement and does not release ownership.
            Assert.Same(retainedBeforeWrite, GetRetainedResult(service));
            var execution = GetActiveExecution(service);
            Assert.False(execution.IsCompleted);
            requests.ReleaseReady(0);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Same(retainedBeforeWrite, GetRetainedResult(service));
            AssertFullResult(retainedBeforeWrite, taskId, outcome);

            responses.Push(MatchingCancel(taskId));
            responses.Push(Probe("after-clear"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// A transport failure (ordinary fault or cancellation) cannot replace the produced result
    /// with a reporting error, for ANY domain outcome.  Each cell runs both executor branches and
    /// both terminations against a Completed, Failed or Cancelled executor result, observes the
    /// full result at the Complete gate, after the injected write termination, and after the body
    /// itself exits, then verifies that execution occurred exactly once before explicitly draining
    /// the owner.
    /// </summary>
    [Theory]
    [InlineData(false, CompleteTermination.Failure, RetainedOutcome.Completed)]
    [InlineData(false, CompleteTermination.Failure, RetainedOutcome.Failed)]
    [InlineData(false, CompleteTermination.Failure, RetainedOutcome.Cancelled)]
    [InlineData(false, CompleteTermination.Cancellation, RetainedOutcome.Completed)]
    [InlineData(false, CompleteTermination.Cancellation, RetainedOutcome.Failed)]
    [InlineData(false, CompleteTermination.Cancellation, RetainedOutcome.Cancelled)]
    [InlineData(true, CompleteTermination.Failure, RetainedOutcome.Completed)]
    [InlineData(true, CompleteTermination.Failure, RetainedOutcome.Failed)]
    [InlineData(true, CompleteTermination.Failure, RetainedOutcome.Cancelled)]
    [InlineData(true, CompleteTermination.Cancellation, RetainedOutcome.Completed)]
    [InlineData(true, CompleteTermination.Cancellation, RetainedOutcome.Failed)]
    [InlineData(true, CompleteTermination.Cancellation, RetainedOutcome.Cancelled)]
    public async Task CompleteWriteTerminates_BothExecutorBranches_ResultSurvivesBodyExitWithoutRetry(
        bool provisioned,
        CompleteTermination termination,
        RetainedOutcome outcome)
    {
        var taskId = $"task-{(provisioned ? "provisioned" : "legacy")}-{termination}-{outcome}";
        var runner = new RetentionRunner(id => outcome == RetainedOutcome.Failed
            ? throw new RetentionInjectedFailureException(
                new RpcException(new Status(StatusCode.ResourceExhausted, InjectedFailureSecret)))
            : LongOutput(id));
        var requests = new RetentionRequestStream(index => index == 0
            ? termination switch
            {
                CompleteTermination.Failure => new InvalidOperationException("injected Complete write failure"),
                CompleteTermination.Cancellation => new OperationCanceledException("injected Complete write cancellation"),
                _ => throw new InvalidOperationException($"Unknown termination: {termination}"),
            }
            : null);
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        using var processRunner = InstallHealthyGit(configRepoDir);
        var service = BuildService(runner, configRepoDir);
        if (provisioned)
            service.TestProvisioner = new ProvisionerHarness(EligibleConfigUrl, "ghp_retention").Provisioner;

        var stream = BuildStream(requests, responses);
        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            if (outcome == RetainedOutcome.Cancelled)
                await CancelOwnerTokenAsync(service);
            else
                runner.Release(taskId);

            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retainedAtGate = AssertFullRetainedResult(service, taskId, outcome);
            AssertWirePayload(requests.Completes[0].Complete, taskId, outcome);
            requests.ReleaseComplete(0);

            // Complete has now faulted/cancelled and the body's existing handler has advanced to
            // Ready.  Ready is held only as a deterministic body-exit barrier, never as an ack.
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(retainedAtGate, GetRetainedResult(service));
            Assert.Single(requests.Completes);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);

            var execution = GetActiveExecution(service);
            Assert.False(execution.IsCompleted);
            requests.ReleaseReady(0);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Same(retainedAtGate, GetRetainedResult(service));
            AssertFullResult(retainedAtGate, taskId, outcome);
            Assert.Single(requests.Completes);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);

            responses.Push(MatchingCancel(taskId));
            responses.Push(Probe("after-clear"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// Provisioning fails inside the assignment body but before TaskExecutor exists.  The owner is
    /// installed and retained through body exit, yet its terminal slot is empty: there is no
    /// fabricated Complete and no executor invocation.  Matching cancel then performs the normal
    /// drain-and-clear transition.
    /// </summary>
    [Fact]
    public async Task ProvisioningFailureBeforeExecution_RetainedHolderStaysEmptyUntilExplicitDrain()
    {
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);
        service.TestProvisioner = new WorkerConfigProvisioner(
            "worker-1",
            (_, _) => Task.FromException<GetWorkerConfigResponse>(
                new InvalidOperationException("injected provisioning failure")),
            _ => null,
            (_, _) => { });

        var stream = BuildStream(requests, responses);
        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        try
        {
            const string taskId = "task-setup-failure";
            responses.Push(ResultAssignment(taskId));
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed-empty"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.NotNull(GetActiveAssignment(service));
            Assert.Null(GetRetainedResult(service));
            Assert.Empty(requests.Completes);
            Assert.Equal(0, runner.ExecutionEntryCount);
            Assert.Equal(0, runner.PromptCount);

            var execution = GetActiveExecution(service);
            requests.ReleaseReady(0);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Null(GetRetainedResult(service));
            Assert.Empty(requests.Completes);
            Assert.Equal(0, runner.ExecutionEntryCount);

            responses.Push(MatchingCancel(taskId));
            responses.Push(Probe("after-empty-clear"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// Replacement is forced through the REAL assignment handler while A is still blocked in its
    /// Ready write. The loop consumes B, but the handler cannot reset the runner, start B's body or
    /// install B's owner until A's ORIGINAL execution has been joined — so A and its result remain
    /// the owner, B has neither started nor been installed, and once A is released B receives a
    /// fresh empty holder that never observes A's result.
    /// <para>
    /// ORDERING PROOF — the removal-proof part. A message merely being consumed is normally not
    /// evidence that its handler ran, so this fixture uses <see cref="InlineDispatchResponseReader"/>
    /// and first proves the loop has a pending read. Completing that read with B resumes production
    /// inline: <c>Push</c> cannot return until B's real handler reaches its first incomplete await —
    /// A's replacement drain in the correct code, or the gated reset in a drain-less/not-awaited
    /// mutant. The pre-release assertions are therefore made only after dispatch. The reset and
    /// prompt-entry captures additionally record whether A's ORIGINAL execution was already complete
    /// at those exact production boundaries. No polling, sleeps, or Task-internal inspection is used.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replacement_DrainsPriorOwnerThenInstallsFreshEmptyResultHolder()
    {
        const string taskA = "task-A-retained";
        const string taskB = "task-B-successor";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new InlineDispatchResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);

        // Hoisted so the finally can join EVERY original task it started, even after a failure, and
        // can release the reset gate that a parked handler may still be waiting on.
        Task? executionA = null;
        Task? executionB = null;
        var bResetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            responses.Push(ResultAssignment(taskA));
            await runner.PromptStarted(taskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(taskA);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("A-installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var resultA = AssertFullRetainedResult(service, taskA, RetainedOutcome.Completed);

            // Capture A's own owner and ORIGINAL body BEFORE replacement begins, so every ordering
            // observation below is about THIS assignment and can never be satisfied by a later one.
            var ownerA = GetActiveAssignment(service);
            executionA = GetActiveExecution(service);

            // ARM THE ORDERING CAPTURES before B can possibly be handled. Each records, at a
            // production-visible instant on B's path, whether A's ORIGINAL execution had already
            // completed. `_resetCount` distinguishes B's reset from A's. The values are what
            // discriminate — no polling, no sleeps, no Task-internals inspection.
            //
            // The reset ALSO parks on a gate the test owns, so the capture is taken at a fixed
            // point and the pre-release observations below cannot race a drain-less handler that
            // rushes ahead: such a handler necessarily reaches the reset (recording `false`) and
            // then waits there, where the test can observe it deterministically.
            var capturedExecutionA = executionA;
            var bResetReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var aJoinedAtBReset = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var aJoinedAtBPromptEntry = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runner.ResetGate = bResetRelease.Task;
            runner.OnResetEntered = _ =>
            {
                if (runner.ResetCount >= 2)
                {
                    aJoinedAtBReset.TrySetResult(capturedExecutionA.IsCompleted);
                    bResetReached.TrySetResult();
                }
            };
            runner.OnPromptEntered = enteredTaskId =>
            {
                if (string.Equals(enteredTaskId, taskB, StringComparison.Ordinal))
                    aJoinedAtBPromptEntry.TrySetResult(capturedExecutionA.IsCompleted);
            };

            // A's body is now parked inside its Ready write: its ORIGINAL execution cannot finish
            // until the test releases that gate.
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The loop is now parked in its next MoveNext. This fixture completes that pending read
            // with inline continuations, so Push(B) cannot return until B's real handler reaches its
            // first incomplete await: the replacement drain (correct) or the reset gate (mutant).
            await responses.WaitForParkedReadCountAsync(3)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Deliver B through the REAL loop while A is still unfinished, followed by a probe the
            // sequential loop can only consume once it has finished handling B.
            responses.Push(ResultAssignment(taskB));
            responses.Push(Probe("B-blocked-probe"));
            await responses.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // A's ORIGINAL body is provably still running, so the handler must still own A: it may
            // not reset the runner, start B, or install B's owner yet.
            Assert.False(executionA.IsCompleted, "A's body must still be unwinding while its drain is parked.");
            Assert.False(runner.HasPromptStarted(taskB), "B must not start while A's original body is still running.");
            Assert.False(responses.Consumed(4).IsCompleted, "The loop must still be parked inside B's handler.");
            Assert.Same(ownerA, GetActiveAssignment(service));
            Assert.Equal(taskA, GetActiveTaskId(service));
            Assert.Same(resultA, GetRetainedResult(service));
            Assert.Equal(1, runner.ResetCount);

            // Release A's Ready write: only now can A's ORIGINAL execution terminate, the drain
            // join, and the handler proceed to B's session reset — where it parks on the gate.
            requests.ReleaseReady(0);

            // THE DETERMINISTIC RENDEZVOUS. BOTH a correct handler and a drain-less one reach B's
            // session reset and park there, so this wait always completes; what DISCRIMINATES them
            // is the value each captured at that instant. A handler that REMOVED the replacement
            // drain reaches the reset while A is still parked in its Ready write and records
            // `false`.
            await bResetReached.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                await aJoinedAtBReset.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken),
                "B's session reset ran before A's original execution was joined — the replacement "
                + "drain did not run or was not awaited.");

            // A's ORIGINAL execution is now genuinely terminal, which is the precondition for the
            // stray-drain check further below.
            await executionA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Let B's handler continue past the reset.
            bResetRelease.TrySetResult();
            await runner.PromptStarted(taskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The same ordering holds at B's own prompt entry.
            Assert.True(
                await aJoinedAtBPromptEntry.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken),
                "B's body started before A's original execution was joined — the replacement drain "
                + "did not run or was not awaited.");

            // A has now drained and B is executing, but B has not produced a result.  A following
            // message boundary proves B's owner was installed before inspecting its fresh holder.
            responses.Push(Probe("B-installed"));
            await responses.Consumed(5).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(taskB, GetActiveTaskId(service));
            Assert.NotSame(ownerA, GetActiveAssignment(service));
            Assert.Null(GetRetainedResult(service));
            Assert.Equal(2, runner.ResetCount);
            Assert.True(executionA.IsCompleted, "A's original execution must be joined before B is installed.");

            // NO STRAY DRAIN MAY STILL BE IN FLIGHT. A's execution is terminal by now, so a
            // replacement drain that was started but NOT awaited would have resumed and cleared the
            // ownership slot — the slot it finds is B's, which it would wrongly empty. The boundary
            // below is the loop's own consumption of another probe: after it, the slot must STILL
            // hold B. This is what makes a fire-and-forget drain observable without polling.
            responses.Push(Probe("no-stray-drain"));
            await responses.Consumed(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(
                taskB,
                GetActiveTaskId(service));
            Assert.Equal(1, GetSlotOccupancy(service));

            runner.Release(taskB);
            await requests.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var resultB = AssertFullRetainedResult(service, taskB, RetainedOutcome.Completed);
            Assert.NotSame(resultA, resultB);
            requests.ReleaseComplete(1);
            await requests.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            executionB = GetActiveExecution(service);
            requests.ReleaseReady(1);
            await executionB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(resultB, GetRetainedResult(service));

            responses.Push(MatchingCancel(taskB));
            responses.Push(Probe("B-cleared"));
            await responses.Consumed(8).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(2, runner.ExecutionEntryCount);
            Assert.Equal(2, runner.PromptCount);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            // RELEASE EVERYTHING FIRST — gates and the reader — so no join below can be blocked
            // by work this cleanup itself still has to unblock. Only then join every original task,
            // each under its own bound, so one live producer cannot skip the remaining joins.
            runner.ReleaseAll();
            requests.ReleaseAll();
            bResetRelease.TrySetResult();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment body A", executionA),
                ("assignment body B", executionB),
                ("loop", loop));
            TryDelete(root);
        }
    }

    // ── Throwing cancellation callback at the assignment teardown ─────────────

    /// <summary>
    /// A THROWING CANCELLATION CALLBACK DURING THE MATCHING-CANCEL DRAIN cannot skip the join.
    /// This test invokes the production matching-cancel ownership transition directly while the
    /// real message loop remains alive and idle. That isolation is intentional: the loop's outer
    /// teardown cannot rescue a broken matching drain, so every observed join, disposal and clear
    /// belongs to <c>DrainRetainedForMatchingCancelAsync</c> itself.
    /// <para>
    /// With the body held behind its unwind gate, the matching drain cannot complete, ownership
    /// remains installed and the source remains undisposed. After release, the ORIGINAL body joins,
    /// the source is disposed, ownership clears, and the exact cancellation-callback evidence is
    /// returned by the matching drain. The connection is still live at that point, proving outer
    /// loop teardown did not perform a second rescue drain.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MatchingCancelWithThrowingCallback_JoinsBodyThenClearsAndSurfacesError()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);
        ArmedCancellationCallback? armed = null;
        MatchingDrainOperation? matchingDrain = null;

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? bodyExecution = null;
        try
        {
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var ownerBeforeCancel = GetActiveAssignment(service);
            var execution = GetActiveExecution(service);
            bodyExecution = execution;
            armed = ArmThrowingCancellationCallback(service);
            var ownerCts = armed.Source;

            // Invoke the ACTUAL production transition used by the matching-cancel handler. The
            // message loop remains parked on its reader, so its outer finally cannot rescue this
            // operation if the cancellation exception escapes before the join.
            matchingDrain = InvokeMatchingCancelDrain(service);
            await runner.CancelObserved("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(armed.CallbackInvoked);
            Assert.True(ownerCts.IsCancellationRequested);
            Assert.False(execution.IsCompleted, "The original body is held behind its unwind gate.");
            Assert.Same(ownerBeforeCancel, GetActiveAssignment(service));
            Assert.Same(ownerCts, GetOwnerCts(service));
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));
            Assert.False(connection.IsRetired, "Outer loop teardown must not be involved in this drain.");

            runner.ReleaseUnwind();

            // A pre-fix matching drain faults here and leaves ownership/source intact. The repaired
            // transition completes normally with the deferred evidence only AFTER joining,
            // disposing and clearing.
            await matchingDrain.Completion.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var surfaced = Assert.IsType<AggregateException>(matchingDrain.DeferredFailure());
            Assert.Contains(armed.CallbackFailure, Flatten(surfaced));

            Assert.True(execution.IsCompleted);
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.Equal(1, armed.InvocationCount);
            Assert.Equal(1, requests.ReadyCount);
            Assert.False(connection.IsRetired, "The matching transition, not outer teardown, performed cleanup.");

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service,
                ("matching-cancel drain", matchingDrain?.Completion),
                ("assignment body", bodyExecution),
                ("loop", loop));
        }
    }

    /// <summary>
    /// The same discipline at LOOP TEARDOWN (EOF with the body still draining): a throwing
    /// cancellation callback cannot skip awaiting the original body, cannot skip the CTS disposal,
    /// and cannot close the connection's access early. The deferred failure surfaces after cleanup
    /// — it does not become a fabricated successful teardown.
    /// </summary>
    [Fact]
    public async Task EofWithThrowingCallback_JoinsBodyThenClearsAndSurfacesError()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);
        ArmedCancellationCallback? armed = null;

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? bodyExecution = null;
        try
        {
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var execution = GetActiveExecution(service);
            bodyExecution = execution;
            armed = ArmThrowingCancellationCallback(service);
            var ownerCts = armed.Source;

            // EOF while the body is still running: the loop's finally cancels the retained
            // assignment. The body's own cancellation-observed signal is the deterministic
            // boundary; no Task internals or polling are used.
            responses.TryComplete();
            await runner.CancelObserved("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Parked in the drain on the ORIGINAL body: nothing cleared, nothing disposed, and the
            // connection's access is still open (retirement follows the drain).
            Assert.True(armed.CallbackInvoked, "The armed callback must have been invoked by the teardown's cancellation request.");
            Assert.False(execution.IsCompleted, "A's body must still be running while its drain is parked.");
            Assert.NotNull(GetActiveAssignment(service));
            Assert.Same(ownerCts, GetOwnerCts(service));
            Assert.Equal("task-A", GetHeartbeatTaskId(service));
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));
            Assert.False(connection.IsRetired, "Access must not be closed before the original body joined.");
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained body is still unwinding.");

            runner.ReleaseUnwind();

            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Contains(armed.CallbackFailure, Flatten(surfaced));

            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.True(ownerCts.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);

            // The teardown cancel is the ONLY cancellation this source ever receives: with the
            // capture fix the loop disposes it here, while a teardown whose failure escaped the
            // cancellation request would leave it live and re-cancel it on the service's Disposal.
            Assert.Equal(1, armed.InvocationCount);
        }
        finally
        {
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service, ("assignment body", bodyExecution), ("loop", loop));
        }
    }

    /// <summary>
    /// ERROR PRECEDENCE AT THE MESSAGE LOOP: a PRIMARY reader fault is preserved when the teardown's
    /// cancellation cleanup ALSO fails. The reader's ORIGINAL exception identity surfaces from the
    /// loop, the join of the retained body still happens, and the secondary cancellation failure is
    /// reported through the EXISTING guarded sanitized log without replacing the real failure.
    /// </summary>
    [Fact]
    public async Task ReaderFaultPrimaryWithThrowingCallback_PreservesReaderFaultAndStillJoins()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var originalFault = new ReaderPrimaryFailureException("reader fault");
        var responses = new FaultingResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        ArmedCancellationCallback? armed = null;

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? bodyExecution = null;
        try
        {
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var execution = GetActiveExecution(service);
            bodyExecution = execution;
            armed = ArmThrowingCancellationCallback(service);
            var ownerCts = armed.Source;

            // The diagnostics run from here on through the EXISTING logger seam.
            Console.SetError(stdErr);

            // The reader faults while the body is still running: a REAL loop primary. The body's
            // cancellation-observed signal confirms teardown cancelled this exact assignment.
            responses.ArmFault(originalFault);
            await runner.CancelObserved("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(armed.CallbackInvoked, "The armed callback must have been invoked by the teardown's cancellation request.");
            Assert.False(execution.IsCompleted, "A's body must still be running while its drain is parked.");

            runner.ReleaseUnwind();

            // The PRIMARY — the reader's own exception instance — is what surfaces.
            var propagated = await Assert.ThrowsAsync<ReaderPrimaryFailureException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(originalFault, propagated);

            // The join and the clear still happened, and the source was disposed.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.True(ownerCts.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);

            // The secondary cancellation failure was REPORTED — classified by type, never by message.
            var diagnostics = stdErr.ToString();
            Assert.Contains("Task cancellation cleanup failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(
                nameof(AssignmentCancellationCallbackException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(ReaderPrimaryFailureException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(armed.CallbackFailure.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service, ("assignment body", bodyExecution), ("loop", loop));
        }
    }

    /// <summary>
    /// A FAILING DIAGNOSTIC cannot prevent cleanup and cannot replace the primary error. This is
    /// the logger-failure cell of the error-precedence rule: with a PRIMARY reader fault AND a
    /// secondary cancellation-callback failure BOTH in play, the guarded sanitized report itself
    /// throws because <c>Console.Error</c> has been replaced by a writer that throws on every
    /// write. The cleanup must still complete — the body joins, the ownership slot clears, the
    /// source is disposed, the connection retires — and the PRIMARY reader fault surfaces with
    /// its ORIGINAL identity, never replaced by the diagnostic failure.
    /// <para>
    /// REMOVAL PROOF. Without the guard around the diagnostic write, the throwing log call inside
    /// <c>PropagateOrReport</c> unwinds the <c>finally</c> and REPLACES the propagating primary:
    /// the surfaced exception would be the injected diagnostic failure (or a wrapper around it),
    /// so <c>Assert.Same</c> on the reader's original instance fails by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReaderFaultPrimaryWithFailingDiagnostics_CleanupStillCompletesAndPrimarySurfaces()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var originalFault = new ReaderPrimaryFailureException("reader fault");
        var responses = new FaultingResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        ArmedCancellationCallback? armed = null;

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? bodyExecution = null;
        try
        {
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A");
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var execution = GetActiveExecution(service);
            bodyExecution = execution;
            armed = ArmThrowingCancellationCallback(service);
            var ownerCts = armed.Source;

            // From here on the diagnostics sink itself is BROKEN: every write throws.
            var throwingWriter = new ThrowingErrorWriter();
            Console.SetError(throwingWriter);

            // The reader faults while the body is still running: a REAL loop primary, and the
            // teardown's cancellation request raises the armed callback failure. The body's own
            // cancellation-observed gate confirms this exact assignment entered its unwind path.
            responses.ArmFault(originalFault);
            await runner.CancelObserved("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(armed.CallbackInvoked, "The armed callback must have been invoked by the teardown's cancellation request.");
            Assert.False(execution.IsCompleted, "A's body must still be running while its drain is parked.");
            Assert.False(loop.IsCompleted, "Cleanup must still be parked on the original body.");

            runner.ReleaseUnwind();

            // The PRIMARY — the reader's own exception instance — is what surfaces, even though
            // the secondary report's sink threw: a failing diagnostic is not an error channel.
            var propagated = await Assert.ThrowsAsync<ReaderPrimaryFailureException>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(originalFault, propagated);

            // The join and the clear still happened, the source was disposed, and retirement ran:
            // the diagnostic failure skipped nothing.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.True(ownerCts.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.Equal(1, armed.InvocationCount);
            Assert.True(throwingWriter.WriteAttempts > 0, "The guarded cleanup diagnostic must be attempted.");
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service, ("assignment body", bodyExecution), ("loop", loop));
        }
    }

    /// <summary>
    /// The non-cancellation body-fault diagnostic inside <c>DrainAssignmentAsync</c> is guarded.
    /// The assignment body faults on its Ready write, then a matching cancel drains that already
    /// faulted ORIGINAL execution while <c>Console.Error</c> throws specifically for the drain
    /// diagnostic. The source must still be disposed and ownership must still clear; a following
    /// probe is consumed by the still-live loop, proving the matching handler completed.
    /// </summary>
    [Fact]
    public async Task MatchingCancel_BodyFaultDiagnosticThrows_StillDisposesSourceAndClearsOwnership()
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var bodyFault = new BodyReadyWriteFailureException("injected body Ready failure");
        requests.FailNextReadyWrite = bodyFault;
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);
        var originalErr = Console.Error;
        var drainDiagnosticFailure = new BodyDiagnosticFailureException("injected drain diagnostic failure");
        var throwingWriter = new MarkerThrowingErrorWriter(
            "Task drain observed a fault", new StringWriter(), drainDiagnosticFailure);
        try
        {
            responses.Push(Assignment("task-A"));
            await runner.PromptStarted("task-A").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var ownerCts = GetOwnerCts(service);
            var execution = GetActiveExecution(service);
            runner.Release("task-A");

            var propagatedBodyFault = await Assert.ThrowsAsync<BodyReadyWriteFailureException>(
                () => execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(bodyFault, propagatedBodyFault);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));

            // Only the DrainAssignmentAsync diagnostic is degraded from this point onward.
            Console.SetError(throwingWriter);
            responses.Push(MatchingCancel("task-A"));
            responses.Push(Probe("after-drain"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(throwingWriter.WriteAttempts > 0, "The guarded drain diagnostic must be attempted.");
            Assert.Null(GetActiveAssignment(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.False(connection.IsRetired, "A guarded diagnostic failure must not terminate the loop.");

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// DEFECT B — A MATCHING CANCEL WHOSE SINGLE READY WRITE FAILS MUST STILL SURFACE THE
    /// CANCELLATION-CALLBACK EVIDENCE.
    /// <para>
    /// The cancel handler only writes Ready when the drained body did NOT claim it. That state is
    /// produced here exactly as production can: the body's provisioning fails, and the sanitized
    /// diagnostic in its generic catch is written to a DEGRADED <c>Console.Error</c> that throws for
    /// that line — so the delegate unwinds through its <c>finally</c> WITHOUT reaching its Ready
    /// claim. The claim is therefore unconsumed (proved by a zero Ready count), and the handler owns
    /// the single Ready attempt.
    /// </para>
    /// <para>
    /// The matching cancel then hits BOTH failures: the assignment's cancellation callback throws,
    /// and the handler's own Ready write fails. The Ready failure is a genuine prior primary, so it
    /// propagates with its ORIGINAL identity — but the deferred callback evidence must NOT be
    /// silently discarded: it is reported through the existing guarded sanitized log.
    /// </para>
    /// <para>
    /// REMOVAL PROOF. With the deferred failure rethrown only AFTER an unguarded Ready write, the
    /// write's exception jumps straight to the loop's catch and the captured callback evidence is
    /// lost entirely — the sanitized report never appears, so the named assertion on it fails.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingCancelWithThrowingCallbackAndFailingReadyWrite_SurfacesReadyFailureAndReportsCallback(
        bool cancelReadyWrite)
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        // A provisioning failure inside the body, BEFORE any executor exists: the body reaches its
        // generic sanitized catch, which is where the degraded diagnostic sink strikes.
        service.TestProvisioner = new WorkerConfigProvisioner(
            "worker-1",
            (_, _) => Task.FromException<GetWorkerConfigResponse>(
                new InvalidOperationException("injected provisioning failure")),
            _ => null,
            (_, _) => { });

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var bodyDiagnosticFailure = new BodyDiagnosticFailureException("injected body diagnostic failure");
        ArmedCancellationCallback? armed = null;

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? bodyExecution = null;
        try
        {
            // The sink throws ONLY for the body's own failure line, so every other sanitized report
            // in this test is still captured and assertable.
            Console.SetError(new MarkerThrowingErrorWriter(
                "Task execution failed", stdErr, bodyDiagnosticFailure));

            responses.Push(ResultAssignment("task-A"));
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Join the ORIGINAL body and pin its exact failure: it faulted on its own diagnostic,
            // so it never reached its Ready claim.
            var execution = GetActiveExecution(service);
            bodyExecution = execution;
            var bodyFault = await Assert.ThrowsAsync<BodyDiagnosticFailureException>(
                () => execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(bodyDiagnosticFailure, bodyFault);

            // NON-VACUITY: inspect the owner's actual Ready claim, not merely the writer count.
            // The body left it untouched, so the matching-cancel handler owns the one attempt below.
            var readyClaim = GetOwnerReadyClaim(service);
            Assert.Equal(0, GetReadyClaimState(readyClaim));
            Assert.Equal(0, requests.ReadyCount);

            // Arm BOTH failures: the assignment's cancellation callback throws, and the handler's
            // own Ready write fails.
            armed = ArmThrowingCancellationCallback(service);
            var ownerCts = armed.Source;
            Exception readyWriteFailure = cancelReadyWrite
                ? new OperationCanceledException("injected Ready write cancellation")
                : new ReadyWritePrimaryException("injected Ready write failure");
            requests.FailNextReadyWrite = readyWriteFailure;

            responses.Push(MatchingCancel("task-A"));

            // The Ready-write failure is the authoritative outcome, with its ORIGINAL identity.
            var propagated = await Record.ExceptionAsync(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(readyWriteFailure, propagated);

            // The handler really did consume the claim and attempt the single Ready write.
            Assert.Equal(1, GetReadyClaimState(readyClaim));
            Assert.Equal(1, requests.ReadyCount);

            // Cleanup completed regardless: ownership cleared, heartbeat state cleaned, the
            // assignment's source cancelled and disposed, and the callback ran exactly once.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.True(ownerCts.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.Equal(1, armed.InvocationCount);

            // THE EVIDENCE SURVIVES: the deferred callback failure was reported in sanitized form
            // (type classification only, never the message) rather than being silently discarded.
            var diagnostics = stdErr.ToString();
            Assert.Contains("Task cancellation cleanup failed", diagnostics, StringComparison.Ordinal);
            Assert.Contains(
                nameof(AssignmentCancellationCallbackException), diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(readyWriteFailure.GetType().Name, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(armed.CallbackFailure.Message, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service, ("assignment body", bodyExecution), ("loop", loop));
        }
    }

    /// <summary>
    /// The MATCHING-CANCEL HANDLER'S NO-PRIOR-PRIMARY BRANCH: when the single Ready write SUCCEEDS
    /// (or was already claimed by the body), a throwing cancellation callback must still be
    /// SURFACED by the REAL handler — never silently swallowed.
    /// <para>
    /// Both reachable shapes of that branch are covered:
    /// <list type="bullet">
    ///   <item><description><see cref="ReadyBranch.HandlerWritesSuccessfully"/> — the body never
    ///   reaches its Ready claim (its provisioning fails and the sanitized diagnostic for that
    ///   failure hits a degraded sink), so the handler owns the one Ready attempt and that write
    ///   SUCCEEDS.</description></item>
    ///   <item><description><see cref="ReadyBranch.AlreadyClaimedByBody"/> — the body completed
    ///   normally and already consumed the claim, so the handler writes NO Ready at
    ///   all.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// REMOVAL PROOF. A handler that propagates/reports the deferred drain failure ONLY when the
    /// Ready write itself failed — and silently returns otherwise — leaves the loop healthy: the
    /// subsequent probe is consumed and EOF ends the loop successfully. Both cells therefore fail
    /// by name on the "must fault" assertion, and neither can be satisfied by the opposite
    /// (failed-Ready) branch, which is covered separately.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ReadyBranch.HandlerWritesSuccessfully)]
    [InlineData(ReadyBranch.AlreadyClaimedByBody)]
    public async Task MatchingCancelWithThrowingCallback_ReadySucceedsOrIsClaimed_StillSurfacesCallbackFailure(
        ReadyBranch branch)
    {
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        if (branch == ReadyBranch.HandlerWritesSuccessfully)
        {
            // A provisioning failure inside the body, BEFORE any executor exists: the body reaches
            // its generic sanitized catch, whose diagnostic the degraded sink below breaks — so the
            // delegate unwinds WITHOUT reaching its Ready claim and the handler owns the one write.
            service.TestProvisioner = new WorkerConfigProvisioner(
                "worker-1",
                (_, _) => Task.FromException<GetWorkerConfigResponse>(
                    new InvalidOperationException("injected provisioning failure")),
                _ => null,
                (_, _) => { });
        }

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);

        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        var bodyDiagnosticFailure = new BodyDiagnosticFailureException("injected body diagnostic failure");
        ArmedCancellationCallback? armed = null;
        Task? bodyExecution = null;
        try
        {
            // The sink breaks ONLY the body's own failure line; every sanitized report asserted
            // below stays observable.
            Console.SetError(new MarkerThrowingErrorWriter(
                "Task execution failed", stdErr, bodyDiagnosticFailure));

            responses.Push(ResultAssignment("task-A"));
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            bodyExecution = GetActiveExecution(service);
            var readyClaim = GetOwnerReadyClaim(service);

            if (branch == ReadyBranch.HandlerWritesSuccessfully)
            {
                // The body faults on its own diagnostic before the claim.
                var bodyFault = await Assert.ThrowsAsync<BodyDiagnosticFailureException>(
                    () => bodyExecution.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
                Assert.Same(bodyDiagnosticFailure, bodyFault);

                // NON-VACUITY: the claim is genuinely unconsumed, so the handler owns the one write.
                Assert.Equal(0, GetReadyClaimState(readyClaim));
                Assert.Equal(0, requests.ReadyCount);
            }
            else
            {
                // The body completes normally and CONSUMES the claim with its own Ready write.
                runner.Release("task-A");
                await bodyExecution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                await requests.WaitForReadyCountAsync(1);

                // NON-VACUITY: the claim is genuinely consumed, so the handler writes no Ready.
                Assert.Equal(1, GetReadyClaimState(readyClaim));
                Assert.Equal(1, requests.ReadyCount);
            }

            var readyCountBeforeCancel = requests.ReadyCount;

            // Arm ONLY the cancellation-callback failure: the Ready write is left to SUCCEED.
            armed = ArmThrowingCancellationCallback(service);
            var ownerCts = armed.Source;
            Assert.Null(requests.FailNextReadyWrite);

            responses.Push(MatchingCancel("task-A"));

            // THE DISCRIMINATOR: with no prior primary the handler must surface the deferred
            // callback failure out of the loop. A silently-returning handler leaves the loop healthy;
            // a failsafe timeout is rejected explicitly and cannot impersonate callback evidence.
            var surfaced = await Assert.ThrowsAnyAsync<Exception>(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsNotType<TimeoutException>(surfaced);
            Assert.Contains(armed.CallbackFailure, Flatten(surfaced));

            // The single-Ready contract is intact for this branch.
            var expectedReadyCount = branch == ReadyBranch.HandlerWritesSuccessfully
                ? readyCountBeforeCancel + 1
                : readyCountBeforeCancel;
            Assert.Equal(expectedReadyCount, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(readyClaim));

            // Cleanup still completed before the failure surfaced.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.True(ownerCts.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.Equal(1, armed.InvocationCount);

            // With NO prior primary the evidence PROPAGATES; it is not downgraded to a report.
            Assert.DoesNotContain(
                "Task cancellation cleanup failed", stdErr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service, ("assignment body", bodyExecution), ("loop", loop));
        }
    }

    /// <summary>
    /// Which reachable shape of the matching-cancel handler's single-Ready step a cell exercises.
    /// </summary>
    public enum ReadyBranch
    {
        /// <summary>The body never claimed Ready, so the handler writes it — successfully.</summary>
        HandlerWritesSuccessfully,

        /// <summary>The body already claimed and wrote Ready, so the handler writes none.</summary>
        AlreadyClaimedByBody,
    }

    /// <summary>
    /// A diagnostic sink that throws for lines containing a MARKER and forwards everything else to
    /// an inner writer. It models a partially degraded <c>Console.Error</c>: the one failure line
    /// this test needs to break is broken, while the sanitized reports under assertion remain
    /// observable.
    /// </summary>
    private sealed class MarkerThrowingErrorWriter(
        string marker,
        System.IO.TextWriter inner,
        Exception failure)
        : System.IO.TextWriter
    {
        private int _writeAttempts;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        internal int WriteAttempts => Volatile.Read(ref _writeAttempts);

        public override void WriteLine(string? value)
        {
            if (value is not null && value.Contains(marker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _writeAttempts);
                throw failure;
            }

            inner.WriteLine(value);
        }

        public override void Write(string? value)
        {
            if (value is not null && value.Contains(marker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _writeAttempts);
                throw failure;
            }

            inner.Write(value);
        }

        public override void Write(char value) => inner.Write(value);
    }

    /// <summary>
    /// A diagnostic sink that throws on EVERY write — modelling a broken or closed
    /// <c>Console.Error</c>. Used to prove the guarded sanitized report cannot skip cleanup or
    /// replace the primary failure: without the production guard the throw from this writer
    /// unwinds the cleanup's finally.
    /// </summary>
    private sealed class ThrowingErrorWriter : System.IO.TextWriter
    {
        private int _writeAttempts;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        internal int WriteAttempts => Volatile.Read(ref _writeAttempts);

        public override void Write(char value)
        {
            Interlocked.Increment(ref _writeAttempts);
            throw new InvalidOperationException("injected diagnostic failure");
        }

        public override void Write(string? value)
        {
            Interlocked.Increment(ref _writeAttempts);
            throw new InvalidOperationException("injected diagnostic failure");
        }

        public override void WriteLine(string? value)
        {
            Interlocked.Increment(ref _writeAttempts);
            throw new InvalidOperationException("injected diagnostic failure");
        }
    }

    // ── Retention assertions and harness ──────────────────────────────────────

    private const string EligibleConfigUrl = "https://github.com/org/config-repo.git";

    /// <summary>
    /// A secret-shaped marker embedded in the injected failure's gRPC status detail. It must
    /// NEVER appear in a retained result or on the wire: <c>SafeExceptionLog.Describe</c> renders
    /// only type names and status codes, so its absence is asserted on the Failed cells.
    /// </summary>
    private const string InjectedFailureSecret = "ghp_quota_secret_marker";

    /// <summary>
    /// The exact sanitized classification <c>SafeExceptionLog.Describe</c> produces for the
    /// injected agent failure, which <c>TaskExecutor</c>'s generic catch embeds in the Failed
    /// result's output and issue. Pinned so the Failed cells assert real payload content rather
    /// than merely a status value.
    /// </summary>
    private const string InjectedFailureDescription =
        "RetentionInjectedFailureException <- RpcException(status=ResourceExhausted)";

    public enum CompleteTermination
    {
        Failure,
        Cancellation,
    }

    /// <summary>
    /// The DOMAIN OUTCOME dimension of the retention matrix. Each value is produced by the REAL
    /// <c>TaskExecutor.ExecuteAsync</c> chain — never by a hand-built <see cref="TaskResult"/> —
    /// so the retained instance is genuinely the executor's own return value:
    /// <list type="bullet">
    ///   <item><description><see cref="Completed"/> — the prompt returns normally.</description></item>
    ///   <item><description><see cref="Failed"/> — the prompt throws, hitting the executor's
    ///   generic sanitized failure boundary.</description></item>
    ///   <item><description><see cref="Cancelled"/> — the ASSIGNMENT's own token is cancelled
    ///   while the prompt is parked, hitting the requested-cancellation boundary.</description></item>
    /// </list>
    /// </summary>
    public enum RetainedOutcome
    {
        Completed,
        Failed,
        Cancelled,
    }

    /// <summary>The agent exception injected to drive a real Failed executor result.</summary>
    private sealed class RetentionInjectedFailureException(Exception inner)
        : InvalidOperationException("injected retention failure", inner);

    private static string LongOutput(string taskId) =>
        $"BEGIN:{taskId}\n" + new string('x', 65_536) + "\nTRAILING-EVIDENCE:\tkept-byte-for-byte  \n\n";

    /// <summary>
    /// Cancels the CURRENT owner's assignment-scoped CTS — the same token the parked prompt is
    /// awaiting — so <c>TaskExecutor</c> reaches its requested-cancellation boundary and returns a
    /// genuine <see cref="TaskOutcome.Cancelled"/> result through the normal reporting path.
    /// </summary>
    private static async Task CancelOwnerTokenAsync(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner to cancel.");
        var cts = (CancellationTokenSource)active.GetType().GetProperty("Cts")!.GetValue(active)!;
        await cts.CancelAsync();
    }

    /// <summary>
    /// The ORIGINAL ASSIGNED model carried by every retention assignment. Nonempty on purpose:
    /// the completion must be self-contained with respect to model provenance, so an executor
    /// that still left <c>Model</c> empty (or that used any other source) is caught by name.
    /// </summary>
    private const string RetentionAssignedModel = "model-retention";

    private static OrchestratorMessage ResultAssignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-retention",
            GoalDescription = "retain complete result",
            Prompt = "produce a complete result",
            Role = GrpcWorkerRole.Tester,
            Model = RetentionAssignedModel,
        },
    };

    /// <summary>
    /// The model EXPECTED on the retained result and on the wire, read from the INPUT ASSIGNMENT
    /// the loop is driven with — never copied from an observed output and never taken from a
    /// mapper-only object.
    /// </summary>
    private static string AssignedModel(string taskId) =>
        ResultAssignment(taskId).Assignment.Model;

    private static OrchestratorMessage Probe(string requestId) => new()
    {
        ToolResponse = new ToolCallResponse { RequestId = requestId, Success = true, ResultJson = "{}" },
    };

    private static OrchestratorMessage MatchingCancel(string taskId) => new()
    {
        Cancel = new CancelTask { TaskId = taskId, Reason = "test drain" },
    };

    /// <summary>
    /// The EXACT terminal result the real executor chain produces for <paramref name="outcome"/>.
    /// Mirrors <c>TaskExecutor</c>'s own composition for each boundary, so the wire-payload
    /// comparison below is a byte-for-byte identity check against the pre-existing mapping.
    /// <para>
    /// The model comes from the INPUT ASSIGNMENT (never from an observed output), so the wire
    /// identity check also proves the producer populated it from the assignment.
    /// </para>
    /// </summary>
    private static TaskResult ExpectedResult(string taskId, RetainedOutcome outcome) =>
        ExpectedOutcome(taskId, outcome) with { Model = AssignedModel(taskId) };

    private static TaskResult ExpectedOutcome(string taskId, RetainedOutcome outcome) => outcome switch
    {
        RetainedOutcome.Completed => new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Completed,
            Output = LongOutput(taskId),
            Metrics = new DomainTaskMetrics
            {
                Verdict = "PASS",
                BuildSuccess = true,
                TotalTests = 987,
                PassedTests = 987,
                FailedTests = 0,
                CoveragePercent = 88.75,
                Issues = ["metrics-evidence-one", "metrics-evidence-two"],
                Summary = "full retained metrics evidence",
            },
            GitStatus = new GitChangeSummary(),
        },
        RetainedOutcome.Failed => new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Failed,
            // The non-Improver evidence accumulator is disabled, so the sanitized diagnostic
            // stands alone — exactly as the executor's generic catch composes it.
            Output = $"Error [{InjectedFailureDescription}]",
            Metrics = new DomainTaskMetrics
            {
                Verdict = "FAIL",
                Issues = [InjectedFailureDescription],
            },
            GitStatus = null,
        },
        RetainedOutcome.Cancelled => new TaskResult
        {
            TaskId = taskId,
            Status = TaskOutcome.Cancelled,
            Output = "Task was cancelled.",
            Metrics = new DomainTaskMetrics { Verdict = "CANCELLED" },
            GitStatus = null,
        },
        _ => throw new InvalidOperationException($"Unknown outcome: {outcome}"),
    };

    private static TaskResult AssertFullRetainedResult(
        WorkerService service, string taskId, RetainedOutcome outcome)
    {
        var result = Assert.IsType<TaskResult>(GetRetainedResult(service));
        AssertFullResult(result, taskId, outcome);
        return result;
    }

    /// <summary>
    /// Asserts FULL payload identity of a retained result for its outcome — every field, not just
    /// the status — so a mutant that retained a different or synthesized result cannot pass.
    /// </summary>
    private static void AssertFullResult(TaskResult actual, string taskId, RetainedOutcome outcome)
    {
        var expected = ExpectedResult(taskId, outcome);

        Assert.Equal(taskId, actual.TaskId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Output, actual.Output);
        Assert.Null(actual.IterationStartSha);
        // The ORIGINAL ASSIGNED model travels with the completion. The expected value is read
        // from the input assignment, so this cannot pass by copying the observed output.
        Assert.Equal(AssignedModel(taskId), actual.Model);

        var metrics = Assert.IsType<DomainTaskMetrics>(actual.Metrics);
        var expectedMetrics = expected.Metrics!;
        Assert.Equal(expectedMetrics.Verdict, metrics.Verdict);
        Assert.Equal(expectedMetrics.BuildSuccess, metrics.BuildSuccess);
        Assert.Equal(expectedMetrics.TotalTests, metrics.TotalTests);
        Assert.Equal(expectedMetrics.PassedTests, metrics.PassedTests);
        Assert.Equal(expectedMetrics.FailedTests, metrics.FailedTests);
        Assert.Equal(expectedMetrics.CoveragePercent, metrics.CoveragePercent);
        Assert.Equal(expectedMetrics.Issues, metrics.Issues);
        Assert.Equal(expectedMetrics.Summary, metrics.Summary);

        switch (outcome)
        {
            case RetainedOutcome.Completed:
                // The full, untruncated agent output survives verbatim, trailing bytes included.
                Assert.EndsWith(
                    "TRAILING-EVIDENCE:\tkept-byte-for-byte  \n\n", actual.Output, StringComparison.Ordinal);
                Assert.Equal(65_536, actual.Output.Count(c => c == 'x'));
                var git = Assert.IsType<GitChangeSummary>(actual.GitStatus);
                Assert.Equal(0, git.FilesChanged);
                Assert.Equal(0, git.Insertions);
                Assert.Equal(0, git.Deletions);
                Assert.False(git.Pushed);
                Assert.Empty(git.ChangedFiles);
                break;

            case RetainedOutcome.Failed:
                // The retained failure carries the SANITIZED classification and never the secret
                // the injected gRPC status detail carried.
                Assert.Contains(InjectedFailureDescription, actual.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(InjectedFailureSecret, actual.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    InjectedFailureSecret, string.Join("\n", metrics.Issues), StringComparison.Ordinal);
                Assert.Null(actual.GitStatus);
                break;

            case RetainedOutcome.Cancelled:
                Assert.Null(actual.GitStatus);
                break;

            default:
                throw new InvalidOperationException($"Unknown outcome: {outcome}");
        }
    }

    private static void AssertWirePayload(TaskComplete actual, string taskId, RetainedOutcome outcome)
    {
        var expected = GrpcMapper.ToGrpc(ExpectedResult(taskId, outcome));
        Assert.Equal(expected.ToByteArray(), actual.ToByteArray());
        Assert.Equal(ExpectedResult(taskId, outcome).Output, actual.Output);

        // The ACTUAL Complete payload carries the assigned model with explicit presence, so a
        // receiver can tell an upgraded sender from a legacy one. Expected comes from the input
        // assignment, never from the observed message.
        Assert.True(actual.HasModel, "The upgraded completion must carry field 7 with presence.");
        Assert.Equal(AssignedModel(taskId), actual.Model);

        if (outcome == RetainedOutcome.Completed)
        {
            Assert.EndsWith(
                "TRAILING-EVIDENCE:\tkept-byte-for-byte  \n\n", actual.Output, StringComparison.Ordinal);
            Assert.Equal("full retained metrics evidence", actual.Metrics.Summary);
            Assert.Equal(987, actual.Metrics.TotalTests);
            Assert.Equal(["metrics-evidence-one", "metrics-evidence-two"], actual.Metrics.Issues);
        }
        else
        {
            Assert.DoesNotContain(InjectedFailureSecret, actual.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Invokes the production matching-cancel ownership transition without delivering a message to
    /// the loop, keeping outer loop teardown out of the observation. The returned accessor is read
    /// only after <see cref="MatchingDrainOperation.Completion"/> terminates.
    /// </summary>
    private static MatchingDrainOperation InvokeMatchingCancelDrain(WorkerService service)
    {
        var operation = typeof(WorkerService).GetMethod(
                "DrainRetainedForMatchingCancelAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, null)!;
        var completion = Assert.IsAssignableFrom<Task>(operation);

        return new MatchingDrainOperation(
            completion,
            () =>
            {
                Assert.True(completion.IsCompletedSuccessfully);
                var result = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
                return (Exception?)result.GetType().GetField("Item2")!.GetValue(result);
            });
    }

    private sealed record MatchingDrainOperation(Task Completion, Func<Exception?> DeferredFailure);

    private static object? GetActiveAssignment(WorkerService service) =>
        typeof(WorkerService).GetField("_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service);

    private static string GetActiveTaskId(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (string)active.GetType().GetProperty("TaskId")!.GetValue(active)!;
    }

    private static Task GetActiveExecution(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task)active.GetType().GetProperty("Execution")!.GetValue(active)!;
    }

    private static TaskResult? GetRetainedResult(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        if (active is null)
            return null;
        var holder = active.GetType().GetProperty("TerminalResult")!.GetValue(active)!;
        return (TaskResult?)holder.GetType().GetProperty("Result")!.GetValue(holder);
    }

    /// <summary>The ACTIVE owner's assignment-scoped cancellation source (observation only).</summary>
    private static CancellationTokenSource GetOwnerCts(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (CancellationTokenSource)active.GetType().GetProperty("Cts")!.GetValue(active)!;
    }

    private static object GetOwnerReadyClaim(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("Ready")!.GetValue(active)!;
    }

    private static int GetReadyClaimState(object readyClaim) =>
        (int)readyClaim.GetType().GetField("_claimed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(readyClaim)!;

    /// <summary>
    /// Flattens an exception (including <see cref="AggregateException"/> wrappers) so an assertion
    /// can locate the ORIGINAL callback evidence inside the runtime's own wrapper without the test
    /// normalizing it away.
    /// </summary>
    private static IReadOnlyList<Exception> Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions]
            : [exception];

    private sealed class AssignmentCancellationCallbackException(string message) : Exception(message);

    private sealed class ReaderPrimaryFailureException(string message) : Exception(message);

    private sealed class ReadyWritePrimaryException(string message) : Exception(message);

    private sealed class BodyReadyWriteFailureException(string message) : Exception(message);

    private sealed class BodyDiagnosticFailureException(string message) : Exception(message);

    /// <summary>
    /// An armed throwing cancellation callback: the ACTUAL assignment-scoped source it was
    /// registered on, the exact exception the callback raises, whether the callback has run, and
    /// the registration handle used for teardown.
    /// </summary>
    private sealed class ArmedCancellationCallback(
        CancellationTokenSource source,
        Exception callbackFailure,
        CancellationTokenRegistration registration)
        : IDisposable
    {
        private int _invoked;

        /// <summary>The ACTUAL owned source the callback was registered on.</summary>
        internal CancellationTokenSource Source { get; } = source;

        /// <summary>The exact exception instance the callback raises.</summary>
        internal Exception CallbackFailure { get; } = callbackFailure;

        /// <summary>Whether the callback has been invoked by a cancellation request.</summary>
        internal bool CallbackInvoked => Volatile.Read(ref _invoked) != 0;

        /// <summary>
        /// How many times the callback ran. The capture fix cancels this source EXACTLY ONCE (the
        /// matching-cancel drain); a teardown that let the failure escape the cancellation request
        /// would leave the source undisposed and re-enter the drain, invoking the callback again —
        /// so this count is a direct, positive discriminator for the fix.
        /// </summary>
        internal int InvocationCount => Volatile.Read(ref _invoked);

        /// <summary>Records one invocation of the callback.</summary>
        internal void MarkInvoked() => Interlocked.Increment(ref _invoked);

        /// <summary>Teardown-only: drops the registration so nothing outlives the test.</summary>
        internal void DisposeRegistration() => registration.Dispose();

        public void Dispose() => registration.Dispose();
    }

    /// <summary>
    /// Arms a THROWING callback on the ACTIVE assignment's own <see cref="CancellationTokenSource"/>
    /// — the very source the production drain cancels — so the drain's cancellation request
    /// provably raises a callback failure.
    /// <para>
    /// The callback records its invocation BEFORE throwing, so the test can prove it really ran
    /// (rather than observing a vacuous, never-invoked registration).
    /// </para>
    /// </summary>
    private static ArmedCancellationCallback ArmThrowingCancellationCallback(
        WorkerService service,
        Exception? failure = null)
    {
        var source = GetOwnerCts(service);
        failure ??= new AssignmentCancellationCallbackException("throwing cancellation callback");
        ArmedCancellationCallback? armed = null;
        var registration = source.Token.Register(() =>
        {
            armed!.MarkInvoked();
            throw failure;
        });

        armed = new ArmedCancellationCallback(source, failure, registration);
        return armed;
    }

    /// <summary>
    /// Drives the real private message loop with an ALREADY-PUBLISHED connection, so a test can
    /// observe the connection's retirement/lifetime state directly.
    /// </summary>
    private static Task InvokeProcessMessagesWith(
        WorkerService service, WorkerConnection connection, CancellationToken ct) =>
        (Task)typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, ct])!;

    private static string CreateRetentionRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "copilothive-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IDisposable InstallHealthyGit(string configRepoDir)
    {
        var launcher = new FakeGitLauncher(tokens =>
        {
            if (TokensMatch(tokens, "rev-parse", "--is-inside-work-tree"))
                return new GitProcessResult(0, "true\n", "");
            if (TokensMatch(tokens, "rev-parse", "--show-toplevel"))
                return new GitProcessResult(0, configRepoDir + "\n", "");
            if (TokensMatch(tokens, "remote", "get-url", "origin"))
                return new GitProcessResult(0, EligibleConfigUrl + "\n", "");
            return new GitProcessResult(0, "", "");
        });
        return WorkerServiceConfigRepoHarness.InstallProcessRunner(launcher);
    }

    private static bool TokensMatch(IReadOnlyList<string> actual, params string[] expectedPrefix)
    {
        if (actual.Count < expectedPrefix.Length)
            return false;
        for (var i = 0; i < expectedPrefix.Length; i++)
        {
            if (!string.Equals(actual[i], expectedPrefix[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Captures every Complete/Ready and gates each occurrence independently.  A Complete failure
    /// is thrown only after the message has entered and the test releases its gate.
    /// </summary>
    private sealed class RetentionRequestStream(Func<int, Exception?>? completeTermination = null)
        : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly List<WorkerMessage> _completes = [];
        private readonly List<WorkerMessage> _readies = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _completeRelease = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyEntered = [];
        private readonly Dictionary<int, TaskCompletionSource> _readyRelease = [];
        private bool _releaseImmediately;

        internal IReadOnlyList<WorkerMessage> Completes
        {
            get { lock (_gate) return [.. _completes]; }
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(
            WorkerMessage message,
            CancellationToken cancellationToken) => WriteCoreAsync(message, cancellationToken);

        private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
        {
            if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Complete)
            {
                TaskCompletionSource entered;
                TaskCompletionSource release;
                int index;
                lock (_gate)
                {
                    index = _completes.Count;
                    _completes.Add(message.Clone());
                    entered = Slot(_completeEntered, index);
                    release = Slot(_completeRelease, index);
                    if (_releaseImmediately) release.TrySetResult();
                }
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                if (completeTermination?.Invoke(index) is { } error)
                    throw error;
                return;
            }

            if (message.PayloadCase == WorkerMessage.PayloadOneofCase.Ready)
            {
                TaskCompletionSource entered;
                TaskCompletionSource release;
                lock (_gate)
                {
                    var index = _readies.Count;
                    _readies.Add(message.Clone());
                    entered = Slot(_readyEntered, index);
                    release = Slot(_readyRelease, index);
                    if (_releaseImmediately) release.TrySetResult();
                }
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        }

        internal Task CompleteEntered(int index)
        {
            lock (_gate)
                return _completes.Count > index ? Task.CompletedTask : Slot(_completeEntered, index).Task;
        }

        internal Task ReadyEntered(int index)
        {
            lock (_gate)
                return _readies.Count > index ? Task.CompletedTask : Slot(_readyEntered, index).Task;
        }

        internal void ReleaseComplete(int index)
        {
            lock (_gate) Slot(_completeRelease, index).TrySetResult();
        }

        internal void ReleaseReady(int index)
        {
            lock (_gate) Slot(_readyRelease, index).TrySetResult();
        }

        internal void ReleaseAll()
        {
            lock (_gate)
            {
                // Teardown mode also releases writes that arrive after this sweep (for example a
                // Ready produced only after a failing assertion releases a parked Complete).
                _releaseImmediately = true;
                foreach (var source in _completeRelease.Values) source.TrySetResult();
                foreach (var source in _readyRelease.Values) source.TrySetResult();
            }
        }

        private static TaskCompletionSource Slot(
            Dictionary<int, TaskCompletionSource> slots,
            int index)
        {
            if (!slots.TryGetValue(index, out var source))
            {
                source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                slots[index] = source;
            }
            return source;
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>A deterministic tester runner exposing TaskExecutor-entry and prompt counts.</summary>
    private sealed class RetentionRunner(Func<string, string> output) : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly HashSet<string> _startedIds = [];
        private string? _taskId;
        private int _promptCount;
        private int _executionEntryCount;
        private int _resetCount;

        internal int PromptCount => Volatile.Read(ref _promptCount);
        internal int ExecutionEntryCount => Volatile.Read(ref _executionEntryCount);
        internal int ResetCount => Volatile.Read(ref _resetCount);

        internal Task PromptStarted(string taskId)
        {
            lock (_gate) return Slot(_started, taskId).Task;
        }

        /// <summary>
        /// Invoked AT prompt entry with the entering task's id, BEFORE the prompt parks. It is the
        /// observation point for ordering evidence that must be captured at the exact instant a
        /// body starts — for example whether a previous assignment's ORIGINAL execution had already
        /// been joined by then.
        /// </summary>
        internal Action<string>? OnPromptEntered { get; set; }

        /// <summary>
        /// Invoked AT session-reset entry with the assignment's model. The assignment handler calls
        /// <c>ResetSessionAsync</c> as its FIRST step after the replacement drain and BEFORE the
        /// body is started or the owner installed, so this is the earliest production-visible point
        /// at which "the drain has returned" can be observed.
        /// </summary>
        internal Action<string?>? OnResetEntered { get; set; }

        /// <summary>
        /// A gate the session reset PARKS on after <see cref="OnResetEntered"/> has run. Setting it
        /// makes the capture at reset entry ORDER-INDEPENDENT: whichever moment the handler reaches
        /// the reset, it records the state AT THAT INSTANT and then waits, so a test can take its
        /// observations and release the gate afterwards without the two racing.
        /// </summary>
        internal Task? ResetGate { get; set; }

        internal bool HasPromptStarted(string taskId)
        {
            lock (_gate) return _startedIds.Contains(taskId);
        }

        internal void Release(string taskId)
        {
            lock (_gate) Slot(_release, taskId).TrySetResult();
        }

        internal void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var source in _release.Values) source.TrySetResult();
            }
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var taskId = _taskId ?? throw new InvalidOperationException("Task ID was not set.");
            Interlocked.Increment(ref _promptCount);

            // The ordering capture runs at ENTRY, before this body can park or be released.
            OnPromptEntered?.Invoke(taskId);

            lock (_gate)
            {
                _startedIds.Add(taskId);
                Slot(_started, taskId).TrySetResult();
            }
            Task release;
            lock (_gate) release = Slot(_release, taskId).Task;
            await release.WaitAsync(ct);
            return output(taskId);
        }

        public TestResultReport? LastTestReport { get; } = new()
        {
            Verdict = CopilotHive.Workers.TaskVerdict.Pass,
            BuildSuccess = true,
            TotalTests = 987,
            PassedTests = 987,
            FailedTests = 0,
            CoveragePercent = 88.75,
            Issues = ["metrics-evidence-one", "metrics-evidence-two"],
            Summary = "full retained metrics evidence",
        };

        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) => Interlocked.Increment(ref _executionEntryCount);
        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task ResetSessionAsync(
            string? model,
            ReasoningEffort? reasoningEffort,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _resetCount);

            // The ordering capture runs at the handler's FIRST post-drain step...
            OnResetEntered?.Invoke(model);

            // ...and then the reset PARKS if a gate was installed, so the capture above is taken at
            // a fixed point the test controls rather than racing the test's own observations.
            if (ResetGate is { } gate)
                await gate.WaitAsync(ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TaskCompletionSource Slot(
            Dictionary<string, TaskCompletionSource> slots,
            string taskId)
        {
            if (!slots.TryGetValue(taskId, out var source))
            {
                source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                slots[taskId] = source;
            }
            return source;
        }
    }

    // ── Existing harness ──────────────────────────────────────────────────────

    private static OrchestratorMessage Assignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-1",
            GoalDescription = "desc",
            Prompt = "prompt",
            Role = GrpcWorkerRole.Coder,
        },
    };

    private static WorkerService BuildService(IAgentRunner runner, string configRepoDir = "/config-repo")
    {
        var service = new WorkerService("http://localhost:9999", "worker-1", ["coder"], configRepoDir);

        var field = typeof(WorkerService).GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(service) is IAgentRunner existing)
            existing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        field.SetValue(service, runner);

        return service;
    }

    private static AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> BuildStream(
        IClientStreamWriter<WorkerMessage> requests, IAsyncStreamReader<OrchestratorMessage> responses)
        => new(requests, responses,
            _ => Task.FromResult(new Metadata()),
            _ => new Status(StatusCode.OK, string.Empty),
            _ => new Metadata(),
            _ => { },
            null!);

    /// <summary>
    /// Drives the real private message loop with a connection carrying the service's
    /// <c>TestProvisioner</c> — a <c>null</c> one keeps the legacy, seam-free executor branch, which
    /// is what these direct-loop fixtures need.
    /// </summary>
    private static Task InvokeProcessMessages(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        var connection = TestConnectionFactory.Attach(service, assignedId, stream, service.TestProvisioner);
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [connection, ct])!;
    }

    /// <summary>Reflects the service-owned ownership slot: 1 when occupied, 0 when empty.</summary>
    private static int GetSlotOccupancy(WorkerService service)
    {
        var slot = typeof(WorkerService).GetField(
            "_activeAssignment", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);
        return slot is null ? 0 : 1;
    }

    /// <summary>Reflects the heartbeat busy state: the current task ID, or null.</summary>
    private static string? GetHeartbeatTaskId(WorkerService service)
        => (string?)typeof(WorkerService).GetField(
            "_currentTaskId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);

    /// <summary>
    /// JOINS EVERY ORIGINAL TASK a test started, each under its OWN bounded wait, and only then
    /// reports whatever went wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test's <c>finally</c> must first release every gate and complete/fault every reader, and
    /// then call this ONCE with every started task. Because each join is attempted independently
    /// and failures are accumulated, one still-live producer can never skip the joins that follow
    /// it — which is what previously let a <c>WorkerService</c> be disposed while other original
    /// work was still running.
    /// </para>
    /// <para>
    /// A STILL-LIVE task is a LOUD, distinct failure (never a silent return): it is reported as a
    /// named teardown failure that identifies the producer. A task that terminated with a fault or
    /// a cancellation is quiescent, which is all teardown requires, so its outcome is swallowed
    /// HERE ONLY — the real outcome is asserted on the test's normal path.
    /// </para>
    /// </remarks>
    /// <param name="service">The service to dispose only after every known producer is terminal.</param>
    /// <param name="producers">
    /// The started tasks, in the order they should be joined. <c>null</c> entries (a producer a
    /// test never started) are skipped.
    /// </param>
    private static async Task JoinAllForTeardownAsync(
        WorkerService service,
        params (string Name, Task? Producer)[] producers)
    {
        List<Exception> failures = [];

        // Capture any assignment body that started before the test reached its explicit local
        // assignment. This closes assertion-failure windows without relying on the loop to be the
        // body's only join owner.
        var active = GetActiveAssignment(service);
        var activeExecution = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
        if (activeExecution is not null
            && !producers.Any(candidate => ReferenceEquals(candidate.Producer, activeExecution)))
        {
            producers = [.. producers, ("active assignment body", activeExecution)];
        }

        foreach (var (name, producer) in producers)
        {
            if (producer is not null)
                await JoinOneAsync(name, producer);
        }

        // The loop may have started an assignment from an already-buffered message after the first
        // snapshot. Re-snapshot after all listed joins were attempted and independently join that
        // late body too; one timeout never prevents this second observation.
        active = GetActiveAssignment(service);
        var lateActiveExecution = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
        if (lateActiveExecution is not null
            && !producers.Any(candidate => ReferenceEquals(candidate.Producer, lateActiveExecution)))
        {
            producers = [.. producers, ("late active assignment body", lateActiveExecution)];
            await JoinOneAsync("late active assignment body", lateActiveExecution);
        }

        // A using declaration would dispose the service while a timed-out original task is still
        // live. Dispose manually only after every known producer is terminal; on timeout the service
        // is intentionally left undisposed and the named failure below is the authoritative result.
        if (producers.All(candidate => candidate.Producer is null || candidate.Producer.IsCompleted))
        {
            try
            {
                service.Dispose();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count == 1)
            throw failures[0];

        if (failures.Count > 1)
            throw new AggregateException("Teardown could not join every original task.", failures);

        async Task JoinOneAsync(string name, Task producer)
        {
            try
            {
                await producer.WaitAsync(Failsafe, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                failures.Add(new Xunit.Sdk.XunitException(
                    $"Teardown failed to join '{name}' within the bounded failsafe; live work remains."));
            }
            catch (Exception) when (producer.IsCompleted)
            {
                // Terminal fault/cancellation: the original task is quiescent.
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }

    /// <summary>
    /// A runner whose <c>SendPromptAsync</c> parks until released and whose session reset can
    /// be made to fail. Gates are TCS-only: no sleeps, no polling. After the assignment token
    /// cancels a parked prompt, the unwind is HELD in a dedicated gate until
    /// <see cref="ReleaseUnwind"/> — that is what lets a test prove the loop's teardown drain
    /// cannot complete while the body is still unwinding.
    /// </summary>
    private sealed class GatedPromptRunner(bool resetFails = false) : IAgentRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _finished = [];
        private readonly Dictionary<string, TaskCompletionSource> _release = [];
        private readonly Dictionary<string, TaskCompletionSource> _cancelObserved = [];
        private readonly TaskCompletionSource _unwindGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resetAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _taskId;

        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string key)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(key, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    map[key] = tcs;
                }
                return tcs;
            }
        }

        public Task PromptStarted(string taskId) => Slot(_started, taskId).Task;
        public Task PromptFinished(string taskId) => Slot(_finished, taskId).Task;
        public void Release(string taskId) => Slot(_release, taskId).TrySetResult();

        /// <summary>Completes when the body has OBSERVED its token as cancelled (pre-unwind).</summary>
        public Task CancelObserved(string taskId) => Slot(_cancelObserved, taskId).Task;

        /// <summary>Releases the held unwind so a cancelled body can finish draining.</summary>
        public void ReleaseUnwind() => _unwindGate.TrySetResult();

        /// <summary>Teardown failsafe: releases every gate a parked producer could hold.</summary>
        public void ReleaseAll()
        {
            ReleaseUnwind();
            lock (_gate)
            {
                foreach (var tcs in _release.Values) tcs.TrySetResult();
            }
            _resetAttempted.TrySetResult();
        }

        public Task ResetAttempted() => _resetAttempted.Task;

        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";
            Slot(_started, id).TrySetResult();
            try
            {
                await Slot(_release, id).Task.WaitAsync(ct);
                return "done";
            }
            catch (OperationCanceledException)
            {
                // The cancellation is OBSERVED; the unwind is HELD until the test releases it,
                // so the loop's drain stays parked here for the duration of the assertion.
                Slot(_cancelObserved, id).TrySetResult();
                await _unwindGate.Task;
                throw;
            }
            finally
            {
                Slot(_finished, id).TrySetResult();
            }
        }

        public TestResultReport? LastTestReport => null;
        public WorkerReport? LastWorkerReport => null;
        public void ClearTestReport() { }
        public void ClearWorkerReport() { }
        public void SetToolBridge(IToolCallBridge? bridge) { }
        public void SetCurrentGoalId(string? goalId) { }
        public void SetTesterReport(string? report) { }
        public void SetCustomAgent(DomainWorkerRole role, string agentsMdContent) { }
        public void SetSession(object? session) { }
        public object? GetSession() => null;
        public void SetMaxContextTokens(int maxTokens) { }
        public int GetContextUsagePercent() => 0;
        public void SetCompactionModel(string? model) { }
        public void SetCompactionMaxTokens(int? maxTokens) { }
        public void SetSubAgentModels(IReadOnlyList<SubAgentModelDto> models) { }
        public void SetConfigProvisioner(Func<string?, CancellationToken, Task>? provisioner) { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task ResetSessionAsync(
            string? model, ReasoningEffort? reasoningEffort, CancellationToken ct = default)
        {
            _resetAttempted.TrySetResult();
            if (resetFails)
                throw new InvalidOperationException("reset failed");
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Counts <c>WorkerReady</c> messages and lets tests await a specific count
    /// deterministically. Implements the cancellable overload explicitly: the default
    /// interface method on <see cref="IAsyncStreamWriter{T}"/> throws for any token that can
    /// be cancelled, and the worker writes Ready with the live stream token.
    /// </summary>
    private sealed class RecordingRequestStream : IClientStreamWriter<WorkerMessage>
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];
        private int _readyCount;
        private Exception? _failNextReadyWrite;

        /// <summary>Snapshot of how many Ready messages were written so far.</summary>
        public int ReadyCount
        {
            get { lock (_gate) return _readyCount; }
        }

        /// <summary>
        /// ONE-SHOT injected failure for the next <c>WorkerReady</c> write, applied AFTER the
        /// message was recorded (so the attempt still counts). It models the single Ready write
        /// failing on the cancel handler's own attempt. Consumed on use.
        /// </summary>
        public Exception? FailNextReadyWrite
        {
            get { lock (_gate) return _failNextReadyWrite; }
            set { lock (_gate) _failNextReadyWrite = value; }
        }

        /// <summary>Completes once at least <paramref name="count"/> Ready messages were written.</summary>
        public Task WaitForReadyCountAsync(int count)
        {
            lock (_gate)
            {
                if (_readyCount >= count) return Task.CompletedTask;
                if (!_waiters.TryGetValue(count, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[count] = tcs;
                }
                return tcs.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }
        }

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WorkerMessage message)
        {
            if (message.PayloadCase != WorkerMessage.PayloadOneofCase.Ready)
                return Task.CompletedTask;

            List<TaskCompletionSource> ready = [];
            List<int> satisfied = [];
            Exception? readyFailure;
            lock (_gate)
            {
                _readyCount++;
                readyFailure = _failNextReadyWrite;
                _failNextReadyWrite = null;
                foreach (var (threshold, tcs) in _waiters)
                {
                    if (_readyCount >= threshold)
                    {
                        ready.Add(tcs);
                        satisfied.Add(threshold);
                    }
                }
                foreach (var threshold in satisfied) _waiters.Remove(threshold);
            }
            foreach (var tcs in ready) tcs.TrySetResult();

            // The ATTEMPT is recorded above before the injected failure applies, so a test can prove
            // the write really was issued by the producer under test.
            return readyFailure is null ? Task.CompletedTask : Task.FromException(readyFailure);
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// A deterministic reader whose pending <c>MoveNext</c> continuation runs inline when
    /// <see cref="Push"/> supplies a message. The replacement-ordering fixture first proves the
    /// loop has a pending read, then pushes B; that call cannot return until B's real handler reaches
    /// its first incomplete await. This gives a handler-dispatch boundary without polling, sleeps,
    /// or inspecting Task internals.
    /// </summary>
    private sealed class InlineDispatchResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly object _gate = new();
        private readonly Queue<OrchestratorMessage> _queued = new();
        private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
        private readonly Dictionary<int, TaskCompletionSource> _parkedReadWaiters = [];
        private TaskCompletionSource<bool>? _pendingRead;
        private bool _completed;
        private int _consumed;
        private int _parkedReads;

        public OrchestratorMessage Current { get; private set; } = null!;

        public void Push(OrchestratorMessage message)
        {
            TaskCompletionSource<bool>? pending;
            List<TaskCompletionSource> consumedReady = [];
            lock (_gate)
            {
                if (_completed)
                    throw new InvalidOperationException("Cannot push after reader completion.");

                pending = _pendingRead;
                if (pending is null)
                {
                    _queued.Enqueue(message);
                    return;
                }

                _pendingRead = null;
                Current = message;
                RecordConsumedLocked(consumedReady);
            }

            foreach (var waiter in consumedReady)
                waiter.TrySetResult();

            // Deliberately NOT RunContinuationsAsynchronously: this is the fixture's dispatch
            // rendezvous. Production resumes inline and runs until its next incomplete await.
            pending.SetResult(true);
        }

        public void TryComplete()
        {
            TaskCompletionSource<bool>? pending;
            lock (_gate)
            {
                _completed = true;
                pending = _pendingRead;
                _pendingRead = null;
            }
            pending?.TrySetResult(false);
        }

        public Task Consumed(int count)
        {
            lock (_gate)
            {
                if (_consumed >= count) return Task.CompletedTask;
                if (!_consumedWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumedWaiters[count] = waiter;
                }
                return waiter.Task;
            }
        }

        public Task WaitForParkedReadCountAsync(int count)
        {
            lock (_gate)
            {
                if (_parkedReads >= count) return Task.CompletedTask;
                if (!_parkedReadWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _parkedReadWaiters[count] = waiter;
                }
                return waiter.Task;
            }
        }

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            List<TaskCompletionSource> consumedReady = [];
            List<TaskCompletionSource> parkedReady = [];
            Task<bool> result;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_queued.TryDequeue(out var queued))
                {
                    Current = queued;
                    RecordConsumedLocked(consumedReady);
                    result = Task.FromResult(true);
                }
                else if (_completed)
                {
                    result = Task.FromResult(false);
                }
                else
                {
                    if (_pendingRead is not null)
                        throw new InvalidOperationException("Only one pending MoveNext is supported.");

                    _pendingRead = new TaskCompletionSource<bool>();
                    _parkedReads++;
                    foreach (var (threshold, waiter) in _parkedReadWaiters)
                    {
                        if (_parkedReads >= threshold)
                            parkedReady.Add(waiter);
                    }
                    result = _pendingRead.Task;
                }
            }

            foreach (var waiter in consumedReady)
                waiter.TrySetResult();
            foreach (var waiter in parkedReady)
                waiter.TrySetResult();
            return result;
        }

        private void RecordConsumedLocked(List<TaskCompletionSource> ready)
        {
            _consumed++;
            foreach (var (threshold, waiter) in _consumedWaiters)
            {
                if (_consumed >= threshold)
                    ready.Add(waiter);
            }
        }
    }

    /// <summary>
    /// A channel-backed reader that behaves like <see cref="ChannelResponseReader"/> until
    /// <see cref="ArmFault"/> is called, then throws the ORIGINAL exception from the next
    /// <c>MoveNext</c> — modelling a reader fault whose identity the loop must propagate.
    /// Local to these tests so the frozen shared doubles in WorkerStreamTestDoubles.cs stay
    /// byte-for-byte unchanged.
    /// </summary>
    private sealed class FaultingResponseReader : IAsyncStreamReader<OrchestratorMessage>
    {
        private readonly Channel<OrchestratorMessage> _channel =
            Channel.CreateUnbounded<OrchestratorMessage>();

        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
        private int _consumed;
        private Exception? _fault;

        public OrchestratorMessage Current { get; private set; } = null!;

        public void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

        public void TryComplete() => _channel.Writer.TryComplete();

        /// <summary>
        /// One-shot: the next <c>MoveNext</c> outcome surfaces <paramref name="fault"/>. Also
        /// completes the channel, so a loop ALREADY parked inside <c>WaitToReadAsync</c> wakes
        /// deterministically (no further message is required) and reaches the fault check.
        /// </summary>
        public void ArmFault(Exception fault)
        {
            lock (_gate) _fault = fault;
            _channel.Writer.TryComplete();
        }

        public Task Consumed(int count)
        {
            lock (_gate)
            {
                if (_consumed >= count) return Task.CompletedTask;
                if (!_consumedWaiters.TryGetValue(count, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _consumedWaiters[count] = waiter;
                }
                return waiter.Task;
            }
        }

        /// <summary>Consumes and throws the armed fault, if any. One-shot.</summary>
        private Exception? TakeFault()
        {
            lock (_gate)
            {
                var fault = _fault;
                _fault = null;
                return fault;
            }
        }

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            // Pre-check: covers a fault armed while the loop was BETWEEN MoveNext calls.
            if (TakeFault() is { } preFault)
                throw preFault;

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                // Post-check: covers a fault armed while THIS call was parked inside
                // WaitToReadAsync — TryComplete woke the park, and the fault must fire
                // instead of a plain EOF.
                if (TakeFault() is { } postFault)
                    throw postFault;

                return false;
            }

            if (!_channel.Reader.TryRead(out var message))
            {
                // The reader signalled data but none was left (a concurrent complete): fall
                // through the fault check so an armed fault still fires rather than EOF.
                if (TakeFault() is { } emptyFault)
                    throw emptyFault;

                return false;
            }

            Current = message;
            List<TaskCompletionSource> ready = [];
            lock (_gate)
            {
                _consumed++;
                foreach (var (threshold, waiter) in _consumedWaiters)
                {
                    if (_consumed >= threshold)
                        ready.Add(waiter);
                }
            }
            foreach (var waiter in ready)
                waiter.TrySetResult();

            return true;
        }
    }
}