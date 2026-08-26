using System.Diagnostics;
using System.Text.Json;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace CopilotHive.Worker;

/// <summary>
/// Orchestrates the full lifecycle of a single task:
/// clone repos, handle branches, run Copilot, collect results.
/// </summary>
public sealed class TaskExecutor(
    IAgentRunner agentRunner,
    IToolCallBridge? toolBridge = null,
    IGitOperations? gitOperations = null,
    ISessionClient? sessionClient = null,
    string configRepoDir = "/config-repo")
{
    /// <summary>
    /// Fallback working directory used when a task has no repositories to clone (e.g. the
    /// improver, or tasks that operate purely via tool calls). Overridable via the
    /// <c>WORKER_WORK_ROOT</c> environment variable so tests can point it at a real directory
    /// that exists on the host OS; the container always creates <c>/copilot-home</c>
    /// (see docker/worker/Dockerfile), so no override is needed in production. Read live
    /// (not cached) so per-test overrides take effect regardless of static-init ordering.
    /// </summary>
    private static string WorkRoot =>
        Environment.GetEnvironmentVariable("WORKER_WORK_ROOT") ?? "/copilot-home";

    private readonly string _configRepoDir = configRepoDir;
    private readonly string _configAgentsDir = Path.Combine(configRepoDir, "agents");

    /// <summary>
    /// Maximum number of changed-file paths rendered into the worker-side Improver log line.
    /// Keeps the log bounded regardless of how many files the improver touched; the remainder
    /// is reported as a <c>(+N more)</c> count. This governs LOG OUTPUT ONLY — the diagnostic
    /// <see cref="GitChangeSummary.ChangedFiles"/> list is governed by
    /// <see cref="GitOperations.ChangedFilesMaxPaths"/>.
    /// </summary>
    internal const int ImproverLogMaxPaths = 10;

    private readonly WorkerLogger _log = new("Task");
    private readonly IGitOperations _git = gitOperations ?? new DefaultGitOperations();

    /// <summary>
    /// Writes to <see cref="Console.Error"/> safely, swallowing <see cref="ObjectDisposedException"/>
    /// and <see cref="IOException"/> so logging never propagates an exception. Uses a lock to avoid
    /// interleaved output when multiple tasks log concurrently.
    /// </summary>
    private static readonly object _errorLock = new();
    private static void TryWriteError(string message)
    {
        try
        {
            lock (_errorLock)
            {
                Console.Error.WriteLine(message);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Executes the full lifecycle of a task: cloning repos, branching,
    /// running Copilot, collecting results, and pushing changes.
    /// </summary>
    /// <param name="task">The domain task containing prompt, repos, and branch info.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="TaskResult"/> with status, output, and git metrics.</returns>
    public async Task<TaskResult> ExecuteAsync(WorkTask task, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Wire tool bridge and task context into CopilotRunner
        agentRunner.SetToolBridge(toolBridge);
        agentRunner.SetCurrentTaskId(task.TaskId);
        agentRunner.SetCurrentGoalId(task.GoalId);
        agentRunner.SetMaxContextTokens(task.MaxContextTokens);
        agentRunner.SetCompactionModel(
            task.Metadata.TryGetValue("compaction_model", out var compModel) ? compModel : null);
        agentRunner.SetCompactionMaxTokens(
            task.Metadata.TryGetValue("compaction_max_tokens", out var cmt) && int.TryParse(cmt, out var cmtVal) ? cmtVal : (int?)null);
        agentRunner.SetSubAgentModels(task.SubAgentModels);
        agentRunner.ClearTestReport();
        agentRunner.ClearWorkerReport();

        // Always clear any stale tester report, then set it only when metadata is present.
        agentRunner.SetTesterReport(null);
        if (task.Role == WorkerRole.Reviewer
            && task.Metadata.TryGetValue("tester_report", out var testerReport))
        {
            agentRunner.SetTesterReport(testerReport);
        }

        // Load persisted session (if any) so the agent can resume prior context
        if (sessionClient != null && !string.IsNullOrEmpty(task.SessionId))
        {
            await LoadSessionAsync(task.SessionId, ct);
        }

        try
        {
            var isImprover = task.Role == WorkerRole.Improver;
            var isReviewer = task.Role == WorkerRole.Reviewer;

            // Clone each repository (skip for improver — it works on the config repo agents folder)
            var repoDirectories = new List<(TargetRepository Repo, string Dir)>();

            if (!isImprover)
            {
                foreach (var repo in task.Repositories)
                {
                    var targetDir = Path.Combine(WorkRoot, repo.Name);

                    // Clean up any previous clone
                    if (Directory.Exists(targetDir))
                        await _git.ForceDeleteDirectoryAsync(targetDir);

                    _log.Info($"Cloning {repo.Name} from {repo.Url}");
                    await _git.CloneRepositoryAsync(repo.Url, targetDir, ct);

                    // Handle branch operations
                    if (task.BranchInfo is { } branchInfo && !string.IsNullOrEmpty(branchInfo.FeatureBranch))
                    {
                        var baseBranch = string.IsNullOrEmpty(branchInfo.BaseBranch)
                            ? repo.DefaultBranch
                            : branchInfo.BaseBranch;

                        switch (branchInfo.Action)
                        {
                            case BranchAction.Create:
                                _log.Info($"Creating branch {branchInfo.FeatureBranch} from {baseBranch}");
                                await _git.CreateBranchAsync(targetDir, branchInfo.FeatureBranch, baseBranch, ct);
                                break;

                            case BranchAction.Checkout:
                                try
                                {
                                    _log.Info($"Checking out branch {branchInfo.FeatureBranch}");
                                    await _git.CheckoutBranchAsync(targetDir, branchInfo.FeatureBranch, ct);
                                }
                                catch (GitOperationException ex)
                                {
                                    _log.Warn($"Checkout failed ({ex.Message}), creating branch from {baseBranch}");
                                    await _git.CreateBranchAsync(targetDir, branchInfo.FeatureBranch, baseBranch, ct);
                                }
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

            // Compute merge-base for feature branches so reviewers/testers diff only branch changes
            string? mergeBase = null;
            if (!isImprover && repoDirectories.Count > 0 && task.BranchInfo is { } bi1
                && !string.IsNullOrEmpty(bi1.FeatureBranch) && !string.IsNullOrEmpty(bi1.BaseBranch))
            {
                var (_, targetDir) = repoDirectories[0];
                mergeBase = await _git.GetMergeBaseAsync(targetDir, bi1.BaseBranch, ct);
                if (mergeBase != null)
                    _log.Info($"Merge base: {mergeBase[..12]}");
                else
                    _log.Info("Could not compute merge-base; falling back to branch name diff");
            }

            // For coder tasks: capture HEAD SHA on the feature branch immediately before the agent runs.
            // This SHA is returned in the result so the orchestrator can store it and later give it to
            // the reviewer as the "iteration start" point for a scoped diff (git diff {sha}..HEAD).
            string? iterationStartSha = null;
            if (task.Role == WorkerRole.Coder && !isImprover && repoDirectories.Count > 0)
            {
                var (_, targetDir) = repoDirectories[0];
                try
                {
                    var (exitCode, stdout, _) = await _git.RunGitCommandAsync(targetDir, "rev-parse HEAD", ct);
                    if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                    {
                        iterationStartSha = stdout.Trim();
                        _log.Info($"Captured iteration start SHA: {iterationStartSha[..Math.Min(iterationStartSha.Length, 12)]}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not capture iteration start SHA (empty repo?): {ex.Message}");
                }
            }

            // For read-only roles (reviewer): capture the per-repository HEAD SHA immediately
            // before the agent runs. This baseline is compared against the final HEAD after the
            // run to classify whether the role moved HEAD, so a spurious "not pushed" warning can
            // be suppressed when the role left no net branch diff. A null value means the baseline
            // could not be captured for that repository.
            Dictionary<string, string?> repoStartShas = [];
            if (isReviewer)
            {
                foreach (var (repo, dir) in repoDirectories)
                {
                    try
                    {
                        var (exitCode, stdout, _) = await _git.RunGitCommandAsync(dir, "rev-parse HEAD", ct);
                        repoStartShas[repo.Name] = exitCode == 0 && !string.IsNullOrWhiteSpace(stdout)
                            ? stdout.Trim()
                            : null;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Capture failure is recorded silently: a repository whose baseline could
                        // not be captured must contribute neither a note nor pushed intent unless
                        // it also has a nonzero branch diff, which the classification decides later.
                        repoStartShas[repo.Name] = null;
                    }
                }
            }

            // Determine working directory for Copilot
            // Improver works in the config-repo/agents folder; others in the first cloned repo
            var primaryWorkDir = isImprover
                ? _configAgentsDir
                : (repoDirectories.Count > 0 ? repoDirectories[0].Dir : WorkRoot);

            // Build context header with branch and repo info for the Copilot agent
            var contextLines = new List<string>
            {
                "=== WORKSPACE CONTEXT ===",
                $"Role: {task.Role}",
            };

            if (repoDirectories.Count > 0)
            {
                var (primaryRepo, _) = repoDirectories[0];
                contextLines.Add($"Repository: {primaryRepo.Name}");
            }

            if (isImprover)
            {
                contextLines.Add($"Working directory: {_configAgentsDir}");
                contextLines.Add("Files: *.agents.md (edit these directly)");
            }

            if (mergeBase != null)
            {
                contextLines.Add($"Merge base commit: {mergeBase}");
                contextLines.Add($"Diff command: git diff {mergeBase}..HEAD");
                contextLines.Add("IMPORTANT: Use the diff command above to see changes. Do NOT diff against branch names.");

                // For reviewers: also expose an iteration-scoped diff so they can focus
                // on what changed in the current iteration vs. all previous iterations.
                if (task.Role == WorkerRole.Reviewer
                    && task.Metadata.TryGetValue("iteration_start_sha", out var reviewerIterationSha)
                    && !string.IsNullOrEmpty(reviewerIterationSha))
                {
                    contextLines.Add($"Iteration diff command: git diff {reviewerIterationSha}..HEAD");
                }
            }
            else if (task.BranchInfo is { } bi2 && !string.IsNullOrEmpty(bi2.BaseBranch))
            {
                contextLines.Add($"Base branch: {bi2.BaseBranch}");
            }

            contextLines.Add($"Working directory: {primaryWorkDir}");
            contextLines.Add("=========================");
            contextLines.Add("");

            var enrichedPrompt = string.Join("\n", contextLines) + task.Prompt;

            // Send prompt to Copilot
            _log.Info($"Sending prompt to Copilot ({enrichedPrompt.Length} chars)");
            var copilotOutput = await agentRunner.SendPromptAsync(enrichedPrompt, primaryWorkDir, ct);

            // For roles that push code, ensure Copilot committed its changes.
            // If the working directory is dirty, re-prompt Copilot to commit.
            if (!isImprover && task.Role != WorkerRole.Reviewer)
            {
                foreach (var (_, dir) in repoDirectories)
                {
                    copilotOutput = await EnsureCleanWorktreeAsync(copilotOutput, dir, ct);
                }
            }

            // For testers: ensure structured test metrics were reported via tool call.
            // If the tester didn't call report_test_results, prompt it to do so.
            if (task.Role == WorkerRole.Tester && agentRunner.LastTestReport is null)
            {
                copilotOutput = await EnsureTestMetricsReportedAsync(copilotOutput, primaryWorkDir, ct);
            }

            // Collect git status from each repo and push changes
            GitChangeSummary? aggregatedStatus = null;
            List<string> pushErrors = [];

            if (isImprover)
            {
                // Enforce character limit on *.agents.md files before committing.
                // Re-prompts Copilot in the same session to condense if over limit.
                copilotOutput = await EnsureAgentsMdWithinLimitsAsync(copilotOutput, ct);

                // Improver: commit and push changes to the config repo agents folder
                aggregatedStatus = await CommitAndPushConfigRepoAsync(ct);
            }
            else
            {
                // Accumulate counts and changed-file paths across ALL repositories that
                // have changes. Paths are repo-qualified only when more than one repo changed.
                var totalFilesChanged = 0;
                var totalInsertions = 0;
                var totalDeletions = 0;
                var anyChanges = false;
                var allPushed = true;
                List<(string RepoName, GitChangeSummary Status)> changedRepos = [];

                // Read-only role classification (reviewer only). Compares the per-repository
                // baseline HEAD captured before the run against the final HEAD:
                //   Class A — HEAD net unmoved (both SHAs captured and equal).
                //   Class B — a nonzero branch diff combined with a moved HEAD or a failed capture.
                //   Class C — HEAD moved but the branch diff is empty (a note-worthy anomaly).
                var anyClassB = false;
                var anyUsableBaseline = false;
                List<string> classCRepos = [];

                foreach (var (repo, dir) in repoDirectories)
                {
                    var baseBranch = task.BranchInfo?.BaseBranch ?? repo.DefaultBranch;
                    var status = await _git.GetGitStatusAsync(dir, baseBranch, ct);

                    if (isReviewer)
                    {
                        string? finalSha = null;
                        try
                        {
                            var (exitCode, stdout, _) = await _git.RunGitCommandAsync(dir, "rev-parse HEAD", ct);
                            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                                finalSha = stdout.Trim();
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // Silent capture failure — the classification below treats a missing
                            // final SHA as "no usable baseline" without emitting a note.
                            finalSha = null;
                        }

                        repoStartShas.TryGetValue(repo.Name, out var startSha);

                        var usableBaseline = !string.IsNullOrEmpty(startSha) && !string.IsNullOrEmpty(finalSha);
                        if (usableBaseline)
                            anyUsableBaseline = true;

                        var headMoved = usableBaseline && finalSha != startSha;

                        if (status.FilesChanged > 0 && (!usableBaseline || headMoved))
                        {
                            anyClassB = true;
                        }
                        else if (headMoved && status.FilesChanged == 0)
                        {
                            classCRepos.Add(repo.Name);
                        }
                    }

                    // Push if there are changes, we have a feature branch, and the role is allowed to push.
                    // Reviewer is read-only — it must never push changes to the coder's branch.
                    var pushed = false;
                    if (task.BranchInfo is { } bi
                        && !string.IsNullOrEmpty(bi.FeatureBranch)
                        && status.FilesChanged > 0
                        && !isReviewer)
                    {
                        try
                        {
                            await _git.PushBranchAsync(dir, bi.FeatureBranch, ct);
                            pushed = true;
                            _log.Info($"Pushed {bi.FeatureBranch} for {repo.Name}");
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"Push failed for {repo.Name}: {ex.Message}");
                            pushErrors.Add($"Push failed for {repo.Name}: {ex.Message}");
                        }
                    }

                    // Only consider repos with actual changes for the aggregate.
                    // Repos with no changes must never contribute counts or flip Pushed.
                    if (status.FilesChanged > 0)
                    {
                        anyChanges = true;
                        totalFilesChanged += status.FilesChanged;
                        totalInsertions += status.Insertions;
                        totalDeletions += status.Deletions;
                        if (!pushed)
                            allPushed = false;
                        changedRepos.Add((repo.Name, status));
                    }
                }

                if (isReviewer)
                {
                    foreach (var repoName in classCRepos)
                        _log.Warn($"Task {task.TaskId}: read-only role moved HEAD during its run in repository {repoName} (no net diff vs base)");
                }

                // For read-only roles the aggregate Pushed flag reports "no unexpected write
                // activity" rather than an actual push: a Class-B repository dominates and keeps
                // it false; otherwise at least one usable baseline pair suppresses the warning.
                var reviewerPushed = !anyClassB && anyUsableBaseline;

                if (anyChanges)
                {
                    List<string> allChangedFiles = [];
                    var qualify = changedRepos.Count > 1;
                    foreach (var (repoName, status) in changedRepos)
                    {
                        foreach (var path in status.ChangedFiles)
                            allChangedFiles.Add(qualify ? $"{repoName}:{path}" : path);
                    }

                    if (allChangedFiles.Count > GitOperations.ChangedFilesMaxPaths)
                        allChangedFiles = [.. allChangedFiles.Take(GitOperations.ChangedFilesMaxPaths)];

                    aggregatedStatus = new GitChangeSummary
                    {
                        FilesChanged = totalFilesChanged,
                        Insertions = totalInsertions,
                        Deletions = totalDeletions,
                        Pushed = isReviewer ? reviewerPushed : allPushed,
                        ChangedFiles = allChangedFiles,
                    };
                }
                else if (isReviewer)
                {
                    // Read-only roles always report an aggregate summary so the orchestrator can
                    // observe the suppression decision even when nothing changed at all.
                    aggregatedStatus = new GitChangeSummary
                    {
                        FilesChanged = 0,
                        Insertions = 0,
                        Deletions = 0,
                        Pushed = reviewerPushed,
                        ChangedFiles = [],
                    };
                }

                if (pushErrors.Count > 0)
                    copilotOutput += "\n\n[Git Push Errors]\n" + string.Join("\n", pushErrors);
            }

            stopwatch.Stop();

            // Build TaskMetrics from structured tool call data when available
            var testReport = agentRunner.LastTestReport;
            var workerReport = agentRunner.LastWorkerReport;
            TaskMetrics metrics;
            if (testReport is not null)
            {
                metrics = new TaskMetrics
                {
                    Verdict = testReport.Verdict.ToVerdictString(),
                    BuildSuccess = testReport.BuildSuccess,
                    TotalTests = testReport.TotalTests,
                    PassedTests = testReport.PassedTests,
                    FailedTests = testReport.FailedTests,
                    CoveragePercent = testReport.CoveragePercent ?? 0,
                    Issues = [.. testReport.Issues],
                    Summary = workerReport?.Summary ?? testReport.Summary,
                };
            }
            else if (workerReport is not null)
            {
                var verdictStr = workerReport.ReviewVerdict?.ToVerdictString()
                    ?? workerReport.TaskVerdict?.ToVerdictString()
                    ?? "PASS";
                metrics = new TaskMetrics
                {
                    Verdict = verdictStr,
                    Issues = [.. workerReport.Issues],
                    Summary = workerReport.Summary,
                };
            }
            else
            {
                // Improver has no report tool — default to PASS.
                // All other roles MUST call their report tool; missing report = FAIL or REQUEST_CHANGES.
                var hasReportTool = task.Role is not WorkerRole.Improver;
                var missingReportVerdict = task.Role == WorkerRole.Reviewer ? "REQUEST_CHANGES" : "FAIL";
                metrics = new TaskMetrics
                {
                    Verdict = hasReportTool ? missingReportVerdict : "PASS",
                    Issues = hasReportTool
                        ? [$"Worker ({task.Role.ToRoleName()}) completed without calling its mandatory report tool. This usually indicates API errors, timeouts, or the worker hallucinating tool calls as text."]
                        : [],
                };
            }

            foreach (var err in pushErrors)
                metrics.Issues.Add(err);

            // Persist updated session so future tasks in the same goal can resume context
            if (sessionClient != null && !string.IsNullOrEmpty(task.SessionId))
            {
                await SaveSessionAsync(task.SessionId, ct);
            }

            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Completed,
                Output = copilotOutput,
                GitStatus = aggregatedStatus ?? new GitChangeSummary(),
                Metrics = metrics,
                IterationStartSha = iterationStartSha,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Real cancellation (e.g., shutdown signal) — propagate as Cancelled
            TryWriteError($"[Task] Cancelled by token: {task.TaskId}");
            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Cancelled,
                Output = "Task was cancelled.",
                Metrics = new TaskMetrics { Verdict = "CANCELLED" },
            };
        }
        catch (OperationCanceledException ex)
        {
            // Not a real cancellation — likely an API timeout or HTTP failure.
            // Treat as a failure so the orchestrator can retry or fail the phase.
            //
            // SANITIZED: this is the FIRST consuming boundary for a provisioning/client/LLM
            // exception thrown out of IAgentRunner.SendPromptAsync. The raw message can echo a
            // provisioned GH_TOKEN or OLLAMA_API_KEY, and the TaskResult below travels to the
            // orchestrator where it is logged and persisted — so neither the log line nor the
            // result may carry raw exception text.
            var safe = SafeExceptionLog.Describe(ex);
            TryWriteError($"[Task] Failed (API timeout/error) [{safe}]");
            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Failed,
                Output = $"Error: API call failed or timed out [{safe}]",
                Metrics = new TaskMetrics
                {
                    Verdict = "FAIL",
                    Issues = [$"API timeout/error [{safe}]"],
                },
            };
        }
        catch (Exception ex)
        {
            // SANITIZED for the same reason as the OperationCanceledException catch above:
            // this is the first boundary that consumes an exception originating at the
            // provisioning / LLM-client / LLM-HTTP layer, and the TaskResult is transmitted to
            // the orchestrator for logging and persistence.
            var safe = SafeExceptionLog.Describe(ex);
            TryWriteError($"[Task] Failed [{safe}]");

            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Failed,
                Output = $"Error [{safe}]",
                Metrics = new TaskMetrics
                {
                    Verdict = "FAIL",
                    Issues = [safe],
                },
            };
        }
    }

    /// <summary>
    /// Loads a persisted <see cref="AgentSession"/> from the session client and calls
    /// <see cref="IAgentRunner.SetSession"/> so the next prompt resumes prior context.
    /// Falls back to a fresh session on any error.
    /// </summary>
    private async Task LoadSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var json = await sessionClient!.GetSessionAsync(sessionId, ct);
            if (json == null)
            {
                _log.Info($"No persisted session found for '{sessionId}' — starting fresh");
                agentRunner.SetSession(null);
                return;
            }

            var session = JsonSerializer.Deserialize<AgentSession>(json, AIJsonUtilities.DefaultOptions);
            agentRunner.SetSession(session);
            _log.Info($"Restored session '{sessionId}' ({session?.MessageHistory?.Count ?? 0} history messages)");
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) throw;
            _log.Warn($"Failed to load session '{sessionId}': {ex.Message} — starting fresh");
            agentRunner.SetSession(null);
        }
    }

    /// <summary>
    /// Serializes the current <see cref="AgentSession"/> from the agent runner and saves it
    /// via the session client. Logs a warning on error but never throws.
    /// </summary>
    private async Task SaveSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var session = agentRunner.GetSession() as AgentSession;
            if (session == null)
            {
                _log.Info($"No session to save for '{sessionId}'");
                return;
            }

            var json = JsonSerializer.Serialize(session, AIJsonUtilities.DefaultOptions);
            await sessionClient!.SaveSessionAsync(sessionId, json, ct);
            _log.Info($"Saved session '{sessionId}' ({session.MessageHistory?.Count ?? 0} history messages)");
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to save session '{sessionId}': {ex.Message}");
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
            if (!await _git.HasUncommittedChangesAsync(workDir, ct))
                return previousOutput;

            _log.Info($"Working directory has uncommitted changes (attempt {attempt + 1}/{maxRetries}), prompting Copilot to commit...");

            var commitPrompt = """
                Your working directory has uncommitted changes. Please:
                1. Run `git add -A` to stage all changes
                2. Run `git commit` with a descriptive message summarizing what you did
                3. Run `git status` to verify the working directory is clean

                Do NOT push — the infrastructure handles pushing.
                """;

            var cleanupOutput = await agentRunner.SendPromptAsync(commitPrompt, workDir, ct);
            previousOutput += "\n\n[Auto-commit prompt]\n" + cleanupOutput;
        }

        if (await _git.HasUncommittedChangesAsync(workDir, ct))
            _log.Error("Working directory still dirty after commit retries — uncommitted changes may be lost");

        return previousOutput;
    }

    /// <summary>
    /// If the tester didn't call <c>report_test_results</c>, re-prompts Copilot in the same
    /// session to report structured metrics via the tool call. No retry — single prompt.
    /// </summary>
    private async Task<string> EnsureTestMetricsReportedAsync(
        string previousOutput, string workDir, CancellationToken ct)
    {
        _log.Info("Tester did not call report_test_results — prompting to report metrics");

        var metricsPrompt = """
            You have not yet reported your test results. You MUST call the `report_test_results` tool
            now with the final aggregated test counts from your test run.

            Extract the numbers from the test output you already produced and call the tool with:
            - verdict: "PASS" if all tests passed, "FAIL" if any failed
            - totalTests: total number of tests
            - passedTests: number that passed
            - failedTests: number that failed
            - coveragePercent: coverage percentage, or -1 if not available
            - buildSuccess: true if the build succeeded
            - issues: array of issue strings, or empty array if none

            Call the tool now. Do not explain — just call it.
            """;

        var metricsOutput = await agentRunner.SendPromptAsync(metricsPrompt, workDir, ct);
        previousOutput += "\n\n[Test metrics enforcement]\n" + metricsOutput;

        if (agentRunner.LastTestReport is null)
            _log.Error("Tester still did not report test metrics after prompt — falling back to text parsing");
        else
            _log.Info($"Tester reported metrics: {agentRunner.LastTestReport.Verdict}, " +
                      $"{agentRunner.LastTestReport.PassedTests}/{agentRunner.LastTestReport.TotalTests} passed");

        return previousOutput;
    }

    /// <summary>
    /// Checks all *.agents.md files in the config repo agents folder against the character limit.
    /// If any file exceeds the limit, re-prompts Copilot in the same session to condense it.
    /// After max retries, discards all changes to keep the config repo clean.
    /// </summary>
    private async Task<string> EnsureAgentsMdWithinLimitsAsync(string previousOutput, CancellationToken ct)
    {
        if (!Directory.Exists(_configAgentsDir))
            return previousOutput;

        for (var attempt = 0; attempt < WorkerConstants.AgentsMdMaxRetries; attempt++)
        {
            var violations = GetAgentsMdViolations();
            if (violations.Count == 0)
                return previousOutput;

            _log.Info($"Agents.md size check (attempt {attempt + 1}/{WorkerConstants.AgentsMdMaxRetries}): " +
                      $"{violations.Count} file(s) over {WorkerConstants.AgentsMdMaxCharacters} chars");

            var violationDetails = string.Join("\n", violations.Select(v =>
                $"  - {v.FileName}: {v.CharCount} characters (limit: {WorkerConstants.AgentsMdMaxCharacters})"));

            var condensePrompt = $"""
                The following agents.md file(s) exceed the {WorkerConstants.AgentsMdMaxCharacters}-character limit:
                {violationDetails}

                Please condense each file to fit within {WorkerConstants.AgentsMdMaxCharacters} characters.
                Prioritize the most impactful rules and remove less important content.
                Do NOT add new content — only condense what is there.
                """;

            var condenseOutput = await agentRunner.SendPromptAsync(condensePrompt, _configAgentsDir, ct);
            previousOutput += "\n\n[Agents.md size enforcement]\n" + condenseOutput;
        }

        // Final check after all retries
        var remaining = GetAgentsMdViolations();
        if (remaining.Count > 0)
        {
            var fileNames = string.Join(", ", remaining.Select(v => $"{v.FileName} ({v.CharCount} chars)"));
            _log.Error($"Agents.md still over limit after {WorkerConstants.AgentsMdMaxRetries} retries: {fileNames}. Discarding all changes.");

            // Discard all agents.md changes to keep config repo clean
            await _git.RunGitCommandAsync(_configRepoDir, "checkout -- agents/", ct);
            previousOutput += $"\n\n[Agents.md changes discarded — files still over {WorkerConstants.AgentsMdMaxCharacters}-char limit after {WorkerConstants.AgentsMdMaxRetries} retries: {fileNames}]";
        }

        return previousOutput;
    }

    /// <summary>
    /// Returns a list of *.agents.md files that exceed the character limit.
    /// </summary>
    private List<(string FileName, int CharCount)> GetAgentsMdViolations()
    {
        var violations = new List<(string FileName, int CharCount)>();

        foreach (var file in Directory.GetFiles(_configAgentsDir, "*.agents.md"))
        {
            var content = File.ReadAllText(file);
            if (content.Length > WorkerConstants.AgentsMdMaxCharacters)
                violations.Add((Path.GetFileName(file), content.Length));
        }

        return violations;
    }

    /// <summary>
    /// Pulls the latest changes from the config repo so the improver works on fresh agents.md files.
    /// The config repo is cloned at container startup by entrypoint.sh.
    /// </summary>
    private async Task PullConfigRepoAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(_configRepoDir, ".git")))
        {
            _log.Info("Config repo not found — improver will work without it");
            return;
        }

        _log.Info("Pulling latest config repo for improver...");
        var (exitCode, stdout, stderr) = await _git.RunGitCommandAsync(
            _configRepoDir, "pull --ff-only", ct);

        if (exitCode == 0)
            _log.Info($"Config repo up to date: {stdout.Trim()}");
        else
            _log.Error($"Config repo pull failed (exit {exitCode}): {stderr.Trim()}");
    }

    /// <summary>
    /// Commits and pushes any changes the improver made to *.agents.md files in the config repo.
    /// Only stages files in the agents/ subfolder to prevent accidental changes elsewhere.
    /// </summary>
    private async Task<GitChangeSummary> CommitAndPushConfigRepoAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(_configRepoDir, ".git")))
            return new GitChangeSummary();

        // Only stage agents/*.agents.md — defense-in-depth to prevent touching other files
        var (addExit, _, addErr) = await _git.RunGitCommandAsync(
            _configRepoDir, "add agents/*.agents.md", ct);
        if (addExit != 0)
        {
            _log.Error($"git add failed: {addErr.Trim()}");
            return new GitChangeSummary();
        }

        // Check if there are staged changes.
        // `-z` gives NUL-delimited, UNQUOTED paths so filenames with unusual characters survive.
        var (diffExit, diffOut, _) = await _git.RunGitCommandAsync(
            _configRepoDir, "diff --cached --name-only -z", ct);
        if (diffExit != 0 || string.IsNullOrWhiteSpace(diffOut))
        {
            _log.Info("No agents.md changes to commit");
            return new GitChangeSummary();
        }

        var changedFiles = diffOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var filesChanged = changedFiles.Length;

        // Diagnostic path list for the orchestrator. The config repo is a SINGLE repository,
        // so paths stay plain relative (no `repoName:` qualification). Capped by the single
        // named constant; the cap never inserts a synthetic truncation marker.
        List<string> cappedPaths = changedFiles.Length > GitOperations.ChangedFilesMaxPaths
            ? [.. changedFiles.Take(GitOperations.ChangedFilesMaxPaths)]
            : [.. changedFiles];

        // The `-z` query returns UNQUOTED paths, so a legal staged filename may contain
        // newlines, tabs, ESC or Unicode line separators. Bound the number of paths shown and
        // sanitize each one so a filename can never forge extra worker log lines. The
        // `(+N more)` suffix is log formatting only and never enters `cappedPaths`.
        var displayPaths = changedFiles.Length > ImproverLogMaxPaths
            ? changedFiles[..ImproverLogMaxPaths]
            : changedFiles;
        _log.Info($"Improver changed {filesChanged} file(s): " +
                  $"{LogSanitizer.FormatPathList(displayPaths, filesChanged)}");

        // Commit
        var (commitExit, commitOut, commitErr) = await _git.RunGitCommandAsync(
            _configRepoDir, "commit -m \"Improve agents.md files (automated by CopilotHive Improver)\"", ct);
        if (commitExit != 0)
        {
            _log.Error($"git commit failed: {commitErr.Trim()}");
            return new GitChangeSummary { FilesChanged = filesChanged, ChangedFiles = cappedPaths };
        }

        _log.Info($"Committed: {commitOut.Trim()}");

        // Pull (merge orchestrator's goals/metrics commits) then push
        var pushed = false;
        try
        {
            var (pullExit, pullOut, pullErr) = await _git.RunGitCommandAsync(
                _configRepoDir, "pull --no-rebase", ct);
            if (pullExit != 0)
            {
                _log.Error($"git pull failed: {pullErr.Trim()}");
                // Abort any in-progress merge and try force-pushing our commit
                await _git.RunGitCommandAsync(_configRepoDir, "merge --abort", ct);
            }

            var (pushExit, _, pushErr) = await _git.RunGitCommandAsync(
                _configRepoDir, "push", ct);
            if (pushExit != 0)
            {
                _log.Error($"git push failed: {pushErr.Trim()}");
                return new GitChangeSummary { FilesChanged = filesChanged, ChangedFiles = cappedPaths };
            }

            pushed = true;
            _log.Info("Pushed config repo changes");
        }
        catch (Exception ex)
        {
            _log.Error($"Push failed: {ex.Message}");
        }

        return new GitChangeSummary
        {
            FilesChanged = filesChanged,
            Pushed = pushed,
            ChangedFiles = cappedPaths,
        };
    }
}
