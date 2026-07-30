using System.Collections.Concurrent;
using System.Reflection;

using CopilotHive.Orchestration;

using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder.SubAgents;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Unit tests for the channel-fed <see cref="SubAgentStateTracker"/> consumer. The tracker is
/// <c>internal</c>; the test project sees it via <c>InternalsVisibleTo</c>. These tests exercise
/// the single-reader drain loop, the total-order display/cap rules and the defensive-copy contract
/// directly — no <see cref="Composer"/> or <see cref="ComposerAgentService"/> is involved.
/// </summary>
public sealed class SubAgentStateTrackerTests
{
    // ── Helpers ──

    /// <summary>
    /// Sentinel Id used by <see cref="DrainAsync"/>. The channel is single-reader and applies
    /// messages sequentially, so when the sentinel's <c>OnSubAgentChanged</c> event fires, every
    /// message posted before it has already been applied. Tests filter the sentinel out of
    /// assertions via <see cref="Snapshot"/>.
    /// </summary>
    private const string DrainSentinelId = "__drain-sentinel__";

    /// <summary>
    /// Creates a tracker and starts its reader loop.
    /// </summary>
    private static async Task<SubAgentStateTracker> CreateStartedTracker()
    {
        var tracker = new SubAgentStateTracker(NullLogger.Instance);
        await tracker.StartAsync();
        return tracker;
    }

    /// <summary>
    /// Flushes the channel by posting a sentinel and waiting for its
    /// <c>OnSubAgentChanged</c> event. Because the channel is single-reader and sequential, when
    /// the sentinel event fires every earlier posted message is already applied. This does NOT
    /// stop the tracker (unlike <c>StopAsync</c>, which publishes an empty snapshot).
    /// </summary>
    private static async Task DrainAsync(SubAgentStateTracker tracker)
    {
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<SubAgentInfo> handler = info =>
        {
            if (info.Id == DrainSentinelId)
                fired.TrySetResult(true);
        };
        tracker.OnSubAgentChanged += handler;

        tracker.Post(new SubAgentInfo
        {
            Id = DrainSentinelId,
            Task = "drain",
            // Running so it never counts toward the 50-terminal cap and never displaces a
            // real terminal entry.
            Status = SubAgentStatus.Running,
            StartedAt = DateTimeOffset.MaxValue,
        });

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        tracker.OnSubAgentChanged -= handler;
    }

    /// <summary>
    /// Returns the current snapshot with the drain sentinel filtered out.
    /// </summary>
    private static IReadOnlyList<SubAgentInfo> Snapshot(SubAgentStateTracker tracker) =>
        tracker.GetSubAgents()
            .Where(e => e.Id != DrainSentinelId)
            .ToList();

    /// <summary>
    /// Posts a message, then drains so assertions see the fully-processed state.
    /// </summary>
    private static async Task PostAndDrainAsync(SubAgentStateTracker tracker, params SubAgentInfo[] infos)
    {
        foreach (var info in infos)
            tracker.Post(info);
        await DrainAsync(tracker);
    }

    private static SubAgentInfo MakeInfo(
        string id,
        SubAgentStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt = null,
        string? task = null,
        string? model = null,
        string? summary = null,
        string? error = null) => new()
    {
        Id = id,
        Task = task ?? $"task-{id}",
        Status = status,
        StartedAt = startedAt,
        CompletedAt = completedAt,
        Model = model,
        Summary = summary,
        Error = error,
    };

    // ── 1. Running then terminal for the same Id reflects the terminal update ──

    [Fact]
    public async Task Post_RunningThenTerminalForSameId_GetSubAgentsReflectsTerminalUpdate()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var t0 = DateTimeOffset.UtcNow;

            // Post Running then terminal for the same Id — the channel serializes both in order,
            // so the final stored value for Id "a" is the terminal update.
            tracker.Post(MakeInfo("a", SubAgentStatus.Running, t0));
            tracker.Post(MakeInfo("a", SubAgentStatus.Completed, t0, t0.AddSeconds(5)));

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            var entry = Assert.Single(result);
            Assert.Equal("a", entry.Id);
            Assert.Equal(SubAgentStatus.Completed, entry.Status);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 2. Display ordering — Running (StartedAt asc, Id tie-break) then terminal (StartedAt desc, Id tie-break) ──

    [Fact]
    public async Task DisplayOrder_RunningFirstAscThenTerminalDesc_IdTieBreak()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // Two Running entries with different StartedAt.
            tracker.Post(MakeInfo("r-late", SubAgentStatus.Running, baseTime.AddSeconds(10)));
            tracker.Post(MakeInfo("r-early", SubAgentStatus.Running, baseTime.AddSeconds(1)));

            // Two terminal entries with different StartedAt.
            tracker.Post(MakeInfo("t-late", SubAgentStatus.Completed, baseTime.AddSeconds(20), baseTime.AddSeconds(25)));
            tracker.Post(MakeInfo("t-early", SubAgentStatus.Completed, baseTime.AddSeconds(2), baseTime.AddSeconds(3)));

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(4, result.Count);

            // Running first, ascending StartedAt.
            Assert.Equal("r-early", result[0].Id);
            Assert.Equal("r-late", result[1].Id);
            // Then terminal, descending StartedAt.
            Assert.Equal("t-late", result[2].Id);
            Assert.Equal("t-early", result[3].Id);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisplayOrder_SameStartedAt_TieBreakByIdOrdinal()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var sameTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // Two Running entries with identical StartedAt — tie-break by Id ordinal.
            tracker.Post(MakeInfo("bbb", SubAgentStatus.Running, sameTime));
            tracker.Post(MakeInfo("aaa", SubAgentStatus.Running, sameTime));

            // Two terminal entries with identical StartedAt — tie-break by Id ordinal.
            tracker.Post(MakeInfo("zzz", SubAgentStatus.Completed, sameTime, sameTime.AddSeconds(1)));
            tracker.Post(MakeInfo("yyy", SubAgentStatus.Completed, sameTime, sameTime.AddSeconds(1)));

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(4, result.Count);

            // Running, Id ascending.
            Assert.Equal("aaa", result[0].Id);
            Assert.Equal("bbb", result[1].Id);
            // Terminal, Id ascending (within the descending-StartedAt group, ties resolve Id asc).
            Assert.Equal("yyy", result[2].Id);
            Assert.Equal("zzz", result[3].Id);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 3. Cap retains most-recent-50 terminal entries (CompletedAt desc, StartedAt desc, Id tie-break) ──

    [Fact]
    public async Task Cap_RetainsMostRecent50Terminal_ByCompletedAtDesc()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // Post 55 terminal entries with distinct, ascending CompletedAt values.
            for (var i = 0; i < 55; i++)
            {
                var started = baseTime.AddSeconds(i);
                var completed = baseTime.AddSeconds(i + 1);
                tracker.Post(MakeInfo($"agent-{i:D2}", SubAgentStatus.Completed, started, completed));
            }

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(50, result.Count);

            // The retained 50 are those with the latest CompletedAt — indices 5..54.
            var ids = result.Select(e => e.Id).ToHashSet();
            for (var i = 5; i < 55; i++)
                Assert.Contains($"agent-{i:D2}", ids);
            for (var i = 0; i < 5; i++)
                Assert.DoesNotContain($"agent-{i:D2}", ids);

            // Display order is StartedAt desc for terminal, so the latest-started is first.
            Assert.Equal("agent-54", result[0].Id);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cap_AllTerminalStatusesShareSameBudget()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // Post a mix of terminal statuses — all count toward the 50 cap.
            var statuses = new[]
            {
                SubAgentStatus.Completed,
                SubAgentStatus.Failed,
                SubAgentStatus.TimedOut,
                SubAgentStatus.Cancelled,
            };

            for (var i = 0; i < 60; i++)
            {
                var status = statuses[i % statuses.Length];
                tracker.Post(MakeInfo(
                    $"agent-{i:D2}",
                    status,
                    baseTime.AddSeconds(i),
                    baseTime.AddSeconds(i + 1)));
            }

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(50, result.Count);

            // The 10 oldest (by CompletedAt) are dropped — indices 0..9.
            var ids = result.Select(e => e.Id).ToHashSet();
            for (var i = 10; i < 60; i++)
                Assert.Contains($"agent-{i:D2}", ids);
            for (var i = 0; i < 10; i++)
                Assert.DoesNotContain($"agent-{i:D2}", ids);

            // Each retained status type should be present.
            var retainedStatuses = result.Select(e => e.Status).ToHashSet();
            Assert.Contains(SubAgentStatus.Completed, retainedStatuses);
            Assert.Contains(SubAgentStatus.Failed, retainedStatuses);
            Assert.Contains(SubAgentStatus.TimedOut, retainedStatuses);
            Assert.Contains(SubAgentStatus.Cancelled, retainedStatuses);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 4. Running entries are never dropped or counted toward the 50 cap ──

    [Fact]
    public async Task Cap_RunningEntriesNeverDroppedOrCounted()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // 55 terminal entries fill the cap.
            for (var i = 0; i < 55; i++)
            {
                tracker.Post(MakeInfo(
                    $"term-{i:D2}",
                    SubAgentStatus.Completed,
                    baseTime.AddSeconds(i),
                    baseTime.AddSeconds(i + 1)));
            }

            // 3 Running entries — must all survive alongside the 50 capped terminals.
            tracker.Post(MakeInfo("run-1", SubAgentStatus.Running, baseTime.AddSeconds(100)));
            tracker.Post(MakeInfo("run-2", SubAgentStatus.Running, baseTime.AddSeconds(101)));
            tracker.Post(MakeInfo("run-3", SubAgentStatus.Running, baseTime.AddSeconds(102)));

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(53, result.Count);

            var ids = result.Select(e => e.Id).ToHashSet();
            Assert.Contains("run-1", ids);
            Assert.Contains("run-2", ids);
            Assert.Contains("run-3", ids);

            // Running entries appear first (ascending StartedAt), then terminal (descending).
            Assert.Equal("run-1", result[0].Id);
            Assert.Equal("run-2", result[1].Id);
            Assert.Equal("run-3", result[2].Id);

            // 50 terminals remain.
            var terminalCount = result.Count(e => e.Status != SubAgentStatus.Running);
            Assert.Equal(50, terminalCount);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 5. Defensive copy — mutating the posted SubAgentInfo does NOT change the stored snapshot ──

    [Fact]
    public async Task DefensiveCopy_MutatingPostedInstance_DoesNotChangeStoredSnapshot()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var original = MakeInfo("x", SubAgentStatus.Running, DateTimeOffset.UtcNow, task: "original");
            tracker.Post(original);
            await DrainAsync(tracker);

            // Mutate the caller's instance after posting.
            original.Task = "mutated";

            var result = Snapshot(tracker);
            var entry = Assert.Single(result);
            Assert.Equal("original", entry.Task);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 6. Defensive copy — mutating a GetSubAgents() result does NOT change tracker state ──

    [Fact]
    public async Task DefensiveCopy_MutatingGetResult_DoesNotChangeTrackerState()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            tracker.Post(MakeInfo("y", SubAgentStatus.Running, DateTimeOffset.UtcNow, task: "original"));
            await DrainAsync(tracker);

            var first = Snapshot(tracker);
            var entry = Assert.Single(first);
            var originalTask = entry.Task;
            Assert.Equal("original", originalTask);

            // Mutate the returned instance.
            entry.Task = "mutated";

            // A second read must be unaffected.
            var second = Snapshot(tracker);
            var entry2 = Assert.Single(second);
            Assert.Equal(originalTask, entry2.Task);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 7. Concurrent-post determinism ──

    [Fact]
    public async Task ConcurrentPost_MultipleThreads_AllIdsPresentWithCorrectTerminalStatus()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            const int threads = 4;
            const int perThread = 5;
            var ids = new ConcurrentBag<string>();

            var tasks = Enumerable.Range(0, threads).Select(threadIndex =>
                Task.Run(() =>
                {
                    for (var j = 0; j < perThread; j++)
                    {
                        var id = $"t{threadIndex}-a{j}";
                        ids.Add(id);
                        // Running then terminal for the SAME Id from the SAME thread.
                        tracker.Post(MakeInfo(id, SubAgentStatus.Running, DateTimeOffset.UtcNow));
                        tracker.Post(MakeInfo(id, SubAgentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                    }
                })).ToArray();

            // The channel serializes all application — no Barrier/SpinWait/TCS-parking needed.
            await Task.WhenAll(tasks);

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(threads * perThread, result.Count);

            // Every Id must be present, and every entry must be terminal (Completed).
            var resultIds = result.Select(e => e.Id).ToHashSet();
            foreach (var id in ids)
                Assert.Contains(id, resultIds);

            Assert.All(result, e => Assert.Equal(SubAgentStatus.Completed, e.Status));
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 8. OnSubAgentChanged fires with a fresh clone after the snapshot is updated ──

    [Fact]
    public async Task OnSubAgentChanged_FiresWithFreshClone_AfterSnapshotUpdated()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            SubAgentInfo? received = null;
            tracker.OnSubAgentChanged += info =>
            {
                if (info.Id == "evt")
                    received = info;
            };

            var posted = MakeInfo("evt", SubAgentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, task: "hello");
            tracker.Post(posted);
            await DrainAsync(tracker);

            Assert.NotNull(received);
            Assert.Equal("evt", received!.Id);
            Assert.Equal(SubAgentStatus.Completed, received.Status);

            // The forwarded instance must be a distinct clone, not the posted instance.
            Assert.NotSame(posted, received);

            // Mutating the received clone must not affect the stored snapshot.
            received.Task = "tampered";
            var snapshot = Snapshot(tracker);
            var entry = Assert.Single(snapshot);
            Assert.Equal("hello", entry.Task);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 9. GetSubAgents returns empty before any post ──

    [Fact]
    public async Task GetSubAgents_EmptyBeforePost()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            Assert.Empty(tracker.GetSubAgents());
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 10. StopAsync publishes an empty snapshot and drops further posts ──

    [Fact]
    public async Task StopAsync_PublishesEmptySnapshot_AndDropsFurtherPosts()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            tracker.Post(MakeInfo("z", SubAgentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await DrainAsync(tracker);
            Assert.Single(Snapshot(tracker));

            // StopAsync drains and publishes an empty snapshot.
            await tracker.StopAsync();
            Assert.Empty(tracker.GetSubAgents());

            // After the writer is completed, new posts are silently dropped.
            tracker.Post(MakeInfo("dropped", SubAgentStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            Assert.Empty(tracker.GetSubAgents());
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    // ── 11. Cap tie-break — same CompletedAt, different StartedAt, Id ──

    [Fact]
    public async Task Cap_TieBreak_CompletedAtThenStartedAtThenId()
    {
        var tracker = await CreateStartedTracker();
        try
        {
            var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // Post 52 terminal entries so the cap (50) actually trims 2.
            // The two OLDEST by the total order should be dropped.
            for (var i = 0; i < 52; i++)
            {
                tracker.Post(MakeInfo(
                    $"agent-{i:D2}",
                    SubAgentStatus.Completed,
                    baseTime.AddSeconds(i),
                    baseTime.AddSeconds(i + 1)));
            }

            await DrainAsync(tracker);

            var result = Snapshot(tracker);
            Assert.Equal(50, result.Count);

            var ids = result.Select(e => e.Id).ToHashSet();
            // The two with the smallest CompletedAt (agent-00, agent-01) are dropped.
            Assert.DoesNotContain("agent-00", ids);
            Assert.DoesNotContain("agent-01", ids);
            Assert.Contains("agent-02", ids);
            Assert.Contains("agent-51", ids);
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }
}