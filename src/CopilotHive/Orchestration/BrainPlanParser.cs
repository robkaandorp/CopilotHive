using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Orchestration;

/// <summary>
/// Pure static helper that transforms Brain tool-call results into <see cref="IterationPlan"/> instances.
/// All methods are deterministic functions — no instance state.
/// </summary>
public static class BrainPlanParser
{
    /// <summary>
    /// Builds an <see cref="IterationPlan"/> from a <c>report_iteration_plan</c> tool call result.
    /// </summary>
    internal static IterationPlan BuildIterationPlanFromToolCall(DistributedBrain.IterationPlanResult toolCall)
    {
        var phaseNames = toolCall.Phases?.ToList() ?? [];

        Dictionary<string, string>? phaseInstructions = null;
        if (!string.IsNullOrEmpty(toolCall.PhaseInstructions))
        {
            try { phaseInstructions = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.PhaseInstructions, ProtocolJson.Options); }
            catch (JsonException) { /* best-effort */ }
        }

        Dictionary<string, string>? modelTiers = null;
        if (!string.IsNullOrEmpty(toolCall.ModelTiers))
        {
            try { modelTiers = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.ModelTiers, ProtocolJson.Options); }
            catch (JsonException) { /* best-effort */ }
        }

        var dto = new IterationPlanDto
        {
            Phases = phaseNames,
            PhaseInstructions = phaseInstructions,
            ModelTiers = modelTiers,
            Reason = toolCall.Reason,
        };
        return MapIterationPlan(dto);
    }

    internal static IterationPlan MapIterationPlan(IterationPlanDto dto)
    {
        var phases = new List<GoalPhase>();
        foreach (var name in dto.Phases)
        {
            if (Enum.TryParse<GoalPhase>(name, ignoreCase: true, out var phase)
                && phase is not (GoalPhase.Planning or GoalPhase.Done or GoalPhase.Failed))
            {
                phases.Add(phase);
            }
        }

        var instructions = new Dictionary<string, string>();
        if (dto.PhaseInstructions is not null)
        {
            foreach (var (key, value) in dto.PhaseInstructions)
            {
                if (value is not null)
                    instructions[key] = value;
            }
        }

        var phaseTiers = new Dictionary<GoalPhase, ModelTier>();
        if (dto.ModelTiers is not null)
        {
            foreach (var (key, value) in dto.ModelTiers)
            {
                if (Enum.TryParse<GoalPhase>(key, ignoreCase: true, out var phase) && value is not null)
                    phaseTiers[phase] = ModelTierExtensions.ParseModelTier(value);
            }
        }

        return new IterationPlan
        {
            Phases = phases,
            PhaseInstructions = instructions,
            PhaseTiers = phaseTiers,
            Reason = dto.Reason,
        };
    }

    /// <summary>DTO for deserializing the Brain's iteration plan JSON response.</summary>
    internal sealed record IterationPlanDto
    {
        public List<string> Phases { get; init; } = [];
        public Dictionary<string, string>? PhaseInstructions { get; init; }
        public Dictionary<string, string>? ModelTiers { get; init; }
        public string? Reason { get; init; }
    }
}
