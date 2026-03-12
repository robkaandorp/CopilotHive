using System.Diagnostics;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Copilot;
using CopilotHive.Git;
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

    public Orchestrator(HiveConfiguration config)
        : this(config,
               new DockerWorkerManager(config),
               new CopilotClientFactory(config.Model, config.GitHubToken))
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

        await _gitManager.InitBareRepoAsync(ct);

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
                    _agentsManager.RollbackAgentsMd("coder");
                    _agentsManager.RollbackAgentsMd("tester");
                }
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

            var (success, output) = await _gitManager.MergeToMainAsync(
                mergeClone, $"coder/{branchName}", ct);

            if (!success)
            {
                Console.WriteLine($"[Orchestrator] Merge conflict: {Truncate(output, 200)}");
                // Future: delegate to merge worker
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
        var agentsMdPath = _agentsManager.GetAgentsMdPath(agentRole);
        var worker = await _workerManager.SpawnWorkerAsync(role, clonePath, agentsMdPath, ct);

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

    private static void ParseTestReport(string response, IterationMetrics metrics)
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
