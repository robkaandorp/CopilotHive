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
        using var service = BuildService(runner);
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
            // Teardown even after an assertion failure: release gates, let the reader end, and
            // OBSERVE the loop before the service's using-disposal runs.
            runner.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner);

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
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner);

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
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner);

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
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner);

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
            await ObserveLoopForTeardownAsync(firstLoop);
            await ObserveLoopForTeardownAsync(secondLoop);
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
        using var service = BuildService(runner);

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
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner, configRepoDir);
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
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner, configRepoDir);
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
            await ObserveLoopForTeardownAsync(loop);
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
        using var service = BuildService(runner, configRepoDir);
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
            await ObserveLoopForTeardownAsync(loop);
            TryDelete(root);
        }
    }

    /// <summary>
    /// Replacement is forced while A is still blocked in its Ready write.  The loop has consumed B
    /// but cannot reset or install it until A's body drains, so A and its result remain the owner.
    /// Once released, B receives a fresh empty holder; it never observes A's retained result.
    /// <para>
    /// ORDERING PROOF.  A message merely being CONSUMED is not evidence that the assignment
    /// handler ran: <c>ChannelResponseReader.MoveNext</c> signals <c>Consumed</c> before
    /// <c>ProcessMessagesAsync</c> dispatches on the payload, so asserting on A right after
    /// <c>Consumed(3)</c> could observe A simply because replacement had not started yet — and a
    /// clear-before-drain implementation would survive.  This test therefore awaits a POSITIVE
    /// drain-entry signal (<see cref="ObserveOwnerDrainEntry"/>): the replacement handler has
    /// provably entered <c>DrainAssignmentAsync</c> and is parked awaiting A's still-blocked body.
    /// Only THEN are A's ownership and retained result asserted, so a clear performed before that
    /// drain leaves an empty slot and fails the test by name.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replacement_DrainsPriorOwnerThenInstallsFreshEmptyResultHolder()
    {
        const string taskA = "task-A-retained";
        const string taskB = "task-B-successor";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        using var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var loop = InvokeProcessMessages(service, stream, "worker-1", TestContext.Current.CancellationToken);
        using var drainObserverCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        DrainEntryObservation? drainObservation = null;
        try
        {
            responses.Push(ResultAssignment(taskA));
            await runner.PromptStarted(taskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(taskA);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("A-installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var resultA = AssertFullRetainedResult(service, taskA, RetainedOutcome.Completed);

            // Capture A's own owner and body BEFORE replacement begins.  The drain-entry probe
            // below watches THIS task, so it can never be satisfied by some later assignment.
            var ownerA = GetActiveAssignment(service);
            var executionA = GetActiveExecution(service);

            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Arm the positive observer BEFORE B is delivered. Its stable TCS is signalled only
            // after A's task acquires a continuation, which is the direct effect of the replacement
            // handler entering `await assignment.Execution`. The producer is retained for finally.
            drainObservation = ObserveOwnerDrainEntry(executionA, drainObserverCts.Token);

            // B is delivered while A's execution cannot finish: A's body is parked inside its
            // Ready write, so A's task can only complete once the test releases that gate.
            responses.Push(ResultAssignment(taskB));

            // POSITIVE DRAIN-ENTRY SYNCHRONIZATION.  Await the pre-created TCS until the replacement
            // handler has actually entered the drain and is awaiting A's body — not merely until
            // B's message was pulled off the channel. A clear-before-drain implementation has
            // already emptied the slot by the time this gate opens.
            await drainObservation.Entered.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The drain is in progress and A's body is provably still running, so a correct
            // implementation MUST still own A and its retained result here.
            Assert.False(executionA.IsCompleted, "A's body must still be unwinding while its drain is parked.");
            Assert.Same(ownerA, GetActiveAssignment(service));
            Assert.Equal(taskA, GetActiveTaskId(service));
            Assert.Same(resultA, GetRetainedResult(service));
            Assert.Equal(1, runner.ResetCount);
            Assert.False(runner.HasPromptStarted(taskB));

            requests.ReleaseReady(0);
            await runner.PromptStarted(taskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // A has now drained and B is executing, but B has not produced a result.  A following
            // message boundary proves B's owner was installed before inspecting its fresh holder.
            responses.Push(Probe("B-installed"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(taskB, GetActiveTaskId(service));
            Assert.NotSame(ownerA, GetActiveAssignment(service));
            Assert.Null(GetRetainedResult(service));
            Assert.Equal(2, runner.ResetCount);

            runner.Release(taskB);
            await requests.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var resultB = AssertFullRetainedResult(service, taskB, RetainedOutcome.Completed);
            Assert.NotSame(resultA, resultB);
            requests.ReleaseComplete(1);
            await requests.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var executionB = GetActiveExecution(service);
            requests.ReleaseReady(1);
            await executionB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(resultB, GetRetainedResult(service));

            responses.Push(MatchingCancel(taskB));
            responses.Push(Probe("B-cleared"));
            await responses.Consumed(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(2, runner.ExecutionEntryCount);
            Assert.Equal(2, runner.PromptCount);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            await drainObserverCts.CancelAsync();
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await ObserveLoopForTeardownAsync(loop);
            if (drainObservation is not null)
                await ObserveLoopForTeardownAsync(drainObservation.Producer);
            TryDelete(root);
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

    /// <summary>Stable positive signal plus the observer producer that owns that signal.</summary>
    private sealed record DrainEntryObservation(Task Entered, Task Producer);

    /// <summary>
    /// Arms a POSITIVE TCS DRAIN-ENTRY GATE for the replacement-ordering proof.
    /// <para>
    /// The production drain is <c>await assignment.Execution</c>. Entering that await REGISTERS a
    /// continuation on the still-incomplete body task, so the body's continuation slot flipping
    /// from empty to non-empty is direct evidence that the replacement handler reached the drain
    /// and parked there — evidence a message-consumed signal cannot give, because the reader
    /// signals before handler dispatch. The observer is armed while that slot is still empty and
    /// completes <see cref="DrainEntryObservation.Entered"/> only after the transition.
    /// </para>
    /// <para>
    /// There are no timing sleeps: the observer yields cooperatively until direct state evidence
    /// appears. Its producer has a stable identity returned to the caller, is cancellation-aware,
    /// and is always joined by the test's <c>finally</c>. The gate's bounded WaitAsync remains only
    /// a hang failsafe.
    /// </para>
    /// </summary>
    private static DrainEntryObservation ObserveOwnerDrainEntry(Task execution, CancellationToken ct)
    {
        var field = typeof(Task).GetField("m_continuationObject", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "Task.m_continuationObject was not found: drain entry cannot be observed on this "
                + "runtime, so replacement ordering cannot be proven.");

        if (field.GetValue(execution) is not null)
            throw new Xunit.Sdk.XunitException(
                "A's continuation slot was already occupied before replacement was delivered; "
                + "the drain-entry gate would be vacuous.");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = Task.Run(async () =>
        {
            try
            {
                while (field.GetValue(execution) is null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (execution.IsCompleted)
                    {
                        throw new Xunit.Sdk.XunitException(
                            "A's body completed before replacement drain entry was observed; "
                            + "the blocking precondition was lost.");
                    }

                    await Task.Yield();
                }

                entered.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                entered.TrySetCanceled(ct);
                throw;
            }
            catch (Exception ex)
            {
                entered.TrySetException(ex);
                throw;
            }
        }, CancellationToken.None);

        return new DrainEntryObservation(entered.Task, producer);
    }

    private static OrchestratorMessage ResultAssignment(string taskId) => new()
    {
        Assignment = new TaskAssignment
        {
            TaskId = taskId,
            GoalId = "goal-retention",
            GoalDescription = "retain complete result",
            Prompt = "produce a complete result",
            Role = GrpcWorkerRole.Tester,
            Model = "model-retention",
        },
    };

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
    /// </summary>
    private static TaskResult ExpectedResult(string taskId, RetainedOutcome outcome) => outcome switch
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
        Assert.Equal(string.Empty, actual.Model);

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
        public Task ResetSessionAsync(
            string? model,
            ReasoningEffort? reasoningEffort,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _resetCount);
            return Task.CompletedTask;
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

        typeof(WorkerService).GetField("_assignedId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, "worker-1");

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

    private static Task InvokeProcessMessages(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        string assignedId,
        CancellationToken ct)
    {
        var method = typeof(WorkerService).GetMethod(
            "ProcessMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [stream, assignedId, ct])!;
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
    /// Teardown-only observation of a started loop: settles it WITHOUT propagating its
    /// outcome, so a faulting loop (cancelled teardown, reader fault, reset failure) can
    /// never mask the assertion that failed inside the test body. The loop's actual outcome
    /// is always asserted in the try body on the normal path; this join exists purely to
    /// guarantee every producer is quiescent before the service's using-disposal runs.
    /// </summary>
    private static async Task ObserveLoopForTeardownAsync(Task loop)
    {
        try
        {
            await loop;
        }
        catch
        {
            // Expected on a failure path: the loop faults with OCE (cancelled teardown) or
            // the scenario's reader fault. Swallowed HERE ONLY, never on the assert path.
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

        /// <summary>Snapshot of how many Ready messages were written so far.</summary>
        public int ReadyCount
        {
            get { lock (_gate) return _readyCount; }
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
            lock (_gate)
            {
                _readyCount++;
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
            return Task.CompletedTask;
        }

        Task IAsyncStreamWriter<WorkerMessage>.WriteAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }

        public Task CompleteAsync() => Task.CompletedTask;
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