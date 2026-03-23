using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotHive.Metrics;
using CopilotHive.Services;
using CopilotHive.Telemetry;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain that runs inside the orchestrator container.
/// The Brain has two jobs: plan iteration phases and craft worker prompts.
/// Uses SharpCoder's IChatClient + AgentSession for LLM communication.
/// </summary>
public sealed class DistributedBrain : IDistributedBrain, IAsyncDisposable
{
    private readonly string _modelOverride;
    private readonly ILogger<DistributedBrain> _logger;
    private readonly MetricsTracker? _metricsTracker;
    private IChatClient? _chatClient;
    private IChatClient? _wrappedClient;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    private readonly string _systemPrompt;
    private readonly List<AITool> _brainTools;

    /// <summary>
    /// Serialises all Brain LLM calls so that <see cref="_lastToolCallResult"/> is
    /// never overwritten by a concurrent goal's tool lambda.
    /// </summary>
    private readonly SemaphoreSlim _brainCallGate = new(1, 1);

    /// <summary>
    /// Stores the last tool call result captured by the Brain tool lambdas.
    /// Written by tool lambdas (from FunctionInvokingChatClient thread),
    /// read after ExecuteAsync completes.
    /// Access is serialised by <see cref="_brainCallGate"/>.
    /// </summary>
    private volatile BrainToolCallResult? _lastToolCallResult;

    private const string DefaultSystemPrompt = """
        You are the CopilotHive Orchestrator Brain — a product owner and project manager.
        You have two jobs:
        1. Plan iteration phases using the report_iteration_plan tool
        2. Craft clear, specific prompts for workers when asked

        RULES:
        - Do NOT run shell commands, tools, or file operations
        - When planning iterations, always call report_iteration_plan
        - When crafting prompts, respond with ONLY the prompt text — no tool calls, no JSON, no markdown formatting
        - Never include git checkout/branch/switch/push commands in prompts — infrastructure handles branching
        - Never include framework-specific build/test commands — workers use /build and /test skills
        """;

    /// <summary>
    /// Initialises a new <see cref="DistributedBrain"/> that connects directly to an LLM provider.
    /// </summary>
    /// <param name="modelOverride">Model string, optionally with provider prefix (e.g. "copilot/claude-sonnet-4.6").</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="metricsTracker">Optional tracker used to include historical metrics in prompts.</param>
    /// <param name="agentsManager">Optional manager used to load the orchestrator's AGENTS.md.</param>
    public DistributedBrain(string modelOverride, ILogger<DistributedBrain> logger,
        MetricsTracker? metricsTracker = null, Agents.AgentsManager? agentsManager = null)
    {
        _modelOverride = modelOverride;
        _logger = logger;
        _metricsTracker = metricsTracker;

        _brainTools = BuildBrainTools();

        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _systemPrompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
            ? DefaultSystemPrompt
            : $"{DefaultSystemPrompt}\n\n{orchestratorInstructions}";
    }

    /// <summary>
    /// Creates the IChatClient for the configured model/provider.
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Brain connecting with model '{Model}'…", _modelOverride);

        _chatClient = SDK.ChatClientFactory.Create(_modelOverride);

        // Wrap with FunctionInvokingChatClient for tool auto-invocation
        _wrappedClient = new ChatClientBuilder(_chatClient)
            .UseFunctionInvocation(configure: fic =>
            {
                fic.MaximumIterationsPerRequest = 5; // Brain tools are simple, 5 iterations is plenty
                fic.IncludeDetailedErrors = true;
            })
            .Build();

        _logger.LogInformation("Brain connected via SharpCoder (model={Model})", _modelOverride);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the AIFunction tools that the Brain LLM can call.
    /// Only <c>report_iteration_plan</c> — prompt crafting uses plain text responses.
    /// </summary>
    private List<AITool> BuildBrainTools()
    {
        var validPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve", "merging" };

        return
        [
            AIFunctionFactory.Create(
                ([Description("Ordered phase names, e.g. [\"coding\",\"testing\",\"docwriting\",\"review\",\"merging\"]")] string[] phases,
                 [Description("JSON-encoded dict of phase name to instruction, e.g. {\"coding\":\"focus on...\",\"review\":\"check...\"}")] string phase_instructions,
                 [Description("Why you chose this iteration plan")] string reason) =>
                {
                    var invalidPhases = phases?.Where(p => !validPhases.Contains(p)).ToList() ?? [];
                    var error = Shared.ToolValidation.Check(
                        (phases is { Length: > 0 }, "phases must be a non-empty array"),
                        (invalidPhases.Count == 0,
                            $"invalid phase names: {string.Join(", ", invalidPhases)}. Valid: {string.Join(", ", validPhases)}"),
                        (!string.IsNullOrEmpty(reason), "reason is required"));
                    if (error is not null) return error;

                    _lastToolCallResult = new BrainToolCallResult("report_iteration_plan", new Dictionary<string, object?>
                    {
                        ["phases"] = phases, ["phase_instructions"] = phase_instructions, ["reason"] = reason,
                    });
                    return "Iteration plan recorded.";
                },
                "report_iteration_plan",
                "Report your iteration plan — which phases to run and in what order."),
        ];
    }

    /// <summary>Result captured from a Brain tool call.</summary>
    internal sealed record BrainToolCallResult(string ToolName, Dictionary<string, object?> Arguments);

    /// <summary>
    /// Gets or creates a dedicated AgentSession for a goal.
    /// </summary>
    private AgentSession GetOrCreateSession(GoalPipeline pipeline)
    {
        return _sessions.GetOrAdd(pipeline.GoalId, _ =>
        {
            var session = AgentSession.Create(pipeline.GoalId);
            _logger.LogInformation("Created new Brain session for goal {GoalId}", pipeline.GoalId);
            return session;
        });
    }

    /// <summary>Removes the session for a completed/failed goal.</summary>
    public Task CleanupGoalSessionAsync(string goalId)
    {
        if (_sessions.TryRemove(goalId, out _))
            _logger.LogInformation("Cleaned up Brain session for goal {GoalId}", goalId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restores a session for an in-progress goal on restart.
    /// Replays conversation history from the pipeline into a new session.
    /// </summary>
    public async Task ReprimeSessionAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        EnsureConnected();

        // Remove any stale session
        _sessions.TryRemove(pipeline.GoalId, out _);

        var session = AgentSession.Create(pipeline.GoalId);

        var entries = pipeline.Conversation;
        if (entries.Count > 0)
        {
            var summary = string.Join("\n\n", entries.Select(e => $"[{e.Role}]: {e.Content}"));
            var primingPrompt = $"""
                You are resuming work on goal '{pipeline.GoalId}':
                {pipeline.Description}

                Here is the conversation so far (summarized from {entries.Count} messages):
                {Truncate(summary, Constants.TruncationFull)}

                Continue from where we left off. The current phase is {pipeline.Phase},
                iteration {pipeline.Iteration}. Acknowledge briefly and await instructions.
                """;

            // Send priming message to populate session history
            await SendToBrainCoreAsync(session, primingPrompt, ct);
        }

        _sessions[pipeline.GoalId] = session;
        _logger.LogInformation("Re-primed Brain session for goal {GoalId} with {Count} conversation entries",
            pipeline.GoalId, entries.Count);
    }

    /// <summary>
    /// Asks the Brain to plan which phases should run during the current iteration.
    /// </summary>
    public async Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
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

        var historyContext = BuildMetricsHistoryContext();

        var failureContext = pipeline.Phase == GoalPhase.Failed || pipeline.TestRetries > 0 || pipeline.ReviewRetries > 0
            ? $"""
              Failure context:
              - Current phase: {pipeline.Phase}
              - Review retries so far: {pipeline.ReviewRetries}
              - Test retries so far: {pipeline.TestRetries}
              This is a retry — consider which phases need re-running and which can be skipped.
              """
            : "";

        var conversationSummary = pipeline.Conversation.Count > 0
            ? $"Conversation history ({pipeline.Conversation.Count} messages): " +
              Truncate(string.Join(" | ", pipeline.Conversation.Select(e => $"[{e.Role}] {e.Content}")), Constants.TruncationConversationSummary)
            : "";

        var prompt = $$"""
            Plan the workflow for iteration {{pipeline.Iteration}} of goal: {{pipeline.Description}}

            {{metricsContext}}
            {{historyContext}}
            {{failureContext}}
            {{conversationSummary}}

            Decide the ordered phases for this iteration. Consider:
            - Is this a documentation-only change? (coder edits, then docwriter — may skip testing)
            - Is this a retry after failure? (what phases need re-running)
            - What does the metrics history suggest?
            IMPORTANT: Always include the docwriting phase — the doc-writer updates the CHANGELOG,
            README, and XML doc comments. Even internal changes need changelog entries.
            Include the improve phase to let the improver refine agents.md guidance based on
            how the iteration went — especially when steps needed retries or produced issues.

            Available phases: coding, testing, docwriting, review, improve, merging

            Call the `report_iteration_plan` tool with:
            - phases: ordered list of phase names
            - phase_instructions: JSON object with per-phase instructions (e.g. {"coding": "focus on...", "review": "check..."})
            - reason: why this plan
            """;

        if (_wrappedClient is null)
        {
            _logger.LogWarning("Brain not connected — using default iteration plan for goal {GoalId}", pipeline.GoalId);
            return IterationPlan.Default();
        }

        try
        {
            var session = GetOrCreateSession(pipeline);
            var (response, toolCall) = await Shared.CopilotRetryPolicy.ExecuteAsync(
                () => SendToBrainCoreAsync(session, prompt, ct),
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain iteration plan call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                },
                ct);

            pipeline.Conversation.Add(new ConversationEntry("user", prompt));
            pipeline.Conversation.Add(new ConversationEntry("assistant", response));

            if (toolCall is not { ToolName: "report_iteration_plan" })
                throw new InvalidOperationException(
                    $"Brain did not call report_iteration_plan for {pipeline.GoalId}. Tool: {toolCall?.ToolName ?? "none"}");

            var plan = BuildIterationPlanFromToolCall(toolCall);

            if (plan is { Phases.Count: > 0 })
            {
                _logger.LogInformation(
                    "Brain planned iteration {Iteration} for goal {GoalId}: [{Phases}] — {Reason}",
                    pipeline.Iteration, pipeline.GoalId,
                    string.Join(", ", plan.Phases), plan.Reason ?? "no reason");
                return plan;
            }

            _logger.LogWarning("Failed to parse iteration plan from Brain response: {Response}",
                Truncate(response, Constants.TruncationShort));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain iteration planning failed for goal {GoalId}", pipeline.GoalId);
            pipeline.Conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}"));
        }

        _logger.LogInformation("Using default iteration plan for goal {GoalId}", pipeline.GoalId);
        return IterationPlan.Default();
    }

    internal static IterationPlan MapIterationPlan(IterationPlanDto dto)
    {
        var phases = new List<GoalPhase>();
        foreach (var name in dto.Phases)
        {
            if (Enum.TryParse<GoalPhase>(name, ignoreCase: true, out var phase)
                && phase is not (GoalPhase.Planning or GoalPhase.Done or GoalPhase.Failed))
            {
                phases.Add(phase);
            }
        }

        var instructions = new Dictionary<GoalPhase, string>();
        if (dto.PhaseInstructions is not null)
        {
            foreach (var (key, value) in dto.PhaseInstructions)
            {
                if (Enum.TryParse<GoalPhase>(key, ignoreCase: true, out var phase) && value is not null)
                    instructions[phase] = value;
            }
        }

        return new IterationPlan
        {
            Phases = phases,
            PhaseInstructions = instructions,
            Reason = dto.Reason,
        };
    }

    /// <summary>
    /// Builds an <see cref="IterationPlan"/> from a <c>report_iteration_plan</c> tool call result.
    /// </summary>
    internal static IterationPlan BuildIterationPlanFromToolCall(BrainToolCallResult toolCall)
    {
        var args = toolCall.Arguments;

        var phaseNames = new List<string>();
        if (args.TryGetValue("phases", out var phasesVal) && phasesVal is not null)
        {
            if (phasesVal is string[] arr)
                phaseNames = arr.ToList();
            else if (phasesVal is JsonElement je && je.ValueKind == JsonValueKind.Array)
                phaseNames = je.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            else if (phasesVal is IEnumerable<object> list)
                phaseNames = list.Select(x => x?.ToString() ?? "").ToList();
        }

        Dictionary<string, string>? phaseInstructions = null;
        var piStr = GetStringArg(args, "phase_instructions");
        if (!string.IsNullOrEmpty(piStr))
        {
            try
            {
                phaseInstructions = JsonSerializer.Deserialize<Dictionary<string, string>>(piStr, ProtocolJson.Options);
            }
            catch (JsonException)
            {
                // Best-effort: ignore unparsable phase instructions
            }
        }

        var dto = new IterationPlanDto
        {
            Phases = phaseNames,
            PhaseInstructions = phaseInstructions,
            Reason = GetStringArg(args, "reason"),
        };

        return MapIterationPlan(dto);
    }

    /// <summary>DTO for deserializing the Brain's iteration plan JSON response.</summary>
    internal sealed record IterationPlanDto
    {
        public List<string> Phases { get; init; } = [];
        public Dictionary<string, string>? PhaseInstructions { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Asks the Brain to craft a prompt for the specified phase's worker.
    /// </summary>
    public async Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        var roleName = phase.ToWorkerRole().ToRoleName();
        var historyContext = BuildMetricsHistoryContext(3);

        var phaseInstructions = "";
        if (pipeline.Plan?.PhaseInstructions.TryGetValue(phase, out var instructions) == true)
            phaseInstructions = $"\nPhase instructions from the plan:\n{instructions}";

        var roleInstruction = phase switch
        {
            GoalPhase.Coding => """
                - For coders: Tell them to start implementing immediately — read the relevant files, make code changes, use /build skill, use /test skill, and commit with `git add -A && git commit`. NEVER include git checkout, git branch, or git push commands. NEVER include dotnet/npm/cargo commands — only reference /build and /test skills.
                """,
            GoalPhase.Review => """
                - For reviewers: tell them to run `git diff origin/<base-branch>...HEAD` to review ALL changes. The base branch and feature branch are provided in the workspace context. The `origin/` prefix is required because the clone only has remote tracking refs. Produce a REVIEW_REPORT.
                """,
            GoalPhase.Testing => """
                - For testers: tell them to build, run the test skill, write integration tests, produce a TEST_REPORT
                """,
            GoalPhase.DocWriting => """
                - For docwriters: tell them to update README, CHANGELOG, and XML doc comments based on the code changes on the branch, build to verify, and commit. Produce a DOC_REPORT.
                """,
            GoalPhase.Improve => """
                - For improvers: tell them to analyze iteration results and update *.agents.md files directly using file tools. Do NOT tell them to run git commands — the infrastructure commits and pushes automatically.
                """,
            _ => throw new InvalidOperationException($"Unhandled phase in CraftPromptAsync: '{phase}'"),
        };

        var prompt = $$"""
            Craft a prompt for the {{roleName}} worker.

            Goal: {{pipeline.Description}}
            Iteration: {{pipeline.Iteration}}
            Current phase: {{phase}}
            {{phaseInstructions}}
            {{(additionalContext is not null ? $"\nAdditional context:\n{additionalContext}" : "")}}
            {{(historyContext.Length > 0 ? $"\n{historyContext}" : "")}}

            The worker has access to project skills (e.g. /build, /test) that describe how to build and test this project.
            Tell the worker to use those skills instead of hardcoding framework-specific commands.

            Rules for the prompt you craft:
            - The branch is already checked out by the infrastructure — do NOT mention branch names
            - NEVER include git checkout, git branch, git switch, or git push commands — the infrastructure handles all branching
            - NEVER include framework-specific build/test commands (dotnet build, npm test, etc.) — tell workers to use /build and /test skills
            {{roleInstruction}}
            - Include any context from previous phases that would help the worker

            Respond with ONLY the prompt text — no tool calls, no JSON, no markdown wrapping.
            """;

        if (_wrappedClient is null)
        {
            _logger.LogWarning("Brain not connected — using fallback prompt for {GoalId} phase {Phase}",
                pipeline.GoalId, phase);
            return $"Work on: {pipeline.Description}";
        }

        try
        {
            var session = GetOrCreateSession(pipeline);

            var craftedPrompt = await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    _logger.LogDebug("Brain craft-prompt request for {GoalId} (phase={Phase}):\n{Prompt}",
                        pipeline.GoalId, phase, Truncate(prompt, Constants.TruncationVerbose));

                    var (response, _) = await SendToBrainCoreAsync(session, prompt, ct);

                    _logger.LogDebug("Brain craft-prompt response for {GoalId}:\n{Response}",
                        pipeline.GoalId, Truncate(response, Constants.TruncationVerbose));

                    pipeline.Conversation.Add(new ConversationEntry("user", prompt));
                    pipeline.Conversation.Add(new ConversationEntry("assistant", response));

                    if (string.IsNullOrWhiteSpace(response))
                        throw new InvalidOperationException(
                            $"Brain returned empty prompt for {pipeline.GoalId} phase {phase}");

                    return response;
                },
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain craft-prompt failed for {GoalId} (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        pipeline.GoalId, attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                    pipeline.Conversation.Add(new ConversationEntry("system", $"Error on attempt {attempt}: {ex.Message}"));
                },
                ct);

            return craftedPrompt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain failed to craft prompt for {GoalId} phase {Phase} — using fallback",
                pipeline.GoalId, phase);
            pipeline.Conversation.Add(new ConversationEntry("system", $"CraftPrompt error: {ex.Message}"));
            return $"Work on: {pipeline.Description}";
        }
    }

    internal static string? GetStringArg(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var val) ? val?.ToString() : null;

    internal static List<string>? GetStringArrayArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val is null)
            return null;

        if (val is string[] arr)
            return arr.ToList();

        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

        if (val is IEnumerable<object> list)
            return list.Select(x => x?.ToString() ?? "").ToList();

        return null;
    }

    /// <summary>
    /// Sends a prompt to the Brain LLM with tool auto-invocation.
    /// Returns the response text and any captured tool call result.
    /// </summary>
    private async Task<(string Text, BrainToolCallResult? ToolCall)> SendToBrainCoreAsync(
        AgentSession session, string prompt, CancellationToken ct)
    {
        EnsureConnected();

        await _brainCallGate.WaitAsync(ct);
        try
        {
            _lastToolCallResult = null;

            var chatOptions = new ChatOptions
            {
                Tools = _brainTools.Cast<AITool>().ToList(),
                ToolMode = ChatToolMode.Auto
            };

            // Build messages: system prompt + session history + new user message
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _systemPrompt)
            };

            if (session.MessageHistory.Count > 0)
                messages.AddRange(session.MessageHistory);

            messages.Add(new ChatMessage(ChatRole.User, prompt));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));

            var response = await _wrappedClient!.GetResponseAsync(messages, chatOptions, cts.Token);
            var responseText = response.Text ?? "";

            // Log usage
            if (response.Usage is not null)
            {
                _logger.LogDebug(
                    "Brain Usage: model={Model} in={InputTokens} out={OutputTokens}",
                    response.ModelId, response.Usage.InputTokenCount, response.Usage.OutputTokenCount);
                FileTracer.WriteBrainUsage(response, "/app/state/traces-brain.jsonl");

                session.InputTokensUsed += response.Usage.InputTokenCount ?? 0;
                session.OutputTokensUsed += response.Usage.OutputTokenCount ?? 0;
            }

            // Update session history (skip system prompt)
            session.MessageHistory = response.Messages
                .Where(m => m.Role != ChatRole.System)
                .ToList();
            session.LastActivityAt = DateTimeOffset.UtcNow;

            _logger.LogDebug("Brain response ({Length} chars), tool={Tool}",
                responseText.Length, _lastToolCallResult?.ToolName ?? "none");

            return (responseText, _lastToolCallResult);
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    /// <summary>
    /// Applies the first-non-null wins rule for model tier.
    /// </summary>
    public static void ApplyModelTierIfNotSet(GoalPipeline pipeline, string? modelTier)
    {
        if (pipeline.LatestModelTier != ModelTier.Default || modelTier is null)
            return;

        pipeline.LatestModelTier = ModelTierExtensions.ParseModelTier(modelTier);
    }

    /// <summary>
    /// Builds a concise metrics history summary from the last N iterations.
    /// </summary>
    private string BuildMetricsHistoryContext(int maxIterations = 5)
    {
        if (_metricsTracker is null || _metricsTracker.History.Count == 0)
            return "";

        var history = _metricsTracker.History;
        var recent = history.Skip(Math.Max(0, history.Count - maxIterations)).ToList();
        var sb = new StringBuilder();

        sb.AppendLine("Metrics History (last " + recent.Count + " iterations):");
        foreach (var m in recent)
        {
            var issues = m.Issues.Count > 0 ? $", issues: [{string.Join(", ", m.Issues)}]" : "";
            sb.AppendLine($"  - Iteration {m.Iteration}: {m.Verdict}, " +
                $"{m.PassedTests}/{m.TotalTests} tests, {m.CoveragePercent:F1}% coverage{issues}");
        }

        if (recent.Count >= 2)
        {
            var first = recent[0];
            var last = recent[^1];
            var coverageDelta = last.CoveragePercent - first.CoveragePercent;
            var testDelta = last.TotalTests - first.TotalTests;

            var coverageTrend = coverageDelta switch
            {
                > 1.0 => $"improving (+{coverageDelta:F1}% over {recent.Count} iterations)",
                < -1.0 => $"degrading ({coverageDelta:F1}% over {recent.Count} iterations)",
                _ => "stable",
            };

            sb.AppendLine($"Trend: Coverage {coverageTrend}, " +
                $"test count {(testDelta > 0 ? "growing" : testDelta < 0 ? "shrinking" : "stable")}");
        }

        if (history.Count >= 2)
        {
            var comparison = _metricsTracker.CompareWithPrevious(history[^1]);
            if (comparison is not null)
                sb.AppendLine($"Delta vs previous: {comparison}");
        }

        return sb.ToString();
    }

    private void EnsureConnected()
    {
        if (_wrappedClient is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    internal static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>Disposes the underlying chat client.</summary>
    public ValueTask DisposeAsync()
    {
        _sessions.Clear();
        _wrappedClient?.Dispose();
        _chatClient?.Dispose();
        _brainCallGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
