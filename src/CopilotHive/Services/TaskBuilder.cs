using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using DomainRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// Constructs <see cref="TaskAssignment"/> proto messages from goal and branch information.
/// </summary>
public sealed class TaskBuilder(BranchCoordinator branchCoordinator)
{
    /// <summary>
    /// Builds a <see cref="TaskAssignment"/> proto message for a multi-repo goal.
    /// </summary>
    /// <param name="goalId">Unique identifier of the goal.</param>
    /// <param name="goalDescription">Human-readable description of the goal.</param>
    /// <param name="role">Worker role that will execute the task.</param>
    /// <param name="iteration">Current iteration number.</param>
    /// <param name="repositories">Repositories the worker should operate on.</param>
    /// <param name="prompt">The prompt to send to the worker.</param>
    /// <param name="branchAction">Git branch action to perform (create, checkout, etc.).</param>
    /// <param name="model">Optional model ID for this task (e.g., "claude-sonnet-4.6").</param>
    /// <returns>A fully constructed <see cref="TaskAssignment"/>.</returns>
    public TaskAssignment Build(
        string goalId,
        string goalDescription,
        DomainRole role,
        int iteration,
        IEnumerable<TargetRepository> repositories,
        string prompt,
        BranchAction branchAction,
        string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var roleName = role.ToRoleName();

        var repoList = repositories.ToList();
        if (repoList.Count == 0)
            throw new InvalidOperationException($"No repositories configured for goal '{goalId}'.");
        var baseBranch = repoList[0].DefaultBranch;

        var branchInfo = branchCoordinator.GetBranchInfo(goalId, branchAction, baseBranch);

        var assignment = new TaskAssignment
        {
            TaskId = $"{goalId}-{roleName}-{iteration:D3}",
            GoalId = goalId,
            GoalDescription = goalDescription,
            Prompt = prompt,
            BranchInfo = branchInfo,
            Role = role.ToGrpcRole(),
            Model = model ?? "",
        };

        foreach (var repo in repoList)
        {
            assignment.Repositories.Add(new RepositoryInfo
            {
                Url = repo.Url,
                Name = repo.Name,
                DefaultBranch = repo.DefaultBranch,
            });
        }

        return assignment;
    }
}
