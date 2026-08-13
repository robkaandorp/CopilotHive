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

    // ── Source-level verification: every LlmSessionInfo sets ReasoningEffort ──

    /// <summary>
    /// Verifies that every production <c>new LlmSessionInfo</c> initializer includes a
    /// <c>ReasoningEffort =</c> assignment. This is a source-level assertion (following the
    /// pattern in DistributedBrainTests.cs) that guards against future LlmSessionInfo
    /// constructions silently omitting the reasoning effort field.
    /// </summary>
    [Fact]
    public void AllLlmSessionInfoInitializers_IncludeReasoningEffort()
    {
        // Find the repo root by walking up from the test assembly location.
        var repoRoot = AppContext.BaseDirectory;
        while (repoRoot is not null
               && !Directory.GetFiles(repoRoot, "*.slnx").Any()
               && !Directory.Exists(Path.Combine(repoRoot, "src", "CopilotHive")))
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);
        Assert.True(Directory.Exists(Path.Combine(repoRoot, "src", "CopilotHive")),
            $"Repo root not found from {AppContext.BaseDirectory}");

        // (file, expected minimum ReasoningEffort occurrences)
        var files = new (string RelativePath, int MinReasoningEffort)[]
        {
            (Path.Combine("src", "CopilotHive", "Orchestration", "DistributedBrain.cs"), 3),
            (Path.Combine("src", "CopilotHive", "Actors", "BrainActor.cs"), 1),
            (Path.Combine("src", "CopilotHive", "Actors", "GoalBrainActor.cs"), 1),
            (Path.Combine("src", "CopilotHive", "Orchestration", "Composer.cs"), 1),
            (Path.Combine("src", "CopilotHive", "Orchestration", "ComposerAgentService.cs"), 2),
            (Path.Combine("src", "CopilotHive", "Services", "GoalReviewService.cs"), 1),
        };

        var totalInitializers = 0;
        var totalReasoningAssignments = 0;

        foreach (var (relativePath, minReasoning) in files)
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            Assert.True(File.Exists(fullPath), $"Source file not found at {fullPath}");

            var source = File.ReadAllText(fullPath);

            // Count `new LlmSessionInfo` occurrences in this file.
            var initializerCount = CountOccurrences(source, "new LlmSessionInfo");
            totalInitializers += initializerCount;

            // Count `ReasoningEffort =` assignments in this file.
            var reasoningCount = CountOccurrences(source, "ReasoningEffort =");
            totalReasoningAssignments += reasoningCount;

            // Every `new LlmSessionInfo` initializer must include ReasoningEffort.
            // Split on `new LlmSessionInfo`; each segment after the first represents one
            // constructor call. The ReasoningEffort assignment must appear before the
            // closing `};` of that initializer.
            var segments = source.Split("new LlmSessionInfo", StringSplitOptions.None);
            for (var i = 1; i < segments.Length; i++)
            {
                var segment = segments[i];
                var closingBrace = segment.IndexOf("};", StringComparison.Ordinal);
                var initializerBody = closingBrace >= 0 ? segment[..closingBrace] : segment;
                Assert.True(initializerBody.Contains("ReasoningEffort =", StringComparison.Ordinal),
                    $"{relativePath}: LlmSessionInfo initializer #{i} is missing ReasoningEffort =");
            }

            // The file must contain at least the expected number of ReasoningEffort assignments
            // (one per LlmSessionInfo initializer, possibly more for other uses).
            Assert.True(reasoningCount >= minReasoning,
                $"{relativePath}: expected at least {minReasoning} 'ReasoningEffort =' occurrences, found {reasoningCount}");
        }

        // Exactly 9 LlmSessionInfo constructions across the 6 production files.
        Assert.Equal(9, totalInitializers);
        Assert.True(totalReasoningAssignments >= 9,
            $"Expected at least 9 'ReasoningEffort =' assignments across all files, found {totalReasoningAssignments}");
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
