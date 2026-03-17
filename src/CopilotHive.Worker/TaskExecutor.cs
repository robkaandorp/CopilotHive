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
    private const string ConfigRepoDir = "/config-repo";
    private const string ConfigAgentsDir = "/config-repo/agents";
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

            // Clone each repository (skip for improver — it works on the config repo agents folder)
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
                // Improver: pull latest config repo to get freshest agents.md files
                await PullConfigRepoAsync(ct);
            }

            // Determine working directory for Copilot
            // Improver works in the config-repo/agents folder; others in the first cloned repo
            var primaryWorkDir = isImprover
                ? ConfigAgentsDir
                : (repoDirectories.Count > 0 ? repoDirectories[0].Dir : WorkRoot);

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

            if (isImprover)
            {
                contextLines.Add($"Working directory: {ConfigAgentsDir}");
                contextLines.Add("Files: *.agents.md (edit these directly)");
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

            // For roles that push code, ensure Copilot committed its changes.
            // If the working directory is dirty, re-prompt Copilot to commit.
            if (!isImprover && assignment.Role != WorkerRole.Reviewer)
            {
                foreach (var (_, dir) in repoDirectories)
                {
                    copilotOutput = await EnsureCleanWorktreeAsync(copilotOutput, dir, ct);
                }
            }

            // Collect git status from each repo and push changes
            GitStatus? aggregatedStatus = null;

            if (isImprover)
            {
                // Improver: commit and push changes to the config repo agents folder
                aggregatedStatus = await CommitAndPushConfigRepoAsync(ct);
            }
            else
            {
                var isReviewer = assignment.Role == WorkerRole.Reviewer;

                foreach (var (repo, dir) in repoDirectories)
                {
                    var baseBranch = assignment.BranchInfo?.BaseBranch ?? repo.DefaultBranch;
                    var status = await GitOperations.GetGitStatusAsync(dir, baseBranch, ct);

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

    /// <summary>
    /// Checks if the working directory has uncommitted changes and, if so, re-prompts Copilot
    /// to stage and commit them. Retries up to <paramref name="maxRetries"/> times.
    /// Returns the accumulated Copilot output including any cleanup conversation.
    /// </summary>
    private async Task<string> EnsureCleanWorktreeAsync(
        string previousOutput, string workDir, CancellationToken ct, int maxRetries = 2)
    {
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            if (!await GitOperations.HasUncommittedChangesAsync(workDir, ct))
                return previousOutput;

            _log.Info($"Working directory has uncommitted changes (attempt {attempt + 1}/{maxRetries}), prompting Copilot to commit...");

            var commitPrompt = """
                Your working directory has uncommitted changes. Please:
                1. Run `git add -A` to stage all changes
                2. Run `git commit` with a descriptive message summarizing what you did
                3. Run `git status` to verify the working directory is clean

                Do NOT push — the infrastructure handles pushing.
                """;

            var cleanupOutput = await copilotRunner.SendPromptAsync(commitPrompt, workDir, ct);
            previousOutput += "\n\n[Auto-commit prompt]\n" + cleanupOutput;
        }

        if (await GitOperations.HasUncommittedChangesAsync(workDir, ct))
            _log.Error("Working directory still dirty after commit retries — uncommitted changes may be lost");

        return previousOutput;
    }

    /// <summary>
    /// Pulls the latest changes from the config repo so the improver works on fresh agents.md files.
    /// The config repo is cloned at container startup by entrypoint.sh.
    /// </summary>
    private async Task PullConfigRepoAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(ConfigRepoDir, ".git")))
        {
            _log.Info("Config repo not found — improver will work without it");
            return;
        }

        _log.Info("Pulling latest config repo for improver...");
        var (exitCode, stdout, stderr) = await GitOperations.RunGitCommandAsync(
            ConfigRepoDir, "pull --ff-only", ct);

        if (exitCode == 0)
            _log.Info($"Config repo up to date: {stdout.Trim()}");
        else
            _log.Error($"Config repo pull failed (exit {exitCode}): {stderr.Trim()}");
    }

    /// <summary>
    /// Commits and pushes any changes the improver made to *.agents.md files in the config repo.
    /// Only stages files in the agents/ subfolder to prevent accidental changes elsewhere.
    /// </summary>
    private async Task<GitStatus> CommitAndPushConfigRepoAsync(CancellationToken ct)
    {
        var status = new GitStatus();

        if (!Directory.Exists(Path.Combine(ConfigRepoDir, ".git")))
            return status;

        // Only stage agents/*.agents.md — defense-in-depth to prevent touching other files
        var (addExit, _, addErr) = await GitOperations.RunGitCommandAsync(
            ConfigRepoDir, "add agents/*.agents.md", ct);
        if (addExit != 0)
        {
            _log.Error($"git add failed: {addErr.Trim()}");
            return status;
        }

        // Check if there are staged changes
        var (diffExit, diffOut, _) = await GitOperations.RunGitCommandAsync(
            ConfigRepoDir, "diff --cached --name-only", ct);
        if (diffExit != 0 || string.IsNullOrWhiteSpace(diffOut))
        {
            _log.Info("No agents.md changes to commit");
            return status;
        }

        var changedFiles = diffOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        status.FilesChanged = changedFiles.Length;
        _log.Info($"Improver changed {changedFiles.Length} file(s): {string.Join(", ", changedFiles)}");

        // Commit
        var (commitExit, commitOut, commitErr) = await GitOperations.RunGitCommandAsync(
            ConfigRepoDir, "commit -m \"Improve agents.md files (automated by CopilotHive Improver)\"", ct);
        if (commitExit != 0)
        {
            _log.Error($"git commit failed: {commitErr.Trim()}");
            return status;
        }

        status.LastCommitMessage = "Improve agents.md files (automated by CopilotHive Improver)";
        _log.Info($"Committed: {commitOut.Trim()}");

        // Pull (merge orchestrator's goals/metrics commits) then push
        try
        {
            var (pullExit, pullOut, pullErr) = await GitOperations.RunGitCommandAsync(
                ConfigRepoDir, "pull --no-rebase", ct);
            if (pullExit != 0)
            {
                _log.Error($"git pull failed: {pullErr.Trim()}");
                // Abort any in-progress merge and try force-pushing our commit
                await GitOperations.RunGitCommandAsync(ConfigRepoDir, "merge --abort", ct);
            }

            var (pushExit, _, pushErr) = await GitOperations.RunGitCommandAsync(
                ConfigRepoDir, "push", ct);
            if (pushExit != 0)
            {
                _log.Error($"git push failed: {pushErr.Trim()}");
                return status;
            }

            status.Pushed = true;
            _log.Info("Pushed config repo changes");
        }
        catch (Exception ex)
        {
            _log.Error($"Push failed: {ex.Message}");
        }

        return status;
    }
}
