using System.Diagnostics;
using CopilotHive.Shared.Grpc;
using GrpcTaskStatus = CopilotHive.Shared.Grpc.TaskStatus;

namespace CopilotHive.Worker;

/// <summary>
/// Orchestrates the full lifecycle of a single task assignment:
/// clone repos, handle branches, run Copilot, collect results.
/// </summary>
public sealed class TaskExecutor(CopilotRunner copilotRunner, IToolCallBridge? toolBridge = null)
{
    private const string WorkRoot = "/copilot-home";
    private readonly WorkerLogger _log = new("Task");

    /// <summary>
    /// Executes the full lifecycle of a task assignment: cloning repos, branching,
    /// running Copilot, collecting results, and pushing changes.
    /// </summary>
    /// <param name="assignment">The task assignment containing prompt, repos, and branch info.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="TaskComplete"/> with status, output, and git metrics.</returns>
    public async Task<TaskComplete> ExecuteAsync(TaskAssignment assignment, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Wire tool bridge and task context into CopilotRunner
        copilotRunner.SetToolBridge(toolBridge);
        copilotRunner.SetCurrentTaskId(assignment.TaskId);

        try
        {
            var isImprover = assignment.Role == WorkerRole.Improver;

            // Clone each repository (skip for improver — it's a pure text-in/text-out role
            // that receives all context in the prompt and must not access source code)
            var repoDirectories = new List<(RepositoryInfo Repo, string Dir)>();

            if (!isImprover)
            {
                foreach (var repo in assignment.Repositories)
                {
                    var targetDir = Path.Combine(WorkRoot, repo.Name);

                    // Clean up any previous clone
                    if (Directory.Exists(targetDir))
                        await GitOperations.ForceDeleteDirectoryAsync(targetDir);

                    Console.WriteLine($"[Task] Cloning {repo.Name} from {repo.Url}");
                    await GitOperations.CloneRepositoryAsync(repo.Url, targetDir, ct);

                    // Handle branch operations
                    if (assignment.BranchInfo is { } branchInfo && !string.IsNullOrEmpty(branchInfo.FeatureBranch))
                    {
                        switch (branchInfo.Action)
                        {
                            case BranchAction.Create:
                                var baseBranch = string.IsNullOrEmpty(branchInfo.BaseBranch)
                                    ? repo.DefaultBranch
                                    : branchInfo.BaseBranch;
                                _log.Info($"Creating branch {branchInfo.FeatureBranch} from {baseBranch}");
                                await GitOperations.CreateBranchAsync(targetDir, branchInfo.FeatureBranch, baseBranch, ct);
                                break;

                            case BranchAction.Checkout:
                                _log.Info($"Checking out branch {branchInfo.FeatureBranch}");
                                await GitOperations.CheckoutBranchAsync(targetDir, branchInfo.FeatureBranch, ct);
                                break;

                            case BranchAction.Merge:
                            case BranchAction.Unspecified:
                            default:
                                break;
                        }
                    }

                    repoDirectories.Add((repo, targetDir));
                }
            }
            else
            {
                _log.Info("Improver role: skipping git clone (pure text-in/text-out)");
            }

            // Determine working directory for Copilot (first repo, or WorkRoot for improver)
            var primaryWorkDir = repoDirectories.Count > 0
                ? repoDirectories[0].Dir
                : WorkRoot;

            // Build context header with branch and repo info for the Copilot agent
            var contextLines = new List<string>
            {
                "=== WORKSPACE CONTEXT ===",
                $"Role: {assignment.Role}",
            };

            if (repoDirectories.Count > 0)
            {
                var (primaryRepo, _) = repoDirectories[0];
                contextLines.Add($"Repository: {primaryRepo.Name}");
                contextLines.Add($"Default branch: {primaryRepo.DefaultBranch}");
            }

            if (assignment.BranchInfo is { } bi2)
            {
                if (!string.IsNullOrEmpty(bi2.BaseBranch))
                    contextLines.Add($"Base branch: {bi2.BaseBranch}");
                if (!string.IsNullOrEmpty(bi2.FeatureBranch))
                    contextLines.Add($"Feature branch: {bi2.FeatureBranch}");
            }

            contextLines.Add($"Working directory: {primaryWorkDir}");
            contextLines.Add("=========================");
            contextLines.Add("");

            var enrichedPrompt = string.Join("\n", contextLines) + assignment.Prompt;

            // Send prompt to Copilot
            _log.Info($"Sending prompt to Copilot ({enrichedPrompt.Length} chars)");
            var copilotOutput = await copilotRunner.SendPromptAsync(enrichedPrompt, primaryWorkDir, ct);

            // Collect git status from each repo and push changes (skip for improver)
            GitStatus? aggregatedStatus = null;

            if (!isImprover)
            {
                var isReviewer = assignment.Role == WorkerRole.Reviewer;

                foreach (var (repo, dir) in repoDirectories)
                {
                    var status = await GitOperations.GetGitStatusAsync(dir, ct);

                    // Push if there are changes, we have a feature branch, and the role is allowed to push.
                    // Reviewer is read-only — it must never push changes to the coder's branch.
                    if (assignment.BranchInfo is { } bi
                        && !string.IsNullOrEmpty(bi.FeatureBranch)
                        && status.FilesChanged > 0
                        && !isReviewer)
                    {
                        try
                        {
                            await GitOperations.PushBranchAsync(dir, bi.FeatureBranch, ct);
                            status.Pushed = true;
                            _log.Info($"Pushed {bi.FeatureBranch} for {repo.Name}");
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"Push failed for {repo.Name}: {ex.Message}");
                            status.Pushed = false;
                        }
                    }

                    // Use first repo's status as the aggregate
                    aggregatedStatus ??= status;
                }
            }

            stopwatch.Stop();

            return new TaskComplete
            {
                TaskId = assignment.TaskId,
                Status = GrpcTaskStatus.Completed,
                Output = copilotOutput,
                GitStatus = aggregatedStatus ?? new GitStatus(),
                Metrics = new TaskMetrics
                {
                    Verdict = "PASS",
                },
            };
        }
        catch (OperationCanceledException)
        {
            return new TaskComplete
            {
                TaskId = assignment.TaskId,
                Status = GrpcTaskStatus.Cancelled,
                Output = "Task was cancelled.",
                Metrics = new TaskMetrics { Verdict = "CANCELLED" },
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Task] Failed: {ex}");

            return new TaskComplete
            {
                TaskId = assignment.TaskId,
                Status = GrpcTaskStatus.Failed,
                Output = $"Error: {ex.Message}",
                Metrics = new TaskMetrics
                {
                    Verdict = "FAIL",
                    Issues = { ex.Message },
                },
            };
        }
    }
}
