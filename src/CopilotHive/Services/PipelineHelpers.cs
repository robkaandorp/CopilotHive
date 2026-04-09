using CopilotHive.Goals;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Pure-function helpers used by the pipeline: output summarization, iteration summary building,
/// commit message construction, and conversation prompt extraction.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
internal static class PipelineHelpers
{
    /// <summary>
    /// Builds a concise summary of worker output for the pipeline conversation.
    /// The Brain uses this to understand what each phase produced — especially
    /// WHY a reviewer rejected or what tests failed.
    /// </summary>
    internal static string BuildWorkerOutputSummary(GoalPhase phase, string verdict, TaskResult result)
    {
        var parts = new List<string> { $"Phase {phase} completed — verdict: {verdict}" };

        if (result.GitStatus is { } git && git.FilesChanged > 0)
            parts.Add($"Files changed: {git.FilesChanged} (+{git.Insertions} -{git.Deletions})");

        if (result.GitStatus is { FilesChanged: > 0, Pushed: false })
            parts.Add("⚠️ Git push FAILED — changes were not pushed to the remote");

        if (result.Metrics is { } m)
        {
            if (m.TotalTests > 0)
                parts.Add($"Tests: {m.PassedTests}/{m.TotalTests} passed, {m.FailedTests} failed");

            if (m.Issues is { Count: > 0 })
            {
                parts.Add("Issues found:");
                foreach (var issue in m.Issues)
                    parts.Add($"  - {issue}");
            }
        }

        // Include summary from structured metrics when available; fall back to truncated raw output
        var summary = result.Metrics?.Summary;
        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add($"Worker summary:\n{summary}");
        else if (!string.IsNullOrWhiteSpace(result.Output))
        {
            const int maxOutputChars = 1500;
            var truncated = result.Output.Length > maxOutputChars
                ? result.Output[..maxOutputChars] + "..."
                : result.Output;
            parts.Add($"Worker output (no summary):\n{truncated}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Builds an <see cref="IterationSummary"/> from the pipeline's <see cref="GoalPipeline.PhaseLog"/>.
    /// Entries for the current iteration are extracted directly — no plan-walking or PhaseDurations needed.
    /// </summary>
    /// <param name="pipeline">Pipeline whose PhaseLog to summarise.</param>
    /// <returns>A populated <see cref="IterationSummary"/>.</returns>
    internal static IterationSummary BuildIterationSummary(GoalPipeline pipeline)
    {
        var metrics = pipeline.Metrics;
        var iteration = pipeline.Iteration;

        // Filter PhaseLog entries for this iteration
        var entries = pipeline.PhaseLog
            .Where(e => e.Iteration == iteration)
            .ToList();

        // The PhaseLog entries ARE the phases — same type, no mapping needed.
        var phases = entries.ToList();

        TestCounts? testCounts = metrics.TotalTests > 0
            ? new TestCounts
            {
                Total = metrics.TotalTests,
                Passed = metrics.PassedTests,
                Failed = metrics.FailedTests,
            }
            : null;

        string? reviewVerdict = metrics.ReviewVerdict switch
        {
            ReviewVerdict.Approve => "approve",
            ReviewVerdict.RequestChanges => "reject",
            _ => null,
        };

        // Check for improver skip in the PhaseLog
        var improverSkipEntry = entries.FirstOrDefault(e => e.Name == GoalPhase.Improve && e.Result == PhaseOutcome.Skip);
        var notes = new List<string>();
        if (improverSkipEntry is not null)
            notes.Add($"improver skipped: {improverSkipEntry.Verdict ?? "unknown"}");

        // Build PhaseOutputs dictionary from log entries for backward compat
        var phaseOutputs = new Dictionary<string, string>();
        foreach (var entry in entries.Where(e => e.WorkerOutput is not null))
        {
            var roleName = entry.Name.ToRoleName();
            if (!string.IsNullOrEmpty(roleName))
            {
                phaseOutputs[$"{roleName}-{entry.Iteration}"] = entry.WorkerOutput!;
                if (entry.Occurrence is > 0)
                    phaseOutputs[$"{roleName}-{entry.Iteration}-{entry.Occurrence}"] = entry.WorkerOutput!;
            }
        }

        return new IterationSummary
        {
            Iteration = iteration,
            Phases = phases,
            TestCounts = testCounts,
            BuildSuccess = metrics.BuildSuccess,
            ReviewVerdict = reviewVerdict,
            Notes = notes,
            PhaseOutputs = phaseOutputs,
            PlanReason = pipeline.Plan?.Reason,
            Clarifications = pipeline.Clarifications
                .Where(c => c.Iteration == iteration)
                .Select(c => new PersistedClarification
                {
                    Timestamp = c.Timestamp,
                    Phase = c.Phase,
                    WorkerRole = c.WorkerRole,
                    Question = c.Question,
                    Answer = c.Answer,
                    AnsweredBy = c.AnsweredBy,
                    Occurrence = c.Occurrence,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Builds a squash-merge commit message from a goal ID and description.
    /// The first line is formatted as <c>Goal: {goalId} — {summary}</c> and truncated to 120 characters.
    /// When the description exceeds the truncation limit, the full description is appended as the commit body.
    /// </summary>
    /// <param name="goalId">The goal identifier.</param>
    /// <param name="description">The goal description (may be multi-line or very long).</param>
    /// <returns>A commit message suitable for a squash merge commit.</returns>
    internal static string BuildSquashCommitMessage(string goalId, string description)
    {
        const int MaxSummaryLength = 120;

        // Use only the first line of the description as the summary
        var firstLine = description.Split('\n', StringSplitOptions.None)[0].Trim();
        var subject = $"Goal: {goalId} \u2014 {firstLine}";

        if (subject.Length <= MaxSummaryLength && firstLine == description.Trim())
        {
            // Short single-line description — subject only
            return subject;
        }

        // Truncate the subject line if needed
        if (subject.Length > MaxSummaryLength)
            subject = subject[..MaxSummaryLength];

        // Append the full description as the commit body
        return $"{subject}\n\n{description.Trim()}";
    }

    /// <summary>
    /// Injects GH_TOKEN into a GitHub URL for authenticated operations.
    /// </summary>
    internal static string InjectTokenIntoUrl(string url)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token) || !url.StartsWith("https://github.com/"))
            return url;

        return url.Replace("https://github.com/", $"https://x-access-token:{token}@github.com/");
    }

    /// <summary>
    /// Extracts the last "craft-prompt" user entry from the pipeline conversation
    /// for the current iteration. This is the prompt that was sent TO the Brain
    /// when asking it to craft a worker prompt.
    /// </summary>
    internal static string? GetLastCraftPromptFromConversation(GoalPipeline pipeline)
    {
        return pipeline.Conversation
            .LastOrDefault(e => e.Iteration == pipeline.Iteration
                             && e.Purpose == "craft-prompt"
                             && e.Role == "user")
            ?.Content;
    }

    /// <summary>
    /// Extracts the planning prompt and response from the pipeline conversation
    /// for the current iteration. Returns the last "planning" user/assistant pair.
    /// </summary>
    internal static (string? Prompt, string? Response) GetPlanningPromptsFromConversation(GoalPipeline pipeline)
    {
        var planningEntries = pipeline.Conversation
            .Where(e => e.Iteration == pipeline.Iteration && e.Purpose == "planning")
            .ToList();
        var prompt = planningEntries.LastOrDefault(e => e.Role == "user")?.Content;
        var response = planningEntries.LastOrDefault(e => e.Role == "assistant")?.Content;
        return (prompt, response);
    }
}
