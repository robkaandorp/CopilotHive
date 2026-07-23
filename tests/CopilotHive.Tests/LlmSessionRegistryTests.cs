using CopilotHive.Dashboard;

namespace CopilotHive.Tests;

/// <summary>Unit tests for <see cref="LlmSessionRegistry"/> and <see cref="LlmSessionInfo"/>.</summary>
public class LlmSessionRegistryTests
{
    private static LlmSessionInfo CreateSession(
        string sessionId,
        string status = "active",
        long currentTokens = 0,
        long maxTokens = 10000,
        DateTime? lastActivity = null,
        string? goalId = null)
    {
        return new LlmSessionInfo
        {
            SessionId = sessionId,
            SessionType = LlmSessionType.Brain,
            Model = "test-model",
            Status = status,
            GoalId = goalId,
            CurrentTokens = currentTokens,
            MaxTokens = maxTokens,
            LastActivity = lastActivity ?? DateTime.UtcNow,
        };
    }

    [Fact]
    public void RegisterOrUpdate_AddsNewSession()
    {
        var registry = new LlmSessionRegistry();
        var session = CreateSession("session-1");

        registry.RegisterOrUpdate(session);

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("session-1", all[0].SessionId);
    }

    [Fact]
    public void RegisterOrUpdate_UpdatesExistingSession()
    {
        var registry = new LlmSessionRegistry();
        var original = CreateSession("session-1", status: "idle", currentTokens: 100);
        var updated = CreateSession("session-1", status: "active", currentTokens: 500);

        registry.RegisterOrUpdate(original);
        registry.RegisterOrUpdate(updated);

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("active", all[0].Status);
        Assert.Equal(500, all[0].CurrentTokens);
    }

    [Fact]
    public void Unregister_RemovesSession()
    {
        var registry = new LlmSessionRegistry();
        var session = CreateSession("session-1");
        registry.RegisterOrUpdate(session);

        var removed = registry.Unregister("session-1");

        Assert.True(removed);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void Unregister_ReturnsFalseForMissingSession()
    {
        var registry = new LlmSessionRegistry();

        var removed = registry.Unregister("missing");

        Assert.False(removed);
    }

    [Fact]
    public void GetAll_ReturnsAllRegisteredSessions()
    {
        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(CreateSession("session-1"));
        registry.RegisterOrUpdate(CreateSession("session-2"));
        registry.RegisterOrUpdate(CreateSession("session-3"));

        var all = registry.GetAll();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void CleanupStale_RemovesOldSessions()
    {
        var registry = new LlmSessionRegistry();
        var stale = CreateSession("session-1", lastActivity: DateTime.UtcNow.AddHours(-2));
        registry.RegisterOrUpdate(stale);

        registry.CleanupStale(TimeSpan.FromMinutes(30));

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void CleanupStale_KeepsRecentSessions()
    {
        var registry = new LlmSessionRegistry();
        var recent = CreateSession("session-1", lastActivity: DateTime.UtcNow);
        registry.RegisterOrUpdate(recent);

        registry.CleanupStale(TimeSpan.FromHours(1));

        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void CleanupStale_KeepsSessionUpdatedBeforeCleanup()
    {
        // Sequential update case: a stale session is registered, then replaced by a fresh
        // instance (same SessionId) BEFORE CleanupStale runs. When CleanupStale enumerates
        // the dictionary it observes the fresh (non-stale) session, so it is not removed.
        // This documents the sequential ordering — it does NOT exercise the conditional
        // removal race; see CleanupStale_ConditionalRemovalPreventsDeletingUpdatedEntry.
        var registry = new LlmSessionRegistry();
        var stale = CreateSession("session-1", lastActivity: DateTime.UtcNow.AddHours(-2));
        registry.RegisterOrUpdate(stale);

        var updated = CreateSession("session-1", lastActivity: DateTime.UtcNow);
        registry.RegisterOrUpdate(updated);

        registry.CleanupStale(TimeSpan.FromMinutes(30));

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal(updated.LastActivity, all[0].LastActivity);
    }

    [Fact]
    public void CleanupStale_RemovesOnlyStaleSession()
    {
        var registry = new LlmSessionRegistry();
        var sessionA = CreateSession("session-A", lastActivity: DateTime.UtcNow.AddHours(-2));
        var sessionB = CreateSession("session-B", lastActivity: DateTime.UtcNow);
        registry.RegisterOrUpdate(sessionA);
        registry.RegisterOrUpdate(sessionB);

        registry.CleanupStale(TimeSpan.FromHours(1));

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("session-B", all[0].SessionId);
    }

    [Fact]
    public void CleanupStale_ConditionalRemovalPreventsDeletingUpdatedEntry()
    {
        var registry = new LlmSessionRegistry();

        // Register a stale session and capture the exact KeyValuePair that was stored.
        var stale = CreateSession("test-cond-removal", status: "idle", currentTokens: 100,
            lastActivity: DateTime.UtcNow.AddHours(-2));
        registry.RegisterOrUpdate(stale);
        var staleKvp = registry.Sessions.Single();

        // Update the session with a fresh, distinct record value (same SessionId).
        var updated = CreateSession("test-cond-removal", status: "active", currentTokens: 500,
            lastActivity: DateTime.UtcNow);
        registry.RegisterOrUpdate(updated);
        var freshKvp = registry.Sessions.Single();

        // The stale snapshot's KeyValuePair no longer matches the current entry (records use
        // value equality, and LastActivity/Status/CurrentTokens all changed), so the
        // conditional Remove returns false and the session is kept.
        var removedStale = ((ICollection<KeyValuePair<string, LlmSessionInfo>>)registry.Sessions)
            .Remove(staleKvp);
        Assert.False(removedStale);
        Assert.Single(registry.GetAll());

        // The fresh KeyValuePair matches the current entry, so the conditional Remove succeeds.
        var removedFresh = ((ICollection<KeyValuePair<string, LlmSessionInfo>>)registry.Sessions)
            .Remove(freshKvp);
        Assert.True(removedFresh);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void ContextUsagePercent_ClampedWithLargeValues()
    {
        var session = CreateSession("session-1", currentTokens: long.MaxValue, maxTokens: 1);

        Assert.Equal(100, session.ContextUsagePercent);
    }

    [Fact]
    public void ContextUsagePercent_ReturnsZeroWhenMaxTokensIsZero()
    {
        var session = CreateSession("session-1", currentTokens: 5000, maxTokens: 0);

        Assert.Equal(0, session.ContextUsagePercent);
    }

    [Fact]
    public void ContextUsagePercent_ReturnsZeroWhenMaxTokensIsNegative()
    {
        var session = CreateSession("session-1", currentTokens: 5000, maxTokens: -1);

        Assert.Equal(0, session.ContextUsagePercent);
    }

    [Fact]
    public void ContextUsagePercent_ReturnsCorrectPercentage()
    {
        var session = CreateSession("session-1", currentTokens: 5000, maxTokens: 10000);

        Assert.Equal(50, session.ContextUsagePercent);
    }
}
