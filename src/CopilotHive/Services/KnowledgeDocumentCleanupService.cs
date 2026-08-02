using CopilotHive.Goals;
using CopilotHive.Knowledge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Deletes goal-scoped <c>progress-*</c> and <c>review-*</c> knowledge documents from the
/// knowledge graph. Used when goals are cleaned up individually or in batches, and during
/// startup to sweep documents whose owning goal no longer exists or whose goal belongs to
/// a release that has been published.
/// </summary>
public sealed class KnowledgeDocumentCleanupService
{
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly ILogger<KnowledgeDocumentCleanupService> _logger;

    /// <summary>
    /// Initialises a new <see cref="KnowledgeDocumentCleanupService"/>.
    /// </summary>
    /// <param name="knowledgeGraph">Optional knowledge graph. When null, all cleanup operations return 0.</param>
    /// <param name="logger">Logger used for warnings. Must not be null.</param>
    public KnowledgeDocumentCleanupService(KnowledgeGraph? knowledgeGraph, ILogger<KnowledgeDocumentCleanupService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _knowledgeGraph = knowledgeGraph;
    }

    /// <summary>
    /// Deletes the <c>progress-{goalId}</c> and <c>review-{goalId}</c> documents for a single goal
    /// from the knowledge graph and persists the deletions.
    /// </summary>
    /// <param name="goalId">ID of the goal whose progress/review documents should be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of documents deleted.</returns>
    public async Task<int> CleanupGoalDocumentsAsync(string goalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_knowledgeGraph is null)
            return 0;
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Goal ID cannot be null or whitespace.", nameof(goalId));

        var documentIds = new[] { $"progress-{goalId}", $"review-{goalId}" };
        var result = await _knowledgeGraph.DeleteDocumentsAndCommitAsync(
            documentIds, _knowledgeGraph.ConfigRepoPath,
            $"Cleanup progress/review docs for goal '{goalId}'", ct);

        if (!result.Persisted)
            _logger.LogWarning(
                "Failed to persist cleanup of progress/review docs for goal '{GoalId}': {PersistError}",
                goalId, result.PersistError);

        return result.DeletedCount;
    }

    /// <summary>
    /// Deletes the progress/review documents for a batch of goals in a single persisted operation.
    /// Null or whitespace elements of <paramref name="goalIds"/> are skipped with a warning, and
    /// duplicate goal IDs (case-insensitive) are collapsed into one cleanup.
    /// </summary>
    /// <param name="goalIds">Goal IDs whose documents should be removed. Null or whitespace elements are skipped.</param>
    /// <param name="commitContext">Commit message used verbatim when persisting the deletions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of documents deleted, or 0 when enumeration fails.</returns>
    public async Task<int> CleanupGoalsDocumentsAsync(IEnumerable<string> goalIds, string commitContext, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_knowledgeGraph is null)
            return 0;
        if (goalIds is null)
            throw new ArgumentNullException(nameof(goalIds));
        if (string.IsNullOrWhiteSpace(commitContext))
            throw new ArgumentException("Commit context cannot be null or whitespace.", nameof(commitContext));

        List<string> validGoalIds;
        try
        {
            validGoalIds = [];
            foreach (var goalId in goalIds)
            {
                if (string.IsNullOrWhiteSpace(goalId))
                {
                    _logger.LogWarning("Skipping null or whitespace goal ID in knowledge document cleanup");
                    continue;
                }

                validGoalIds.Add(goalId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate goal IDs for knowledge document cleanup");
            return 0;
        }

        var documentIds = new List<string>();
        foreach (var goalId in validGoalIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            documentIds.Add($"progress-{goalId}");
            documentIds.Add($"review-{goalId}");
        }

        var result = await _knowledgeGraph.DeleteDocumentsAndCommitAsync(
            documentIds, _knowledgeGraph.ConfigRepoPath, commitContext, ct);

        if (!result.Persisted)
            _logger.LogWarning("Failed to persist cleanup of goal documents: {PersistError}", result.PersistError);

        return result.DeletedCount;
    }

    /// <summary>
    /// Sweeps the knowledge graph for stale <c>progress-*</c> and <c>review-*</c> documents.
    /// A document is a candidate for deletion when its (topic, id-prefix) pair matches
    /// (<c>progress</c>, <c>progress-</c>) or (<c>review</c>, <c>review-</c>) AND its goal ID is
    /// either absent from <paramref name="allGoals"/> (orphaned) or belongs to a release whose
    /// status is <see cref="ReleaseStatus.Released"/>. The topic is matched case-insensitively
    /// but the ID prefix is matched with <see cref="StringComparison.Ordinal"/>, so a
    /// mixed-case id such as <c>Progress-orphan</c> is never a candidate. Documents with an
    /// empty goal ID (e.g. a bare <c>progress-</c> id) are always kept.
    /// Evaluation of each document is best-effort: a non-cancellation failure on one document
    /// is logged and skipped rather than aborting the whole sweep.
    /// </summary>
    /// <param name="allGoals">All known goals. A document whose goal is absent from this list is orphaned.</param>
    /// <param name="allReleases">All known releases. Goals whose release is <see cref="ReleaseStatus.Released"/> have their documents removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of documents deleted.</returns>
    public async Task<int> SweepOrphanedDocumentsAsync(IReadOnlyList<Goal> allGoals, IReadOnlyList<Release> allReleases, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_knowledgeGraph is null)
            return 0;
        if (allGoals is null)
            throw new ArgumentNullException(nameof(allGoals));
        if (allReleases is null)
            throw new ArgumentNullException(nameof(allReleases));

        var liveGoalIds = new HashSet<string>(
            allGoals.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);

        var releasedReleaseIds = new HashSet<string>(
            allReleases.Where(r => r.Status == ReleaseStatus.Released).Select(r => r.Id),
            StringComparer.OrdinalIgnoreCase);

        var releasedGoalIds = new HashSet<string>(
            allGoals
                .Where(g => g.ReleaseId is not null && releasedReleaseIds.Contains(g.ReleaseId))
                .Select(g => g.Id),
            StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>();
        foreach (var doc in _knowledgeGraph.GetAllDocuments())
        {
            // Per-document best-effort: one malformed document must not abort the sweep.
            // Cancellation still propagates.
            try
            {
                string? goalId = null;
                if (string.Equals(doc.Topic, "progress", StringComparison.OrdinalIgnoreCase) &&
                    doc.Id.StartsWith("progress-", StringComparison.Ordinal))
                {
                    goalId = doc.Id["progress-".Length..];
                }
                else if (string.Equals(doc.Topic, "review", StringComparison.OrdinalIgnoreCase) &&
                         doc.Id.StartsWith("review-", StringComparison.Ordinal))
                {
                    goalId = doc.Id["review-".Length..];
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrEmpty(goalId))
                    continue;

                if (!liveGoalIds.Contains(goalId) || releasedGoalIds.Contains(goalId))
                    candidates.Add(doc.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to evaluate knowledge document '{DocumentId}' during startup sweep; skipping it",
                    doc.Id);
            }
        }

        var distinctCandidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctCandidates.Count == 0)
            return 0;

        var result = await _knowledgeGraph.DeleteDocumentsAndCommitAsync(
            distinctCandidates, _knowledgeGraph.ConfigRepoPath,
            "Startup sweep: remove stale progress/review knowledge documents", ct);

        if (!result.Persisted)
            _logger.LogWarning(
                "Failed to persist startup sweep of stale progress/review knowledge documents: {PersistError}",
                result.PersistError);

        return result.DeletedCount;
    }

    /// <summary>
    /// Runs the startup sweep of stale progress/review knowledge documents against the
    /// services registered in <paramref name="services"/>. Resolves the cleanup service and
    /// the goal store; when either is unavailable the sweep is skipped and 0 is returned.
    /// This method never throws — a failed sweep must never block application startup.
    /// Exposed as <c>internal</c> for unit testing via <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="services">Root service provider used to resolve the sweep dependencies.</param>
    /// <param name="logger">Logger used for the outcome message. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of documents deleted, or 0 when the sweep was skipped or failed.</returns>
    internal static async Task<int> ExecuteStartupSweepAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var cleanupService = services.GetService<KnowledgeDocumentCleanupService>();
            var goalStore = services.GetService<IGoalStore>();
            if (cleanupService is null || goalStore is null)
            {
                logger.LogDebug(
                    "Startup sweep skipped — cleanup service or goal store is not registered");
                return 0;
            }

            var allGoals = await goalStore.GetAllGoalsAsync(ct);
            var allReleases = await goalStore.GetReleasesAsync(ct);
            var deleted = await cleanupService.SweepOrphanedDocumentsAsync(allGoals, allReleases, ct);

            logger.LogInformation("Startup sweep removed {DeletedCount} stale knowledge documents", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup sweep of stale knowledge documents failed; continuing startup");
            return 0;
        }
    }
}
