using System.Net;
using CopilotHive.Models;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.DependencyInjection;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Unit and integration tests for <see cref="WorkerUtilizationService"/>.
/// </summary>
public class WorkerUtilizationServiceTests
{
    private static WorkerPool CreatePool() => new WorkerPool();

    private static ConnectedWorker MakeWorker(WorkerPool pool, string id, bool busy)
    {
        var w = pool.RegisterWorker(id, []);
        if (busy) pool.MarkBusy(id, "task-" + id);
        return w;
    }

    /// <summary>
    /// A minimal dequeued task whose role flows into the claimed worker — the ONLY pool lifecycle
    /// operation that assigns a real role to a registered worker.
    /// </summary>
    private static WorkTask ClaimTask(string taskId, WorkerRole role) => new()
    {
        TaskId = taskId,
        GoalId = "goal-utilization",
        GoalDescription = "reach a real per-role state",
        Prompt = "do the work",
        Role = role,
        Repositories = [],
    };

    /// <summary>
    /// Claims a REAL assignment through the pool's checked claim-and-activate: enqueue, dequeue and
    /// <see cref="WorkerPool.TryClaimAndActivate"/> publish the busy flag AND the task's role as one
    /// pool-owned transition, so the worker reaches a real busy Coder/Tester state — never a
    /// hand-set property on a leaked instance.
    /// </summary>
    private static ConnectedWorker ClaimedWorker(WorkerPool pool, string id, WorkerRole role)
    {
        var worker = pool.RegisterWorker(id, []);
        var queue = new TaskQueue();
        var task = ClaimTask("task-" + id, role);
        queue.Enqueue(task);
        var dequeued = queue.TryDequeueAny();
        Assert.Same(task, dequeued);

        Assert.True(pool.TryClaimAndActivate(worker, dequeued!, queue));

        // The premise: the claim really produced the busy-in-role state.
        Assert.True(worker.IsBusy);
        Assert.Equal(role, worker.Role);
        return worker;
    }

    [Fact]
    public void GetUtilization_EmptyPool_ReturnsZeroUtilization()
    {
        var pool = CreatePool();
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(0.0, result.OverallUtilization);
        Assert.Empty(result.RoleBreakdown);
        Assert.Empty(result.BottleneckRoles);
    }

    [Fact]
    public void GetUtilization_SomeBusy_ReturnsCorrectFraction()
    {
        var pool = CreatePool();
        MakeWorker(pool, "w1", busy: true);
        MakeWorker(pool, "w2", busy: true);
        MakeWorker(pool, "w3", busy: false);
        MakeWorker(pool, "w4", busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(0.5, result.OverallUtilization);
    }

    [Fact]
    public void GetUtilization_AllBusy_ReturnsOne()
    {
        var pool = CreatePool();
        MakeWorker(pool, "w1", busy: true);
        MakeWorker(pool, "w2", busy: true);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Equal(1.0, result.OverallUtilization);
    }

    [Fact]
    public void GetUtilization_RoleBreakdown_IsAccurate()
    {
        var pool = CreatePool();
        MakeWorker(pool, "c1", busy: true);
        MakeWorker(pool, "c2", busy: false);
        MakeWorker(pool, "t1", busy: true);
        MakeWorker(pool, "t2", busy: true);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        // All workers are Unspecified — 3 of 4 busy = 0.75
        Assert.Single(result.RoleBreakdown);
        Assert.Equal(0.75, result.RoleBreakdown["Unspecified"]);
    }

    [Fact]
    public void GetUtilization_BottleneckRoles_DetectsAbove80Percent()
    {
        var pool = CreatePool();
        // 9 of 10 busy → 0.9 > 0.8
        for (int i = 0; i < 9; i++)
            MakeWorker(pool, $"c{i}", busy: true);
        MakeWorker(pool, "c9", busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.Contains("Unspecified", result.BottleneckRoles);
    }

    [Fact]
    public void GetUtilization_BottleneckRoles_ExcludesAt80Percent()
    {
        var pool = CreatePool();
        // 4 of 5 busy → exactly 0.8 (not > 0.8)
        for (int i = 0; i < 4; i++)
            MakeWorker(pool, $"r{i}", busy: true);
        MakeWorker(pool, "r4", busy: false);
        var svc = new WorkerUtilizationService(pool);

        var result = svc.GetUtilization();

        Assert.DoesNotContain("Unspecified", result.BottleneckRoles);
        Assert.Equal(0.8, result.RoleBreakdown["Unspecified"]);
    }

    /// <summary>
    /// THE SEMANTICS THIS SERVICE KEEPS: withheld workers — awaiting their own accepted Ready, or
    /// still holding a completion publication — are NON-BUSY and remain part of the denominator, so
    /// utilization stays "busy registered workers / all registered workers" rather than a measure of
    /// assignable capacity.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: substituting an availability-based metric (denominator = only the
    /// assignable workers, or numerator = busy + withheld). Four registered workers of which exactly
    /// one is busy is 0.25 under the preserved semantics and 1.0/0.5 under those substitutions, so
    /// the assertions below separate them.
    /// </remarks>
    [Fact]
    public void GetUtilization_WithheldWorkers_AreNonBusyButStayInTheDenominator()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w-clean", []);

        // An ACK-enabled worker whose negotiated completion has been released: the readiness wait is
        // the only fact left in force, so it is non-busy yet not assignable.
        var awaiting = pool.RegisterWorker(
            "w-awaiting", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("w-awaiting", "task-awaiting");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(awaiting, "task-awaiting"));
        Assert.True(pool.ClearCompletionPublicationHold(awaiting));

        // An ACK-disabled worker still inside its completion-publication hold — non-busy, withheld
        // for a different reason.
        var publishing = pool.RegisterWorker(
            "w-publishing", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: false);
        pool.MarkBusy("w-publishing", "task-publishing");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(publishing, "task-publishing"));

        MakeWorker(pool, "w-busy", busy: true);

        // The premise: both withheld workers really are non-busy and really are withheld.
        Assert.False(awaiting.IsBusy);
        Assert.True(awaiting.AwaitingWorkerReady);
        Assert.False(publishing.IsBusy);
        Assert.True(publishing.CompletionPublicationPending);

        var result = new WorkerUtilizationService(pool).GetUtilization();

        Assert.Equal(0.25, result.OverallUtilization);
        Assert.Equal(0.25, result.RoleBreakdown["Unspecified"]);
        Assert.DoesNotContain("Unspecified", result.BottleneckRoles);
    }

    /// <summary>
    /// THE PER-ROLE FRACTIONS SURVIVE THE MIXED STATES, built entirely through real pool lifecycle
    /// operations and across MULTIPLE roles in the SAME capture: two busy Coder claims and one busy
    /// Tester claim (roles assigned by <see cref="WorkerPool.TryClaimAndActivate"/>), plus a clean
    /// idle worker, an awaiting-Ready worker and a completion-publication-held worker.
    /// </summary>
    /// <remarks>
    /// REAL-LIFECYCLE ROLE PLACEMENT: the pool's checked release resets the role to
    /// <see cref="WorkerRole.Unspecified"/>, so every non-busy worker the lifecycle can produce —
    /// idle, awaiting-Ready, publication-held — is grouped under <c>Unspecified</c>, while claimed
    /// workers carry their task's role. THE MUTATION THIS KILLS: substituting an availability-based
    /// metric. An availability denominator or a busy+withheld numerator moves the asserted overall
    /// 0.5 and the <c>Unspecified</c> 0.0 (the withheld workers would vanish from or pollute the
    /// fraction), and per-role 1.0 fractions separate a per-role busy+withheld numerator.
    /// </remarks>
    [Fact]
    public void GetUtilization_MixedRolesAndWithheldStates_KeepBusyOverRegisteredFractions()
    {
        var pool = CreatePool();

        // Two real busy Coder claims and one real busy Tester claim — real roles via the checked
        // claim path, each carrying its task's role while busy.
        ClaimedWorker(pool, "w-coder-1", WorkerRole.Coder);
        ClaimedWorker(pool, "w-coder-2", WorkerRole.Coder);
        ClaimedWorker(pool, "w-tester-1", WorkerRole.Tester);

        // A clean registered worker: idle, never claimed, genuinely available.
        pool.RegisterWorker("w-clean", []);

        // An awaiting-Ready worker: released with the publication hold installed, then the short
        // hold cleared — only the readiness wait remains in force. NON-busy, in the denominator.
        var awaiting = pool.RegisterWorker(
            "w-awaiting", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("w-awaiting", "task-awaiting");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(awaiting, "task-awaiting"));
        Assert.True(pool.ClearCompletionPublicationHold(awaiting));
        Assert.False(awaiting.IsBusy);
        Assert.True(awaiting.AwaitingWorkerReady);

        // A publication-held worker: ACK-disabled, released with the hold still installed. NON-busy.
        var publishing = pool.RegisterWorker(
            "w-publishing", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: false);
        pool.MarkBusy("w-publishing", "task-publishing");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(publishing, "task-publishing"));
        Assert.False(publishing.IsBusy);
        Assert.True(publishing.CompletionPublicationPending);

        var result = new WorkerUtilizationService(pool).GetUtilization();

        // ── overall: 3 busy claims of 6 registered = 0.5 (withheld workers count, non-busy) ──────
        Assert.Equal(0.5, result.OverallUtilization);

        // ── per role, from the same one capture ─────────────────────────────────────────────────
        // Coder: both claims busy → 2/2 = 1.0. Tester: its one claim busy → 1/1 = 1.0. No
        // availability substitution can produce these from a restricted denominator.
        Assert.Equal(1.0, result.RoleBreakdown[nameof(WorkerRole.Coder)]);
        Assert.Equal(1.0, result.RoleBreakdown[nameof(WorkerRole.Tester)]);
        // Unspecified: the clean idle worker, the awaiting-Ready worker and the publication-held
        // worker — all NON-busy members of the denominator → 0/3 = 0.0.
        Assert.Equal(0.0, result.RoleBreakdown[nameof(WorkerRole.Unspecified)]);
        Assert.Equal(3, result.RoleBreakdown.Count);

        // ── bottleneck: the two claimed roles exceed 0.8; Unspecified (carrying the withheld
        // workers) does NOT — they stay non-busy members, never busy-withheld numerator.
        Assert.Equal(
            new[] { nameof(WorkerRole.Coder), nameof(WorkerRole.Tester) }.OrderBy(r => r),
            result.BottleneckRoles.OrderBy(r => r));
        Assert.DoesNotContain(nameof(WorkerRole.Unspecified), result.BottleneckRoles);
    }

    /// <summary>
    /// THE BOUNDARY AT EXACTLY 0.8 IS NOT A BOTTLENECK while strictly above 0.8 IS — with an
    /// awaiting-Ready worker kept in the denominator by real lifecycle operations, so the boundary
    /// is evaluated over the FULL registered population, not the assignable one.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: any <c>>= 0.8</c> threshold (fails on the exact-0.8 step), and any
    /// availability-based denominator (dropping the withheld worker turns the exact-0.8 capture into
    /// 1.0, which the asserted non-bottleneck rejects).
    /// </remarks>
    [Fact]
    public void GetUtilization_ThresholdBoundary_WithAWithheldWorkerInTheDenominator()
    {
        var pool = CreatePool();

        // Four real busy workers through the pool's ID-based MarkBusy (all stay Unspecified-role,
        // matching every registered worker that is not currently carrying a claimed task).
        for (var i = 0; i < 4; i++)
        {
            pool.RegisterWorker($"w-boundary-{i}", []);
            pool.MarkBusy($"w-boundary-{i}", $"task-boundary-{i}");
        }

        // An awaiting-Ready worker, created by real lifecycle operations: NON-busy, withheld, and
        // still a registered member of the denominator.
        var awaiting = pool.RegisterWorker(
            "w-boundary-awaiting", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("w-boundary-awaiting", "task-boundary-awaiting");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(awaiting, "task-boundary-awaiting"));
        Assert.True(pool.ClearCompletionPublicationHold(awaiting));
        Assert.False(awaiting.IsBusy);
        Assert.True(awaiting.AwaitingWorkerReady);

        var atBoundary = new WorkerUtilizationService(pool).GetUtilization();

        // 4 busy of 5 registered = exactly 0.8, overall AND in the Unspecified role that carries
        // the withheld worker — and exactly 0.8 is NOT a bottleneck (strict > only).
        Assert.Equal(0.8, atBoundary.OverallUtilization);
        Assert.Equal(0.8, atBoundary.RoleBreakdown[nameof(WorkerRole.Unspecified)]);
        Assert.DoesNotContain(nameof(WorkerRole.Unspecified), atBoundary.BottleneckRoles);

        // Dropping the withheld worker from the denominator is exactly the availability mutation:
        // with it removed (5 → 4 registered), the same four busy workers read 1.0 and DO report a
        // bottleneck — proving the 0.8 figure above genuinely included it.
        Assert.True(pool.RemoveWorker("w-boundary-awaiting"));
        var aboveBoundary = new WorkerUtilizationService(pool).GetUtilization();
        Assert.Equal(1.0, aboveBoundary.OverallUtilization);
        Assert.Contains(nameof(WorkerRole.Unspecified), aboveBoundary.BottleneckRoles);
    }

    /// <summary>
    /// THE ONE-CAPTURE SHAPE, bound to the production source: <c>GetUtilization</c> calls
    /// <c>CaptureWorkerStatus()</c> exactly once, binds it to a local, and never reads the live worker
    /// list.
    /// </summary>
    /// <remarks>
    /// A SECOND, INDEPENDENT READ IS NOT OBSERVABLE FROM THE RESULT: two reads that agree on a quiet
    /// pool produce exactly the same metrics as one read, while reintroducing the torn snapshot under
    /// contention — which is why this vector asserts the SCOPE of the single capture.
    /// </remarks>
    [Fact]
    public void GetUtilization_DerivesEveryFigureFromOneCapture()
    {
        var source = StripLineComments(
            ReadProductionSource("src/CopilotHive/Services/WorkerUtilizationService.cs"));

        var methodStart = source.IndexOf(
            "public WorkerUtilizationMetrics GetUtilization()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "GetUtilization is gone.");

        var body = BraceScopedBody(source, methodStart);

        Assert.Equal(1, CountOccurrences(body, "CaptureWorkerStatus()"));
        Assert.Contains("var captured = _workerPool.CaptureWorkerStatus();", body, StringComparison.Ordinal);

        // The capture is the ONLY pool interaction in the method: no second member access on the
        // injected pool, so no second read can hide beside the capture (e.g. a count or an
        // IsSelectableIdle re-consultation).
        Assert.Equal(1, CountOccurrences(body, "_workerPool."));
        Assert.Contains("captured.Count(w => w.IsBusy)", body, StringComparison.Ordinal);
        Assert.Contains("captured.GroupBy(w => w.Role.ToString())", body, StringComparison.Ordinal);

        // Every derivation reads the captured local, and the live list is not read at all.
        Assert.Contains("captured.Count", body, StringComparison.Ordinal);
        Assert.Contains("captured.GroupBy", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAllWorkers", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips <c>//</c> line (and documentation) comments so the structural assertions read CODE
    /// rather than prose: a comment mentioning the forbidden call must not be able to satisfy a
    /// prohibition.
    /// </summary>
    private static string StripLineComments(string code) =>
        string.Join(
            '\n',
            code.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment].TrimEnd();
            }));

    /// <summary>Counts the non-overlapping occurrences of a literal in the source text.</summary>
    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Returns the body of the block that OPENS at the first <c>{</c> at or after
    /// <paramref name="from"/>, delimited by BALANCED BRACE COUNTING, so the scope claim is made
    /// against the matched body rather than against text order.
    /// </summary>
    private static string BraceScopedBody(string text, int from)
    {
        var open = text.IndexOf('{', from);
        Assert.True(open >= 0, "no block body opens after the anchor; the production shape changed.");

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[(open + 1)..i];
            }
        }

        Assert.Fail("the block body opened at the anchor is never closed; the production shape changed.");
        return null!;
    }

    /// <summary>
    /// Loads a production file by its repository-relative path, walking up from the test assembly to
    /// the repository root. A MISSING FILE IS A LOUD FAILURE, never a silently skipped assertion.
    /// </summary>
    private static string ReadProductionSource(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        Assert.Fail(
            $"'{relative}' was not found walking up from '{AppContext.BaseDirectory}'; the structural "
            + "vector cannot be evaluated.");
        return null!;
    }
}

/// <summary>
/// Integration tests for the <c>GET /health/utilization</c> endpoint.
/// </summary>
[Collection("HiveIntegration")]
public class UtilizationEndpointTests
{
    private readonly HttpClient _client;

    /// <summary>Initialises with the shared <see cref="HiveTestFactory"/> fixture.</summary>
    /// <param name="factory">The shared test factory.</param>
    public UtilizationEndpointTests(HiveTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task UtilizationEndpoint_Returns200WithJsonContentType()
    {
        var response = await _client.GetAsync("/health/utilization", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
    }
}
