using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

public sealed class TaskBuilder(BranchCoordinator branchCoordinator)
{
    /// <summary>
    /// Builds a <see cref="TaskAssignment"/> proto message for a multi-repo goal.
    /// </summary>
    public TaskAssignment Build(
        string goalId,
        string goalDescription,
        WorkerRole role,
        int iteration,
        IEnumerable<TargetRepository> repositories,
        string prompt,
        BranchAction branchAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var roleName = role switch
        {
            WorkerRole.Coder => "coder",
            WorkerRole.Reviewer => "reviewer",
            WorkerRole.Tester => "tester",
            WorkerRole.Improver => "improver",
            _ => "worker",
        };

        var baseBranch = "main";
        var repoList = repositories.ToList();
        if (repoList.Count > 0)
            baseBranch = repoList[0].DefaultBranch;

        var branchInfo = branchCoordinator.GetBranchInfo(goalId, roleName, iteration, branchAction, baseBranch);

        var assignment = new TaskAssignment
        {
            TaskId = $"{goalId}-{roleName}-{iteration:D3}",
            GoalId = goalId,
            GoalDescription = goalDescription,
            Prompt = prompt,
            BranchInfo = branchInfo,
            Role = role,
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
