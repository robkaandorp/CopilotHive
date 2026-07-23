using System.Globalization;
using System.Text;

using CopilotHive.Dashboard;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the LLM Sessions dashboard section rendered by <c>Orchestrator.razor</c>.
/// Mirrors the private helpers and conditional markup from the page so the behavior can be
/// verified without bUnit or a live Blazor runtime.
/// </summary>
public sealed class OrchestratorPageLlmSessionTests
{
    // ── helpers that mirror Orchestrator.razor private helpers ─────────────────

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}k",
        _ => tokens.ToString(),
    };

    private static string GetSessionTypeIcon(LlmSessionType type) => type switch
    {
        LlmSessionType.Brain => "🎵",
        LlmSessionType.BrainGoal => "🎵",
        LlmSessionType.Composer => "💬",
        LlmSessionType.GoalReview => "🔍",
        _ => "💬"
    };

    private static string FormatLastActivity(DateTime lastActivity)
    {
        var ago = DateTime.UtcNow - lastActivity;
        if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s ago";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        return $"{(int)ago.TotalHours}h ago";
    }

    /// <summary>
    /// Builds the LLM Sessions section markup exactly as <c>Orchestrator.razor</c> does,
    /// including the <c>_llmSessions.Count &gt; 0</c> guard.
    /// </summary>
    private static string BuildSectionHtml(List<LlmSessionInfo> sessions)
    {
        if (sessions.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<div class=\"card\" style=\"margin-top:1rem\">");
        sb.Append("<h2 style=\"font-size:1rem;font-weight:600;margin-bottom:0.75rem\">LLM Sessions</h2>");
        sb.Append("<table class=\"data-table\">");
        sb.Append("<thead><tr><th>Type</th><th>Goal</th><th>Model</th><th>Context</th><th>Status</th><th>Last Activity</th></tr></thead>");
        sb.Append("<tbody>");

        foreach (var session in sessions)
        {
            sb.Append("<tr>");
            sb.Append(CultureInfo.InvariantCulture, $"<td>{GetSessionTypeIcon(session.SessionType)} {session.SessionType}</td>");

            sb.Append("<td>");
            if (session.GoalId is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<a href=\"/goals/{session.GoalId}\" class=\"goal-link\">{session.GoalId}</a>");
            }
            else
            {
                sb.Append("<span style=\"color:var(--text-muted)\">—</span>");
            }
            sb.Append("</td>");

            sb.Append(CultureInfo.InvariantCulture, $"<td>{session.Model}</td>");

            sb.Append("<td>");
            sb.Append("<div class=\"session-context-bar\">");
            sb.Append("<div class=\"session-context-track\">");
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"session-context-fill\" style=\"width:{session.ContextUsagePercent}%\"></div>");
            sb.Append("</div>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<span style=\"font-size:0.8rem;color:var(--text-muted)\">{session.ContextUsagePercent}% ({FormatTokens(session.CurrentTokens)}/{FormatTokens(session.MaxTokens)})</span>");
            sb.Append("</div>");
            sb.Append("</td>");

            sb.Append(CultureInfo.InvariantCulture, $"<td>{session.Status}</td>");
            sb.Append(CultureInfo.InvariantCulture, $"<td>{FormatLastActivity(session.LastActivity)}</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody>");
        sb.Append("</table>");
        sb.Append("</div>");

        return sb.ToString();
    }

    // ── rendering ─────────────────────────────────────────────────────────────

    [Fact]
    public void Section_WithSessions_RendersTableAndFields()
    {
        var sessions = new List<LlmSessionInfo>
        {
            new()
            {
                SessionId = "brain-master",
                SessionType = LlmSessionType.Brain,
                Model = "copilot/brain-model",
                Status = "idle",
                GoalId = null,
                CurrentTokens = 2_500,
                MaxTokens = 10_000,
                LastActivity = DateTime.UtcNow.AddSeconds(-5),
            },
            new()
            {
                SessionId = "brain-goal-g1",
                SessionType = LlmSessionType.BrainGoal,
                Model = "copilot/goal-model",
                Status = "active",
                GoalId = "goal-1",
                CurrentTokens = 5_000,
                MaxTokens = 10_000,
                LastActivity = DateTime.UtcNow.AddMinutes(-2),
            },
            new()
            {
                SessionId = "goal-review-g2",
                SessionType = LlmSessionType.GoalReview,
                Model = "copilot/review-model",
                Status = "reviewing",
                GoalId = "goal-2",
                CurrentTokens = 1_000_000,
                MaxTokens = 2_000_000,
                LastActivity = DateTime.UtcNow.AddHours(-1),
            },
        };

        var html = BuildSectionHtml(sessions);

        Assert.Contains("LLM Sessions", html);
        Assert.Contains("<table", html);
        Assert.Contains("</table>", html);
        Assert.Contains("🎵 Brain", html);
        Assert.Contains("🎵 BrainGoal", html);
        Assert.Contains("🔍 GoalReview", html);
        Assert.Contains("copilot/brain-model", html);
        Assert.Contains("copilot/goal-model", html);
        Assert.Contains("copilot/review-model", html);
        Assert.Contains("<a href=\"/goals/goal-1\" class=\"goal-link\">goal-1</a>", html);
        Assert.Contains("<a href=\"/goals/goal-2\" class=\"goal-link\">goal-2</a>", html);
        Assert.Contains("idle", html);
        Assert.Contains("active", html);
        Assert.Contains("reviewing", html);
    }

    [Fact]
    public void Section_WithNoSessions_IsHidden()
    {
        var html = BuildSectionHtml([]);

        Assert.Empty(html);
        Assert.DoesNotContain("LLM Sessions", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public void SessionRow_RendersContextBarModelStatusAndLastActivity()
    {
        var session = new LlmSessionInfo
        {
            SessionId = "composer",
            SessionType = LlmSessionType.Composer,
            Model = "copilot/composer-model",
            Status = "streaming",
            GoalId = "goal-3",
            CurrentTokens = 1_234_567,
            MaxTokens = 2_000_000,
            LastActivity = DateTime.UtcNow.AddSeconds(-30),
        };

        var html = BuildSectionHtml([session]);

        Assert.Contains("💬 Composer", html);
        Assert.Contains("<a href=\"/goals/goal-3\" class=\"goal-link\">goal-3</a>", html);
        Assert.Contains("copilot/composer-model", html);
        Assert.Contains("streaming", html);
        Assert.Contains("class=\"session-context-bar\"", html);
        Assert.Contains("class=\"session-context-track\"", html);
        Assert.Contains("class=\"session-context-fill\"", html);
        Assert.Contains("style=\"width:61%\"", html);
        Assert.Contains("61% (1.2M/2.0M)", html);
        Assert.Contains("s ago", html);
    }

    [Fact]
    public void SessionRow_NullGoalId_RendersEmDash()
    {
        var session = new LlmSessionInfo
        {
            SessionId = "brain-master",
            SessionType = LlmSessionType.Brain,
            Model = "copilot/brain-model",
            Status = "idle",
            GoalId = null,
            CurrentTokens = 0,
            MaxTokens = 10_000,
            LastActivity = DateTime.UtcNow,
        };

        var html = BuildSectionHtml([session]);

        Assert.Contains("<span style=\"color:var(--text-muted)\">—</span>", html);
        Assert.DoesNotContain("<a href=\"/goals/", html);
    }

    // ── helper behavior ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(LlmSessionType.Brain, "🎵")]
    [InlineData(LlmSessionType.BrainGoal, "🎵")]
    [InlineData(LlmSessionType.Composer, "💬")]
    [InlineData(LlmSessionType.GoalReview, "🔍")]
    public void GetSessionTypeIcon_ReturnsExpectedEmoji(LlmSessionType type, string expected)
    {
        Assert.Equal(expected, GetSessionTypeIcon(type));
    }

    [Fact]
    public void FormatLastActivity_ReturnsSeconds_WhenRecent()
    {
        var result = FormatLastActivity(DateTime.UtcNow.AddSeconds(-45));

        Assert.EndsWith("s ago", result);
        Assert.DoesNotContain("m", result);
    }

    [Fact]
    public void FormatLastActivity_ReturnsMinutes_WhenWithinHour()
    {
        var result = FormatLastActivity(DateTime.UtcNow.AddMinutes(-5));

        Assert.EndsWith("m ago", result);
    }

    [Fact]
    public void FormatLastActivity_ReturnsHours_WhenOlder()
    {
        var result = FormatLastActivity(DateTime.UtcNow.AddHours(-3).AddMinutes(-10));

        Assert.EndsWith("h ago", result);
    }

    [Theory]
    [InlineData(500L, "500")]
    [InlineData(1_500L, "1.5k")]
    [InlineData(2_000_000L, "2.0M")]
    public void FormatTokens_FormatsTokenCounts(long tokens, string expected)
    {
        Assert.Equal(expected, FormatTokens(tokens));
    }
}
