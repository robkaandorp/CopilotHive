using System.Collections;
using System.Collections.Concurrent;

namespace CopilotHive.Services;

/// <summary>
/// Stores per-role agent session JSON blobs with case-insensitive keys.
/// Encapsulates the concurrent dictionary previously exposed directly on <see cref="GoalPipeline"/>.
/// </summary>
public sealed class RoleSessionStore : IEnumerable<KeyValuePair<string, string>>
{
    private readonly ConcurrentDictionary<string, string> _sessions
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the session JSON for the given role, or <c>null</c> if not stored.</summary>
    public string? Get(string roleName) => _sessions.GetValueOrDefault(roleName);

    /// <summary>Stores (or overwrites) the session JSON for the given role.</summary>
    public void Set(string roleName, string sessionJson) => _sessions[roleName] = sessionJson;

    /// <summary>Number of role sessions currently stored.</summary>
    public int Count => _sessions.Count;

    /// <summary>Returns all stored role sessions for persistence enumeration.</summary>
    public IEnumerable<KeyValuePair<string, string>> GetAll() => _sessions;

    /// <summary>Bulk-loads role sessions (e.g. from a persisted snapshot).</summary>
    public void Load(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var kv in entries) _sessions[kv.Key] = kv.Value;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _sessions.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
