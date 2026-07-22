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
            SessionType = "brain",
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
    public void CleanupStale_DoesNotRemoveConcurrentlyUpdatedSession()
    {
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
