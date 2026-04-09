using CopilotHive.Orchestration;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Handles clarification recording and escalation routing on behalf of <see cref="GoalDispatcher"/>.
/// Encapsulates three tiers of clarification resolution: Brain direct answer,
/// Composer auto-answer, and human escalation with timeout fallback.
/// </summary>
internal sealed class ClarificationHandler
{
    private readonly IDistributedBrain? _brain;
    private readonly IClarificationRouter? _clarificationRouter;
    private readonly ClarificationQueueService? _clarificationQueue;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new <see cref="ClarificationHandler"/>.
    /// </summary>
    /// <param name="brain">Optional LLM brain for intelligent Q&amp;A routing.</param>
    /// <param name="clarificationRouter">Optional Composer router for auto-answer.</param>
    /// <param name="clarificationQueue">Optional queue for human escalation.</param>
    /// <param name="logger">Logger instance.</param>
    public ClarificationHandler(
        IDistributedBrain? brain,
        IClarificationRouter? clarificationRouter,
        ClarificationQueueService? clarificationQueue,
        ILogger logger)
    {
        _brain = brain;
        _clarificationRouter = clarificationRouter;
        _clarificationQueue = clarificationQueue;
        _logger = logger;
    }

    /// <summary>
    /// Handles a question from a worker tool call by routing it to the Brain via
    /// <see cref="IDistributedBrain.AskQuestionAsync"/>. If the Brain returns an escalation
    /// response, the question is forwarded to the Composer LLM for auto-answer. If the
    /// Composer cannot answer, the question is queued for human resolution with a 5-minute
    /// timeout. Returns the resolved answer as a string.
    /// </summary>
    public async Task<string> AskBrainAsync(GoalPipeline pipeline, string question, CancellationToken ct)
    {
        if (_brain is null)
            return "Brain is not available. Please proceed with your best judgment.";

        _logger.LogInformation("Worker asks Brain: {Question}", question);
        var brainResponse = await _brain.AskQuestionAsync(
            pipeline.GoalId,
            pipeline.Iteration,
            pipeline.Phase.ToString(),
            pipeline.Phase.ToWorkerRole().ToRoleName(),
            question,
            ct);

        if (!brainResponse.IsEscalation)
        {
            var answer = brainResponse.Text ?? string.Empty;
            _logger.LogInformation("Brain answers: {Answer}", answer[..Math.Min(answer.Length, 200)]);
            RecordClarification(pipeline, question, answer, "brain");
            return answer;
        }

        _logger.LogInformation("Brain escalated question for goal {GoalId} — routing to Composer", pipeline.GoalId);
        return await ResolveClarificationAsync(pipeline, question, null, ct);
    }

    /// <summary>
    /// Records a clarification Q&amp;A into the pipeline and emits a structured log entry.
    /// </summary>
    public void RecordClarification(GoalPipeline pipeline, string question, string answer, string answeredBy)
    {
        // Planning, Merging, Done, Failed phases have no worker role — use "brain" as the role
        var workerRole = pipeline.Phase is GoalPhase.Planning or GoalPhase.Merging or GoalPhase.Done or GoalPhase.Failed
            ? "brain"
            : pipeline.Phase.ToWorkerRole().ToRoleName();

        var entry = new ClarificationEntry(
            Timestamp: DateTime.UtcNow,
            GoalId: pipeline.GoalId,
            Iteration: pipeline.Iteration,
            Phase: pipeline.Phase.ToString(),
            WorkerRole: workerRole,
            Question: question,
            Answer: answer,
            AnsweredBy: answeredBy)
        {
            Occurrence = pipeline.CurrentPhaseEntry?.Occurrence ?? 1,
        };

        pipeline.Clarifications.Add(entry);

        _logger.LogInformation(
            "Clarification recorded for goal {GoalId} iteration {Iteration} phase {Phase}: Q={Question} | AnsweredBy={AnsweredBy}",
            entry.GoalId, entry.Iteration, entry.Phase,
            question.Length > 100 ? question[..100] + "..." : question,
            answeredBy);
    }

    /// <summary>
    /// Routes a Brain escalation through the clarification pipeline and returns
    /// the resolved answer (from Composer, human, or timeout fallback).
    /// </summary>
    public async Task<string> RouteEscalationAsync(
        GoalPipeline pipeline, string question, string reason, CancellationToken ct)
    {
        _logger.LogInformation(
            "Brain escalated for goal {GoalId} — routing clarification to Composer. Reason: {Reason}",
            pipeline.GoalId, reason);

        var additionalContext = $"Goal description: {pipeline.Description}. Current phase: {pipeline.Phase}. Reason for escalation: {reason}";
        return await ResolveClarificationAsync(pipeline, question, additionalContext, ct);
    }

    /// <summary>
    /// Resolves a clarification request through the escalation pipeline:
    /// 1. Try Composer auto-answer (30s timeout)
    /// 2. Fall back to human answer (5min timeout)
    /// Returns the resolved answer, or <see cref="ClarificationQueueService.TimeoutFallbackMessage"/>.
    /// </summary>
    private async Task<string> ResolveClarificationAsync(
        GoalPipeline pipeline,
        string question,
        string? additionalContext,
        CancellationToken ct)
    {
        if (_clarificationRouter is null || _clarificationQueue is null)
        {
            _logger.LogWarning(
                "No clarification router or queue available — returning fallback for goal {GoalId}",
                pipeline.GoalId);
            RecordClarification(pipeline, question, ClarificationQueueService.TimeoutFallbackMessage, "timeout");
            return ClarificationQueueService.TimeoutFallbackMessage;
        }

        // Planning, Merging, Done, Failed phases have no worker role — use "brain"
        var workerRole = pipeline.Phase is GoalPhase.Planning or GoalPhase.Merging or GoalPhase.Done or GoalPhase.Failed
            ? "brain"
            : pipeline.Phase.ToWorkerRole().ToRoleName();

        var request = new ClarificationRequest
        {
            GoalId = pipeline.GoalId,
            WorkerRole = workerRole,
            Question = question,
        };

        var tcs = _clarificationQueue.Enqueue(request);
        pipeline.IsWaitingForClarification = true;

        // Step 1: Try Composer auto-answer
        var composerAnswer = await _clarificationRouter.TryAutoAnswerAsync(
            pipeline.GoalId,
            question,
            additionalContext ?? $"Goal description: {pipeline.Description}. Current phase: {pipeline.Phase}.",
            _clarificationQueue,
            request,
            ct);

        if (composerAnswer is not null)
        {
            _clarificationQueue.SubmitAnswer(request.Id, composerAnswer, "composer");
            pipeline.IsWaitingForClarification = false;
            RecordClarification(pipeline, question, composerAnswer, "composer");
            return composerAnswer;
        }

        // Step 2: Composer escalated to human — wait with proper cancellation handling
        _logger.LogInformation(
            "Clarification for goal {GoalId} escalated to human. Waiting up to {Timeout} for answer.",
            pipeline.GoalId, ClarificationQueueService.HumanTimeout);

        using var timeoutCts = new CancellationTokenSource(ClarificationQueueService.HumanTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

        try
        {
            var humanAnswer = await tcs.Task.WaitAsync(linkedCts.Token);
            _logger.LogInformation("Human answered clarification for goal {GoalId}", pipeline.GoalId);
            pipeline.IsWaitingForClarification = false;
            RecordClarification(pipeline, question, humanAnswer, "human");
            return humanAnswer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Clarification timeout (not caller cancellation) — return fallback
            _logger.LogWarning(
                "Clarification timed out for goal {GoalId} — returning fallback", pipeline.GoalId);
            _clarificationQueue.MarkTimedOut(request.Id);
            pipeline.IsWaitingForClarification = false;
            RecordClarification(pipeline, question, ClarificationQueueService.TimeoutFallbackMessage, "timeout");
            return ClarificationQueueService.TimeoutFallbackMessage;
        }
        // OperationCanceledException where ct.IsCancellationRequested propagates naturally
    }

    /// <summary>
    /// Calls <see cref="IDistributedBrain.PlanIterationAsync"/> and handles any escalation
    /// by routing to the clarification pipeline. On successful clarification, retries planning
    /// with the answer as additional context. On timeout, returns the default plan.
    /// </summary>
    public async Task<IterationPlan> ResolvePlanAsync(
        GoalPipeline pipeline, string? additionalContext, CancellationToken ct)
    {
        if (_brain is null)
            return IterationPlan.Default();

        var result = await _brain.PlanIterationAsync(pipeline, additionalContext, ct);

        if (!result.IsEscalation)
            return result.Plan ?? IterationPlan.Default();

        // Brain needs clarification before planning
        var answer = await RouteEscalationAsync(
            pipeline,
            result.EscalationQuestion ?? "Brain requested clarification during planning",
            result.EscalationReason ?? string.Empty,
            ct);

        if (answer == ClarificationQueueService.TimeoutFallbackMessage)
        {
            _logger.LogWarning(
                "Brain planning escalation timed out for goal {GoalId} — using default plan", pipeline.GoalId);
            return IterationPlan.Default();
        }

        // Retry planning with the answer as additional context
        _logger.LogInformation(
            "Retrying Brain planning for goal {GoalId} with clarification answer", pipeline.GoalId);
        var retryContext = additionalContext is not null
            ? $"{additionalContext}\n\n=== Clarification answer ===\n{answer}\n=== End clarification answer ==="
            : $"=== Clarification answer ===\n{answer}\n=== End clarification answer ===";

        var retryResult = await _brain.PlanIterationAsync(pipeline, retryContext, ct);
        return retryResult.Plan ?? IterationPlan.Default();
    }

    /// <summary>
    /// Calls <see cref="IDistributedBrain.CraftPromptAsync"/> and handles any escalation
    /// by routing to the clarification pipeline. On successful clarification, retries prompt
    /// crafting with the answer as additional context. On timeout, returns a fallback prompt.
    /// </summary>
    public async Task<string> ResolvePromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext, CancellationToken ct)
    {
        if (_brain is null)
            return $"Work on: {pipeline.Description} (phase: {phase})";

        var result = await _brain.CraftPromptAsync(pipeline, phase, additionalContext, ct);

        if (!result.IsEscalation)
            return result.Prompt ?? $"Work on: {pipeline.Description}";

        // Brain needs clarification before crafting the prompt
        var answer = await RouteEscalationAsync(
            pipeline,
            result.EscalationQuestion ?? "Brain requested clarification during prompt crafting",
            result.EscalationReason ?? string.Empty,
            ct);

        if (answer == ClarificationQueueService.TimeoutFallbackMessage)
        {
            _logger.LogWarning(
                "Brain prompt escalation timed out for goal {GoalId} phase {Phase} — using fallback prompt",
                pipeline.GoalId, phase);
            return phase == GoalPhase.Review
                ? DistributedBrain.BuildReviewFallbackPrompt(pipeline, additionalContext)
                : $"Work on: {pipeline.Description}";
        }

        // Retry prompt crafting with the answer as additional context
        _logger.LogInformation(
            "Retrying Brain prompt crafting for goal {GoalId} phase {Phase} with clarification answer",
            pipeline.GoalId, phase);
        var retryContext = additionalContext is not null
            ? $"{additionalContext}\n\n=== Clarification answer ===\n{answer}\n=== End clarification answer ==="
            : $"=== Clarification answer ===\n{answer}\n=== End clarification answer ===";

        var retryResult = await _brain.CraftPromptAsync(pipeline, phase, retryContext, ct);
        return retryResult.Prompt ?? $"Work on: {pipeline.Description}";
    }
}
