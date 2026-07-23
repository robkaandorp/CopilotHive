using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Git;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Typed failure categories for a release execution, used to map to HTTP status codes.
/// </summary>
public enum ReleaseExecutionFailure
{
    /// <summary>No failure — execution succeeded.</summary>
    None,
    /// <summary>The release could not be found in the store.</summary>
    NotFound,
    /// <summary>The release is already in the Released status.</summary>
    AlreadyReleased,
    /// <summary>The release is already being executed.</summary>
    AlreadyExecuting,
    /// <summary>Validation of the release failed (goals not completed, unconfigured repos, etc.).</summary>
    Validation,
    /// <summary>A git operation (merge or tag) failed during execution.</summary>
    Execution,
}

/// <summary>
/// Result of executing a release: overall success, per-repository results, and typed failure info.
/// </summary>
/// <param name="Success">Whether the whole release executed successfully.</param>
/// <param name="Results">Per-repository execution results.</param>
/// <param name="Error">A human-readable error message, or <c>null</c> on success.</param>
/// <param name="Failure">The typed failure category.</param>
public sealed record ReleaseExecutionResult(
    bool Success,
    IReadOnlyList<RepoReleaseResult> Results,
    string? Error = null,
    ReleaseExecutionFailure Failure = ReleaseExecutionFailure.None);

/// <summary>
/// Result of executing the release for a single repository.
/// </summary>
/// <param name="RepoName">The repository name.</param>
/// <param name="Skipped">Whether the repository was skipped (no release config).</param>
/// <param name="Success">Whether the merge and tag operations succeeded.</param>
/// <param name="MergedTo">The branch that was merged into, or <c>null</c>.</param>
/// <param name="MergeSha">The resulting merge commit SHA, or <c>null</c> for a no-op merge.</param>
/// <param name="TaggedBranch">The branch that was tagged, or <c>null</c>.</param>
/// <param name="TagCreated">Whether a new tag was created (false = tag already existed).</param>
/// <param name="Error">An error message when the repository failed, or <c>null</c>.</param>
public sealed record RepoReleaseResult(
    string RepoName,
    bool Skipped,
    bool Success = false,
    string? MergedTo = null,
    string? MergeSha = null,
    string? TaggedBranch = null,
    bool TagCreated = false,
    string? Error = null);

/// <summary>
/// Result of validating a release prior to execution.
/// </summary>
/// <param name="IsValid">Whether the release passed all validation checks.</param>
/// <param name="Errors">The list of validation errors (empty when valid).</param>
public sealed record ReleaseValidationResult(bool IsValid, List<string> Errors);

/// <summary>
/// Validates releases, executes per-repository merge and tag operations, and rolls back
/// created tags on failure. Serialises executions with a semaphore so only one release
/// runs at a time.
/// </summary>
public sealed class ReleaseExecutionService
{
    private readonly IGoalStore _goalStore;
    private readonly HiveConfigFile _config;
    private readonly IBrainRepoManager _repoManager;
    private readonly ILogger<ReleaseExecutionService> _logger;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    /// <summary>
    /// Initialises a new <see cref="ReleaseExecutionService"/>.
    /// </summary>
    /// <param name="goalStore">The goal/release store.</param>
    /// <param name="config">The hive configuration containing repository definitions.</param>
    /// <param name="repoManager">The Brain repo manager used for merge and tag operations.</param>
    /// <param name="logger">Logger instance.</param>
    public ReleaseExecutionService(
        IGoalStore goalStore,
        HiveConfigFile config,
        IBrainRepoManager repoManager,
        ILogger<ReleaseExecutionService> logger)
    {
        _goalStore = goalStore;
        _config = config;
        _repoManager = repoManager;
        _logger = logger;
    }

    /// <summary>
    /// Validates that a release can be executed: it has at least one goal, all its goals are
    /// completed, and every repository it targets is configured.
    /// </summary>
    /// <param name="release">The release to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validation result.</returns>
    public async Task<ReleaseValidationResult> ValidateReleaseAsync(Release release, CancellationToken ct)
    {
        var errors = new List<string>();

        var goals = await _goalStore.GetGoalsByReleaseAsync(release.Id, ct);
        if (goals.Count == 0)
        {
            errors.Add("Release has no assigned goals.");
        }
        else
        {
            foreach (var goal in goals)
            {
                if (goal.Status != GoalStatus.Completed)
                    errors.Add($"Goal '{goal.Id}' is not completed (status: {goal.Status}).");
            }
        }

        foreach (var repoName in release.RepositoryNames)
        {
            if (!_config.Repositories.Any(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Repository '{repoName}' is not configured.");
        }

        return new ReleaseValidationResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Executes a release: re-reads the current state, validates, then per-repository merges the
    /// default branch into the configured target branch and creates a release tag. On failure,
    /// created tags are rolled back (merges are NOT reverted).
    /// </summary>
    /// <param name="release">The release to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The execution result.</returns>
    public async Task<ReleaseExecutionResult> ExecuteReleaseAsync(Release release, CancellationToken ct)
    {
        await _executionLock.WaitAsync(ct);
        try
        {
            var current = await _goalStore.GetReleaseAsync(release.Id, ct);
            if (current is null)
                return new ReleaseExecutionResult(false, [], "Release not found.", ReleaseExecutionFailure.NotFound);

            if (current.Status == ReleaseStatus.Released)
                return new ReleaseExecutionResult(false, [], "Release is already Released.", ReleaseExecutionFailure.AlreadyReleased);

            if (current.ExecutionState == ReleaseExecutionState.Executing)
                return new ReleaseExecutionResult(false, [], "Release is already being executed.", ReleaseExecutionFailure.AlreadyExecuting);

            current.ExecutionState = ReleaseExecutionState.Executing;
            await _goalStore.UpdateReleaseAsync(current, ct);

            var validation = await ValidateReleaseAsync(current, ct);
            if (!validation.IsValid)
            {
                current.ExecutionState = ReleaseExecutionState.Failed;
                await _goalStore.UpdateReleaseAsync(current, CancellationToken.None);
                return new ReleaseExecutionResult(
                    false, [], string.Join("; ", validation.Errors), ReleaseExecutionFailure.Validation);
            }

            var results = new List<RepoReleaseResult>();
            var createdTags = new Stack<(string Repo, string Tag)>();

            foreach (var repoName in current.RepositoryNames)
            {
                var repo = _config.Repositories.FirstOrDefault(
                    r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));

                if (repo?.Release is null)
                {
                    results.Add(new RepoReleaseResult(repoName, Skipped: true));
                    continue;
                }

                try
                {
                    var mergeSha = await _repoManager.MergeBranchAsync(
                        repoName, repo.DefaultBranch, repo.Release.MergeTo!, ct);

                    var tagCreated = await _repoManager.CreateTagAsync(
                        repoName, current.Tag, repo.Release.TagBranch!, $"Release {current.Tag}", ct);

                    if (tagCreated)
                        createdTags.Push((repoName, current.Tag));

                    results.Add(new RepoReleaseResult(
                        repoName,
                        Skipped: false,
                        Success: true,
                        MergedTo: repo.Release.MergeTo,
                        MergeSha: mergeSha,
                        TaggedBranch: repo.Release.TagBranch,
                        TagCreated: tagCreated));
                }
                catch (Exception ex)
                {
                    results.Add(new RepoReleaseResult(repoName, Skipped: false, Success: false, Error: ex.Message));

                    await RollbackTagsAsync(createdTags);

                    current.ExecutionState = ReleaseExecutionState.Failed;
                    await _goalStore.UpdateReleaseAsync(current, CancellationToken.None);

                    return new ReleaseExecutionResult(
                        false,
                        results,
                        $"Release failed at '{repoName}': {ex.Message}. Tags rolled back (attempted). Merges NOT reverted.",
                        ReleaseExecutionFailure.Execution);
                }
            }

            current.ExecutionState = ReleaseExecutionState.Completed;
            await _goalStore.UpdateReleaseAsync(current, ct);

            return new ReleaseExecutionResult(true, results);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>
    /// Attempts to delete every tag created during an execution, in reverse order. Uses a fresh
    /// bounded (30s) token so rollback runs even if the caller's token was canceled. Failures are
    /// logged but never thrown.
    /// </summary>
    /// <param name="tags">The stack of created (repo, tag) pairs to roll back.</param>
    private async Task RollbackTagsAsync(Stack<(string Repo, string Tag)> tags)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (tags.TryPop(out var entry))
        {
            try
            {
                await _repoManager.DeleteTagAsync(entry.Repo, entry.Tag, cts.Token);
                _logger.LogWarning("Rolled back tag {Tag} for {Repo}", entry.Tag, entry.Repo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rollback tag {Tag} for {Repo}", entry.Tag, entry.Repo);
            }
        }
    }
}
