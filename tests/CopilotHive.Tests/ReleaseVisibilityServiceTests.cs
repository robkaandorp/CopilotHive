using CopilotHive.Goals;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="ReleaseVisibilityService"/>, the pure helper that decides
/// which releases of a repo-group are visible by default and which are collapsed behind
/// the per-group "Show older releases" toggle. Supersedes the old grouping tests.
/// </summary>
public sealed class ReleaseVisibilityServiceTests
{
    private static readonly DateTime Base = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Release Planning(string id, int createdDays) => new()
    {
        Id = id,
        Tag = id,
        Status = ReleaseStatus.Planning,
        CreatedAt = Base.AddDays(createdDays),
        ReleasedAt = null,
    };

    private static Release Released(string id, int createdDays, int? releasedDays) => new()
    {
        Id = id,
        Tag = id,
        Status = ReleaseStatus.Released,
        CreatedAt = Base.AddDays(createdDays),
        ReleasedAt = releasedDays is null ? null : Base.AddDays(releasedDays.Value),
    };

    private static List<string> Ids(IEnumerable<Release> releases) => releases.Select(r => r.Id).ToList();

    // ── Admitted-set scoping ──────────────────────────────────────────────────

    [Fact]
    public void AllStatusesFilter_PlanningRelease_IsNeverCollapsed()
    {
        // "All statuses" → Planning is admitted. Even with a full page of dated
        // released entries, Planning must stay visible.
        var admitted = new List<Release> { Planning("v9.0.0-plan", 100) };
        for (var i = 0; i < 20; i++)
            admitted.Add(Released($"v1.{i:00}.0", i, i));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Contains(partition.VisibleReleases, r => r.Id == "v9.0.0-plan");
        Assert.DoesNotContain(partition.CollapsedReleases, r => r.Id == "v9.0.0-plan");
    }

    [Fact]
    public void ReleasedOnlyFilter_PlanningExcludedFromAdmittedSet_HelperNeverSeesIt()
    {
        // Caller applies the status filter; the helper only receives the admitted set.
        var all = new List<Release>
        {
            Planning("v2.0.0", 5),
            Released("v1.0.0", 1, 2),
        };
        var admitted = all.Where(r => r.Status == ReleaseStatus.Released).ToList();

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(["v1.0.0"], Ids(partition.VisibleReleases));
        Assert.Empty(partition.CollapsedReleases);
        Assert.Equal(0, partition.HiddenCount);
    }

    // ── Eligibility: undated released releases ────────────────────────────────

    [Fact]
    public void UndatedReleased_IsAlwaysCollapsed_EvenWhenGroupIsSmall()
    {
        // Only 2 dated eligible releases (well below the cap) — the undated one
        // must still be collapsed and must NOT take a visible slot.
        var admitted = new List<Release>
        {
            Released("v1.0.0", 1, 1),
            Released("v1.1.0", 2, 2),
            Released("v0.9.0-undated", 3, null),
        };

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(["v1.1.0", "v1.0.0"], Ids(partition.VisibleReleases));
        Assert.Equal(["v0.9.0-undated"], Ids(partition.CollapsedReleases));
        Assert.Equal(1, partition.HiddenCount);
    }

    [Fact]
    public void VisibleSet_IsExactlyFirstNDatedEligible_UndatedAndOlderCollapsed()
    {
        var admitted = new List<Release>();
        // 15 dated eligible: released day 1..15 → newest 12 visible.
        for (var i = 1; i <= 15; i++)
            admitted.Add(Released($"v1.{i:00}.0", i, i));
        // 2 undated released.
        admitted.Add(Released("v0.1.0-undated", 50, null));
        admitted.Add(Released("v0.2.0-undated", 51, null));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(ReleaseVisibilityService.VisibleRecentReleasedCount, partition.VisibleReleases.Count);
        Assert.Equal(
            ["v1.15.0", "v1.14.0", "v1.13.0", "v1.12.0", "v1.11.0", "v1.10.0",
             "v1.09.0", "v1.08.0", "v1.07.0", "v1.06.0", "v1.05.0", "v1.04.0"],
            Ids(partition.VisibleReleases));

        // Older dated first, undated last.
        Assert.Equal(
            ["v1.03.0", "v1.02.0", "v1.01.0", "v0.2.0-undated", "v0.1.0-undated"],
            Ids(partition.CollapsedReleases));
        Assert.Equal(5, partition.HiddenCount);
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public void Visible_PlanningByCreatedAtDesc_ThenDatedReleasedByReleasedAtDesc()
    {
        var admitted = new List<Release>
        {
            Planning("plan-old", 1),
            Planning("plan-new", 9),
            Released("rel-old", 2, 3),
            Released("rel-new", 3, 8),
        };

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(["plan-new", "plan-old", "rel-new", "rel-old"], Ids(partition.VisibleReleases));
    }

    [Fact]
    public void DatedReleased_SortByReleasedAt_NotCreatedAt()
    {
        // The v0.13.0-style case: created earlier than v0.12.0 but released later.
        var admitted = new List<Release>
        {
            Released("v0.12.0", createdDays: 20, releasedDays: 30),
            Released("v0.13.0", createdDays: 10, releasedDays: 40),
        };

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(["v0.13.0", "v0.12.0"], Ids(partition.VisibleReleases));
    }

    [Fact]
    public void ReleasedAtTie_ResolvedByCreatedAtDesc_ThenIdAscOrdinal()
    {
        var admitted = new List<Release>
        {
            Released("b", createdDays: 1, releasedDays: 10),
            Released("a", createdDays: 1, releasedDays: 10),
            Released("c", createdDays: 5, releasedDays: 10),
        };

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        // c has the newest CreatedAt; a/b tie on CreatedAt → Id ascending ordinal.
        Assert.Equal(["c", "a", "b"], Ids(partition.VisibleReleases));
    }

    [Fact]
    public void TieAtCapBoundary_ResolvesDeterministicallyByIdAscOrdinal()
    {
        var admitted = new List<Release>();
        // 11 clearly-newest dated entries.
        for (var i = 1; i <= 11; i++)
            admitted.Add(Released($"v2.{i:00}.0", 100 + i, 100 + i));

        // Two candidates tie exactly on ReleasedAt and CreatedAt for slot 12.
        admitted.Add(Released("tie-B", 50, 50));
        admitted.Add(Released("tie-A", 50, 50));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(ReleaseVisibilityService.VisibleRecentReleasedCount, partition.VisibleReleases.Count);
        Assert.Equal("tie-A", partition.VisibleReleases[^1].Id);
        Assert.Equal(["tie-B"], Ids(partition.CollapsedReleases));
        Assert.Equal(1, partition.HiddenCount);
    }

    [Fact]
    public void Collapsed_OlderDatedInRecencyOrder_ThenUndatedLast()
    {
        var admitted = new List<Release>();
        for (var i = 1; i <= 12; i++)
            admitted.Add(Released($"v3.{i:00}.0", 200 + i, 200 + i));

        admitted.Add(Released("older-1", 5, 5));
        admitted.Add(Released("older-2", 7, 7));
        admitted.Add(Released("undated-x", 90, null));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(["older-2", "older-1", "undated-x"], Ids(partition.CollapsedReleases));
        Assert.Equal(3, partition.HiddenCount);
    }

    // ── Multi-repo independence ───────────────────────────────────────────────

    [Fact]
    public void MultiRepo_EachGroupPartitionsIndependently()
    {
        var repoA = new List<Release>();
        for (var i = 1; i <= 14; i++)
            repoA.Add(Released($"a-{i:00}", i, i));

        var repoB = new List<Release>
        {
            Released("b-01", 1, 1),
            Released("b-undated", 2, null),
            Planning("b-plan", 3),
        };

        var partitionA = ReleaseVisibilityService.PartitionGroup(repoA);
        var partitionB = ReleaseVisibilityService.PartitionGroup(repoB);

        Assert.Equal(12, partitionA.VisibleReleases.Count);
        Assert.Equal(2, partitionA.HiddenCount);

        Assert.Equal(["b-plan", "b-01"], Ids(partitionB.VisibleReleases));
        Assert.Equal(["b-undated"], Ids(partitionB.CollapsedReleases));
        Assert.Equal(1, partitionB.HiddenCount);
    }

    // ── Partition is constant (expansion is the caller's concern) ─────────────

    [Fact]
    public void Partition_AlwaysKeepsOlderReleasesCollapsed_RegardlessOfCallerExpansionState()
    {
        // The helper has no expansion input: overflow always lands in CollapsedReleases
        // so the page can render it after the toggle when the group is expanded.
        var admitted = new List<Release> { Planning("plan", 99) };
        for (var i = 1; i <= 15; i++)
            admitted.Add(Released($"v4.{i:00}.0", i, i));
        admitted.Add(Released("undated", 60, null));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        // Visible: the Planning release + the 12 most-recently-released dated entries.
        Assert.Equal(1 + ReleaseVisibilityService.VisibleRecentReleasedCount, partition.VisibleReleases.Count);
        Assert.Equal("plan", partition.VisibleReleases[0].Id);
        Assert.Equal("v4.15.0", partition.VisibleReleases[1].Id);
        Assert.DoesNotContain(partition.VisibleReleases, r => r.Id == "undated");

        // Collapsed is NON-empty: older dated in recency order, undated last.
        Assert.Equal(["v4.03.0", "v4.02.0", "v4.01.0", "undated"], Ids(partition.CollapsedReleases));
        Assert.Equal(4, partition.HiddenCount);
        Assert.True(partition.HiddenCount > 0);

        // Every admitted release appears exactly once across both sets.
        Assert.Equal(admitted.Count, partition.VisibleReleases.Count + partition.CollapsedReleases.Count);
    }

    [Fact]
    public void Partition_IsDeterministic_RepeatedCallsReturnEqualPartitions()
    {
        var admitted = new List<Release>();
        for (var i = 1; i <= 14; i++)
            admitted.Add(Released($"v8.{i:00}.0", i, i));
        admitted.Add(Released("undated", 40, null));

        var first = ReleaseVisibilityService.PartitionGroup(admitted);
        var second = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(Ids(first.VisibleReleases), Ids(second.VisibleReleases));
        Assert.Equal(Ids(first.CollapsedReleases), Ids(second.CollapsedReleases));
        Assert.Equal(first.HiddenCount, second.HiddenCount);
        Assert.Equal(3, first.HiddenCount);
    }

    [Fact]
    public void MoreThanTwelveDatedEligible_HidesOverflow()
    {
        var admitted = new List<Release>();
        for (var i = 1; i <= 13; i++)
            admitted.Add(Released($"v5.{i:00}.0", i, i));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(12, partition.VisibleReleases.Count);
        Assert.Equal(["v5.01.0"], Ids(partition.CollapsedReleases));
        Assert.Equal(1, partition.HiddenCount);
    }

    // ── Cap boundary ──────────────────────────────────────────────────────────

    [Fact]
    public void ExactlyTwelveDatedEligible_NothingHidden()
    {
        var admitted = new List<Release>();
        for (var i = 1; i <= 12; i++)
            admitted.Add(Released($"v6.{i:00}.0", i, i));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(12, partition.VisibleReleases.Count);
        Assert.Empty(partition.CollapsedReleases);
        Assert.Equal(0, partition.HiddenCount);
    }

    [Fact]
    public void ThirteenDatedEligible_ThirteenthIsCollapsed()
    {
        var admitted = new List<Release>();
        for (var i = 1; i <= 13; i++)
            admitted.Add(Released($"v7.{i:00}.0", i, i));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(12, partition.VisibleReleases.Count);
        Assert.Equal(1, partition.HiddenCount);
        Assert.Equal("v7.01.0", partition.CollapsedReleases[0].Id);
    }

    [Fact]
    public void ZeroDatedEligible_WithFiveUndated_AllCollapsed_OnlyPlanningVisible()
    {
        var admitted = new List<Release>
        {
            Planning("plan-a", 3),
            Planning("plan-b", 8),
        };
        for (var i = 1; i <= 5; i++)
            admitted.Add(Released($"u-{i}", i, null));

        var partition = ReleaseVisibilityService.PartitionGroup(admitted);

        Assert.Equal(["plan-b", "plan-a"], Ids(partition.VisibleReleases));
        Assert.Equal(5, partition.CollapsedReleases.Count);
        Assert.Equal(5, partition.HiddenCount);
        Assert.All(partition.CollapsedReleases, r => Assert.StartsWith("u-", r.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyGroup_ProducesEmptyPartition()
    {
        var partition = ReleaseVisibilityService.PartitionGroup([]);

        Assert.Empty(partition.VisibleReleases);
        Assert.Empty(partition.CollapsedReleases);
        Assert.Equal(0, partition.HiddenCount);
    }

    [Fact]
    public void NullGroup_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ReleaseVisibilityService.PartitionGroup(null!));
    }
}
