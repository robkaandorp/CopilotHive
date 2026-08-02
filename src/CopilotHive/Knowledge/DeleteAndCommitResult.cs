namespace CopilotHive.Knowledge;

/// <summary>
/// Result of a serialized batch document deletion and persistence operation.
/// Persistence is not transactional — partial disk/git state may remain on failure,
/// and tracking is retained for idempotent retry.
/// </summary>
/// <param name="DeletedCount">Number of documents removed from the in-memory graph.</param>
/// <param name="Persisted">Whether persistence completed. True for in-memory-only mode.</param>
/// <param name="PersistError">Exception causing persistence failure, or null on success.</param>
public sealed record DeleteAndCommitResult(int DeletedCount, bool Persisted, Exception? PersistError);
