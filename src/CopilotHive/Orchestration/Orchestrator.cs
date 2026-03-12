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
    {
        _config = config;
        _workerManager = workerManager;
        _clientFactory = clientFactory;
        _gitManager = new GitWorkspaceManager(config.WorkspacePath);
        _agentsManager = new AgentsManager(config.AgentsPath);
        _metricsTracker = new MetricsTracker(config.MetricsPath);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[Orchestrator] Goal: {_config.Goal}");
        Console.WriteLine($"[Orchestrator] Max iterations: {_config.MaxIterations}");
        Console.WriteLine($"[Orchestrator] Models — coder: {_config.GetModelForRole("coder")}, reviewer: {_config.GetModelForRole("reviewer")}, tester: {_config.GetModelForRole("tester")}, improver: {_config.GetModelForRole("improver")}");

        await _gitManager.InitBareRepoAsync(_config.SourcePath, ct);

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

        // Phase 1: Coder
        Console.WriteLine("[Orchestrator] Phase 1: Coding");
        var coderClone = await _gitManager.CreateWorkerCloneAsync($"coder-{iteration}", ct);
        await _gitManager.CreateBranchAsync(coderClone, $"coder/{branchName}", ct);

        var coderResponse = await RunWorkerAsync(
            WorkerRole.Coder, coderClone, "coder",
            $"""
            You are working on this goal: {_config.Goal}

            This is iteration {iteration}. Work on the coder/{branchName} branch.
            Write the code and commit your changes with clear commit messages.
            Do NOT run git push — the orchestrator handles that.
            """, ct);
        Console.WriteLine($"[Coder] {Truncate(coderResponse, 200)}");

        await _gitManager.RepairRemoteAsync(coderClone, ct);
        await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);

        // Phase 1.5: Code Review → Fix loop
        for (var reviewRetry = 0; reviewRetry <= _config.MaxRetriesPerTask; reviewRetry++)
        {
            var reviewPhase = reviewRetry == 0
                ? "Phase 1.5: Code Review"
                : $"Phase 1.5: Review retry {reviewRetry}/{_config.MaxRetriesPerTask}";
            Console.WriteLine($"[Orchestrator] {reviewPhase}");

            var reviewClone = await _gitManager.CreateWorkerCloneAsync(
                $"reviewer-{iteration}-{reviewRetry}", ct);
            await _gitManager.PullBranchAsync(reviewClone, $"coder/{branchName}", ct);

            var reviewResponse = await RunWorkerAsync(
                WorkerRole.Reviewer, reviewClone, "reviewer",
                $"""
                You are reviewing code changes for this goal: {_config.Goal}

                This is iteration {iteration}. The coder's work is on branch coder/{branchName}.
                Review the diff against main and produce a REVIEW_REPORT block.
                Do NOT modify any code. Do NOT run git push.
                """, ct);
            Console.WriteLine($"[Reviewer] {Truncate(reviewResponse, 300)}");

            var reviewVerdict = ParseReviewReport(reviewResponse, metrics);

            if (reviewVerdict.Equals("APPROVE", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Orchestrator] ✅ Reviewer verdict: APPROVE");
                break;
            }

            // Review requested changes — send feedback to coder
            if (reviewRetry < _config.MaxRetriesPerTask)
            {
                var reviewIssues = metrics.ReviewIssues.Count > 0
                    ? "Review issues:\n- " + string.Join("\n- ", metrics.ReviewIssues)
                    : "See reviewer report for details.";

                Console.WriteLine($"[Orchestrator] Reviewer verdict: {reviewVerdict} — sending feedback to coder");

                await _gitManager.PullBranchAsync(coderClone, $"coder/{branchName}", ct);
                var fixResponse = await RunWorkerAsync(
                    WorkerRole.Coder, coderClone, "coder",
                    $"""
                    The code reviewer found issues with your code. Their verdict: {reviewVerdict}

                    {reviewIssues}

                    Reviewer's full report:
                    {Truncate(reviewResponse, 2000)}

                    Fix the issues and commit your changes.
                    Do NOT run git push — the orchestrator handles that.
                    """, ct);
                Console.WriteLine($"[Coder:review-fix] {Truncate(fixResponse, 200)}");

                await _gitManager.RepairRemoteAsync(coderClone, ct);
                await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);
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

            testerResponse = await RunWorkerAsync(
                WorkerRole.Tester, testerClone, "tester",
                $"""
                You are testing code for this goal: {_config.Goal}

                This is iteration {iteration}. The coder's work is on branch coder/{branchName}.

                Follow your full testing workflow:
                1. Create a TEST_PLAN.md with scope, acceptance criteria, and test cases.
                2. Build the project — if it fails, stop and report FAIL immediately.
                3. Run all existing tests (unit tests written by the coder).
                4. Write integration tests that verify components work together.
                5. Run the application and verify it starts and works correctly.
                6. Produce the TEST_REPORT block with your findings.

                Commit your test plan, integration tests, and test report on the branch.
                Do NOT run git push — the orchestrator handles that.

                IMPORTANT: End your response with a TEST_REPORT block in the exact format
                specified in your instructions.
                """, ct);
            Console.WriteLine($"[Tester] {Truncate(testerResponse, 300)}");

            ParseTestReport(testerResponse, metrics);

            // All tests pass AND runtime verified → done
            if (metrics.Verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[Orchestrator] ✅ Tester verdict: PASS");
                break;
            }

            // Tests fail and we have retries left → send feedback to coder
            if (retry < _config.MaxRetriesPerTask)
            {
                metrics.RetryCount++;
                var issuesSummary = metrics.Issues.Count > 0
                    ? "Issues found:\n- " + string.Join("\n- ", metrics.Issues)
                    : "See tester report for details.";

                Console.WriteLine($"[Orchestrator] Verdict: {metrics.Verdict} — sending feedback to coder");

                // Push tester's test files so coder can see them
                await _gitManager.RepairRemoteAsync(testerClone, ct);
                await _gitManager.PushBranchAsync(testerClone, $"coder/{branchName}", ct);

                // Pull tester's changes into coder clone and send fix prompt
                await _gitManager.PullBranchAsync(coderClone, $"coder/{branchName}", ct);
                var fixResponse = await RunWorkerAsync(
                    WorkerRole.Coder, coderClone, "coder",
                    $"""
                    The tester found issues with your code. Their verdict: {metrics.Verdict}

                    {issuesSummary}

                    Tester's full report:
                    {Truncate(testerResponse, 2000)}

                    Fix the code AND update your unit tests. Commit your changes.
                    Do NOT run git push — the orchestrator handles that.
                    """, ct);
                Console.WriteLine($"[Coder:fix] {Truncate(fixResponse, 200)}");

                await _gitManager.RepairRemoteAsync(coderClone, ct);
                await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);
            }
        }

        // Phase 3: Merge if tester passed
        if (metrics.Verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
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

        var verifyResponse = await RunWorkerAsync(
            WorkerRole.Tester, verifyClone, "tester",
            $"""
            You are verifying the merged code on the main branch for this goal: {_config.Goal}

            A merge was just completed to main. Run ALL tests on the main branch to verify
            nothing was broken by the merge.
            Follow your standard testing workflow and produce a TEST_REPORT block.
            Do NOT run git push.
            """, ct);
        Console.WriteLine($"[Tester:verify] {Truncate(verifyResponse, 300)}");

        var verifyMetrics = new IterationMetrics { Iteration = iteration };
        ParseTestReport(verifyResponse, verifyMetrics);

        if (verifyMetrics.Verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
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

        // Send failure feedback to coder
        Console.WriteLine("[Orchestrator] Phase 3c: Coder fixing post-merge failures");
        var issuesSummary = verifyMetrics.Issues.Count > 0
            ? "Issues found after merge:\n- " + string.Join("\n- ", verifyMetrics.Issues)
            : "See tester report for details.";

        await _gitManager.PullBranchAsync(coderClone, $"coder/{branchName}", ct);
        var fixResponse = await RunWorkerAsync(
            WorkerRole.Coder, coderClone, "coder",
            $"""
            Your code PASSED testing on the feature branch but FAILED after merging to main.
            This means the merge introduced issues (likely conflicts with existing code on main).

            Tester verdict after merge: {verifyMetrics.Verdict}
            {issuesSummary}

            Tester's full report:
            {Truncate(verifyResponse, 2000)}

            Fix the code so it works correctly when merged with main.
            Commit your changes. Do NOT run git push.
            """, ct);
        Console.WriteLine($"[Coder:postmerge-fix] {Truncate(fixResponse, 200)}");

        await _gitManager.RepairRemoteAsync(coderClone, ct);
        await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);

        // Re-test the fix on the branch before re-merging
        Console.WriteLine("[Orchestrator] Phase 3d: Re-testing after post-merge fix");
        var retestClone = await _gitManager.CreateWorkerCloneAsync($"retest-{iteration}", ct);
        await _gitManager.PullBranchAsync(retestClone, $"coder/{branchName}", ct);

        var retestResponse = await RunWorkerAsync(
            WorkerRole.Tester, retestClone, "tester",
            $"""
            You are re-testing code after a post-merge fix for this goal: {_config.Goal}
            The coder fixed issues that appeared after merging to main.
            Run ALL tests and produce a TEST_REPORT block.
            Do NOT run git push.
            """, ct);
        Console.WriteLine($"[Tester:retest] {Truncate(retestResponse, 300)}");

        ParseTestReport(retestResponse, metrics);

        if (!metrics.Verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
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
            // LLMs sometimes use "Status:" instead of "verdict:"
            else if (TryParseField(trimmed, "status:", out var statusVal))
                metrics.Verdict = statusVal;
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

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    public async ValueTask DisposeAsync()
    {
        await _workerManager.DisposeAsync();
    }
}
