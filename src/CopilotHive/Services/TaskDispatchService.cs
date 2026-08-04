using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Knowledge;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// Handles per-role task dispatching and repository resolution for goal pipelines.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
internal sealed class TaskDispatchService
{
    private readonly TaskQueue _taskQueue;
    private readonly IWorkerGateway _workerGateway;
    private readonly TaskBuilder _taskBuilder;
    private readonly HiveConfigFile? _config;
    private readonly ILogger<TaskDispatchService> _logger;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly DispatcherMaintenance _maintenance;

    public TaskDispatchService(
        TaskQueue taskQueue,
        IWorkerGateway workerGateway,
        TaskBuilder taskBuilder,
        HiveConfigFile? config,
        ILogger<TaskDispatchService> logger,
        GoalPipelineManager pipelineManager,
        GoalLifecycleService lifecycleService,
        DispatcherMaintenance maintenance)
    {
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _taskBuilder = taskBuilder;
        _config = config;
        _logger = logger;
        _pipelineManager = pipelineManager;
        _lifecycleService = lifecycleService;
        _maintenance = maintenance;
    }

    internal async Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
    {
        prompt ??= $"Work on: {pipeline.Description}";

        // Log the prompt being sent to the worker
        var promptPreview = prompt.Length > 1500
            ? prompt[..1500] + $"... ({prompt.Length} chars total)"
            : prompt;
        _logger.LogDebug("Prompt for {Role} (goal={GoalId}):\n{Prompt}",
            role, pipeline.GoalId, promptPreview);

        var branchAction = pipeline.CoderBranch is null ? BranchAction.Create : BranchAction.Checkout;

        List<TargetRepository> repositories;
        try
        {
            repositories = ResolveRepositories(pipeline.Goal);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Repository configuration error for goal {GoalId}", pipeline.GoalId);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, ex.Message, ct);
            return;
        }

        // Resolve per-role model from config; upgrade to premium when the Brain requested it for this phase
        var roleName = role.ToRoleName();
        var model = _config?.GetModelForRole(roleName);
        var currentPhase = pipeline.StateMachine.Phase;
        var phaseTier = pipeline.Plan?.PhaseTiers.GetValueOrDefault(currentPhase, ModelTier.Default) ?? ModelTier.Default;
        if (phaseTier == ModelTier.Premium && _config is not null)
        {
            var premiumModel = _config.GetPremiumModelForRole(roleName);
            if (!string.IsNullOrWhiteSpace(premiumModel))
                model = premiumModel;
        }

        // Resolve the effective reasoning effort for this task.
        //
        // Precedence:
        //   1. WorkerConfig.PremiumReasoningEffort — when the phase requested the premium tier AND
        //      a premium model is actually configured for this role.
        //   2. WorkerConfig.ReasoningEffort — otherwise.
        //
        // The parsed enum is transported on the WorkTask and is authoritative for the worker.
        // The model name always stays plain — reasoning is never baked into it.
        ReasoningEffort? effectiveReasoning = null;
        if (_config is not null && model is not null)
        {
            var hasPremiumModel = !string.IsNullOrWhiteSpace(_config.GetPremiumModelForRole(roleName));
            _config.Workers.TryGetValue(roleName.ToLowerInvariant(), out var workerConfig);

            var effortString = phaseTier == ModelTier.Premium && hasPremiumModel
                ? workerConfig?.PremiumReasoningEffort
                : workerConfig?.ReasoningEffort;

            // Startup validates reasoning efforts, but dynamic config reloads deliberately do
            // not re-validate. An invalid value must degrade to "unset" rather than fail the
            // dispatch (and the goal) with an unhandled ArgumentException.
            try
            {
                effectiveReasoning = ReasoningEffortConverter.Parse(effortString);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning(
                    "Invalid reasoning_effort '{Effort}' configured for role {Role}; dispatching with reasoning effort unset.",
                    effortString, roleName);
                effectiveReasoning = null;
            }
        }

        _logger.LogDebug("Model for {Role}: {Model} (tier={Tier}, configLoaded={ConfigLoaded})",
            roleName, model ?? "(null)", phaseTier, _config is not null);

        // Resolve context window: per-role override > global worker default > constant fallback
        var maxContextTokens = _config?.GetContextWindowForRole(roleName) ?? Constants.DefaultBrainContextWindow;

        // Populate sub-agent model catalog from config (no reasoning suffix — sub-agents inherit parent reasoning)
        var subAgentModels = new List<SubAgentModelDto>();
        var subAgentCatalog = _config?.GetSubAgentModels() ?? [];
        foreach (var entry in subAgentCatalog)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;
            var autoDescription = entry.ContextWindow is int cw && cw > 0
                ? $"Configured model, {cw / 1000}K context"
                : "Configured model";
            subAgentModels.Add(new SubAgentModelDto
            {
                Id = entry.Name,
                ContextWindow = entry.ContextWindow,
                Description = !string.IsNullOrWhiteSpace(entry.Description) ? entry.Description : autoDescription,
                SupportsVision = entry.SupportsVision ?? false,
            });
        }

        var task = _taskBuilder.Build(
            goalId: pipeline.GoalId,
            goalDescription: pipeline.Description,
            role: role,
            iteration: pipeline.Iteration,
            repositories: repositories,
            prompt: prompt,
            branchAction: branchAction,
            model: model,
            maxContextTokens: maxContextTokens,
            subAgentModels: subAgentModels,
            reasoningEffort: effectiveReasoning);

        // Improver operates read-only: it can see the feature branch but must not push.
        // Downgrade the action to Unspecified so the worker runtime skips push operations.
        if (role == WorkerRole.Improver && task.BranchInfo is not null)
        {
            task.BranchInfo.Action = BranchAction.Unspecified;
        }

        // Propagate the iteration start SHA to the worker via metadata so reviewers can
        // compute an iteration-scoped diff alongside the cumulative branch diff.
        if (pipeline.IterationStartSha is not null)
            task.Metadata["iteration_start_sha"] = pipeline.IterationStartSha;

        // Propagate the tester's structured report to the reviewer so it can be retrieved via get_test_report.
        if (role == WorkerRole.Reviewer)
        {
            var testerEntry = pipeline.PhaseLog
                .LastOrDefault(e => e.Name == GoalPhase.Testing && e.Iteration == pipeline.Iteration && e.WorkerOutput is not null);
            if (testerEntry?.WorkerOutput is not null)
            {
                task.Metadata["tester_report"] = testerEntry.WorkerOutput;
            }
        }

        // Propagate compaction model to the worker so it creates a separate IChatClient for context compaction.
        var compactionModel = _config?.Models?.CompactionModel;
        if (!string.IsNullOrEmpty(compactionModel))
        {
            var compactionCtx = _config?.TryGetContextWindowForModel(compactionModel);

            task.Metadata["compaction_model"] = compactionModel;
            if (compactionCtx is int ctx && ctx > 0)
                task.Metadata["compaction_max_tokens"] = ctx.ToString();
        }

        pipeline.SetActiveTask(task.TaskId, task.BranchInfo?.FeatureBranch);
        _pipelineManager.RegisterTask(task.TaskId, pipeline.GoalId);

        _taskQueue.Enqueue(task);
        _logger.LogInformation("Dispatched {Role} task {TaskId} for goal {GoalId} (branch={Branch})",
            role, task.TaskId, pipeline.GoalId, task.BranchInfo?.FeatureBranch);

        // Try to push directly to an idle worker
        var idleWorker = _workerGateway.GetIdleWorker();
        if (idleWorker is not null)
        {
            var queuedTask = _taskQueue.TryDequeue(role);
            queuedTask ??= _taskQueue.TryDequeueAny();

            if (queuedTask is not null)
            {
                idleWorker.Role = queuedTask.Role;
                var taskRoleName = queuedTask.Role.ToRoleName();
                _logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
                    idleWorker.Id, taskRoleName, queuedTask.TaskId);
                await _maintenance.SendAgentsMdToWorkerAsync(idleWorker, queuedTask.Role, ct);

                _taskQueue.Activate(queuedTask, idleWorker.Id);
                _workerGateway.MarkBusy(idleWorker.Id, queuedTask.TaskId);
                idleWorker.CurrentModel = queuedTask.Model;
                await _workerGateway.SendTaskAsync(idleWorker.Id, queuedTask, ct);
                _logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleWorker.Id);
            }
        }
    }

    /// <summary>
    /// Resolves the list of <see cref="TargetRepository"/> instances for the given goal by looking
    /// up each repository name in the hive configuration.
    /// </summary>
    /// <param name="goal">The goal whose <see cref="Goal.RepositoryNames"/> are to be resolved.</param>
    /// <returns>A list of resolved <see cref="TargetRepository"/> objects with injected credentials.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any repository name referenced by the goal is not defined in hive-config.yaml.
    /// </exception>
    internal List<TargetRepository> ResolveRepositories(Goal goal)
    {
        var repos = new List<TargetRepository>();

        foreach (var repoName in goal.RepositoryNames)
        {
            var repoConfig = _config?.Repositories.FirstOrDefault(
                r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

            if (repoConfig is not null)
            {
                var url = PipelineHelpers.InjectTokenIntoUrl(repoConfig.Url);
                repos.Add(new TargetRepository
                {
                    Name = repoConfig.Name,
                    Url = url,
                    DefaultBranch = repoConfig.DefaultBranch,
                });
            }
            else
            {
                throw new InvalidOperationException(
                    $"Goal '{goal.Id}' references repository '{repoName}' which is not defined in hive-config.yaml. Add it to the repositories section or remove it from the goal.");
            }
        }

        return repos;
    }
}
