using System.Diagnostics;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Copilot;
using CopilotHive.Git;
using CopilotHive.Improvement;
using CopilotHive.Metrics;
using CopilotHive.Workers;

namespace CopilotHive.Orchestration;

public sealed class Orchestrator : IAsyncDisposable
{
    private readonly HiveConfiguration _config;
    private readonly IWorkerManager _workerManager;
    private readonly ICopilotClientFactory _clientFactory;
    private readonly GitWorkspaceManager _gitManager;
    private readonly AgentsManager _agentsManager;
    private readonly MetricsTracker _metricsTracker;
    private readonly ImprovementAnalyzer _improvementAnalyzer = new();
    private readonly IOrchestratorBrain _brain;

    public Orchestrator(HiveConfiguration config)
        : this(config,
               new DockerWorkerManager(config),
               new CopilotClientFactory(config.GitHubToken))
    {
    }

    public Orchestrator(
        HiveConfiguration config,
        IWorkerManager workerManager,
        ICopilotClientFactory clientFactory)
        : this(config, workerManager, clientFactory, brain: null)
    {
    }

    public Orchestrator(
        HiveConfiguration config,
        IWorkerManager workerManager,
        ICopilotClientFactory clientFactory,
        IOrchestratorBrain? brain)
    {
        _config = config;
        _workerManager = workerManager;
        _clientFactory = clientFactory;
        _gitManager = new GitWorkspaceManager(config.WorkspacePath);
        _agentsManager = new AgentsManager(config.AgentsPath);
        _metricsTracker = new MetricsTracker(config.MetricsPath);
        _brain = brain ?? new OrchestratorBrain(
            config, workerManager, clientFactory,
            config.WorkspacePath,
            new AgentsManager(config.AgentsPath).GetAgentsMdPath("orchestrator"));
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[Orchestrator] Goal: {_config.Goal}");
        Console.WriteLine($"[Orchestrator] Max iterations: {_config.MaxIterations}");
        Console.WriteLine($"[Orchestrator] Models — coder: {_config.GetModelForRole("coder")}, reviewer: {_config.GetModelForRole("reviewer")}, tester: {_config.GetModelForRole("tester")}, improver: {_config.GetModelForRole("improver")}, orchestrator: {_config.GetModelForRole("orchestrator")}");

        await _workerManager.CleanupStaleContainersAsync(ct);
        await _gitManager.InitBareRepoAsync(_config.SourcePath, ct);

        // Start persistent orchestrator brain
        Console.WriteLine("[Orchestrator] Starting orchestrator brain...");
        await _brain.StartAsync(ct);
        Console.WriteLine("[Orchestrator] Brain ready.");


        for (var iteration = 1; iteration <= _config.MaxIterations; iteration++)
        {
            if (ct.IsCancellationRequested) break;

            Console.WriteLine($"\n{'='} Iteration {iteration} {'='}");
            var metrics = await RunIterationAsync(iteration, ct);

            _metricsTracker.RecordIteration(metrics);

            var comparison = _metricsTracker.CompareWithPrevious(metrics);
            if (comparison is not null)
            {
                Console.WriteLine($"[Orchestrator] Delta: {comparison}");

                if (_metricsTracker.HasRegressed(metrics))
                {
                    Console.WriteLine("[Orchestrator] ⚠ REGRESSION DETECTED — rolling back AGENTS.md");
                    foreach (var role in new[] { "coder", "reviewer", "tester", "orchestrator", "improver" })
                        _agentsManager.RollbackAgentsMd(role);
                }
            }

            // Phase 4: Self-improvement (skip on clean pass or if we're on the last iteration)
            if ((_config.AlwaysImprove || _improvementAnalyzer.ShouldImprove(metrics))
                && iteration < _config.MaxIterations)
            {
                await RunImprovementPhaseAsync(metrics, ct);
            }

            if (metrics.FailedTests == 0 && metrics.TotalTests > 0)
            {
                Console.WriteLine($"[Orchestrator] ✅ All {metrics.TotalTests} tests passing — verdict: {metrics.Verdict}. Iteration complete.");
                break;
            }
        }
    }

    private async Task<IterationMetrics> RunIterationAsync(int iteration, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var branchName = $"iteration-{iteration:D3}";
        var metrics = new IterationMetrics { Iteration = iteration };

        // Ask the brain to plan this iteration
        var previousMetrics = _metricsTracker.History.LastOrDefault();
        var plan = await _brain.PlanIterationAsync(iteration, _config.Goal, previousMetrics, ct);
        Console.WriteLine($"[Brain] Plan: {plan.Reason ?? plan.Action.ToString()}");

        // Phase 1: Coding — brain crafts the prompt
        Console.WriteLine("[Orchestrator] Phase 1: Coding");
        var coderClone = await _gitManager.CreateWorkerCloneAsync($"coder-{iteration}", ct);
        await _gitManager.CreateBranchAsync(coderClone, $"coder/{branchName}", ct);

        var coderPrompt = await _brain.CraftPromptAsync(
            "coder", _config.Goal, iteration, branchName, additionalContext: null, ct);
        var coderResponse = await RunWorkerAsync(
            WorkerRole.Coder, coderClone, "coder", coderPrompt, ct);
        Console.WriteLine($"[Coder] {Truncate(coderResponse, 200)}");

        await _gitManager.RepairRemoteAsync(coderClone, ct);
        await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);

        // Inform brain of coder result
        await _brain.InformAsync(
            $"Coder completed. Output summary: {Truncate(coderResponse, 500)}", ct);

        // Phase 1.5: Code Review — brain decides verdict and crafts feedback
        for (var reviewRetry = 0; reviewRetry <= _config.MaxRetriesPerTask; reviewRetry++)
        {
            var reviewPhase = reviewRetry == 0
                ? "Phase 1.5: Code Review"
                : $"Phase 1.5: Review retry {reviewRetry}/{_config.MaxRetriesPerTask}";
            Console.WriteLine($"[Orchestrator] {reviewPhase}");

            // Ask brain if we should skip review (e.g., docs-only change)
            if (reviewRetry == 0)
            {
                var skipDecision = await _brain.DecideNextStepAsync(
                    "pre_review",
                    $"The coder has finished. Should we review this change or skip directly to testing?\nCoder output: {Truncate(coderResponse, 500)}",
                    ct);
                if (skipDecision.Action == OrchestratorActionType.Skip)
                {
                    Console.WriteLine($"[Brain] Skipping review: {skipDecision.Reason}");
                    break;
                }
            }

            var reviewClone = await _gitManager.CreateWorkerCloneAsync(
                $"reviewer-{iteration}-{reviewRetry}", ct);
            await _gitManager.PullBranchAsync(reviewClone, $"coder/{branchName}", ct);

            var reviewContext = reviewRetry > 0
                ? $"This is review attempt {reviewRetry + 1}. The coder addressed previous review feedback."
                : null;
            var reviewPrompt = await _brain.CraftPromptAsync(
                "reviewer", _config.Goal, iteration, branchName, reviewContext, ct);
            var reviewResponse = await RunWorkerAsync(
                WorkerRole.Reviewer, reviewClone, "reviewer", reviewPrompt, ct);
            Console.WriteLine($"[Reviewer] {Truncate(reviewResponse, 300)}");

            // Brain interprets the review output
            var reviewDecision = await _brain.InterpretOutputAsync("reviewer", reviewResponse, ct);
            var reviewVerdict = reviewDecision.ReviewVerdict ?? reviewDecision.Verdict ?? "";

            // Also run string parsing as supplementary data for metrics
            ParseReviewReport(reviewResponse, metrics);

            // Use brain's verdict, falling back to string parser
            if (string.IsNullOrEmpty(reviewVerdict))
                reviewVerdict = metrics.ReviewVerdict;
            else
                metrics.ReviewVerdict = reviewVerdict;

            if (reviewDecision.Issues?.Count > 0)
                metrics.ReviewIssues = reviewDecision.Issues;

            if (reviewVerdict.Equals("APPROVE", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Orchestrator] ✅ Reviewer verdict: APPROVE");
                break;
            }

            // Review requested changes — brain crafts feedback for coder
            if (reviewRetry < _config.MaxRetriesPerTask)
            {
                Console.WriteLine($"[Orchestrator] Reviewer verdict: {reviewVerdict} — sending feedback to coder");

                var fixContext = $"""
                    The reviewer requested changes.
                    Review verdict: {reviewVerdict}
                    Issues: {string.Join("; ", reviewDecision.Issues ?? metrics.ReviewIssues)}
                    Full review: {Truncate(reviewResponse, 2000)}
                    """;
                var fixPrompt = await _brain.CraftPromptAsync(
                    "coder", _config.Goal, iteration, branchName, fixContext, ct);

                await _gitManager.PullBranchAsync(coderClone, $"coder/{branchName}", ct);
                var fixResponse = await RunWorkerAsync(
                    WorkerRole.Coder, coderClone, "coder", fixPrompt, ct);
                Console.WriteLine($"[Coder:review-fix] {Truncate(fixResponse, 200)}");

                await _gitManager.RepairRemoteAsync(coderClone, ct);
                await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);

                await _brain.InformAsync(
                    $"Coder addressed review feedback. Output: {Truncate(fixResponse, 300)}", ct);
            }
            else
            {
                Console.WriteLine($"[Orchestrator] Reviewer still requesting changes after {reviewRetry} retries — proceeding to testing");
            }
        }

        // Phase 2: Test → Fix loop (up to MaxRetriesPerTask attempts)
        string testerResponse = "";
        for (var retry = 0; retry <= _config.MaxRetriesPerTask; retry++)
        {
            var phase = retry == 0 ? "Phase 2: Testing" : $"Phase 2: Retry {retry}/{_config.MaxRetriesPerTask}";
            Console.WriteLine($"[Orchestrator] {phase}");

            var testerClone = await _gitManager.CreateWorkerCloneAsync($"tester-{iteration}-{retry}", ct);
            await _gitManager.PullBranchAsync(testerClone, $"coder/{branchName}", ct);

            var testContext = retry > 0
                ? $"This is test attempt {retry + 1}. The coder fixed issues from the previous test run."
                : null;
            var testerPrompt = await _brain.CraftPromptAsync(
                "tester", _config.Goal, iteration, branchName, testContext, ct);
            testerResponse = await RunWorkerAsync(
                WorkerRole.Tester, testerClone, "tester", testerPrompt, ct);
            Console.WriteLine($"[Tester] {Truncate(testerResponse, 300)}");

            // Brain interprets test output
            var testDecision = await _brain.InterpretOutputAsync("tester", testerResponse, ct);

            // Also run string parsing as supplementary data
            ParseTestReport(testerResponse, metrics);

            // Apply brain's interpretation (overrides string parsing when available)
            ApplyTestDecision(testDecision, metrics);

            // All tests pass → done
            if (IsPassingVerdict(metrics.Verdict))
            {
                Console.WriteLine("[Orchestrator] ✅ Tester verdict: PASS");
                break;
            }

            // Tests fail and we have retries left → brain crafts feedback for coder
            if (retry < _config.MaxRetriesPerTask)
            {
                metrics.RetryCount++;

                Console.WriteLine($"[Orchestrator] Verdict: {metrics.Verdict} — sending feedback to coder");

                var fixContext = $"""
                    The tester found issues.
                    Verdict: {metrics.Verdict}
                    Issues: {string.Join("; ", testDecision.Issues ?? metrics.Issues)}
                    Full report: {Truncate(testerResponse, 2000)}
                    """;
                var fixPrompt = await _brain.CraftPromptAsync(
                    "coder", _config.Goal, iteration, branchName, fixContext, ct);

                // Push tester's test files so coder can see them
                await _gitManager.RepairRemoteAsync(testerClone, ct);
                await _gitManager.PushBranchAsync(testerClone, $"coder/{branchName}", ct);

                // Pull tester's changes into coder clone and send fix prompt
                await _gitManager.PullBranchAsync(coderClone, $"coder/{branchName}", ct);
                var fixResponse = await RunWorkerAsync(
                    WorkerRole.Coder, coderClone, "coder", fixPrompt, ct);
                Console.WriteLine($"[Coder:fix] {Truncate(fixResponse, 200)}");

                await _gitManager.RepairRemoteAsync(coderClone, ct);
                await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);
            }
        }

        // Phase 3: Merge if tester passed
        if (IsPassingVerdict(metrics.Verdict))
        {
            Console.WriteLine("[Orchestrator] Phase 3: Merging to main");
            var mergeClone = await _gitManager.CreateWorkerCloneAsync($"merge-{iteration}", ct);
            await _gitManager.PullBranchAsync(mergeClone, $"coder/{branchName}", ct);

            var (mergeSuccess, mergeOutput) = await _gitManager.MergeToMainAsync(
                mergeClone, $"coder/{branchName}", ct);

            if (!mergeSuccess)
            {
                Console.WriteLine($"[Orchestrator] Merge conflict: {Truncate(mergeOutput, 200)}");
                metrics.Issues.Add("Merge conflict: " + Truncate(mergeOutput, 200));
            }
            else
            {
                // Phase 3b: Post-merge verification
                Console.WriteLine("[Orchestrator] Phase 3b: Post-merge verification");
                var postMergeResult = await RunPostMergeVerificationAsync(
                    iteration, branchName, mergeClone, coderClone, metrics, ct);

                if (!postMergeResult)
                    metrics.Issues.Add("Post-merge verification failed");
            }
        }

        sw.Stop();
        metrics.Duration = sw.Elapsed;
        return metrics;
    }

    /// <summary>
    /// Applies the brain's test interpretation to metrics, preferring brain values over string parsing.
    /// </summary>
    private static void ApplyTestDecision(OrchestratorDecision decision, IterationMetrics metrics)
    {
        if (!string.IsNullOrEmpty(decision.Verdict))
            metrics.Verdict = decision.Verdict;

        if (decision.TestMetrics is { } tm)
        {
            if (tm.BuildSuccess.HasValue) metrics.BuildSuccess = tm.BuildSuccess.Value;
            if (tm.TotalTests.HasValue && tm.TotalTests.Value > 0) metrics.TotalTests = tm.TotalTests.Value;
            if (tm.PassedTests.HasValue && tm.PassedTests.Value > 0) metrics.PassedTests = tm.PassedTests.Value;
            if (tm.FailedTests.HasValue) metrics.FailedTests = tm.FailedTests.Value;
            if (tm.CoveragePercent.HasValue && tm.CoveragePercent.Value > 0)
                metrics.CoveragePercent = tm.CoveragePercent.Value;
        }

        if (decision.Issues?.Count > 0 && metrics.Issues.Count == 0)
            metrics.Issues.AddRange(decision.Issues);
    }

    private async Task<string> RunWorkerAsync(
        WorkerRole role, string clonePath, string agentRole,
        string prompt, CancellationToken ct)
    {
        var model = _config.GetModelForRole(agentRole);
        var agentsMdPath = _agentsManager.GetAgentsMdPath(agentRole);
        var worker = await _workerManager.SpawnWorkerAsync(role, clonePath, agentsMdPath, model, ct);

        try
        {
            await using var client = _clientFactory.Create(worker.Port);
            await client.ConnectAsync(ct);
            return await client.SendTaskAsync(prompt, ct);
        }
        finally
        {
            await _workerManager.StopWorkerAsync(worker.Id, ct);
        }
    }

    /// <summary>
    /// Runs post-merge verification: tests main after merge, reverts + sends to coder if tests fail.
    /// Returns true if main is in a good state (tests pass), false otherwise.
    /// </summary>
    private async Task<bool> RunPostMergeVerificationAsync(
        int iteration, string branchName, string mergeClone, string coderClone,
        IterationMetrics metrics, CancellationToken ct)
    {
        var verifyClone = await _gitManager.CreateWorkerCloneAsync($"verify-{iteration}", ct);

        var verifyPrompt = await _brain.CraftPromptAsync(
            "tester", _config.Goal, iteration, branchName,
            "This is POST-MERGE VERIFICATION. A merge was just completed to main. Run ALL tests on the main branch to verify nothing was broken by the merge.",
            ct);
        var verifyResponse = await RunWorkerAsync(
            WorkerRole.Tester, verifyClone, "tester", verifyPrompt, ct);
        Console.WriteLine($"[Tester:verify] {Truncate(verifyResponse, 300)}");

        var verifyMetrics = new IterationMetrics { Iteration = iteration };
        ParseTestReport(verifyResponse, verifyMetrics);

        // Brain interprets
        var verifyDecision = await _brain.InterpretOutputAsync("tester", verifyResponse, ct);
        ApplyTestDecision(verifyDecision, verifyMetrics);

        if (IsPassingVerdict(verifyMetrics.Verdict))
        {
            Console.WriteLine("[Orchestrator] ✅ Post-merge verification passed");
            // Update metrics with post-merge numbers (more authoritative)
            if (verifyMetrics.TotalTests > 0) metrics.TotalTests = verifyMetrics.TotalTests;
            if (verifyMetrics.PassedTests > 0) metrics.PassedTests = verifyMetrics.PassedTests;
            metrics.FailedTests = verifyMetrics.FailedTests;
            if (verifyMetrics.CoveragePercent > 0) metrics.CoveragePercent = verifyMetrics.CoveragePercent;
            return true;
        }

        // Post-merge test failed — revert the merge
        Console.WriteLine($"[Orchestrator] ⚠ Post-merge verification FAILED (verdict: {verifyMetrics.Verdict}) — reverting merge");
        try
        {
            await _gitManager.RevertLastMergeAsync(mergeClone, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Orchestrator] ⚠ Could not revert merge: {ex.Message}");
        }

        // Send failure feedback to coder — brain crafts the prompt
        Console.WriteLine("[Orchestrator] Phase 3c: Coder fixing post-merge failures");
        var postMergeFixContext = $"""
            Code PASSED on the feature branch but FAILED after merging to main.
            Tester verdict after merge: {verifyMetrics.Verdict}
            Issues: {string.Join("; ", verifyMetrics.Issues)}
            Full report: {Truncate(verifyResponse, 2000)}
            """;

        var fixPrompt = await _brain.CraftPromptAsync(
            "coder", _config.Goal, iteration, branchName, postMergeFixContext, ct);

        await _gitManager.PullBranchAsync(coderClone, $"coder/{branchName}", ct);
        var fixResponse = await RunWorkerAsync(
            WorkerRole.Coder, coderClone, "coder", fixPrompt, ct);
        Console.WriteLine($"[Coder:postmerge-fix] {Truncate(fixResponse, 200)}");

        await _gitManager.RepairRemoteAsync(coderClone, ct);
        await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);

        // Re-test the fix on the branch before re-merging
        Console.WriteLine("[Orchestrator] Phase 3d: Re-testing after post-merge fix");
        var retestClone = await _gitManager.CreateWorkerCloneAsync($"retest-{iteration}", ct);
        await _gitManager.PullBranchAsync(retestClone, $"coder/{branchName}", ct);

        var retestPrompt = await _brain.CraftPromptAsync(
            "tester", _config.Goal, iteration, branchName,
            "This is a RE-TEST after a post-merge fix. The coder fixed issues that appeared after merging to main. Run ALL tests.", ct);
        var retestResponse = await RunWorkerAsync(
            WorkerRole.Tester, retestClone, "tester", retestPrompt, ct);
        Console.WriteLine($"[Tester:retest] {Truncate(retestResponse, 300)}");

        ParseTestReport(retestResponse, metrics);
        var retestDecision = await _brain.InterpretOutputAsync("tester", retestResponse, ct);
        ApplyTestDecision(retestDecision, metrics);

        if (!IsPassingVerdict(metrics.Verdict))
        {
            Console.WriteLine($"[Orchestrator] Post-merge fix still failing (verdict: {metrics.Verdict}) — will retry next iteration");
            return false;
        }

        // Re-merge the fixed code
        Console.WriteLine("[Orchestrator] ✅ Post-merge fix verified — re-merging to main");
        var reMergeClone = await _gitManager.CreateWorkerCloneAsync($"remerge-{iteration}", ct);
        await _gitManager.PullBranchAsync(reMergeClone, $"coder/{branchName}", ct);

        var (remergeSuccess, remergeOutput) = await _gitManager.MergeToMainAsync(
            reMergeClone, $"coder/{branchName}", ct);

        if (!remergeSuccess)
        {
            Console.WriteLine($"[Orchestrator] Re-merge failed: {Truncate(remergeOutput, 200)}");
            metrics.Issues.Add("Re-merge failed after post-merge fix");
            return false;
        }

        return true;
    }

    private async Task RunImprovementPhaseAsync(IterationMetrics metrics, CancellationToken ct)
    {
        Console.WriteLine("[Orchestrator] Phase 4: Self-Improvement");

        var rolesToImprove = new[] { "coder", "reviewer", "tester", "orchestrator", "improver" };
        var currentAgentsMd = new Dictionary<string, string>();
        foreach (var role in rolesToImprove)
        {
            var content = _agentsManager.GetAgentsMd(role);
            if (!string.IsNullOrEmpty(content))
                currentAgentsMd[role] = content;
        }

        var requests = _improvementAnalyzer.BuildRequests(
            metrics, _metricsTracker.History, currentAgentsMd);

        // Build a combined prompt for the improver
        var combinedPrompt = string.Join("\n\n---\n\n", requests.Select(r => r.Prompt));

        try
        {
            var improverResponse = await RunWorkerAsync(
                WorkerRole.Improver,
                await _gitManager.CreateWorkerCloneAsync("improver", ct),
                "improver",
                combinedPrompt, ct);

            var improvements = ParseImproverResponse(improverResponse);
            var appliedCount = 0;
            var rejectedCount = 0;

            foreach (var (role, newContent) in improvements)
            {
                if (!currentAgentsMd.TryGetValue(role, out var originalContent))
                    continue;

                if (!ValidateImprovement(originalContent, newContent, role))
                {
                    rejectedCount++;
                    continue;
                }

                _agentsManager.UpdateAgentsMd(role, newContent);
                appliedCount++;
                Console.WriteLine($"[Orchestrator] 📝 Updated {role}.agents.md");
            }

            if (rejectedCount > 0)
                Console.WriteLine($"[Orchestrator] ⚠ Rejected {rejectedCount} improvement(s) that were too short (likely summaries, not full files).");
            if (appliedCount == 0)
                Console.WriteLine("[Orchestrator] No improvements applied this iteration.");
            else
                Console.WriteLine($"[Orchestrator] Applied {appliedCount} AGENTS.md improvement(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Orchestrator] Improvement phase failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that improved content is a plausible full AGENTS.md replacement,
    /// not a summary or placeholder. Rejects content that is too short absolutely
    /// or relative to the original.
    /// </summary>
    internal static bool ValidateImprovement(string originalContent, string newContent, string role)
    {
        const int MinimumLineCount = 5;

        var newLines = newContent.Split('\n').Length;
        var originalLines = originalContent.Split('\n').Length;

        if (newLines < MinimumLineCount)
        {
            Console.WriteLine($"[Orchestrator] ⚠ Rejected {role}.agents.md: only {newLines} lines (minimum {MinimumLineCount})");
            return false;
        }

        if (originalLines > 0 && newLines < originalLines / 2)
        {
            Console.WriteLine($"[Orchestrator] ⚠ Rejected {role}.agents.md: {newLines} lines vs {originalLines} original (<50%)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses the improver's response to extract improved AGENTS.md content per role.
    /// Expects blocks delimited by === IMPROVED {role}.agents.md === / === END {role}.agents.md ===
    /// </summary>
    internal static Dictionary<string, string> ParseImproverResponse(string response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(response))
            return result;

        var lines = response.Split('\n');
        string? currentRole = null;
        var contentLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Match: === IMPROVED coder.agents.md ===
            if (trimmed.StartsWith("=== IMPROVED ", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith(".agents.md ===", StringComparison.OrdinalIgnoreCase))
            {
                currentRole = trimmed
                    .Replace("=== IMPROVED ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(".agents.md ===", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                contentLines.Clear();
                continue;
            }

            // Match: === END coder.agents.md ===
            if (currentRole is not null
                && trimmed.StartsWith("=== END ", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith(".agents.md ===", StringComparison.OrdinalIgnoreCase))
            {
                var content = string.Join('\n', contentLines).Trim();
                if (!string.IsNullOrEmpty(content))
                    result[currentRole] = content;

                currentRole = null;
                contentLines.Clear();
                continue;
            }

            // Skip === UNCHANGED lines
            if (trimmed.StartsWith("=== UNCHANGED ", StringComparison.OrdinalIgnoreCase))
            {
                currentRole = null;
                continue;
            }

            if (currentRole is not null)
                contentLines.Add(line);
        }

        return result;
    }

    /// <summary>
    /// Parses the reviewer's response to extract the review verdict and issues.
    /// Returns the verdict string (APPROVE or REQUEST_CHANGES).
    /// </summary>
    internal static string ParseReviewReport(string response, IterationMetrics metrics)
    {
        var inReport = false;
        var inIssues = false;
        var verdict = "";

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("REVIEW_REPORT", StringComparison.OrdinalIgnoreCase))
            {
                inReport = true;
                inIssues = false;
                continue;
            }

            if (!inReport) continue;

            if (inIssues && trimmed.StartsWith("- "))
            {
                metrics.ReviewIssues.Add(trimmed[2..].Trim());
                continue;
            }

            if (inIssues && !trimmed.StartsWith("- "))
                inIssues = false;

            if (TryParseField(trimmed, "verdict:", out var verdictVal))
                verdict = verdictVal;
            else if (TryParseField(trimmed, "issues_count:", out var countVal)
                     && int.TryParse(countVal, out var count))
                metrics.ReviewIssuesFound = count;
            else if (TryParseField(trimmed, "critical_issues:", out var critVal)
                     && int.TryParse(critVal, out var crit))
                metrics.ReviewIssuesFound = Math.Max(metrics.ReviewIssuesFound, crit);
            else if (trimmed.StartsWith("issues:", StringComparison.OrdinalIgnoreCase))
                inIssues = true;
        }

        metrics.ReviewVerdict = verdict;
        return verdict;
    }

    internal static void ParseTestReport(string response, IterationMetrics metrics)
    {
        var inReport = false;
        var inIssues = false;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();

            // Both TEST_REPORT: and METRICS: start the report section
            // Also handle TEST_REPORT without colon (LLMs are creative with formatting)
            if (trimmed.StartsWith("TEST_REPORT") || trimmed.StartsWith("METRICS:"))
            {
                inReport = true;
                inIssues = false;
                continue;
            }

            if (!inReport) continue;

            // Issue list items (lines starting with "- ")
            if (inIssues && trimmed.StartsWith("- "))
            {
                metrics.Issues.Add(trimmed[2..].Trim());
                continue;
            }

            if (inIssues && !trimmed.StartsWith("- "))
                inIssues = false;

            if (TryParseField(trimmed, "build_success:", out var buildVal))
                metrics.BuildSuccess = buildVal.Equals("true", StringComparison.OrdinalIgnoreCase);
            else if (TryParseField(trimmed, "unit_tests_total:", out var utTotal) && int.TryParse(utTotal, out var t))
                metrics.TotalTests = t;
            else if (TryParseField(trimmed, "unit_tests_passed:", out var utPassed) && int.TryParse(utPassed, out var p))
            {
                metrics.PassedTests = p;
                metrics.FailedTests = metrics.TotalTests - p;
            }
            else if (TryParseField(trimmed, "integration_tests_total:", out var itTotal) && int.TryParse(itTotal, out var it))
                metrics.IntegrationTestsTotal = it;
            else if (TryParseField(trimmed, "integration_tests_passed:", out var itPassed) && int.TryParse(itPassed, out var ip))
                metrics.IntegrationTestsPassed = ip;
            else if (TryParseField(trimmed, "runtime_verified:", out var rvVal))
                metrics.RuntimeVerified = rvVal.Equals("true", StringComparison.OrdinalIgnoreCase);
            else if (TryParseField(trimmed, "coverage_percent:", out var covVal)
                     && double.TryParse(covVal, System.Globalization.CultureInfo.InvariantCulture, out var cov))
                metrics.CoveragePercent = cov;
            else if (TryParseField(trimmed, "verdict:", out var verdictVal))
                metrics.Verdict = verdictVal;
            // LLMs use various labels for the verdict field
            else if (TryParseField(trimmed, "status:", out var statusVal))
                metrics.Verdict = statusVal;
            else if (TryParseField(trimmed, "conclusion:", out var conclusionVal))
                metrics.Verdict = conclusionVal;
            else if (TryParseField(trimmed, "result:", out var resultVal)
                     && !int.TryParse(resultVal, out _)) // avoid matching "Results: 96"
                metrics.Verdict = resultVal;
            else if (TryParseField(trimmed, "summary:", out var summaryVal))
                metrics.TestReportSummary = summaryVal;
            else if (trimmed.StartsWith("issues:", StringComparison.OrdinalIgnoreCase))
                inIssues = true;

            // Short-form aliases (LLMs abbreviate)
            else if (TryParseField(trimmed, "total:", out var shortTotal) && int.TryParse(shortTotal, out var st))
                metrics.TotalTests = st;
            else if (TryParseField(trimmed, "passed:", out var shortPassed) && int.TryParse(shortPassed, out var sp))
                metrics.PassedTests = sp;
            else if (TryParseField(trimmed, "failed:", out var shortFailed) && int.TryParse(shortFailed, out var sf))
                metrics.FailedTests = sf;

            // Also support the old METRICS: format for backwards compatibility
            else if (TryParseField(trimmed, "total_tests:", out var oldTotal) && int.TryParse(oldTotal, out var ot))
                metrics.TotalTests = ot;
            else if (TryParseField(trimmed, "passed_tests:", out var oldPassed) && int.TryParse(oldPassed, out var op))
                metrics.PassedTests = op;
            else if (TryParseField(trimmed, "failed_tests:", out var oldFailed) && int.TryParse(oldFailed, out var of2))
                metrics.FailedTests = of2;
        }

        // Fallback: infer verdict from test numbers when no explicit verdict was parsed
        if (string.IsNullOrWhiteSpace(metrics.Verdict) && metrics.TotalTests > 0
            && metrics.FailedTests == 0 && metrics.PassedTests == metrics.TotalTests)
        {
            metrics.Verdict = "PASS";
        }
    }

    private static bool TryParseField(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[prefix.Length..].Trim();
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>
    /// Determines whether a test verdict indicates success.
    /// LLMs are creative with formatting — accepts "PASS", "ALL TESTS PASSED", etc.
    /// </summary>
    internal static bool IsPassingVerdict(string verdict)
    {
        if (string.IsNullOrWhiteSpace(verdict))
            return false;

        // Explicit PASS signal takes priority (handles "ALL 96 TESTS PASSED — no failures")
        if (verdict.Contains("PASS", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    public async ValueTask DisposeAsync()
    {
        await _brain.DisposeAsync();
        await _workerManager.DisposeAsync();
    }
}
