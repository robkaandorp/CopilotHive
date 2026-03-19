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
}
