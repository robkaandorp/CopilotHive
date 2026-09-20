using System.Data.Common;

using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;

using Grpc.Core;

using Google.Protobuf;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using DomainTaskMetrics = CopilotHive.Services.TaskMetrics;
using DomainWorkerRole = CopilotHive.Workers.WorkerRole;
using GrpcGitStatus = CopilotHive.Shared.Grpc.GitStatus;
using GrpcTaskComplete = CopilotHive.Shared.Grpc.TaskComplete;
using GrpcTaskMetrics = CopilotHive.Shared.Grpc.TaskMetrics;
using GrpcTaskStatus = CopilotHive.Shared.Grpc.TaskStatus;

namespace CopilotHive.Tests;

/// <summary>
/// THE READ-ONLY COMPLETION-EVIDENCE QUERY: <see cref="HiveOrchestratorService.QueryCompletionReceipt"/>,
/// invoked directly as the REAL generated override over the REAL completion-receipt stores.
/// </summary>
/// <para>
/// THE ONE QUESTION IT ANSWERS is whether EXACT durable completion evidence for a task is already
/// retained — and every vector below asserts that answer together with what the call must NOT do: no
/// registration, no stream, no ownership, readiness, queue, pipeline or notification side effect, and
/// no write of any kind.
/// </para>
/// <remarks>
/// <para>
/// IT DRIVES PRODUCTION, NOT A REIMPLEMENTATION. The service is constructed exactly as the existing
/// registration/receipt fixtures construct it (real <see cref="WorkerPool"/>, real
/// <see cref="TaskQueue"/>, real <see cref="GoalPipelineManager"/>, real dispatch graph over a
/// TEMP-rooted <see cref="BrainRepoManager"/>), the stores are the REAL insert-once
/// <see cref="CompletionReceiptStore"/> and <see cref="WorkerAssignmentContextStore"/> over a
/// hermetic in-memory SQLite database, and the recorder under the service is the REAL
/// <see cref="WorkerCompletionRecorder"/> wrapped in a thin OBSERVING FORWARDER — so every
/// confirmation genuinely reads production-written evidence, and the forwarder only counts calls and
/// can inject a refusal.
/// </para>
/// <para>
/// THE RECEIPTS ARE SEEDED WITH NO WORKER AND NO ASSIGNMENT CONTEXT — no registered worker, no queue
/// task, no assignment row and no pipeline — which is exactly the situation the query exists for:
/// the evidence alone, before anything has been reconstructed.
/// </para>
/// <para>
/// NO SLEEPS, NO AMBIENT ENVIRONMENT, NO NETWORK HOST. Every vector is synchronous and
/// deterministic; teardown disposes the fixture's database and deletes its temp root on every path.
/// </para>
/// </remarks>
public sealed class CompletionReceiptQueryTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // (1) THE ANSWER: matching evidence, absence, and genuine difference
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RICH, EXACT MATCHING EVIDENCE ANSWERS <see cref="CompletionReceiptOutcome.MatchingStored"/> —
    /// with NO registered worker, NO assignment context row and NO queue or pipeline entry, and with
    /// the evidence genuinely reaching the comparison (the mapped nested lists and the verbatim model
    /// are asserted before the answer is).
    /// </summary>
    [Fact]
    public void Query_RichMatchingEvidenceWithoutRegistration_ReturnsMatchingStored()
    {
        using var h = QueryHarness.Create();

        // The evidence really was mapped, so a "match" cannot be the product of a dropped field.
        AssertMappedEvidence(GrpcMapper.ToDomain(RichComplete()), RichResult());

        h.AssertNoOwnershipState();
        Assert.Equal(0L, h.Database.ReceiptRowCount(TaskId));

        h.SeedReceiptFor(RichResult());

        // THE PREMISE: the evidence is retained, but nothing is registered and nothing is assigned.
        Assert.Null(h.Pool.GetWorker(WorkerId));
        Assert.Null(h.Queue.GetActiveTask(TaskId));
        Assert.Equal(0L, h.Database.AssignmentRowCount);
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));

        var response = h.Query(WorkerId, RichComplete());

        Assert.Equal(CompletionReceiptOutcome.MatchingStored, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);

        // THE ONE ROW IS STILL THE SEEDED ONE, and no assignment context was invented.
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(0L, h.Database.AssignmentRowCount);

        // …and the query opened no stream and touched no ownership state.
        h.AssertNoOwnershipState();
    }

    /// <summary>
    /// ABSENCE OF ANY RETAINED EVIDENCE ANSWERS <see cref="CompletionReceiptOutcome.NotConfirmed"/>:
    /// absence is never a confirmation, and it is deliberately indistinguishable from a difference.
    /// </summary>
    [Fact]
    public void Query_AbsentEvidence_ReturnsNotConfirmed()
    {
        using var h = QueryHarness.Create();

        Assert.Equal(0L, h.Database.ReceiptRowCount(TaskId));

        var response = h.Query(WorkerId, RichComplete());

        Assert.Equal(CompletionReceiptOutcome.NotConfirmed, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0L, h.Database.ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// A CASE-VARIANT WORKER, A DIFFERENT WORKER AND A CASE-VARIANT TASK ARE ALL
    /// <see cref="CompletionReceiptOutcome.NotConfirmed"/>: identity is compared ORDINALLY and is
    /// never trimmed, normalized or case-folded, and a case-variant task id is the OPAQUE KEY that
    /// finds the retained row of a different task (or none at all).
    /// </summary>
    [Theory]
    [InlineData("worker-case")]
    [InlineData("worker-other")]
    [InlineData("task-case")]
    public void Query_OrdinalIdentityVariants_ReturnNotConfirmed(string cell)
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        var workerId = WorkerId;
        var complete = RichComplete();

        switch (cell)
        {
            case "worker-case":
                workerId = WorkerId.ToUpperInvariant();
                break;

            case "worker-other":
                workerId = "worker-somewhere-else";
                break;

            case "task-case":
                // The row exists, but under the ORDINAL task id — so the lookup key does not find it.
                complete.TaskId = TaskId.ToUpperInvariant();
                break;

            default:
                throw new InvalidOperationException($"Unknown identity cell '{cell}'.");
        }

        var response = h.Query(workerId, complete);

        Assert.Equal(CompletionReceiptOutcome.NotConfirmed, response.Outcome);

        // THE RETAINED EVIDENCE IS UNTOUCHED by the non-match.
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(CanonicalRich(), h.Database.RawPayload(TaskId));
    }

    /// <summary>
    /// A REPRESENTABLE DIFFERENCE IN ANY EVIDENCE — the output, the model, the iteration-start SHA,
    /// and the NESTED metrics and git status (values, an ADDITIONAL list member and a REORDERED
    /// list) — is <see cref="CompletionReceiptOutcome.NotConfirmed"/>, exactly like absence, and is
    /// never dressed up as a failure or a confirmation.
    /// </summary>
    /// <remarks>
    /// THE REORDERED AND EXTRA LIST CELLS ARE THE ONES THAT PROVE THE NESTED EVIDENCE REALLY REACHES
    /// THE COMPARISON: a comparison that ignored list order, or dropped a member, would answer
    /// <see cref="CompletionReceiptOutcome.MatchingStored"/> for them.
    /// </remarks>
    [Theory]
    [InlineData("output")]
    [InlineData("model")]
    [InlineData("sha")]
    [InlineData("metrics-verdict")]
    [InlineData("metrics-coverage")]
    [InlineData("metrics-issues-extra")]
    [InlineData("metrics-issues-reordered")]
    [InlineData("metrics-summary")]
    [InlineData("git-files-changed")]
    [InlineData("git-changed-files-extra")]
    [InlineData("git-changed-files-reordered")]
    [InlineData("git-pushed")]
    public void Query_ChangedEvidence_ReturnsNotConfirmed(string cell)
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        var complete = RichComplete();
        switch (cell)
        {
            case "output":
                complete.Output = "a-different-output";
                break;

            case "model":
                complete.Model = "model/verbatim";
                break;

            case "sha":
                complete.IterationStartSha = "sha-other";
                break;

            case "metrics-verdict":
                complete.Metrics.Verdict = "FAIL";
                break;

            case "metrics-coverage":
                complete.Metrics.CoveragePercent = 88.6;
                break;

            case "metrics-issues-extra":
                complete.Metrics.Issues.Add("issue-c");
                break;

            case "metrics-issues-reordered":
                complete.Metrics.Issues.Clear();
                complete.Metrics.Issues.AddRange([RichIssueB, RichIssueA]);
                break;

            case "metrics-summary":
                complete.Metrics.Summary = "a-different-summary";
                break;

            case "git-files-changed":
                complete.GitStatus.FilesChanged = 5;
                break;

            case "git-changed-files-extra":
                complete.GitStatus.ChangedFiles.Add("src/c.cs");
                break;

            case "git-changed-files-reordered":
                complete.GitStatus.ChangedFiles.Clear();
                complete.GitStatus.ChangedFiles.AddRange([RichPathB, RichPathA]);
                break;

            case "git-pushed":
                complete.GitStatus.Pushed = false;
                break;

            default:
                throw new InvalidOperationException($"Unknown difference cell '{cell}'.");
        }

        // THE VECTOR'S OWN PREMISE: the difference really is a difference.
        Assert.NotEqual(
            CanonicalRich(),
            CanonicalFor(GrpcMapper.ToDomain(complete)));

        var response = h.Query(WorkerId, complete);

        Assert.Equal(CompletionReceiptOutcome.NotConfirmed, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);

        // The retained bytes are never touched by a non-match.
        Assert.Equal(CanonicalRich(), h.Database.RawPayload(TaskId));
    }

    /// <summary>
    /// MODEL PRESENCE IS OBSERVED, NOT INFERRED. A PRESENT-EMPTY model matches a receipt whose
    /// retained model is empty (presence is not part of the compared evidence, and neither value is
    /// substituted), while the SAME evidence with the field ABSENT is rejected as
    /// <see cref="CompletionReceiptOutcome.InvalidRequest"/> before any comparison.
    /// </summary>
    [Fact]
    public void Query_PresentEmptyModelMatchesEmptyModelReceipt_WhileAbsentModelIsInvalidRequest()
    {
        using var h = QueryHarness.Create();

        var complete = RichComplete();
        complete.Model = "";
        Assert.True(complete.HasModel, "assigning an empty model must still set protobuf presence.");

        // The seeded evidence carries exactly that empty model.
        var emptyModelResult = RichResult() with { Model = "" };
        h.SeedReceiptFor(emptyModelResult);

        Assert.Equal(CompletionReceiptOutcome.MatchingStored, h.Query(WorkerId, complete).Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);

        // THE CONTRAST: the same evidence with the field ABSENT is a rejected request shape, and the
        // recorder is never consulted for it.
        var absentModel = AbsentModelComplete();
        Assert.False(absentModel.HasModel);

        var response = h.Query(WorkerId, absentModel);

        Assert.Equal(CompletionReceiptOutcome.InvalidRequest, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) REJECTED REQUEST SHAPES, MISSING RECORDER, UNREADABLE EVIDENCE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY REJECTED REQUEST SHAPE — an empty/whitespace worker id, a missing completion, an
    /// empty/whitespace task id, absent model presence, a non-terminal status and an UNKNOWN NUMERIC
    /// status — answers
    /// <see cref="CompletionReceiptOutcome.InvalidRequest"/> and NEVER invokes the recorder, so the
    /// rejection genuinely precedes the comparison.
    /// </summary>
    [Theory]
    [InlineData("blank-worker")]
    [InlineData("whitespace-worker")]
    [InlineData("null-completion")]
    [InlineData("empty-task")]
    [InlineData("whitespace-task")]
    [InlineData("absent-model")]
    [InlineData("unspecified-status")]
    [InlineData("in-progress-status")]
    [InlineData("unknown-numeric-status")]
    public void Query_RejectedRequestShapes_ReturnInvalidRequestWithoutInvokingTheRecorder(string cell)
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        var workerId = WorkerId;
        GrpcTaskComplete? complete = RichComplete();

        switch (cell)
        {
            case "blank-worker":
                workerId = "";
                break;

            case "whitespace-worker":
                workerId = " \t ";
                break;

            case "null-completion":
                complete = null;
                break;

            case "empty-task":
                complete!.TaskId = "";
                break;

            case "whitespace-task":
                complete!.TaskId = " \t ";
                break;

            case "absent-model":
                complete = AbsentModelComplete();
                Assert.False(complete.HasModel);
                break;

            case "unspecified-status":
                complete!.Status = GrpcTaskStatus.Unspecified;
                break;

            case "in-progress-status":
                complete!.Status = GrpcTaskStatus.InProgress;
                break;

            case "unknown-numeric-status":
                complete!.Status = (GrpcTaskStatus)99;
                break;

            default:
                throw new InvalidOperationException($"Unknown rejected-request cell '{cell}'.");
        }

        var response = h.Query(workerId, complete);

        Assert.Equal(CompletionReceiptOutcome.InvalidRequest, response.Outcome);

        // THE RECORDER WAS NEVER REACHED — the rejection is a request-shape decision, not a read.
        Assert.Equal(0, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);

        // The matching evidence is still there, untouched.
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// A VALID REQUEST WITH NO RECORDER CONFIGURED ANSWERS
    /// <see cref="CompletionReceiptOutcome.Unavailable"/>: never a confirmation, never
    /// <see cref="CompletionReceiptOutcome.NotConfirmed"/>, and no fall-through to any other path.
    /// </summary>
    [Fact]
    public void Query_WithoutARecorder_ReturnsUnavailable()
    {
        using var h = QueryHarness.Create(withRecorder: false);
        h.SeedReceiptFor(RichResult());

        var response = h.Query(WorkerId, RichComplete());

        Assert.Equal(CompletionReceiptOutcome.Unavailable, response.Outcome);

        // The evidence is intact and the response exposes NOTHING about it beyond the single outcome.
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(0, h.Recorder.ConfirmCalls);
    }

    /// <summary>
    /// GENUINELY CORRUPT RETAINED EVIDENCE — a malformed payload written by raw SQL — answers
    /// <see cref="CompletionReceiptOutcome.Unavailable"/>, because the comparison could not be
    /// performed. It is never absence, never a difference, and the row is left byte-identical.
    /// </summary>
    [Fact]
    public void Query_CorruptRetainedPayload_ReturnsUnavailableAndLeavesTheRow()
    {
        using var h = QueryHarness.Create();

        const string corruptPayload = """{"version":1}""";
        h.Database.ExecuteRaw(
            "INSERT INTO completion_receipts (task_id, goal_id, payload_json, first_stored_at_utc) " +
            $"VALUES ('{ReceiptQueryDatabase.Escape(TaskId)}', '{ReceiptQueryDatabase.Escape(GoalId)}', " +
            $"'{ReceiptQueryDatabase.Escape(corruptPayload)}', '2024-01-02T03:04:05.0000000Z')");

        Assert.Equal(corruptPayload, h.Database.RawPayload(TaskId));

        var response = h.Query(WorkerId, RichComplete());

        Assert.Equal(CompletionReceiptOutcome.Unavailable, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);

        // THE ROW IS NOT REPAIRED, REPLACED OR DELETED by an unreadable evidence row.
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(corruptPayload, h.Database.RawPayload(TaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) THE RECORDER BOUNDARY: exactly one consultation, no recording
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE RECORDER IS CONSULTED EXACTLY ONCE AND THE RECORDING OPERATION IS NEVER INVOKED, and the
    /// evidence handed to the confirmation is the SUPPLIED completion's own mapped evidence — the
    /// verbatim model included, with no queue, worker-current-model or stored-row substitution.
    /// </summary>
    [Fact]
    public void Query_ConsultsTheRecorderExactlyOnce_AndNeverRecords()
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        var complete = RichComplete();
        complete.Model = "model/verbatim  ";

        var response = h.Query(WorkerId, complete);

        Assert.Equal(CompletionReceiptOutcome.MatchingStored, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);

        Assert.Equal(WorkerId, h.Recorder.LastWorkerId);
        Assert.Equal(TaskId, h.Recorder.LastTaskId);
        Assert.NotNull(h.Recorder.LastResult);
        Assert.Equal("model/verbatim  ", h.Recorder.LastResult!.Model);
        Assert.Equal("rich output", h.Recorder.LastResult.Output);
        Assert.Equal(TaskOutcome.Completed, h.Recorder.LastResult.Status);
    }

    /// <summary>
    /// A RECORDER REFUSAL IS MAPPED BY ITS OWN REASON: an
    /// <see cref="WorkerCompletionRecordingFailureReason.InvalidContext"/> refusal is a
    /// request-shaped <see cref="CompletionReceiptOutcome.InvalidRequest"/>, while a store failure
    /// and EVERY other refusal a substitute implementation may raise (Conflict, Indeterminate,
    /// MissingRecorder and an unrelated exception) are
    /// <see cref="CompletionReceiptOutcome.Unavailable"/> — never a confirmation.
    /// </summary>
    [Theory]
    [InlineData("invalid-context", CompletionReceiptOutcome.InvalidRequest)]
    [InlineData("store-error", CompletionReceiptOutcome.Unavailable)]
    [InlineData("conflict", CompletionReceiptOutcome.Unavailable)]
    [InlineData("indeterminate", CompletionReceiptOutcome.Unavailable)]
    [InlineData("missing-recorder", CompletionReceiptOutcome.Unavailable)]
    [InlineData("unexpected", CompletionReceiptOutcome.Unavailable)]
    public void Query_RecorderRefusals_MapToInvalidRequestOrUnavailable(
        string cell, CompletionReceiptOutcome expected)
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        h.Recorder.ConfirmFault = cell switch
        {
            "invalid-context" => () => throw WorkerCompletionRecordingException.InvalidContext(
                "query invalid-context sentinel"),
            "store-error" => () => throw WorkerCompletionRecordingException.StoreError(
                "query store-error sentinel", new InvalidOperationException("query store sentinel")),
            "conflict" => () => throw WorkerCompletionRecordingException.Conflict(
                "query conflict sentinel"),
            "indeterminate" => () => throw WorkerCompletionRecordingException.Indeterminate(
                "query indeterminate sentinel", new InvalidOperationException("query write sentinel")),
            "missing-recorder" => () => throw WorkerCompletionRecordingException.MissingRecorder(),
            "unexpected" => () => throw new InvalidOperationException("query unexpected sentinel"),
            _ => throw new InvalidOperationException($"Unknown refusal cell '{cell}'."),
        };

        var response = h.Query(WorkerId, RichComplete());

        Assert.Equal(expected, response.Outcome);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);

        // THE ROW IS UNTOUCHED BY A REFUSED COMPARISON, and no assignment context is invented.
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(0L, h.Database.AssignmentRowCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) CALLER CANCELLATION IS NEVER AN OUTCOME
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AN ALREADY-CANCELLED CALLER TOKEN CANCELS THE RPC AND THE RECORDER IS INVOKED ZERO TIMES: the
    /// token is observed BEFORE any validation, mapping or recorder call, so cancellation is never
    /// swallowed into <see cref="CompletionReceiptOutcome.Unavailable"/> and never produces an
    /// outcome at all.
    /// </summary>
    [Fact]
    public void Query_PreCancelledToken_CancelsWithoutInvokingTheRecorder()
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = new CancellationServerCallContext(cts.Token);

        // The RPC is invoked SYNCHRONOUSLY (the override is non-async), so this is a plain void call.
        Action invoke = () => h.Service
            .QueryCompletionReceipt(QueryRequest(WorkerId, RichComplete()), context)
            .GetAwaiter().GetResult();

        Assert.ThrowsAny<OperationCanceledException>(invoke);

        Assert.Equal(0, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
    }

    /// <summary>
    /// A TOKEN CANCELLED WHILE THE READ WAS IN FLIGHT CANCELS THE RPC INSTEAD OF PRODUCING AN
    /// OUTCOME: the read really happened (the recorder WAS consulted, and the confirmation it
    /// returned was positive), yet the second cancellation observation turns that positive read into
    /// a cancellation — so a cancelled call never returns
    /// <see cref="CompletionReceiptOutcome.MatchingStored"/>.
    /// </summary>
    [Fact]
    public void Query_TokenCancelledDuringTheRead_CancelsInsteadOfReturningAnOutcome()
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        using var cts = new CancellationTokenSource();
        var context = new CancellationServerCallContext(cts.Token);

        // THE CANCELLATION LANDS INSIDE THE READ: the real confirmation has already returned
        // positively by the time the second observation runs.
        h.Recorder.OnConfirmSettled = cts.Cancel;

        Action invoke = () => h.Service
            .QueryCompletionReceipt(QueryRequest(WorkerId, RichComplete()), context)
            .GetAwaiter().GetResult();

        Assert.ThrowsAny<OperationCanceledException>(invoke);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);
    }

    /// <summary>
    /// A TOKEN CANCELLED WHILE THE READ WAS IN FLIGHT CANCELS THE RPC EVEN WHEN THAT READ THEN
    /// THROWS A MAPPED FAILURE: the read really happened and produced a store failure, yet the caller
    /// gets a cancellation rather than <see cref="CompletionReceiptOutcome.Unavailable"/>.
    /// </summary>
    /// <remarks>
    /// THIS IS THE DISCRIMINATING VECTOR FOR THE FAILURE RETURN PATHS. A failure branch that built its
    /// response directly — without observing the caller's token — would return
    /// <see cref="CompletionReceiptOutcome.Unavailable"/> here and this vector would fail, which the
    /// success-path cancellation vectors cannot detect because the read never fails there.
    /// </remarks>
    [Theory]
    [InlineData("store-error")]
    [InlineData("invalid-context")]
    public void Query_TokenCancelledDuringAFailingRead_CancelsInsteadOfReturningMappedOutcome(string cell)
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        using var cts = new CancellationTokenSource();
        var context = new CancellationServerCallContext(cts.Token);

        // THE CANCELLATION LANDS FIRST, THEN THE READ FAILS: each mapped failure is produced AFTER the
        // caller token is already cancelled, which is exactly the window the failure returns used to
        // swallow. Without the final response check these cells return Unavailable and InvalidRequest.
        var failure = cell switch
        {
            "store-error" => WorkerCompletionRecordingException.StoreError(
                "query failing-read store sentinel", new InvalidOperationException("query store sentinel")),
            "invalid-context" => WorkerCompletionRecordingException.InvalidContext(
                "query failing-read invalid-context sentinel"),
            _ => throw new InvalidOperationException($"Unknown failing-read cell '{cell}'."),
        };
        h.Recorder.ConfirmFault = () =>
        {
            cts.Cancel();
            throw failure;
        };

        Action invoke = () => h.Service
            .QueryCompletionReceipt(QueryRequest(WorkerId, RichComplete()), context)
            .GetAwaiter().GetResult();

        // NO RESPONSE IS PRODUCED — specifically neither mapped Unavailable nor InvalidRequest: the
        // caller abandoned the call, so the exact caller-token cancellation wins.
        var thrown = Assert.ThrowsAny<OperationCanceledException>(invoke);
        Assert.Equal(cts.Token, thrown.CancellationToken);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);
    }

    /// <summary>
    /// A RECORDER-THROWN <see cref="OperationCanceledException"/> PROPAGATES AS ITS EXACT INSTANCE,
    /// WITH ITS ORIGINAL TOKEN — it is never classified as
    /// <see cref="CompletionReceiptOutcome.Unavailable"/>, and no foreign same-type exception is ever
    /// substituted for it.
    /// </summary>
    /// <remarks>
    /// THE CALLER'S TOKEN IS DELIBERATELY LEFT UNCANCELLED, so the final cancellation check passes and
    /// the ONLY thing that can produce a cancellation here is the recorder's own exception. A method
    /// without the explicit, FIRST-ORDERED `catch (OperationCanceledException) { throw; }` would have
    /// its generic catch classify this as Unavailable and return normally — failing the identity
    /// assertion — while a substituted/re-wrapped cancellation would fail <see cref="Assert.Same"/> and
    /// the token comparison.
    /// </remarks>
    [Fact]
    public void Query_ReadThrowsCancellation_PropagatesTheExactInstanceAndToken()
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        using var callerCts = new CancellationTokenSource();

        // The sentinel carries THE CALLER TOKEN but does not cancel it. Therefore the method's initial
        // and final token checks both pass; only an exact `throw;` from the recorder catch can produce
        // this instance. A newly-created or foreign same-type exception cannot satisfy either assertion.
        var sentinel = new OperationCanceledException("recorder cancellation sentinel", callerCts.Token);
        h.Recorder.ConfirmFault = () => throw sentinel;

        var context = new CancellationServerCallContext(callerCts.Token);

        Action invoke = () => h.Service
            .QueryCompletionReceipt(QueryRequest(WorkerId, RichComplete()), context)
            .GetAwaiter().GetResult();

        var thrown = Assert.ThrowsAny<OperationCanceledException>(invoke);

        // THE EXACT INSTANCE AND THE CALLER'S ORIGINAL TOKEN — nothing was mapped, wrapped or replaced.
        Assert.Same(sentinel, thrown);
        Assert.Equal(callerCts.Token, thrown.CancellationToken);
        Assert.False(callerCts.IsCancellationRequested);
        Assert.Equal(1, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);
    }

    /// <summary>
    /// EVERY PRE-RECORDER RESPONSE ALSO PASSES THROUGH THE FINAL CANCELLATION CHECK. A context that
    /// exposes a live token to the initial check and a cancelled token on the next access cancels both
    /// an InvalidRequest branch and the missing-recorder Unavailable branch instead of returning either
    /// outcome.
    /// </summary>
    /// <remarks>
    /// THIS KILLS A DIRECT EARLY RETURN. The token is demonstrably not cancelled at method entry and
    /// the recorder counter remains zero; the second token access can only come from the common final
    /// response path. Returning either response directly would make Assert.ThrowsAny fail.
    /// </remarks>
    [Theory]
    [InlineData("invalid-request")]
    [InlineData("missing-recorder")]
    public void Query_TokenCancelledBeforePreRecorderResponse_CancelsInsteadOfReturningOutcome(string cell)
    {
        using var h = QueryHarness.Create(withRecorder: cell != "missing-recorder");
        h.SeedReceiptFor(RichResult());

        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        var tokenAccesses = 0;
        var context = new CancellationServerCallContext(() =>
            Interlocked.Increment(ref tokenAccesses) == 1
                ? CancellationToken.None
                : cancelledCts.Token);

        var request = cell switch
        {
            "invalid-request" => QueryRequest("   ", RichComplete()),
            "missing-recorder" => QueryRequest(WorkerId, RichComplete()),
            _ => throw new InvalidOperationException($"Unknown pre-recorder response cell '{cell}'."),
        };

        Action invoke = () => h.Service
            .QueryCompletionReceipt(request, context)
            .GetAwaiter().GetResult();

        // NO InvalidRequest or Unavailable response escapes: the second token access cancels first.
        var thrown = Assert.ThrowsAny<OperationCanceledException>(invoke);
        Assert.Equal(cancelledCts.Token, thrown.CancellationToken);
        Assert.Equal(2, tokenAccesses);
        Assert.Equal(0, h.Recorder.ConfirmCalls);
        Assert.Equal(0, h.Recorder.RecordCalls);
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) NO SIDE EFFECTS: no writes, no storage change, no ownership change
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// REPEATED QUERIES CHANGE NOTHING IN DURABLE STORAGE: the raw payload bytes, the first-stored
    /// timestamp and the row count are byte-for-byte identical, and the EF command interceptor
    /// observes ZERO non-query (write) commands — not merely a re-read of the rows.
    /// </summary>
    /// <remarks>
    /// THE INTERCEPTOR IS A POSITIVE-CONTROL-CAPABLE OBSERVER: after the query assertions, a REAL
    /// store insert for a second task is performed and the counter MOVES, so "zero writes" is a fact
    /// the observer can refute rather than an observer that never fires.
    /// </remarks>
    [Fact]
    public void Query_RepeatedQueries_WriteNothingAndChangeNoStoredBytesOrTimestamps()
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        var payloadBefore = h.Database.RawPayload(TaskId);
        var firstStoredBefore = h.Database.RawFirstStoredText(TaskId);
        Assert.NotNull(payloadBefore);
        Assert.NotNull(firstStoredBefore);

        var writesBefore = h.Database.Interceptor.NonQueryAttempts;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(CompletionReceiptOutcome.MatchingStored, h.Query(WorkerId, RichComplete()).Outcome);
            Assert.Equal(CompletionReceiptOutcome.NotConfirmed, h.Query("worker-somewhere-else", RichComplete()).Outcome);
        }

        Assert.Equal(writesBefore, h.Database.Interceptor.NonQueryAttempts);
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(payloadBefore, h.Database.RawPayload(TaskId));
        Assert.Equal(firstStoredBefore, h.Database.RawFirstStoredText(TaskId));
        Assert.Equal(0L, h.Database.AssignmentRowCount);

        // ── THE POSITIVE CONTROL: the observer really can see a write ────────────────────────
        h.SeedReceiptFor(RichResult() with { TaskId = OtherTaskId }, taskId: OtherTaskId);
        Assert.True(
            h.Database.Interceptor.NonQueryAttempts > writesBefore,
            "the write observer must fire for a real store insert, otherwise 'zero writes' proves nothing.");

        // …and the FIRST row is still exactly as it was.
        Assert.Equal(payloadBefore, h.Database.RawPayload(TaskId));
        Assert.Equal(firstStoredBefore, h.Database.RawFirstStoredText(TaskId));
    }

    /// <summary>
    /// A POSITIVE NO-SIDE-EFFECT WITNESS: with a POPULATED live worker (registered, busy with an
    /// assigned task, carrying its model) plus a queued pending task, the queries answer from the
    /// evidence alone while the registered instance, its busy/current-task state, its stream
    /// attachment, the queue contents, the retained rows, the assignment rows and every notification
    /// counter are IDENTICAL afterwards.
    /// </summary>
    [Fact]
    public void Query_WithLiveWorkerAndQueuedWork_ChangesNoOwnershipQueueOrRegistryState()
    {
        using var h = QueryHarness.Create();
        h.SeedReceiptFor(RichResult());

        // ── THE LIVE STATE: one registered worker genuinely owning an assigned task ─────────
        const string activeTaskId = "task-live-active";
        const string pendingTaskId = "task-live-pending";

        var worker = h.Pool.RegisterWorker(LiveWorkerId, ["dotnet"]);
        h.Queue.Enqueue(BuildTask(activeTaskId));
        var dequeued = h.Queue.TryDequeue(DomainWorkerRole.Unspecified);
        Assert.NotNull(dequeued);
        Assert.True(h.Service.ApplyTaskAssignment(worker, dequeued!));

        h.Queue.Enqueue(BuildTask(pendingTaskId));

        Assert.True(worker.IsBusy);
        Assert.Equal(activeTaskId, worker.CurrentTaskId);
        Assert.Equal("live-model", worker.CurrentModel);
        Assert.False(worker.IsWorkStreamAttached);

        // The setup's own notifications are excluded from the observation below.
        h.ResetNotificationCounters();

        var response = h.Query(WorkerId, RichComplete());
        Assert.Equal(CompletionReceiptOutcome.MatchingStored, response.Outcome);
        Assert.Equal(CompletionReceiptOutcome.NotConfirmed, h.Query(WorkerId, RichCompleteOf(OtherTaskId)).Outcome);

        // ── NOTHING ABOUT THE LIVE STATE MOVED ───────────────────────────────────────────────
        Assert.Same(worker, h.Pool.GetWorker(LiveWorkerId));
        Assert.True(worker.IsBusy);
        Assert.Equal(activeTaskId, worker.CurrentTaskId);
        Assert.Equal("live-model", worker.CurrentModel);
        Assert.False(worker.IsWorkStreamAttached);

        Assert.True(h.Pool.TryGetWorkerSnapshot(LiveWorkerId, out var snapshot));
        Assert.Same(worker, snapshot.Worker);
        Assert.True(snapshot.IsBusy);
        Assert.Equal(activeTaskId, snapshot.CurrentTaskId);

        // The ACTIVE entry is the very same task instance …
        Assert.Same(dequeued, h.Queue.GetActiveTask(activeTaskId));

        // … and the PENDING queue still holds exactly the one pending task, in the same order.
        var pending = h.Queue.TryDequeueAny();
        Assert.NotNull(pending);
        Assert.Equal(pendingTaskId, pending!.TaskId);
        Assert.Null(h.Queue.TryDequeueAny());

        // The fixture is restored before teardown.
        h.Queue.Enqueue(pending);

        Assert.Equal(0, h.DashboardNotifications);
        Assert.Equal(0, h.CompletionNotifications);
        Assert.Equal(1L, h.Database.ReceiptRowCount(TaskId));
        Assert.Equal(0L, h.Database.AssignmentRowCount);
        Assert.Null(h.Database.RawPayload(OtherTaskId));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (6) THE WIRE CONTRACT OF THE ADDITIVE REQUEST/RESPONSE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ADDITIVE WIRE SHAPE: the request and response use their documented tag numbers, the five
    /// outcomes keep their documented numeric values, request and response messages round-trip through
    /// protobuf, the request preserves worker id, task id, EXPLICIT model presence and terminal status,
    /// and an untouched response is <see cref="CompletionReceiptOutcome.Unspecified"/>.
    /// </summary>
    [Fact]
    public void WireContract_RequestRoundTripsAndUntouchedResponseIsUnspecified()
    {
        Assert.Equal(1, CompletionReceiptQueryRequest.WorkerIdFieldNumber);
        Assert.Equal(2, CompletionReceiptQueryRequest.CompletionFieldNumber);
        Assert.Equal(1, CompletionReceiptQueryResponse.OutcomeFieldNumber);

        Assert.Equal(0, (int)CompletionReceiptOutcome.Unspecified);
        Assert.Equal(1, (int)CompletionReceiptOutcome.MatchingStored);
        Assert.Equal(2, (int)CompletionReceiptOutcome.NotConfirmed);
        Assert.Equal(3, (int)CompletionReceiptOutcome.InvalidRequest);
        Assert.Equal(4, (int)CompletionReceiptOutcome.Unavailable);

        Assert.Equal(CompletionReceiptOutcome.Unspecified, new CompletionReceiptQueryResponse().Outcome);

        var complete = RichComplete();
        complete.TaskId = "task-roundtrip";
        complete.Model = "";
        Assert.True(complete.HasModel);

        var request = new CompletionReceiptQueryRequest
        {
            WorkerId = "worker-roundtrip",
            Completion = complete,
        };

        var parsed = CompletionReceiptQueryRequest.Parser.ParseFrom(request.ToByteArray());

        Assert.Equal("worker-roundtrip", parsed.WorkerId);
        Assert.Equal("task-roundtrip", parsed.Completion.TaskId);
        Assert.True(parsed.Completion.HasModel);
        Assert.Equal("", parsed.Completion.Model);
        Assert.Equal(GrpcTaskStatus.Completed, parsed.Completion.Status);
        Assert.Equal("rich output", parsed.Completion.Output);
        Assert.Equal(2, parsed.Completion.Metrics.Issues.Count);
        Assert.Equal(2, parsed.Completion.GitStatus.ChangedFiles.Count);

        var response = new CompletionReceiptQueryResponse
        {
            Outcome = CompletionReceiptOutcome.MatchingStored,
        };
        var parsedResponse = CompletionReceiptQueryResponse.Parser.ParseFrom(response.ToByteArray());
        Assert.Equal(CompletionReceiptOutcome.MatchingStored, parsedResponse.Outcome);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // the subject's inputs
    // ═══════════════════════════════════════════════════════════════════════

    private const string WorkerId = "worker-query";

    private const string LiveWorkerId = "worker-query-live";

    private const string GoalId = "goal-query";

    /// <summary>An OPAQUE task id: no parseable structure, trailing whitespace included.</summary>
    private const string TaskId = "ord/0007:3  ";

    private const string OtherTaskId = "ord/0008:4";

    private const string RichModel = "model/verbatim  ";

    private const string RichOutput = "rich output";

    private const string RichSha = "sha-abc123";

    private const string RichIssueA = "issue-a";

    private const string RichIssueB = "issue-b";

    private const string RichPathA = "src/a.cs";

    private const string RichPathB = "src/b.cs";

    private static GrpcTaskComplete RichComplete() => RichCompleteOf(TaskId);

    /// <summary>
    /// The SAME rich evidence with field 7 NEVER ASSIGNED, so proto3 presence is genuinely unset (a
    /// legacy sender). Every other field is identical to <see cref="RichComplete"/>, which makes the
    /// absent-model vector a pure test of presence rather than of any other evidence.
    /// </summary>
    private static GrpcTaskComplete AbsentModelComplete()
    {
        var complete = RichComplete();
        var absent = new GrpcTaskComplete
        {
            TaskId = complete.TaskId,
            Status = complete.Status,
            Output = complete.Output,
            IterationStartSha = complete.IterationStartSha,
            Metrics = complete.Metrics,
            GitStatus = complete.GitStatus,
        };

        Assert.False(absent.HasModel);
        return absent;
    }

    /// <summary>
    /// The rich wire completion for a task id. Every field the codec compares is populated, and the
    /// two evidence LISTS carry two members each so a REORDERED list is a genuine difference rather
    /// than a single-element list that cannot be reordered.
    /// </summary>
    private static GrpcTaskComplete RichCompleteOf(string taskId)
    {
        var complete = new GrpcTaskComplete
        {
            TaskId = taskId,
            Status = GrpcTaskStatus.Completed,
            Output = RichOutput,
            IterationStartSha = RichSha,
            // ASSIGNING the field sets proto3 presence, which is what the query requires.
            Model = RichModel,
            Metrics = new GrpcTaskMetrics
            {
                Verdict = "PASS",
                BuildSuccess = true,
                TotalTests = 11,
                PassedTests = 10,
                FailedTests = 1,
                CoveragePercent = 88.5,
                Summary = "rich summary",
            },
            GitStatus = new GrpcGitStatus
            {
                CurrentBranch = "feature/query",
                LastCommitSha = "deadbeef",
                LastCommitMessage = "rich commit",
                FilesChanged = 4,
                Insertions = 40,
                Deletions = 2,
                Pushed = true,
            },
        };

        complete.Metrics.Issues.AddRange([RichIssueA, RichIssueB]);
        complete.GitStatus.ChangedFiles.AddRange([RichPathA, RichPathB]);

        Assert.True(complete.HasModel);
        return complete;
    }

    /// <summary>
    /// THE DOMAIN EVIDENCE the rich wire completion maps to, and the evidence the seeded receipt
    /// carries. It is written out explicitly so the vectors assert a REAL mapping rather than
    /// whatever the mapper happens to produce.
    /// </summary>
    private static TaskResult RichResult() => new()
    {
        TaskId = TaskId,
        Status = TaskOutcome.Completed,
        Output = RichOutput,
        Model = RichModel,
        IterationStartSha = RichSha,
        Metrics = new DomainTaskMetrics
        {
            Verdict = "PASS",
            BuildSuccess = true,
            TotalTests = 11,
            PassedTests = 10,
            FailedTests = 1,
            CoveragePercent = 88.5,
            Issues = [RichIssueA, RichIssueB],
            Summary = "rich summary",
        },
        GitStatus = new GitChangeSummary
        {
            FilesChanged = 4,
            Insertions = 40,
            Deletions = 2,
            Pushed = true,
            ChangedFiles = [RichPathA, RichPathB],
        },
    };

    /// <summary>
    /// THE AUTHORITATIVE SLOT: a repeated phase position with a non-first attempt, so evidence that
    /// reconstructed its position from the task id (or defaulted it) could not reproduce it.
    /// </summary>
    private static WorkSlot RichSlot() =>
        new(TaskId, new WorkSlotPosition(2, GoalPhase.Coding, 2), 3);

    /// <summary>
    /// The canonical text of the receipt the seeded retained evidence produces under the RETAINED
    /// identity (the stored goal/worker/role and the FULL stored slot) — the vector's own premise of
    /// what is on the row, read through the EXISTING codec rather than a second serialization.
    /// </summary>
    /// <returns>The canonical payload text.</returns>
    private static string CanonicalRich() => CanonicalFor(RichResult());

    /// <summary>The canonical text a given mapped result would produce under the retained identity.</summary>
    /// <param name="result">The mapped domain result.</param>
    /// <returns>The canonical payload text.</returns>
    private static string CanonicalFor(TaskResult result) =>
        CompletionReceiptCodec.Encode(
            new CompletionReceipt(GoalId, WorkerId, DomainWorkerRole.Coder, RichSlot(), result));

    /// <summary>Asserts the boundary mapper preserves EVERY piece of the rich evidence.</summary>
    /// <param name="mapped">The mapped domain result.</param>
    /// <param name="expected">The explicitly written expected evidence.</param>
    private static void AssertMappedEvidence(TaskResult mapped, TaskResult expected)
    {
        Assert.Equal(expected.TaskId, mapped.TaskId);
        Assert.Equal(expected.Status, mapped.Status);
        Assert.Equal(expected.Output, mapped.Output);
        Assert.Equal(expected.Model, mapped.Model);
        Assert.Equal(expected.IterationStartSha, mapped.IterationStartSha);

        Assert.NotNull(mapped.Metrics);
        Assert.Equal(expected.Metrics!.Verdict, mapped.Metrics.Verdict);
        Assert.Equal(expected.Metrics.BuildSuccess, mapped.Metrics.BuildSuccess);
        Assert.Equal(expected.Metrics.TotalTests, mapped.Metrics.TotalTests);
        Assert.Equal(expected.Metrics.PassedTests, mapped.Metrics.PassedTests);
        Assert.Equal(expected.Metrics.FailedTests, mapped.Metrics.FailedTests);
        Assert.Equal(expected.Metrics.CoveragePercent, mapped.Metrics.CoveragePercent);
        Assert.Equal(expected.Metrics.Issues, mapped.Metrics.Issues);
        Assert.Equal(expected.Metrics.Summary, mapped.Metrics.Summary);

        Assert.NotNull(mapped.GitStatus);
        Assert.Equal(expected.GitStatus!.FilesChanged, mapped.GitStatus.FilesChanged);
        Assert.Equal(expected.GitStatus.Insertions, mapped.GitStatus.Insertions);
        Assert.Equal(expected.GitStatus.Deletions, mapped.GitStatus.Deletions);
        Assert.Equal(expected.GitStatus.Pushed, mapped.GitStatus.Pushed);
        Assert.Equal(expected.GitStatus.ChangedFiles, mapped.GitStatus.ChangedFiles);
    }

    private static WorkTask BuildTask(string taskId) => new()
    {
        TaskId = taskId,
        GoalId = "goal-live",
        GoalDescription = "live work that must not be disturbed",
        Prompt = "do the work",
        Role = DomainWorkerRole.Coder,
        Model = "live-model",
        Repositories = [],
    };

    private static CompletionReceiptQueryRequest QueryRequest(
        string workerId, GrpcTaskComplete? complete) =>
        new() { WorkerId = workerId, Completion = complete };

    // ═══════════════════════════════════════════════════════════════════════
    // the harness
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A REAL <see cref="HiveOrchestratorService"/> over the real collaborator graph, the real
    /// insert-once stores on a hermetic in-memory SQLite database, and the REAL recorder behind a thin
    /// observing forwarder.
    /// </summary>
    private sealed class QueryHarness : IDisposable
    {
        private readonly string _repoRoot;
        private int _dashboardNotifications;
        private int _completionNotifications;

        private QueryHarness(string repoRoot) => _repoRoot = repoRoot;

        public required HiveOrchestratorService Service { get; init; }

        public required WorkerPool Pool { get; init; }

        public required TaskQueue Queue { get; init; }

        public required ReceiptQueryDatabase Database { get; init; }

        public required CompletionReceiptStore ReceiptStore { get; init; }

        public required WorkerAssignmentContextStore AssignmentStore { get; init; }

        public required WorkerCompletionRecorder RealRecorder { get; init; }

        public required ObservingRecorder Recorder { get; init; }

        public int DashboardNotifications => Volatile.Read(ref _dashboardNotifications);

        public int CompletionNotifications => Volatile.Read(ref _completionNotifications);

        /// <summary>
        /// Builds the harness. The service is constructed exactly as the existing receipt fixtures do,
        /// with `completionRecorder` supplied — or deliberately omitted for the no-recorder vector.
        /// </summary>
        /// <param name="withRecorder">Whether the service is configured with a completion recorder.</param>
        /// <returns>The live harness.</returns>
        public static QueryHarness Create(bool withRecorder = true)
        {
            var database = ReceiptQueryDatabase.Create();
            var pool = new WorkerPool();
            var queue = new TaskQueue();
            var pipelineManager = new GoalPipelineManager();
            var dashboard = new DashboardNotifier();
            var completionNotifier = new TaskCompletionNotifier();

            // A TEMP-rooted brain repo — never the config repo.
            var repoRoot = Path.Combine(Path.GetTempPath(), $"copilothive-query-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repoRoot);

            var dispatcher = new GoalDispatcher(
                new GoalManager(),
                pipelineManager,
                queue,
                new GrpcWorkerGateway(pool),
                completionNotifier,
                NullLogger<GoalDispatcher>.Instance,
                new BrainRepoManager(repoRoot, NullLogger<BrainRepoManager>.Instance));

            var assignmentStore = new WorkerAssignmentContextStore(
                database, NullLogger<WorkerAssignmentContextStore>.Instance);
            var receiptStore = new CompletionReceiptStore(
                database, NullLogger<CompletionReceiptStore>.Instance);
            var realRecorder = new WorkerCompletionRecorder(assignmentStore, receiptStore);
            var recorder = new ObservingRecorder(realRecorder);

            var service = new HiveOrchestratorService(
                pool,
                queue,
                pipelineManager,
                completionNotifier,
                dispatcher,
                NullLogger<HiveOrchestratorService>.Instance,
                dashboardNotifier: dashboard,
                completionRecorder: withRecorder ? recorder : null);

            var harness = new QueryHarness(repoRoot)
            {
                Service = service,
                Pool = pool,
                Queue = queue,
                Database = database,
                ReceiptStore = receiptStore,
                AssignmentStore = assignmentStore,
                RealRecorder = realRecorder,
                Recorder = recorder,
            };

            dashboard.OnStateChanged += () => Interlocked.Increment(ref harness._dashboardNotifications);
            completionNotifier.OnTaskCompleted += _ =>
            {
                Interlocked.Increment(ref harness._completionNotifications);
                return Task.CompletedTask;
            };

            return harness;
        }

        /// <summary>Zeroes the notification observations, so a setup step's own notifications are excluded.</summary>
        public void ResetNotificationCounters()
        {
            Interlocked.Exchange(ref _dashboardNotifications, 0);
            Interlocked.Exchange(ref _completionNotifications, 0);
        }

        /// <summary>
        /// Seeds ONE receipt through the REAL insert-once store, with NO assignment context and no
        /// registered worker — the evidence-only shape this endpoint exists for.
        /// </summary>
        /// <param name="result">The evidence the retained receipt carries.</param>
        /// <param name="taskId">The opaque task id the row is keyed by.</param>
        /// <param name="workerId">The worker the retained evidence names.</param>
        /// <returns>The stored receipt.</returns>
        public CompletionReceipt SeedReceiptFor(
            TaskResult result, string? taskId = null, string? workerId = null)
        {
            var slot = new WorkSlot(
                taskId ?? TaskId, new WorkSlotPosition(2, GoalPhase.Coding, 2), 3);

            var receipt = new CompletionReceipt(
                GoalId, workerId ?? WorkerId, DomainWorkerRole.Coder, slot, result);

            var write = ReceiptStore.InsertOnce(receipt);
            Assert.Equal(CompletionReceiptWriteStatus.Stored, write.Status);
            Assert.Equal(0L, Database.AssignmentRowCount);
            return receipt;
        }

        /// <summary>Runs the REAL override with a live call context.</summary>
        /// <param name="workerId">The comparison-evidence worker id.</param>
        /// <param name="complete">The completion evidence, or <c>null</c> for the missing-completion shape.</param>
        /// <returns>The response.</returns>
        public CompletionReceiptQueryResponse Query(string workerId, GrpcTaskComplete? complete) =>
            Service.QueryCompletionReceipt(QueryRequest(workerId, complete), LiveCallContext()).GetAwaiter().GetResult();

        /// <summary>
        /// THE NO-OWNERSHIP WITNESS: no worker is registered, no stream is attached, nothing is busy
        /// and nothing is queued.
        /// </summary>
        public void AssertNoOwnershipState()
        {
            Assert.Equal(0, Pool.ConnectedWorkerCount);
            Assert.Null(Pool.GetIdleWorker());
            Assert.Null(Pool.GetWorker(WorkerId));
            Assert.False(Pool.TryGetWorkerSnapshot(WorkerId, out _));
            Assert.Null(Queue.GetActiveTask(TaskId));
            Assert.Null(Queue.TryDequeueAny());
        }

        public void Dispose()
        {
            Database.Dispose();

            try
            {
                if (Directory.Exists(_repoRoot))
                    Directory.Delete(_repoRoot, recursive: true);
            }
            catch
            {
                // Best-effort — a leftover temp root must never fail a test.
            }
        }

        private static ServerCallContext LiveCallContext() => new Mock<ServerCallContext>().Object;
    }

    /// <summary>
    /// THE HERMETIC DATABASE: a private in-memory SQLite database kept alive by its own anchor
    /// connection, exposed as the stores' <see cref="IDbContextFactory{CopilotHiveDbContext}"/>, with
    /// raw SQL observation through that SAME anchor connection and an EF command interceptor that
    /// counts write attempts.
    /// </summary>
    private sealed class ReceiptQueryDatabase : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        private ReceiptQueryDatabase(
            SqliteConnection connection,
            DbContextOptions<CopilotHiveDbContext> options,
            WriteCommandInterceptor interceptor)
        {
            _connection = connection;
            _options = options;
            Interceptor = interceptor;
        }

        public WriteCommandInterceptor Interceptor { get; }

        /// <summary>Creates a fresh in-memory database with both tables and an armed write observer.</summary>
        /// <returns>The live database fixture.</returns>
        public static ReceiptQueryDatabase Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();

            // THE SCHEMA IS CREATED THROUGH A PLAIN CONTEXT, so the observer below sees only the
            // subject's own commands.
            using (var bootstrap = new CopilotHiveDbContext(
                new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connection).Options))
            {
                bootstrap.Database.EnsureCreated();
            }

            var interceptor = new WriteCommandInterceptor();
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;

            return new ReceiptQueryDatabase(connection, options, interceptor);
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _connection.Dispose();

        public void ExecuteRaw(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public object? RawScalar(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value is DBNull ? null : value;
        }

        public static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        public long ReceiptRowCount(string taskId) =>
            (long)RawScalar($"SELECT COUNT(*) FROM completion_receipts WHERE task_id = '{Escape(taskId)}'")!;

        public long AssignmentRowCount =>
            (long)RawScalar("SELECT COUNT(*) FROM worker_assignment_contexts")!;

        public string? RawPayload(string taskId) =>
            (string?)RawScalar($"SELECT payload_json FROM completion_receipts WHERE task_id = '{Escape(taskId)}'");

        public string? RawFirstStoredText(string taskId) =>
            (string?)RawScalar($"SELECT first_stored_at_utc FROM completion_receipts WHERE task_id = '{Escape(taskId)}'");
    }

    /// <summary>
    /// THE WRITE OBSERVER: counts every NON-QUERY command attempt issued through a context, which is
    /// what "the query issued no INSERT/UPDATE/DELETE" means in terms of the provider's own commands.
    /// </summary>
    private sealed class WriteCommandInterceptor : DbCommandInterceptor
    {
        private int _nonQueryAttempts;

        public int NonQueryAttempts => Volatile.Read(ref _nonQueryAttempts);

        /// <inheritdoc />
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _nonQueryAttempts);
            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _nonQueryAttempts);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// THE OBSERVING FORWARDER over the REAL recorder. It delegates BOTH operations, so the evidence
    /// the confirmation reads is genuinely production-written; it adds only call accounting, the
    /// captured arguments, and a one-shot fault seam that lets a vector raise a refusal or cancel the
    /// caller's token from INSIDE the read.
    /// </summary>
    private sealed class ObservingRecorder : IWorkerCompletionRecorder
    {
        private readonly IWorkerCompletionRecorder _inner;
        private int _confirmCalls;
        private int _recordCalls;

        public ObservingRecorder(IWorkerCompletionRecorder inner) => _inner = inner;

        /// <summary>A ONE-SHOT fault thrown IN PLACE OF the real confirmation; consumed as it fires.</summary>
        public Action? ConfirmFault { get; set; }

        /// <summary>Runs INSIDE the confirmation, immediately after the real read returned.</summary>
        public Action? OnConfirmSettled { get; set; }

        public int ConfirmCalls => Volatile.Read(ref _confirmCalls);

        public int RecordCalls => Volatile.Read(ref _recordCalls);

        public string? LastWorkerId { get; private set; }

        public string? LastTaskId { get; private set; }

        public TaskResult? LastResult { get; private set; }

        /// <inheritdoc />
        public void Record(string workerId, WorkTask task, TaskResult result)
        {
            Interlocked.Increment(ref _recordCalls);
            _inner.Record(workerId, task, result);
        }

        /// <inheritdoc />
        public bool ConfirmStoredReceipt(string workerId, string taskId, TaskResult result)
        {
            Interlocked.Increment(ref _confirmCalls);
            LastWorkerId = workerId;
            LastTaskId = taskId;
            LastResult = result;

            var fault = ConfirmFault;
            if (fault is not null)
            {
                ConfirmFault = null;
                fault();
            }

            var answer = _inner.ConfirmStoredReceipt(workerId, taskId, result);

            OnConfirmSettled?.Invoke();

            return answer;
        }
    }

    /// <summary>
    /// A REAL <see cref="ServerCallContext"/> whose <see cref="ServerCallContext.CancellationToken"/> —
    /// the non-virtual property that delegates to <see cref="ServerCallContext.CancellationTokenCore"/>
    /// — carries the caller's token. Moq cannot mock that property, so a subclass is the only seam.
    /// </summary>
    private sealed class CancellationServerCallContext : ServerCallContext
    {
        private readonly Func<CancellationToken> _tokenProvider;

        public CancellationServerCallContext(CancellationToken token)
            : this(() => token)
        {
        }

        public CancellationServerCallContext(Func<CancellationToken> tokenProvider) =>
            _tokenProvider = tokenProvider;

        protected override CancellationToken CancellationTokenCore => _tokenProvider();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException("propagation is not used by the query fixture");

        protected override string MethodCore => "/copilothive.HiveOrchestrator/QueryCompletionReceipt";

        protected override string HostCore => "test-host";

        protected override string PeerCore => "test-peer";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore => [];

        protected override Metadata ResponseTrailersCore => [];

        protected override Status StatusCore { get; set; } = Status.DefaultSuccess;

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore => null!;
    }
}
