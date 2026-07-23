namespace CopilotHive.Git;

/// <summary>
/// Thrown when a git merge fails due to conflicts that cannot be resolved automatically.
/// </summary>
public sealed class MergeConflictException : Exception
{
    /// <summary>The short name of the repository where the conflict occurred.</summary>
    public string RepoName { get; }

    /// <summary>The source branch being merged.</summary>
    public string SourceBranch { get; }

    /// <summary>The target branch being merged into.</summary>
    public string TargetBranch { get; }

    /// <summary>
    /// Initialises a new <see cref="MergeConflictException"/>.
    /// </summary>
    /// <param name="repoName">The short name of the repository.</param>
    /// <param name="sourceBranch">The source branch being merged.</param>
    /// <param name="targetBranch">The target branch being merged into.</param>
    /// <param name="innerException">The optional underlying exception.</param>
    public MergeConflictException(
        string repoName, string sourceBranch, string targetBranch, Exception? innerException = null)
        : base($"Merge conflict in '{repoName}': {sourceBranch} -> {targetBranch}", innerException)
    {
        RepoName = repoName;
        SourceBranch = sourceBranch;
        TargetBranch = targetBranch;
    }
}
