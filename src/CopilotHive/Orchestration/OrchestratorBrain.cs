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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator to plan which phases are needed for this goal.
    /// Returns a list of planned actions in order.
    /// </summary>
    /// <param name="iteration">One-based iteration number.</param>
    /// <param name="goal">Natural-language goal description.</param>
    /// <param name="previousMetrics">Metrics from the previous iteration, or <c>null</c> for the first iteration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the recommended first action.</returns>
    Task<OrchestratorDecision> PlanIterationAsync(
        int iteration, string goal, IterationMetrics? previousMetrics, CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator to craft a prompt for a specific worker role.
    /// </summary>
    /// <param name="workerRole">Role of the target worker (e.g. "coder", "reviewer", "tester").</param>
    /// <param name="goal">Natural-language goal description.</param>
    /// <param name="iteration">Current iteration number.</param>
    /// <param name="branchName">Feature branch the worker should operate on.</param>
    /// <param name="additionalContext">Optional extra context to include in the crafted prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The crafted prompt string to send to the worker.</returns>
    Task<string> CraftPromptAsync(
        string workerRole, string goal, int iteration, string branchName,
        string? additionalContext, CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator to interpret worker output and determine the verdict.
    /// </summary>
    /// <param name="workerRole">Role of the worker that produced the output (e.g. "tester", "reviewer").</param>
    /// <param name="workerOutput">Raw text output from the worker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> containing the extracted verdict and any issues.</returns>
    Task<OrchestratorDecision> InterpretOutputAsync(
        string workerRole, string workerOutput, CancellationToken ct = default);

    /// <summary>
    /// Ask the orchestrator what to do next, given the current state.
    /// </summary>
    /// <param name="currentPhase">Name of the current pipeline phase.</param>
    /// <param name="context">Textual context describing the current situation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the recommended next action.</returns>
    Task<OrchestratorDecision> DecideNextStepAsync(
        string currentPhase, string context, CancellationToken ct = default);

    /// <summary>
    /// Inform the orchestrator about something that happened (for context continuity).
    /// </summary>
    /// <param name="information">Human-readable status update to pass to the orchestrator.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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

    /// <summary>
    /// Initialises a new <see cref="OrchestratorBrain"/> that will spawn a dedicated orchestrator worker container.
    /// </summary>
    /// <param name="config">Hive configuration providing model and port settings.</param>
    /// <param name="workerManager">Manager used to spawn and stop the orchestrator container.</param>
    /// <param name="clientFactory">Factory used to connect to the orchestrator container.</param>
    /// <param name="workspacePath">Path passed to the spawned orchestrator container as its workspace.</param>
    /// <param name="agentsMdPath">Path to the orchestrator's AGENTS.md file.</param>
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

    /// <summary>
    /// Starts the orchestrator container and establishes a Copilot session primed with the system prompt.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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

    /// <summary>
    /// Asks the brain to plan which phases are needed for the specified iteration.
    /// </summary>
    /// <param name="iteration">One-based iteration number.</param>
    /// <param name="goal">Natural-language goal description.</param>
    /// <param name="previousMetrics">Metrics from the previous iteration, or <c>null</c> for the first iteration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the recommended first action.</returns>
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
              "reason": "<why you chose this plan>",
              "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
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

    /// <summary>
    /// Asks the brain to craft a task prompt for the specified worker role.
    /// </summary>
    /// <param name="workerRole">Role of the target worker (e.g. "coder").</param>
    /// <param name="goal">Natural-language goal description.</param>
    /// <param name="iteration">Current iteration number.</param>
    /// <param name="branchName">Feature branch the worker should operate on.</param>
    /// <param name="additionalContext">Optional extra context to include.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The crafted prompt string.</returns>
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
              "reason": "<why you crafted the prompt this way>",
              "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
            }
            """;

        var decision = await AskAsync<OrchestratorDecision>(prompt, ct);
        return decision?.Prompt ?? GetFallbackPrompt(workerRole, goal, iteration, branchName);
    }

    /// <summary>
    /// Asks the brain to interpret raw worker output and produce a structured verdict.
    /// </summary>
    /// <param name="workerRole">Role of the worker that produced the output.</param>
    /// <param name="workerOutput">Raw text output from the worker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> containing the extracted verdict.</returns>
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
            {{Truncate(workerOutput, Constants.TruncationVeryLong)}}
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

    /// <summary>
    /// Asks the brain what the next action should be given the current phase and context.
    /// </summary>
    /// <param name="currentPhase">Name of the current pipeline phase.</param>
    /// <param name="context">Textual context describing the current situation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OrchestratorDecision"/> with the recommended next action.</returns>
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
              "reason": "<why this is the right next step>",
              "model_tier": "standard or premium — use premium for complex, high-stakes, or retry tasks"
            }
            """;

        return await AskAsync<OrchestratorDecision>(prompt, ct)
            ?? new OrchestratorDecision
            {
                Action = OrchestratorActionType.Done,
                Reason = "Failed to decide — defaulting to done",
            };
    }

    /// <summary>
    /// Informs the brain of an event so it can maintain context continuity.
    /// </summary>
    /// <param name="information">Human-readable status update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task InformAsync(string information, CancellationToken ct = default)
    {
        EnsureStarted();
        var response = await _client!.SendTaskAsync(
            $"[STATUS UPDATE] {information}\n\nAcknowledge briefly.", ct);
        _conversation.Add(new ConversationEntry("system", information));
        _conversation.Add(new ConversationEntry("assistant", Truncate(response, Constants.TruncationShort)));
    }

    private async Task<T?> AskAsync<T>(string prompt, CancellationToken ct) where T : class
    {
        EnsureStarted();

        _conversation.Add(new ConversationEntry("user", prompt));

        try
        {
            var response = await _client!.SendTaskAsync(prompt, ct);
            _conversation.Add(new ConversationEntry("assistant", Truncate(response, Constants.TruncationMedium)));

            var parsed = ProtocolJson.ParseFromLlmResponse<T>(response);
            if (parsed is null)
                Console.WriteLine($"[Brain] ⚠ Failed to parse JSON from LLM response: {Truncate(response, Constants.TruncationShort)}");

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

    /// <summary>Stops the orchestrator worker container and disposes the Copilot client.</summary>
    /// <returns>A value task that represents the asynchronous dispose operation.</returns>
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
            catch (Exception ex) { Console.WriteLine($"[Brain] Failed to stop orchestrator worker: {ex.Message}"); }
            _worker = null;
        }
    }
}
