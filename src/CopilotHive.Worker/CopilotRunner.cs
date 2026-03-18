using System.ComponentModel;
using CopilotHive.Worker.Telemetry;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace CopilotHive.Worker;

/// <summary>
/// Communicates with the Copilot CLI running in headless mode via the GitHub.Copilot.SDK.
/// The Copilot CLI is already running (started by entrypoint.sh); this class connects to it.
/// </summary>
public sealed class CopilotRunner : IAsyncDisposable
{
    private CopilotClient? _client;
    private CopilotSession? _session;
    private readonly int _port;
    private string _currentRole = "worker";
    private CustomAgentConfig? _customAgent;
    private readonly WorkerLogger _log = new("Copilot");

    // Tool call bridge for custom tools — set before creating a session
    private IToolCallBridge? _toolBridge;
    private string? _currentTaskId;

    // Structured test metrics reported via the report_test_results tool call
    private TestResultReport? _lastTestReport;

    /// <summary>Structured test results reported by the tester via tool call, or null if not reported.</summary>
    public TestResultReport? LastTestReport => _lastTestReport;

    /// <summary>Clears any previously reported test results. Call before starting a new task.</summary>
    public void ClearTestReport() => _lastTestReport = null;

    /// <summary>Maximum number of connection attempts before giving up.</summary>
    public int MaxConnectRetries { get; init; } = WorkerConstants.MaxConnectRetries;
    /// <summary>Delay between consecutive connection attempts.</summary>
    public TimeSpan RetryDelay { get; init; } = WorkerConstants.RetryDelay;

    /// <summary>
    /// Initialises a new <see cref="CopilotRunner"/> connecting on the given <paramref name="port"/>.
    /// </summary>
    /// <param name="port">The TCP port the Copilot CLI headless server is listening on.</param>
    public CopilotRunner(int port = WorkerConstants.DefaultAgentPort)
    {
        _port = port;
    }

    /// <summary>
    /// Sets the tool call bridge for custom tools. Must be called before session creation.
    /// </summary>
    public void SetToolBridge(IToolCallBridge? bridge) => _toolBridge = bridge;

    /// <summary>
    /// Sets the current task ID for tool call context.
    /// </summary>
    public void SetCurrentTaskId(string? taskId) => _currentTaskId = taskId;

    /// <summary>
    /// Sets the custom agent configuration for the specified role.
    /// Updates the current role label used for telemetry and permissions.
    /// </summary>
    public void SetCustomAgent(string role, string agentsMdContent)
    {
        _currentRole = role;
        _customAgent = new CustomAgentConfig
        {
            Name = role,
            DisplayName = $"CopilotHive {char.ToUpperInvariant(role[0])}{role[1..]}",
            Description = $"CopilotHive worker agent for the {role} role",
            Prompt = agentsMdContent,
            Tools = GetToolsForRole(role),
            Infer = false,
        };
        _log.Info($"Custom agent set for role '{role}' ({agentsMdContent.Length} chars, tools={(_customAgent.Tools is null ? "all" : $"[{string.Join(",", _customAgent.Tools)}]")})");
        _log.Debug($"Agent prompt:\n{agentsMdContent}");
    }

    private SessionConfig BuildSessionConfig()
    {
        _client ??= new CopilotClient(new CopilotClientOptionsWithTelemetry
        {
            CliUrl = $"localhost:{_port}",
            AutoStart = false,
            Telemetry = new TelemetryConfig
            {
                FilePath = $"/app/state/otel-{_currentRole}.jsonl",
                ExporterType = "file",
                SourceName = $"copilothive-worker-{_currentRole}",
                CaptureContent = true
            }
        });

        var config = new SessionConfig
        {
            Streaming = true,
            OnPermissionRequest = GetPermissionHandlerForRole(_currentRole),
            CustomAgents = _customAgent is not null ? [_customAgent] : [],
        };

        // Register custom AIFunction tools for roles that have a tool bridge
        var tools = BuildCustomTools();
        if (tools.Count > 0)
            config.Tools = tools;

        // Enable the native ask_user tool: when the model calls ask_user, forward
        // the question to the orchestrator's Brain via gRPC and return the answer.
        if (_toolBridge is not null)
        {
            config.OnUserInputRequest = async (request, _) =>
            {
                var taskId = _currentTaskId ?? "unknown";
                var question = request.Question ?? "";
                if (request.Choices is { Count: > 0 })
                    question += $"\nChoices: {string.Join(", ", request.Choices)}";

                _log.Info($"ask_user: '{question[..Math.Min(question.Length, 100)]}...'");
                var answer = await _toolBridge.AskOrchestratorAsync(taskId, question, CancellationToken.None);
                return new UserInputResponse { Answer = answer };
            };
        }

        return config;
    }

    /// <summary>
    /// Builds custom AIFunction tools that the Copilot model can call.
    /// These tools bridge back to the orchestrator via gRPC.
    /// Note: ask_orchestrator is replaced by the native OnUserInputRequest handler (ask_user tool).
    /// </summary>
    private List<AIFunction> BuildCustomTools()
    {
        var tools = new List<AIFunction>();

        // Only register orchestrator tools for roles that have a bridge
        if (_toolBridge is null)
            return tools;

        tools.Add(AIFunctionFactory.Create(
            async ([Description("Current status")] string status,
                   [Description("Details about what you're doing")] string details) =>
            {
                var taskId = _currentTaskId ?? "unknown";
                _log.Info($"Tool call: report_progress('{status}')");
                await _toolBridge.ReportProgressAsync(taskId, status, details, CancellationToken.None);
                return "Progress reported.";
            },
            "report_progress",
            "Report your current progress to the orchestrator. " +
            "Call this periodically to show what you're working on."));

        tools.Add(AIFunctionFactory.Create(
            ([Description("PASS or FAIL")] string verdict,
             [Description("Total number of tests")] int totalTests,
             [Description("Number of tests that passed")] int passedTests,
             [Description("Number of tests that failed")] int failedTests,
             [Description("Code coverage percentage (0-100), or -1 if not available")] double coveragePercent,
             [Description("Build succeeded (true/false)")] bool buildSuccess,
             [Description("List of issues found, empty if none")] string[] issues) =>
            {
                _log.Info($"Tool call: report_test_results(verdict={verdict}, total={totalTests}, passed={passedTests}, failed={failedTests})");
                _lastTestReport = new TestResultReport
                {
                    Verdict = verdict,
                    TotalTests = totalTests,
                    PassedTests = passedTests,
                    FailedTests = failedTests,
                    CoveragePercent = coveragePercent >= 0 ? coveragePercent : null,
                    BuildSuccess = buildSuccess,
                    Issues = issues.ToList(),
                };
                return "Test results recorded. The infrastructure will use these structured metrics.";
            },
            "report_test_results",
            "Report structured test results. REQUIRED for testers after running tests. " +
            "Call this ONCE with the final aggregated test counts and verdict."));

        return tools;
    }

    /// <summary>Returns the tool whitelist for a given role. Null means all tools.</summary>
    internal static List<string>? GetToolsForRole(string? role) => role switch
    {
        _ => null,  // all roles get full tool access (permissions control what's allowed)
    };

    /// <summary>Returns the permission handler appropriate for the role.</summary>
    internal static PermissionRequestHandler GetPermissionHandlerForRole(string? role) => role switch
    {
        "improver" => ImproverPermissions,
        "reviewer" => ReviewerPermissions,
        _ => PermissionHandler.ApproveAll,  // coder, tester
    };

    /// <summary>Improver: can read and write *.agents.md files but cannot execute shell commands.</summary>
    private static readonly PermissionRequestHandler ImproverPermissions =
        (request, _) => Task.FromResult(new PermissionRequestResult
        {
            Kind = request.Kind is "shell" or "command"
                ? PermissionRequestResultKind.DeniedByRules
                : PermissionRequestResultKind.Approved,
        });

    private static readonly PermissionRequestHandler DenyAllPermissions =
        (_, _) => Task.FromResult(new PermissionRequestResult
        {
            Kind = PermissionRequestResultKind.DeniedByRules,
        });

    /// <summary>Reviewer: static analysis only — can read files and run commands (git diff) but cannot write files.</summary>
    private static readonly PermissionRequestHandler ReviewerPermissions =
        (request, _) => Task.FromResult(new PermissionRequestResult
        {
            Kind = request.Kind == "write"
                ? PermissionRequestResultKind.DeniedByRules
                : PermissionRequestResultKind.Approved,
        });

    /// <summary>
    /// Connects to the Copilot CLI, retrying up to <see cref="MaxConnectRetries"/> times.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var sessionConfig = BuildSessionConfig();
        await _client!.StartAsync();
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxConnectRetries; attempt++)
        {
            try
            {
                _session = await _client.CreateSessionAsync(sessionConfig);
                await SelectCustomAgentAsync();
                _log.Info($"Connected to Copilot CLI on port {_port} (attempt {attempt})");
                _log.Debug($"Session config: streaming={false}, customAgents={(_customAgent?.Name ?? "none")}");
                return;
            }
            catch (Exception ex) when (attempt < MaxConnectRetries)
            {
                lastException = ex;
                _log.Error($"Connection attempt {attempt}/{MaxConnectRetries} failed: {ex.Message}");
                await Task.Delay(RetryDelay, ct);
            }
        }

        throw new CopilotRunnerException(
            $"Failed to connect to Copilot CLI on port {_port} after {MaxConnectRetries} attempts: {lastException?.Message}");
    }

    /// <summary>
    /// Dispose the current session and create a fresh one, ensuring no context leaks between tasks.
    /// The new session picks up the latest CustomAgent configuration and optionally switches to a new model.
    /// </summary>
    /// <param name="model">Optional model ID to use for the new session. If empty/null, uses the default.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ResetSessionAsync(string? model = null, CancellationToken ct = default)
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        var config = BuildSessionConfig();
        if (!string.IsNullOrEmpty(model))
            config.Model = model;

        _session = await _client!.CreateSessionAsync(config);
        await SelectCustomAgentAsync();

        var modelInfo = string.IsNullOrEmpty(model) ? "default" : model;
        _log.Info($"Session reset (localhost:{_port}, agent={_customAgent?.Name ?? "none"}, model={modelInfo})");
    }

    /// <summary>
    /// Pre-selects the custom agent on the active session via RPC so that the
    /// role-specific system prompt is active from the very first user message.
    /// Without this call the runtime relies on inference to match the agent,
    /// which is unreliable with generic descriptions.
    /// </summary>
    private async Task SelectCustomAgentAsync()
    {
        if (_session is null || _customAgent is null) return;
        try
        {
            await _session.Rpc.Agent.SelectAsync(_customAgent.Name);
            _log.Info($"Pre-selected agent '{_customAgent.Name}' via RPC");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to pre-select agent '{_customAgent.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Send a prompt to the Copilot CLI and return the response text.
    /// With streaming enabled, captures tool execution events and intent updates.
    /// </summary>
    public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        if (_session is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        _log.Info($"Sending prompt ({prompt.Length} chars) to localhost:{_port}");
        _log.LogBlock("PROMPT TO MODEL", prompt);

        var done = new TaskCompletionSource<string>();
        var response = "";

        using var subscription = _session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    response = msg.Data.Content;
                    Console.WriteLine($"[SDK] AssistantMessage ({response.Length} chars)");
                    Console.Out.Flush();
                    break;
                case AssistantUsageEvent usage:
                    Console.WriteLine($"[SDK] Usage: model={usage.Data.Model} in={usage.Data.InputTokens} out={usage.Data.OutputTokens} cost={usage.Data.Cost:F4} duration={usage.Data.Duration:F0}ms");
                    Console.Out.Flush();
                    FileTracer.WriteUsage(usage.Data, $"/app/state/traces-{_currentRole}.jsonl", _currentRole);
                    break;
                case SessionIdleEvent:
                    Console.WriteLine("[SDK] SessionIdle");
                    Console.Out.Flush();
                    done.TrySetResult(response);
                    break;
                case SessionErrorEvent err:
                    Console.WriteLine($"[SDK] SessionError: {err.Data.Message}");
                    Console.Out.Flush();
                    done.TrySetException(new CopilotRunnerException(err.Data.Message));
                    break;
                case SubagentSelectedEvent selected:
                    _log.Info($"🎯 Agent selected: {selected.Data.AgentDisplayName} (tools: {(selected.Data.Tools is { Length: > 0 } t ? string.Join(", ", t) : "all")})");
                    break;
                case SubagentStartedEvent started:
                    _log.Info($"▶ Sub-agent started: {started.Data.AgentDisplayName} — {started.Data.AgentDescription}");
                    break;
                case SubagentCompletedEvent completed:
                    _log.Info($"✅ Sub-agent completed: {completed.Data.AgentDisplayName}");
                    break;
                case SubagentFailedEvent failed:
                    _log.Error($"❌ Sub-agent failed: {failed.Data.AgentDisplayName} — {failed.Data.Error}");
                    break;
                case SubagentDeselectedEvent:
                    _log.Info("↩ Agent deselected, returning to parent");
                    break;
                // Content deltas — write text and flush on newlines
                case AssistantMessageDeltaEvent delta:
                    Console.Write(delta.Data.DeltaContent);
                    if (delta.Data.DeltaContent.Contains('\n'))
                        Console.Out.Flush();
                    break;
                // Muted events — no output needed
                case AssistantStreamingDeltaEvent:
                case AssistantReasoningDeltaEvent:
                case AssistantReasoningEvent:
                case AssistantTurnStartEvent:
                case AssistantTurnEndEvent:
                case SessionUsageInfoEvent:
                case PendingMessagesModifiedEvent:
                case UserMessageEvent:
                case PermissionRequestedEvent:
                case PermissionCompletedEvent:
                case ToolExecutionStartEvent:
                case ToolExecutionCompleteEvent:
                    break;
                default:
                    // Log unknown event types for discovery
                    Console.WriteLine($"[SDK] {evt.GetType().Name}");
                    Console.Out.Flush();
                    break;
            }
        });

        using var ctReg = ct.Register(() => done.TrySetCanceled());

        await _session.SendAsync(new MessageOptions { Prompt = prompt });
        var result = await done.Task;

        _log.Info($"Received response ({result.Length} chars)");
        _log.LogBlock("MODEL RESPONSE", result);
        return result;
    }

    /// <summary>Disposes the active Copilot session and stops the underlying client.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();

        if (_client is not null)
            await _client.StopAsync();
    }
}

/// <summary>Represents an error raised by <see cref="CopilotRunner"/>.</summary>
public sealed class CopilotRunnerException(string message) : Exception(message);
