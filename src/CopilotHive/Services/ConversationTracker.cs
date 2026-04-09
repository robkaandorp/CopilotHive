using CopilotHive.Orchestration;

namespace CopilotHive.Services;

/// <summary>
/// Owns the per-goal conversation history and builds context summaries
/// for the Brain from that history plus pipeline state.
/// </summary>
public sealed class ConversationTracker
{
    /// <summary>Per-goal conversation entries for the Brain.</summary>
    public List<ConversationEntry> Entries { get; } = [];

    /// <summary>
    /// Builds a context summary for the Brain about the pipeline's current state.
    /// Includes phase log outputs with truncation for long worker output.
    /// </summary>
    public string BuildContextSummary(GoalPipeline pipeline)
    {
        var parts = new List<string>
        {
            $"Goal: {pipeline.Description}",
            $"Phase: {pipeline.Phase}",
            $"Iteration: {pipeline.Iteration}",
            $"Review retries: {pipeline.ReviewRetries}/{pipeline.MaxRetries}",
            $"Test retries: {pipeline.TestRetries}/{pipeline.MaxRetries}",
        };

        if (pipeline.CoderBranch is not null)
            parts.Add($"Branch: {pipeline.CoderBranch}");

        foreach (var entry in pipeline.PhaseLog.Where(e => e.WorkerOutput is not null))
        {
            var key = $"{entry.Name.ToRoleName()}-{entry.Iteration}-{entry.Occurrence}";
            var output = entry.WorkerOutput!;
            var truncated = output.Length > 2000 ? output[..2000] + "..." : output;
            parts.Add($"\n=== Output from {key} ===\n{truncated}\n=== End output from {key} ===");
        }

        return string.Join("\n", parts);
    }
}
