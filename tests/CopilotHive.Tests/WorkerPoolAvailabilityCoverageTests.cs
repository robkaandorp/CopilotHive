using CopilotHive.Dashboard;
using CopilotHive.Models;
using CopilotHive.Services;
using CopilotHive.Workers;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Vectors for the AVAILABLE / AWAITING-READY operator visibility that the coder's
/// <see cref="WorkerPoolAvailabilityStatsTests"/> leave open: the checked-claim busy state, the
/// legacy release paths (which install NO holds), removal, the inconsistent idle-with-task shape,
/// snapshot detachment, DTO JSON compatibility and the dashboard's preserved per-worker fields.
/// </summary>
/// <remarks>
/// Every state here is reached through the pool's own production operations — registration, the
/// checked claim, the checked releases, the hold clearing, the accepted Ready and removal. The one
/// deliberately synthetic shape (non-busy with a non-null task id) has NO production entry point by
/// design — it is the inconsistent shape the claim refuses — so it is produced by writing the public
/// busy flag directly after a real <see cref="WorkerPool.MarkBusy"/>.
/// </remarks>
public sealed class WorkerPoolAvailabilityCoverageTests
{
    /// <summary>Builds a minimal task for the checked claim, mirroring the pool's own claim vectors.</summary>
    private static WorkTask ClaimTask(string taskId) => new()
    {
        TaskId = taskId,
        GoalId = "goal-coverage",
        GoalDescription = "availability coverage",
        Prompt = "do the work",
        Role = WorkerRole.Coder,
        Model = "claim-model",
        Repositories = [],
    };

    /// <summary>
    /// Registers an ACK-enabled instance and drives it through the pool's negotiated completion
    /// release and the pool's own hold clearing, leaving ONLY the readiness wait in force.
    /// </summary>
    private static ConnectedWorker AwaitingReadyWorker(WorkerPool pool, string id, string taskId)
    {
        var worker = pool.RegisterWorker(
            id, [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy(id, taskId);
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(worker, taskId));
        Assert.True(pool.ClearCompletionPublicationHold(worker));
        Assert.True(worker.AwaitingWorkerReady);
        Assert.False(worker.CompletionPublicationPending);
        return worker;
    }

    /// <summary>
    /// THE CHECKED CLAIM IS A BUSY STATE the statistics must report as not-available: a worker
    /// claimed through <see cref="WorkerPool.TryClaimAndActivate"/> — the production assignment
    /// entry point, not the unconditional ID-based marking — is busy with its task, counted in
    /// <c>BusyWorkers</c>, excluded from <c>IdleWorkers</c>, excluded from
    /// <c>AvailableWorkers</c> and excluded from <c>AwaitingReadyWorkers</c>.
    /// </summary>
    [Fact]
    public void GetWorkerStats_CheckedClaimedWorker_IsBusyAndNotAvailable()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-claim-clean", []);

        var worker = pool.RegisterWorker("w-claim-busy", []);
        var queue = new TaskQueue();
        var task = ClaimTask("task-claim-coverage");
        queue.Enqueue(task);
        var dequeued = queue.TryDequeueAny();
        Assert.Same(task, dequeued);
        Assert.True(pool.TryClaimAndActivate(worker, dequeued!, queue));

        var stats = pool.GetWorkerStats();

        Assert.Equal(2, stats.TotalWorkers);
        Assert.Equal(1, stats.BusyWorkers);
        Assert.Equal(1, stats.IdleWorkers);
        Assert.Equal(1, stats.AvailableWorkers);
        Assert.Equal(0, stats.AwaitingReadyWorkers);

        // The claimed instance really is busy with THAT task, and its per-worker projection is
        // consistent with the aggregate taken from the same capture.
        Assert.True(worker.IsBusy);
        Assert.Equal("task-claim-coverage", worker.CurrentTaskId);

        var details = pool.GetDetailedStats();
        var claimed = details.Workers.Single(w => w.Id == "w-claim-busy");
        Assert.True(claimed.IsBusy);
        Assert.Equal("task-claim-coverage", claimed.CurrentTaskId);
        Assert.False(claimed.IsAvailable);
        Assert.False(claimed.AwaitingWorkerReady);
    }

    /// <summary>
    /// THE LEGACY RELEASE PATHS INSTALL NO HOLDS: both the two-argument checked release
    /// (<see cref="WorkerPool.TryReleaseCompletedTask"/>) and the ID-based
    /// <see cref="WorkerPool.MarkIdle"/> leave the completion-publication hold and the readiness
    /// wait untouched (nothing installed), so a worker released through either route is back to
    /// genuinely available — even when its registration negotiated the acknowledgement.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: making the negotiated release route's hold installation leak into
    /// the legacy routes (or vice versa). The ACK-enabled registration makes the confusion
    /// observable: only the negotiated route may install anything.
    /// </remarks>
    [Fact]
    public void GetDetailedStats_LegacyReleasePaths_InstallNoHoldsAndRemainAvailable()
    {
        var pool = new WorkerPool();

        // ACK-ENABLED registration + the LEGACY two-argument checked release.
        var released = pool.RegisterWorker(
            "w-legacy-release", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("w-legacy-release", "task-legacy-release");
        Assert.True(pool.TryReleaseCompletedTask(released, "task-legacy-release"));

        // ACK-ENABLED registration + the ID-based idle reset.
        var idled = pool.RegisterWorker(
            "w-legacy-idle", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("w-legacy-idle", "task-legacy-idle");
        pool.MarkIdle("w-legacy-idle");

        Assert.False(released.CompletionPublicationPending);
        Assert.False(released.AwaitingWorkerReady);
        Assert.False(idled.CompletionPublicationPending);
        Assert.False(idled.AwaitingWorkerReady);

        var stats = pool.GetDetailedStats();

        Assert.Equal(2, stats.TotalWorkers);
        Assert.Equal(0, stats.BusyWorkers);
        Assert.Equal(2, stats.IdleWorkers);
        Assert.Equal(2, stats.AvailableWorkers);
        Assert.Equal(0, stats.AwaitingReadyWorkers);

        var releasedEntry = stats.Workers.Single(w => w.Id == "w-legacy-release");
        Assert.True(releasedEntry.IsAvailable);
        Assert.False(releasedEntry.AwaitingWorkerReady);
        // The legacy release cleared the model inside the checked release, as always.
        Assert.Null(releasedEntry.CurrentTaskId);

        var idledEntry = stats.Workers.Single(w => w.Id == "w-legacy-idle");
        Assert.True(idledEntry.IsAvailable);
    }

    /// <summary>
    /// A REMOVED WORKER IS EXCLUDED FROM EVERY COUNT: before removal it is part of the totals; after
    /// <see cref="WorkerPool.RemoveWorker(ConnectedWorker)"/> it is gone from
    /// <c>TotalWorkers</c>, <c>IdleWorkers</c>, <c>AvailableWorkers</c>,
    /// <c>AwaitingReadyWorkers</c> and the per-worker entries.
    /// </summary>
    [Fact]
    public void GetWorkerStats_RemovedWorker_IsExcludedFromAllCounts()
    {
        var pool = new WorkerPool();
        var kept = pool.RegisterWorker("w-removal-kept", []);
        var removed = AwaitingReadyWorker(pool, "w-removal-gone", "task-removal-gone");

        var before = pool.GetWorkerStats();
        Assert.Equal(2, before.TotalWorkers);
        // PRESERVED SEMANTICS: both workers are non-busy, so the awaiting one is still idle.
        Assert.Equal(2, before.IdleWorkers);
        Assert.Equal(1, before.AvailableWorkers);
        Assert.Equal(1, before.AwaitingReadyWorkers);

        Assert.True(pool.RemoveWorker(removed));

        var after = pool.GetWorkerStats();
        Assert.Equal(1, after.TotalWorkers);
        Assert.Equal(1, after.IdleWorkers);
        Assert.Equal(1, after.AvailableWorkers);
        Assert.Equal(0, after.AwaitingReadyWorkers);
        // The by-role breakdown agrees with the shrunken total, not with the pre-removal population.
        Assert.Equal(after.TotalWorkers, after.WorkersByRole.Values.Sum());
        Assert.False(after.WorkersByRole.TryGetValue(
            WorkerRole.Unspecified.ToString(), out var unspecifiedCount) && unspecifiedCount == 2);

        var details = pool.GetDetailedStats();
        Assert.Equal(1, details.TotalWorkers);
        Assert.DoesNotContain(details.Workers, w => w.Id == "w-removal-gone");
        Assert.Single(details.Workers);
        Assert.True(details.Workers.Single(w => w.Id == "w-removal-kept").IsAvailable);
    }

    /// <summary>
    /// THE INCONSISTENT SHAPE — non-busy but still carrying a non-null
    /// <see cref="ConnectedWorker.CurrentTaskId"/> — is NOT available capacity: the claim refuses it
    /// (its null-task check fails), so <c>AvailableWorkers</c> excludes it, while the PRESERVED
    /// <c>IdleWorkers</c> semantics (not currently executing) still count it, and it is not an
    /// awaiting-Ready worker either.
    /// </summary>
    /// <remarks>
    /// NO PRODUCTION ENTRY POINT creates this shape — that is the point of the claim's null-task
    /// check — so the vector writes the public busy flag directly after a real
    /// <see cref="WorkerPool.MarkBusy"/> to pin the defensive predicate.
    /// </remarks>
    [Fact]
    public void GetWorkerStats_IdleWithNonNullTask_IsNotAvailableButStillIdle()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-shape-clean", []);

        var inconsistent = pool.RegisterWorker("w-shape-inconsistent", []);
        pool.MarkBusy("w-shape-inconsistent", "task-shape-orphan");
        inconsistent.IsBusy = false; // the inconsistent external write: task id survives, busy flag does not

        var stats = pool.GetWorkerStats();

        Assert.Equal(2, stats.TotalWorkers);
        Assert.Equal(0, stats.BusyWorkers);
        // PRESERVED SEMANTICS: not busy ⇒ idle, inconsistent shape or not.
        Assert.Equal(2, stats.IdleWorkers);
        // THE HONEST OBSERVATION: only the clean worker is assignable.
        Assert.Equal(1, stats.AvailableWorkers);
        Assert.Equal(0, stats.AwaitingReadyWorkers);

        var details = pool.GetDetailedStats();
        var entry = details.Workers.Single(w => w.Id == "w-shape-inconsistent");
        Assert.False(entry.IsBusy);
        Assert.Equal("task-shape-orphan", entry.CurrentTaskId);
        Assert.False(entry.IsAvailable);
        Assert.False(entry.AwaitingWorkerReady);

        var clean = details.Workers.Single(w => w.Id == "w-shape-clean");
        Assert.True(clean.IsAvailable);
    }

    /// <summary>
    /// DETACHMENT: a captured <see cref="WorkerPoolStats"/> and a captured
    /// <see cref="WorkerPoolStatsDto"/> describe the instant they were taken and are NEVER updated
    /// by later pool mutations — busy a worker, clear a hold, remove a worker — while the NEXT call
    /// reflects the new state, and the two calls do not share list state.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: caching a last result (the first capture would move with the pool),
    /// and aliasing live <see cref="ConnectedWorker"/> instances into the DTO list (the captured
    /// per-worker flags would follow the instances).
    /// </remarks>
    [Fact]
    public async Task StatsSnapshots_AreDetachedFromLaterPoolMutations()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-detach-clean", []);
        var awaiting = AwaitingReadyWorker(pool, "w-detach-awaiting", "task-detach-awaiting");
        var publishing = pool.RegisterWorker(
            "w-detach-publishing", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: false);
        pool.MarkBusy("w-detach-publishing", "task-detach-publishing");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(publishing, "task-detach-publishing"));

        var capturedStats = pool.GetWorkerStats();
        var capturedDetails = pool.GetDetailedStats();

        Assert.Equal(3, capturedStats.TotalWorkers);
        Assert.Equal(0, capturedStats.BusyWorkers);
        Assert.Equal(3, capturedStats.IdleWorkers);
        Assert.Equal(1, capturedStats.AvailableWorkers);
        Assert.Equal(1, capturedStats.AwaitingReadyWorkers);

        // ── Mutate the pool in three different ways ──────────────────────────────
        pool.MarkBusy("w-detach-clean", "task-detach-busy");                       // busy a worker
        Assert.True(pool.ClearCompletionPublicationHold(publishing));              // clear a hold
        Assert.True(pool.RemoveWorker(awaiting));                                  // remove a worker

        // The FIRST captures are unchanged — no cached/shared state moved with the pool.
        Assert.Equal(3, capturedStats.TotalWorkers);
        Assert.Equal(0, capturedStats.BusyWorkers);
        Assert.Equal(3, capturedStats.IdleWorkers);
        Assert.Equal(1, capturedStats.AvailableWorkers);
        Assert.Equal(1, capturedStats.AwaitingReadyWorkers);
        Assert.Equal(3, capturedDetails.TotalWorkers);
        Assert.Equal(3, capturedDetails.Workers.Count);
        Assert.True(capturedDetails.Workers.Single(w => w.Id == "w-detach-awaiting").AwaitingWorkerReady);
        var publishingEntry = capturedDetails.Workers.Single(w => w.Id == "w-detach-publishing");
        Assert.False(publishingEntry.IsAvailable);
        Assert.False(publishingEntry.AwaitingWorkerReady); // the hold is not the readiness wait

        // The per-worker DTOs are detached copies: mutating the captured list cannot touch the pool
        // or a later capture.
        capturedDetails.Workers.Add(new WorkerInfoDto
        {
            Id = "w-detach-fake", Role = null, IsBusy = false, CurrentTaskId = null,
        });

        // A LATER call reflects the new state: 2 workers, one busy, one available, none awaiting.
        var freshStats = pool.GetWorkerStats();
        Assert.Equal(2, freshStats.TotalWorkers);
        Assert.Equal(1, freshStats.BusyWorkers);
        Assert.Equal(1, freshStats.IdleWorkers);
        Assert.Equal(1, freshStats.AvailableWorkers);
        Assert.Equal(0, freshStats.AwaitingReadyWorkers);

        var freshDetails = pool.GetDetailedStats();
        Assert.Equal(2, freshDetails.Workers.Count);
        Assert.DoesNotContain(freshDetails.Workers, w => w.Id == "w-detach-fake");
        Assert.DoesNotContain(freshDetails.Workers, w => w.Id == "w-detach-awaiting");
        Assert.False(freshDetails.Workers.Single(w => w.Id == "w-detach-clean").IsAvailable);
        Assert.True(freshDetails.Workers.Single(w => w.Id == "w-detach-publishing").IsAvailable);

        // The dashboard projection detaches the same way: an earlier snapshot does not follow the
        // pool's mutations.
        using var service = new DashboardStateService(
            pool, new GoalPipelineManager(), new DashboardLogSink(), new ProgressLog(), goalStore: null);
        var capturedSnapshot = await service.GetSnapshot();
        Assert.Equal(2, capturedSnapshot.TotalWorkers);
        Assert.Equal(1, capturedSnapshot.AvailableWorkers);

        pool.MarkIdle("w-detach-clean");

        var snapshotAfter = await service.GetSnapshot();
        Assert.Equal(2, snapshotAfter.AvailableWorkers);
        Assert.Equal(0, snapshotAfter.BusyWorkers);
        // The FIRST snapshot is frozen: it reported the instant it was taken (1 available),
        // and MarkIdle afterwards changed nothing about it.
        Assert.Equal(1, capturedSnapshot.AvailableWorkers);
    }

    /// <summary>
    /// JSON COMPATIBILITY: the pre-existing <see cref="WorkerPoolStatsDto"/> /
    /// <see cref="WorkerInfoDto"/> wire contract is retained — every old snake_case key with its old
    /// type — and the new fields are purely additive with the exact names
    /// <c>available_workers</c>, <c>awaiting_ready_workers</c>, <c>is_available</c> and
    /// <c>awaiting_worker_ready</c>. Existing construction sites that do not set the new properties
    /// serialize them as <c>0</c>/<c>false</c>.
    /// </summary>
    [Fact]
    public void WorkerPoolStatsDto_JsonContract_IsAdditiveWithExactNames()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-json-clean", []);
        AwaitingReadyWorker(pool, "w-json-awaiting", "task-json-awaiting");
        pool.RegisterWorker("w-json-busy", []);
        pool.MarkBusy("w-json-busy", "task-json-busy");

        var json = JsonSerializer.Serialize(pool.GetDetailedStats());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // ── Old aggregate keys, unchanged names and integer types ────────────────
        Assert.Equal(JsonValueKind.Number, root.GetProperty("total_workers").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("idle_workers").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("busy_workers").ValueKind);
        Assert.Equal(3, root.GetProperty("total_workers").GetInt32());
        Assert.Equal(1, root.GetProperty("busy_workers").GetInt32());
        Assert.Equal(2, root.GetProperty("idle_workers").GetInt32());

        // ── New aggregate keys, exact names and integer types ────────────────────
        Assert.Equal(JsonValueKind.Number, root.GetProperty("available_workers").ValueKind);
        Assert.Equal(JsonValueKind.Number, root.GetProperty("awaiting_ready_workers").ValueKind);
        Assert.Equal(1, root.GetProperty("available_workers").GetInt32());
        Assert.Equal(1, root.GetProperty("awaiting_ready_workers").GetInt32());

        // ── Per-worker keys: old names first, then the additive ones ─────────────
        var clean = root.GetProperty("workers").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == "w-json-clean");
        Assert.Equal(JsonValueKind.String, clean.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, clean.GetProperty("role").ValueKind);
        Assert.Equal(JsonValueKind.False, clean.GetProperty("is_busy").ValueKind);
        Assert.Equal(JsonValueKind.Null, clean.GetProperty("current_task_id").ValueKind);
        Assert.Equal(JsonValueKind.True, clean.GetProperty("is_available").ValueKind);
        Assert.Equal(JsonValueKind.False, clean.GetProperty("awaiting_worker_ready").ValueKind);

        var busy = root.GetProperty("workers").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == "w-json-busy");
        Assert.Equal(JsonValueKind.True, busy.GetProperty("is_busy").ValueKind);
        Assert.Equal("task-json-busy", busy.GetProperty("current_task_id").GetString());
        Assert.Equal(JsonValueKind.False, busy.GetProperty("is_available").ValueKind);

        var awaiting = root.GetProperty("workers").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == "w-json-awaiting");
        Assert.Equal(JsonValueKind.False, awaiting.GetProperty("is_busy").ValueKind);
        Assert.Equal(JsonValueKind.False, awaiting.GetProperty("is_available").ValueKind);
        Assert.Equal(JsonValueKind.True, awaiting.GetProperty("awaiting_worker_ready").ValueKind);

        // ── Compat: existing construction sites that never set the new fields ────
        var legacyShaped = JsonSerializer.Serialize(new WorkerPoolStatsDto
        {
            TotalWorkers = 1,
            IdleWorkers = 1,
            BusyWorkers = 0,
            Workers = [new WorkerInfoDto { Id = "legacy", Role = null, IsBusy = false, CurrentTaskId = null }],
        });
        using var legacy = JsonDocument.Parse(legacyShaped);
        Assert.Equal(0, legacy.RootElement.GetProperty("available_workers").GetInt32());
        Assert.Equal(0, legacy.RootElement.GetProperty("awaiting_ready_workers").GetInt32());
        Assert.False(legacy.RootElement.GetProperty("workers")[0].GetProperty("is_available").GetBoolean());
        Assert.False(legacy.RootElement.GetProperty("workers")[0].GetProperty("awaiting_worker_ready").GetBoolean());
    }

    /// <summary>
    /// THE DASHBOARD PROJECTION PRESERVES EVERY PRE-EXISTING PER-WORKER FIELD — role string, task id,
    /// model, context usage, heartbeat and connection timestamps — while adding the availability
    /// flags, on a quiescent pool reached only through production operations.
    /// </summary>
    [Fact]
    public async Task DashboardProjection_PreservesExistingWorkerFieldsAndAddsFlags()
    {
        var pool = new WorkerPool();

        var clean = pool.RegisterWorker("w-dash-clean", []);
        clean.CurrentModel = null;
        pool.UpdateHeartbeat("w-dash-clean", 37);

        var awaiting = AwaitingReadyWorker(pool, "w-dash-awaiting", "task-dash-awaiting");
        awaiting.Role = WorkerRole.Tester; // the role the task set before release stays visible
        pool.UpdateHeartbeat("w-dash-awaiting", 64);

        pool.RegisterWorker("w-dash-busy", []);
        pool.MarkBusy("w-dash-busy", "task-dash-busy");
        pool.GetWorker("w-dash-busy")!.CurrentModel = "dash-model";

        using var service = new DashboardStateService(
            pool, new GoalPipelineManager(), new DashboardLogSink(), new ProgressLog(), goalStore: null);

        var snapshot = await service.GetSnapshot();

        Assert.Equal(3, snapshot.TotalWorkers);
        Assert.Equal(1, snapshot.BusyWorkers);
        Assert.Equal(2, snapshot.IdleWorkers);
        Assert.Equal(1, snapshot.AvailableWorkers);
        Assert.Equal(1, snapshot.AwaitingReadyWorkers);

        // ── The clean worker: available, with every legacy field intact ─────────
        var cleanInfo = snapshot.Workers.Single(w => w.Id == "w-dash-clean");
        Assert.True(cleanInfo.IsAvailable);
        Assert.False(cleanInfo.AwaitingWorkerReady);
        Assert.False(cleanInfo.IsBusy);
        Assert.Equal(WorkerRole.Unspecified.ToString(), cleanInfo.Role);
        Assert.Null(cleanInfo.CurrentTaskId);
        Assert.Null(cleanInfo.CurrentModel);
        Assert.Equal(37, cleanInfo.ContextUsagePercent);
        Assert.True(cleanInfo.LastHeartbeat > DateTime.MinValue);
        Assert.True(cleanInfo.ConnectedAt > DateTime.MinValue);
        Assert.Equal("Available", cleanInfo.StatusLabel);
        Assert.Equal("badge-green", cleanInfo.StatusCssClass);

        // ── The awaiting worker: withheld, still carrying its role and context ──
        var awaitingInfo = snapshot.Workers.Single(w => w.Id == "w-dash-awaiting");
        Assert.False(awaitingInfo.IsAvailable);
        Assert.True(awaitingInfo.AwaitingWorkerReady);
        Assert.False(awaitingInfo.IsBusy);
        Assert.Null(awaitingInfo.CurrentTaskId); // the release cleared the task, the wait remains
        Assert.Equal(WorkerRole.Tester.ToString(), awaitingInfo.Role);
        Assert.Equal(64, awaitingInfo.ContextUsagePercent);
        Assert.True(awaitingInfo.LastHeartbeat > DateTime.MinValue);
        Assert.Equal("Awaiting Ready", awaitingInfo.StatusLabel);
        Assert.NotEqual("badge-green", awaitingInfo.StatusCssClass);

        // ── The busy worker: task, model and busy flag unchanged ────────────────
        var busyInfo = snapshot.Workers.Single(w => w.Id == "w-dash-busy");
        Assert.True(busyInfo.IsBusy);
        Assert.False(busyInfo.IsAvailable);
        Assert.False(busyInfo.AwaitingWorkerReady);
        Assert.Equal("task-dash-busy", busyInfo.CurrentTaskId);
        Assert.Equal("dash-model", busyInfo.CurrentModel);
        Assert.Equal("Busy", busyInfo.StatusLabel);
        Assert.Equal("badge-yellow", busyInfo.StatusCssClass);

        // The snapshot's rows and its aggregates describe the same capture.
        Assert.Equal(snapshot.Workers.Count(w => w.IsAvailable), snapshot.AvailableWorkers);
        Assert.Equal(snapshot.Workers.Count(w => w.AwaitingWorkerReady), snapshot.AwaitingReadyWorkers);
    }
}

/// <summary>
/// REAL-HTTP vectors for the /health contract pieces the endpoint vectors in
/// <see cref="WorkerPoolAvailabilityStatsTests"/> do not pin: the unchanged response status and
/// legacy per-entry key types, and the removed-worker exclusion through the served JSON.
/// </summary>
[Collection("HiveIntegration")]
public class WorkerPoolAvailabilityEndpointCompatTests
{
    private readonly HttpClient _client;
    private readonly HiveTestFactory _factory;

    public WorkerPoolAvailabilityEndpointCompatTests(HiveTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// THE UNCHANGED /health SURFACE: HTTP 200 with <c>status</c> "Healthy", the legacy top-level
    /// keys and types, and — for a registration/removal cycle — the removed worker's absence from
    /// the served per-worker array with the totals agreeing between two responses.
    /// </summary>
    [Fact]
    public async Task GetHealth_StatusLegacyTypesAndRemoval_Unchanged()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "availability-compat-" + Guid.NewGuid();

        using (var first = await GetHealthJsonAsync())
        {
            Assert.Equal("Healthy", first.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Number, first.RootElement.GetProperty("connectedWorkers").ValueKind);
            Assert.Equal(JsonValueKind.Number, first.RootElement.GetProperty("checkNumber").ValueKind);
        }

        pool.RegisterWorker(workerId, []);
        try
        {
            using var withWorker = await GetHealthJsonAsync();
            var wp = withWorker.RootElement.GetProperty("worker_pool");
            var entry = wp.GetProperty("workers").EnumerateArray()
                .Single(w => w.GetProperty("id").GetString() == workerId);

            // Legacy per-entry keys and types, unchanged.
            Assert.Equal(JsonValueKind.False, entry.GetProperty("is_busy").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("current_task_id").ValueKind);
            Assert.Equal(JsonValueKind.String, entry.GetProperty("id").ValueKind);

            // A freshly registered worker is genuinely available in the served JSON.
            Assert.True(entry.GetProperty("is_available").GetBoolean());
            Assert.False(entry.GetProperty("awaiting_worker_ready").GetBoolean());
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }

        // After removal the worker is gone from the served JSON: the nested total and the per-worker
        // array agree on its absence.
        using var after = await GetHealthJsonAsync();
        var wpAfter = after.RootElement.GetProperty("worker_pool");
        Assert.DoesNotContain(
            wpAfter.GetProperty("workers").EnumerateArray(),
            w => w.GetProperty("id").GetString() == workerId);
        Assert.Equal(
            wpAfter.GetProperty("workers").GetArrayLength(),
            wpAfter.GetProperty("total_workers").GetInt32());
        Assert.Equal(
            after.RootElement.GetProperty("connectedWorkers").GetInt32(),
            wpAfter.GetProperty("total_workers").GetInt32());
    }

    private async Task<JsonDocument> GetHealthJsonAsync()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}