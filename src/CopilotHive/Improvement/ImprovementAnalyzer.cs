using CopilotHive.Metrics;

namespace CopilotHive.Improvement;

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
        if (metrics.Verdict.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (metrics.Verdict.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase))
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
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildAnalysis(current, history));

        if (agentsMd.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Current AGENTS.md Files");
            foreach (var (role, content) in agentsMd)
            {
                sb.AppendLine($"### {role}.agents.md");
                sb.AppendLine("```");
                sb.AppendLine(content.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    internal string BuildAnalysis(IterationMetrics current, IReadOnlyList<IterationMetrics> history)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("## Current Iteration Results");
        sb.AppendLine($"- Iteration: {current.Iteration}");
        sb.AppendLine($"- Verdict: {current.Verdict}");
        sb.AppendLine($"- Retry count: {current.RetryCount}");
        sb.AppendLine($"- Build success: {current.BuildSuccess}");
        sb.AppendLine($"- Unit tests: {current.PassedTests}/{current.TotalTests} passed");
        sb.AppendLine($"- Integration tests: {current.IntegrationTestsPassed}/{current.IntegrationTestsTotal} passed");
        sb.AppendLine($"- Coverage: {current.CoveragePercent:F1}%");

        if (!string.IsNullOrEmpty(current.TestReportSummary))
            sb.AppendLine($"- Summary: {current.TestReportSummary}");

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
                    $"{h.RetryCount} retries | " +
                    $"{h.CoveragePercent:F1}% coverage");
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
