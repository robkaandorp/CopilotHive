using CopilotHive.Goals;
using CopilotHive.Orchestration;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Reconstructs and publishes system events for state changes that occurred while the
/// orchestrator was down. The in-memory <see cref="ComposerEventSubscriber"/> buffer is
/// empty after a restart, so this scanner queries recent goals, releases, and issues and
/// publishes the corresponding events to the event bus so the Composer is aware of what
/// happened while it was offline.
/// </summary>
internal sealed class EventBusStartupScanner
{
    private readonly IGoalStore? _goalStore;
    private readonly IIssueStore? _issueStore;
    private readonly IEventBus _eventBus;
    private readonly Composer _composer;
    private readonly ILogger<EventBusStartupScanner> _logger;

    /// <summary>Fallback window when no persisted session activity is available.</summary>
    private static readonly TimeSpan FallbackWindow = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Initialises a new <see cref="EventBusStartupScanner"/>.
    /// </summary>
    /// <param name="goalStore">Goal store, or <c>null</c> to skip goal/release events.</param>
    /// <param name="issueStore">Issue store, or <c>null</c> to skip issue events.</param>
    /// <param name="eventBus">The event bus to publish reconstructed events to.</param>
    /// <param name="composer">The Composer whose session activity bounds the scan window.</param>
    /// <param name="logger">Logger.</param>
    public EventBusStartupScanner(
        IGoalStore? goalStore,
        IIssueStore? issueStore,
        IEventBus eventBus,
        Composer composer,
        ILogger<EventBusStartupScanner> logger)
    {
        _goalStore = goalStore;
        _issueStore = issueStore;
        _eventBus = eventBus;
        _composer = composer;
        _logger = logger;
    }

    /// <summary>
    /// Scans recent state changes since the Composer's last session activity and publishes
    /// reconstructed events to the event bus, ordered by timestamp ascending.
    /// </summary>
    /// <param name="ct">Cancellation token (application lifetime).</param>
    public async Task ScanAsync(CancellationToken ct)
    {
        var cutoff = GetCutoff();
        var events = new List<SystemEvent>();

        if (_goalStore is null)
        {
            _logger.LogWarning("Event bus startup scan: no goal store available — skipping goal and release events");
        }
        else
        {
            await CollectGoalEventsAsync(cutoff, events, ct);
            await CollectReleaseEventsAsync(cutoff, events, ct);
        }

        if (_issueStore is null)
        {
            _logger.LogWarning("Event bus startup scan: no issue store available — skipping issue events");
        }
        else
        {
            await CollectIssueEventsAsync(cutoff, events, ct);
        }

        // Publish in chronological order (oldest first) so the Composer sees events
        // in the order they actually happened.
        foreach (var evt in events.OrderBy(e => e.Timestamp))
            _eventBus.Publish(evt);

        _logger.LogInformation(
            "Event bus startup scan: published {Count} reconstructed event(s) since {Cutoff:O}",
            events.Count, cutoff);
    }

    /// <summary>
    /// Computes the scan cutoff: the Composer's last session activity when a session was
    /// loaded from disk and connected, otherwise a 60-minute fallback window.
    /// </summary>
    private DateTime GetCutoff()
    {
        if (_composer.SessionLoadedFromDisk
            && _composer.GetLastSessionActivity() is { } lastActivity
            && lastActivity > DateTimeOffset.MinValue)
        {
            return lastActivity.UtcDateTime;
        }

        return DateTime.UtcNow - FallbackWindow;
    }

    private async Task CollectGoalEventsAsync(DateTime cutoff, List<SystemEvent> events, CancellationToken ct)
    {
        IReadOnlyList<Goal> goals;
        try
        {
            goals = await _goalStore!.GetAllGoalsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event bus startup scan failed to load goals");
            return;
        }

        foreach (var goal in goals)
        {
            if (goal.CompletedAt is not { } completedAt || completedAt <= cutoff)
                continue;

            switch (goal.Status)
            {
                case GoalStatus.Completed:
                    events.Add(new SystemEvent(
                        EventType.GoalCompleted,
                        "Goal merged successfully",
                        GoalId: goal.Id,
                        Timestamp: completedAt));
                    break;
                case GoalStatus.Failed:
                    events.Add(new SystemEvent(
                        EventType.GoalFailed,
                        goal.FailureReason ?? "Goal failed",
                        GoalId: goal.Id,
                        Timestamp: completedAt));
                    break;
            }
        }
    }

    private async Task CollectReleaseEventsAsync(DateTime cutoff, List<SystemEvent> events, CancellationToken ct)
    {
        IReadOnlyList<Release> releases;
        try
        {
            releases = await _goalStore!.GetReleasesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event bus startup scan failed to load releases");
            return;
        }

        foreach (var release in releases)
        {
            if (release.Status != ReleaseStatus.Released
                || release.ReleasedAt is not { } releasedAt
                || releasedAt <= cutoff)
            {
                continue;
            }

            events.Add(new SystemEvent(
                EventType.ReleaseCompleted,
                $"Release '{release.Tag}' marked as Released",
                ReleaseId: release.Id,
                Timestamp: releasedAt));
        }
    }

    private async Task CollectIssueEventsAsync(DateTime cutoff, List<SystemEvent> events, CancellationToken ct)
    {
        IReadOnlyList<Issue> issues;
        try
        {
            issues = await _issueStore!.GetIssuesAsync(ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event bus startup scan failed to load issues");
            return;
        }

        foreach (var issue in issues)
        {
            if (issue.CreatedAt > cutoff)
            {
                events.Add(new SystemEvent(
                    EventType.IssueRaised,
                    issue.Title,
                    GoalId: issue.SourceGoalId,
                    IssueId: issue.Id,
                    Timestamp: issue.CreatedAt));
            }

            if ((issue.Status == IssueStatus.Resolved || issue.Status == IssueStatus.Closed)
                && issue.ResolvedAt is { } resolvedAt
                && resolvedAt > cutoff)
            {
                events.Add(new SystemEvent(
                    EventType.IssueResolved,
                    $"Issue '{issue.Id}' marked as {issue.Status}",
                    GoalId: issue.LinkedGoalId,
                    IssueId: issue.Id,
                    Timestamp: resolvedAt));
            }
        }
    }
}
