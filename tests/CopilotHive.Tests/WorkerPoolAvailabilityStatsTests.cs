using CopilotHive.Dashboard;
using CopilotHive.Services;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Vectors for the honest AVAILABLE / AWAITING-READY operator visibility layered on top of the
/// worker pool's existing statistics, and for the pure row-status presentation helper the Workers
/// page renders.
/// </summary>
/// <remarks>
/// THE POINT OF EVERY VECTOR HERE IS THAT <c>IdleWorkers</c> AND AVAILABILITY ARE DIFFERENT FACTS.
/// A registered worker can be non-busy yet unassignable — awaiting its own accepted Ready, or still
/// publishing a completion — so a count that calls every non-busy worker "available" over-reports
/// capacity. These vectors pin BOTH halves: the historical <c>IdleWorkers</c> meaning is unchanged
/// (it still includes withheld workers) and the new counts tell the truth about eligibility.
/// </remarks>
public sealed class WorkerPoolAvailabilityStatsTests
{
    /// <summary>
    /// Registers an ACK-ENABLED instance and releases its negotiated completion, ending the SHORT
    /// publication hold so what remains withheld is the readiness wait alone — the longer fact this
    /// state exists for.
    /// </summary>
    private static CopilotHive.Services.ConnectedWorker AwaitingReadyWorker(
        WorkerPool pool, string id, string taskId)
    {
        var worker = pool.RegisterWorker(
            id, [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy(id, taskId);
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(worker, taskId));
        Assert.True(worker.AwaitingWorkerReady);

        // Only the readiness wait is left in force, so the assertions about this worker are about the
        // wait rather than about the publication interval.
        Assert.True(pool.ClearCompletionPublicationHold(worker));
        Assert.True(worker.AwaitingWorkerReady);
        Assert.False(worker.CompletionPublicationPending);
        return worker;
    }

    /// <summary>
    /// Registers an ACK-DISABLED instance and releases its negotiated completion, leaving ONLY the
    /// short completion-publication hold (no readiness wait).
    /// </summary>
    private static CopilotHive.Services.ConnectedWorker PublicationPendingWorker(
        WorkerPool pool, string id, string taskId)
    {
        var worker = pool.RegisterWorker(
            id, [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: false);
        pool.MarkBusy(id, taskId);
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(worker, taskId));
        return worker;
    }

    /// <summary>
    /// THE CORE SEMANTIC: <c>IdleWorkers</c> keeps counting withheld workers, while
    /// <c>AvailableWorkers</c> counts only workers the checked claim could actually take, and
    /// <c>AwaitingReadyWorkers</c> counts the readiness wait specifically.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: making <c>AvailableWorkers</c> a copy of <c>IdleWorkers</c> (i.e.
    /// re-labeling every non-busy worker as available), or making <c>IdleWorkers</c> exclude the
    /// withheld workers. The four counts below separate all four populations, so neither mutation can
    /// pass.
    /// </remarks>
    [Fact]
    public void GetWorkerStats_WithheldWorkers_AreIdleButNotAvailable()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-stats-clean", []);
        var awaiting = AwaitingReadyWorker(pool, "w-stats-awaiting", "task-stats-awaiting");
        var publishing = PublicationPendingWorker(pool, "w-stats-publishing", "task-stats-publishing");
        pool.RegisterWorker("w-stats-busy", []);
        pool.MarkBusy("w-stats-busy", "task-stats-busy");

        var stats = pool.GetWorkerStats();

        Assert.Equal(4, stats.TotalWorkers);
        Assert.Equal(1, stats.BusyWorkers);

        // THE UNCHANGED MEANING — non-busy, withheld members included.
        Assert.Equal(3, stats.IdleWorkers);

        // THE HONEST OBSERVATION — only the clean idle worker can be assigned.
        Assert.Equal(1, stats.AvailableWorkers);

        // …and exactly one worker is waiting for its own accepted Ready: the publication-pending
        // worker is withheld for a DIFFERENT reason and is not counted here.
        Assert.Equal(1, stats.AwaitingReadyWorkers);

        // The withheld instances really are in those states (the premise, not an assumption).
        Assert.True(awaiting.AwaitingWorkerReady);
        Assert.True(publishing.CompletionPublicationPending);
        Assert.False(publishing.AwaitingWorkerReady);
    }

    /// <summary>
    /// THE AVAILABILITY DEFINITION ITSELF: it follows the same state the checked claim accepts, so an
    /// awaiting-Ready worker becomes available exactly when its own accepted Ready clears the wait —
    /// and the withheld counts move in step.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: computing availability from the busy flag alone. That mutation leaves
    /// the awaiting worker "available" both before and after the Ready, so the before/after delta
    /// below cannot be produced.
    /// </remarks>
    [Fact]
    public void GetWorkerStats_AcceptedReady_IsWhatMakesAnAwaitingWorkerAvailable()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-ready-clean", []);
        var awaiting = AwaitingReadyWorker(pool, "w-ready-awaiting", "task-ready-awaiting");

        var before = pool.GetWorkerStats();
        Assert.Equal(1, before.AvailableWorkers);
        Assert.Equal(1, before.AwaitingReadyWorkers);

        // The accepted Ready: the released instance is idle with no task, so this is the idle/initial
        // shape, and it is the ONLY transition that ends the readiness wait.
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-awaiting", out var observed));
        Assert.True(pool.TryMarkIdleForReady(observed, queueEntryAbsent: true));
        Assert.False(awaiting.AwaitingWorkerReady);

        var after = pool.GetWorkerStats();
        Assert.Equal(2, after.AvailableWorkers);
        Assert.Equal(0, after.AwaitingReadyWorkers);

        // IdleWorkers never moved, because it never depended on either selection fact.
        Assert.Equal(before.IdleWorkers, after.IdleWorkers);
    }

    /// <summary>
    /// THE PER-WORKER PROJECTION of the same facts on the <c>/health</c> details snapshot: each entry
    /// carries the availability verdict and the readiness wait for its own worker, and the aggregate
    /// counts agree with the entries beside them.
    /// </summary>
    [Fact]
    public void GetDetailedStats_ReportsPerWorkerAvailabilityAndAggregatesFromTheSameCapture()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-detail-clean", []);
        AwaitingReadyWorker(pool, "w-detail-awaiting", "task-detail-awaiting");
        PublicationPendingWorker(pool, "w-detail-publishing", "task-detail-publishing");
        pool.RegisterWorker("w-detail-busy", []);
        pool.MarkBusy("w-detail-busy", "task-detail-busy");

        var stats = pool.GetDetailedStats();

        Assert.Equal(4, stats.TotalWorkers);
        Assert.Equal(1, stats.BusyWorkers);
        Assert.Equal(3, stats.IdleWorkers);
        Assert.Equal(1, stats.AvailableWorkers);
        Assert.Equal(1, stats.AwaitingReadyWorkers);

        Assert.Equal(4, stats.Workers.Count);

        var clean = stats.Workers.Single(w => w.Id == "w-detail-clean");
        Assert.True(clean.IsAvailable);
        Assert.False(clean.AwaitingWorkerReady);
        Assert.False(clean.IsBusy);

        var awaiting = stats.Workers.Single(w => w.Id == "w-detail-awaiting");
        Assert.False(awaiting.IsAvailable);
        Assert.True(awaiting.AwaitingWorkerReady);

        var publishing = stats.Workers.Single(w => w.Id == "w-detail-publishing");
        Assert.False(publishing.IsAvailable);
        Assert.False(publishing.AwaitingWorkerReady);

        var busy = stats.Workers.Single(w => w.Id == "w-detail-busy");
        Assert.False(busy.IsAvailable);
        Assert.False(busy.AwaitingWorkerReady);

        // THE AGGREGATE AGREES WITH THE ENTRIES, because both come from one capture.
        Assert.Equal(stats.Workers.Count(w => w.IsAvailable), stats.AvailableWorkers);
        Assert.Equal(stats.Workers.Count(w => w.AwaitingWorkerReady), stats.AwaitingReadyWorkers);
        Assert.Equal(stats.Workers.Count(w => !w.IsBusy), stats.IdleWorkers);
    }

    /// <summary>
    /// THE ROW-STATUS PRESENTATION HELPER distinguishes all four row states, so a non-busy row is
    /// never rendered as green available capacity unless it really was available.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: collapsing the non-busy states into the old two-way
    /// <c>IsBusy ? "Busy" : "Idle"</c> render. That mutation gives the awaiting-Ready and
    /// publication-pending rows the same label and class as the available row, so the three distinct
    /// expectations below cannot all hold.
    /// </remarks>
    [Fact]
    public void DescribeStatus_DistinguishesBusyAvailableAwaitingReadyAndUnavailable()
    {
        var busy = WorkerInfo.DescribeStatus(isBusy: true, isAvailable: false, awaitingWorkerReady: false);
        Assert.Equal("Busy", busy.Label);
        Assert.Equal("badge-yellow", busy.CssClass);

        var available = WorkerInfo.DescribeStatus(isBusy: false, isAvailable: true, awaitingWorkerReady: false);
        Assert.Equal("Available", available.Label);
        Assert.Equal("badge-green", available.CssClass);

        var awaiting = WorkerInfo.DescribeStatus(isBusy: false, isAvailable: false, awaitingWorkerReady: true);
        Assert.Equal("Awaiting Ready", awaiting.Label);
        Assert.NotEqual("badge-green", awaiting.CssClass);

        var other = WorkerInfo.DescribeStatus(isBusy: false, isAvailable: false, awaitingWorkerReady: false);
        Assert.Equal("Unavailable", other.Label);
        Assert.Equal("badge-muted", other.CssClass);

        // The four states are mutually distinct, so no two rows can be conflated.
        var labels = new[] { busy.Label, available.Label, awaiting.Label, other.Label };
        Assert.Equal(labels.Length, labels.Distinct().Count());
    }

    /// <summary>
    /// A BUSY WORKER IS NEVER GREEN even when the other captured flags are inconsistent, so the row
    /// label can never understate work in flight.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void DescribeStatus_BusyWinsRegardlessOfTheRemainingFlags(
        bool isAvailable, bool awaitingWorkerReady)
    {
        var status = WorkerInfo.DescribeStatus(
            isBusy: true, isAvailable: isAvailable, awaitingWorkerReady: awaitingWorkerReady);

        Assert.Equal("Busy", status.Label);
        Assert.NotEqual("badge-green", status.CssClass);
    }

    /// <summary>
    /// The instance-level projection used by the page agrees with the helper for every state.
    /// </summary>
    [Fact]
    public void WorkerInfo_StatusProjection_UsesTheCapturedFlags()
    {
        var available = new WorkerInfo { Id = "w1", IsAvailable = true };
        Assert.Equal("Available", available.StatusLabel);
        Assert.Equal("badge-green", available.StatusCssClass);

        var withheld = new WorkerInfo { Id = "w2", AwaitingWorkerReady = true };
        Assert.Equal("Awaiting Ready", withheld.StatusLabel);

        var busy = new WorkerInfo { Id = "w3", IsBusy = true, IsAvailable = true };
        Assert.Equal("Busy", busy.StatusLabel);

        var other = new WorkerInfo { Id = "w4" };
        Assert.Equal("Unavailable", other.StatusLabel);
        Assert.Equal("badge-muted", other.StatusCssClass);
    }

    /// <summary>
    /// THE DASHBOARD SNAPSHOT derives its worker rows AND its worker aggregates from ONE pool-owned
    /// capture, so a withheld worker is carried through as non-busy-but-unavailable while the
    /// non-busy aggregate still counts it.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: reading the pool twice (rows from one read, counts from another) or
    /// dropping the availability flags from the projection. The agreement assertions below compare the
    /// snapshot's own rows against its own aggregates, which a second independent read cannot
    /// guarantee.
    /// </remarks>
    [Fact]
    public async Task DashboardSnapshot_ProjectsAvailabilityFromOneCapture()
    {
        var pool = new WorkerPool();
        pool.RegisterWorker("w-snap-clean", []);
        AwaitingReadyWorker(pool, "w-snap-awaiting", "task-snap-awaiting");
        PublicationPendingWorker(pool, "w-snap-publishing", "task-snap-publishing");
        pool.RegisterWorker("w-snap-busy", []);
        pool.MarkBusy("w-snap-busy", "task-snap-busy");

        using var service = new DashboardStateService(
            pool,
            new GoalPipelineManager(),
            new DashboardLogSink(),
            new ProgressLog(),
            goalStore: null);

        var snapshot = await service.GetSnapshot();

        Assert.Equal(4, snapshot.TotalWorkers);
        Assert.Equal(1, snapshot.BusyWorkers);
        Assert.Equal(3, snapshot.IdleWorkers);
        Assert.Equal(1, snapshot.AvailableWorkers);
        Assert.Equal(1, snapshot.AwaitingReadyWorkers);

        var awaiting = snapshot.Workers.Single(w => w.Id == "w-snap-awaiting");
        Assert.False(awaiting.IsBusy);
        Assert.False(awaiting.IsAvailable);
        Assert.True(awaiting.AwaitingWorkerReady);
        Assert.Equal("Awaiting Ready", awaiting.StatusLabel);

        var clean = snapshot.Workers.Single(w => w.Id == "w-snap-clean");
        Assert.True(clean.IsAvailable);
        Assert.Equal("Available", clean.StatusLabel);

        // The snapshot's own rows agree with its own aggregates.
        Assert.Equal(snapshot.Workers.Count(w => w.IsAvailable), snapshot.AvailableWorkers);
        Assert.Equal(snapshot.Workers.Count(w => w.AwaitingWorkerReady), snapshot.AwaitingReadyWorkers);
        Assert.Equal(snapshot.Workers.Count(w => !w.IsBusy), snapshot.IdleWorkers);

        // Every pre-existing dashboard field keeps its meaning.
        var busy = snapshot.Workers.Single(w => w.Id == "w-snap-busy");
        Assert.Equal("task-snap-busy", busy.CurrentTaskId);
        Assert.True(busy.LastHeartbeat > DateTime.MinValue);
        Assert.True(busy.ConnectedAt > DateTime.MinValue);
    }

    /// <summary>
    /// THE WORKER DETAIL PAGE CONSUMES THE SHARED PRESENTATION HELPER: its Status card renders
    /// <c>_worker.StatusCssClass</c> / <c>_worker.StatusLabel</c> instead of a two-way
    /// <c>IsBusy ? … : …</c> badge, so a non-busy-but-withheld worker is never shown as green Idle.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: restoring the two-way badge, restating the badge policy in the page
    /// (a local <c>? "Busy" : "Idle"</c> or a hardcoded <c>badge-green</c> for the non-busy branch),
    /// or rendering the shared helper while ignoring one of its two outputs — e.g. keeping the label
    /// and re-deriving the class locally, which the exact-markup assertion below rejects.
    /// </remarks>
    [Fact]
    public void WorkerDetail_StatusCard_ConsumesTheSharedStatusPresentation()
    {
        var page = ReadProductionSource("src/CopilotHive/Components/Pages/WorkerDetail.razor");

        Assert.Contains(
            "<span class=\"badge @_worker.StatusCssClass\">@_worker.StatusLabel</span>",
            page,
            StringComparison.Ordinal);

        // The shared policy lives on the model, never restated here: the page consumes the two
        // resolved values and does not re-derive either from the raw busy flag.
        Assert.DoesNotContain("IsBusy", page, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-green", page, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-yellow", page, StringComparison.Ordinal);
        Assert.DoesNotContain("badge-muted", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Busy\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Idle\"", page, StringComparison.Ordinal);

        // The rest of the page is untouched: its fields, progress section and refresh wiring remain.
        Assert.Contains("GetDisplayModel(_info.RoleModels)", page, StringComparison.Ordinal);
        Assert.Contains("State.OnStateChanged += RefreshState;", page, StringComparison.Ordinal);
        Assert.Contains("State.OnStateChanged -= RefreshState;", page, StringComparison.Ordinal);
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
/// Integration vectors for the ADDITIVE <c>/health</c> worker-pool contract: the new snake_case
/// fields are present with their exact names, and the top-level worker total and the nested
/// <c>worker_pool.total_workers</c> of one response are the SAME capture.
/// </summary>
/// <remarks>
/// BOOTED THROUGH THE REAL APPLICATION: the route is read from <see cref="HiveTestFactory"/> rather
/// than re-implemented here, so the assertions bind to the production projection.
/// </remarks>
[Collection("HiveIntegration")]
public class WorkerPoolAvailabilityEndpointTests
{
    private readonly HttpClient _client;
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public WorkerPoolAvailabilityEndpointTests(HiveTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>Sends GET /health and parses the JSON response body.</summary>
    /// <returns>A parsed <see cref="JsonDocument"/> of the response body.</returns>
    private async Task<JsonDocument> GetHealthJsonAsync()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// THE EXACT ADDITIVE FIELD NAMES: <c>available_workers</c> and <c>awaiting_ready_workers</c> on
    /// the pool, and <c>is_available</c> and <c>awaiting_worker_ready</c> on a worker entry.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: renaming or dropping any <c>[JsonPropertyName]</c>. The old names
    /// (<c>total_workers</c>, <c>idle_workers</c>, <c>busy_workers</c>, <c>is_busy</c>,
    /// <c>current_task_id</c>) are asserted in the same response, so the surface must be purely
    /// additive to pass.
    /// </remarks>
    [Fact]
    public async Task GetHealth_ExposesAvailabilityFieldsWithTheExactNames()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "availability-endpoint-" + Guid.NewGuid();
        pool.RegisterWorker(workerId, []);

        try
        {
            using var json = await GetHealthJsonAsync();

            var wp = json.RootElement.GetProperty("worker_pool");

            // The retained contract, unchanged.
            Assert.True(wp.TryGetProperty("total_workers", out var total));
            Assert.True(wp.TryGetProperty("idle_workers", out var idle));
            Assert.True(wp.TryGetProperty("busy_workers", out var busy));
            Assert.True(total.GetInt32() >= 0);
            Assert.True(idle.GetInt32() >= 0);
            Assert.True(busy.GetInt32() >= 0);

            // The new observations, with exactly these names.
            Assert.True(wp.TryGetProperty("available_workers", out var available));
            Assert.True(wp.TryGetProperty("awaiting_ready_workers", out var awaitingReady));
            Assert.True(available.GetInt32() >= 0);
            Assert.True(awaitingReady.GetInt32() >= 0);

            var entry = wp.GetProperty("workers").EnumerateArray()
                .Single(w => w.GetProperty("id").GetString() == workerId);

            Assert.True(entry.TryGetProperty("is_busy", out var isBusy));
            Assert.True(entry.TryGetProperty("current_task_id", out _));
            Assert.True(entry.TryGetProperty("is_available", out var isAvailable));
            Assert.True(entry.TryGetProperty("awaiting_worker_ready", out var awaitingWorkerReady));

            // The freshly registered worker is busy-free, task-free and withheld by nothing, so it is
            // genuinely available — which also proves the new field is populated, not a constant.
            Assert.False(isBusy.GetBoolean());
            Assert.True(isAvailable.GetBoolean());
            Assert.False(awaitingWorkerReady.GetBoolean());
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }
    }

    /// <summary>
    /// ONE RESPONSE, ONE CAPTURE: the top-level <c>connectedWorkers</c> and the nested
    /// <c>worker_pool.total_workers</c> agree, because the route now projects both from the SAME
    /// snapshot instead of performing a second independent read.
    /// </summary>
    [Fact]
    public async Task GetHealth_TopLevelAndNestedWorkerTotals_Agree()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "availability-torn-" + Guid.NewGuid();
        pool.RegisterWorker(workerId, []);

        try
        {
            using var json = await GetHealthJsonAsync();

            var connectedWorkers = json.RootElement.GetProperty("connectedWorkers").GetInt32();
            var nestedTotal = json.RootElement.GetProperty("worker_pool")
                .GetProperty("total_workers").GetInt32();

            Assert.Equal(nestedTotal, connectedWorkers);
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }
    }

    /// <summary>
    /// A WITHHELD WORKER IS NON-BUSY BUT NOT AVAILABLE, reflected end-to-end: an awaiting-Ready
    /// instance appears in the per-worker array as <c>is_available = false</c> with
    /// <c>awaiting_worker_ready = true</c>, while the pool's <c>idle_workers</c> still counts it.
    /// </summary>
    /// <remarks>
    /// THE MUTATION THIS KILLS: reporting the withheld worker as available (the original defect) —
    /// the per-worker and aggregate assertions below both fail for that mutation.
    /// </remarks>
    [Fact]
    public async Task GetHealth_AwaitingReadyWorker_IsNotCountedAsAvailable()
    {
        var pool = _factory.Services.GetRequiredService<WorkerPool>();
        var workerId = "availability-awaiting-" + Guid.NewGuid();
        var taskId = "task-availability-awaiting-" + Guid.NewGuid();

        var worker = pool.RegisterWorker(
            workerId, [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy(workerId, taskId);
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(worker, taskId));
        Assert.True(pool.ClearCompletionPublicationHold(worker));
        Assert.True(worker.AwaitingWorkerReady);

        try
        {
            using var json = await GetHealthJsonAsync();
            var wp = json.RootElement.GetProperty("worker_pool");

            var entry = wp.GetProperty("workers").EnumerateArray()
                .Single(w => w.GetProperty("id").GetString() == workerId);

            Assert.False(entry.GetProperty("is_busy").GetBoolean());
            Assert.False(entry.GetProperty("is_available").GetBoolean());
            Assert.True(entry.GetProperty("awaiting_worker_ready").GetBoolean());

            // The withheld worker is still part of the non-busy population — IdleWorkers is unchanged.
            Assert.True(wp.GetProperty("idle_workers").GetInt32() >= 1);
            Assert.True(wp.GetProperty("awaiting_ready_workers").GetInt32() >= 1);

            // …and the aggregate availability agrees with the entries in the SAME response.
            var availableInEntries = wp.GetProperty("workers").EnumerateArray()
                .Count(w => w.GetProperty("is_available").GetBoolean());
            Assert.Equal(availableInEntries, wp.GetProperty("available_workers").GetInt32());
        }
        finally
        {
            pool.RemoveWorker(workerId);
        }
    }
}

/// <summary>
/// STRUCTURAL vectors binding the two "one capture" claims to the production source, where a
/// post-condition alone cannot see them.
/// </summary>
/// <remarks>
/// A SECOND, INDEPENDENT READ IS NOT OBSERVABLE FROM THE RESPONSE. Two reads that agree when the pool
/// happens to be quiet look exactly like one read, yet reintroduce the torn-total risk under
/// contention — which is why these vectors assert the SCOPE of the single capture rather than its
/// observed output.
/// </remarks>
public sealed class WorkerStatusCaptureShapeTests
{
    /// <summary>
    /// THE <c>/health</c> PROJECTION TAKES ONE SNAPSHOT: the handler calls
    /// <c>GetDetailedStats()</c> exactly once, uses that same value for BOTH the top-level worker
    /// total and the nested <c>worker_pool</c>, and performs NO second pool read of its own.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: re-adding a <c>GetAllWorkers().Count</c> read beside the snapshot,
    /// or calling <c>GetDetailedStats()</c> twice (once per field). Both restore two independent
    /// instants in one response while leaving the quiet-pool output identical.
    /// </remarks>
    [Fact]
    public void HealthEndpoint_ProjectsBothWorkerTotalsFromOneSnapshot()
    {
        var source = StripLineComments(ReadProductionSource("src/CopilotHive/ApiEndpoints.cs"));

        var start = source.IndexOf("app.MapGet(\"/health\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the /health route is gone.");
        var end = source.IndexOf("app.MapGet(\"/health/utilization\"", start, StringComparison.Ordinal);
        Assert.True(end > start, "the /health route's following route is gone.");
        var handler = source[start..end];

        Assert.Equal(1, CountOccurrences(handler, "GetDetailedStats()"));
        Assert.DoesNotContain("GetAllWorkers()", handler, StringComparison.Ordinal);

        // ONE capture, bound to a local, and BOTH projections read THAT local.
        Assert.Equal(1, CountOccurrences(handler, "var workerPoolStats = workerPool.GetDetailedStats();"));
        Assert.Contains("ConnectedWorkers = workerPoolStats.TotalWorkers,", handler, StringComparison.Ordinal);
        Assert.Contains("WorkerPool = workerPoolStats,", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE POOL'S STATISTICS ARE BUILT FROM THE SHARED CAPTURE, TAKEN UNDER THE ACTIVITY LOCK, AND THE
    /// AVAILABILITY VERDICT COMPOSES THE EXISTING SELECTABILITY PREDICATE.
    /// </summary>
    /// <remarks>
    /// THE MUTATIONS THIS KILLS: reading <c>_workers.Values</c> directly in either statistics method
    /// (an unlocked, potentially torn read); restating the availability expression as its own
    /// competing conjunction instead of composing <c>IsSelectableIdleNoLock</c> (from which the
    /// verdict would silently drift); and moving the capture's lock away from the copied fields.
    /// </remarks>
    [Fact]
    public void WorkerPool_StatisticsComeFromTheLockedSharedCapture()
    {
        var source = ReadProductionSource("src/CopilotHive/Services/WorkerPool.cs");

        // ── the availability predicate COMPOSES the shared one ────────────────────────────────
        var availabilityStart = source.IndexOf(
            "private static bool IsAvailableIdleNoLock(", StringComparison.Ordinal);
        Assert.True(availabilityStart >= 0, "the availability predicate is gone.");
        var availabilityEnd = source.IndexOf(
            "private List<WorkerStatusSnapshot> CaptureWorkerStatusNoLock(",
            availabilityStart,
            StringComparison.Ordinal);
        Assert.True(availabilityEnd > availabilityStart, "the capture is gone.");
        var availability = source[availabilityStart..availabilityEnd];

        Assert.Contains("worker.CurrentTaskId is null", availability, StringComparison.Ordinal);
        Assert.Contains("IsSelectableIdleNoLock(worker)", availability, StringComparison.Ordinal);

        // ── the capture copies the fields INSIDE the activity lock ────────────────────────────
        var captureStart = source.IndexOf(
            "internal IReadOnlyList<WorkerStatusSnapshot> CaptureWorkerStatus()", StringComparison.Ordinal);
        Assert.True(captureStart >= 0, "the shared capture is gone.");
        var captureEnd = source.IndexOf(
            "public int ConnectedWorkerCount", captureStart, StringComparison.Ordinal);
        Assert.True(captureEnd > captureStart, "the capture's following member is gone.");
        var capture = source[captureStart..captureEnd];

        var lockIndex = capture.IndexOf("lock (_activityLock)", StringComparison.Ordinal);
        Assert.True(lockIndex >= 0, "the shared capture no longer takes the activity lock.");
        Assert.True(
            BraceScopedBody(capture, lockIndex).Contains(
                "CaptureWorkerStatusNoLock()", StringComparison.Ordinal),
            "the capture must be performed INSIDE the activity lock's body, not merely after the lock "
            + "keyword: a read outside the lock is exactly the torn capture this exists to prevent.");

        // ── BOTH statistics methods project the capture, never the live dictionary ────────────
        foreach (var signature in new[]
                 {
                     "public WorkerPoolStats GetWorkerStats()",
                     "public WorkerPoolStatsDto GetDetailedStats()",
                 })
        {
            var methodStart = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(methodStart >= 0, $"'{signature}' is gone.");

            var methodEnd = source.IndexOf("\n    /// <summary>", methodStart, StringComparison.Ordinal);
            Assert.True(methodEnd > methodStart, $"'{signature}' has no following member.");
            var method = source[methodStart..methodEnd];

            Assert.Contains("var captured = CaptureWorkerStatus();", method, StringComparison.Ordinal);
            Assert.DoesNotContain("_workers.Values", method, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Strips <c>//</c> line comments so the structural assertions read CODE rather than prose:
    /// the production file's comments legitimately mention the very call these assertions forbid, so
    /// without this a prohibition could be satisfied by a comment describing the removal.
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
    /// Returns the body of the block that OPENS at the first <c>{</c> at or after <paramref name="from"/>,
    /// delimited by BALANCED BRACE COUNTING, so a scope claim is made against the matched body rather
    /// than against text order.
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
