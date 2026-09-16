using CopilotHive.Models;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Tests;

public sealed class WorkerPoolTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WorkerPool CreatePool() => new();

    /// <summary>Registers a worker and back-dates its heartbeat to simulate staleness.</summary>
    private static ConnectedWorker RegisterWithHeartbeat(
        WorkerPool pool, string id,
        DateTime lastHeartbeat, bool isBusy = false)
    {
        var worker = pool.RegisterWorker(id, []);
        worker.LastHeartbeat = lastHeartbeat;
        worker.IsBusy = isBusy;
        return worker;
    }

    // ── GetStaleWorkers ───────────────────────────────────────────────────────

    #region GetStaleWorkers — empty pool

    [Fact]
    public void GetStaleWorkers_EmptyPool_ReturnsEmpty()
    {
        var pool = CreatePool();

        var result = pool.GetStaleWorkers(TimeSpan.FromMinutes(1));

        Assert.Empty(result);
    }

    #endregion

    #region GetStaleWorkers — no stale workers

    [Fact]
    public void GetStaleWorkers_AllWorkersRecent_ReturnsEmpty()
    {
        var pool = CreatePool();
        var now = DateTime.UtcNow;
        RegisterWithHeartbeat(pool, "w1", now.AddSeconds(-10));
        RegisterWithHeartbeat(pool, "w2", now.AddSeconds(-5));

        var result = pool.GetStaleWorkers(TimeSpan.FromMinutes(1));

        Assert.Empty(result);
    }

    #endregion

    #region GetStaleWorkers — just under boundary (not stale)

    [Fact]
    public void GetStaleWorkers_HeartbeatJustUnderTimeout_NotConsideredStale()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(1);
        // LastHeartbeat is 1 second fresher than the timeout boundary — clearly not stale.
        // Note: a test for the exact boundary (now - timeout == LastHeartbeat) is omitted because
        // DateTime.UtcNow advances between the heartbeat assignment and the GetStaleWorkers call,
        // making a reliable exact-equality assertion impossible without a controlled time source.
        var withinBoundary = DateTime.UtcNow - timeout + TimeSpan.FromSeconds(1);
        RegisterWithHeartbeat(pool, "w1", withinBoundary);

        var result = pool.GetStaleWorkers(timeout);

        Assert.Empty(result);
    }

    #endregion

    #region GetStaleWorkers — just past boundary (stale)

    [Fact]
    public void GetStaleWorkers_HeartbeatJustPastTimeout_IsStale()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(1);
        var justPast = DateTime.UtcNow - timeout - TimeSpan.FromMilliseconds(1);
        RegisterWithHeartbeat(pool, "w1", justPast);

        var result = pool.GetStaleWorkers(timeout);

        Assert.Single(result);
        Assert.Equal("w1", result[0].Id);
    }

    #endregion

    #region GetStaleWorkers — some stale

    [Fact]
    public void GetStaleWorkers_SomeStale_ReturnsOnlyStale()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;

        RegisterWithHeartbeat(pool, "fresh", now.AddSeconds(-10));
        RegisterWithHeartbeat(pool, "stale1", now.AddMinutes(-10));
        RegisterWithHeartbeat(pool, "stale2", now.AddMinutes(-6));

        var result = pool.GetStaleWorkers(timeout);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, w => w.Id == "stale1");
        Assert.Contains(result, w => w.Id == "stale2");
        Assert.DoesNotContain(result, w => w.Id == "fresh");
    }

    #endregion

    #region GetStaleWorkers — all stale

    [Fact]
    public void GetStaleWorkers_AllStale_ReturnsAll()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(1);
        var now = DateTime.UtcNow;

        RegisterWithHeartbeat(pool, "w1", now.AddMinutes(-2));
        RegisterWithHeartbeat(pool, "w2", now.AddMinutes(-3));

        var result = pool.GetStaleWorkers(timeout);

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region GetStaleWorkers — does not mutate pool

    [Fact]
    public void GetStaleWorkers_DoesNotRemoveWorkers()
    {
        var pool = CreatePool();
        RegisterWithHeartbeat(pool, "w1",
            DateTime.UtcNow.AddMinutes(-10));

        _ = pool.GetStaleWorkers(TimeSpan.FromMinutes(1));

        Assert.Single(pool.GetAllWorkers());
    }

    #endregion

    // ── GetWorkerStats ────────────────────────────────────────────────────────

    #region GetWorkerStats — empty pool

    [Fact]
    public void GetWorkerStats_EmptyPool_ReturnsZeroCounts()
    {
        var pool = CreatePool();

        var stats = pool.GetWorkerStats();

        Assert.Equal(0, stats.TotalWorkers);
        Assert.Equal(0, stats.BusyWorkers);
        Assert.Equal(0, stats.IdleWorkers);
        Assert.Empty(stats.WorkersByRole);
    }

    #endregion

    #region GetWorkerStats — single idle worker

    [Fact]
    public void GetWorkerStats_SingleIdleWorker_CorrectCounts()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);

        var stats = pool.GetWorkerStats();

        Assert.Equal(1, stats.TotalWorkers);
        Assert.Equal(0, stats.BusyWorkers);
        Assert.Equal(1, stats.IdleWorkers);
    }

    #endregion

    #region GetWorkerStats — single busy worker

    [Fact]
    public void GetWorkerStats_SingleBusyWorker_CorrectCounts()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");

        var stats = pool.GetWorkerStats();

        Assert.Equal(1, stats.TotalWorkers);
        Assert.Equal(1, stats.BusyWorkers);
        Assert.Equal(0, stats.IdleWorkers);
    }

    #endregion

    #region GetWorkerStats — mixed busy and idle

    [Fact]
    public void GetWorkerStats_MixedBusyAndIdle_CorrectCounts()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.RegisterWorker("w2", []);
        pool.RegisterWorker("w3", []);
        pool.MarkBusy("w1", "task-1");
        pool.MarkBusy("w3", "task-2");

        var stats = pool.GetWorkerStats();

        Assert.Equal(3, stats.TotalWorkers);
        Assert.Equal(2, stats.BusyWorkers);
        Assert.Equal(1, stats.IdleWorkers);
    }

    #endregion

    #region GetWorkerStats — WorkersByRole

    [Fact]
    public void GetWorkerStats_MultipleWorkers_WorkersByRoleCorrect()
    {
        var pool = CreatePool();
        pool.RegisterWorker("c1", []);
        pool.RegisterWorker("c2", []);
        pool.RegisterWorker("r1", []);
        pool.RegisterWorker("t1", []);

        var stats = pool.GetWorkerStats();

        var unspecifiedKey = WorkerRole.Unspecified.ToString();

        Assert.Single(stats.WorkersByRole);
        Assert.Equal(4, stats.WorkersByRole[unspecifiedKey]);
    }

    [Fact]
    public void GetWorkerStats_TwoWorkers_WorkersByRoleHasOneEntry()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.RegisterWorker("w2", []);

        var stats = pool.GetWorkerStats();

        Assert.Single(stats.WorkersByRole);
        Assert.Equal(2, stats.WorkersByRole[WorkerRole.Unspecified.ToString()]);
    }

    #endregion

    // ── GetDetailedStats ──────────────────────────────────────────────────────

    #region GetDetailedStats — empty pool

    [Fact]
    public void GetDetailedStats_EmptyPool_ReturnsZeroCounts()
    {
        var pool = CreatePool();

        var stats = pool.GetDetailedStats();

        Assert.Equal(0, stats.TotalWorkers);
        Assert.Equal(0, stats.BusyWorkers);
        Assert.Equal(0, stats.IdleWorkers);
        Assert.Empty(stats.Workers);
    }

    #endregion

    #region GetDetailedStats — mixed workers

    [Fact]
    public void GetDetailedStats_MixedWorkers_CorrectCountsAndEntries()
    {
        var pool = CreatePool();
        pool.RegisterWorker("c1", []);
        pool.RegisterWorker("g1", []);
        pool.MarkBusy("c1", "task-1");

        var stats = pool.GetDetailedStats();

        Assert.Equal(2, stats.TotalWorkers);
        Assert.Equal(1, stats.BusyWorkers);
        Assert.Equal(1, stats.IdleWorkers);
        Assert.Equal(2, stats.Workers.Count);

        var busy = stats.Workers.First(w => w.Id == "c1");
        Assert.Null(busy.Role);
        Assert.True(busy.IsBusy);
        Assert.Equal("task-1", busy.CurrentTaskId);

        var idle = stats.Workers.First(w => w.Id == "g1");
        Assert.Null(idle.Role);
        Assert.False(idle.IsBusy);
        Assert.Null(idle.CurrentTaskId);
    }

    #endregion

    // ── PurgeStaleWorkers ─────────────────────────────────────────────────────

    #region PurgeStaleWorkers — empty pool

    [Fact]
    public void PurgeStaleWorkers_EmptyPool_ReturnsEmpty()
    {
        var pool = CreatePool();

        var result = pool.PurgeStaleWorkers(TimeSpan.FromMinutes(1));

        Assert.Empty(result);
    }

    #endregion

    #region PurgeStaleWorkers — no stale workers

    [Fact]
    public void PurgeStaleWorkers_NoStaleWorkers_ReturnsEmptyAndPoolUnchanged()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.RegisterWorker("w2", []);

        var result = pool.PurgeStaleWorkers(TimeSpan.FromMinutes(5));

        Assert.Empty(result);
        Assert.Equal(2, pool.GetAllWorkers().Count);
    }

    #endregion

    #region PurgeStaleWorkers — some stale

    [Fact]
    public void PurgeStaleWorkers_SomeStale_RemovesOnlyStaleAndReturnsThem()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;

        RegisterWithHeartbeat(pool, "fresh", now.AddSeconds(-30));
        RegisterWithHeartbeat(pool, "stale1", now.AddMinutes(-10));
        RegisterWithHeartbeat(pool, "stale2", now.AddMinutes(-7));

        var result = pool.PurgeStaleWorkers(timeout);

        // Correct workers returned
        Assert.Equal(2, result.Count);
        Assert.Contains(result, w => w.Id == "stale1");
        Assert.Contains(result, w => w.Id == "stale2");

        // Fresh worker remains in pool
        Assert.Single(pool.GetAllWorkers());
        Assert.NotNull(pool.GetWorker("fresh"));

        // Stale workers removed from pool
        Assert.Null(pool.GetWorker("stale1"));
        Assert.Null(pool.GetWorker("stale2"));
    }

    #endregion

    #region PurgeStaleWorkers — all stale

    [Fact]
    public void PurgeStaleWorkers_AllStale_RemovesAllAndReturnsAll()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(1);
        var now = DateTime.UtcNow;

        RegisterWithHeartbeat(pool, "w1", now.AddMinutes(-2));
        RegisterWithHeartbeat(pool, "w2", now.AddMinutes(-5));

        var result = pool.PurgeStaleWorkers(timeout);

        Assert.Equal(2, result.Count);
        Assert.Empty(pool.GetAllWorkers());
    }

    #endregion

    #region PurgeStaleWorkers — pool count after purge

    [Fact]
    public void PurgeStaleWorkers_AfterPurge_PoolCountIsCorrect()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;

        RegisterWithHeartbeat(pool, "w1", now.AddSeconds(-10));
        RegisterWithHeartbeat(pool, "w2", now.AddMinutes(-3));
        RegisterWithHeartbeat(pool, "w3", now.AddMinutes(-3));

        pool.PurgeStaleWorkers(timeout);

        Assert.Single(pool.GetAllWorkers());
        Assert.NotNull(pool.GetWorker("w1"));
    }

    #endregion

    #region PurgeStaleWorkers — returned workers match pool snapshot

    [Fact]
    public void PurgeStaleWorkers_ReturnedWorkers_HaveCorrectIds()
    {
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(1);
        var now = DateTime.UtcNow;

        RegisterWithHeartbeat(pool, "alpha", now.AddMinutes(-2));
        RegisterWithHeartbeat(pool, "beta", now.AddMinutes(-3));

        var purged = pool.PurgeStaleWorkers(timeout);

        var ids = purged.Select(w => w.Id).OrderBy(id => id).ToList();
        Assert.Equal(["alpha", "beta"], ids);
    }

    #endregion

    // ── MarkBusy / GetWorkersWithTimedOutTasks (LastActivityAt semantics) ─────

    #region MarkBusy — sets LastActivityAt

    [Fact]
    public void MarkBusy_SetsLastActivityAt()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        // Back-date the activity clock to simulate a worker that finished a previous task long
        // ago — MarkBusy must reset it, otherwise the new task looks immediately timed out.
        pool.GetWorker("w1")!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        pool.MarkBusy("w1", "task-1");

        var worker = pool.GetWorker("w1");
        Assert.NotNull(worker);
        Assert.True(worker.IsBusy);
        Assert.Equal("task-1", worker.CurrentTaskId);
        Assert.NotNull(worker.CurrentTaskStartedAt);
        // LastActivityAt was reset to ~now by MarkBusy, discarding the stale prior value.
        Assert.True(DateTime.UtcNow - worker.LastActivityAt < TimeSpan.FromSeconds(5));
        // Consequently the freshly assigned worker is not a reclamation candidate.
        Assert.Empty(pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)));
    }

    #endregion

    #region GetWorkersWithTimedOutTasks — LastActivityAt based

    [Fact]
    public void GetWorkersWithTimedOutTasks_OldLastActivityAt_ReturnsWorker()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        pool.GetWorker("w1")!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        var only = Assert.Single(timedOut);
        Assert.Equal("w1", only.Id);
    }

    [Fact]
    public void GetWorkersWithTimedOutTasks_RecentLastActivityAt_ReturnsEmpty()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        // LastActivityAt defaults to ~now via MarkBusy

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        Assert.Empty(timedOut);
    }

    /// <summary>
    /// Regression: a task that started long ago is NOT reclaimed when the worker has
    /// recent task-specific stream activity — activity, not task start, drives the timeout.
    /// </summary>
    [Fact]
    public void GetWorkersWithTimedOutTasks_OldTaskStartButRecentActivity_NotReclaimed()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        worker.CurrentTaskStartedAt = DateTime.UtcNow.AddMinutes(-90); // old start
        worker.LastActivityAt = DateTime.UtcNow;                        // recent activity

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        Assert.Empty(timedOut);
    }

    /// <summary>
    /// A recent heartbeat must NOT count as task activity: a worker that heartbeats but
    /// sends no task-specific stream messages is still reclaimed.
    /// </summary>
    [Fact]
    public void GetWorkersWithTimedOutTasks_RecentHeartbeatButOldActivity_StillReclaimed()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        pool.UpdateHeartbeat("w1"); // recent heartbeat — must NOT reset LastActivityAt

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        var only = Assert.Single(timedOut);
        Assert.Equal("w1", only.Id);
    }

    [Fact]
    public void GetWorkersWithTimedOutTasks_IdleWorker_NotReturned()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.GetWorker("w1")!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var timedOut = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));

        Assert.Empty(timedOut);
    }

    #endregion

    #region ConnectedWorker activity defaults

    /// <summary>
    /// <see cref="ConnectedWorker.LastActivityAt"/> must be a <see cref="DateTime"/> that defaults
    /// to ~<see cref="DateTime.UtcNow"/> (so a freshly-registered worker is not immediately stale),
    /// and <see cref="ConnectedWorker.CurrentTaskStartedAt"/> must default to <c>null</c> (display-only).
    /// </summary>
    [Fact]
    public void RegisterWorker_LastActivityAtDefaultsToUtcNow()
    {
        var pool = CreatePool();
        var before = DateTime.UtcNow;

        var worker = pool.RegisterWorker("w1", []);

        // LastActivityAt is a DateTime (non-nullable) defaulting to ~now.
        Assert.True(worker.LastActivityAt >= before,
            "LastActivityAt must default to UtcNow");
        Assert.True(DateTime.UtcNow - worker.LastActivityAt < TimeSpan.FromSeconds(5),
            "LastActivityAt must be ~now, not far in the past");

        // CurrentTaskStartedAt is display/statistics only and null when idle.
        Assert.Null(worker.CurrentTaskStartedAt);

        // A freshly registered (idle) worker is never a reclamation candidate.
        Assert.Empty(pool.GetWorkersWithTimedOutTasks(TimeSpan.Zero));
    }

    #endregion

    // ── TouchActivity ─────────────────────────────────────────────────────────

    #region TouchActivity — synchronized activity authority

    [Fact]
    public void TouchActivity_KnownWorker_UpdatesLastActivityAtAndReturnsTrue()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        var before = DateTime.UtcNow;

        var touched = pool.TouchActivity("w1");

        Assert.True(touched);
        Assert.True(worker.LastActivityAt >= before,
            "TouchActivity must set LastActivityAt to ~now");
    }

    /// <summary>
    /// The activity update must be published through the same lock that reclamation uses,
    /// so a touched worker immediately stops being a timeout candidate.
    /// </summary>
    [Fact]
    public void TouchActivity_MakesWorkerNoLongerTimedOut()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        pool.GetWorker("w1")!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        Assert.Single(pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)));

        pool.TouchActivity("w1");

        Assert.Empty(pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void TouchActivity_UnknownWorker_ReturnsFalse()
    {
        var pool = CreatePool();

        Assert.False(pool.TouchActivity("ghost"));
    }

    [Fact]
    public void TouchActivity_DoesNotChangeHeartbeatOrBusyState()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        var heartbeat = DateTime.UtcNow.AddMinutes(-30);
        worker.LastHeartbeat = heartbeat;

        pool.TouchActivity("w1");

        Assert.Equal(heartbeat, worker.LastHeartbeat);
        Assert.True(worker.IsBusy);
        Assert.Equal("task-1", worker.CurrentTaskId);
    }

    #endregion

    // ── TryRemoveTimedOutWorker ───────────────────────────────────────────────

    #region TryRemoveTimedOutWorker — atomic re-check at removal time

    [Fact]
    public void TryRemoveTimedOutWorker_StillTimedOut_RemovesAndReturnsTrue()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        var worker = pool.GetWorker("w1")!;
        worker.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var removed = pool.TryRemoveTimedOutWorker("w1", TimeSpan.FromMinutes(60));

        Assert.True(removed);
        Assert.Null(pool.GetWorker("w1"));
        // The message channel is completed so the worker's stream loop terminates.
        Assert.False(worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    /// <summary>
    /// The core TOCTOU fix: a candidate selected by <see cref="WorkerPool.GetWorkersWithTimedOutTasks"/>
    /// that reports activity before removal must NOT be evicted.
    /// </summary>
    [Fact]
    public void TryRemoveTimedOutWorker_ActivityArrivedAfterSelection_DoesNotRemove()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        pool.GetWorker("w1")!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        // 1. Candidate selection sees the worker as timed out.
        var candidates = pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60));
        Assert.Single(candidates);

        // 2. Activity arrives between selection and removal.
        pool.TouchActivity("w1");

        // 3. Removal re-checks and refuses.
        var removed = pool.TryRemoveTimedOutWorker("w1", TimeSpan.FromMinutes(60));

        Assert.False(removed);
        Assert.NotNull(pool.GetWorker("w1"));
    }

    /// <summary>
    /// A candidate that completed its task (and went idle) between selection and removal
    /// must NOT be evicted either.
    /// </summary>
    [Fact]
    public void TryRemoveTimedOutWorker_WorkerWentIdleAfterSelection_DoesNotRemove()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1");
        pool.GetWorker("w1")!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        Assert.Single(pool.GetWorkersWithTimedOutTasks(TimeSpan.FromMinutes(60)));

        pool.MarkIdle("w1");

        Assert.False(pool.TryRemoveTimedOutWorker("w1", TimeSpan.FromMinutes(60)));
        Assert.NotNull(pool.GetWorker("w1"));
    }

    [Fact]
    public void TryRemoveTimedOutWorker_UnknownWorker_ReturnsFalse()
    {
        var pool = CreatePool();

        Assert.False(pool.TryRemoveTimedOutWorker("ghost", TimeSpan.Zero));
    }

    [Fact]
    public void TryRemoveTimedOutWorker_NotTimedOut_ReturnsFalseAndKeepsWorker()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.MarkBusy("w1", "task-1"); // fresh activity

        Assert.False(pool.TryRemoveTimedOutWorker("w1", TimeSpan.FromMinutes(60)));
        Assert.NotNull(pool.GetWorker("w1"));
    }

    #endregion

    // ── Concurrency: activity vs. reclamation ─────────────────────────────────

    #region Concurrent activity and reclamation

    /// <summary>
    /// Drives <c>TouchActivity</c> and the select→remove reclamation sequence concurrently from
    /// two dedicated threads, rendezvousing on a <see cref="Barrier"/> each round. Because both
    /// paths take the same lock and removal re-checks the condition, a worker whose activity was
    /// recorded can never be evicted: every successful removal must be justified by the worker's
    /// timestamp still being older than the timeout.
    /// </summary>
    [Fact]
    public void ConcurrentTouchAndReclaim_NeverRemovesWorkerWithFreshActivity()
    {
        const int rounds = 200;
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(60);

        // 3 participants: the main thread stages exactly one timed-out worker per round, so the
        // toucher and the reclaimer always contend over the same worker.
        var barrier = new Barrier(3);
        var exceptions = new List<Exception>();
        var falseEvictions = 0;
        var removals = 0;
        var touches = 0;

        var reclaimer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < rounds; i++)
                {
                    barrier.SignalAndWait();
                    foreach (var candidate in pool.GetWorkersWithTimedOutTasks(timeout))
                    {
                        if (!pool.TryRemoveTimedOutWorker(candidate.Id, timeout))
                            continue;

                        Interlocked.Increment(ref removals);
                        // Removal happened: the worker must NOT have fresh activity. Reading the
                        // timestamp is safe — the worker is out of the pool, so TouchActivity
                        // can no longer reach it.
                        if (DateTime.UtcNow - candidate.LastActivityAt <= timeout)
                            Interlocked.Increment(ref falseEvictions);
                    }
                    barrier.SignalAndWait();
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        var toucher = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < rounds; i++)
                {
                    barrier.SignalAndWait();
                    if (pool.TouchActivity($"w-{i}"))
                        Interlocked.Increment(ref touches);
                    barrier.SignalAndWait();
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        reclaimer.Start();
        toucher.Start();

        for (var i = 0; i < rounds; i++)
        {
            var id = $"w-{i}";
            pool.RegisterWorker(id, []);
            pool.MarkBusy(id, $"task-{i}");
            pool.GetWorker(id)!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

            barrier.SignalAndWait(TestContext.Current.CancellationToken);  // round start — both threads race
            barrier.SignalAndWait(TestContext.Current.CancellationToken);  // round end

            pool.RemoveWorker(id);    // clear the stage for the next round
        }

        Assert.True(reclaimer.Join(TimeSpan.FromSeconds(30)), "reclaimer thread did not finish");
        Assert.True(toucher.Join(TimeSpan.FromSeconds(30)), "toucher thread did not finish");

        Assert.Empty(exceptions);
        Assert.Equal(0, falseEvictions);
        // Sanity: the interleaving really exercised both paths, so the result is not vacuous.
        Assert.True(removals + touches > 0, "expected the race to exercise removals and/or touches");
    }

    /// <summary>
    /// <c>MarkBusy</c> must publish <see cref="ConnectedWorker.LastActivityAt"/> together with
    /// <see cref="ConnectedWorker.IsBusy"/>/<see cref="ConnectedWorker.CurrentTaskId"/>. A reader
    /// racing the assignment therefore observes either "idle" (not a candidate) or "busy with a
    /// fresh timestamp" (not a candidate) — never "busy with the previous task's stale timestamp".
    /// </summary>
    [Fact]
    public void ConcurrentMarkBusyAndSelect_NeverObservesStaleActivityForNewTask()
    {
        const int rounds = 200;
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(60);

        // Workers are pre-created idle with a deliberately ancient LastActivityAt, mimicking a
        // worker that finished a previous task long ago and is now being re-assigned.
        for (var i = 0; i < rounds; i++)
        {
            var id = $"w-{i}";
            pool.RegisterWorker(id, []);
            pool.GetWorker(id)!.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);
        }

        var barrier = new Barrier(2);
        var exceptions = new List<Exception>();
        var staleObservations = 0;

        var assigner = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < rounds; i++)
                {
                    barrier.SignalAndWait();
                    pool.MarkBusy($"w-{i}", $"task-{i}");
                    barrier.SignalAndWait();
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        var selector = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < rounds; i++)
                {
                    barrier.SignalAndWait();
                    if (pool.GetWorkersWithTimedOutTasks(timeout).Count > 0)
                        Interlocked.Increment(ref staleObservations);
                    barrier.SignalAndWait();
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        assigner.Start();
        selector.Start();
        Assert.True(assigner.Join(TimeSpan.FromSeconds(30)), "assigner thread did not finish");
        Assert.True(selector.Join(TimeSpan.FromSeconds(30)), "selector thread did not finish");

        Assert.Empty(exceptions);
        Assert.Equal(0, staleObservations);
    }

    #endregion

    // ── PurgeStaleWorkers vs. activity/heartbeat updates ──────────────────────

    #region PurgeStaleWorkers — fresh workers are never transiently removed

    /// <summary>
    /// A fresh worker must be left in place by <see cref="WorkerPool.PurgeStaleWorkers"/>, not
    /// removed and re-added. The earlier remove-then-reinsert implementation made fresh workers
    /// transiently absent from the pool, which silently dropped concurrent activity updates.
    /// </summary>
    [Fact]
    public void PurgeStaleWorkers_FreshWorker_StaysInPoolWithActivityPreserved()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("fresh", []);
        pool.MarkBusy("fresh", "task-1");
        var activityBeforePurge = worker.LastActivityAt;
        var heartbeatBeforePurge = worker.LastHeartbeat;

        var removed = pool.PurgeStaleWorkers(TimeSpan.FromMinutes(5));

        Assert.Empty(removed);
        // Same instance, still present — never removed and re-added.
        Assert.Same(worker, pool.GetWorker("fresh"));
        Assert.Equal(activityBeforePurge, worker.LastActivityAt);
        Assert.Equal(heartbeatBeforePurge, worker.LastHeartbeat);
        // The channel of a surviving worker must not be completed.
        Assert.True(worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    /// <summary>
    /// The purge still completes the channel of workers it actually evicts.
    /// </summary>
    [Fact]
    public void PurgeStaleWorkers_StaleWorker_ChannelCompleted()
    {
        var pool = CreatePool();
        var stale = pool.RegisterWorker("stale", []);
        stale.LastHeartbeat = DateTime.UtcNow.AddMinutes(-10);

        var removed = pool.PurgeStaleWorkers(TimeSpan.FromMinutes(5));

        Assert.Same(stale, Assert.Single(removed));
        Assert.Null(pool.GetWorker("stale"));
        Assert.False(stale.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    #endregion

    #region PurgeStaleWorkers — concurrent activity is never lost

    /// <summary>
    /// Reproduces the exact interleaving the reviewer called out: WorkStream resolves a worker,
    /// a purge pass runs over the pool, <c>TouchActivity</c> lands during that pass, and the
    /// inactivity scan runs immediately afterwards.
    /// <para>
    /// With the old remove-then-reinsert purge, a fresh worker was transiently absent from the
    /// dictionary while the pass ran. A <c>TouchActivity</c> landing in that window returned
    /// <c>false</c> and silently dropped the update; the purge then reinserted the unchanged
    /// worker with its stale <c>LastActivityAt</c>, and the following inactivity scan falsely
    /// evicted it. Now the whole pass is held under the activity lock, so a concurrent touch
    /// either precedes or follows it — never lands inside it — and can never be lost.
    /// </para>
    /// <para>
    /// The pool is padded with many fresh workers so each purge pass covers a wide window, and
    /// the toucher spins continuously for the pass's whole duration, so any window in which the
    /// target worker is absent is sampled many times over. Every single touch must succeed.
    /// </para>
    /// </summary>
    [Fact]
    public void ConcurrentPurgeAndTouch_ActivityIsNeverLostAndWorkerNotFalselyEvicted()
    {
        const int padding = 20_000;
        const int passes = 40;
        var pool = CreatePool();
        var timeout = TimeSpan.FromMinutes(60);

        // Padding workers keep every purge pass wide (all fresh, so none are ever evicted).
        for (var i = 0; i < padding; i++)
            pool.RegisterWorker($"pad-{i}", []);

        // The worker under test is busy with an ancient activity timestamp: it is a reclamation
        // candidate unless the touch is recorded, so a lost touch becomes directly observable.
        const string targetId = "target";
        var target = pool.RegisterWorker(targetId, []);
        pool.MarkBusy(targetId, "task-1");
        target.LastActivityAt = DateTime.UtcNow.AddMinutes(-90);

        var exceptions = new List<Exception>();
        var lostTouches = 0;
        var absentObservations = 0;
        var touchCount = 0;
        var stop = false;

        // Spins for the whole run: every touch must succeed, and the lock-free GetWorker probe
        // must never observe the fresh target missing from the pool.
        var toucher = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    if (pool.GetWorker(targetId) is null)
                        Interlocked.Increment(ref absentObservations);

                    if (!pool.TouchActivity(targetId))
                        Interlocked.Increment(ref lostTouches);

                    Interlocked.Increment(ref touchCount);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        toucher.Start();
        try
        {
            for (var i = 0; i < passes; i++)
                pool.PurgeStaleWorkers(TimeSpan.FromMinutes(5));
        }
        finally
        {
            Volatile.Write(ref stop, true);
            Assert.True(toucher.Join(TimeSpan.FromSeconds(60)), "toucher thread did not finish");
        }

        Assert.Empty(exceptions);
        // Sanity: the race really ran, so the assertions below are not vacuous.
        Assert.True(touchCount > 0, "expected the toucher to run during the purge passes");

        Assert.Equal(0, absentObservations);
        Assert.Equal(0, lostTouches);

        // The touched worker survived and its activity was recorded, so the inactivity scan that
        // runs right after the purge must not select it.
        Assert.NotNull(pool.GetWorker(targetId));
        Assert.DoesNotContain(pool.GetWorkersWithTimedOutTasks(timeout), w => w.Id == targetId);
    }

    /// <summary>
    /// <c>UpdateHeartbeat</c> must be synchronized with <see cref="WorkerPool.PurgeStaleWorkers"/>:
    /// a heartbeat that arrives while a purge pass is running is never lost, so the worker it
    /// refreshes is never evicted by that pass nor reported stale immediately afterwards.
    /// </summary>
    [Fact]
    public void ConcurrentPurgeAndHeartbeat_HeartbeatIsNeverLost()
    {
        const int padding = 20_000;
        const int passes = 40;
        var pool = CreatePool();
        var staleTimeout = TimeSpan.FromMinutes(5);

        for (var i = 0; i < padding; i++)
            pool.RegisterWorker($"pad-{i}", []);

        // The target starts stale: only a heartbeat that is actually applied keeps it alive.
        const string targetId = "target";
        var target = pool.RegisterWorker(targetId, []);
        target.LastHeartbeat = DateTime.UtcNow.AddMinutes(-90);

        var exceptions = new List<Exception>();
        var absentObservations = 0;
        var beatCount = 0;
        var stop = false;

        var beater = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    pool.UpdateHeartbeat(targetId, contextUsagePercent: 42);

                    // Once the first heartbeat landed the worker is fresh, so a later purge pass
                    // must never evict it.
                    if (Volatile.Read(ref beatCount) > 0 && pool.GetWorker(targetId) is null)
                        Interlocked.Increment(ref absentObservations);

                    Interlocked.Increment(ref beatCount);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        // Let the first heartbeat land so the target is unambiguously fresh before purging.
        beater.Start();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref beatCount) == 0 && DateTime.UtcNow < deadline)
            Thread.Yield();
        Assert.True(Volatile.Read(ref beatCount) > 0, "beater thread did not start heartbeating");

        try
        {
            for (var i = 0; i < passes; i++)
                pool.PurgeStaleWorkers(staleTimeout);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            Assert.True(beater.Join(TimeSpan.FromSeconds(60)), "beater thread did not finish");
        }

        Assert.Empty(exceptions);
        Assert.Equal(0, absentObservations);

        // The heartbeat was applied and survived every purge pass.
        var survivor = pool.GetWorker(targetId);
        Assert.NotNull(survivor);
        Assert.Equal(42, survivor.ContextUsagePercent);
        Assert.DoesNotContain(pool.GetStaleWorkers(staleTimeout), w => w.Id == targetId);
    }

    /// <summary>
    /// The invariant <see cref="WorkerPool.PurgeStaleWorkers"/> depends on: once
    /// <c>UpdateHeartbeat</c> has returned, the worker is immediately non-stale and its context
    /// usage is visible — both fields are published together under the activity lock, so a purge
    /// pass can never act on a half-applied heartbeat.
    /// </summary>
    [Fact]
    public void UpdateHeartbeat_PublishesBothFields_AndImmediatelyClearsStaleness()
    {
        var pool = CreatePool();
        var staleTimeout = TimeSpan.FromMinutes(5);
        var worker = pool.RegisterWorker("w1", []);
        worker.LastHeartbeat = DateTime.UtcNow.AddMinutes(-90);

        // Precondition: the worker is stale and would be purged.
        Assert.Contains(pool.GetStaleWorkers(staleTimeout), w => w.Id == "w1");

        pool.UpdateHeartbeat("w1", contextUsagePercent: 42);

        // Both writes are visible, and the staleness verdict flipped in the same step.
        Assert.Equal(42, worker.ContextUsagePercent);
        Assert.True(DateTime.UtcNow - worker.LastHeartbeat < TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(pool.GetStaleWorkers(staleTimeout), w => w.Id == "w1");
        Assert.Empty(pool.PurgeStaleWorkers(staleTimeout));
        Assert.Same(worker, pool.GetWorker("w1"));
    }

    [Fact]
    public void UpdateHeartbeat_UnknownWorker_IsNoOp()
    {
        var pool = CreatePool();

        pool.UpdateHeartbeat("ghost", contextUsagePercent: 42);

        Assert.Null(pool.GetWorker("ghost"));
        Assert.Equal(0, pool.ConnectedWorkerCount);
    }

    /// <summary>
    /// <see cref="WorkerPool.PurgeStaleWorkers"/> must never remove a fresh worker even
    /// momentarily. Lock-free readers — <see cref="WorkerPool.GetWorker"/>,
    /// <see cref="WorkerPool.ConnectedWorkerCount"/>, <see cref="WorkerPool.GetAllWorkers"/> and
    /// the stats endpoints — do not take the activity lock, so a remove-then-reinsert pass is
    /// directly observable to them as a worker blinking out of existence (and, for
    /// <c>TouchActivity</c> callers, as a silently dropped activity update).
    /// <para>
    /// A dedicated thread probes membership in a tight loop while purge passes run over a large
    /// pool of fresh workers, so any transient-absence window is sampled many times.
    /// </para>
    /// </summary>
    [Fact]
    public void PurgeStaleWorkers_FreshWorkersAreNeverTransientlyAbsent()
    {
        const int padding = 20_000;
        const int passes = 40;
        var pool = CreatePool();

        // Every worker is fresh, so a correct purge is a pure no-op on membership.
        for (var i = 0; i < padding; i++)
            pool.RegisterWorker($"pad-{i}", []);

        const string targetId = "target";
        pool.RegisterWorker(targetId, []);
        var expectedCount = pool.ConnectedWorkerCount;

        var exceptions = new List<Exception>();
        var absentObservations = 0;
        var shrunkCountObservations = 0;
        var probes = 0;
        var stop = false;

        var prober = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    if (pool.GetWorker(targetId) is null)
                        Interlocked.Increment(ref absentObservations);

                    if (pool.ConnectedWorkerCount < expectedCount)
                        Interlocked.Increment(ref shrunkCountObservations);

                    Interlocked.Increment(ref probes);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        })
        { IsBackground = true };

        prober.Start();
        try
        {
            for (var i = 0; i < passes; i++)
                pool.PurgeStaleWorkers(TimeSpan.FromMinutes(5));
        }
        finally
        {
            Volatile.Write(ref stop, true);
            Assert.True(prober.Join(TimeSpan.FromSeconds(60)), "prober thread did not finish");
        }

        Assert.Empty(exceptions);
        Assert.True(probes > 0, "expected the prober to run during the purge passes");

        Assert.Equal(0, absentObservations);
        Assert.Equal(0, shrunkCountObservations);
        Assert.Equal(expectedCount, pool.ConnectedWorkerCount);
    }

    #endregion

    // ── ConnectedWorkerCount ──────────────────────────────────────────────────

    #region ConnectedWorkerCount — empty pool

    [Fact]
    public void ConnectedWorkerCount_EmptyPool_ReturnsZero()
    {
        var pool = CreatePool();

        Assert.Equal(0, pool.ConnectedWorkerCount);
    }

    #endregion

    #region ConnectedWorkerCount — increments as workers are registered

    [Fact]
    public void ConnectedWorkerCount_AfterRegisteringWorkers_ReturnsCorrectCount()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", []);
        pool.RegisterWorker("w2", []);

        Assert.Equal(2, pool.ConnectedWorkerCount);
    }

    #endregion

    // ── RemoveWorker(ConnectedWorker) — instance-aware removal ────────────────

    #region RemoveWorker(ConnectedWorker) — exact instance

    [Fact]
    public void RemoveWorkerInstance_ExactInstance_RemovesAndCompletesChannel()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w1", []);

        var removed = pool.RemoveWorker(worker);

        Assert.True(removed);
        Assert.Null(pool.GetWorker("w1"));
        Assert.Equal(0, pool.ConnectedWorkerCount);
        // The message channel is completed so the worker's stream loop terminates.
        Assert.False(worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    #endregion

    #region RemoveWorker(ConnectedWorker) — different instance, same ID

    [Fact]
    public void RemoveWorkerInstance_DifferentInstanceSameId_ReturnsFalseAndKeepsRegistered()
    {
        var pool = CreatePool();
        var registered = pool.RegisterWorker("w1", []);
        // A stale reference to an old instance that is no longer registered.
        var stale = new ConnectedWorker
        {
            Id = "w1",
            Role = CopilotHive.Workers.WorkerRole.Unspecified,
            Capabilities = [],
        };

        var removed = pool.RemoveWorker(stale);

        Assert.False(removed);
        Assert.Same(registered, pool.GetWorker("w1"));
        // The registered worker's channel must NOT be completed.
        Assert.True(registered.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    #endregion

    #region RemoveWorker(ConnectedWorker) — ABA: old instance must not evict replacement

    /// <summary>
    /// The ABA regression: register A, remove A, register B under the same ID, then attempt to
    /// remove the old instance A. The removal must fail and B must remain registered with its
    /// channel intact.
    /// </summary>
    [Fact]
    public void RemoveWorkerInstance_AbaReplacement_OldInstanceRemovalDoesNotEvictNew()
    {
        var pool = CreatePool();

        // Phase 1: register A and remove it.
        var a = pool.RegisterWorker("ws-aba", []);
        Assert.True(pool.RemoveWorker(a));

        // Phase 2: register B under the same ID.
        var b = pool.RegisterWorker("ws-aba", []);
        Assert.Same(b, pool.GetWorker("ws-aba"));

        // Phase 3: the stale stream's finally tries to remove A.
        var removed = pool.RemoveWorker(a);

        Assert.False(removed);
        // B is still registered — the old instance's removal must not evict the replacement.
        Assert.Same(b, pool.GetWorker("ws-aba"));
        Assert.Equal(1, pool.ConnectedWorkerCount);
        // B's channel must NOT be completed.
        Assert.True(b.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    #endregion

    #region RemoveWorker(ConnectedWorker) — unknown instance

    [Fact]
    public void RemoveWorkerInstance_UnknownInstance_ReturnsFalse()
    {
        var pool = CreatePool();
        var ghost = new ConnectedWorker
        {
            Id = "ghost",
            Role = CopilotHive.Workers.WorkerRole.Unspecified,
            Capabilities = [],
        };

        Assert.False(pool.RemoveWorker(ghost));
    }

    #endregion

    #region RemoveWorker(string) — backward compatibility

    [Fact]
    public void RemoveWorkerById_RegisteredWorker_RemovesAndCompletesChannel()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w1", []);

        var removed = pool.RemoveWorker("w1");

        Assert.True(removed);
        Assert.Null(pool.GetWorker("w1"));
        Assert.False(worker.MessageChannel.Writer.TryWrite(new OrchestratorMessage()));
    }

    [Fact]
    public void RemoveWorkerById_UnknownId_ReturnsFalse()
    {
        var pool = CreatePool();

        Assert.False(pool.RemoveWorker("ghost"));
    }

    #endregion

    // ── Checked snapshot / idle / release primitives ──────────────────────────

    #region TryGetWorkerSnapshot

    /// <summary>
    /// The snapshot reports the CURRENT instance together with its busy flag and current task,
    /// all read at one instant under the pool's activity lock.
    /// </summary>
    [Fact]
    public void TryGetWorkerSnapshot_BusyWorker_ReportsInstanceBusyAndTask()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-snap", []);
        pool.MarkBusy("w-snap", "task-snap");

        Assert.True(pool.TryGetWorkerSnapshot("w-snap", out var snapshot));

        Assert.Same(worker, snapshot.Worker);
        Assert.True(snapshot.IsBusy);
        Assert.Equal("task-snap", snapshot.CurrentTaskId);
    }

    /// <summary>An idle worker's snapshot reports no task and no busy state.</summary>
    [Fact]
    public void TryGetWorkerSnapshot_IdleWorker_ReportsNoTask()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-snap-idle", []);

        Assert.True(pool.TryGetWorkerSnapshot("w-snap-idle", out var snapshot));

        Assert.Same(worker, snapshot.Worker);
        Assert.False(snapshot.IsBusy);
        Assert.Null(snapshot.CurrentTaskId);
    }

    /// <summary>The snapshot reports the CURRENT instance, never a removed predecessor.</summary>
    [Fact]
    public void TryGetWorkerSnapshot_AfterReplacement_ReportsTheCurrentInstance()
    {
        var pool = CreatePool();
        var stale = pool.RegisterWorker("w-snap-aba", []);
        Assert.True(pool.RemoveWorker(stale));
        var replacement = pool.RegisterWorker("w-snap-aba", []);

        Assert.True(pool.TryGetWorkerSnapshot("w-snap-aba", out var snapshot));

        Assert.Same(replacement, snapshot.Worker);
        Assert.NotSame(stale, snapshot.Worker);
    }

    [Fact]
    public void TryGetWorkerSnapshot_UnknownWorker_ReturnsFalse()
    {
        var pool = CreatePool();

        Assert.False(pool.TryGetWorkerSnapshot("ghost", out _));
    }

    #endregion

    #region TryReleaseCompletedTask

    /// <summary>
    /// The checked completion release applies the SAME idle-reset field set as
    /// <see cref="WorkerPool.MarkIdle"/> and clears <c>CurrentModel</c> INSIDE the release.
    /// </summary>
    [Fact]
    public void TryReleaseCompletedTask_MatchingOwnership_ResetsIdleFieldsAndClearsModel()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-rel", []);
        pool.MarkBusy("w-rel", "task-rel");
        worker.Role = CopilotHive.Workers.WorkerRole.Coder;
        worker.CurrentModel = "some-model";

        Assert.True(pool.TryReleaseCompletedTask(worker, "task-rel"));

        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.Null(worker.CurrentTaskStartedAt);
        Assert.Equal(CopilotHive.Workers.WorkerRole.Unspecified, worker.Role);
        Assert.Null(worker.CurrentModel);
    }

    /// <summary>
    /// A completion for a DIFFERENT task id is refused and mutates nothing — the successor's
    /// assignment fields survive intact.
    /// </summary>
    [Fact]
    public void TryReleaseCompletedTask_DifferentTaskId_IsRefusedAndMutatesNothing()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-rel-other", []);
        pool.MarkBusy("w-rel-other", "task-successor");
        worker.Role = CopilotHive.Workers.WorkerRole.Tester;
        worker.CurrentModel = "successor-model";

        Assert.False(pool.TryReleaseCompletedTask(worker, "task-predecessor"));

        Assert.True(worker.IsBusy);
        Assert.Equal("task-successor", worker.CurrentTaskId);
        Assert.Equal(CopilotHive.Workers.WorkerRole.Tester, worker.Role);
        Assert.Equal("successor-model", worker.CurrentModel);
    }

    /// <summary>
    /// TASK-ID COMPARISON IS ORDINAL: a case-differing id is a DIFFERENT task and is refused.
    /// </summary>
    [Fact]
    public void TryReleaseCompletedTask_CaseDifferingTaskId_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-rel-case", []);
        pool.MarkBusy("w-rel-case", "Task-Case");
        worker.CurrentModel = "m";

        Assert.False(pool.TryReleaseCompletedTask(worker, "task-case"));

        Assert.True(worker.IsBusy);
        Assert.Equal("Task-Case", worker.CurrentTaskId);
        Assert.Equal("m", worker.CurrentModel);
    }

    /// <summary>
    /// A worker that is no longer busy has nothing to release: the completion release is refused
    /// even when the task id still matches.
    /// </summary>
    [Fact]
    public void TryReleaseCompletedTask_NotBusy_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-rel-idle", []);
        pool.MarkBusy("w-rel-idle", "task-rel-idle");
        worker.CurrentModel = "m";
        // The inconsistent shape: a task id with IsBusy == false.
        worker.IsBusy = false;

        Assert.False(pool.TryReleaseCompletedTask(worker, "task-rel-idle"));

        // NOT released and NOT dequeued of its assignment fields.
        Assert.Equal("task-rel-idle", worker.CurrentTaskId);
        Assert.Equal("m", worker.CurrentModel);
    }

    /// <summary>
    /// An ALREADY-REPLACED instance (ABA) can never release the replacement registered under the
    /// same ID, even when both carry the same task id.
    /// </summary>
    [Fact]
    public void TryReleaseCompletedTask_ReplacedInstance_IsRefusedAndReplacementSurvives()
    {
        var pool = CreatePool();
        var stale = pool.RegisterWorker("w-rel-aba", []);
        pool.MarkBusy("w-rel-aba", "task-aba");
        Assert.True(pool.RemoveWorker(stale));

        var replacement = pool.RegisterWorker("w-rel-aba", []);
        pool.MarkBusy("w-rel-aba", "task-aba");
        replacement.CurrentModel = "replacement-model";

        Assert.False(pool.TryReleaseCompletedTask(stale, "task-aba"));

        Assert.True(replacement.IsBusy);
        Assert.Equal("task-aba", replacement.CurrentTaskId);
        Assert.Equal("replacement-model", replacement.CurrentModel);
    }

    /// <summary>
    /// A REMOVED worker's obsolete fields are never rewritten by the checked release: membership
    /// is checked, so a worker no longer in the pool is refused.
    /// </summary>
    [Fact]
    public void TryReleaseCompletedTask_RemovedWorker_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-rel-gone", []);
        pool.MarkBusy("w-rel-gone", "task-gone");
        worker.CurrentModel = "m";
        Assert.True(pool.RemoveWorker(worker));

        Assert.False(pool.TryReleaseCompletedTask(worker, "task-gone"));

        Assert.True(worker.IsBusy);
        Assert.Equal("task-gone", worker.CurrentTaskId);
        Assert.Equal("m", worker.CurrentModel);
    }

    #endregion

    #region TryMarkIdleForReady

    /// <summary>
    /// THE INITIAL/IDLE READY: a worker with no task and no busy state is accepted, and the idle
    /// reset is applied without touching <c>CurrentModel</c>.
    /// </summary>
    [Fact]
    public void TryMarkIdleForReady_IdleWorkerWithNullTask_IsAppliedAndPreservesModel()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-idle", []);
        worker.Role = CopilotHive.Workers.WorkerRole.Reviewer;
        worker.CurrentModel = "carried-model";
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-idle", out var snapshot));

        Assert.True(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.Equal(CopilotHive.Workers.WorkerRole.Unspecified, worker.Role);
        // THE READY PATH PRESERVES EXISTING MODEL BEHAVIOUR: the model is not cleared here.
        Assert.Equal("carried-model", worker.CurrentModel);
    }

    /// <summary>
    /// A null-task observation of a BUSY worker is the inconsistent shape and is refused: a busy
    /// worker's assignment may never be dropped by a null-task Ready.
    /// </summary>
    /// <remarks>
    /// THE ISOLATED SIDE: the MUTATION-TIME half of the null-task predicate. The observation saw
    /// <c>IsBusy=false</c>, so only the current-value check can refuse this call — dropping it
    /// makes this vector go green while the observation-time half stays satisfied. The
    /// observation-time half is isolated by
    /// <see cref="TryMarkIdleForReady_NullTask_BusyAtObservationIdleAtMutation_IsRefused"/>.
    /// </remarks>
    [Fact]
    public void TryMarkIdleForReady_NullTaskButBusy_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-busynull", []);
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-busynull", out var snapshot));

        // THE OBSERVATION SAW AN IDLE WORKER — so the mutation-time check is the only one that
        // can refuse what follows.
        Assert.False(snapshot.IsBusy);
        Assert.Null(snapshot.CurrentTaskId);

        // The worker becomes busy (with no task id) AFTER the observation.
        worker.IsBusy = true;

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));
        Assert.True(worker.IsBusy);
    }

    /// <summary>
    /// THE NULL-TASK OBSERVATION-TIME HALF, ISOLATED. The observation saw a BUSY worker (with no
    /// task id — the inconsistent shape), and by the time the checked idle runs the worker is idle
    /// again. The CURRENT value would therefore satisfy the mutation-time half on its own, so only
    /// the OBSERVATION-time half can refuse this call: dropping <c>!observed.IsBusy</c> turns this
    /// vector green.
    /// </summary>
    /// <remarks>
    /// A stale observation must never authorize a release. Every assignment field is asserted at
    /// its EXACT pre-call value, so an applied idle reset (which would clear the role and the task
    /// start timestamp) is detected rather than merely "not obviously changed".
    /// </remarks>
    [Fact]
    public void TryMarkIdleForReady_NullTask_BusyAtObservationIdleAtMutation_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-null-stale-busy", []);

        // THE OBSERVED STATE: busy, but carrying NO task id.
        worker.IsBusy = true;
        worker.Role = CopilotHive.Workers.WorkerRole.Reviewer;
        worker.CurrentModel = "carried-model";
        worker.CurrentTaskStartedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Assert.True(pool.TryGetWorkerSnapshot("w-ready-null-stale-busy", out var snapshot));
        Assert.True(snapshot.IsBusy, "the observation must see a BUSY worker for this vector to isolate its side");
        Assert.Null(snapshot.CurrentTaskId);

        // THE MUTATION-TIME STATE: idle again — so the current-value half alone would accept.
        worker.IsBusy = false;

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        // EVERY assignment field is EXACTLY as it was before the refused call.
        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.Equal(CopilotHive.Workers.WorkerRole.Reviewer, worker.Role);
        Assert.Equal("carried-model", worker.CurrentModel);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), worker.CurrentTaskStartedAt);
    }

    /// <summary>
    /// THE RELEASING READY: a busy worker whose task has no active queue entry is released.
    /// </summary>
    [Fact]
    public void TryMarkIdleForReady_BusyWorkerWithAbsentQueueEntry_IsApplied()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-release", []);
        pool.MarkBusy("w-ready-release", "task-ready");
        worker.CurrentModel = "carried-model";
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-release", out var snapshot));

        Assert.True(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        Assert.False(worker.IsBusy);
        Assert.Null(worker.CurrentTaskId);
        Assert.Null(worker.CurrentTaskStartedAt);
        Assert.Equal("carried-model", worker.CurrentModel);
    }

    /// <summary>
    /// A STILL-PRESENT queue entry refuses the releasing Ready — nothing is released.
    /// </summary>
    [Fact]
    public void TryMarkIdleForReady_BusyWorkerWithPresentQueueEntry_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-held", []);
        pool.MarkBusy("w-ready-held", "task-held");
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-held", out var snapshot));

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: false));

        Assert.True(worker.IsBusy);
        Assert.Equal("task-held", worker.CurrentTaskId);
    }

    /// <summary>
    /// A NON-NULL observed task with <c>IsBusy == false</c> is the inconsistent shape: refused
    /// WITHOUT releasing and without clearing any assignment field.
    /// </summary>
    /// <remarks>
    /// THIS VECTOR PINS THE COMBINED SHAPE (not busy at EITHER point). The two temporal halves are
    /// isolated separately by
    /// <see cref="TryMarkIdleForReady_NonNullTask_BusyAtObservationIdleAtMutation_IsRefused"/> and
    /// <see cref="TryMarkIdleForReady_NonNullTask_IdleAtObservationBusyAtMutation_IsRefused"/>.
    /// </remarks>
    [Fact]
    public void TryMarkIdleForReady_NonNullTaskButNotBusy_IsRefusedAndMutatesNothing()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-inconsistent", []);
        pool.MarkBusy("w-ready-inconsistent", "task-inconsistent");
        worker.Role = CopilotHive.Workers.WorkerRole.Coder;
        worker.IsBusy = false;
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-inconsistent", out var snapshot));
        Assert.False(snapshot.IsBusy);
        Assert.Equal("task-inconsistent", snapshot.CurrentTaskId);

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        Assert.Equal("task-inconsistent", worker.CurrentTaskId);
        Assert.NotNull(worker.CurrentTaskStartedAt);
        Assert.Equal(CopilotHive.Workers.WorkerRole.Coder, worker.Role);
    }

    /// <summary>
    /// THE NON-NULL MUTATION-TIME HALF, ISOLATED. The observation saw a BUSY worker owning the
    /// task, and by the time the checked idle runs the SAME task id is still present but the
    /// worker is no longer busy. The OBSERVATION-time half is therefore satisfied, so only the
    /// MUTATION-time half (<c>!expected.IsBusy</c>) can refuse this call: dropping it turns this
    /// vector green.
    /// </summary>
    /// <remarks>
    /// The refusal contract is pinned by EXACT pre-call values for every assignment field, so an
    /// applied idle reset — which would null the task id and the start timestamp and reset the
    /// role — is detected rather than merely "not obviously changed".
    /// </remarks>
    [Fact]
    public void TryMarkIdleForReady_NonNullTask_BusyAtObservationIdleAtMutation_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-nonnull-stale-busy", []);
        pool.MarkBusy("w-ready-nonnull-stale-busy", "task-stale-busy");
        worker.Role = CopilotHive.Workers.WorkerRole.Tester;
        worker.CurrentModel = "carried-model";
        var startedAt = worker.CurrentTaskStartedAt;
        Assert.NotNull(startedAt);

        // THE OBSERVATION: busy, owning the task.
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-nonnull-stale-busy", out var snapshot));
        Assert.True(snapshot.IsBusy, "the observation must see a BUSY worker for this vector to isolate its side");
        Assert.Equal("task-stale-busy", snapshot.CurrentTaskId);

        // THE MUTATION-TIME STATE: the SAME task id, but no longer busy.
        worker.IsBusy = false;

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        // EVERY assignment field is EXACTLY as it was before the refused call.
        Assert.False(worker.IsBusy);
        Assert.Equal("task-stale-busy", worker.CurrentTaskId);
        Assert.Equal(CopilotHive.Workers.WorkerRole.Tester, worker.Role);
        Assert.Equal("carried-model", worker.CurrentModel);
        Assert.Equal(startedAt, worker.CurrentTaskStartedAt);
    }

    /// <summary>
    /// THE NON-NULL OBSERVATION-TIME HALF, ISOLATED. The observation saw the task id with
    /// <c>IsBusy=false</c> (the inconsistent shape), and by the time the checked idle runs the
    /// worker is busy with that SAME task. The MUTATION-time half is therefore satisfied, so only
    /// the OBSERVATION-time half (<c>!observed.IsBusy</c>) can refuse this call: dropping it turns
    /// this vector green.
    /// </summary>
    /// <remarks>
    /// A stale, internally inconsistent observation must never authorize a release of ownership
    /// that is live right now. Every assignment field is asserted at its EXACT pre-call value.
    /// </remarks>
    [Fact]
    public void TryMarkIdleForReady_NonNullTask_IdleAtObservationBusyAtMutation_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-nonnull-stale-idle", []);
        pool.MarkBusy("w-ready-nonnull-stale-idle", "task-stale-idle");
        worker.Role = CopilotHive.Workers.WorkerRole.DocWriter;
        worker.CurrentModel = "carried-model";
        var startedAt = worker.CurrentTaskStartedAt;
        Assert.NotNull(startedAt);

        // THE OBSERVATION: the task id is present but the worker reads as NOT busy.
        worker.IsBusy = false;
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-nonnull-stale-idle", out var snapshot));
        Assert.False(snapshot.IsBusy, "the observation must see a NON-BUSY worker for this vector to isolate its side");
        Assert.Equal("task-stale-idle", snapshot.CurrentTaskId);

        // THE MUTATION-TIME STATE: busy with the SAME task — the current-value half alone accepts.
        worker.IsBusy = true;

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        // EVERY assignment field is EXACTLY as it was before the refused call.
        Assert.True(worker.IsBusy);
        Assert.Equal("task-stale-idle", worker.CurrentTaskId);
        Assert.Equal(CopilotHive.Workers.WorkerRole.DocWriter, worker.Role);
        Assert.Equal("carried-model", worker.CurrentModel);
        Assert.Equal(startedAt, worker.CurrentTaskStartedAt);
    }

    /// <summary>
    /// A task id that CHANGED between the observation and the mutation point is refused — the
    /// successor's assignment survives.
    /// </summary>
    [Fact]
    public void TryMarkIdleForReady_TaskChangedAfterObservation_IsRefused()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-ready-moved", []);
        pool.MarkBusy("w-ready-moved", "task-first");
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-moved", out var snapshot));

        // The worker moved on to another task after the observation.
        pool.MarkBusy("w-ready-moved", "task-second");

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        Assert.True(worker.IsBusy);
        Assert.Equal("task-second", worker.CurrentTaskId);
    }

    /// <summary>
    /// A REPLACED instance (ABA) can never idle the replacement registered under its ID.
    /// </summary>
    [Fact]
    public void TryMarkIdleForReady_ReplacedInstance_IsRefusedAndReplacementSurvives()
    {
        var pool = CreatePool();
        var stale = pool.RegisterWorker("w-ready-aba", []);
        pool.MarkBusy("w-ready-aba", "task-aba");
        Assert.True(pool.TryGetWorkerSnapshot("w-ready-aba", out var snapshot));

        Assert.True(pool.RemoveWorker(stale));
        var replacement = pool.RegisterWorker("w-ready-aba", []);
        pool.MarkBusy("w-ready-aba", "task-aba");

        Assert.False(pool.TryMarkIdleForReady(snapshot, queueEntryAbsent: true));

        Assert.True(replacement.IsBusy);
        Assert.Equal("task-aba", replacement.CurrentTaskId);
    }

    #endregion

    // ── Completion-receipt negotiation fact ──────────────────────────────────

    #region RegisterWorker — requested completion-receipt ACK

    /// <summary>
    /// THE EXISTING TWO-ARGUMENT CALLERS DEFAULT TO NO REQUEST. Every caller that cannot express a
    /// negotiation request registers a worker that asked for nothing.
    /// </summary>
    [Fact]
    public void RegisterWorker_TwoArgumentCaller_DefaultsRequestedAckToFalse()
    {
        var pool = CreatePool();

        var worker = pool.RegisterWorker("w-no-request", []);

        Assert.False(worker.RequestCompletionReceiptAck);
        Assert.False(pool.GetWorker("w-no-request")!.RequestCompletionReceiptAck);
    }

    /// <summary>
    /// THE REQUESTED FACT IS RETAINED PER REGISTRATION, and it is decided BEFORE the instance is
    /// published: the returned worker — the very instance the pool holds — already carries the
    /// correct value, so no observer can ever see an undecided request.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegisterWorker_ExplicitRequest_IsRetainedOnThePublishedInstance(bool requested)
    {
        var pool = CreatePool();

        var worker = pool.RegisterWorker("w-explicit", [], requested);

        Assert.Equal(requested, worker.RequestCompletionReceiptAck);
        Assert.Same(worker, pool.GetWorker("w-explicit"));
        Assert.Equal(requested, pool.GetWorker("w-explicit")!.RequestCompletionReceiptAck);
    }

    /// <summary>
    /// A DUPLICATE registration is still rejected AND leaves the original instance — including the
    /// fact it registered with — completely untouched. The second, conflicting request changes
    /// nothing.
    /// </summary>
    [Fact]
    public void RegisterWorker_Duplicate_IsRejectedAndDoesNotRebindTheRequestedFact()
    {
        var pool = CreatePool();
        var original = pool.RegisterWorker("w-dup-request", [], requestCompletionReceiptAck: true);

        Assert.Throws<InvalidOperationException>(
            () => pool.RegisterWorker("w-dup-request", [], requestCompletionReceiptAck: false));

        Assert.Same(original, pool.GetWorker("w-dup-request"));
        Assert.True(original.RequestCompletionReceiptAck);
        Assert.Equal(1, pool.ConnectedWorkerCount);
    }

    #endregion

    // ── The exclusive per-instance WorkStream attachment claim ───────────────

    #region RegisterWorker — exclusive WorkStream attachment claim

    /// <summary>
    /// EXACTLY ONE CALLER CAN EVER CLAIM AN INSTANCE, and the claim is visible afterwards.
    /// </summary>
    [Fact]
    public void TryAttachWorkStream_FirstCallWinsAndEveryLaterCallLoses()
    {
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-claim", []);

        Assert.False(worker.IsWorkStreamAttached);
        Assert.True(worker.TryAttachWorkStream());
        Assert.True(worker.IsWorkStreamAttached);

        // EVERY later attempt loses — including one from the same thread.
        Assert.False(worker.TryAttachWorkStream());
        Assert.False(worker.TryAttachWorkStream());
    }

    /// <summary>
    /// THE CLAIM IS ONE-WAY: removing the instance from the pool does not reset it, so the very
    /// same object can never be re-attached. A re-registration under the same ID is a NEW instance
    /// with its OWN fresh claim.
    /// </summary>
    [Fact]
    public void TryAttachWorkStream_IsOneWayAndPerInstance()
    {
        var pool = CreatePool();
        var first = pool.RegisterWorker("w-claim-oneway", []);
        Assert.True(first.TryAttachWorkStream());

        Assert.True(pool.RemoveWorker(first));
        Assert.False(first.TryAttachWorkStream());
        Assert.True(first.IsWorkStreamAttached);

        var replacement = pool.RegisterWorker("w-claim-oneway", []);
        Assert.NotSame(first, replacement);
        Assert.False(replacement.IsWorkStreamAttached);
        Assert.True(replacement.TryAttachWorkStream());
    }

    /// <summary>
    /// THE CLAIM IS ATOMIC: under CONCURRENT attempts on the same instance exactly ONE wins. The
    /// losing threads all observe <c>false</c>, and the winner count is exactly one.
    /// </summary>
    /// <remarks>
    /// The contenders are released together by a barrier so the attempts genuinely overlap; the
    /// assertion is on the OBSERVED winners, not on timing.
    /// </remarks>
    [Fact]
    public async Task TryAttachWorkStream_ConcurrentAttempts_ExactlyOneWins()
    {
        const int contenders = 16;
        var pool = CreatePool();
        var worker = pool.RegisterWorker("w-claim-race", []);

        using var gate = new Barrier(contenders);
        var winners = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, contenders).Select(_ => Task.Run(() =>
        {
            start.Task.GetAwaiter().GetResult();
            gate.SignalAndWait();
            if (worker.TryAttachWorkStream())
                Interlocked.Increment(ref winners);
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(attempts);

        Assert.Equal(1, winners);
        Assert.True(worker.IsWorkStreamAttached);
    }

    /// <summary>
    /// DISTINCT INSTANCES CLAIM INDEPENDENTLY: each worker owns its own claim, so N distinct
    /// registrations all attach.
    /// </summary>
    [Fact]
    public void TryAttachWorkStream_DistinctInstances_AllAttach()
    {
        var pool = CreatePool();
        var workers = Enumerable.Range(0, 4)
            .Select(i => pool.RegisterWorker($"w-claim-{i}", []))
            .ToList();

        foreach (var worker in workers)
            Assert.True(worker.TryAttachWorkStream());

        Assert.All(workers, w => Assert.True(w.IsWorkStreamAttached));
    }

    #endregion
}
