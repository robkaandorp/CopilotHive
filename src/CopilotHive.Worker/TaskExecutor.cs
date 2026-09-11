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
    /// The config-repo git seam (slices 2c-b1b/2c-b1c/2c-b2/2c-b3). When non-null, EVERY
    /// config-repo git command is routed through it in TOKENIZED form; when null, the LEGACY
    /// opaque-argument path via <see cref="IGitOperations.RunGitCommandAsync"/> applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam is INJECTED (see the internal testing constructor) — never constructed here and
    /// never disposed here, so <see cref="TaskExecutor"/> does not become
    /// <see cref="IDisposable"/>. The PUBLIC constructor leaves this null, so production keeps
    /// the legacy routing; the production activation is a later slice.
    /// </para>
    /// <para>
    /// <b>The askpass content contract.</b> For an eligible transport command the seam injects
    /// <c>GITHUB_CONFIG_REPO_TOKEN</c> plus <c>GIT_ASKPASS</c> (pointing at the credential
    /// helper path supplied to its constructor). The helper script implements git's
    /// <c>$1</c>-prompt protocol and is TOKEN-FREE — the credential is read from the
    /// environment at invocation time, so the script itself may be written to disk with no
    /// secret in it:
    /// </para>
    /// <code>
    /// #!/bin/sh
    /// case "$1" in
    ///   *sername*) printf '%s' "x-access-token" ;;
    ///   *) printf '%s' "$GITHUB_CONFIG_REPO_TOKEN" ;;
    /// esac
    /// </code>
    /// <para>
    /// git invokes the helper once per prompt, passing the prompt text as <c>$1</c>: a
    /// username prompt (matching <c>*sername*</c>) answers with the fixed
    /// <c>x-access-token</c> principal, and every other prompt (the password) answers with the
    /// environment-supplied token. This contract is DOCUMENTED here only — this slice neither
    /// writes the script nor provisions a placeholder for it.
    /// </para>
    /// </remarks>
    private readonly ConfigRepoGitOperations? _configRepoSeam;

    /// <summary>
    /// The CALLER-OWNED integration constructor: identical to the public constructor plus an
    /// INJECTED config-repo git seam that takes over every config-repo git command.
    /// </summary>
    /// <remarks>
    /// It is used by BOTH the <c>WorkerService</c> PRODUCTION assignment path — which builds a
    /// per-assignment seam (with its askpass helper), prepares the config repo through it and
    /// then constructs this executor around it — AND by tests that inject a seam directly. In
    /// every case the seam's lifetime belongs to the CALLER: this type never constructs it and
    /// never disposes it, which is exactly why <see cref="TaskExecutor"/> is not
    /// <see cref="IDisposable"/>.
    /// </remarks>
    internal TaskExecutor(
        IAgentRunner agentRunner,
        IToolCallBridge? toolBridge,
        IGitOperations? gitOperations,
        ISessionClient? sessionClient,
        string configRepoDir,
        ConfigRepoGitOperations configRepoSeam)
        : this(agentRunner, toolBridge, gitOperations, sessionClient, configRepoDir)
    {
        ArgumentNullException.ThrowIfNull(configRepoSeam);
        _configRepoSeam = configRepoSeam;
    }

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
    /// The improver's config-repo commit message. Shared by BOTH dispatch forms — the
    /// tokenized <c>commit -m &lt;message&gt;</c> (no quoting: the seam hands each token to
    /// <c>ArgumentList</c>) and the legacy opaque <c>commit -m "&lt;message&gt;"</c> — so the
    /// two paths can never drift apart.
    /// </summary>
    private const string ImproverCommitMessage =
        "Improve agents.md files (automated by CopilotHive Improver)";

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
    /// Writes to <see cref="Console.Out"/> safely, mirroring <see cref="TryWriteError"/>'s
    /// contract: an informational line NEVER propagates an exception. Shares the same lock so
    /// concurrent tasks cannot interleave partial lines across the two streams.
    /// </summary>
    private static void TryWriteInfo(string message)
    {
        try
        {
            lock (_errorLock)
            {
                Console.WriteLine(message);
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// THE NONTHROWING FINALIZATION LOGGING BOUNDARY (informational).
    /// <para>
    /// <see cref="WorkerLogger.Info"/> writes STRAIGHT to <see cref="Console.Out"/>, so a
    /// disposed or failing writer throws out of the call. Inside finalization that would turn a
    /// verified-clean cleanup into an error path — or escape a catch body and hide the primary
    /// Completed/Failed/Cancelled result. Every finalization diagnostic therefore goes through
    /// this boundary, which preserves the SAME rendered content on the normal path
    /// (<c>[Task] &lt;message&gt;</c>, matching <see cref="WorkerLogger"/>'s category prefix) and
    /// swallows only the writer's own failure.
    /// </para>
    /// <para>
    /// The catch is deliberately BROAD (unlike <see cref="TryWriteError"/>'s two-exception
    /// form): this is a diagnostic sink of last resort whose ONLY contract is that reporting the
    /// outcome can never replace it. A custom <see cref="TextWriter"/> installed by a host — or
    /// by a test — may fail with any exception type, and none of them may reach the caller.
    /// </para>
    /// </summary>
    private static void LogFinalizationInfo(string message)
    {
        try
        {
            TryWriteInfo($"[Task] {message}");
        }
        catch (Exception)
        {
            // Swallowed by design — see the remarks above. A diagnostic must never become the
            // result.
        }
    }

    /// <summary>
    /// THE NONTHROWING FINALIZATION LOGGING BOUNDARY (error). The error counterpart of
    /// <see cref="LogFinalizationInfo"/>: identical rendered content to
    /// <see cref="WorkerLogger.Error"/> (<c>[Task] ERROR: &lt;message&gt;</c>) with the writer's
    /// own failure swallowed, so a cleanup diagnostic can never escape a catch body and replace
    /// the outcome it was meant to describe.
    /// </summary>
    private static void LogFinalizationError(string message)
    {
        try
        {
            TryWriteError($"[Task] ERROR: {message}");
        }
        catch (Exception)
        {
            // Swallowed by design — see LogFinalizationInfo's remarks.
        }
    }

    /// <summary>
    /// PHASE-ROUTED error logging for the helpers shared by preparation, publication and the
    /// step-end cleanup. The CLEANUP phase goes through the nonthrowing
    /// <see cref="LogFinalizationError"/> boundary (a diagnostic there must never become the
    /// outcome); every other phase keeps the EXISTING <see cref="WorkerLogger"/> behavior
    /// unchanged, so preparation and publication semantics are untouched.
    /// </summary>
    private void LogPhaseError(string phase, string message)
    {
        if (string.Equals(phase, CleanupPhase, StringComparison.Ordinal))
            LogFinalizationError(message);
        else
            _log.Error(message);
    }

    /// <summary>
    /// Executes the full lifecycle of a task: cloning repos, branching,
    /// running Copilot, collecting results, and pushing changes.
    /// <para>
    /// For the IMPROVER role the outcome is composed through ONE shared finalization path
    /// (<see cref="FinalizeImproverOutcomeAsync"/>) before this method returns: the awaited,
    /// verified clean-at-step-end cleanup runs on normal/no-change completions, agent/retry
    /// exceptions, publication failures AND requested cancellations alike — every prepared
    /// Improver outcome flows through it. The cleanup carries its own independent finite
    /// budget and never changes a non-Improver task.
    /// </para>
    /// </summary>
    /// <param name="task">The domain task containing prompt, repos, and branch info.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="TaskResult"/> with status, output, and git metrics.</returns>
    public async Task<TaskResult> ExecuteAsync(WorkTask task, CancellationToken ct)
    {
        // Invocation-local state: the trusted restore evidence plus the confirmed publication
        // SHA. Never persisted, never a dirty flag — created fresh per call and discarded.
        var finalization = new ConfigRepoFinalization();

        var outcome = await ExecuteCoreAsync(task, ct, finalization);

        // ONE shared finalization path for every prepared Improver outcome, awaited BEFORE
        // this method returns. Composes cleanup diagnostics WITH the original outcome.
        var finalized = await FinalizeImproverOutcomeAsync(task, outcome, finalization);

        // THE COMMON FINALIZED-RESULT RETURN BOUNDARY. The ORIGINAL ASSIGNED model is stamped
        // here, ONCE, AFTER finalization — so Completed/Failed/Cancelled results from every
        // executor branch (including a cleanup-adjusted Improver outcome) carry it without
        // duplicating an initializer on each of the many returns above. The value is the
        // assignment's own model VERBATIM: never the runner's provider-stripped display value,
        // never an environment default, never the actual provider response model, and never
        // trimmed or normalized. A runtime-null assigned model becomes the empty string
        // ("unknown/empty"), which the wire mapping still transmits with explicit presence.
        //
        // No result is synthesized here: when execution or finalization throws, this boundary
        // is never reached and the exception propagates exactly as before.
        return finalized with { Model = task.Model ?? "" };
    }

    /// <summary>
    /// The original execution body, returning the outcome that the shared finalization path
    /// then composes with the step-end cleanup diagnostics.
    /// </summary>
    private async Task<TaskResult> ExecuteCoreAsync(
        WorkTask task, CancellationToken ct, ConfigRepoFinalization finalization)
    {
        var stopwatch = Stopwatch.StartNew();

        // The accumulated agent/retry output, recorded AS EACH SEGMENT ARRIVES so a
        // cancellation or a later retry exception cannot discard evidence the agent already
        // produced. The catch boundaries below compose their diagnostics ONTO this evidence.
        //
        // SCOPED TO THE IMPROVER: every other role keeps its BASE terminal output byte for
        // byte (the bare cancellation notice / the sanitized error alone). The accumulator is
        // constructed DISABLED for them, so it records nothing and composes nothing.
        var agentEvidence = new AgentOutputEvidence(enabled: task.Role == WorkerRole.Improver);

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

                    // The clone URL carries credentials (the orchestrator injects a token into it),
                    // so the log gets the credential-free form. The RAW url is still what is handed
                    // to the git clone below — redaction is a log-construction concern only.
                    _log.Info($"Cloning {repo.Name} from {GitUrlRedactor.Redact(repo.Url)}");
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
                                    _log.Warn($"Checkout failed ({GitUrlRedactor.Redact(ex.Message)}), creating branch from {baseBranch}");
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
                // Improver: restore the dedicated worker config checkout to a VERIFIED, freshly
                // fetched remote baseline before the agent is prompted. A missing repository, a
                // foreign worktree root, a detached/unborn HEAD, a missing or mismatched
                // upstream, a failed fetch, a malformed rev-parse output, or any residual
                // working-tree content after the destructive restore is a TRUTHFUL failure: the
                // agent is never prompted to edit an unverified directory, and an unsuccessful
                // preparation never masquerades as a normal no-change completion. The thrown
                // ConfigRepoPublicationException carries the sanitized stage reason and is
                // mapped by the catch blocks below into TaskOutcome.Failed + FAIL.
                await PrepareConfigRepoBaselineAsync(ct, finalization);
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
                    _log.Warn($"Could not capture iteration start SHA (empty repo?): {GitUrlRedactor.Redact(ex.Message)}");
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

            // RECORD ON ARRIVAL (Improver only — the accumulator is disabled for every other
            // role): the initial agent output is evidence the catch boundaries below must not
            // discard if a later Improver stage is cancelled or throws.
            agentEvidence.Append(copilotOutput);

            // For roles that push code, ensure Copilot committed its changes.
            // If the working directory is dirty, re-prompt Copilot to commit.
            // NON-IMPROVER path: the base string-accumulating form, unchanged.
            if (!isImprover && task.Role != WorkerRole.Reviewer)
            {
                foreach (var (_, dir) in repoDirectories)
                {
                    copilotOutput = await EnsureCleanWorktreeAsync(copilotOutput, dir, ct);
                }
            }

            // For testers: ensure structured test metrics were reported via tool call.
            // If the tester didn't call report_test_results, prompt it to do so.
            // NON-IMPROVER path: the base string-accumulating form, unchanged.
            if (task.Role == WorkerRole.Tester && agentRunner.LastTestReport is null)
            {
                copilotOutput = await EnsureTestMetricsReportedAsync(copilotOutput, primaryWorkDir, ct);
            }

            // Collect git status from each repo and push changes
            GitChangeSummary? aggregatedStatus = null;
            List<string> pushErrors = [];

            // Non-null EXACTLY when this Improver exhausted its guidance-compression retries
            // and therefore published nothing. It carries the generated exhaustion warning and
            // drives the AUTHORITATIVE SKIP verdict selected after the ordinary report/default
            // metric selection below.
            string? improverSkipWarning = null;

            if (isImprover)
            {
                // Enforce character limit on *.agents.md files before committing.
                // Re-prompts Copilot in the same session to condense if over limit, and
                // returns the EXPLICIT limits-satisfied / exhausted decision this branch acts
                // on. Every returned agent segment is already recorded in agentEvidence.
                var limits = await EnsureAgentsMdWithinLimitsAsync(agentEvidence, ct);

                // The accumulated evidence (initial output plus every returned retry segment,
                // verbatim and in order) is the Improver's output on BOTH decisions.
                copilotOutput = agentEvidence.Snapshot;

                if (limits.Satisfied)
                {
                    // Improver: commit and push changes to the config repo agents folder.
                    // The result distinguishes a successful EMPTY staged diff (a genuine
                    // no-change completion) from every FAILED preparation command. A failed
                    // add/diff/commit/pull/push is a publication FAILURE: the accumulated
                    // agent/retry output is preserved verbatim and a sanitized stage-specific
                    // reason is appended, and the throw is mapped to TaskOutcome.Failed + an
                    // authoritative FAIL verdict below — regardless of any test/worker report
                    // or the default Improver PASS.
                    var publication = await CommitAndPushConfigRepoAsync(ct, finalization);
                    if (publication.FailureReason is { } failureReason)
                    {
                        // A failed add/diff/commit/pull/push — AND a failed publication-HEAD
                        // resolution — is a PUBLICATION FAILURE, never a successful no-change
                        // completion. The accumulated agent/retry output and the staged summary
                        // are carried on the exception so the catch below preserves them
                        // VERBATIM and appends only the sanitized stage reason — never
                        // replacing the evidence.
                        throw new ConfigRepoPublicationException(
                            agentEvidence.Snapshot, failureReason, publication.Summary);
                    }

                    aggregatedStatus = publication.Summary;
                }
                else
                {
                    // ── EXHAUSTED GUIDANCE COMPRESSION: publish NOTHING ────────────────
                    // The initial prompt plus the three condensation retries left at least one
                    // *.agents.md file over the limit, so the WHOLE update is skipped: none of
                    // the publication-stage commands (add, staged diff, commit, pull, push)
                    // runs at all — this is not a publication that is merely reported as
                    // "not pushed". The existing preparation fetch already ran, and the shared
                    // finalization below still restores and VERIFIES the baseline.
                    //
                    // TOKEN OBSERVATION: skipping publication removes every token-observing
                    // await from this path, so the execution token is observed EXPLICITLY here.
                    // A requested cancellation (including one requested by the final
                    // condensation response) therefore stays Cancelled/CANCELLED and is never
                    // reported as a benign skip.
                    ct.ThrowIfCancellationRequested();

                    improverSkipWarning = BuildAgentsMdExhaustionWarning(limits.Remaining);

                    // The GENERATED diagnostic is appended AFTER the agent's own evidence,
                    // which is never truncated or sanitized.
                    agentEvidence.Append(improverSkipWarning);
                    copilotOutput = agentEvidence.Snapshot;

                    // No publication happened: no files changed, nothing pushed. Cleanliness is
                    // NOT claimed here — only the finalization below may verify it.
                    aggregatedStatus = new GitChangeSummary();
                }
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
                            // A push failure message embeds git's stderr, which echoes the
                            // credential-bearing remote URL. Redact ONCE here so the identical
                            // text is safe both in the log line and in the task result, which
                            // travels to the orchestrator to be logged and persisted.
                            var pushError = GitUrlRedactor.Redact(
                                $"Push failed for {repo.Name}: {ex.Message}");
                            _log.Error(pushError);
                            pushErrors.Add(pushError);
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

            if (improverSkipWarning is not null)
            {
                // AUTHORITATIVE SKIP for the exhausted Improver, applied AFTER the ordinary
                // report/default selection above so an incidental worker/test report (or the
                // default Improver PASS) can never override it. The existing fields and issues
                // stay where they are meaningful — only the verdict and the summary are forced:
                //   * the warning is added to Issues so the orchestrator sees the reason; and
                //   * the Summary carries the COMPLETE accumulated evidence plus the warning,
                //     so the receiver's nonblank-summary precedence cannot hide the evidence.
                // No fabricated test counts are introduced; the counts that a report genuinely
                // produced are left untouched.
                metrics = metrics with
                {
                    Verdict = "SKIP",
                    Issues = [.. metrics.Issues, improverSkipWarning],
                    Summary = copilotOutput,
                };
            }

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
                // A CONFIRMED publication is reported from the RETAINED evidence, so the
                // Pushed=true fact cannot be lost by anything that ran after the confirmation.
                GitStatus = finalization.PublishedSummary ?? aggregatedStatus ?? new GitChangeSummary(),
                Metrics = metrics,
                IterationStartSha = iterationStartSha,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Real cancellation (e.g., shutdown signal) — propagate as Cancelled.
            // EVIDENCE PRESERVED (IMPROVER ONLY): once the initial prompt has returned, the
            // accumulated initial/retry output is real agent work. A cancellation during size
            // enforcement or publication must not replace it with the bare notice —
            // finalization would then compose its cleanup diagnostics onto an impoverished
            // result. For every OTHER role the accumulator is disabled, so Compose returns the
            // bare "Task was cancelled." notice EXACTLY as the base behavior does. The
            // Cancelled/CANCELLED semantics are unchanged for all roles.
            TryWriteError($"[Task] Cancelled by token: {task.TaskId}");
            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Cancelled,
                Output = agentEvidence.Compose("Task was cancelled."),
                GitStatus = finalization.PublishedSummary,
                Metrics = new TaskMetrics { Verdict = "CANCELLED" },
            };
        }
        catch (OperationCanceledException ex)
        {
            // Not a real cancellation — likely an API timeout or HTTP failure, or an
            // OperationCanceledException surfaced by a config-repo Git command WITHOUT the
            // execution token being cancelled. Treat as a failure so the orchestrator can retry
            // or fail the phase — a config Git cancellation must never masquerade as a normal
            // no-change completion. Requested cancellations keep flowing through the
            // ct.IsCancellationRequested guard above into Cancelled/CANCELLED.
            //
            // SANITIZED: this is the FIRST consuming boundary for a provisioning/client/LLM
            // exception thrown out of IAgentRunner.SendPromptAsync. The raw message can echo a
            // provisioned GH_TOKEN or OLLAMA_API_KEY, and the TaskResult below travels to the
            // orchestrator where it is logged and persisted — so neither the log line nor the
            // result may carry raw exception text. The agent's OWN accumulated output is
            // evidence, not exception text, and is preserved VERBATIM ahead of the diagnostic —
            // FOR THE IMPROVER ONLY. For every other role the accumulator is disabled, so this
            // returns the sanitized diagnostic ALONE, exactly as the base behavior does.
            var safe = SafeExceptionLog.Describe(ex);
            TryWriteError($"[Task] Failed (API timeout/error) [{safe}]");
            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Failed,
                Output = agentEvidence.Compose($"Error: API call failed or timed out [{safe}]"),
                GitStatus = finalization.PublishedSummary,
                Metrics = new TaskMetrics
                {
                    Verdict = "FAIL",
                    Issues = [$"API timeout/error [{safe}]"],
                },
            };
        }
        catch (ConfigRepoPublicationException ex)
        {
            // ORDINARY Improver Git failure (failed preparation/pull/add/diff/commit/pull/push
            // or a push that threw). Authoritative semantics: TaskOutcome.Failed with a FAIL
            // verdict regardless of any test/worker report or the default Improver PASS, and
            // NEVER a SKIP — a genuine Git/infrastructure failure is not guidance exhaustion.
            //
            // SANITIZED: the reason was built from RenderForLog/SafeExceptionLog.Describe at the
            // stage boundary, so no raw exception text, credential, or control character reaches
            // the log or the persisted result.
            TryWriteError($"[Task] Failed (config repo Git) [{ex.Reason}]");

            // Best-effort session save for the accumulated context after the agent has run.
            if (sessionClient != null && !string.IsNullOrEmpty(task.SessionId))
                await SaveSessionAsync(task.SessionId, ct);

            // The wrapper at the publication call site carries the accumulated output; a
            // preparation failure (thrown before the agent ran) carries none, so the
            // accumulated evidence is the fallback. Either way the evidence is never replaced.
            var preserved = ex.PreservedOutput.Length > 0
                ? ex.PreservedOutput
                : agentEvidence.Snapshot;

            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Failed,
                // The accumulated agent/retry output is preserved VERBATIM (Git-log sanitization
                // is never applied to the agent's own output) with the sanitized stage-specific
                // reason appended — the evidence is never replaced by the error alone.
                Output = $"{preserved}\n\n[Config Repo Git Failure]\n{ex.Reason}",
                // A CONFIRMED publication survives a later failure: the retained Pushed=true
                // evidence wins over a summary that predates the confirmation.
                GitStatus = finalization.PublishedSummary ?? ex.Summary ?? new GitChangeSummary(),
                Metrics = new TaskMetrics
                {
                    Verdict = "FAIL",
                    Issues = [ex.Reason],
                },
            };
        }
        catch (Exception ex)
        {
            // SANITIZED for the same reason as the OperationCanceledException catch above:
            // this is the first boundary that consumes an exception originating at the
            // provisioning / LLM-client / LLM-HTTP layer, and the TaskResult is transmitted to
            // the orchestrator for logging and persistence. The agent's OWN accumulated output
            // is evidence, not exception text, and is preserved VERBATIM ahead of the
            // diagnostic — a later retry exception must not discard it. IMPROVER ONLY: for
            // every other role the accumulator is disabled, so this returns the sanitized
            // diagnostic ALONE, exactly as the base behavior does.
            var safe = SafeExceptionLog.Describe(ex);
            TryWriteError($"[Task] Failed [{safe}]");

            return new TaskResult
            {
                TaskId = task.TaskId,
                Status = TaskOutcome.Failed,
                Output = agentEvidence.Compose($"Error [{safe}]"),
                // A CONFIRMED publication survives even when a later step (a failing log
                // write, a failing session save) throws into this handler.
                GitStatus = finalization.PublishedSummary,
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
            _log.Warn($"Failed to load session '{sessionId}': {GitUrlRedactor.Redact(ex.Message)} — starting fresh");
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
            _log.Warn($"Failed to save session '{sessionId}': {GitUrlRedactor.Redact(ex.Message)}");
        }
    }

    /// <summary>
    /// Checks if the working directory has uncommitted changes and, if so, re-prompts Copilot
    /// to stage and commit them. Retries up to <paramref name="maxRetries"/> times.
    /// Returns the accumulated Copilot output including any cleanup conversation.
    /// <para>
    /// NON-IMPROVER ONLY (coder/tester paths), so this keeps its BASE string-accumulating
    /// form verbatim — including the empty-initial-output formatting. The Improver's
    /// evidence accumulator deliberately does not participate here.
    /// </para>
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
    /// <para>
    /// NON-IMPROVER ONLY (the tester path), so this keeps its BASE string-accumulating form
    /// verbatim — including the empty-initial-output formatting.
    /// </para>
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
    /// The EXPLICIT decision the size-enforcement helper hands back to the Improver branch:
    /// either the limits are SATISFIED (publication may proceed) or the guidance-compression
    /// retries were EXHAUSTED with at least one file still over the limit (publication is
    /// skipped entirely). There is no third, implicit state and no rollback side effect.
    /// </summary>
    /// <param name="Satisfied">
    /// <c>true</c> when NO <c>*.agents.md</c> file exceeds the limit at the decision point —
    /// including a repair achieved by the THIRD retry, which is success, not exhaustion.
    /// </param>
    /// <param name="Remaining">
    /// The files still over the limit when <paramref name="Satisfied"/> is <c>false</c>, with
    /// their character counts. Empty when the limits are satisfied.
    /// </param>
    private sealed record AgentsMdLimitDecision(
        bool Satisfied, IReadOnlyList<(string FileName, int CharCount)> Remaining)
    {
        /// <summary>The satisfied decision — nothing remains over the limit.</summary>
        public static readonly AgentsMdLimitDecision LimitsSatisfied = new(true, []);
    }

    /// <summary>
    /// Checks all *.agents.md files in the config repo agents folder against the character limit.
    /// If any file exceeds the limit, re-prompts Copilot in the same session to condense it:
    /// the check runs BEFORE each retry and once more AFTER the third retry, so the agent
    /// receives the initial prompt plus AT MOST three condensation prompts.
    /// <para>
    /// Returns the EXPLICIT <see cref="AgentsMdLimitDecision"/> the caller acts on. This helper
    /// performs NO rollback and issues NO git command of its own: when the retries are
    /// exhausted the caller skips the entire update — including otherwise-valid edits to files
    /// that are within the limit — and the shared finalization restores and VERIFIES the
    /// baseline afterwards.
    /// </para>
    /// <para>
    /// Each condensation segment is recorded into <paramref name="evidence"/> AS IT ARRIVES, so
    /// a cancellation or throw during a later retry cannot discard the segments the agent
    /// already produced.
    /// </para>
    /// </summary>
    private async Task<AgentsMdLimitDecision> EnsureAgentsMdWithinLimitsAsync(
        AgentOutputEvidence evidence, CancellationToken ct)
    {
        if (!Directory.Exists(_configAgentsDir))
            return AgentsMdLimitDecision.LimitsSatisfied;

        for (var attempt = 0; attempt < WorkerConstants.AgentsMdMaxRetries; attempt++)
        {
            var violations = GetAgentsMdViolations();
            if (violations.Count == 0)
                return AgentsMdLimitDecision.LimitsSatisfied;

            _log.Info($"Agents.md size check (attempt {attempt + 1}/{WorkerConstants.AgentsMdMaxRetries}): " +
                      $"{violations.Count} file(s) over {WorkerConstants.AgentsMdMaxCharacters} chars");

            var violationDetails = string.Join("\n", violations.Select(v =>
                $"  - {v.FileName}: {v.CharCount} characters (limit: {WorkerConstants.AgentsMdMaxCharacters})"));

            var condensePrompt = $"""
                The following agents.md file(s) exceed the {WorkerConstants.AgentsMdMaxCharacters}-character limit:
                {violationDetails}

                Please bring each file back within {WorkerConstants.AgentsMdMaxCharacters} characters
                using the append-new/compress-old policy:
                - Work from the TOP of the existing material downward: first consolidate and compress
                  the older rules, then, only if that is still not enough, remove the oldest material
                  that is redundant or obsolete.
                - Never meet the limit by truncating a whole file: do not cut the file short, do not
                  drop its remaining content wholesale, and do not replace it with a stub. Reduce
                  from the TOP of the older material downward instead.
                - Leave the lessons you appended in this session untouched — do not edit, reword,
                  reorder or delete them.
                - Do NOT add new content or new lessons on this pass — only the older, already
                  existing material may change.
                - Do not weaken or drop protected safety constraints (git workflow, test
                  requirements, output-format compliance) and do not degrade guidance into cryptic
                  abbreviations or symbol-heavy shorthand — keep readable ordinary-language bullets.
                - The compressed file must stay readable to a new reader: keep useful headings and
                  concise ordinary-language bullets so someone opening the file for the first time
                  can follow it.
                - If the protected content genuinely cannot fit within the limit, stop and report
                  that blocker in your response instead of sacrificing protected content.
                """;

            var condenseOutput = await agentRunner.SendPromptAsync(condensePrompt, _configAgentsDir, ct);
            evidence.Append("[Agents.md size enforcement]\n" + condenseOutput);
        }

        // Final check after all retries: a repair achieved by the THIRD retry is SUCCESS.
        var remaining = GetAgentsMdViolations();
        if (remaining.Count == 0)
            return AgentsMdLimitDecision.LimitsSatisfied;

        _log.Error($"Agents.md still over limit after {WorkerConstants.AgentsMdMaxRetries} retries: " +
                   $"{RenderRemainingViolationsForLog(remaining)}. Skipping the guidance update.");

        return new AgentsMdLimitDecision(false, remaining);
    }

    /// <summary>
    /// Renders the still-violating filenames with their character counts for a LOG line. The
    /// filenames come from the filesystem and are therefore untrusted, so each one is
    /// SANITIZED with the shared helper — the counts are integers and need none.
    /// </summary>
    private static string RenderRemainingViolationsForLog(
        IReadOnlyList<(string FileName, int CharCount)> remaining) =>
        string.Join(", ", remaining.Select(v =>
            $"{LogSanitizer.SanitizePath(v.FileName)} ({v.CharCount} chars)"));

    /// <summary>
    /// Builds the GENERATED exhaustion warning appended to the agent's own (never truncated,
    /// never sanitized) evidence and carried in the SKIP metrics. It identifies the exhaustion,
    /// the remaining filenames with their character counts, the configured character limit, the
    /// retry count, and that publication is SKIPPED. Untrusted filenames are sanitized with the
    /// existing helper. Cleanup is deliberately NOT claimed here — only the shared finalization
    /// may verify it.
    /// </summary>
    private static string BuildAgentsMdExhaustionWarning(
        IReadOnlyList<(string FileName, int CharCount)> remaining)
    {
        var files = string.Join("\n", remaining.Select(v =>
            $"  - {LogSanitizer.SanitizePath(v.FileName)}: {v.CharCount} characters " +
            $"(limit: {WorkerConstants.AgentsMdMaxCharacters})"));

        return $"""
            [WARNING: agents.md guidance update SKIPPED — compression retries exhausted]
            After the initial prompt and {WorkerConstants.AgentsMdMaxRetries} condensation retries the following
            file(s) still exceed the {WorkerConstants.AgentsMdMaxCharacters}-character limit:
            {files}
            No agents.md change was published: the entire guidance update was skipped, including
            edits to files that are within the limit. No add, staged diff, commit, pull or push
            was performed for this Improver run.
            """;
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
    /// The SINGLE dispatch for every config-repo git command. When the seam is injected the
    /// TOKENIZED form is validated and executed by <see cref="ConfigRepoGitOperations"/>;
    /// otherwise the LEGACY opaque form is handed to <see cref="IGitOperations.RunGitCommandAsync"/>
    /// and its tuple is mapped DETERMINISTICALLY onto the same
    /// <see cref="ConfigRepoOpResult"/> shape, so every caller sees one uniform result
    /// regardless of the path taken.
    /// </summary>
    /// <param name="tokenizedForm">The seam input: one token per argument, never re-parsed.</param>
    /// <param name="legacyOpaqueForm">
    /// The EXACT legacy argument string (carried alongside the tokenized form so nothing is
    /// reconstructed from it — the two forms are not always mechanically related, e.g. the
    /// tokenized <c>push origin HEAD</c> versus the legacy bare <c>push</c>).
    /// </param>
    /// <param name="ct">Cancellation token, forwarded verbatim on BOTH paths.</param>
    private async Task<ConfigRepoOpResult> RunConfigRepoCommandAsync(
        IReadOnlyList<string> tokenizedForm, string legacyOpaqueForm, CancellationToken ct)
    {
        if (_configRepoSeam is not null)
            return await _configRepoSeam.RunConfigRepoCommandAsync(tokenizedForm, _configRepoDir, ct);

        var (exitCode, stdout, stderr) = await _git.RunGitCommandAsync(
            _configRepoDir, legacyOpaqueForm, ct);

        return new ConfigRepoOpResult(
            Success: exitCode == 0,
            ExitCode: exitCode,
            Stdout: stdout,
            SanitizedError: string.IsNullOrWhiteSpace(stderr) ? "" : stderr.Trim());
    }

    /// <summary>
    /// Renders an untrusted config-repo result field for a log line: TRIM, then the
    /// credential REDACTION, then the control-character SANITIZATION. TaskExecutor's logging
    /// is the sanitization boundary — the seam's results (and raw git output) may carry
    /// newlines, ESC or Unicode line separators that would otherwise forge log lines.
    /// </summary>
    private static string RenderForLog(string value) =>
        LogSanitizer.SanitizeText(GitUrlRedactor.Redact(value.Trim()));

    /// <summary>
    /// The outcome of the Improver's config-repo publication attempt. A non-null
    /// <see cref="FailureReason"/> is a FAILURE; <c>null</c> means the attempt concluded
    /// truthfully — either a confirmed push (<see cref="GitChangeSummary.Pushed"/>) or a
    /// successful empty staged diff (a genuine no-change completion).
    /// </summary>
    private sealed record ConfigRepoPublication(GitChangeSummary Summary, string? FailureReason);

    /// <summary>The preparation phase label — the pre-run baseline restore.</summary>
    private const string PreparationPhase = "preparation";

    /// <summary>The step-end cleanup (finalization) phase label used in every diagnostic.</summary>
    private const string CleanupPhase = "cleanup";

    /// <summary>The publication phase label for the publication-HEAD resolution diagnostics.</summary>
    private const string PublicationPhase = "publication";

    /// <summary>
    /// The INDEPENDENT finite cancellation budget for the whole step-end cleanup sequence.
    /// This is deliberately NOT the (possibly already-cancelled) execution token and NOT an
    /// unbounded wait: one 30-second cooperative budget covers the cleanup's trust
    /// revalidation, the destructive restore and the verification. It is a managed,
    /// cooperative budget — never a process-crash or absolute OS-call termination guarantee,
    /// and never a Git-subprocess supervision mechanism.
    /// </summary>
    private static readonly TimeSpan ConfigRepoCleanupBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The INVOCATION-LOCAL state one Improver execution carries from preparation through
    /// finalization. It is created fresh per <see cref="ExecuteAsync"/> call, never persisted,
    /// and holds no dirty/manual-clear flag — only the trusted restore evidence.
    /// <para>
    /// <see cref="BaselineSha"/> and <see cref="Branch"/> are captured from the freshly
    /// fetched, validated target BEFORE the first destructive preparation reset, so a
    /// preparation that fails or is cancelled after that point still leaves finalization a
    /// safe target. Before any validation establishes a target, both stay null and cleanup is
    /// NEVER performed (fail-before-mutation is preserved — no target is ever guessed).
    /// </para>
    /// <para>
    /// <see cref="PublishedSha"/> and <see cref="PublishedSummary"/> are set ONLY by a confirmed
    /// exit-zero push, TOGETHER and immediately after that confirmation (see
    /// <see cref="RetainConfirmedPublication"/>), before any fallible logging/session/result
    /// work. <see cref="PublishedSha"/> selects the restore target for finalization;
    /// <see cref="PublishedSummary"/> is the retained <c>Pushed=true</c> evidence every later
    /// outcome is constructed from, so a throwing post-push log can never yield a result that
    /// omits the confirmed publication. A failed or interrupted push leaves BOTH null: the
    /// remote state stays uncertain and the LOCAL reset to the baseline neither rolls back nor
    /// establishes the remote result.
    /// </para>
    /// </summary>
    private sealed class ConfigRepoFinalization
    {
        /// <summary>The fetched, validated baseline SHA captured before the first reset.</summary>
        public string? BaselineSha { get; set; }

        /// <summary>The validated attached branch captured before the first reset.</summary>
        public string? Branch { get; set; }

        /// <summary>The confirmed publication HEAD, set immediately after an exit-zero push.</summary>
        public string? PublishedSha { get; private set; }

        /// <summary>
        /// The retained <c>Pushed=true</c> summary, materialized in the SAME step as
        /// <see cref="PublishedSha"/>. Non-null exactly when a push was confirmed.
        /// </summary>
        public GitChangeSummary? PublishedSummary { get; private set; }

        /// <summary>
        /// Retains BOTH publication facts ATOMICALLY with respect to the caller's subsequent
        /// fallible work: after this returns, the confirmed publication is recorded and any
        /// later throw (a failing log write, a failing session save) can no longer produce an
        /// outcome that omits it. Callers construct their result from
        /// <see cref="PublishedSummary"/> rather than recomputing it.
        /// </summary>
        public void RetainConfirmedPublication(string publishedSha, GitChangeSummary publishedSummary)
        {
            PublishedSha = publishedSha;
            PublishedSummary = publishedSummary;
        }

        /// <summary>Whether a push was CONFIRMED for this invocation.</summary>
        public bool HasConfirmedPublication => PublishedSha is not null && PublishedSummary is not null;

        /// <summary>
        /// One-shot guard: the shared finalization path runs at most ONCE per invocation. A
        /// cleanup failure is terminal — there is no retry loop and no second attempt.
        /// </summary>
        public bool Finalized { get; set; }

        /// <summary>Whether a safe restore target exists — the precondition for any cleanup.</summary>
        public bool HasTarget => BaselineSha is not null && Branch is not null;
    }

    /// <summary>
    /// The INVOCATION-LOCAL accumulator for the agent's returned output, recorded segment by
    /// segment AS EACH ARRIVES rather than only when a helper returns.
    /// <para>
    /// SCOPE — IMPROVER ONLY. The Improver's post-prompt stages (size enforcement, publication)
    /// can be cancelled or throw AFTER the initial prompt has already returned real agent output
    /// and after one or more retry segments have been produced. Without this accumulator that
    /// evidence lives only in a local string inside the try block, so the catch boundaries
    /// replace it with <c>"Task was cancelled."</c> or a bare error line — and finalization then
    /// composes its cleanup diagnostics onto an impoverished result. Recording each segment on
    /// arrival keeps the accumulated initial/retry output available to EVERY catch boundary.
    /// </para>
    /// <para>
    /// Every OTHER role (Coder, Tester, Reviewer, …) keeps its BASE behavior byte for byte: a
    /// requested cancellation returns the bare <c>"Task was cancelled."</c> notice and a failure
    /// returns the sanitized diagnostic ALONE. The scope is enforced STRUCTURALLY by
    /// <see cref="_enabled"/> rather than at each call site: a disabled accumulator ignores
    /// every <see cref="Append"/> and returns the caller's diagnostic UNCHANGED from
    /// <see cref="Compose"/>, so no future call site can reintroduce the leak by forgetting a
    /// guard.
    /// </para>
    /// </summary>
    private sealed class AgentOutputEvidence(bool enabled)
    {
        private readonly System.Text.StringBuilder _builder = new();
        private readonly bool _enabled = enabled;

        /// <summary>
        /// Appends one arrived segment verbatim (no sanitization — this is the agent's own
        /// output). A DISABLED (non-Improver) accumulator records nothing at all.
        /// </summary>
        public void Append(string segment)
        {
            if (!_enabled || string.IsNullOrEmpty(segment))
                return;

            if (_builder.Length > 0)
                _builder.Append("\n\n");

            _builder.Append(segment);
        }

        /// <summary>Whether any agent output has been recorded yet.</summary>
        public bool HasOutput => _builder.Length > 0;

        /// <summary>The accumulated output so far, VERBATIM.</summary>
        public string Snapshot => _builder.ToString();

        /// <summary>
        /// Composes the accumulated evidence with a terminal diagnostic: the evidence FIRST,
        /// the diagnostic appended — never the diagnostic alone when evidence exists.
        /// <para>
        /// A DISABLED (non-Improver) accumulator has no evidence and therefore returns
        /// <paramref name="diagnostic"/> EXACTLY as given, which is the base behavior.
        /// </para>
        /// </summary>
        public string Compose(string diagnostic) =>
            HasOutput ? $"{Snapshot}\n\n{diagnostic}" : diagnostic;
    }

    /// <summary>
    /// An ordinary Improver config-repo Git failure. Carries the accumulated agent/retry output
    /// VERBATIM (Git-log sanitization is never applied to it), the sanitized stage-specific
    /// failure reason, and the diagnostic <see cref="GitChangeSummary"/> (when the staged-file
    /// list was already computed) so the catch boundary can preserve the evidence instead of
    /// replacing it.
    /// </summary>
    private sealed class ConfigRepoPublicationException(
        string preservedOutput, string reason, GitChangeSummary? summary = null)
        : InvalidOperationException(reason)
    {
        public string PreservedOutput { get; } = preservedOutput;
        public string Reason { get; } = reason;
        public GitChangeSummary? Summary { get; } = summary;
    }

    /// <summary>
    /// THE SHARED FINALIZATION PATH for every prepared Improver outcome. Awaits the verified
    /// clean-at-step-end cleanup (when a safe restore target exists), then COMPOSES the
    /// cleanup diagnostics WITH — never replacing — the original outcome/evidence:
    /// <list type="bullet">
    ///   <item><description>A normal completion whose cleanup FAILS is returned as
    ///   <see cref="TaskOutcome.Failed"/>/FAIL — cleanliness is never claimed when it cannot
    ///   be verified.</description></item>
    ///   <item><description>Existing failures remain Failed/FAIL with their original
    ///   returned agent/retry output and sanitized reason, with cleanup diagnostics
    ///   APPENDED; <c>Pushed=true</c> survives a confirmed publication even when
    ///   finalization itself fails.</description></item>
    ///   <item><description>A requested execution cancellation remains Cancelled/CANCELLED
    ///   and reports any cleanup failure explicitly.</description></item>
    ///   <item><description>No cleanup exception may escape and hide the primary
    ///   result.</description></item>
    /// </list>
    /// Non-Improver tasks pass through untouched (no target is ever captured for them).
    /// </summary>
    private async Task<TaskResult> FinalizeImproverOutcomeAsync(
        WorkTask task, TaskResult outcome, ConfigRepoFinalization finalization)
    {
        if (task.Role != WorkerRole.Improver || finalization.Finalized || !finalization.HasTarget)
        {
            // No prepared Improver outcome, or nothing was ever prepared: no cleanup target
            // exists, so nothing may be cleaned and nothing is claimed clean. Fail-before-
            // mutation is preserved — an unverified tree is never silently "finalized".
            return outcome;
        }

        finalization.Finalized = true;

        // The independent, finite cooperative budget — deliberately NOT linked to the
        // (possibly already-cancelled) execution token, and not an unbounded wait.
        using var budget = new CancellationTokenSource(ConfigRepoCleanupBudget);

        string? cleanupFailure;
        try
        {
            // The restore target is selected by ACTUAL publication evidence: the confirmed
            // published SHA when publication was confirmed, the captured fetched baseline
            // otherwise. Never current HEAD, never a stale FETCH_HEAD or tracking ref.
            var selectedSha = finalization.PublishedSha ?? finalization.BaselineSha!;
            var branch = finalization.Branch!;
            await FinalizeConfigRepoCleanAsync(budget.Token, selectedSha, branch);
            cleanupFailure = null;
            // NONTHROWING: the success line is a diagnostic, never a gate. A failing writer
            // must not turn a VERIFIED-clean cleanup into a cleanup failure.
            LogFinalizationInfo(
                $"Config repo cleanup verified at {RenderForLog(selectedSha[..Math.Min(selectedSha.Length, 12)])}");
        }
        catch (ConfigRepoPublicationException ex)
        {
            // SANITIZED at the stage boundary (the phase label in the reason is accurate to
            // the cleanup); carried as diagnostics, never thrown onward. The write itself is
            // NONTHROWING so the diagnostic can never escape this catch and hide the primary
            // Completed/Failed/Cancelled result.
            cleanupFailure = ex.Reason;
            LogFinalizationError($"Config repo cleanup failed [{ex.Reason}]");
        }
        catch (Exception ex)
        {
            // SANITIZED: an unexpected cleanup exception (including a cooperative budget
            // expiry surfaced as an OperationCanceledException) is classified — never
            // propagated out of the finalization to hide the primary result. The failure
            // reason is materialized BEFORE the (nonthrowing) write, so the diagnostic and the
            // composed outcome cannot diverge.
            var safe = SafeExceptionLog.Describe(ex);
            cleanupFailure = $"Config repo cleanup failed with an error [{safe}].";
            LogFinalizationError($"Config repo cleanup failed with an error [{safe}]");
        }

        if (cleanupFailure is null)
            return outcome;

        var diagnostic = $"[Config Repo Cleanup Failure]\n{cleanupFailure}";

        // Cancellation keeps its established semantics; the cleanup failure is reported
        // explicitly but never reclassified as an ordinary failure.
        if (outcome.Status == TaskOutcome.Cancelled)
        {
            return outcome with
            {
                Output = outcome.Output.Length == 0 ? diagnostic : outcome.Output + "\n\n" + diagnostic,
                Metrics = outcome.Metrics is null
                    ? null
                    : outcome.Metrics with { Issues = [.. outcome.Metrics.Issues, cleanupFailure] },
            };
        }

        if (outcome.Status == TaskOutcome.Completed)
        {
            // A normal completion whose cleanup failed is a FAILED outcome: the verified-clean
            // claim would otherwise be false. Pushed survives a confirmed publication.
            return outcome with
            {
                Status = TaskOutcome.Failed,
                Output = outcome.Output + "\n\n" + diagnostic,
                Metrics = outcome.Metrics is null
                    ? null
                    : outcome.Metrics with { Verdict = "FAIL", Issues = [.. outcome.Metrics.Issues, cleanupFailure] },
            };
        }

        // An existing failure (Failed): the original output, sanitized reason, GitStatus
        // (including Pushed=true after a confirmed publication) and issues all survive, with
        // the cleanup diagnostics APPENDED — never replacing the evidence.
        return outcome with
        {
            Output = outcome.Output + "\n\n" + diagnostic,
            Metrics = outcome.Metrics is null
                ? null
                : outcome.Metrics with { Issues = [.. outcome.Metrics.Issues, cleanupFailure] },
        };
    }

    /// <summary>
    /// Runs ONE verified clean-at-step-end restore through the shared command dispatch:
    /// revalidate the configured worktree root and the captured branch/upstream identity, then
    /// a checked <c>reset --hard</c> to the SELECTED full SHA (the confirmed publication SHA
    /// when publication was confirmed; the captured fetched baseline otherwise — never current
    /// HEAD, never a stale FETCH_HEAD or tracking ref), EXACTLY one <c>clean -fdx</c>, then a
    /// peeled-HEAD equality check and a strictly-empty verbose status. Every step's failure —
    /// a command error, a timeout, a malformed SHA, a mismatched HEAD or a nonempty status —
    /// fails the cleanup TRUTHFULLY: cleanliness is never claimed without verification.
    /// <para>
    /// Through <see cref="RunPreparationCommandAsync"/> with the CLEANUP phase label (both seam
    /// and legacy routes), the same helpers as preparation. There is no additional push, force
    /// push, or network reconciliation; reflogs, unreachable objects and external
    /// session/credential files are not worktree leftovers. A failed trust validation stops
    /// every further destructive command. A cooperative budget expiry surfaces as an
    /// <see cref="OperationCanceledException"/> and is classified by the finalization
    /// boundary as an ordinary cleanup failure — never a supervision mechanism.
    /// </para>
    /// </summary>
    private async Task FinalizeConfigRepoCleanAsync(CancellationToken ct, string selectedSha, string branch)
    {
        // ── Trust revalidation BEFORE any destructive command ─────────────────
        var toplevel = await RunPreparationCommandAsync(
            ["rev-parse", "--show-toplevel"], "rev-parse --show-toplevel", "worktree root check", ct,
            phase: CleanupPhase);
        VerifyWorktreeRoot(toplevel.Stdout, CleanupPhase);

        var currentBranch = await ResolveAttachedBranchAsync(ct, CleanupPhase);
        if (!string.Equals(currentBranch, branch, StringComparison.Ordinal))
        {
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: "Config repo cleanup rejected: the worktree is no longer on the captured branch.");
        }

        await VerifyUpstreamAsync(currentBranch, ct, CleanupPhase);

        // ── Destructive restore to the SELECTED SHA ───────────────────────────
        await RunPreparationCommandAsync(
            ["reset", "--hard", selectedSha], $"reset --hard {selectedSha}", "reset", ct,
            phase: CleanupPhase);
        await RunPreparationCommandAsync(
            ["clean", "-fdx"], "clean -fdx", "clean", ct, phase: CleanupPhase);

        // ── Verification: HEAD equality, then the strictly empty status ───────
        var restoredSha = await ResolveSingleShaAsync(
            ConfigRepoGitOperations.RevHeadCommit, "post-restore HEAD check", "HEAD", ct,
            phase: CleanupPhase);
        if (!string.Equals(restoredSha, selectedSha, StringComparison.Ordinal))
        {
            LogFinalizationError("Config repo cleanup rejected: HEAD does not match the selected restore SHA after the restore");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: "Config repo cleanup rejected: HEAD does not match the selected restore SHA after the restore.");
        }

        var status = await RunPreparationCommandAsync(
            ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"],
            "status --porcelain=v1 --untracked-files=all --ignored",
            "post-restore status check",
            ct,
            phase: CleanupPhase);
        if (status.Stdout.Length != 0)
        {
            // STRICT EMPTY: the verified-clean oracle is a completely EMPTY status output.
            // Whitespace-only output is NOT clean — it is unrecognized residual output, and
            // a protected nested repository is never forcibly deleted (its residual status is
            // cleanup FAILURE). No second forced clean, no recursive deletion.
            LogFinalizationError($"Config repo cleanup rejected: the working tree is not clean after the restore: {RenderForLog(status.Stdout)}");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo cleanup rejected: the working tree is not clean after the restore: {RenderForLog(status.Stdout)}");
        }
    }

    /// <summary>
    /// Restores the dedicated worker config checkout to a VERIFIED, freshly fetched remote
    /// baseline before the improver is prompted. The config repository is prepared per task
    /// assignment — WorkerService performs the per-assignment preparation (the probe plus the
    /// clone-if-absent) BEFORE this assignment's TaskExecutor runs, and this method operates on
    /// that prepared repository; it never clones and never touches paths outside
    /// <see cref="_configRepoDir"/>.
    /// <para>
    /// THE SEQUENCE (every command result is checked; the first failure stops everything):
    /// <list type="number">
    ///   <item><description>PREFLIGHT, before any mutation: the <c>.git</c> directory must
    ///   exist; <c>rev-parse --show-toplevel</c> must report exactly the configured config repo
    ///   root; <c>rev-parse --verify HEAD^{commit}</c> must yield exactly one full 40/64-hex
    ///   SHA (the bare <c>--verify HEAD</c> would also accept a tag or other non-commit
    ///   object); <c>rev-parse --symbolic-full-name HEAD</c> must yield an ATTACHED
    ///   <c>refs/heads/&lt;branch&gt;</c>; and <c>rev-parse --symbolic-full-name @{upstream}</c>
    ///   must yield exactly <c>refs/remotes/origin/&lt;same branch&gt;</c>. A foreign root, a
    ///   detached/unborn HEAD, a missing upstream, a different remote or a mismatched branch is
    ///   rejected BEFORE any reset/clean and before prompting.</description></item>
    ///   <item><description>FETCH the discovered branch — <c>fetch origin
    ///   refs/heads/&lt;branch&gt;</c>. A failed fetch is a preparation failure: there is NEVER
    ///   a fallback to a previous <c>FETCH_HEAD</c>, a local commit or a remote-tracking ref,
    ///   and no reset is ever issued from stale evidence.</description></item>
    ///   <item><description>Only after THIS fetch succeeded, resolve the baseline with
    ///   <c>rev-parse --verify FETCH_HEAD^{commit}</c>; the single full SHA it yields is the
    ///   ONLY permitted reset target.</description></item>
    ///   <item><description>RESTORE destructively: <c>reset --hard &lt;SHA&gt;</c>, then
    ///   <c>clean -fdx</c>, then (idempotently) recreate the agents working directory, then
    ///   VERIFY that <c>rev-parse --verify HEAD^{commit}</c> equals the captured SHA and that
    ///   the verbose <c>status</c> reports an EMPTY working tree. Only when every check passes
    ///   may the agent run.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// An ordinary dirty/ahead/diverged but structurally valid checkout is RESTORED
    /// automatically — never blocked for manual cleanup — and published remote guidance
    /// survives, because the target is the freshly fetched remote baseline rather than a
    /// deletion of history. A protected nested repository or any other cleanup failure is
    /// reported truthfully instead of escalating: there is no second forced clean, no arbitrary
    /// recursive deletion, no quarantine flag and no remote rollback or force push.
    /// </para>
    /// </summary>
    private async Task PrepareConfigRepoBaselineAsync(CancellationToken ct, ConfigRepoFinalization finalization)
    {
        if (!Directory.Exists(Path.Combine(_configRepoDir, ".git")))
        {
            _log.Error("Config repo not found — refusing to prompt the improver to edit a non-repository directory");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: "Config repo not found — the improver cannot prepare its workspace without the config repository.");
        }

        _log.Info("Preparing config repo baseline for improver...");

        // ── Preflight ─────────────────────────────────────────────────────────────
        var toplevel = await RunPreparationCommandAsync(
            ["rev-parse", "--show-toplevel"], "rev-parse --show-toplevel", "worktree root check", ct);
        VerifyWorktreeRoot(toplevel.Stdout);

        var headSha = await ResolveSingleShaAsync(
            ConfigRepoGitOperations.RevHeadCommit, "HEAD commit check", "HEAD", ct);

        var branch = await ResolveAttachedBranchAsync(ct);
        await VerifyUpstreamAsync(branch, ct);

        _log.Info($"Config repo baseline preflight passed on branch {RenderForLog(branch)} " +
                  $"at {RenderForLog(headSha[..Math.Min(headSha.Length, 12)])}");

        // ── Fetch (the ONLY source of the reset target) ───────────────────────────
        var branchRef = BranchRefPrefix + branch;
        var fetch = await RunPreparationCommandAsync(
            ["fetch", "origin", branchRef],
            $"fetch origin \"{branchRef}\"",
            "fetch",
            ct);

        // git echoes the credential-bearing config-repo remote in stdout too, so the LOG
        // rendering is redacted AND control-character sanitized. The raw value is untouched.
        _log.Info($"Config repo fetched {RenderForLog(branchRef)}: {RenderForLog(fetch.Stdout)}");

        var baselineSha = await ResolveSingleShaAsync(
            ConfigRepoGitOperations.RevFetchHeadCommit, "fetched baseline check", "FETCH_HEAD", ct);

        // ── TRUSTED RESTORE EVIDENCE, retained for finalization ───────────────────
        // The freshly fetched, validated target becomes available to the step-end cleanup
        // BEFORE the first destructive reset: an interrupted or failed partial preparation
        // after this point still leaves finalization a safe, validated target. Before this
        // point nothing is stored, so cleanup never guesses a target.
        finalization.BaselineSha = baselineSha;
        finalization.Branch = branch;
        _log.Info($"Config repo captured restore evidence: branch {RenderForLog(branch)} " +
                  $"baseline {RenderForLog(baselineSha[..Math.Min(baselineSha.Length, 12)])}");

        // ── Destructive restore to the fetched baseline ───────────────────────────
        await RunPreparationCommandAsync(
            ["reset", "--hard", baselineSha], $"reset --hard {baselineSha}", "reset", ct);
        await RunPreparationCommandAsync(["clean", "-fdx"], "clean -fdx", "clean", ct);

        EnsureAgentsDirectoryExists();

        // ── Verification: the restore actually took effect ────────────────────────
        var restoredSha = await ResolveSingleShaAsync(
            ConfigRepoGitOperations.RevHeadCommit, "post-restore HEAD check", "HEAD", ct);
        if (!string.Equals(restoredSha, baselineSha, StringComparison.Ordinal))
        {
            _log.Error("Config repo preparation rejected: HEAD does not match the fetched baseline after the restore");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: "Config repo preparation rejected: HEAD does not match the fetched baseline after the restore.");
        }

        var status = await RunPreparationCommandAsync(
            ["status", "--porcelain=v1", "--untracked-files=all", "--ignored"],
            "status --porcelain=v1 --untracked-files=all --ignored",
            "post-restore status check",
            ct);
        if (status.Stdout.Length != 0)
        {
            // STRICT EMPTY: the verified-clean oracle is a completely EMPTY status output.
            // Whitespace-only output is NOT clean — it is output this preparation does not
            // recognize, so it is reported as a residual dirty tree rather than assumed clean.
            // A protected nested repository or any other residue the single-force clean cannot
            // remove is reported TRUTHFULLY — no second forced clean, no recursive deletion.
            _log.Error($"Config repo preparation rejected: the working tree is not clean after the restore: {RenderForLog(status.Stdout)}");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo preparation rejected: the working tree is not clean after the restore: {RenderForLog(status.Stdout)}");
        }

        _log.Info($"Config repo restored to the fetched baseline {RenderForLog(baselineSha[..Math.Min(baselineSha.Length, 12)])}");
    }

    /// <summary>The only accepted <c>symbolic-full-name HEAD</c> prefix — an ATTACHED branch.</summary>
    private const string BranchRefPrefix = "refs/heads/";

    /// <summary>The only accepted upstream prefix — the ordinary single-origin clone topology.</summary>
    private const string UpstreamRefPrefix = "refs/remotes/origin/";

    /// <summary>
    /// Runs ONE preparation command through the shared <see cref="RunConfigRepoCommandAsync"/>
    /// dispatch (so both the tokenized seam path and the legacy opaque path are exercised) and
    /// converts every failure form — a thrown exception, a non-requested-cancellation
    /// <see cref="OperationCanceledException"/>, or a non-zero result — into the sanitized
    /// preparation failure. A REQUESTED execution cancellation is never converted: it
    /// propagates to <see cref="ExecuteAsync"/>'s <c>ct.IsCancellationRequested</c> guard and
    /// stays Cancelled/CANCELLED.
    /// </summary>
    private async Task<ConfigRepoOpResult> RunPreparationCommandAsync(
        IReadOnlyList<string> tokenizedForm, string legacyOpaqueForm, string stage, CancellationToken ct,
        string phase = PreparationPhase)
    {
        ConfigRepoOpResult result;
        try
        {
            result = await RunConfigRepoCommandAsync(tokenizedForm, legacyOpaqueForm, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // An OCE that is NOT a requested execution cancellation is an ordinary preparation
            // failure (a transport timeout, for example) — never a normal completion. Rendered
            // through SafeExceptionLog.Describe so no raw exception text escapes. A REQUESTED
            // cancellation does not enter this filter and propagates to the outer
            // ct.IsCancellationRequested guard unchanged.
            var safe = SafeExceptionLog.Describe(ex);
            LogPhaseError(phase, $"Config repo {phase} {stage} failed (cancelled) [{safe}]");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} {stage} was interrupted without a requested cancellation [{safe}].");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var safe = SafeExceptionLog.Describe(ex);
            LogPhaseError(phase, $"Config repo {phase} {stage} failed (error) [{safe}]");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} {stage} failed with an error [{safe}].");
        }

        if (result.Success)
            return result;

        // git echoes the credential-bearing config-repo remote in both streams, so the LOG
        // rendering is redacted AND control-character sanitized. The raw values are untouched.
        LogPhaseError(phase, $"Config repo {phase} {stage} failed (exit {result.ExitCode}): {RenderForLog(result.SanitizedError)}");
        throw new ConfigRepoPublicationException(
            preservedOutput: "",
            reason: $"Config repo {phase} {stage} failed (exit {result.ExitCode}): {RenderForLog(result.SanitizedError)}");
    }

    /// <summary>
    /// The LEXICAL trust boundary (the same convention the config-repo seam uses for its
    /// containment check): the reported worktree root, canonicalized, must EQUAL the
    /// canonicalized configured config repo directory. A foreign repository is rejected before
    /// any mutation. This is not a filesystem-sandbox claim.
    /// <para>
    /// The ROOT consumer takes the VERBATIM single-line content: a legitimate configured
    /// repository may live at a path containing INTERNAL whitespace (<c>/tmp/config repo</c>,
    /// <c>C:\Users\Jane Doe\config-repo</c>), and <c>rev-parse --show-toplevel</c> reports that
    /// exact path. Only canonical equality against <see cref="_configRepoDir"/> decides — the
    /// SHA/ref no-whitespace rule deliberately does NOT apply here. PADDED output
    /// (<c>  /tmp/root  </c>) is still rejected by the shared extraction, so this widening
    /// admits internal whitespace ONLY.
    /// </para>
    /// </summary>
    private void VerifyWorktreeRoot(string toplevelStdout, string phase = PreparationPhase)
    {
        var reported = ExactSingleLineContent(toplevelStdout);
        if (reported is null)
        {
            LogPhaseError(phase, $"Config repo {phase} rejected: the worktree root could not be determined");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} rejected: the worktree root could not be determined.");
        }

        string canonicalReported;
        string canonicalConfigured;
        try
        {
            canonicalReported = CanonicalizePath(reported);
            canonicalConfigured = CanonicalizePath(_configRepoDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            var safe = SafeExceptionLog.Describe(ex);
            LogPhaseError(phase, $"Config repo {phase} rejected: the worktree root could not be canonicalized [{safe}]");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} rejected: the worktree root could not be canonicalized [{safe}].");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!canonicalReported.Equals(canonicalConfigured, comparison))
        {
            LogPhaseError(phase, $"Config repo {phase} rejected: the worktree root is not the configured config repository");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} rejected: the worktree root is not the configured config repository.");
        }
    }

    /// <summary>
    /// Canonicalizes a path exactly as the config-repo seam does: the fully-qualified form with
    /// any trailing separator removed (except for a bare root).
    /// </summary>
    private static string CanonicalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.Ordinal))
            return full;

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Runs <c>rev-parse --verify &lt;revision&gt;</c> for one of the two admitted peeled-commit
    /// spellings and requires EXACTLY one non-empty line holding a full 40/64 ASCII-hex SHA
    /// (the seam's own <c>reset</c> SHA domain — no second, weaker validator). Blank,
    /// multi-line (ambiguous) and malformed outputs are rejected.
    /// </summary>
    private async Task<string> ResolveSingleShaAsync(
        string revision, string stage, string label, CancellationToken ct,
        string phase = PreparationPhase)
    {
        var result = await RunPreparationCommandAsync(
            ["rev-parse", "--verify", revision], $"rev-parse --verify {revision}", stage, ct, phase);

        var line = ExactSingleLine(result.Stdout);
        if (line is null || !ConfigRepoGitOperations.IsValidCommitSha(line))
        {
            LogPhaseError(phase, $"Config repo {phase} rejected: {label} did not resolve to a single full commit SHA");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} rejected: {label} did not resolve to a single full commit SHA.");
        }

        return line;
    }

    /// <summary>
    /// Reads the ATTACHED branch from <c>rev-parse --symbolic-full-name HEAD</c>. A detached
    /// HEAD (the bare <c>HEAD</c> output, or any value outside <c>refs/heads/</c>) is rejected;
    /// the branch is never guessed from a default name, <c>origin/HEAD</c>, or a task id.
    /// <para>
    /// The discovered ref is then validated COMPLETELY — the prechecks PLUS the authoritative
    /// <c>git check-ref-format</c> subprocess, via the seam's single shared
    /// <see cref="ConfigRepoGitOperations.ValidateRefCompletelyAsync"/> — BEFORE either
    /// dispatch form is built. The prechecks alone accept refs git itself rejects
    /// (<c>foo.lock</c>, <c>.hidden</c>, <c>foo//bar</c>, <c>foo@{bar}</c>), so without this
    /// step the legacy opaque route would launch a fetch for a ref git will not accept.
    /// Validating here means BOTH routes reach the same verdict before any fetch.
    /// </para>
    /// </summary>
    private async Task<string> ResolveAttachedBranchAsync(
        CancellationToken ct, string phase = PreparationPhase)
    {
        var result = await RunPreparationCommandAsync(
            ["rev-parse", "--symbolic-full-name", "HEAD"],
            "rev-parse --symbolic-full-name HEAD",
            "HEAD branch check",
            ct,
            phase);

        var line = ExactSingleLine(result.Stdout);
        if (line is null
            || !line.StartsWith(BranchRefPrefix, StringComparison.Ordinal)
            || !IsCarryableBranch(line[BranchRefPrefix.Length..]))
        {
            LogPhaseError(phase, $"Config repo {phase} rejected: HEAD is not attached to a usable branch");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} rejected: HEAD is not attached to a usable branch.");
        }

        await ValidateDiscoveredRefAsync(line, ct, phase);

        return line[BranchRefPrefix.Length..];
    }

    /// <summary>
    /// Runs the COMPLETE ref validation (prechecks + the authoritative <c>check-ref-format</c>
    /// subprocess) for the discovered branch ref. A rejection is an ordinary sanitized
    /// preparation failure raised BEFORE any fetch form is built, so neither route can carry a
    /// malformed ref. A REQUESTED execution cancellation propagates unchanged; every other
    /// interruption is an ordinary preparation failure, matching
    /// <see cref="RunPreparationCommandAsync"/>'s contract.
    /// </summary>
    private async Task ValidateDiscoveredRefAsync(
        string discoveredRef, CancellationToken ct, string phase = PreparationPhase)
    {
        string? refError;
        try
        {
            refError = await ConfigRepoGitOperations.ValidateRefCompletelyAsync(
                discoveredRef, _configRepoDir, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var safe = SafeExceptionLog.Describe(ex);
            LogPhaseError(phase, $"Config repo {phase} ref validation failed (cancelled) [{safe}]");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} ref validation was interrupted without a requested cancellation [{safe}].");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var safe = SafeExceptionLog.Describe(ex);
            LogPhaseError(phase, $"Config repo {phase} ref validation failed (error) [{safe}]");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} ref validation failed with an error [{safe}].");
        }

        if (refError is null)
            return;

        LogPhaseError(phase, $"Config repo {phase} rejected: the discovered branch ref is not a valid git ref: {RenderForLog(refError)}");
        throw new ConfigRepoPublicationException(
            preservedOutput: "",
            reason: $"Config repo {phase} rejected: the discovered branch ref is not a valid git ref: {RenderForLog(refError)}");
    }

    /// <summary>
    /// Requires <c>rev-parse --symbolic-full-name @{upstream}</c> to be exactly
    /// <c>refs/remotes/origin/&lt;branch&gt;</c> for the SAME branch. A missing upstream, a
    /// different remote, or a mismatched branch name is a topology rejection: this deliberately
    /// supports the ordinary single-origin clone only.
    /// </summary>
    private async Task VerifyUpstreamAsync(
        string branch, CancellationToken ct, string phase = PreparationPhase)
    {
        var result = await RunPreparationCommandAsync(
            ["rev-parse", "--symbolic-full-name", ConfigRepoGitOperations.RevUpstream],
            $"rev-parse --symbolic-full-name {ConfigRepoGitOperations.RevUpstream}",
            "upstream check",
            ct,
            phase);

        var line = ExactSingleLine(result.Stdout);
        if (line is null || !string.Equals(line, UpstreamRefPrefix + branch, StringComparison.Ordinal))
        {
            LogPhaseError(phase, $"Config repo {phase} rejected: the branch has no matching origin upstream");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo {phase} rejected: the branch has no matching origin upstream.");
        }
    }

    /// <summary>
    /// Recreates the agents working directory (idempotent — <c>clean -fdx</c> removes it when
    /// the baseline tracks no file under it). A failure here is a preparation failure, never a
    /// silent continuation into a prompt for a directory that does not exist.
    /// </summary>
    private void EnsureAgentsDirectoryExists()
    {
        try
        {
            Directory.CreateDirectory(_configAgentsDir);
        }
        catch (Exception ex)
        {
            var safe = SafeExceptionLog.Describe(ex);
            _log.Error($"Config repo preparation failed to create the agents working directory [{safe}]");
            throw new ConfigRepoPublicationException(
                preservedOutput: "",
                reason: $"Config repo preparation failed to create the agents working directory [{safe}].");
        }
    }

    /// <summary>
    /// THE SHARED exact one-line EXTRACTION — structure only, no domain rules.
    /// <para>
    /// STRICT: only the permitted Git line terminators (<c>\n</c> and <c>\r\n</c>) are accepted,
    /// and ONLY as the terminator of the single line — at most one, at the very end. Anything
    /// else that looks like a terminator (a bare <c>\r</c>, a second terminator, a blank or
    /// padded second line) stays in the content and is rejected here. LEADING and TRAILING
    /// whitespace is rejected for EVERY consumer: a value git spells with padding around it
    /// (<c> 1111…1111 </c>, <c> refs/heads/main </c>, <c>  /tmp/root  </c>) is malformed output
    /// that this preparation may not act on. Control characters are rejected everywhere too —
    /// they are a log-forging vector and no legitimate SHA, ref or configured root carries one.
    /// </para>
    /// <para>
    /// The content is returned VERBATIM — never trimmed and never normalized. INTERNAL
    /// whitespace is deliberately NOT judged here: it is legal for one consumer (a worktree root
    /// such as <c>/tmp/config repo</c>) and illegal for the others, so each consumer applies its
    /// OWN domain rule on top of this extraction.
    /// </para>
    /// </summary>
    private static string? ExactSingleLineContent(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // Strip AT MOST ONE trailing terminator, and only a permitted one.
        var content = value;
        if (content.EndsWith("\r\n", StringComparison.Ordinal))
            content = content[..^2];
        else if (content.EndsWith('\n'))
            content = content[..^1];

        if (content.Length == 0)
            return null;

        // No embedded terminator may remain: a second line (blank, padded or functional) means
        // the output was ambiguous or unexpected.
        if (content.Contains('\n') || content.Contains('\r'))
            return null;

        // PADDING is malformed for every consumer — this is what keeps `  root  ` rejected even
        // though an INTERNAL space is legal in a path.
        if (char.IsWhiteSpace(content[0]) || char.IsWhiteSpace(content[^1]))
            return null;

        // Control characters are rejected for every consumer (log-forging boundary).
        foreach (var c in content)
        {
            if (char.IsControl(c))
                return null;
        }

        return content;
    }

    /// <summary>
    /// The SHA/ref domain on top of <see cref="ExactSingleLineContent"/>: the extracted content
    /// must additionally carry NO whitespace at all. A SHA or a ref never legitimately contains
    /// one, so any internal whitespace is malformed output.
    /// </summary>
    private static string? ExactSingleLine(string? value)
    {
        var content = ExactSingleLineContent(value);
        if (content is null)
            return null;

        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c))
                return null;
        }

        return content;
    }

    /// <summary>
    /// Whether a discovered branch name can be carried on BOTH dispatch paths: it must be a
    /// non-empty ref-safe token (the seam's own <see cref="ConfigRepoGitOperations.ValidateRef"/>
    /// prechecks apply to the full ref) that additionally contains no quote or backslash, so the
    /// legacy opaque form can carry it as ONE quoted argument without any interpolation risk.
    /// </summary>
    private static bool IsCarryableBranch(string branch) =>
        branch.Length > 0 && !branch.Contains('"') && !branch.Contains('\\');

    /// <summary>
    /// Commits and pushes any changes the improver made to *.agents.md files in the config repo.
    /// Only stages files in the agents/ subfolder to prevent accidental changes elsewhere.
    /// <para>
    /// TRUTHFUL PUBLICATION: every stage distinguishes SUCCESS from FAILURE. A failed add, diff,
    /// or commit stops all subsequent publication commands and is reported as a FAILURE with a
    /// sanitized stage-specific reason — never as a no-change completion. After a failed
    /// post-commit pull the existing merge-abort attempt still runs (best effort), but push NEVER
    /// proceeds — whether the abort succeeds, fails, or throws; there is no force push, retry,
    /// or remote rollback. A failed or throwing push is a publication FAILURE, never a no-change
    /// result, and push acceptance is never inferred from a transport error — a non-null failure
    /// reason is returned in every such case, and Pushed is set only after a confirmed
    /// successful push. The summary preserves the diagnostic
    /// changed-file paths on failure so the orchestrator can log a useful warning.
    /// </para>
    /// </summary>
    private async Task<ConfigRepoPublication> CommitAndPushConfigRepoAsync(
        CancellationToken ct, ConfigRepoFinalization finalization)
    {
        if (!Directory.Exists(Path.Combine(_configRepoDir, ".git")))
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                "Config repo not found — the improver cannot publish without the config repository.");

        // Only stage agents/*.agents.md — defense-in-depth to prevent touching other files
        ConfigRepoOpResult addResult;
        try
        {
            addResult = await RunConfigRepoCommandAsync(
                ["add", "agents/*.agents.md"], "add agents/*.agents.md", ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.Error($"git add threw (no requested cancellation) [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                $"git add failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"git add threw [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                $"git add failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        if (!addResult.Success)
        {
            _log.Error($"git add failed: {RenderForLog(addResult.SanitizedError)}");
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                $"git add failed (exit {addResult.ExitCode}): {RenderForLog(addResult.SanitizedError)}");
        }

        // Check if there are staged changes.
        // `-z` gives NUL-delimited, UNQUOTED paths so filenames with unusual characters survive.
        ConfigRepoOpResult diffResult;
        try
        {
            diffResult = await RunConfigRepoCommandAsync(
                ["diff", "--cached", "--name-only", "-z"], "diff --cached --name-only -z", ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.Error($"git diff threw (no requested cancellation) [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                $"git diff failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"git diff threw [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                $"git diff failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        if (!diffResult.Success)
        {
            _log.Error($"git diff failed: {RenderForLog(diffResult.SanitizedError)}");
            return new ConfigRepoPublication(
                new GitChangeSummary(),
                $"git diff failed (exit {diffResult.ExitCode}): {RenderForLog(diffResult.SanitizedError)}");
        }

        if (string.IsNullOrWhiteSpace(diffResult.Stdout))
        {
            // A SUCCESSFUL empty staged diff is a genuine no-change completion — kept strictly
            // separate from every failure above.
            _log.Info("No agents.md changes to commit");
            return new ConfigRepoPublication(new GitChangeSummary(), null);
        }

        var changedFiles = diffResult.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
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

        var stagedSummary = new GitChangeSummary
        {
            FilesChanged = filesChanged,
            ChangedFiles = cappedPaths,
        };

        // Commit
        ConfigRepoOpResult commitResult;
        try
        {
            commitResult = await RunConfigRepoCommandAsync(
                ["commit", "-m", ImproverCommitMessage],
                $"commit -m \"{ImproverCommitMessage}\"",
                ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.Error($"git commit threw (no requested cancellation) [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git commit failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"git commit threw [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git commit failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        if (!commitResult.Success)
        {
            _log.Error($"git commit failed: {RenderForLog(commitResult.SanitizedError)}");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git commit failed (exit {commitResult.ExitCode}): {RenderForLog(commitResult.SanitizedError)}");
        }

        _log.Info($"Committed: {RenderForLog(commitResult.Stdout)}");

        // Pull (merge orchestrator's goals/metrics commits) then push.
        // After a FAILED post-commit pull, the merge-abort attempt below still runs (best
        // effort), but push NEVER proceeds — there is no force push, retry, or remote rollback.
        ConfigRepoOpResult pullResult;
        try
        {
            pullResult = await RunConfigRepoCommandAsync(
                ["pull", "--no-rebase"], "pull --no-rebase", ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.Error($"git pull threw (no requested cancellation) [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git pull failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"git pull threw [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git pull failed with an error [{SafeExceptionLog.Describe(ex)}].");
        }

        if (!pullResult.Success)
        {
            _log.Error($"git pull failed: {RenderForLog(pullResult.SanitizedError)}");
            // Abort any in-progress merge (best effort). Whatever happens to the abort —
            // success, failure, or a thrown error — push is NEVER attempted afterwards.
            try
            {
                var abortResult = await RunConfigRepoCommandAsync(
                    ["merge", "--abort"], "merge --abort", ct);
                if (!abortResult.Success)
                    _log.Error($"git merge --abort failed (exit {abortResult.ExitCode}): {RenderForLog(abortResult.SanitizedError)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // requested execution cancellation keeps its established semantics
            }
            catch (Exception ex)
            {
                _log.Error($"git merge --abort threw [{SafeExceptionLog.Describe(ex)}]");
            }

            return new ConfigRepoPublication(
                stagedSummary,
                $"git pull failed (exit {pullResult.ExitCode}): {RenderForLog(pullResult.SanitizedError)} — push not attempted after the failed pull");
        }

        // ── Publication-HEAD resolution (the EXACT publication evidence) ─────────
        // After the successful pull and BEFORE the push: the exact publication HEAD is
        // resolved and validated. If this fails, push NEVER proceeds — publication stops
        // here as an ordinary failure, never guessing a SHA.
        //
        // EVIDENCE PRESERVATION: ResolveSingleShaAsync/RunPreparationCommandAsync signal every
        // failure form (nonzero exit, thrown command, malformed output) by THROWING a
        // ConfigRepoPublicationException carrying an EMPTY preservedOutput and no summary.
        // Letting that escape here would bypass the caller's wrapper — which is what attaches
        // the accumulated agent/retry output and the staged GitChangeSummary — and return a
        // result with empty agent evidence and empty Git diagnostics. It is therefore caught
        // and converted into the ordinary publication FAILURE shape, so the caller's wrapper
        // attaches the evidence exactly as it does for a failed add/diff/commit/pull/push.
        string publicationSha;
        try
        {
            publicationSha = await ResolveSingleShaAsync(
                ConfigRepoGitOperations.RevHeadCommit, "publication HEAD check", "HEAD", ct,
                phase: PublicationPhase);
        }
        catch (ConfigRepoPublicationException ex)
        {
            // The stage-boundary reason is already sanitized; the staged summary carries the
            // diagnostic changed-file paths. Push is NOT attempted after this failure.
            return new ConfigRepoPublication(stagedSummary, ex.Reason);
        }

        // Push — the FINAL confirmation of publication. A failed or throwing push is a
        // publication FAILURE, never a no-change result, and push acceptance is never inferred
        // from a transport error: Published requires a confirmed exit-0 push.
        ConfigRepoOpResult pushResult;
        try
        {
            pushResult = await RunConfigRepoCommandAsync(
                ["push", "origin", "HEAD"], "push", ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.Error($"git push threw (no requested cancellation) [{SafeExceptionLog.Describe(ex)}]");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git push failed with an error [{SafeExceptionLog.Describe(ex)}] — the remote state is unknown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"Push failed: {SafeExceptionLog.Describe(ex)}");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git push failed with an error [{SafeExceptionLog.Describe(ex)}] — the remote state is unknown");
        }

        if (!pushResult.Success)
        {
            _log.Error($"git push failed: {RenderForLog(pushResult.SanitizedError)}");
            return new ConfigRepoPublication(
                stagedSummary,
                $"git push failed (exit {pushResult.ExitCode}): {RenderForLog(pushResult.SanitizedError)}");
        }

        // ── CONFIRMED PUBLICATION — retained ATOMICALLY, before ANY fallible work ──
        // BOTH facts are materialized here, immediately after the confirmed exit-zero push and
        // BEFORE the (fallible) logging below:
        //   * the published SHA, so finalization resets to the PUBLISHED tip; and
        //   * the pushed SUMMARY (Pushed=true), so the confirmed publication survives even if
        //     the log write, the session save, or any other later step throws.
        // WorkerLogger.Info writes straight to Console.Out, so a disposed/failing writer would
        // otherwise throw into the generic handler and produce a result WITHOUT Pushed=true
        // even though finalization already resets to the published SHA. Every later outcome is
        // constructed from this retained evidence rather than recomputed.
        var publishedSummary = stagedSummary with { Pushed = true };
        finalization.RetainConfirmedPublication(publicationSha, publishedSummary);

        _log.Info($"Pushed config repo changes at {RenderForLog(publicationSha[..Math.Min(publicationSha.Length, 12)])}");
        return new ConfigRepoPublication(finalization.PublishedSummary!, null);
    }
}
