using CopilotHive.Configuration;
using CopilotHive.Copilot;
using CopilotHive.Metrics;
using CopilotHive.Workers;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered orchestrator brain. Maintains a persistent container and conversation
/// to make all tactical decisions: prompt crafting, output interpretation, workflow control.
/// </summary>
public interface IOrchestratorBrain : IAsyncDisposable
{
    /// <summary>Start the persistent orchestrator container.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator to plan which phases are needed for this goal.
    /// Returns a list of planned actions in order.
    /// </summary>
    Task<OrchestratorDecision> PlanIterationAsync(
        int iteration, string goal, IterationMetrics? previousMetrics, CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator to craft a prompt for a specific worker role.
    /// </summary>
    Task<string> CraftPromptAsync(
        string workerRole, string goal, int iteration, string branchName,
        string? additionalContext, CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator to interpret worker output and determine the verdict.
    /// </summary>
    Task<OrchestratorDecision> InterpretOutputAsync(
        string workerRole, string workerOutput, CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator what to do next, given the current state.
    /// </summary>
    Task<OrchestratorDecision> DecideNextStepAsync(
        string currentPhase, string context, CancellationToken ct = default);

    /// <summary>
    /// Inform the orchestrator about something that happened (for context continuity).
    /// </summary>
    Task InformAsync(string information, CancellationToken ct = default);
}

/// <summary>
/// Real implementation backed by a persistent Docker container running an LLM.
/// </summary>
public sealed class OrchestratorBrain : IOrchestratorBrain
{
    private readonly HiveConfiguration _config;
    private readonly IWorkerManager _workerManager;
    private readonly ICopilotClientFactory _clientFactory;
    private readonly string _workspacePath;
    private readonly string _agentsMdPath;

    private WorkerInfo? _worker;
    private ICopilotWorkerClient? _client;

    // Conversation log — included in prompts for context continuity
    private readonly List<ConversationEntry> _conversation = [];

    private const string SystemPrompt = """
        You are the CopilotHive Orchestrator — a product owner and project manager for a
        multi-agent software development system. You make ALL tactical decisions:

        - You decide which workers to spawn and in what order
        - You craft the prompts sent to workers (coder, reviewer, tester, improver)
        - You interpret worker output to determine verdicts and extract metrics
        - You decide whether to retry, skip, or proceed to the next phase

        IMPORTANT: Always respond with valid JSON matching the requested schema.
        Do NOT wrap the JSON in markdown code fences. Return ONLY the JSON object.
        """;

    public OrchestratorBrain(
        HiveConfiguration config,
        IWorkerManager workerManager,
        ICopilotClientFactory clientFactory,
        string workspacePath,
        string agentsMdPath)
    {
        _config = config;
        _workerManager = workerManager;
        _clientFactory = clientFactory;
        _workspacePath = workspacePath;
        _agentsMdPath = agentsMdPath;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_worker is not null) return;

        var model = _config.GetModelForRole("orchestrator");
        _worker = await _workerManager.SpawnWorkerAsync(
            WorkerRole.Orchestrator, _workspacePath, _agentsMdPath, model, ct);

        _client = _clientFactory.Create(_worker.Port);
        await _client.ConnectAsync(ct);

        Console.WriteLine($"[Brain] Orchestrator LLM started on port {_worker.Port} (model: {model})");

        // Prime with system prompt
        _conversation.Add(new ConversationEntry("system", SystemPrompt));
        var primeResponse = await _client.SendTaskAsync(SystemPrompt, ct);
        _conversation.Add(new ConversationEntry("assistant", primeResponse));
    }

    public async Task<OrchestratorDecision> PlanIterationAsync(
        int iteration, string goal, IterationMetrics? previousMetrics, CancellationToken ct = default)
    {
        var metricsContext = previousMetrics is not null
            ? $"""
              Previous iteration metrics:
              - Verdict: {previousMetrics.Verdict}
              - Tests: {previousMetrics.PassedTests}/{previousMetrics.TotalTests} passed
              - Coverage: {previousMetrics.CoveragePercent}%
              - Retries: {previousMetrics.RetryCount}
              - Issues: {string.Join(", ", previousMetrics.Issues)}
              """
            : "This is the first iteration — no previous metrics.";

        var prompt = $$"""
            Plan iteration {{iteration}} for this goal:
            {{goal}}

            {{metricsContext}}

            Decide which phases are needed. Consider:
            - Is this a documentation-only change? (skip coder, maybe skip review)
            - Is this a code change? (needs coder → reviewer → tester → merge)
            - Is there context from previous iterations to address?

            Respond with JSON:
            {
              "action": "spawn_coder",
              "prompt": "<the prompt you want to send to the first worker>",
              "reason": "<why you chose this plan>"
            }

            Valid actions: spawn_coder, spawn_reviewer, spawn_tester, done, skip
            """;

        return await AskAsync<OrchestratorDecision>(prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.SpawnCoder,
                Reason = "Default: starting with coding phase",
            };
    }

    public async Task<string> CraftPromptAsync(
        string workerRole, string goal, int iteration, string branchName,
        string? additionalContext, CancellationToken ct = default)
    {
        var prompt = $$"""
            Craft a prompt for the {{workerRole}} worker.

            Goal: {{goal}}
            Iteration: {{iteration}}
            Branch: coder/{{branchName}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}

            Rules for the prompt you craft:
            - For coders: tell them the goal, the branch to work on, remind them to commit changes and NOT run git push
            - For reviewers: tell them to review the diff on the branch against main, produce a REVIEW_REPORT
            - For testers: tell them to build, run tests, write integration tests, produce a TEST_REPORT
            - Include any context from previous phases that would help the worker

            Respond with JSON:
            {
              "action": "spawn_{{workerRole}}",
              "prompt": "<the complete prompt to send to the worker>",
              "reason": "<why you crafted the prompt this way>"
            }
            """;

        var decision = await AskAsync<OrchestratorDecision>(prompt, ct);
        return decision?.Prompt ?? GetFallbackPrompt(workerRole, goal, iteration, branchName);
    }

    public async Task<OrchestratorDecision> InterpretOutputAsync(
        string workerRole, string workerOutput, CancellationToken ct = default)
    {
        var schema = workerRole.ToLowerInvariant() switch
        {
            "tester" => """
                {
                  "verdict": "PASS or FAIL",
                  "test_metrics": {
                    "build_success": true/false,
                    "total_tests": <number>,
                    "passed_tests": <number>,
                    "failed_tests": <number>,
                    "coverage_percent": <number>
                  },
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
            "reviewer" => """
                {
                  "review_verdict": "APPROVE or REQUEST_CHANGES",
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
            _ => """
                {
                  "verdict": "PASS or FAIL or COMPLETE",
                  "issues": ["<issue1>", "<issue2>"]
                }
                """,
        };

        var prompt = $$"""
            Interpret this {{workerRole}}'s output and extract structured data.

            === {{workerRole.ToUpperInvariant()}} OUTPUT (truncated) ===
            {{Truncate(workerOutput, 6000)}}
            === END OUTPUT ===

            Analyze the output carefully:
            - Did the worker succeed or fail at its task?
            - For testers: did all tests pass? What are the numbers?
            - For reviewers: did they approve or request changes? What issues did they find?
            - Extract any specific issues mentioned

            Respond with JSON:
            {{schema}}
            """;

        var decision = await AskAsync<OrchestratorDecision>(prompt, ct);
        if (decision is not null)
        {
            _conversation.Add(new ConversationEntry("system",
                $"[Interpreted {workerRole} output: verdict={decision.Verdict ?? decision.ReviewVerdict}]"));
        }
        return decision ?? new OrchestratorDecision
        {
            Action = OrchestratorActionType.Done,
            Verdict = "UNKNOWN",
            Reason = "Failed to interpret output",
        };
    }

    public async Task<OrchestratorDecision> DecideNextStepAsync(
        string currentPhase, string context, CancellationToken ct = default)
    {
        var prompt = $$"""
            Current phase: {{currentPhase}}
            {{context}}

            What should we do next?

            Respond with JSON:
            {
              "action": "<next action: spawn_coder, spawn_reviewer, spawn_tester, merge, done, skip>",
              "prompt": "<prompt for the worker, if spawning one>",
              "reason": "<why this is the right next step>"
            }
            """;

        return await AskAsync<OrchestratorDecision>(prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Reason = "Failed to decide — defaulting to done",
            };
    }

    public async Task InformAsync(string information, CancellationToken ct = default)
    {
        EnsureStarted();
        var response = await _client!.SendTaskAsync(
            $"[STATUS UPDATE] {information}\n\nAcknowledge briefly.", ct);
        _conversation.Add(new ConversationEntry("system", information));
        _conversation.Add(new ConversationEntry("assistant", Truncate(response, 200)));
    }

    private async Task<T?> AskAsync<T>(string prompt, CancellationToken ct) where T : class
    {
        EnsureStarted();

        _conversation.Add(new ConversationEntry("user", prompt));

        try
        {
            var response = await _client!.SendTaskAsync(prompt, ct);
            _conversation.Add(new ConversationEntry("assistant", Truncate(response, 500)));

            var parsed = ProtocolJson.ParseFromLlmResponse<T>(response);
            if (parsed is null)
                Console.WriteLine($"[Brain] ⚠ Failed to parse JSON from LLM response: {Truncate(response, 200)}");

            return parsed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Brain] ⚠ LLM query failed: {ex.Message}");
            _conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}"));
            return null;
        }
    }

    private void EnsureStarted()
    {
        if (_client is null)
            throw new InvalidOperationException("OrchestratorBrain not started. Call StartAsync first.");
    }

    private static string GetFallbackPrompt(string role, string goal, int iteration, string branchName) =>
        role.ToLowerInvariant() switch
        {
            "coder" => $"""
                You are working on this goal: {goal}
                This is iteration {iteration}. Work on the coder/{branchName} branch.
                Write the code and commit your changes with clear commit messages.
                Do NOT run git push — the orchestrator handles that.
                """,
            "reviewer" => $"""
                You are reviewing code changes for this goal: {goal}
                This is iteration {iteration}. The coder's work is on branch coder/{branchName}.
                Review the diff against main and produce a REVIEW_REPORT block.
                Do NOT modify any code. Do NOT run git push.
                """,
            "tester" => $"""
                You are testing code for this goal: {goal}
                This is iteration {iteration}. The coder's work is on branch coder/{branchName}.
                Build the project, run all tests, write integration tests, and produce a TEST_REPORT block.
                Do NOT run git push — the orchestrator handles that.
                """,
            _ => $"Work on this goal: {goal}",
        };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        if (_worker is not null)
        {
            try { await _workerManager.StopWorkerAsync(_worker.Id); }
            catch { /* Container may already be stopped */ }
            _worker = null;
        }
    }
}
