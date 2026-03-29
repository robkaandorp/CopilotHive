namespace CopilotHive.Worker;

/// <summary>
/// Abstracts session persistence operations so that <see cref="TaskExecutor"/>
/// can load and save agent sessions without depending directly on <see cref="WorkerService"/>.
/// </summary>
public interface ISessionClient
{
    /// <summary>
    /// Retrieves the persisted session JSON for the given session identifier.
    /// </summary>
    /// <param name="sessionId">The session identifier, typically in the format "goalId:roleName".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session JSON string, or <c>null</c> if no session exists for that identifier.</returns>
    Task<string?> GetSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Persists the session JSON under the given session identifier.
    /// </summary>
    /// <param name="sessionId">The session identifier, typically in the format "goalId:roleName".</param>
    /// <param name="sessionJson">The serialized session JSON to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveSessionAsync(string sessionId, string sessionJson, CancellationToken ct);
}
