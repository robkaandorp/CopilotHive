using CopilotHive.Copilot;
using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain that runs inside the orchestrator container.
/// Uses the Copilot SDK to communicate with a local headless Copilot CLI instance.
/// Maintains per-goal conversation histories for context-aware multi-phase orchestration.
/// </summary>
public sealed class DistributedBrain : IDistributedBrain, IAsyncDisposable
{
    private readonly int _port;
    private readonly ILogger<DistributedBrain> _logger;
    private ICopilotWorkerClient? _client;

    private const string SystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager
        for a distributed multi-agent software development system. You make ALL tactical decisions:

        - You decide which workers to spawn and in what order
        - You craft the prompts sent to workers (coder, reviewer, tester, improver)
        - You interpret worker output to determine verdicts and extract metrics
        - You decide whether to retry, skip, or proceed to the next phase

        IMPORTANT: Always respond with valid JSON matching the requested schema.
        Do NOT wrap the JSON in markdown code fences. Return ONLY the JSON object.
        """;

    public DistributedBrain(int port, ILogger<DistributedBrain> logger)
    {
        _port = port;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new CopilotWorkerClient(_port) { MaxConnectRetries = 20, RetryDelay = TimeSpan.FromSeconds(5) };
        await _client.ConnectAsync(ct);
        _logger.LogInformation("Brain connected to Copilot on port {Port}", _port);

        // Prime the Brain with the system prompt
        var primeResponse = await _client.SendTaskAsync(SystemPrompt, ct);
        _logger.LogInformation("Brain primed: {Response}", Truncate(primeResponse, 200));
    }

    public async Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        var metricsContext = pipeline.Iteration > 1
            ? $"""
              Previous iteration metrics:
              - Tests: {pipeline.Metrics.PassedTests}/{pipeline.Metrics.TotalTests} passed
              - Coverage: {pipeline.Metrics.CoveragePercent}%
              - Review retries: {pipeline.ReviewRetries}
              - Test retries: {pipeline.TestRetries}
              - Issues: {string.Join(", ", pipeline.Metrics.Issues)}
              """
            : "This is the first iteration — no previous metrics.";

        var prompt = $$"""
            Plan the workflow for this goal:
            {{pipeline.Description}}

            {{metricsContext}}

            Decide which phase to start with. Consider:
            - Is this a documentation-only change? (just coder, maybe skip review)
            - Is this a code change? (needs coder → reviewer → tester → merge)
            - Is there context from previous iterations?

            Respond with JSON:
            {
              "action": "spawn_coder",
              "prompt": "<the prompt you want to send to the first worker>",
              "reason": "<why you chose this plan>"
            }

            Valid actions: spawn_coder, spawn_reviewer, spawn_tester, done, skip
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        return decision ?? new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            Reason = "Default: starting with coding phase",
        };
    }

    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext, CancellationToken ct = default)
    {
        var prompt = $$"""
            Craft a prompt for the {{workerRole}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Branch: {{pipeline.CoderBranch ?? "TBD"}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}

            Rules for the prompt you craft:
            - For coders: tell them the goal, the branch to work on, remind them to commit changes and NOT run git push
            - For reviewers: tell them to review the diff on the branch against the base branch, produce a REVIEW_REPORT
            - For testers: tell them to build, run tests, write integration tests, produce a TEST_REPORT
            - Include any context from previous phases that would help the worker

            Respond with JSON:
            {
              "action": "spawn_{{workerRole}}",
              "prompt": "<the complete prompt to send to the worker>",
              "reason": "<why you crafted the prompt this way>"
            }
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        return decision?.Prompt ?? GetFallbackPrompt(workerRole, pipeline);
    }

    public async Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, string workerRole, string workerOutput, CancellationToken ct = default)
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
            - For reviewers: did they approve or request changes? What issues?
            - Extract any specific issues mentioned

            Respond with JSON:
            {{schema}}
            """;

        var decision = await AskAsync(pipeline, prompt, ct);
        if (decision is not null)
        {
            pipeline.Conversation.Add(new ConversationEntry("system",
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
        GoalPipeline pipeline, string context, CancellationToken ct = default)
    {
        var prompt = $$"""
            Current goal: {{pipeline.Description}}
            Current phase: {{pipeline.Phase}}
            {{context}}

            What should we do next?

            Respond with JSON:
            {
              "action": "<next action: spawn_coder, spawn_reviewer, spawn_tester, merge, done, skip>",
              "prompt": "<prompt for the worker, if spawning one>",
              "reason": "<why this is the right next step>"
            }
            """;

        return await AskAsync(pipeline, prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Reason = "Failed to decide — defaulting to done",
            };
    }

    public async Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default)
    {
        EnsureConnected();
        var response = await _client!.SendTaskAsync(
            $"[STATUS UPDATE for goal '{pipeline.GoalId}'] {information}\n\nAcknowledge briefly.", ct);
        pipeline.Conversation.Add(new ConversationEntry("system", information));
        pipeline.Conversation.Add(new ConversationEntry("assistant", Truncate(response, 200)));
    }

    private async Task<OrchestratorDecision?> AskAsync(GoalPipeline pipeline, string prompt, CancellationToken ct)
    {
        EnsureConnected();

        // Build context-aware prompt with conversation history
        var contextualPrompt = BuildContextualPrompt(pipeline, prompt);
        pipeline.Conversation.Add(new ConversationEntry("user", prompt));

        try
        {
            var response = await _client!.SendTaskAsync(contextualPrompt, ct);
            pipeline.Conversation.Add(new ConversationEntry("assistant", Truncate(response, 500)));

            var parsed = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(response);
            if (parsed is null)
                _logger.LogWarning("Failed to parse Brain JSON response: {Response}", Truncate(response, 200));

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain LLM query failed for goal {GoalId}", pipeline.GoalId);
            pipeline.Conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}"));
            return null;
        }
    }

    private static string BuildContextualPrompt(GoalPipeline pipeline, string prompt)
    {
        if (pipeline.Conversation.Count == 0)
            return prompt;

        // Include recent conversation context (last 6 entries max to stay within token budget)
        var recentHistory = pipeline.Conversation
            .TakeLast(6)
            .Select(c => $"[{c.Role}]: {c.Content}");

        return $"""
            === CONVERSATION CONTEXT (goal: {pipeline.GoalId}) ===
            {string.Join("\n", recentHistory)}
            === END CONTEXT ===

            {prompt}
            """;
    }

    private static string GetFallbackPrompt(string role, GoalPipeline pipeline) =>
        role.ToLowerInvariant() switch
        {
            "coder" => $"""
                You are working on this goal: {pipeline.Description}
                This is iteration {pipeline.Iteration}. Work on the {pipeline.CoderBranch} branch.
                Write the code and commit your changes with clear commit messages.
                Do NOT run git push — the orchestrator handles that.
                """,
            "reviewer" => $"""
                You are reviewing code changes for this goal: {pipeline.Description}
                This is iteration {pipeline.Iteration}. The coder's work is on branch {pipeline.CoderBranch}.
                Review the diff against the base branch and produce a REVIEW_REPORT block with:
                - verdict: APPROVE or REQUEST_CHANGES
                - issues: list of issues found (if any)
                Do NOT modify any code. Do NOT run git push.
                """,
            "tester" => $"""
                You are testing code for this goal: {pipeline.Description}
                This is iteration {pipeline.Iteration}. The coder's work is on branch {pipeline.CoderBranch}.
                Build the project, run all tests, write integration tests, and produce a TEST_REPORT block with:
                - verdict: PASS or FAIL
                - build_success, total_tests, passed_tests, failed_tests, coverage_percent
                - issues: list of issues found (if any)
                Do NOT run git push — the orchestrator handles that.
                """,
            _ => $"Work on this goal: {pipeline.Description}",
        };

    private void EnsureConnected()
    {
        if (_client is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
