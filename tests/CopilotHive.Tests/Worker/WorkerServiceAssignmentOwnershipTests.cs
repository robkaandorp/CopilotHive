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
    /// Bound on teardown's drain-to-fixpoint loop. Each pass joins every newly admitted assignment
    /// execution; a fixture can only create a handful, so exceeding this means the runner kept
    /// admitting new work after the recording was sealed, which is reported as a named failure
    /// rather than looped on forever.
    /// </summary>
    private const int MaxTeardownDrainPasses = 8;

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
    /// wire mapping.  A successful report and reporting exit do not clear the owner; only the later
    /// matching-cancel drain does.
    /// <para>
    /// THE EXECUTION/REPORTING SPLIT is observed at both gates. At the Complete gate the EXECUTION
    /// task is already TERMINAL while REPORTING is held by the transport write; at the Ready gate
    /// only REPORTING is incomplete. Completing reporting alone still leaves the owner, both
    /// original tasks and the exact retained result in place — a successful write is not an
    /// acknowledgement.
    /// </para>
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

            // THE SPLIT, OBSERVED AT THE COMPLETE GATE. The held transport write keeps only the
            // CONNECTION-BOUND REPORTING task alive; the EXECUTION task is already TERMINAL.
            Assert.True(
                GetActiveExecution(service).IsCompleted,
                "Execution must be terminal once its result is retained — a held Complete write may not keep it running.");
            Assert.False(
                GetActiveReporting(service).IsCompleted,
                "Reporting must still be held inside the gated Complete write.");
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);

            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Successful transport is not an acknowledgement and does not release ownership.
            Assert.Same(retainedBeforeWrite, GetRetainedResult(service));
            var execution = GetActiveExecution(service);
            var reporting = GetActiveReporting(service);

            // THE SEPARATELY OWNED READINESS WRITE — the ACTUAL task the response loop started from
            // the report's published eligibility, and the one every later transition joins.
            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained the assignment's readiness write.");

            // REPORTING HAS TERMINATED INDEPENDENTLY: only the readiness WRITE is still held, and
            // it is a task of its OWN — never the reporting task.
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(execution.IsCompleted, "Execution must stay terminal while Ready is held.");
            Assert.True(
                reporting.IsCompleted,
                "Reporting must terminate independently of the readiness write.");
            Assert.NotSame(reporting, readinessWrite);
            Assert.False(
                readinessWrite.IsCompleted,
                "The separately owned readiness write must still be held inside its gated write.");
            requests.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(readinessWrite.IsCompletedSuccessfully);

            // Completing BOTH the report and its readiness write neither clears ownership nor
            // implies acknowledgement: the owner, BOTH of its original tasks and the EXACT retained
            // result are all still held.
            Assert.NotNull(GetActiveAssignment(service));
            Assert.Same(execution, GetActiveExecution(service));
            Assert.Same(reporting, GetActiveReporting(service));
            Assert.Same(retainedBeforeWrite, GetRetainedResult(service));
            AssertFullResult(retainedBeforeWrite, taskId, outcome);
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
    /// A transport failure (ordinary fault or cancellation) cannot replace the produced result
    /// with a reporting error, for ANY domain outcome.  Each cell runs both executor branches and
    /// both terminations against a Completed, Failed or Cancelled executor result, observes the
    /// full result at the Complete gate, after the injected write termination, and after the
    /// reporting task itself exits, then verifies that execution occurred exactly once before
    /// explicitly draining the owner.
    /// <para>
    /// A FAILED OR CANCELLED COMPLETE IS A REPORTING FAULT ONLY. The execution task is already
    /// terminal at the gate and stays successfully completed, the reporting task also completes
    /// successfully (no retry, exactly one Complete attempt), and the retained result is the
    /// IDENTICAL instance throughout.
    /// </para>
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

            // The EXECUTION task already produced its result and terminated; only the
            // CONNECTION-BOUND REPORTING is held by the write that is about to fail/cancel.
            var execution = GetActiveExecution(service);
            var reporting = GetActiveReporting(service);
            Assert.True(execution.IsCompleted, "Execution must be terminal before the transport write terminates.");
            Assert.False(reporting.IsCompleted, "Reporting must be held inside the gated Complete write.");

            requests.ReleaseComplete(0);

            // Complete has now faulted/cancelled. REPORTING TERMINATES INDEPENDENTLY of readiness:
            // it publishes the ordinary-Ready eligibility and exits, and the RESPONSE LOOP starts
            // the ACTUAL readiness write — a task of its OWN — which is held here only as a
            // deterministic barrier, never as an ack.
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(retainedAtGate, GetRetainedResult(service));
            Assert.Single(requests.Completes);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);

            Assert.True(execution.IsCompleted, "A failed Complete write must not revive or re-run execution.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the readiness write — a failed/cancelled "
                + "Complete write leaves its producer OBSERVED, never re-raised.");

            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained the assignment's readiness write.");
            Assert.NotSame(reporting, readinessWrite);
            requests.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(readinessWrite.IsCompletedSuccessfully);

            // THE PRODUCER SUCCEEDED: a transport failure is a REPORTING fault only. Both original
            // tasks completed successfully, with the IDENTICAL retained result and no retry.
            Assert.True(execution.IsCompletedSuccessfully, "A transport failure must not fault the execution task.");
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
    /// Provisioning fails inside the assignment's EXECUTION task but before TaskExecutor exists.
    /// The owner is installed and retained through reporting exit, yet its terminal slot is empty:
    /// there is no fabricated Complete and no executor invocation.  Matching cancel then performs
    /// the normal drain-and-clear transition, joining BOTH original tasks.
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

            // The setup failure ended EXECUTION normally (the body's own handler swallowed it), so
            // the report PUBLISHED the ordinary-Ready eligibility and TERMINATED; the RESPONSE LOOP
            // started the ACTUAL, separately owned readiness write, which is what is held here.
            var execution = GetActiveExecution(service);
            var reporting = GetActiveReporting(service);
            Assert.True(execution.IsCompleted, "A setup failure terminates execution before any Ready write.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the readiness write.");

            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained the assignment's readiness write.");
            Assert.NotSame(reporting, readinessWrite);

            requests.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(readinessWrite.IsCompletedSuccessfully);

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
    /// HELD REPORT AT EOF — the regression this split must not introduce. With the assignment's
    /// EXECUTION already terminal and its CONNECTION-BOUND REPORTING held inside a gated Complete
    /// write, the reader reaches EOF: the loop CANNOT finish and CANNOT retire the connection while
    /// that report is still outstanding, because its teardown drain joins BOTH original tasks.
    /// Releasing the gates lets the report finish, the drain settle and join the readiness write,
    /// ownership clear and the connection retire.
    /// <para>
    /// REMOVAL PROOF — deterministic, not schedule-dependent. A teardown that joined only the
    /// execution would find it already terminal and run straight through, RETIRING the connection
    /// while the report is still parked in its Complete write. The report's published eligibility
    /// could then never produce a readiness write that ENTERS the writer at all, so the awaited
    /// <c>ReadyEntered(0)</c> below never completes and the single-Ready assertion fails by name.
    /// That consequence is caused by production ordering, not by test scheduling. The SAME
    /// discriminator covers the readiness-write join: a drain that skipped it would retire the
    /// connection while the write is still in flight.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EofWhileReportHeld_LoopCannotFinishOrRetireUntilReportingReleases()
    {
        const string taskId = "task-held-report";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? execution = null;
        Task? reporting = null;
        var teardownEnteredRegistration = default(CancellationTokenRegistration);
        try
        {
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // A BENIGN rendezvous on the owner's OWN source: the teardown drain's cancellation
            // request is the first thing it does, so this fires once teardown has entered the
            // drain. It changes no outcome — it only records.
            var teardownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            teardownEnteredRegistration = GetOwnerCts(service).Token.Register(
                () => teardownEntered.TrySetResult());

            // Let the executor finish; the report then parks inside the gated Complete write.
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retained = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);

            // EOF while the REPORT — and only the report — is still outstanding.
            responses.TryComplete();
            await teardownEntered.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // The teardown drain is parked on the reporting join, so nothing may be finished,
            // cleared or retired. The report cannot complete before its gate opens.
            Assert.True(execution.IsCompleted, "Execution is terminal; only the report is held.");
            Assert.False(reporting.IsCompleted, "The report must still be held inside its Complete write.");
            Assert.False(loop.IsCompleted, "The loop must not finish while the retained report is outstanding.");
            Assert.NotNull(GetActiveAssignment(service));
            Assert.False(connection.IsRetired, "Retirement must follow the drain of BOTH original tasks.");
            Assert.Same(retained, GetRetainedResult(service));

            // THE DETERMINISTIC DISCRIMINATOR. Release the Complete write: the report PUBLISHES the
            // ordinary-Ready eligibility and terminates, and the drain's settlement then starts the
            // assignment's single readiness write — which can only ENTER the writer while the
            // connection is still usable, i.e. only if teardown really is waiting on this assignment.
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "Teardown must settle and start the assignment's readiness write.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "The report must terminate independently of the readiness write.");
            Assert.NotSame(reporting, readinessWrite);

            requests.ReleaseReady(0);

            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
        }
        finally
        {
            teardownEnteredRegistration.Dispose();
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// A matching cancel arriving while execution is terminal and the assignment's SEPARATELY OWNED
    /// readiness write is parked must join the report, the readiness write and both original tasks
    /// before clearing ownership.
    /// <para>
    /// THE CANCELLATION PHASE IS COMPLETED FIRST, BY THE TEST. Before invoking the transition, the
    /// fixture calls <c>await ownerCts.CancelAsync()</c> and awaits it to completion, so every
    /// registered callback has already run. That matters because
    /// <c>DrainAssignmentAsync</c>'s first step is <c>CaptureCancellationFailureAsync</c>, whose
    /// <c>CancelAsync</c> dispatches callbacks ASYNCHRONOUSLY: signalling from inside a callback
    /// proves only that cancellation STARTED, not that the drain reached any join, which left a
    /// legal schedule where a join-less mutant was still inside cancellation while the test took
    /// its in-flight observations.
    /// </para>
    /// <para>
    /// WITH THE SOURCE ALREADY CANCELLED that ambiguity disappears. <c>CancelAsync</c> on an
    /// already-cancelled source completes synchronously, the execution join is likewise already
    /// complete, and — because the REPORT now terminates independently of readiness — so is the
    /// reporting join. The transition therefore runs SYNCHRONOUSLY through its cancellation phase
    /// and all three preceding steps and can only return here by reaching the HELD readiness-write
    /// join, its first genuinely incomplete await:
    /// <list type="bullet">
    ///   <item><description>CORRECT code does exactly that, returning an INCOMPLETE task with the
    ///   owner, the retained result and the undisposed CTS all still installed.</description></item>
    ///   <item><description>A version with the readiness-write join REMOVED has no incomplete await
    ///   left: it disposes the CTS, clears the slot and completes BEFORE
    ///   <c>InvokeMatchingCancelDrain</c> even returns. Every in-flight assertion below then fails
    ///   by name, on every schedule.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The release-side proof is retained and remains independent: a test-owned source is signalled
    /// from the readiness WRITE's own callback, on the caller's own call stack, and only when the
    /// exact original owner and retained result are STILL installed at that last instant where the
    /// write is provably alive. A mutant that cleared ownership early can never produce that
    /// signal; a no-op callback or a post-hoc count could not satisfy it either.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MatchingCancelWhileReportHeldAtReadyGate_DrainJoinsReportingBeforeClear()
    {
        const string taskId = "task-cancel-held-report";
        var runner = new RetentionRunner(LongOutput);

        // SIGNALLED ONLY ON THE CORRECT PATH. The writer invokes this callback on the calling task's
        // own stack after the readiness write's gate opens but before that write returns. The
        // callback signals only if the matching-cancel transition STILL owns the exact original
        // assignment and result at that last instant where the readiness write is provably alive.
        // Because the private transition below executes synchronously until its first incomplete
        // await, a mutant that removes the readiness-write join has already cleared ownership before
        // this gate is released and can never signal this source.
        var ownerRetainedAtReportRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        object? expectedOwner = null;
        TaskResult? expectedResult = null;
        WorkerService? serviceRef = null;
        var requests = new RetentionRequestStream(readyTermination: index =>
        {
            if (index == 0
                && serviceRef is { } observed
                && ReferenceEquals(expectedOwner, GetActiveAssignment(observed))
                && ReferenceEquals(expectedResult, GetRetainedResult(observed)))
            {
                ownerRetainedAtReportRelease.TrySetResult();
            }

            return null;
        });

        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);
        serviceRef = service;

        var stream = BuildStream(requests, responses);
        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        Task? execution = null;
        Task? reporting = null;
        MatchingDrainOperation? matchingDrain = null;
        CancellationTokenRegistration cancellationObservedRegistration = default;
        try
        {
            // 1. Run A to its READY write. Complete already succeeded and the shared claim is
            // consumed by the SEPARATELY OWNED readiness write, which is what stays alive.
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            expectedOwner = GetActiveAssignment(service);
            var ownerCts = GetOwnerCts(service);
            var ownerReadyClaim = GetOwnerReadyClaim(service);
            expectedResult = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            AssertWirePayload(requests.Completes[0].Complete, taskId, RetainedOutcome.Completed);

            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained the assignment's readiness write.");

            Assert.True(execution.IsCompletedSuccessfully);

            // REPORTING HAS ALREADY TERMINATED — independently of readiness — while the SEPARATELY
            // OWNED readiness write is still parked inside its gated write.
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the readiness write.");
            Assert.NotSame(reporting, readinessWrite);
            Assert.False(
                readinessWrite.IsCompleted,
                "The separately owned readiness write must still be parked inside its gated write.");
            Assert.Equal(1, GetReadyClaimState(ownerReadyClaim));
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);

            // 2. COMPLETE THE CANCELLATION PHASE UP FRONT, and prove it really ran: a callback on
            // the assignment's own token records that cancellation was dispatched, and awaiting
            // CancelAsync guarantees every such callback has run to completion before we continue.
            // This is the SAME source and the SAME cancellation the transition performs, so nothing
            // about production behaviour changes — it is merely already done when the call starts.
            var cancellationObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationObservedRegistration = ownerCts.Token.Register(
                () => cancellationObserved.TrySetResult());

            await ownerCts.CancelAsync();

            Assert.True(ownerCts.IsCancellationRequested, "The assignment source must be cancelled up front.");
            Assert.True(
                cancellationObserved.Task.IsCompletedSuccessfully,
                "Awaiting CancelAsync must have run every registered callback to completion.");

            // The cancellation phase is finished, yet NOTHING has been drained or cleared: the
            // readiness write is still held, ownership and the retained result are intact, and the
            // source is still usable. Cancelling alone is not an ownership transition.
            Assert.False(readinessWrite.IsCompleted);
            Assert.Same(expectedOwner, GetActiveAssignment(service));
            Assert.Same(expectedResult, GetRetainedResult(service));
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));

            // 3. Invoke the ACTUAL private transition used by the matching-cancel handler while the
            // loop itself remains parked on its reader. An async method runs synchronously until its
            // first INCOMPLETE await. Cancellation is already complete (step 2) and so are BOTH the
            // execution and reporting joins, so correct code can only return here by reaching the
            // HELD readiness-write join. A version with that join removed has no incomplete await
            // left and therefore disposes the source, clears ownership and COMPLETES before this
            // call returns.
            matchingDrain = InvokeMatchingCancelDrain(service);

            // IMMEDIATE, SCHEDULE-INDEPENDENT PRE-RELEASE PROOF — taken with no intervening await,
            // so no continuation of any kind can have run in between. A no-readiness-join mutant
            // fails these BY NAME on every run.
            Assert.False(
                matchingDrain.Completion.IsCompleted,
                "Matching-cancel must remain incomplete while the readiness write is held — with "
                + "cancellation and both original-task joins already complete, the only await it can "
                + "be parked on is the held readiness-write join.");
            Assert.False(readinessWrite.IsCompleted);
            Assert.Equal(1, GetSlotOccupancy(service));
            Assert.Same(expectedOwner, GetActiveAssignment(service));
            Assert.Same(expectedResult, GetRetainedResult(service));
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));

            // 4. Release the readiness write. The test-owned source above is signalled only from its
            // write callback while the exact original owner/result are still installed. A removed
            // readiness join has already cleared them synchronously and cannot produce this signal.
            requests.ReleaseReady(0);
            await ownerRetainedAtReportRelease.Task.WaitAsync(
                Failsafe, TestContext.Current.CancellationToken);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await matchingDrain.Completion.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Null(matchingDrain.DeferredFailure());
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetActiveAssignment(service));
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.False(connection.IsRetired, "A matching-cancel ownership transition does not retire the connection.");

            // Exactly one execution, Complete and Ready attempt; the matching cancel found the
            // shared claim consumed by the SEPARATELY OWNED readiness write and could not retry
            // anything. The retained local remains full.
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(ownerReadyClaim));
            Assert.True(readinessWrite.IsCompletedSuccessfully);
            AssertFullResult(expectedResult, taskId, RetainedOutcome.Completed);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(connection.IsRetired);
        }
        finally
        {
            cancellationObservedRegistration.Dispose();
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("matching-cancel drain", matchingDrain?.Completion),
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// A PRODUCER CANCELLED BEFORE ITS <c>Task.Run</c> BODY EVER STARTS is still OBSERVED by the
    /// connection-bound reporting task, which terminates rather than being skipped.
    /// <para>
    /// HOW THE PRE-START CANCELLATION IS PRODUCED — through real production ordering, not a seam.
    /// The assignment handler calls <c>ResetSessionAsync</c> with the LOOP token, and only
    /// afterwards creates the assignment CTS and launches <c>Task.Run(..., ct)</c> with that SAME
    /// loop token. Cancelling the loop source from INSIDE the runner's reset entry therefore lands
    /// strictly between those two steps, so the runtime never invokes the delegate at all: the
    /// execution task goes straight to <c>Canceled</c> with its body unrun.
    /// (<c>ExecutionEntryCount</c> and <c>PromptCount</c> staying at zero prove the body never ran.)
    /// </para>
    /// <para>
    /// WHAT THIS PINS. Reporting awaits that ORIGINAL task DIRECTLY, so this otherwise-unexercised
    /// path is covered end to end and its contract is fixed:
    /// <list type="bullet">
    ///   <item><description>NO Complete is fabricated and NO Ready is attempted — the holder is
    ///   empty and the claim is left UNCONSUMED;</description></item>
    ///   <item><description>the heartbeat task state is cleared;</description></item>
    ///   <item><description>the loop's teardown drain — which joins BOTH original tasks WITHOUT a
    ///   caller token — REACHES COMPLETION and clears ownership, so neither task was abandoned. A
    ///   report that never terminated would park that drain forever; the failsafe outcome is
    ///   rejected by name below, so a hang cannot masquerade as the expected cancellation.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// SCOPE, STATED PLAINLY. This cell does NOT claim to kill a
    /// <c>ContinueWith(..., NotOnCanceled)</c> mutant. On this exact path such a continuation
    /// yields a CANCELLED reporting task, and the drain tolerates ordinary cancellation from either
    /// owned task identically — so the externally observable outcome (no Complete, no Ready,
    /// cleared state, completed teardown) is the same. What the cell does guarantee is that the
    /// pre-start-cancelled producer is genuinely REACHED by production (proved by zero executor and
    /// prompt entries against one session reset) and that the two-task drain still terminates
    /// through it, which is exactly the regression an accidental rewrite of this path would break.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProducerCancelledBeforeBodyStart_ReportingObservesItAndDrainStillCompletes()
    {
        const string taskId = "task-prestart-cancelled";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var loop = InvokeProcessMessagesWith(service, connection, loopCts.Token);
        try
        {
            // CANCEL THE LOOP SOURCE AT THE RESET BOUNDARY — after the handler's reset call, and
            // strictly before it creates the assignment CTS and launches Task.Run with that token.
            runner.OnResetEntered = _ => loopCts.Cancel();

            responses.Push(ResultAssignment(taskId));

            // The loop's next read observes the cancelled token and unwinds into its teardown,
            // which cancels and drains BOTH original tasks before clearing ownership.
            //
            // REACHING COMPLETION IS THE JOIN EVIDENCE. DrainAssignmentAsync awaits the reporting
            // task with NO caller token, so a report that never observed its pre-start-cancelled
            // producer — and therefore never terminated — would park the drain forever and this
            // wait would end in a TimeoutException. That outcome is rejected BY NAME below, so a
            // hang can never be mistaken for the expected cancellation.
            var loopOutcome = await Record.ExceptionAsync(
                () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.IsNotType<TimeoutException>(loopOutcome);
            Assert.IsAssignableFrom<OperationCanceledException>(loopOutcome);
            Assert.True(loop.IsCompleted, "Both original tasks must have been joined by the teardown drain.");

            // THE BODY NEVER RAN: the producer was cancelled before its delegate was invoked.
            Assert.Equal(0, runner.ExecutionEntryCount);
            Assert.Equal(0, runner.PromptCount);
            Assert.Equal(1, runner.ResetCount);

            // NOTHING WAS FABRICATED and NOTHING was written on the assignment's behalf.
            Assert.Empty(requests.Completes);
            Assert.Equal(0, requests.ReadyCount);

            // OWNERSHIP AND HEARTBEAT STATE were cleaned up by the completed teardown drain.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetActiveAssignment(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
        }
        finally
        {
            runner.OnResetEntered = null;
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// Directly pins the reporting boundary's behavior for a producer cancelled before its
    /// <c>Task.Run</c> delegate starts. This complements the end-to-end fixture above by retaining
    /// the ORIGINAL reporting task itself and asserting it completes SUCCESSFULLY after observing
    /// the cancelled producer; a cancellation-skippable continuation would instead be cancelled
    /// and fail this assertion.
    /// </summary>
    [Fact]
    public async Task PreStartCancelledProducer_ReportTaskObservesCancellationAndCompletesSuccessfully()
    {
        const string taskId = "task-direct-prestart-cancel";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var service = BuildService(runner, root);
        var stream = BuildStream(requests, responses);
        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);

        try
        {
            // The token is cancelled BEFORE Task.Run is called, so the delegate can never start.
            var bodyEntries = 0;
            using var producerCts = new CancellationTokenSource();
            producerCts.Cancel();
            Task execution = Task.Run(
                () => Interlocked.Increment(ref bodyEntries), producerCts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.True(execution.IsCanceled);
            Assert.Equal(0, Volatile.Read(ref bodyEntries));

            // Construct the same assignment-local objects production passes to reporting. They
            // are private implementation types, so reflection is observation/invocation only; no
            // state is injected into the service's ownership slot.
            var serviceType = typeof(WorkerService);
            var holderType = serviceType.GetNestedType(
                "TerminalResultHolder", BindingFlags.NonPublic)!;
            var readyType = serviceType.GetNestedType("ReadyClaim", BindingFlags.NonPublic)!;
            var receiptType = serviceType.GetNestedType(
                "CompletionReceiptTracker", BindingFlags.NonPublic)!;
            var holder = Activator.CreateInstance(holderType, nonPublic: true)!;
            var ready = Activator.CreateInstance(readyType, nonPublic: true)!;
            var receipt = Activator.CreateInstance(
                receiptType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [connection],
                culture: null)!;
            var ordinaryReady = NewOrdinaryReadySlot(connection, CancellationToken.None, ready);
            var domainTask = GrpcMapper.ToDomain(ResultAssignment(taskId).Assignment);

            serviceType.GetField("_currentTaskId", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, taskId);
            serviceType.GetField("_currentRole", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "tester");

            // Invoke the ACTUAL reporting method with the ORIGINAL pre-start-cancelled producer.
            // ObserveExecutionAsync must catch that cancellation and let reporting run its cleanup
            // to successful termination; it must not fabricate Complete/Ready or consume the claim,
            // and it must NOT publish the ordinary-Ready eligibility.
            var reporting = (Task)serviceType.GetMethod(
                    "ReportAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [
                    execution,
                    domainTask,
                    connection,
                    holder,
                    receipt,
                    ordinaryReady,
                ])!;

            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must observe a pre-start-cancelled producer and terminate successfully.");
            Assert.True(execution.IsCanceled);
            Assert.Equal(0, Volatile.Read(ref bodyEntries));
            Assert.Null(holderType.GetProperty("Result")!.GetValue(holder));
            Assert.Equal(0, GetReadyClaimState(ready));

            // NO RESULT, NO ARMING. A producer that never produced a result leaves the receipt
            // unarmed, so no acknowledgement could ever be accepted for it.
            Assert.False(
                GetReceiptArmed(receipt),
                "An absent result must leave the completion receipt unarmed.");
            Assert.False(GetReceiptConfirmed(receipt));

            // THE ELIGIBILITY WAS NEVER PUBLISHED, so the production settlement starts NOTHING and
            // leaves the shared claim UNCONSUMED — exactly what lets a matching cancel still emit
            // the fallback single Ready.
            Assert.Null(InvokeSettleOrdinaryReady(service, ordinaryReady));
            Assert.Equal(0, GetReadyClaimState(ready));
            Assert.Null(ordinaryReady.GetType().GetProperty("Write")!.GetValue(ordinaryReady));

            Assert.Empty(requests.Completes);
            Assert.Equal(0, requests.ReadyCount);
            Assert.Null(GetHeartbeatTaskId(service));
        }
        finally
        {
            connection.Retire();
            requests.ReleaseAll();
            responses.TryComplete();
            service.Dispose();
            TryDelete(root);
        }
    }

    /// <summary>
    /// A terminal result that cannot be mapped never arms receipt eligibility. This invokes the real
    /// reporting flow with a completed original producer and an assignment-local holder containing
    /// an otherwise complete result whose deliberately invalid status makes the production mapper
    /// throw. Ready retains its legacy single attempt, while no Complete or ACK eligibility appears.
    /// </summary>
    [Fact]
    public async Task FailedCompletionMapping_LeavesReceiptUnarmedAndStillAttemptsLegacyReady()
    {
        const string taskId = "task-unmappable-result";
        const string payloadSecret = "completion-payload-must-not-enter-diagnostic";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var service = BuildService(runner, root);
        var stream = BuildStream(requests, responses);
        var connection = TestConnectionFactory.Attach(
            service, "worker-1", stream, service.TestProvisioner, completionReceiptAckEnabled: true);

        var originalErr = Console.Error;
        var stdErr = new StringWriter();
        Task? reporting = null;
        try
        {
            Console.SetError(stdErr);

            var serviceType = typeof(WorkerService);
            var holderType = serviceType.GetNestedType(
                "TerminalResultHolder", BindingFlags.NonPublic)!;
            var readyType = serviceType.GetNestedType("ReadyClaim", BindingFlags.NonPublic)!;
            var receiptType = serviceType.GetNestedType(
                "CompletionReceiptTracker", BindingFlags.NonPublic)!;
            var holder = Activator.CreateInstance(holderType, nonPublic: true)!;
            var ready = Activator.CreateInstance(readyType, nonPublic: true)!;
            var receipt = Activator.CreateInstance(
                receiptType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [connection],
                culture: null)!;
            var domainTask = GrpcMapper.ToDomain(ResultAssignment(taskId).Assignment);
            var unmappable = new TaskResult
            {
                TaskId = taskId,
                Status = (TaskOutcome)int.MaxValue,
                Output = payloadSecret,
                Model = domainTask.Model,
            };
            holderType.GetMethod("Publish")!.Invoke(holder, [unmappable]);

            var ordinaryReady = NewOrdinaryReadySlot(connection, CancellationToken.None, ready);

            reporting = (Task)serviceType.GetMethod(
                    "ReportAssignmentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [
                    Task.CompletedTask,
                    domainTask,
                    connection,
                    holder,
                    receipt,
                    ordinaryReady,
                ])!;

            // REPORTING TERMINATES WITHOUT TOUCHING THE CONNECTION for a mapping failure.
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Same(unmappable, holderType.GetProperty("Result")!.GetValue(holder));
            Assert.False(GetReceiptArmed(receipt));
            Assert.False(GetReceiptConfirmed(receipt));
            Assert.Empty(requests.Completes);
            Assert.Equal(0, requests.ReadyCount);
            Assert.DoesNotContain(payloadSecret, stdErr.ToString(), StringComparison.Ordinal);

            // THE SEPARATELY OWNED READINESS WRITE: the failed mapping does not suppress the old
            // ordinary-Ready condition, so the settlement starts the ONE write — consumed claim,
            // exactly one attempt — and that write really is a Ready on the ACTUAL stream.
            var readinessWrite = InvokeSettleOrdinaryReady(service, ordinaryReady);
            Assert.NotNull(readinessWrite);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(readinessWrite.IsCompletedSuccessfully);
            Assert.Same(
                readinessWrite, ordinaryReady.GetType().GetProperty("Write")!.GetValue(ordinaryReady));
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(ready));
            Assert.True(reporting.IsCompletedSuccessfully);

            // A SECOND settlement is a no-op: the claim is already consumed and nothing is retried.
            Assert.Same(readinessWrite, InvokeSettleOrdinaryReady(service, ordinaryReady));
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(ready));
        }
        finally
        {
            Console.SetError(originalErr);
            requests.ReleaseAll();
            responses.TryComplete();
            if (reporting is not null)
                await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            connection.Retire();
            service.Dispose();
            TryDelete(root);
        }
    }

    /// <summary>
    /// Replacement is forced through the REAL assignment handler while A's REPORT is still blocked
    /// in its Ready write. The loop consumes B, but the handler cannot reset the runner, start B's
    /// body or install B's owner until BOTH of A's ORIGINAL tasks — its execution and its
    /// connection-bound reporting — have been joined; so A and its result remain the owner, B has
    /// neither started nor been installed, and once A is released B receives a fresh empty holder
    /// that never observes A's result.
    /// <para>
    /// ORDERING PROOF — the removal-proof part. A message merely being consumed is not evidence
    /// that its handler ran, so this fixture layers three independent, production-visible
    /// discriminators and never claims a happens-before edge the doubles do not provide (see
    /// <see cref="InlineDispatchResponseReader"/> for exactly what the dispatch rendezvous does and
    /// does not guarantee across the async-iterator boundary):
    /// <list type="number">
    ///   <item><description>PRE-RELEASE, while A's report is still parked: B's session-reset gate
    ///   must NOT have been signalled. The test neither pre-releases that gate nor releases A
    ///   beforehand, so correct code cannot have reached it — it is parked in the drain awaiting
    ///   A.</description></item>
    ///   <item><description>AT THE RESET AND PROMPT BOUNDARIES: each captures whether BOTH of A's
    ///   ORIGINAL tasks were already complete at that exact production instant.</description></item>
    ///   <item><description>POST-RELEASE CONSEQUENCE: if a detached drain resumes after B is
    ///   installed, it clears B's slot. The fixture therefore asserts that the slot still holds B
    ///   while B's body is alive; as documented below, this targets but cannot force that schedule.</description></item>
    /// </list>
    /// No polling, sleeps, or Task-internal inspection is used.
    /// </para>
    /// <para>
    /// RESIDUAL — stated plainly rather than claimed away. The pre-release discriminators fire only
    /// if the mutant's handler has REACHED B's session reset by the time they run. Because the
    /// dispatch rendezvous cannot force the async-iterator boundary to be traversed synchronously
    /// (see <see cref="InlineDispatchResponseReader"/>), a legal queued-continuation schedule exists
    /// in which a fire-and-forget (`_ = DrainRetainedForReplacementAsync()`) mutant has not yet
    /// entered B's handler at that instant. On that schedule the pre-release checks are vacuously
    /// satisfied and only the post-release consequence assertions remain as evidence.
    /// </para>
    /// <para>
    /// WHAT THAT MUTANT CAN STILL VIOLATE. A detached drain genuinely breaks a stated production
    /// contract — "ownership is cleared only AFTER the original execution joined" — because its
    /// continuation can clear the slot after B was installed. The post-release assertions below
    /// (slot occupancy, owner task id after a further consumed probe) target exactly that
    /// consequence, and in practice they catch it. What cannot be offered here is a
    /// SCHEDULE-INDEPENDENT proof: the continuation may also resume at a moment when clearing is
    /// unobservable to this fixture.
    /// </para>
    /// <para>
    /// THE SINGLE BINDING CONSTRAINT is the no-new-production-seam rule for this goal. A
    /// production-visible signal at the drain/clear boundary would make the ordering directly
    /// observable and close the residual; sleeps, polling, and async-state-machine/continuation-field
    /// reflection are likewise forbidden and are deliberately NOT used to approximate one.
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
        // can release the reset gate that a parked handler may still be waiting on. BOTH owned
        // tasks per assignment are tracked: an execution can be terminal while its report is still
        // parked in a transport write.
        Task? executionA = null;
        Task? reportingA = null;
        Task? readinessA = null;
        Task? executionB = null;
        Task? reportingB = null;
        var bResetRelease = new TaskCompletionSource();
        List<Exception> preReleaseFailures = [];
        try
        {
            responses.Push(ResultAssignment(taskA));
            await runner.PromptStarted(taskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            runner.Release(taskA);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("A-installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var resultA = AssertFullRetainedResult(service, taskA, RetainedOutcome.Completed);

            // Capture A's own owner, BOTH ORIGINAL tasks and its SEPARATELY OWNED readiness write
            // before replacement begins, so every ordering observation below is about THIS
            // assignment and can never be satisfied by a later one.
            var ownerA = GetActiveAssignment(service);
            executionA = GetActiveExecution(service);
            reportingA = GetActiveReporting(service);

            // ARM THE ORDERING CAPTURES before B can possibly be handled. Each records, at a
            // production-visible instant on B's path, whether ALL of A's owned tasks had already
            // completed. `_resetCount` distinguishes B's reset from A's. The values are what
            // discriminate — no polling, no sleeps, no Task-internals inspection.
            //
            // The reset ALSO parks on a gate the test owns, so the capture is taken at a fixed
            // point and the pre-release observations below cannot race a drain-less handler that
            // rushes ahead: such a handler necessarily reaches the reset (recording `false`) and
            // then waits there, where the test can observe it deterministically.
            var capturedExecutionA = executionA;
            var capturedReportingA = reportingA;
            Task? capturedReadinessA = null;
            var aJoinedAtBReset = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var aJoinedAtBPromptEntry = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            bool AllOfAJoined() =>
                capturedExecutionA.IsCompleted
                && capturedReportingA.IsCompleted
                && capturedReadinessA?.IsCompleted == true;

            var bResetReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.ResetGate = bResetRelease;
            runner.OnResetEntered = _ =>
            {
                if (runner.ResetCount >= 2)
                {
                    aJoinedAtBReset.TrySetResult(AllOfAJoined());
                    bResetReached.TrySetResult();
                }
            };
            runner.OnPromptEntered = enteredTaskId =>
            {
                if (string.Equals(enteredTaskId, taskB, StringComparison.Ordinal))
                    aJoinedAtBPromptEntry.TrySetResult(AllOfAJoined());
            };

            // A's REPORT publishes its ordinary-Ready eligibility and TERMINATES; the loop starts
            // the ACTUAL, separately owned readiness write, which stays parked until released.
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reportingA.IsCompletedSuccessfully,
                "A's report must terminate independently of the readiness write.");
            readinessA = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained A's readiness write.");
            capturedReadinessA = readinessA;
            Assert.NotSame(reportingA, readinessA);

            // The loop is now parked in its next MoveNext. Completing that pending read is this
            // fixture's DISPATCH RENDEZVOUS — see InlineDispatchResponseReader for the exact
            // happens-before edge it does, and does not, provide.
            await responses.WaitForParkedReadCountAsync(3)
                .WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // Deliver B through the REAL loop while A is still unfinished, followed by a probe the
            // sequential loop can only consume once it has finished handling B.
            responses.Push(ResultAssignment(taskB));
            responses.Push(Probe("B-blocked-probe"));
            await responses.Consumed(3).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // A's ORIGINAL READINESS WRITE is provably still running, so the handler must still own
            // A: it may not reset the runner, start B, or install B's owner yet.
            CapturePreRelease(() =>
                Assert.False(
                    readinessA.IsCompleted,
                    "A's separately owned readiness write must still be parked while its drain waits."));
            CapturePreRelease(() =>
                Assert.False(runner.HasPromptStarted(taskB), "B must not start while A's original readiness write is still running."));
            CapturePreRelease(() =>
                Assert.False(responses.Consumed(4).IsCompleted, "The loop must still be parked inside B's handler."));
            CapturePreRelease(() => Assert.Same(ownerA, GetActiveAssignment(service)));
            CapturePreRelease(() => Assert.Equal(taskA, GetActiveTaskId(service)));
            CapturePreRelease(() => Assert.Same(resultA, GetRetainedResult(service)));
            CapturePreRelease(() => Assert.Equal(1, runner.ResetCount));

            // B IS NOT INSTALLED — asserted directly, not merely implied by A still owning the slot.
            // The slot holds exactly one owner and that owner is NOT B, so the handler cannot have
            // reached InstallActiveAssignment for B while A's ORIGINAL readiness write is running.
            CapturePreRelease(() => Assert.Equal(1, GetSlotOccupancy(service)));
            CapturePreRelease(() => Assert.NotEqual(taskB, GetActiveTaskId(service)));

            // NOR HAS B'S EXECUTION BODY BEGUN ANY WORK. The executor's first act is to take the
            // tool bridge (ExecutionEntryCount) and the prompt is what PromptCount counts, so both
            // staying at A's single entry proves no part of B's body ran ahead of the drain.
            CapturePreRelease(() => Assert.Equal(1, runner.ExecutionEntryCount));
            CapturePreRelease(() => Assert.Equal(1, runner.PromptCount));

            // THE POSITIVE PRE-RELEASE DISCRIMINATOR. B's session reset is the FIRST production step
            // after the replacement drain. The test does NOT pre-release the reset gate and does NOT
            // release A's readiness write before this check, so correct code cannot have reached the
            // reset: it is parked in the drain awaiting A's still-held readiness write. A handler
            // that removed, detached, or failed to await that drain runs straight on to the reset,
            // where OnResetEntered signals this gate while A is still parked. Nothing in the test
            // can complete this signal, so it firing here is evidence production skipped the drain.
            CapturePreRelease(() => Assert.False(
                bResetReached.Task.IsCompleted,
                "B's session reset was reached while A's original readiness write was still parked — "
                + "the replacement drain did not run, was detached, or was not awaited."));

            // RELEASE A'S READINESS WRITE ONLY NOW — after every pre-release observation above has
            // been taken. From here A's drain can settle its remaining work and proceed, which is
            // what lets a DETACHED drain resume and expose its consequence below.
            requests.ReleaseReady(0);

            // THE DETERMINISTIC RENDEZVOUS. BOTH a correct handler and a drain-less one reach B's
            // session reset and park there, so this wait always completes; what DISCRIMINATES them
            // is the value each captured at that instant. A handler that REMOVED the replacement
            // drain reaches the reset while A's readiness write is still parked and records `false`.
            await bResetReached.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var joinedAtReset = await aJoinedAtBReset.Task.WaitAsync(
                Failsafe, TestContext.Current.CancellationToken);
            CapturePreRelease(() => Assert.True(
                joinedAtReset,
                "B's session reset ran before BOTH of A's original tasks were joined — the "
                + "replacement drain did not run or was not awaited."));

            // BOTH of A's ORIGINAL tasks are now genuinely terminal, which is the precondition for
            // the detached-drain checks below.
            await executionA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                executionA.IsCompleted,
                "A's original execution must be terminal once its Ready write was released.");
            Assert.True(
                reportingA.IsCompleted,
                "A's original report must be terminal once its Ready write was released.");

            // Let B's handler continue past the reset, then await positive evidence that the real
            // handler attempted B and its body entered.
            bResetRelease.TrySetResult();
            await runner.PromptStarted(taskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            if (GetActiveAssignment(service) is not null
                && string.Equals(GetActiveTaskId(service), taskB, StringComparison.Ordinal))
            {
                executionB = GetActiveExecution(service);
                reportingB = GetActiveReporting(service);
            }

            // The same ordering holds at B's own prompt entry. Record any failure so the test can
            // still reach and inspect the detached drain's production-visible consequence.
            var joinedAtPrompt = await aJoinedAtBPromptEntry.Task.WaitAsync(
                Failsafe, TestContext.Current.CancellationToken);
            CapturePreRelease(() => Assert.True(
                joinedAtPrompt,
                "B's body started before BOTH of A's original tasks were joined — the replacement "
                + "drain did not run or was not awaited."));

            // A following message boundary proves B's real handler completed owner installation.
            responses.Push(Probe("B-installed"));
            await responses.Consumed(5).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE DETACHED-DRAIN CONSEQUENCE TARGET. B's handler has now provably been attempted,
            // B is installed, and A's ORIGINAL execution is terminal. If a detached drain resumes
            // during this window it clears B's ownership; the assertions below reject that outcome.
            // They do not force the detached continuation to run in this window (the documented
            // no-seam residual), while correct production has no detached continuation at all.
            Assert.Equal(1, GetSlotOccupancy(service));
            Assert.Equal(taskB, GetActiveTaskId(service));
            Assert.False(
                runner.PromptCompleted(taskB),
                "B's body must still be running while the test holds its prompt gate.");

            Assert.Equal(taskB, GetActiveTaskId(service));
            executionB ??= GetActiveExecution(service);
            reportingB ??= GetActiveReporting(service);
            Assert.NotSame(ownerA, GetActiveAssignment(service));
            Assert.Null(GetRetainedResult(service));
            Assert.Equal(2, runner.ResetCount);
            Assert.True(executionA.IsCompleted, "A's original execution must be joined before B is installed.");
            Assert.True(reportingA.IsCompleted, "A's original report must be joined before B is installed.");

            // NO OBSERVED STRAY DRAIN MAY CLEAR B. A's execution is terminal by now, so any detached
            // replacement drain that resumes after B's install wrongly empties B's slot. The loop's
            // consumption of another probe is a real message-loop boundary; after it, B must STILL
            // own the slot. This strengthens the consequence check without claiming the probe is a
            // rendezvous with an independently scheduled detached continuation.
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
            reportingB = GetActiveReporting(service);
            requests.ReleaseReady(1);
            await executionB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            await reportingB.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Same(resultB, GetRetainedResult(service));

            responses.Push(MatchingCancel(taskB));
            responses.Push(Probe("B-cleared"));
            await responses.Consumed(8).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(2, runner.ExecutionEntryCount);
            Assert.Equal(2, runner.PromptCount);

            if (preReleaseFailures.Count == 1)
                throw preReleaseFailures[0];
            if (preReleaseFailures.Count > 1)
                throw new AggregateException("Replacement ordering failed before A was released.", preReleaseFailures);

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
                ("assignment execution A", executionA),
                ("assignment reporting A", reportingA),
                ("assignment execution B", executionB),
                ("assignment reporting B", reportingB),
                ("loop", loop));
            TryDelete(root);
        }

        void CapturePreRelease(Action assertion)
        {
            try
            {
                assertion();
            }
            catch (Exception ex)
            {
                preReleaseFailures.Add(ex);
            }
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
    /// A FAILED READY WRITE IS A REPORTING FAULT THAT CHANGES NOTHING ABOUT THE RETAINED RESULT,
    /// and the drain's guarded diagnostic still completes the ownership transition.
    /// <para>
    /// The assignment runs the REAL executor to a full Completed result and writes its Complete;
    /// its single Ready write then FAILS. The test captures the EXACT retained
    /// <see cref="TaskResult"/> instance before the fault and compares that same instance after
    /// it, so the evidence cannot be lost once ownership is cleared:
    /// <list type="bullet">
    ///   <item><description>the retained instance is IDENTICAL before and after the Ready fault,
    ///   with its full payload intact (<see cref="AssertFullResult"/>);</description></item>
    ///   <item><description>the Complete payload on the wire is byte-for-byte the pre-existing
    ///   mapping of that result (<see cref="AssertWirePayload"/>);</description></item>
    ///   <item><description>the EXECUTION task completed SUCCESSFULLY — a transport fault belongs
    ///   to REPORTING only — while the reporting task carries the ORIGINAL failure;</description></item>
    ///   <item><description>after the matching-cancel drain there is exactly ONE execution, ONE
    ///   Complete attempt and ONE Ready attempt: a failed Ready consumes the claim and is NEVER
    ///   retried, by the report or by the cancel handler.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The matching cancel drains that already-faulted ORIGINAL report while <c>Console.Error</c>
    /// throws specifically for the drain diagnostic: the source must still be disposed and
    /// ownership must still clear, and a following probe consumed by the still-live loop proves the
    /// matching handler completed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MatchingCancel_BodyFaultDiagnosticThrows_StillDisposesSourceAndClearsOwnership()
    {
        const string taskId = "task-ready-fault";
        var runner = new RetentionRunner(LongOutput);
        var readyFault = new BodyReadyWriteFailureException("injected body Ready failure");
        var requests = new RetentionRequestStream(
            readyTermination: index => index == 0 ? readyFault : null);
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var connection = TestConnectionFactory.Attach(service, "worker-1", stream, service.TestProvisioner);
        var loop = InvokeProcessMessagesWith(service, connection, TestContext.Current.CancellationToken);

        var originalErr = Console.Error;
        var drainDiagnosticFailure = new BodyDiagnosticFailureException("injected drain diagnostic failure");
        var throwingWriter = new MarkerThrowingErrorWriter(
            "Task drain observed a fault", new StringWriter(), drainDiagnosticFailure);

        // Hoisted so the finally joins EVERY original task it started, even after a failure.
        Task? execution = null;
        Task? reporting = null;
        try
        {
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var ownerCts = GetOwnerCts(service);
            var readyClaim = GetOwnerReadyClaim(service);
            execution = GetActiveExecution(service);
            reporting = GetActiveReporting(service);

            // Run the REAL executor to a full Completed result and hold its Complete write.
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // CAPTURE THE EXACT RETAINED INSTANCE — before the Ready fault, while ownership holds.
            var retainedBeforeFault = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            AssertWirePayload(requests.Completes[0].Complete, taskId, RetainedOutcome.Completed);

            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);

            // THE READY-WRITE FAULT BELONGS TO THE SEPARATELY OWNED READINESS WRITE, never to the
            // execution and never to the report: the report terminated successfully as soon as it
            // published the eligibility, and the ORIGINAL write task carries the fault — observed by
            // the DRAIN (its nonfatal, sanitized treatment), never propagated out of reporting.
            var readinessWrite = GetRetainedReadinessWrite(service)
                ?? throw new Xunit.Sdk.XunitException(
                    "The loop must have started and retained the assignment's readiness write.");
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must terminate independently of the readiness write.");
            Assert.NotSame(reporting, readinessWrite);
            var propagatedReadyFault = await Assert.ThrowsAsync<BodyReadyWriteFailureException>(
                () => readinessWrite.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(readyFault, propagatedReadyFault);
            await execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(
                execution.IsCompletedSuccessfully,
                "A failing readiness write is a reporting-side fault and must never fault the execution task.");

            // THE RETAINED RESULT SURVIVES THE REPORTING FAULT — the IDENTICAL instance, intact.
            Assert.Same(retainedBeforeFault, GetRetainedResult(service));
            AssertFullResult(retainedBeforeFault, taskId, RetainedOutcome.Completed);

            // ONE execution, ONE Complete attempt, ONE Ready attempt — the failed Ready consumed
            // the claim and was never retried.
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(readyClaim));
            Assert.Null(Record.Exception(() => _ = ownerCts.Token));

            // Only the DrainAssignmentAsync diagnostic is degraded from this point onward.
            Console.SetError(throwingWriter);
            responses.Push(MatchingCancel(taskId));
            responses.Push(Probe("after-drain"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(throwingWriter.WriteAttempts > 0, "The guarded drain diagnostic must be attempted.");
            Assert.Null(GetActiveAssignment(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.Throws<ObjectDisposedException>(() => _ = ownerCts.Token);
            Assert.False(connection.IsRetired, "A guarded diagnostic failure must not terminate the loop.");

            // AFTER THE DRAIN the counts are still exactly one apiece: the cancel handler found the
            // claim consumed and never retried the failed Ready, and nothing re-executed.
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(readyClaim));

            // The captured instance is STILL the full, unchanged result even after ownership was
            // cleared — the evidence was retained in a local, not read back from the slot.
            AssertFullResult(retainedBeforeFault, taskId, RetainedOutcome.Completed);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service,
                ("assignment execution", execution),
                ("assignment reporting", reporting),
                ("loop", loop));
            TryDelete(root);
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
        Task? bodyReporting = null;
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
            var reporting = GetActiveReporting(service);
            bodyReporting = reporting;
            var bodyFault = await Assert.ThrowsAsync<BodyDiagnosticFailureException>(
                () => execution.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            Assert.Same(bodyDiagnosticFailure, bodyFault);

            // The report OBSERVES that producer fault and terminates successfully: an empty holder
            // means no Complete is fabricated and the Ready claim is left for the cancel handler.
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(reporting.IsCompletedSuccessfully);

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

            // THE EXECUTION JOIN REALLY RAN, AND ONLY IT. The EXECUTION task faulted with its own
            // diagnostic failure, and the drain's EXECUTION join reports that in sanitized form.
            // The REPORTING task merely OBSERVED that fault — it never re-raises a producer
            // exception — so it completed SUCCESSFULLY and its join contributes no report.
            //
            // EXACT COUNT, not mere presence: a reporting task that re-raised the observed
            // producer exception would make the drain log this line TWICE, which a `Contains`
            // check alone would happily accept.
            Assert.True(
                reporting.IsCompletedSuccessfully,
                "Reporting must OBSERVE the producer fault and complete successfully — never re-raise it.");
            Assert.Equal(1, CountOccurrences(diagnostics, "Task drain observed a fault"));
            Assert.Equal(1, CountOccurrences(diagnostics, nameof(BodyDiagnosticFailureException)));
        }
        finally
        {
            Console.SetError(originalErr);
            runner.ReleaseAll();
            responses.TryComplete();
            armed?.DisposeRegistration();
            await JoinAllForTeardownAsync(service,
                ("assignment body", bodyExecution),
                ("assignment reporting", bodyReporting),
                ("loop", loop));
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

    // ══════════════════════════════════════════════════════════════════════════
    // Negotiated completion-receipt ACK — reader and reporting.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The EXACT guarded diagnostic production emits for the first accepted receipt.</summary>
    private const string ReceiptConfirmedLog = "Completion receipt confirmed by orchestrator for task";

    /// <summary>
    /// THE EARLY ACK, over BOTH executor branches and every terminal result. The assignment runs the
    /// REAL executor to a full Completed, Failed or Cancelled result, its single Complete write is
    /// HELD inside the fake, and the acknowledgement
    /// arrives on the ENABLED connection WHILE that write is still pending.
    /// <para>
    /// The acknowledgement is LATCHED and nothing else moves: the Complete write is neither joined
    /// nor finished, the send permit is still held by it, the owner is untouched, the EXACT retained
    /// <see cref="TaskResult"/> instance is unchanged, and no extra Complete, Ready, execution or
    /// runner reset happens. Only then is the write released, and the ordinary Complete/Ready
    /// sequence finishes exactly as before.
    /// </para>
    /// <para>
    /// ARMING IS NOT INSTALLATION. Before the executor produced a result the assignment is installed
    /// and running yet UNARMED, so the acknowledgement delivered at that point confirms nothing —
    /// which is what distinguishes arming at the Complete attempt from arming at install.
    /// </para>
    /// <para>
    /// The ACK is delivered through the REAL reader, and a FOLLOWING probe message is the barrier:
    /// the loop is sequential, so consuming the probe proves the ACK's own handler already ran. A
    /// delivery gate alone would not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, RetainedOutcome.Completed)]
    [InlineData(false, RetainedOutcome.Failed)]
    [InlineData(false, RetainedOutcome.Cancelled)]
    [InlineData(true, RetainedOutcome.Completed)]
    [InlineData(true, RetainedOutcome.Failed)]
    [InlineData(true, RetainedOutcome.Cancelled)]
    public async Task EarlyReceiptAck_BothExecutorBranchesAndAllResults_LatchesWithoutTouchingHeldCompleteWrite(
        bool provisioned,
        RetainedOutcome outcome)
    {
        var taskId = $"task-ack-{(provisioned ? "provisioned" : "legacy")}-{outcome}";
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
        ProvisionerHarness? provisionerHarness = null;
        if (provisioned)
        {
            provisionerHarness = new ProvisionerHarness(EligibleConfigUrl, "ghp_retention");
            service.TestProvisioner = provisionerHarness.Provisioner;
        }

        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            Assert.Same(connection, GetReceiptOwnerConnection(receipt));

            // AN INSTALLED, RUNNING ASSIGNMENT IS NOT ARMED. An ACK delivered now confirms nothing.
            Assert.False(GetReceiptArmed(receipt), "A merely installed/running assignment must not be armed.");
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("ack-before-result"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.False(GetReceiptConfirmed(receipt), "An ACK before the result/arming must not confirm.");
            Assert.False(GetReceiptArmed(receipt));

            // Produce the real result and HOLD the single Complete write. Cancelled is generated by
            // the assignment's own token so it traverses TaskExecutor's real cancellation boundary.
            if (outcome == RetainedOutcome.Cancelled)
                await CancelOwnerTokenAsync(service);
            else
                runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retained = AssertFullRetainedResult(service, taskId, outcome);
            AssertWirePayload(requests.Completes[0].Complete, taskId, outcome);
            Assert.Equal(provisioned ? 1 : 0, provisionerHarness?.FetchCount ?? 0);

            // ARMED — the exact result was mapped and the single Complete attempt is in flight.
            Assert.True(GetReceiptArmed(receipt), "The single Complete attempt must arm the receipt.");
            Assert.False(GetReceiptConfirmed(receipt));

            var execution = GetActiveExecution(service);
            var reporting = GetActiveReporting(service);
            Assert.True(execution.IsCompleted);
            Assert.False(reporting.IsCompleted, "Reporting must be held inside the gated Complete write.");
            Assert.Equal(0, GetSendGate(service).CurrentCount);

            // THE EARLY ACK, while the write is still pending.
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("ack-while-gated"));
            await responses.Consumed(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(GetReceiptConfirmed(receipt), "A matching ACK must record receipt confirmation.");

            // ...and NOTHING else moved: the write is still parked, the permit still held, the
            // owner and the exact retained instance unchanged, no extra send and no re-execution.
            Assert.False(
                reporting.IsCompleted,
                "An ACK must not join or finish the pending Complete write.");
            Assert.Equal(0, GetSendGate(service).CurrentCount);
            Assert.NotNull(GetActiveAssignment(service));
            Assert.Same(execution, GetActiveExecution(service));
            Assert.Same(reporting, GetActiveReporting(service));
            Assert.Same(retained, GetRetainedResult(service));
            Assert.Single(requests.Completes);
            Assert.Equal(0, requests.ReadyCount);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);
            Assert.Equal(1, runner.ResetCount);

            // The ordinary sequence then proceeds untouched.
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(reporting.IsCompletedSuccessfully);
            Assert.Same(retained, GetRetainedResult(service));
            AssertFullResult(retained, taskId, outcome);
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.True(GetReceiptConfirmed(receipt));

            // EXACTLY ONE concise diagnostic, and its complete line claims receipt confirmation only.
            // Exact-line equality excludes completion payloads, provisioned values, exception text,
            // transport-write success, processing, and phase-advancement claims in one assertion.
            var log = stdOut.ToString();
            var receiptLines = log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(ReceiptConfirmedLog, StringComparison.Ordinal))
                .ToArray();
            Assert.Equal([$"[Worker] {ReceiptConfirmedLog} {taskId}"], receiptLines);
            Assert.DoesNotContain("TRAILING-EVIDENCE", log, StringComparison.Ordinal);
            Assert.DoesNotContain("ghp_retention", log, StringComparison.Ordinal);
            Assert.DoesNotContain(InjectedFailureSecret, log, StringComparison.Ordinal);

            responses.Push(MatchingCancel(taskId));
            responses.Push(Probe("after-clear"));
            await responses.Consumed(8).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// AN EARLY ACK NEVER CONVERTS A LATER FAILED OR CANCELLED COMPLETE WRITE INTO A SUCCESS.
    /// Receipt confirmation and local write success are SEPARATE facts.
    /// <para>
    /// The acknowledgement is latched while the Complete write is still pending; that write then
    /// terminates with its injected transport outcome. Failure keeps its sanitized diagnostic while
    /// cancellation keeps its cancellation handling; neither is retried or re-runs execution, and
    /// the receipt stays confirmed with the identical retained result throughout.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(CompleteTermination.Failure)]
    [InlineData(CompleteTermination.Cancellation)]
    public async Task EarlyReceiptAck_ThenTerminatedCompleteWrite_KeepsWriteOutcomeSeparateFromConfirmation(
        CompleteTermination termination)
    {
        var taskId = $"task-ack-then-{termination.ToString().ToLowerInvariant()}-write";
        Exception writeTermination = termination switch
        {
            CompleteTermination.Failure => new InvalidOperationException("injected Complete write failure"),
            CompleteTermination.Cancellation => new OperationCanceledException("injected Complete write cancellation"),
            _ => throw new InvalidOperationException($"Unknown termination: {termination}"),
        };
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream(index => index == 0 ? writeTermination : null);
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdOut = new StringWriter();
        var stdErr = new StringWriter();
        try
        {
            Console.SetOut(stdOut);
            Console.SetError(stdErr);

            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var retained = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            var execution = GetActiveExecution(service);
            var reporting = GetActiveReporting(service);

            // THE ACK LANDS FIRST, while the write that is about to fail is still pending.
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("ack-before-failure"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.False(reporting.IsCompleted);

            // ...and now the write FAILS or CANCELS according to the current cell.
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE WRITE OUTCOME KEPT ITS EXISTING TREATMENT. Ordinary failure emits the sanitized
            // report; cancellation is swallowed by the dedicated cancellation catch. Neither raw
            // transport message is logged and neither outcome is converted by the confirmed ACK.
            if (termination == CompleteTermination.Failure)
                Assert.Contains("Task execution failed", stdErr.ToString(), StringComparison.Ordinal);
            else
                Assert.DoesNotContain("Task execution failed", stdErr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(writeTermination.Message, stdErr.ToString(), StringComparison.Ordinal);

            // No resend, no re-execution, the identical retained instance, and the receipt is still
            // confirmed — two separate facts that never merged.
            Assert.Single(requests.Completes);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);
            Assert.Same(retained, GetRetainedResult(service));
            AssertFullResult(retained, taskId, RetainedOutcome.Completed);
            Assert.True(execution.IsCompletedSuccessfully);
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.Equal(1, CountOccurrences(stdOut.ToString(), ReceiptConfirmedLog));

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// The identity/negotiation vectors an acknowledgement can arrive with. Each differs from the
    /// accepted shape in EXACTLY ONE respect, so no cell can pass for the wrong reason.
    /// </summary>
    public enum AckVector
    {
        /// <summary>A task ID that is not the retained assignment's — ordinally different.</summary>
        WrongTaskId,

        /// <summary>The retained task ID with surrounding whitespace: IDs are never trimmed.</summary>
        UntrimmedTaskId,

        /// <summary>The retained task ID in a different case: comparison stays ordinal and case-sensitive.</summary>
        CaseFoldedTaskId,

        /// <summary>A worker ID that is not this connection's assigned identity.</summary>
        WrongWorkerId,

        /// <summary>The connection's assigned identity with whitespace: IDs are never trimmed.</summary>
        UntrimmedWorkerId,

        /// <summary>The connection's assigned identity in a different case: never normalized.</summary>
        CaseFoldedWorkerId,

        /// <summary>Both identities match, but the connection negotiated NO acknowledgements.</summary>
        DisabledConnection,
    }

    /// <summary>
    /// NON-MATCHING DELIVERIES CONFIRM NOTHING. Each vector is delivered through the REAL reader
    /// against an ARMED assignment whose Complete write already succeeded, and each must leave the
    /// receipt unconfirmed — and must never confirm some other assignment, since there is exactly
    /// one and it stays unconfirmed. IDs are matched ordinally and verbatim: neither trimming nor
    /// case folding is performed.
    /// <para>
    /// NON-VACUITY: every cell then delivers the CORRECT acknowledgement on the same enabled loop
    /// (or, for the disabled cell, asserts the assignment was never armed at all), so a cell can
    /// never pass merely because acknowledgement processing is broken everywhere.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AckVector.WrongTaskId)]
    [InlineData(AckVector.UntrimmedTaskId)]
    [InlineData(AckVector.CaseFoldedTaskId)]
    [InlineData(AckVector.WrongWorkerId)]
    [InlineData(AckVector.UntrimmedWorkerId)]
    [InlineData(AckVector.CaseFoldedWorkerId)]
    [InlineData(AckVector.DisabledConnection)]
    public async Task ReceiptAck_NonMatchingVector_DoesNotConfirm(AckVector vector)
    {
        var taskId = $"task-vector-{vector}";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var enabled = vector != AckVector.DisabledConnection;
        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, enabled, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retained = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await GetActiveReporting(service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // THE ARMING PREMISE. An enabled connection armed at its Complete attempt; a DISABLED
            // one never arms at all, which is itself the vector under test.
            Assert.Equal(enabled, GetReceiptArmed(receipt));

            var ack = vector switch
            {
                AckVector.WrongTaskId => ReceiptAck(taskId + "-other", connection.AssignedId),
                AckVector.UntrimmedTaskId => ReceiptAck(" " + taskId + " ", connection.AssignedId),
                AckVector.CaseFoldedTaskId => ReceiptAck(taskId.ToUpperInvariant(), connection.AssignedId),
                AckVector.WrongWorkerId => ReceiptAck(taskId, connection.AssignedId + "-other"),
                AckVector.UntrimmedWorkerId => ReceiptAck(taskId, " " + connection.AssignedId + " "),
                AckVector.CaseFoldedWorkerId => ReceiptAck(taskId, connection.AssignedId.ToUpperInvariant()),
                AckVector.DisabledConnection => ReceiptAck(taskId, connection.AssignedId),
                _ => throw new InvalidOperationException($"Unknown vector: {vector}"),
            };

            responses.Push(ack);
            responses.Push(Probe("after-vector"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(
                GetReceiptConfirmed(receipt),
                $"The {vector} delivery must not record receipt confirmation.");
            Assert.DoesNotContain(ReceiptConfirmedLog, stdOut.ToString(), StringComparison.Ordinal);

            // Nothing else moved either: no resend, no extra Ready, no re-execution, same result.
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Same(retained, GetRetainedResult(service));
            Assert.NotNull(GetActiveAssignment(service));

            // NON-VACUITY for the enabled cells: the CORRECT delivery on this very loop confirms.
            if (enabled)
            {
                responses.Push(ReceiptAck(taskId, connection.AssignedId));
                responses.Push(Probe("after-correct"));
                await responses.Consumed(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                Assert.True(GetReceiptConfirmed(receipt));
                Assert.Equal(1, CountOccurrences(stdOut.ToString(), ReceiptConfirmedLog));
            }

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// THE READER'S NEGOTIATION GATE, EXERCISED AGAINST AN ARMED ASSIGNMENT.
    /// <para>
    /// A DISABLED connection never arms through reporting, so the vector matrix's disabled cell
    /// alone cannot show that the reader itself refuses. Here the assignment is armed through the
    /// tracker's OWN production transition and a fully matching acknowledgement is then delivered
    /// through the REAL reader on the DISABLED connection: it must still confirm nothing.
    /// </para>
    /// <para>
    /// NON-VACUITY, on the same delivery path: a second loop over an ENABLED connection, armed the
    /// same way with the same identities, DOES confirm — so the only difference between the two
    /// outcomes is the negotiated answer the reader consults.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArmedAssignmentOnConnection_ReceiptAckAcceptedOnlyWhenNegotiationEnabled(bool ackEnabled)
    {
        var taskId = $"task-reader-gate-{ackEnabled}";
        var runner = new GatedPromptRunner();
        var service = BuildService(runner);

        var responses = new ChannelResponseReader();
        var requests = new RecordingRequestStream();
        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            responses.Push(Assignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            Assert.False(GetReceiptArmed(receipt));

            // ARM through the tracker's OWN production transition, so BOTH cells face an armed
            // assignment and the ONLY difference is the connection's negotiated answer.
            ArmReceiptDirectly(receipt);
            Assert.True(GetReceiptArmed(receipt));

            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("after-ack"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(ackEnabled, GetReceiptConfirmed(receipt));
            Assert.Equal(
                ackEnabled ? 1 : 0,
                CountOccurrences(stdOut.ToString(), ReceiptConfirmedLog));

            // Neither cell touched anything else: the assignment is still installed and running.
            Assert.Equal(1, GetSlotOccupancy(service));
            Assert.Equal(taskId, GetActiveTaskId(service));
            Assert.False(
                GetActiveExecution(service).IsCompleted,
                "The assignment must still be running — an ACK never cancels or advances it.");
            Assert.Equal(0, requests.ReadyCount);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
        }
    }

    /// <summary>
    /// A NO-OWNER ACKNOWLEDGEMENT IS FORGOTTEN, while duplicate acknowledgements for the later armed
    /// assignment are idempotent: the receipt stays confirmed, exactly ONE guarded diagnostic is
    /// emitted (the FIRST accepted receipt only), and no Complete, Ready or execution is repeated.
    /// </summary>
    [Fact]
    public async Task NoOwnerThenDuplicateReceiptAcks_DoNotPreconfirmAndLogOnce()
    {
        const string taskId = "task-duplicate-ack";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            // NO OWNER: a matching-looking acknowledgement cannot be remembered globally and later
            // applied to an assignment that has not even arrived yet. The following probe proves the
            // real reader completed this no-owner handler before the assignment is delivered.
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("ack-with-no-owner"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.DoesNotContain(ReceiptConfirmedLog, stdOut.ToString(), StringComparison.Ordinal);

            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            Assert.False(
                GetReceiptConfirmed(receipt),
                "An ACK received with no owner must not pre-confirm a later assignment.");
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retained = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await GetActiveReporting(service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("after-duplicates"));
            await responses.Consumed(8).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(GetReceiptConfirmed(receipt));
            Assert.Equal(1, CountOccurrences(stdOut.ToString(), ReceiptConfirmedLog));

            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, runner.ExecutionEntryCount);
            Assert.Equal(1, runner.PromptCount);
            Assert.Same(retained, GetRetainedResult(service));
            AssertFullResult(retained, taskId, RetainedOutcome.Completed);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// THE RECEIPT DIAGNOSTIC IS GUARDED. A sink that throws specifically for the first accepted
    /// receipt line cannot fault the reader or undo confirmation: a following-message barrier is
    /// processed, ownership/result/Ready/Complete state stays unchanged, and a duplicate does not
    /// retry the failed diagnostic. Removing the production guard makes the reader fault before the
    /// first probe and this test fails by name.
    /// </summary>
    [Fact]
    public async Task FirstReceiptAck_WhenDiagnosticSinkThrows_ConfirmationAndReaderContinue()
    {
        const string taskId = "task-receipt-log-failure";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var forwarded = new StringWriter();
        var throwingWriter = new MarkerThrowingErrorWriter(
            ReceiptConfirmedLog,
            forwarded,
            new InvalidOperationException("injected receipt diagnostic failure"));
        try
        {
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retained = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            var reporting = GetActiveReporting(service);
            await reporting.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Console.SetOut(throwingWriter);
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("after-throwing-diagnostic"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(GetReceiptConfirmed(receipt));
            Assert.Equal(1, throwingWriter.WriteAttempts);
            Assert.False(loop.IsCompleted);
            Assert.NotNull(GetActiveAssignment(service));
            Assert.Same(retained, GetRetainedResult(service));
            Assert.Same(reporting, GetActiveReporting(service));
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, runner.ExecutionEntryCount);

            // The first acceptance consumed the state transition before logging. A duplicate is a
            // no-op and must not retry even the failed diagnostic.
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("after-duplicate"));
            await responses.Consumed(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Equal(1, throwingWriter.WriteAttempts);
            Assert.True(GetReceiptConfirmed(receipt));

            Console.SetOut(originalOut);
            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// A DELIVERY THAT BELONGS TO ANOTHER CONNECTION CONFIRMS NOTHING, even when both wire
    /// identities match exactly and that other connection is itself enabled.
    /// <para>
    /// The message loop always hands its OWN connection to the acknowledgement handler, so a
    /// previous-connection delivery is not producible through the reader. This drives the SAME
    /// production handler with the only input that differs — the delivering connection — and then
    /// proves non-vacuity by delivering the identical acknowledgement through the REAL loop, which
    /// does confirm.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReceiptAckFromPreviousConnection_DoesNotConfirmRetainedAssignment()
    {
        const string taskId = "task-foreign-connection";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        // A DIFFERENT connection object with the SAME assigned identity and the SAME negotiated
        // answer — deliberately never published, so it can only stand in for a previous one.
        var otherStream = BuildStream(new RetentionRequestStream(), new ChannelResponseReader());
        var previousConnection = TestConnectionFactory.CreateUnpublished(
            connection.AssignedId, otherStream, completionReceiptAckEnabled: true);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await GetActiveReporting(service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptArmed(receipt));

            // THE FOREIGN DELIVERY — identical identities, identical negotiation, other object.
            Assert.NotSame(connection, previousConnection);
            Assert.Equal(connection.AssignedId, previousConnection.AssignedId);
            InvokeReceiptAckOnConnection(
                service,
                previousConnection,
                new CompletionReceiptAck { TaskId = taskId, WorkerId = previousConnection.AssignedId });

            Assert.False(
                GetReceiptConfirmed(receipt),
                "A delivery bound to a previous connection must not confirm this assignment.");
            Assert.DoesNotContain(ReceiptConfirmedLog, stdOut.ToString(), StringComparison.Ordinal);

            // NON-VACUITY: the identical acknowledgement on THIS connection's real loop confirms.
            responses.Push(ReceiptAck(taskId, connection.AssignedId));
            responses.Push(Probe("after-own"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(receipt));
            Assert.Equal(1, CountOccurrences(stdOut.ToString(), ReceiptConfirmedLog));

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// AN ORDINARY COMPLETION WITH NO ACKNOWLEDGEMENT KEEPS THE LEGACY BEHAVIOR EXACTLY. On an
    /// ENABLED connection whose orchestrator simply never acknowledges, the Complete and the single
    /// Ready flow as before, the owner and its exact result stay retained, the receipt is armed but
    /// UNCONFIRMED, and the matching cancel performs the same drain-and-clear with no duplicate
    /// Ready. Reporting never awaited anything.
    /// </summary>
    [Fact]
    public async Task OrdinaryCompleteWithoutAck_RetainsLegacyReadyAndOwnershipBehavior()
    {
        const string taskId = "task-no-ack";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var (_, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            var readyClaim = GetOwnerReadyClaim(service);
            runner.Release(taskId);

            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            var retained = AssertFullRetainedResult(service, taskId, RetainedOutcome.Completed);
            AssertWirePayload(requests.Completes[0].Complete, taskId, RetainedOutcome.Completed);
            requests.ReleaseComplete(0);

            // REPORTING NEVER AWAITS AN ACK: it advances to its single Ready with none delivered.
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await GetActiveReporting(service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(GetReceiptArmed(receipt));
            Assert.False(GetReceiptConfirmed(receipt));
            Assert.Equal(1, requests.ReadyCount);
            Assert.Equal(1, GetReadyClaimState(readyClaim));
            Assert.Single(requests.Completes);
            Assert.NotNull(GetActiveAssignment(service));
            Assert.Same(retained, GetRetainedResult(service));
            Assert.DoesNotContain(ReceiptConfirmedLog, stdOut.ToString(), StringComparison.Ordinal);

            // The matching cancel drains and clears exactly as before — no duplicate Ready.
            responses.Push(MatchingCancel(taskId));
            responses.Push(Probe("after-clear"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.Null(GetActiveAssignment(service));
            Assert.Equal(1, requests.ReadyCount);
            Assert.Single(requests.Completes);
            Assert.Equal(1, runner.ExecutionEntryCount);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// A LEGITIMATE SUCCESSOR ARRIVING BEFORE THE OLD ACKNOWLEDGEMENT continues through the
    /// EXISTING replacement path: no new ACK wait, no rejection, no hang. The old acknowledgement
    /// then arrives after the ownership clear and is IGNORED — it must never confirm the successor,
    /// which is still running and unarmed. This slice makes no retention-until-ACK promise.
    /// </summary>
    [Fact]
    public async Task SuccessorBeforeOldReceiptAck_ProceedsThroughReplacementAndNeverConfirmsSuccessor()
    {
        const string taskA = "task-old-ack";
        const string taskB = "task-successor";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var stdOut = new StringWriter();
        try
        {
            Console.SetOut(stdOut);

            // A runs to completion — armed, never acknowledged.
            responses.Push(ResultAssignment(taskA));
            await runner.PromptStarted(taskA).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("A-installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receiptA = GetOwnerReceipt(service);
            var executionA = GetActiveExecution(service);
            var reportingA = GetActiveReporting(service);
            runner.Release(taskA);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await reportingA.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptArmed(receiptA));
            Assert.False(GetReceiptConfirmed(receiptA));

            // THE SUCCESSOR ARRIVES FIRST. It must flow through the existing replacement path
            // without waiting for A's acknowledgement and without being rejected.
            responses.Push(ResultAssignment(taskB));
            await runner.PromptStarted(taskB).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("B-installed"));
            await responses.Consumed(4).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(executionA.IsCompleted, "A's original execution must have been joined by the replacement drain.");
            Assert.True(reportingA.IsCompleted, "A's original report must have been joined by the replacement drain.");
            Assert.Equal(taskB, GetActiveTaskId(service));

            var receiptB = GetOwnerReceipt(service);
            Assert.NotSame(receiptA, receiptB);
            Assert.False(GetReceiptArmed(receiptB), "The still-running successor must not be armed.");

            // A'S OLD ACKNOWLEDGEMENT, after the ownership clear: ignored entirely.
            responses.Push(ReceiptAck(taskA, connection.AssignedId));
            responses.Push(Probe("after-old-ack"));
            await responses.Consumed(6).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.False(GetReceiptConfirmed(receiptA), "A delayed ACK after the ownership clear confirms nothing.");
            Assert.False(GetReceiptConfirmed(receiptB), "An ACK for A must never confirm the successor B.");
            Assert.DoesNotContain(ReceiptConfirmedLog, stdOut.ToString(), StringComparison.Ordinal);

            // B is untouched: still running, still installed, exactly one Complete so far (A's).
            Assert.Equal(taskB, GetActiveTaskId(service));
            Assert.False(runner.PromptCompleted(taskB));
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);

            // B then finishes and can be acknowledged in its OWN right.
            runner.Release(taskB);
            await requests.CompleteEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseComplete(1);
            await requests.ReadyEntered(1).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(1);
            await GetActiveReporting(service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            responses.Push(ReceiptAck(taskB, connection.AssignedId));
            responses.Push(Probe("after-B-ack"));
            await responses.Consumed(8).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.True(GetReceiptConfirmed(receiptB));
            Assert.Equal(1, CountOccurrences(stdOut.ToString(), ReceiptConfirmedLog));
            Assert.Equal(2, runner.ExecutionEntryCount);

            responses.TryComplete();
            await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        }
        finally
        {
            Console.SetOut(originalOut);
            runner.ReleaseAll();
            requests.ReleaseAll();
            responses.TryComplete();
            await JoinAllForTeardownAsync(service, ("loop", loop));
            TryDelete(root);
        }
    }

    /// <summary>
    /// TEARDOWN ADDS NO ACK WAIT. With an ARMED but never-acknowledged assignment retained, the
    /// reader reaches EOF (or the loop token is cancelled): the loop completes within the existing
    /// bounded join, the ownership slot clears, the heartbeat state clears and the connection
    /// retires — there is no new waiter to close on disconnect.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArmedButUnacknowledgedAssignment_EofOrCancel_AddsNoTeardownWait(bool cancelInsteadOfEof)
    {
        var taskId = $"task-teardown-{(cancelInsteadOfEof ? "cancel" : "eof")}";
        var runner = new RetentionRunner(LongOutput);
        var requests = new RetentionRequestStream();
        var responses = new ChannelResponseReader();
        var root = CreateRetentionRoot();
        var configRepoDir = Path.Combine(root, "config-repo");
        Directory.CreateDirectory(configRepoDir);
        var service = BuildService(runner, configRepoDir);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var stream = BuildStream(requests, responses);
        var (connection, loop) = StartNegotiatedLoop(
            service, stream, ackEnabled: true, loopCts.Token);

        try
        {
            responses.Push(ResultAssignment(taskId));
            await runner.PromptStarted(taskId).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            responses.Push(Probe("installed"));
            await responses.Consumed(2).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            var receipt = GetOwnerReceipt(service);
            runner.Release(taskId);
            await requests.CompleteEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseComplete(0);
            await requests.ReadyEntered(0).WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            requests.ReleaseReady(0);
            await GetActiveReporting(service).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            // ARMED, NEVER ACKNOWLEDGED — the state a new ACK wait would deadlock teardown on.
            Assert.True(GetReceiptArmed(receipt));
            Assert.False(GetReceiptConfirmed(receipt));

            if (cancelInsteadOfEof)
            {
                await loopCts.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
            }
            else
            {
                responses.TryComplete();
                await loop.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            }

            // The existing teardown sequencing is intact; nothing waited for an acknowledgement.
            Assert.Equal(0, GetSlotOccupancy(service));
            Assert.Null(GetHeartbeatTaskId(service));
            Assert.True(connection.IsRetired);
            Assert.False(GetReceiptConfirmed(receipt));
            Assert.Single(requests.Completes);
            Assert.Equal(1, requests.ReadyCount);
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

    /// <summary>
    /// The ACTIVE owner's CONNECTION-BOUND REPORTING task — the second ORIGINAL task an assignment
    /// owns. It is what a held, failed or cancelled Complete/Ready write blocks, and it is joined
    /// alongside the execution by every ownership transition.
    /// </summary>
    private static Task GetActiveReporting(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return (Task)active.GetType().GetProperty("Reporting")!.GetValue(active)!;
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

    // ── Ordinary-readiness observation seam ───────────────────────────────────
    //
    // Reporting no longer owns the Ready write: it PUBLISHES the ordinary-Ready eligibility and its
    // owner (the response loop, or an ownership transition) starts the SINGLE readiness write from
    // it, retaining that ORIGINAL task on the assignment. These helpers observe the ACTUAL
    // separately owned write — never the report — and drive the production settlement directly for
    // the vectors that cannot run a loop.

    /// <summary>The ACTIVE owner's ordinary-readiness slot.</summary>
    private static object GetOwnerOrdinaryReady(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("OrdinaryReady")!.GetValue(active)!;
    }

    /// <summary>
    /// The ACTUAL readiness write task the ACTIVE owner's slot retains — the very task every
    /// ownership transition joins — or <c>null</c> while none has been started.
    /// </summary>
    private static Task? GetRetainedReadinessWrite(WorkerService service)
    {
        var slot = GetOwnerOrdinaryReady(service);
        return (Task?)slot.GetType().GetProperty("Write")!.GetValue(slot);
    }

    /// <summary>Whether the given ordinary-readiness slot has already been settled.</summary>
    private static bool IsOrdinaryReadySettled(object slot) =>
        (bool)slot.GetType().GetProperty("IsSettled")!.GetValue(slot)!;

    /// <summary>
    /// Constructs the production ordinary-readiness slot the assignment handler itself creates, for
    /// the vectors that invoke reporting directly instead of running the loop.
    /// </summary>
    private static object NewOrdinaryReadySlot(
        WorkerConnection connection, CancellationToken token, object readyClaim) =>
        Activator.CreateInstance(
            typeof(WorkerService).GetNestedType("OrdinaryReadySlot", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [connection, token, readyClaim],
            culture: null)!;

    /// <summary>
    /// Invokes the production settlement of an ordinary-readiness slot and returns the ONE retained
    /// write (or <c>null</c> when the assignment is not eligible / the claim was already taken).
    /// </summary>
    private static Task? InvokeSettleOrdinaryReady(WorkerService service, object slot) =>
        (Task?)typeof(WorkerService)
            .GetMethod("SettleOrdinaryReady", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [slot]);

    // ── Completion-receipt ACK observation seam ───────────────────────────────
    //
    // The receipt state is read through the SAME reflection technique every other owner-local
    // value in this fixture uses (the retained result, the Ready claim, the CTS). Production
    // deliberately exposes no public getter for it.

    /// <summary>The ACTIVE owner's assignment-local completion-receipt tracker.</summary>
    private static object GetOwnerReceipt(WorkerService service)
    {
        var active = GetActiveAssignment(service)
            ?? throw new Xunit.Sdk.XunitException("Expected an active assignment owner.");
        return active.GetType().GetProperty("Receipt")!.GetValue(active)!;
    }

    private static bool GetReceiptArmed(object receipt) =>
        (bool)receipt.GetType().GetProperty("IsArmed")!.GetValue(receipt)!;

    private static bool GetReceiptConfirmed(object receipt) =>
        (bool)receipt.GetType().GetProperty("IsConfirmed")!.GetValue(receipt)!;

    /// <summary>
    /// Invokes the tracker's OWN production <c>Arm</c> transition — the identical call the reporting
    /// flow makes immediately before its single Complete send.
    /// </summary>
    /// <remarks>
    /// It exists for ONE vector: a DISABLED connection never arms through reporting, so without this
    /// the reader's negotiation gate could not be exercised against an ARMED assignment and its
    /// removal would be unobservable. Nothing else in the fixture uses it.
    /// </remarks>
    private static void ArmReceiptDirectly(object receipt) =>
        receipt.GetType().GetMethod("Arm")!.Invoke(receipt, null);

    /// <summary>The connection the ACTIVE owner's receipt tracker is bound to.</summary>
    private static WorkerConnection GetReceiptOwnerConnection(object receipt) =>
        (WorkerConnection)receipt.GetType().GetProperty("Owner")!.GetValue(receipt)!;

    /// <summary>The production send gate (observation only — never mutated).</summary>
    private static SemaphoreSlim GetSendGate(WorkerService service) =>
        (SemaphoreSlim)typeof(WorkerService)
            .GetField("_sendGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;

    /// <summary>One orchestrator-to-worker completion-receipt acknowledgement, verbatim.</summary>
    private static OrchestratorMessage ReceiptAck(string taskId, string workerId) => new()
    {
        CompletionReceiptAck = new CompletionReceiptAck { TaskId = taskId, WorkerId = workerId },
    };

    /// <summary>
    /// Invokes the REAL <c>HandleCompletionReceiptAck</c> with an explicitly supplied connection.
    /// </summary>
    /// <remarks>
    /// Used ONLY for the FOREIGN-CONNECTION vector. The message loop always passes the connection
    /// it was started for, so a delivery bound to a DIFFERENT (for example previous) connection
    /// object is not producible through the loop; this drives the same production method the loop's
    /// <c>CompletionReceiptAck</c> case calls, with the only input that differs.
    /// </remarks>
    private static void InvokeReceiptAckOnConnection(
        WorkerService service, WorkerConnection connection, CompletionReceiptAck ack) =>
        typeof(WorkerService).GetMethod(
            "HandleCompletionReceiptAck", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [connection, ack]);

    /// <summary>
    /// Attaches a connection carrying an explicit NEGOTIATED ACK answer and starts the REAL message
    /// loop on it — the same publication + direct-loop pattern every other fixture here uses.
    /// </summary>
    private static (WorkerConnection Connection, Task Loop) StartNegotiatedLoop(
        WorkerService service,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        bool ackEnabled,
        CancellationToken ct,
        string assignedId = "worker-1")
    {
        var connection = TestConnectionFactory.Attach(
            service, assignedId, stream, service.TestProvisioner, ackEnabled);
        return (connection, InvokeProcessMessagesWith(service, connection, ct));
    }

    /// <summary>
    /// Counts NON-OVERLAPPING occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.
    /// Used where mere presence is not enough — a duplicated diagnostic (for example a producer
    /// exception reported by BOTH drain joins because reporting wrongly re-raised it) must fail,
    /// and a <c>Contains</c> assertion would pass for it.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

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
    /// <param name="completeTermination">
    /// Per-index injected failure for a <c>TaskComplete</c> write, applied after its gate released.
    /// </param>
    /// <param name="readyTermination">
    /// Per-index injected failure for a <c>WorkerReady</c> write, applied AFTER the message was
    /// recorded and AFTER its gate released — so the ATTEMPT still counts and a test can prove the
    /// write really was issued and is NEVER retried. Models a Ready write that fails on the
    /// REPORTING task's own single attempt.
    /// </param>
    private sealed class RetentionRequestStream(
        Func<int, Exception?>? completeTermination = null,
        Func<int, Exception?>? readyTermination = null)
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

        /// <summary>How many <c>WorkerReady</c> writes have been ENTERED so far.</summary>
        internal int ReadyCount
        {
            get { lock (_gate) return _readies.Count; }
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
                int index;
                lock (_gate)
                {
                    index = _readies.Count;
                    _readies.Add(message.Clone());
                    entered = Slot(_readyEntered, index);
                    release = Slot(_readyRelease, index);
                    if (_releaseImmediately) release.TrySetResult();
                }
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);

                // The ATTEMPT is recorded above BEFORE the injected failure applies, so a test can
                // prove the write really was issued — and that it is never retried.
                if (readyTermination?.Invoke(index) is { } readyError)
                    throw readyError;
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

        /// <summary>Task ids whose prompt invocation has RETURNED (normally or by throwing).</summary>
        private readonly HashSet<string> _completed = [];

        /// <summary>
        /// APPEND-ONLY record of every prompt invocation that ever STARTED, in start order. Each
        /// entry completes when that invocation returns or throws.
        /// </summary>
        /// <remarks>
        /// This tracks PROMPT invocations only. It is NOT the assignment's enclosing execution —
        /// see <see cref="AssignmentExecutions"/>, which is what teardown must join.
        /// </remarks>
        private readonly List<Task> _startedBodies = [];

        /// <summary>
        /// APPEND-ONLY record of the REAL enclosing assignment executions
        /// (<c>ActiveAssignment.Execution</c>, i.e. the <c>Task.Run</c> body), discovered through
        /// <see cref="ExecutionProbe"/> at every production boundary this double already observes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A prompt-return surrogate is NOT a substitute: the enclosing execution continues past
        /// <c>SendPromptAsync</c> through result creation and the Complete/Ready reporting, so a
        /// surrogate can be terminal while the real execution is still live. Teardown joins THESE.
        /// </para>
        /// <para>
        /// THE CLOSURE HANDSHAKE. <see cref="SealRecording"/> latches this record. After sealing,
        /// work admitted CONCURRENTLY is still recorded (every production boundary keeps probing),
        /// and <see cref="RecordedSinceSeal"/> reports whether anything was admitted after the
        /// latch — so a caller can re-drain to a fixpoint instead of assuming the set is closed.
        /// </para>
        /// </remarks>
        private readonly List<Task> _assignmentExecutions = [];

        /// <summary>Set once <see cref="SealRecording"/> has run.</summary>
        private bool _sealed;

        /// <summary>Set whenever an execution is admitted AFTER the seal.</summary>
        private bool _recordedSinceSeal;

        /// <summary>
        /// TEARDOWN LATCH. Once set it stays set for the rest of the fixture, and every release gate
        /// created from then on is completed AT CREATION, so an invocation that starts after the
        /// sweep can never park on a gate nobody will ever release.
        /// </summary>
        private bool _teardown;
        private string? _taskId;
        private int _promptCount;
        private int _executionEntryCount;
        private int _resetCount;

        internal int PromptCount => Volatile.Read(ref _promptCount);
        internal int ExecutionEntryCount => Volatile.Read(ref _executionEntryCount);
        internal int ResetCount => Volatile.Read(ref _resetCount);

        /// <summary>Snapshot of every prompt invocation that ever started, in start order.</summary>
        internal IReadOnlyList<Task> StartedBodies
        {
            get { lock (_gate) return [.. _startedBodies]; }
        }

        /// <summary>
        /// Snapshot of every REAL enclosing assignment execution this double ever observed. This is
        /// the authoritative teardown join set.
        /// </summary>
        internal IReadOnlyList<Task> AssignmentExecutions
        {
            get { lock (_gate) return [.. _assignmentExecutions]; }
        }

        /// <summary>
        /// Reads the service's CURRENT <c>ActiveAssignment.Execution</c>, or <c>null</c> when the
        /// ownership slot is empty. Installed by the fixture so this double can record the REAL
        /// execution at each production boundary it already observes (reset entry, prompt entry) —
        /// the moments the enclosing body exists and is reachable.
        /// </summary>
        internal Func<Task?>? ExecutionProbe { get; set; }

        /// <summary>
        /// Reads the service's CURRENT <c>ActiveAssignment.Reporting</c> — the SECOND original task
        /// an assignment owns — or <c>null</c> when the slot is empty. Recorded at the same
        /// boundaries as the execution, so teardown joins BOTH owned tasks and a report still
        /// parked in a transport write can never be missed.
        /// </summary>
        internal Func<Task?>? ReportingProbe { get; set; }

        /// <summary>
        /// Whether an execution was admitted AFTER <see cref="SealRecording"/>. Teardown re-drains
        /// while this keeps flipping, so a body admitted concurrently with the seal is joined
        /// rather than missed.
        /// </summary>
        internal bool RecordedSinceSeal
        {
            get { lock (_gate) return _recordedSinceSeal; }
        }

        /// <summary>
        /// CLOSURE HANDSHAKE — latches the recording set. Recording itself does NOT stop (that
        /// would hide late work); instead every later admission is flagged through
        /// <see cref="RecordedSinceSeal"/> so teardown can re-drain to a fixpoint. Returns the
        /// snapshot taken at the instant of sealing.
        /// </summary>
        internal IReadOnlyList<Task> SealRecording()
        {
            lock (_gate)
            {
                _sealed = true;
                _recordedSinceSeal = false;
                return [.. _assignmentExecutions];
            }
        }

        /// <summary>
        /// Clears the post-seal flag so a caller can detect a NEW admission during its next drain
        /// pass. Returns the current snapshot for that pass.
        /// </summary>
        internal IReadOnlyList<Task> TakeRecordedSinceSeal()
        {
            lock (_gate)
            {
                _recordedSinceSeal = false;
                return [.. _assignmentExecutions];
            }
        }

        /// <summary>
        /// Records the REAL enclosing execution AND its connection-bound reporting task, if any.
        /// Idempotent by reference: a task observed at several boundaries is stored once.
        /// </summary>
        private void RecordCurrentExecution()
        {
            RecordOwnedTask(ExecutionProbe?.Invoke());
            RecordOwnedTask(ReportingProbe?.Invoke());
        }

        private void RecordOwnedTask(Task? owned)
        {
            if (owned is null)
                return;

            lock (_gate)
            {
                foreach (var recorded in _assignmentExecutions)
                {
                    if (ReferenceEquals(recorded, owned))
                        return;
                }

                _assignmentExecutions.Add(owned);
                if (_sealed)
                    _recordedSinceSeal = true;
            }
        }

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
        internal TaskCompletionSource? ResetGate { get; set; }

        internal bool HasPromptStarted(string taskId)
        {
            lock (_gate) return _startedIds.Contains(taskId);
        }

        /// <summary>
        /// Whether the prompt invocation for <paramref name="taskId"/> has RETURNED. It is the
        /// positive "that body is still alive" observation a teardown-ordering assertion needs:
        /// <c>false</c> while the invocation is parked on its gate.
        /// </summary>
        internal bool PromptCompleted(string taskId)
        {
            lock (_gate)
                return _completed.Contains(taskId);
        }

        internal void Release(string taskId)
        {
            lock (_gate) Slot(_release, taskId).TrySetResult();
        }

        /// <summary>
        /// Enters TEARDOWN MODE and releases everything currently parked. The latch persists, so any
        /// gate created afterwards (a late-started body's prompt gate, or a reset gate) is completed
        /// at creation — closing the window where a body that starts after the sweep parks forever.
        /// </summary>
        internal void ReleaseAll()
        {
            lock (_gate)
            {
                _teardown = true;
                ResetGate?.TrySetResult();
                foreach (var source in _release.Values) source.TrySetResult();
            }
        }

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var taskId = _taskId ?? throw new InvalidOperationException("Task ID was not set.");
            Interlocked.Increment(ref _promptCount);

            // RECORD THIS INVOCATION DURABLY before any observer can throw, so every invocation
            // that entered the runner remains visible to teardown.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task release;
            lock (_gate)
            {
                _startedBodies.Add(completion.Task);
                _startedIds.Add(taskId);
                Slot(_started, taskId).TrySetResult();
                release = Slot(_release, taskId).Task;
            }

            try
            {
                // The ordering capture runs at ENTRY, before this body can park or be released.
                OnPromptEntered?.Invoke(taskId);

                // RECORD THE REAL ENCLOSING EXECUTION. At prompt entry the assignment's Task.Run
                // body exists; if the owner is already installed this captures the actual
                // ActiveAssignment.Execution rather than this prompt-return surrogate.
                RecordCurrentExecution();

                await release.WaitAsync(ct);
                return output(taskId);
            }
            finally
            {
                // Probe again on the way out: an execution installed while this prompt was parked
                // (the handler installs the owner only after Task.Run returns) is admitted here.
                RecordCurrentExecution();
                lock (_gate) _completed.Add(taskId);
                completion.TrySetResult();
            }
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

            // RECORD THE REAL ENCLOSING EXECUTION of whatever assignment is installed right now.
            // The reset for assignment N+1 runs while assignment N's execution may still be the
            // installed owner, so this boundary admits an execution the ownership snapshots could
            // otherwise miss once a later clear happens.
            RecordCurrentExecution();

            // ...and then the reset PARKS if a gate was installed, so the capture above is taken at
            // a fixed point the test controls rather than racing the test's own observations. The
            // TEARDOWN LATCH suppresses the park entirely, so a reset that is reached after the
            // sweep cannot hold a late-started assignment open.
            Task? gate;
            lock (_gate)
            {
                if (_teardown)
                    ResetGate?.TrySetResult();
                gate = ResetGate?.Task;
            }

            if (gate is not null)
                await gate.WaitAsync(ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>
        /// Returns the gate for <paramref name="taskId"/>, creating it on first use. A gate created
        /// while the TEARDOWN LATCH is set is completed AT CREATION, so a body that starts after the
        /// teardown sweep never parks on a gate nobody will release. Callers hold <c>_gate</c>.
        /// </summary>
        private TaskCompletionSource Slot(
            Dictionary<string, TaskCompletionSource> slots,
            string taskId)
        {
            if (!slots.TryGetValue(taskId, out var source))
            {
                source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_teardown)
                    source.TrySetResult();
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

        // INSTALL THE REAL-TASK PROBES. The double calls them at the production boundaries it
        // already observes, so it records BOTH enclosing owned tasks — the ActiveAssignment's
        // Execution (the Task.Run body) and its connection-bound Reporting — rather than a
        // prompt-return surrogate. Teardown joins those recorded tasks, which is what makes an
        // orphaned-but-live execution OR a report still parked in a write impossible to miss.
        switch (runner)
        {
            case RetentionRunner retention:
                retention.ExecutionProbe = () => TryGetActiveExecution(service);
                retention.ReportingProbe = () => TryGetActiveReporting(service);
                break;
            case GatedPromptRunner gated:
                gated.ExecutionProbe = () => TryGetActiveExecution(service);
                gated.ReportingProbe = () => TryGetActiveReporting(service);
                break;
            default:
                break;
        }

        return service;
    }

    /// <summary>
    /// The service's CURRENT <c>ActiveAssignment.Execution</c>, or <c>null</c> when the ownership
    /// slot is empty. Observation only — it never mutates production state.
    /// </summary>
    private static Task? TryGetActiveExecution(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        return active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
    }

    /// <summary>
    /// The service's CURRENT <c>ActiveAssignment.Reporting</c>, or <c>null</c> when the ownership
    /// slot is empty. Observation only.
    /// </summary>
    private static Task? TryGetActiveReporting(WorkerService service)
    {
        var active = GetActiveAssignment(service);
        return active is null
            ? null
            : (Task?)active.GetType().GetProperty("Reporting")!.GetValue(active);
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

        // EVERY REAL ASSIGNMENT EXECUTION the runner ever observed. These are the enclosing
        // Task.Run bodies (ActiveAssignment.Execution) — NOT prompt-return surrogates, which go
        // terminal while the real execution is still creating its result and writing Complete/Ready.
        // A gate dictionary cannot answer "is a late-started body still live?", and the ownership
        // slot only ever exposes the CURRENT owner, so a detached drain that clears the slot would
        // otherwise hide an orphaned-but-live execution from teardown entirely.
        var runnerField = typeof(WorkerService)
            .GetField("_agentRunner", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var runnerInstance = runnerField.GetValue(service);

        // Prompt surrogates are still joined (they are real work the double owns), but they are
        // additive evidence only; the assignment executions below are the authoritative set.
        IReadOnlyList<Task> startedBodies = runnerInstance switch
        {
            RetentionRunner retention => retention.StartedBodies,
            GatedPromptRunner gated => gated.StartedBodies,
            _ => [],
        };

        foreach (var body in startedBodies)
            AddProducer("started prompt body", body);

        foreach (var execution in RecordedExecutions())
            AddProducer("recorded assignment execution", execution);

        // Capture any assignment task that started before the test reached its explicit local
        // assignment. BOTH owned tasks are taken — an execution can be terminal while its report is
        // still parked in a transport write. This closes assertion-failure windows without relying
        // on the loop to be their only join owner.
        var active = GetActiveAssignment(service);
        var activeExecution = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
        if (activeExecution is not null)
            AddProducer("active assignment body", activeExecution);

        var activeReporting = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Reporting")!.GetValue(active);
        if (activeReporting is not null)
            AddProducer("active assignment reporting", activeReporting);

        foreach (var (name, producer) in producers)
        {
            if (producer is not null)
                await JoinOneAsync(name, producer);
        }

        // The loop may have started an assignment from an already-buffered message after the first
        // snapshot. Re-snapshot after all listed joins were attempted and independently join that
        // late assignment's BOTH tasks too; one timeout never prevents this second observation.
        active = GetActiveAssignment(service);
        var lateActiveExecution = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Execution")!.GetValue(active);
        if (lateActiveExecution is not null
            && AddProducer("late active assignment body", lateActiveExecution))
        {
            await JoinOneAsync("late active assignment body", lateActiveExecution);
        }

        var lateActiveReporting = active is null
            ? null
            : (Task?)active.GetType().GetProperty("Reporting")!.GetValue(active);
        if (lateActiveReporting is not null
            && AddProducer("late active assignment reporting", lateActiveReporting))
        {
            await JoinOneAsync("late active assignment reporting", lateActiveReporting);
        }

        // THE CLOSURE HANDSHAKE. Seal the runner's recording, then drain to a FIXPOINT: join
        // everything recorded, and if the runner admitted a new execution concurrently with (or
        // after) the seal, take the new snapshot and join again. The loop is bounded by the number
        // of distinct executions a fixture can create, and each individual join keeps its own
        // bounded wait, so a body that starts during teardown is joined rather than missed.
        SealRunnerRecording();
        var drainPasses = 0;
        while (true)
        {
            var admittedNew = false;
            foreach (var execution in RecordedExecutions())
            {
                if (!AddProducer($"sealed assignment execution #{drainPasses}", execution))
                    continue;

                admittedNew = true;
                await JoinOneAsync($"sealed assignment execution #{drainPasses}", execution);
            }

            if (!admittedNew && !RunnerRecordedSinceSeal())
                break;

            if (++drainPasses > MaxTeardownDrainPasses)
            {
                failures.Add(new Xunit.Sdk.XunitException(
                    "Teardown could not reach a closed join set: the runner kept admitting new "
                    + "assignment executions after the recording was sealed."));
                break;
            }

            TakeRunnerRecordedSinceSeal();
        }

        // FINAL SWEEP of the prompt record: a prompt may have started while the joins above ran.
        startedBodies = runnerInstance switch
        {
            RetentionRunner retention => retention.StartedBodies,
            GatedPromptRunner gated => gated.StartedBodies,
            _ => [],
        };

        for (var index = 0; index < startedBodies.Count; index++)
        {
            var body = startedBodies[index];
            if (!AddProducer($"late-started prompt body #{index}", body))
                continue;

            await JoinOneAsync($"late-started prompt body #{index}", body);
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

        // Adds a producer to the join set unless it is already present (by reference). Returns
        // whether it was newly admitted, so a caller can join only what it just added.
        bool AddProducer(string name, Task? producer)
        {
            if (producer is null)
                return false;

            foreach (var candidate in producers)
            {
                if (ReferenceEquals(candidate.Producer, producer))
                    return false;
            }

            producers = [.. producers, (name, producer)];
            return true;
        }

        IReadOnlyList<Task> RecordedExecutions() => runnerInstance switch
        {
            RetentionRunner retention => retention.AssignmentExecutions,
            GatedPromptRunner gated => gated.AssignmentExecutions,
            _ => [],
        };

        void SealRunnerRecording()
        {
            switch (runnerInstance)
            {
                case RetentionRunner retention:
                    retention.SealRecording();
                    break;
                case GatedPromptRunner gated:
                    gated.SealRecording();
                    break;
                default:
                    break;
            }
        }

        bool RunnerRecordedSinceSeal() => runnerInstance switch
        {
            RetentionRunner retention => retention.RecordedSinceSeal,
            GatedPromptRunner gated => gated.RecordedSinceSeal,
            _ => false,
        };

        void TakeRunnerRecordedSinceSeal()
        {
            switch (runnerInstance)
            {
                case RetentionRunner retention:
                    retention.TakeRecordedSinceSeal();
                    break;
                case GatedPromptRunner gated:
                    gated.TakeRecordedSinceSeal();
                    break;
                default:
                    break;
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

        /// <summary>
        /// APPEND-ONLY record of every prompt invocation that ever STARTED, in start order. Each
        /// entry completes when that invocation returns or throws. This tracks PROMPT invocations
        /// only — see <see cref="AssignmentExecutions"/> for the enclosing executions teardown joins.
        /// </summary>
        private readonly List<Task> _startedBodies = [];

        /// <summary>
        /// APPEND-ONLY record of the REAL enclosing assignment executions
        /// (<c>ActiveAssignment.Execution</c>), discovered through <see cref="ExecutionProbe"/> at
        /// the production boundaries this double already observes. A prompt-return surrogate is not
        /// a substitute: the enclosing execution continues past the prompt through result creation
        /// and the Complete/Ready reporting.
        /// </summary>
        private readonly List<Task> _assignmentExecutions = [];

        /// <summary>Set once <see cref="SealRecording"/> has run.</summary>
        private bool _sealed;

        /// <summary>Set whenever an execution is admitted AFTER the seal.</summary>
        private bool _recordedSinceSeal;

        private readonly TaskCompletionSource _unwindGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resetAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// TEARDOWN LATCH. Once set it stays set, and every release gate created from then on is
        /// completed AT CREATION, so a body that starts after the sweep cannot park forever.
        /// </summary>
        private bool _teardown;
        private string? _taskId;

        /// <summary>Snapshot of every prompt invocation that ever started, in start order.</summary>
        internal IReadOnlyList<Task> StartedBodies
        {
            get { lock (_gate) return [.. _startedBodies]; }
        }

        /// <summary>
        /// Snapshot of every REAL enclosing assignment execution this double ever observed. This is
        /// the authoritative teardown join set.
        /// </summary>
        internal IReadOnlyList<Task> AssignmentExecutions
        {
            get { lock (_gate) return [.. _assignmentExecutions]; }
        }

        /// <summary>
        /// Reads the service's CURRENT <c>ActiveAssignment.Execution</c>, or <c>null</c> when the
        /// slot is empty. Installed by a fixture so this double records the REAL execution at the
        /// production boundaries it already observes.
        /// </summary>
        internal Func<Task?>? ExecutionProbe { get; set; }

        /// <summary>
        /// Reads the service's CURRENT <c>ActiveAssignment.Reporting</c> — the SECOND original task
        /// an assignment owns — or <c>null</c> when the slot is empty.
        /// </summary>
        internal Func<Task?>? ReportingProbe { get; set; }

        /// <summary>Whether an execution was admitted AFTER <see cref="SealRecording"/>.</summary>
        internal bool RecordedSinceSeal
        {
            get { lock (_gate) return _recordedSinceSeal; }
        }

        /// <summary>
        /// CLOSURE HANDSHAKE — latches the recording set. Recording continues (that is what keeps
        /// late work visible); every later admission is flagged so teardown can re-drain to a
        /// fixpoint instead of assuming the set is closed.
        /// </summary>
        internal IReadOnlyList<Task> SealRecording()
        {
            lock (_gate)
            {
                _sealed = true;
                _recordedSinceSeal = false;
                return [.. _assignmentExecutions];
            }
        }

        /// <summary>Clears the post-seal flag and returns the snapshot for the next drain pass.</summary>
        internal IReadOnlyList<Task> TakeRecordedSinceSeal()
        {
            lock (_gate)
            {
                _recordedSinceSeal = false;
                return [.. _assignmentExecutions];
            }
        }

        /// <summary>
        /// Records the REAL enclosing execution AND its reporting task, if any. Idempotent by
        /// reference.
        /// </summary>
        private void RecordCurrentExecution()
        {
            RecordOwnedTask(ExecutionProbe?.Invoke());
            RecordOwnedTask(ReportingProbe?.Invoke());
        }

        private void RecordOwnedTask(Task? owned)
        {
            if (owned is null)
                return;

            lock (_gate)
            {
                foreach (var recorded in _assignmentExecutions)
                {
                    if (ReferenceEquals(recorded, owned))
                        return;
                }

                _assignmentExecutions.Add(owned);
                if (_sealed)
                    _recordedSinceSeal = true;
            }
        }

        /// <summary>
        /// Returns the gate for <paramref name="key"/>, creating it on first use. A gate created
        /// while the TEARDOWN LATCH is set is completed AT CREATION.
        /// </summary>
        private TaskCompletionSource Slot(Dictionary<string, TaskCompletionSource> map, string key)
        {
            lock (_gate)
            {
                if (!map.TryGetValue(key, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (_teardown)
                        tcs.TrySetResult();
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

        /// <summary>
        /// Teardown failsafe: enters TEARDOWN MODE and releases every gate a parked producer could
        /// hold. The latch persists, so gates created after this sweep are released on creation.
        /// </summary>
        public void ReleaseAll()
        {
            ReleaseUnwind();
            lock (_gate)
            {
                _teardown = true;
                foreach (var tcs in _release.Values) tcs.TrySetResult();
            }
            _resetAttempted.TrySetResult();
        }

        public Task ResetAttempted() => _resetAttempted.Task;

        public void SetCurrentTaskId(string? taskId) => _taskId = taskId;

        public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
        {
            var id = _taskId ?? "(unknown)";

            // RECORD THIS INVOCATION DURABLY, at entry, so teardown joins it even when it started
            // after the sweep and the ownership slot never exposes it.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _startedBodies.Add(completion.Task);

            Slot(_started, id).TrySetResult();
            try
            {
                // RECORD THE REAL ENCLOSING EXECUTION: at prompt entry the assignment's Task.Run
                // body exists, so this captures the actual ActiveAssignment.Execution rather than
                // this prompt-return surrogate.
                RecordCurrentExecution();

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
                // Probe again on the way out: an execution installed while this prompt was parked
                // (the handler installs the owner only after Task.Run returns) is admitted here.
                RecordCurrentExecution();
                Slot(_finished, id).TrySetResult();
                completion.TrySetResult();
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
            // Record whatever execution is installed at this boundary: the reset for assignment
            // N+1 runs while assignment N may still be the installed owner.
            RecordCurrentExecution();
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
    /// A deterministic reader whose pending <c>MoveNext</c> continuation is completed SYNCHRONOUSLY
    /// (its <see cref="TaskCompletionSource{TResult}"/> is created WITHOUT
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>), so
    /// <see cref="Push"/> hands the message to a loop that is already parked on that read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS DOUBLE GUARANTEES. (1) <see cref="WaitForParkedReadCountAsync"/> gives positive
    /// evidence that the loop is parked inside <c>MoveNext</c> — the message is handed to a waiting
    /// reader, never merely enqueued. (2) <see cref="Consumed"/> fires only after the message was
    /// actually dequeued by the loop's read, so it is real read progress, not a test-side counter.
    /// (3) The pending read's completion is synchronous, so the loop's resumption is scheduled at
    /// the moment of <see cref="Push"/> rather than at some later, test-invisible time.
    /// </para>
    /// <para>
    /// WHAT THIS DOUBLE DOES **NOT** GUARANTEE — stated plainly, because the fixture must not claim
    /// an edge it does not have. Production parks on ONE owned pending read (the
    /// <c>ReadNextMessageAsync</c> task) and dispatches it after the read/readiness race resolves.
    /// Completing the reader's <c>MoveNext</c> task resumes that owned read, but the runtime is NOT
    /// required to traverse the race's <c>WhenAny</c> continuation synchronously on the pushing
    /// thread. Therefore <c>Push</c> RETURNING does not by itself prove the message's HANDLER has
    /// been entered: on a legal queued-continuation schedule the handler may start slightly later.
    /// Closing that last gap would require a production-visible signal at the drain/clear boundary,
    /// i.e. a new production seam, which is explicitly out of scope.
    /// </para>
    /// <para>
    /// HOW THE FIXTURE COMPENSATES. The replacement-ordering test never relies on "Push returned"
    /// as handler-entry evidence. Its discriminators are production-visible signals raised from
    /// INSIDE the handler (the session-reset entry gate and the prompt-entry capture) plus the
    /// ownership consequence observed after A is released, and it holds A parked until every
    /// pre-release observation has been taken. A mutant that skips the drain reaches the reset while
    /// A is still parked and is caught whenever it does so; the residual is a schedule in which the
    /// mutant's handler has not started by the time the pre-release checks run, which the
    /// post-release consequence assertions are designed to catch instead.
    /// </para>
    /// </remarks>
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

            // Deliberately NOT RunContinuationsAsynchronously: this synchronously completes the
            // underlying reader wait. The owned pending read may resume inline, but the runtime need
            // not traverse its WhenAny continuation before this call returns; the remarks above
            // state that residual explicitly.
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