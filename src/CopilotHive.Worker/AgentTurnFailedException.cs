namespace CopilotHive.Worker;

/// <summary>
/// Thrown by <see cref="SharpCoderRunner"/> when an agent turn ends with a SharpCoder
/// <c>AgentResult.Status</c> of <c>"Error"</c> — the shape SharpCoder reports for most provider
/// failures instead of rethrowing (only <c>OperationCanceledException</c>,
/// <c>HttpRequestException</c> and <c>ObjectDisposedException</c> propagate as real exceptions).
/// <para>
/// FAIL-FAST, NOT OUTPUT. Returning the failed turn's <c>AgentResult.Message</c> as if it were the
/// agent's normal output made <c>TaskExecutor</c> treat a provider failure as a completed phase:
/// the provider text became the phase narrative and the task was reported as
/// <c>TaskOutcome.Completed</c>. Throwing instead routes the turn through <c>TaskExecutor</c>'s
/// existing generic catch, which reports <c>TaskOutcome.Failed</c> with a <c>FAIL</c> verdict and
/// never saves the session.
/// </para>
/// <para>
/// CARRIES NO PROVIDER TEXT AND HAS NO CUSTOM PROPERTIES. Worker results travel to the
/// orchestrator, and provider failure text can echo a provisioned secret (see the
/// <see cref="SafeExceptionLog"/> rationale — a GitHub token or an Ollama API key handed to the
/// worker by the orchestrator). The message is a single fixed sentence, so the classification
/// <c>TaskExecutor</c> renders through <see cref="SafeExceptionLog.Describe"/> — the exception TYPE
/// NAME, e.g. <c>AgentTurnFailedException</c> — is the only thing that ever leaves the worker.
/// </para>
/// </summary>
internal sealed class AgentTurnFailedException : Exception
{
    /// <summary>
    /// THE single fixed message. Every instance carries exactly this text and nothing else — never
    /// the provider's failure text, and never an inner exception that could carry it.
    /// </summary>
    internal const string StatusErrorMessage = "The agent turn ended with SharpCoder status 'Error'.";

    /// <summary>
    /// Initializes a new <see cref="AgentTurnFailedException"/> carrying
    /// <see cref="StatusErrorMessage"/>.
    /// </summary>
    public AgentTurnFailedException()
        : base(StatusErrorMessage)
    {
    }
}
