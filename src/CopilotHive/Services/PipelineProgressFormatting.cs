namespace CopilotHive.Services;

/// <summary>
/// Formatting helpers for the living progress document that the Brain maintains per goal in the
/// knowledge graph. Produces markdown sections for iteration plans, worker narratives, and summaries.
/// </summary>
internal static class PipelineProgressFormatting
{
    /// <summary>
    /// Builds a markdown section describing the Brain's plan for an iteration, including the phase
    /// sequence, reasoning, and per-phase instructions.
    /// </summary>
    public static string BuildPlanSection(int iteration, IterationPlan plan)
    {
        var text = $"## Iteration {iteration}\n\n### Brain Plan\n\n" +
            $"Phases: {string.Join(" → ", plan.Phases)}\n\n";

        if (!string.IsNullOrWhiteSpace(plan.Reason))
            text += $"Reasoning: {plan.Reason}\n\n";

        // Use occurrence-based instruction lookup so multi-round plans (e.g. coding → testing →
        // coding → testing) surface the per-occurrence instructions ("coding-1", "coding-2") rather
        // than only the first occurrence's bare "coding" key. The occurrence index counts how many
        // times a specific phase has appeared so far — NOT its absolute position in the list —
        // matching the semantics of PipelineStateMachine.GetCurrentPhaseOccurrence.
        var occurrences = new Dictionary<GoalPhase, int>();
        foreach (var phase in plan.Phases)
        {
            occurrences[phase] = occurrences.GetValueOrDefault(phase, 0) + 1;
            var instructions = plan.GetPhaseInstruction(phase, occurrences[phase]);
            if (!string.IsNullOrWhiteSpace(instructions))
                text += $"**{phase}**: {instructions}\n";
        }

        return text;
    }
}
