using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Shared no-op <see cref="IDistributedBrain"/> for test hosts.
/// <para>
/// CopilotHive requires a Brain: <c>Program.cs</c> registers <see cref="IDistributedBrain"/>
/// UNCONDITIONALLY and resolves it eagerly right after <c>builder.Build()</c>, so a test host
/// booted without a real config repo (the null <c>orchestrator.model</c> fallback) must supply a
/// Brain explicitly. This stub is that Brain: it returns default plans and a simple prompt, so
/// dispatch completes with no LLM involvement, and everything else is a completed no-op.
/// </para>
/// <para>
/// Same behaviour as the file-scoped <c>StubBrain</c> in
/// <c>Services/GoalReadyNotifierTests.cs</c> — deliberately duplicated here (that fake stays
/// untouched) because this one is shared by every host-booting test factory.
/// </para>
/// </summary>
internal sealed class NoOpDistributedBrain : IDistributedBrain
{
    /// <summary>Connects successfully without any provider I/O.</summary>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Accepts the model update without recreating any chat client.</summary>
    public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>Returns the default iteration plan.</summary>
    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        => Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    /// <summary>Returns a simple, phase-named prompt for the pipeline.</summary>
    public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        => Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    /// <summary>Never produces a commit message (the caller falls back to its own).</summary>
    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>No-op: no Brain clone is created.</summary>
    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>No-op: no session instructions are injected.</summary>
    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: no system note is recorded.</summary>
    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Answers every worker question directly, with no escalation.</summary>
    public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
        => Task.FromResult(BrainResponse.Answer("proceed"));

    /// <summary>No-op: there is no session to reset.</summary>
    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: there is no session to fork.</summary>
    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: there is no session to delete.</summary>
    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>No-op: there is no session to load.</summary>
    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Never reports a persisted goal session (there are none).</summary>
    public bool GoalSessionExists(string goalId) => false;

    /// <summary>Returns a completed-goal summary without touching the master session.</summary>
    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    /// <summary>Never reports Brain stats (nothing is connected).</summary>
    public BrainStats? GetStats() => null;
}

/// <summary>
/// Test-host wiring for the mandatory Brain: replaces any registered
/// <see cref="IDistributedBrain"/> with the caller's instance.
/// </summary>
internal static class DistributedBrainTestExtensions
{
    /// <summary>
    /// Removes EVERY existing <see cref="IDistributedBrain"/> descriptor and adds
    /// <paramref name="brain"/> as a singleton.
    /// </summary>
    /// <remarks>
    /// <c>WithWebHostBuilder</c>/<c>ConfigureServices</c> callbacks run AFTER <c>Program.cs</c>'s
    /// own registrations, so simply adding a second descriptor would leave the production
    /// registration in the collection (and let it win on a later <c>GetServices</c> enumeration).
    /// Removing the existing descriptors is what makes the stub the resolved Brain — and it also
    /// means the production factory never runs, so the startup contract
    /// (<c>Program.RequireBrainModel</c>) and its "Brain enabled" line are never triggered in a
    /// host that supplies its own Brain.
    /// </remarks>
    /// <param name="services">The test host's service collection.</param>
    /// <param name="brain">The Brain instance the test host must resolve.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection ReplaceDistributedBrain(this IServiceCollection services, IDistributedBrain brain)
    {
        var existing = services.Where(d => d.ServiceType == typeof(IDistributedBrain)).ToList();
        foreach (var descriptor in existing)
            services.Remove(descriptor);

        services.AddSingleton(brain);
        return services;
    }
}
