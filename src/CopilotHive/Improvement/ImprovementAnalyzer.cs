using CopilotHive.Metrics;
using CopilotHive.Workers;

namespace CopilotHive.Improvement;

/// <summary>
/// Recommendation for whether a specific role should be improved, with a confidence score and reasons.
/// </summary>
/// <param name="Role">The worker role.</param>
/// <param name="ShouldImprove">Whether improvement is recommended (ConfidenceScore >= 50).</param>
/// <param name="ConfidenceScore">Score in [0, 100] indicating how strongly improvement is warranted.</param>
/// <param name="Reasons">Human-readable explanations for the score.</param>
public record RoleImprovementRecommendation(
    WorkerRole Role,
    bool ShouldImprove,
    int ConfidenceScore,
    List<string> Reasons
);

/// <summary>
/// Request to improve one role's AGENTS.md.
/// </summary>
/// <param name="Role">The worker role whose AGENTS.md should be improved.</param>
/// <param name="CurrentAgentsMd">The current AGENTS.md content before improvement.</param>
/// <param name="Prompt">The prompt to send to the improver worker.</param>
public sealed record ImprovementRequest(string Role, string CurrentAgentsMd, string Prompt);

/// <summary>
/// Analyses iteration metrics and decides whether/how to improve AGENTS.md files.
/// </summary>
public sealed class ImprovementAnalyzer
{
    /// <summary>
    /// Determines whether an improvement cycle is warranted after the given iteration.
    /// Skips improvement when the iteration was clean (PASS, no retries, no issues).
    /// </summary>
    public bool ShouldImprove(IterationMetrics metrics)
    {
        if (metrics.Verdict == TaskVerdict.Fail)
            return true;

        if (metrics.Verdict == TaskVerdict.Partial)
            return true;

        if (metrics.RetryCount > 0)
            return true;

        if (metrics.Issues.Count > 0)
            return true;

        if (metrics.FailedTests > 0)
            return true;

        // PASS with no retries and no issues — nothing to improve
        return false;
    }

    /// <summary>
    /// Builds improvement requests for the given roles based on iteration outcomes.
    /// </summary>
    /// <param name="current">Metrics from the iteration just completed.</param>
    /// <param name="history">Full metric history used to detect recurring patterns.</param>
    /// <param name="currentAgentsMd">Current AGENTS.md content keyed by role name.</param>
    /// <returns>One <see cref="ImprovementRequest"/> per role in <paramref name="currentAgentsMd"/>.</returns>
    public IReadOnlyList<ImprovementRequest> BuildRequests(
        IterationMetrics current,
        IReadOnlyList<IterationMetrics> history,
        IReadOnlyDictionary<string, string> currentAgentsMd)
    {
        var requests = new List<ImprovementRequest>();
        var analysis = BuildAnalysis(current, history);

        foreach (var (role, agentsMd) in currentAgentsMd)
        {
            var prompt = BuildPromptForRole(role, agentsMd, analysis);
            requests.Add(new ImprovementRequest(role, agentsMd, prompt));
        }

        return requests;
    }

    internal string BuildAnalysis(
        IterationMetrics current,
        IReadOnlyList<IterationMetrics> history,
        IReadOnlyDictionary<string, string> agentsMd)
    {
        // The agents.md files are available on disk in the worker's working directory.
        // Injecting their contents into the prompt wastes tokens and can cause timeouts.
        // We only include the metric analysis; the improver reads files directly.
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildAnalysis(current, history));

        if (agentsMd.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## AGENTS.md Files Available on Disk");
            sb.AppendLine("The following agents.md files are in your working directory — read them directly:");
            foreach (var role in agentsMd.Keys)
                sb.AppendLine($"- agents/{role}.agents.md");
        }

        return sb.ToString();
    }

    internal string BuildAnalysis(IterationMetrics current, IReadOnlyList<IterationMetrics> history)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("## Current Iteration Results");
        sb.AppendLine($"- Iteration: {current.Iteration}");
        sb.AppendLine($"- Verdict: {current.Verdict}");
        sb.AppendLine($"- Duration: {current.Duration.TotalMinutes:F1} minutes");
        sb.AppendLine($"- Total retries: {current.RetryCount} (review: {current.ReviewRetryCount}, test: {current.TestRetryCount})");
        sb.AppendLine($"- Build success: {current.BuildSuccess}");
        sb.AppendLine($"- Unit tests: {current.PassedTests}/{current.TotalTests} passed");
        sb.AppendLine($"- Integration tests: {current.IntegrationTestsPassed}/{current.IntegrationTestsTotal} passed");
        sb.AppendLine($"- Coverage: {current.CoveragePercent:F1}%");

        if (current.ReviewVerdict is not null)
            sb.AppendLine($"- Review verdict: {current.ReviewVerdict} ({current.ReviewIssuesFound} issues found)");

        if (current.PhaseDurations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Phase Durations");
            foreach (var (phase, duration) in current.PhaseDurations)
                sb.AppendLine($"- {phase}: {duration.TotalMinutes:F1} min");
        }

        if (!string.IsNullOrEmpty(current.TestReportSummary))
            sb.AppendLine($"- Summary: {current.TestReportSummary}");

        if (current.ReviewIssues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Review Issues Found");
            foreach (var issue in current.ReviewIssues)
                sb.AppendLine($"- {issue}");
        }

        if (current.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Issues Found");
            foreach (var issue in current.Issues)
                sb.AppendLine($"- {issue}");
        }

        if (history.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("## Metrics Trend (last 5 iterations)");
            var recent = history.TakeLast(5).ToList();
            foreach (var h in recent)
            {
                sb.AppendLine($"- Iteration {h.Iteration}: {h.Verdict} | " +
                    $"{h.PassedTests}/{h.TotalTests} tests | " +
                    $"{h.RetryCount} retries (review: {h.ReviewRetryCount}, test: {h.TestRetryCount}) | " +
                    $"{h.CoveragePercent:F1}% coverage | " +
                    $"{h.Duration.TotalMinutes:F1} min");
            }

            // Identify recurring issues
            var allIssues = history.TakeLast(3)
                .SelectMany(h => h.Issues)
                .GroupBy(i => i, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (allIssues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Recurring Issues (appeared in multiple iterations)");
                foreach (var issue in allIssues)
                    sb.AppendLine($"- {issue}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Analyses metrics for a specific role and returns a confidence-scored recommendation.
    /// </summary>
    public RoleImprovementRecommendation AnalyzeRole(
        WorkerRole role,
        IterationMetrics current,
        IReadOnlyList<IterationMetrics> history)
    {
        List<(Func<IterationMetrics, bool> HasSignal, Func<IterationMetrics, string> CurrentReason, string HistorySignalName)>? signals =
            role switch
            {
                WorkerRole.Reviewer =>
                [
                    (m => m.ReviewIssuesFound > 0,
                     m => $"Current iteration had {m.ReviewIssuesFound} review issues",
                     "review issues"),
                    (m => m.ReviewRetryCount > 0,
                     m => $"Current iteration had {m.ReviewRetryCount} review retries",
                     "review retries"),
                ],
                WorkerRole.Tester =>
                [
                    (m => m.FailedTests > 0,
                     m => $"Current iteration had {m.FailedTests} test failures",
                     "test failures"),
                    (m => m.TestRetryCount > 0,
                     m => $"Current iteration had {m.TestRetryCount} test retries",
                     "test retries"),
                ],
                WorkerRole.Coder =>
                [
                    (m => m.Issues.Count > 0,
                     m => $"Current iteration had {m.Issues.Count} code issues",
                     "code issues"),
                    (m => !m.BuildSuccess,
                     _ => "Current iteration had a build failure",
                     "build failures"),
                ],
                WorkerRole.DocWriter or WorkerRole.Improver or WorkerRole.Orchestrator => [],
                _ => throw new InvalidOperationException($"Unhandled role in AnalyzeRole: '{role}'"),
            };

        if (signals.Count == 0)
            return new RoleImprovementRecommendation(role, false, 0, []);

        var reasons = new List<string>();
        int score = 0;

        foreach (var (hasSignal, currentReason, historySignalName) in signals)
        {
            if (hasSignal(current))
            {
                score += 25;
                reasons.Add(currentReason(current));
            }

            int historicalCount = history.Count(hasSignal);
            if (historicalCount > 0)
            {
                score += historicalCount * 10;
                reasons.Add($"{historicalCount} of {history.Count} historical iterations had {historySignalName}");
            }
        }

        score = Math.Clamp(score, 0, 100);
        return new RoleImprovementRecommendation(role, score >= 50, score, reasons);
    }

    /// <summary>
    /// Returns improvement recommendations for all roles, sorted by ConfidenceScore descending
    /// (ties broken alphabetically by Role).
    /// </summary>
    public IReadOnlyList<RoleImprovementRecommendation> GetRolePriorities(
        IterationMetrics current,
        IReadOnlyList<IterationMetrics> history)
    {
        return WorkerRoles.ImprovableRoles
            .Select(role => AnalyzeRole(role, current, history))
            .OrderByDescending(r => r.ConfidenceScore)
            .ThenBy(r => r.Role)
            .ToList();
    }

    private static string BuildPromptForRole(string role, string agentsMd, string analysis)
    {
        return $"""
            You are the CopilotHive Improver. Analyse the iteration results below and improve
            the {role}'s AGENTS.md to address the problems found.

            {analysis}

            ## Current {role}.agents.md

            ```
            {agentsMd}
            ```

            ## Instructions

            Return the complete improved {role}.agents.md content. If no changes are needed,
            return the content unchanged. Focus on:
            - Addressing specific issues listed above
            - Reducing retry count (clearer instructions = fewer failures)
            - Improving output format compliance
            - Making instructions more precise where ambiguity caused problems

            Return ONLY the improved AGENTS.md content between these markers:

            === IMPROVED {role}.agents.md ===
            <content>
            === END {role}.agents.md ===
            """;
    }
}
