using CopilotHive.Metrics;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests.Fakes;

/// <summary>
/// Fake brain for testing. Uses delegates to simulate LLM decisions
/// with predictable, controllable behavior.
/// </summary>
public sealed class FakeOrchestratorBrain : IOrchestratorBrain
{
    public bool Started { get; private set; }
    public List<string> Prompts { get; } = [];
    public List<string> Interpretations { get; } = [];
    public List<string> Decisions { get; } = [];
    public List<string> Informations { get; } = [];

    /// <summary>
    /// Optional: override prompt crafting. Default returns a standard prompt.
    /// </summary>
    public Func<string, string, int, string, string?, string>? CraftPromptOverride { get; set; }

    /// <summary>
    /// Optional: override output interpretation. Default returns PASS/APPROVE.
    /// </summary>
    public Func<string, string, OrchestratorDecision>? InterpretOutputOverride { get; set; }

    /// <summary>
    /// Optional: override plan iteration. Default returns SpawnCoder.
    /// </summary>
    public Func<int, string, IterationMetrics?, OrchestratorDecision>? PlanIterationOverride { get; set; }

    /// <summary>
    /// Optional: override next step decision. Default returns SpawnCoder.
    /// </summary>
    public Func<string, string, OrchestratorDecision>? DecideNextStepOverride { get; set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task<OrchestratorDecision> PlanIterationAsync(
        int iteration, string goal, IterationMetrics? previousMetrics, CancellationToken ct = default)
    {
        var decision = PlanIterationOverride?.Invoke(iteration, goal, previousMetrics)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.SpawnCoder,
                Reason = "Default plan: start with coding",
            };

        return Task.FromResult(decision);
    }

    public Task<string> CraftPromptAsync(
        string workerRole, string goal, int iteration, string branchName,
        string? additionalContext, CancellationToken ct = default)
    {
        var basePrompt = CraftPromptOverride?.Invoke(workerRole, goal, iteration, branchName, additionalContext)
            ?? GetDefaultPrompt(workerRole, goal, iteration, branchName);

        // Include additionalContext so tests can match on it
        var prompt = additionalContext is not null
            ? $"{basePrompt}\n\n{additionalContext}"
            : basePrompt;

        Prompts.Add(prompt);
        return Task.FromResult(prompt);
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(
        string workerRole, string workerOutput, CancellationToken ct = default)
    {
        Interpretations.Add($"{workerRole}: {workerOutput[..Math.Min(100, workerOutput.Length)]}");

        var decision = InterpretOutputOverride?.Invoke(workerRole, workerOutput)
            ?? GetDefaultInterpretation(workerRole);

        return Task.FromResult(decision);
    }

    public Task<OrchestratorDecision> DecideNextStepAsync(
        string currentPhase, string context, CancellationToken ct = default)
    {
        Decisions.Add($"{currentPhase}: {context[..Math.Min(100, context.Length)]}");

        var decision = DecideNextStepOverride?.Invoke(currentPhase, context)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.SpawnCoder,
                Reason = "Default: proceed",
            };

        return Task.FromResult(decision);
    }

    public Task InformAsync(string information, CancellationToken ct = default)
    {
        Informations.Add(information);
        return Task.CompletedTask;
    }

    private static string GetDefaultPrompt(string role, string goal, int iteration, string branchName) =>
        role.ToLowerInvariant() switch
        {
            "coder" => $"You are working on this goal: {goal}\nIteration {iteration}, branch coder/{branchName}.\nWrite code and commit. Do NOT run git push.",
            "reviewer" => $"You are reviewing code changes for this goal: {goal}\nIteration {iteration}, branch coder/{branchName}.\nProduce a REVIEW_REPORT. Do NOT modify code.",
            "tester" => $"You are testing code for this goal: {goal}\nIteration {iteration}, branch coder/{branchName}.\nRun tests, produce TEST_REPORT. Do NOT run git push.",
            _ => $"Work on: {goal}",
        };

    private static OrchestratorDecision GetDefaultInterpretation(string role) =>
        role.ToLowerInvariant() switch
        {
            // Default: don't override verdict or metrics — let string parsing stand.
            // Tests can use InterpretOutputOverride to provide specific brain responses.
            "tester" => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
            },
            "reviewer" => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
            },
            _ => new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
            },
        };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
