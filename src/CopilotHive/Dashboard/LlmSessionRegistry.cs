using System.Collections.Concurrent;

namespace CopilotHive.Dashboard;

/// <summary>Thread-safe registry of active LLM sessions for the dashboard.</summary>
public sealed class LlmSessionRegistry
{
    internal readonly ConcurrentDictionary<string, LlmSessionInfo> Sessions = new();

    /// <summary>Registers a new session or replaces an existing one with the same <see cref="LlmSessionInfo.SessionId"/>.</summary>
    /// <param name="session">The session information to register.</param>
    public void RegisterOrUpdate(LlmSessionInfo session) => Sessions[session.SessionId] = session;

    /// <summary>Removes the session with the specified identifier.</summary>
    /// <param name="sessionId">The session identifier to remove.</param>
    /// <returns><c>true</c> if the session was removed; otherwise <c>false</c>.</returns>
    public bool Unregister(string sessionId) => Sessions.TryRemove(sessionId, out _);

    /// <summary>Returns a snapshot of all registered sessions.</summary>
    /// <returns>A list containing every registered session.</returns>
    public List<LlmSessionInfo> GetAll() => Sessions.Values.ToList();

    /// <summary>Removes sessions with no activity for longer than the specified timeout.</summary>
    /// <param name="maxAge">Maximum age of the last activity before a session is considered stale.</param>
    /// <remarks>
    /// Uses conditional removal to avoid deleting a session that was concurrently updated
    /// between observation and removal. KeyValuePair comparison ensures we only remove
    /// the stale entry we observed, not a newer entry with the same key.
    /// </remarks>
    public void CleanupStale(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        foreach (var kvp in Sessions)
        {
            if (kvp.Value.LastActivity < cutoff)
            {
                // Conditional removal: only remove if the entry hasn't been updated since we read it.
                ((ICollection<KeyValuePair<string, LlmSessionInfo>>)Sessions).Remove(kvp);
            }
        }
    }
}
