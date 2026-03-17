using System.Collections.Concurrent;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Orchestration;

/// <summary>
/// Generates consistent feature branch names and tracks which branches have been created per goal.
/// </summary>
public sealed class BranchCoordinator
{
    private readonly ConcurrentDictionary<string, List<(string Repo, string Branch)>> _branchesByGoal = new();

    /// <summary>
    /// Generates a consistent feature branch name: copilothive/{goalId}/{role}-{iteration:D3}
    /// </summary>
    public string GetFeatureBranch(string goalId, string role, int iteration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentOutOfRangeException.ThrowIfNegative(iteration);

        return $"copilothive/{goalId}/{role.ToLowerInvariant()}-{iteration:D3}";
    }

    /// <summary>
    /// Builds a <see cref="BranchInfo"/> proto message for the given parameters.
    /// </summary>
    public BranchInfo GetBranchInfo(string goalId, string role, int iteration, BranchAction action, string baseBranch)
    {
        var featureBranch = GetFeatureBranch(goalId, role, iteration);

        return new BranchInfo
        {
            BaseBranch = baseBranch,
            FeatureBranch = featureBranch,
            Action = action,
        };
    }

    /// <summary>
    /// Records that a branch was created in a specific repo for a goal.
    /// </summary>
    public void RecordBranchCreated(string goalId, string repo, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var list = _branchesByGoal.GetOrAdd(goalId, _ => []);
        lock (list)
        {
            if (!list.Exists(b => b.Repo == repo && b.Branch == branch))
                list.Add((repo, branch));
        }
    }

    /// <summary>
    /// Returns all tracked branches for a given goal.
    /// </summary>
    public IReadOnlyList<(string Repo, string Branch)> GetBranchesForGoal(string goalId)
    {
        if (_branchesByGoal.TryGetValue(goalId, out var list))
        {
            lock (list)
            {
                return list.ToList().AsReadOnly();
            }
        }

        return [];
    }
}
