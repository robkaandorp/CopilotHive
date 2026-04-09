namespace CopilotHive.Services;

/// <summary>
/// Groups branch-related state for a <see cref="GoalPipeline"/>:
/// the feature branch name, the iteration-start SHA, and the merge commit hash.
/// </summary>
public sealed class BranchContext
{
    /// <summary>The feature branch created by the coder for this goal.</summary>
    public string? CoderBranch { get; set; }

    /// <summary>
    /// HEAD SHA of the target repository at the moment the coder was dispatched for this iteration.
    /// Used to compute an iteration-scoped diff (<c>git diff {sha}..HEAD</c>) for reviewers so they
    /// can distinguish this iteration's changes from earlier iterations on the same branch.
    /// <c>null</c> when the SHA could not be captured (e.g. empty repository).
    /// </summary>
    public string? IterationStartSha { get; set; }

    /// <summary>SHA-1 hash of the merge commit produced when the feature branch was merged, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash { get; set; }
}
