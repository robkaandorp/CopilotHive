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
        WorkerPool pool, string id, WorkerRole role,
        DateTime lastHeartbeat, bool isBusy = false)
    {
        var worker = pool.RegisterWorker(id, role, []);
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
        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder, now.AddSeconds(-10));
        RegisterWithHeartbeat(pool, "w2", WorkerRole.Reviewer, now.AddSeconds(-5));

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
        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder, withinBoundary);

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
        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder, justPast);

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

        RegisterWithHeartbeat(pool, "fresh", WorkerRole.Coder, now.AddSeconds(-10));
        RegisterWithHeartbeat(pool, "stale1", WorkerRole.Reviewer, now.AddMinutes(-10));
        RegisterWithHeartbeat(pool, "stale2", WorkerRole.Tester, now.AddMinutes(-6));

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

        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder, now.AddMinutes(-2));
        RegisterWithHeartbeat(pool, "w2", WorkerRole.Reviewer, now.AddMinutes(-3));

        var result = pool.GetStaleWorkers(timeout);

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region GetStaleWorkers — does not mutate pool

    [Fact]
    public void GetStaleWorkers_DoesNotRemoveWorkers()
    {
        var pool = CreatePool();
        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder,
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
        pool.RegisterWorker("w1", WorkerRole.Coder, []);

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
        pool.RegisterWorker("w1", WorkerRole.Coder, []);
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
        pool.RegisterWorker("w1", WorkerRole.Coder, []);
        pool.RegisterWorker("w2", WorkerRole.Coder, []);
        pool.RegisterWorker("w3", WorkerRole.Reviewer, []);
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
    public void GetWorkerStats_MultipleRoles_WorkersByRoleCorrect()
    {
        var pool = CreatePool();
        pool.RegisterWorker("c1", WorkerRole.Coder, []);
        pool.RegisterWorker("c2", WorkerRole.Coder, []);
        pool.RegisterWorker("r1", WorkerRole.Reviewer, []);
        pool.RegisterWorker("t1", WorkerRole.Tester, []);

        var stats = pool.GetWorkerStats();

        var coderKey = WorkerRole.Coder.ToString();
        var reviewerKey = WorkerRole.Reviewer.ToString();
        var testerKey = WorkerRole.Tester.ToString();

        Assert.Equal(2, stats.WorkersByRole[coderKey]);
        Assert.Equal(1, stats.WorkersByRole[reviewerKey]);
        Assert.Equal(1, stats.WorkersByRole[testerKey]);
    }

    [Fact]
    public void GetWorkerStats_SingleRole_WorkersByRoleHasOneEntry()
    {
        var pool = CreatePool();
        pool.RegisterWorker("w1", WorkerRole.Tester, []);
        pool.RegisterWorker("w2", WorkerRole.Tester, []);

        var stats = pool.GetWorkerStats();

        Assert.Single(stats.WorkersByRole);
        Assert.Equal(2, stats.WorkersByRole[WorkerRole.Tester.ToString()]);
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
        Assert.Equal(0, stats.GenericWorkers);
        Assert.Empty(stats.Workers);
    }

    #endregion

    #region GetDetailedStats — mixed workers

    [Fact]
    public void GetDetailedStats_MixedWorkers_CorrectCountsAndEntries()
    {
        var pool = CreatePool();
        pool.RegisterWorker("c1", WorkerRole.Coder, []);
        pool.RegisterWorker("g1", WorkerRole.Unspecified, []);
        pool.MarkBusy("c1", "task-1");

        var stats = pool.GetDetailedStats();

        Assert.Equal(2, stats.TotalWorkers);
        Assert.Equal(1, stats.BusyWorkers);
        Assert.Equal(1, stats.IdleWorkers);
        Assert.Equal(1, stats.GenericWorkers);
        Assert.Equal(2, stats.Workers.Count);

        var coder = stats.Workers.First(w => w.Id == "c1");
        Assert.Equal("Coder", coder.Role);
        Assert.True(coder.IsBusy);
        Assert.False(coder.IsGeneric);
        Assert.Equal("task-1", coder.CurrentTaskId);

        var generic = stats.Workers.First(w => w.Id == "g1");
        Assert.Null(generic.Role);
        Assert.False(generic.IsBusy);
        Assert.True(generic.IsGeneric);
        Assert.Null(generic.CurrentTaskId);
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
        pool.RegisterWorker("w1", WorkerRole.Coder, []);
        pool.RegisterWorker("w2", WorkerRole.Reviewer, []);

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

        RegisterWithHeartbeat(pool, "fresh", WorkerRole.Coder, now.AddSeconds(-30));
        RegisterWithHeartbeat(pool, "stale1", WorkerRole.Reviewer, now.AddMinutes(-10));
        RegisterWithHeartbeat(pool, "stale2", WorkerRole.Tester, now.AddMinutes(-7));

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

        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder, now.AddMinutes(-2));
        RegisterWithHeartbeat(pool, "w2", WorkerRole.Reviewer, now.AddMinutes(-5));

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

        RegisterWithHeartbeat(pool, "w1", WorkerRole.Coder, now.AddSeconds(-10));
        RegisterWithHeartbeat(pool, "w2", WorkerRole.Coder, now.AddMinutes(-3));
        RegisterWithHeartbeat(pool, "w3", WorkerRole.Tester, now.AddMinutes(-3));

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

        RegisterWithHeartbeat(pool, "alpha", WorkerRole.Coder, now.AddMinutes(-2));
        RegisterWithHeartbeat(pool, "beta", WorkerRole.Reviewer, now.AddMinutes(-3));

        var purged = pool.PurgeStaleWorkers(timeout);

        var ids = purged.Select(w => w.Id).OrderBy(id => id).ToList();
        Assert.Equal(["alpha", "beta"], ids);
    }

    #endregion
}
