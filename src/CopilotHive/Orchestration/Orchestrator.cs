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
    private readonly DockerWorkerManager _dockerManager;
    private readonly GitWorkspaceManager _gitManager;
    private readonly AgentsManager _agentsManager;
    private readonly MetricsTracker _metricsTracker;

    public Orchestrator(HiveConfiguration config)
    {
        _config = config;
        _dockerManager = new DockerWorkerManager(config);
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
                Console.WriteLine($"[Orchestrator] ✅ All {metrics.TotalTests} tests passing. Iteration complete.");
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

        var coderAgentsMd = _agentsManager.GetAgentsMdPath("coder");
        var coderWorker = await _dockerManager.SpawnWorkerAsync(
            WorkerRole.Coder, coderClone, coderAgentsMd, ct);

        try
        {
            // Give the container time to start
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            await using var coderClient = new CopilotWorkerClient(
                coderWorker.Port, _config.Model, _config.GitHubToken);
            await coderClient.ConnectAsync(ct);

            var coderPrompt = $"""
                You are working on this goal: {_config.Goal}

                This is iteration {iteration}. Work on the coder/{branchName} branch.
                Write the code, commit your changes with clear commit messages.
                When done, push your branch with: git push origin coder/{branchName}
                """;

            var coderResponse = await coderClient.SendTaskAsync(coderPrompt, ct);
            Console.WriteLine($"[Coder] {Truncate(coderResponse, 200)}");
        }
        finally
        {
            await _dockerManager.StopWorkerAsync(coderWorker.Id, ct);
        }

        // Push coder's work to the bare repo
        await _gitManager.PushBranchAsync(coderClone, $"coder/{branchName}", ct);

        // Phase 2: Tester
        Console.WriteLine("[Orchestrator] Phase 2: Testing");
        var testerClone = await _gitManager.CreateWorkerCloneAsync($"tester-{iteration}", ct);
        await _gitManager.PullBranchAsync(testerClone, $"coder/{branchName}", ct);

        var testerAgentsMd = _agentsManager.GetAgentsMdPath("tester");
        var testerWorker = await _dockerManager.SpawnWorkerAsync(
            WorkerRole.Tester, testerClone, testerAgentsMd, ct);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            await using var testerClient = new CopilotWorkerClient(
                testerWorker.Port, _config.Model, _config.GitHubToken);
            await testerClient.ConnectAsync(ct);

            var testerPrompt = $"""
                You are testing code for this goal: {_config.Goal}

                Review the code on the current branch. Write comprehensive tests.
                Run the tests and report results in this exact format at the end:

                METRICS:
                total_tests: <number>
                passed_tests: <number>
                failed_tests: <number>
                coverage_percent: <number>

                Commit your test files and push with: git push origin coder/{branchName}
                """;

            var testerResponse = await testerClient.SendTaskAsync(testerPrompt, ct);
            Console.WriteLine($"[Tester] {Truncate(testerResponse, 200)}");

            ParseMetrics(testerResponse, metrics);
        }
        finally
        {
            await _dockerManager.StopWorkerAsync(testerWorker.Id, ct);
        }

        // Phase 3: Merge if tests pass
        if (metrics.FailedTests == 0 && metrics.TotalTests > 0)
        {
            Console.WriteLine("[Orchestrator] Phase 3: Merging to main");
            // Use the tester clone (has both code and tests)
            var (success, output) = await _gitManager.MergeToMainAsync(
                testerClone, $"coder/{branchName}", ct);

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

    private static void ParseMetrics(string response, IterationMetrics metrics)
    {
        // Parse the structured metrics from the tester's response
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("total_tests:") && int.TryParse(trimmed[12..].Trim(), out var total))
                metrics.TotalTests = total;
            else if (trimmed.StartsWith("passed_tests:") && int.TryParse(trimmed[13..].Trim(), out var passed))
                metrics.PassedTests = passed;
            else if (trimmed.StartsWith("failed_tests:") && int.TryParse(trimmed[13..].Trim(), out var failed))
                metrics.FailedTests = failed;
            else if (trimmed.StartsWith("coverage_percent:") && double.TryParse(trimmed[17..].Trim(), out var coverage))
                metrics.CoveragePercent = coverage;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    public async ValueTask DisposeAsync()
    {
        await _dockerManager.DisposeAsync();
    }
}
