using CopilotHive.Orchestration;

namespace CopilotHive.Services;

/// <summary>
/// Routes clarification questions from workers through the Composer LLM for auto-answer.
/// This interface breaks the circular dependency between <see cref="GoalDispatcher"/>
/// and <see cref="Composer"/> — the dispatcher depends on this interface rather than
/// on the Composer directly.
/// </summary>
public interface IClarificationRouter
{
    /// <summary>
    /// Attempts to auto-answer a clarification question using the Composer LLM.
    /// If the Composer is confident, returns the answer string directly.
    /// If the Composer cannot answer (returns <c>ESCALATE_TO_HUMAN</c>, times out, or errors),
    /// escalates the request to the human queue and returns <c>null</c>.
    /// </summary>
    /// <param name="goalId">The goal that triggered the clarification.</param>
    /// <param name="question">The worker's question text.</param>
    /// <param name="context">Additional context about the goal and current phase.</param>
    /// <param name="clarificationQueue">The queue service for managing clarification state.</param>
    /// <param name="request">The clarification request to escalate if needed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The answer string if the Composer is confident; <c>null</c> if escalated to human.</returns>
    Task<string?> TryAutoAnswerAsync(
        string goalId,
        string question,
        string context,
        ClarificationQueueService clarificationQueue,
        ClarificationRequest request,
        CancellationToken ct = default);
}
