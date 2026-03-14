using System.ComponentModel;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace CopilotHive.Worker;

/// <summary>
/// Communicates with the Copilot CLI running in headless mode via the GitHub.Copilot.SDK.
/// The Copilot CLI is already running (started by entrypoint.sh); this class connects to it.
/// </summary>
public sealed class CopilotRunner : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private CopilotSession? _session;
    private readonly int _port;
    private CustomAgentConfig? _customAgent;
    private string? _role;
    private readonly WorkerLogger _log = new("Copilot");

    // Tool call bridge for custom tools — set before creating a session
    private IToolCallBridge? _toolBridge;
    private string? _currentTaskId;

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
        _client = new CopilotClient(new CopilotClientOptions
        {
            CliUrl = $"localhost:{port}",
            AutoStart = false,
        });
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
    /// Sets the custom agent configuration for this worker's role.
    /// Applied on the next session creation (ConnectAsync or ResetSessionAsync).
    /// </summary>
    public void SetCustomAgent(string role, string agentsMdContent)
    {
        _role = role;
        _customAgent = new CustomAgentConfig
        {
            Name = role,
            DisplayName = $"CopilotHive {char.ToUpperInvariant(role[0])}{role[1..]}",
            Description = $"CopilotHive worker agent for the {role} role",
            Prompt = agentsMdContent,
            Tools = GetToolsForRole(role),
        };
        Console.WriteLine($"[Copilot] Custom agent set for role '{role}' ({agentsMdContent.Length} chars, tools={(_customAgent.Tools is null ? "all" : $"[{string.Join(",", _customAgent.Tools)}]")})");
        _log.Debug($"Agent prompt:\n{agentsMdContent}");
    }

    private SessionConfig BuildSessionConfig()
    {
        var isImprover = _role == "improver";
        var config = new SessionConfig
        {
            // Improver is pure text-in/text-out with DenyAllPermissions;
            // streaming can cause hangs when the model tries tools that all get denied.
            Streaming = !isImprover,
            OnPermissionRequest = GetPermissionHandlerForRole(_role),
            CustomAgents = _customAgent is not null ? [_customAgent] : [],
        };

        // Register custom AIFunction tools for roles that have a tool bridge
        var tools = BuildCustomTools();
        if (tools.Count > 0)
            config.Tools = tools;

        return config;
    }

    /// <summary>
    /// Builds custom AIFunction tools that the Copilot model can call.
    /// These tools bridge back to the orchestrator via gRPC.
    /// </summary>
    private List<AIFunction> BuildCustomTools()
    {
        var tools = new List<AIFunction>();

        // Only register orchestrator tools for roles that have a bridge
        // and are not the improver (which has DenyAll permissions)
        if (_toolBridge is null || _role == "improver")
            return tools;

        tools.Add(AIFunctionFactory.Create(
            async ([Description("The question to ask the orchestrator")] string question) =>
            {
                var taskId = _currentTaskId ?? "unknown";
                _log.Info($"Tool call: ask_orchestrator('{question[..Math.Min(question.Length, 80)]}...')");
                var answer = await _toolBridge.AskOrchestratorAsync(taskId, question, CancellationToken.None);
                return answer;
            },
            "ask_orchestrator",
            "Ask the orchestrator a question about the task, architecture, or codebase. " +
            "Use this when you need clarification before making changes."));

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

        return tools;
    }

    /// <summary>Returns the tool whitelist for a given role. Null means all tools.</summary>
    internal static List<string>? GetToolsForRole(string? role) => role switch
    {
        // Improver is text-in/text-out only — restrict to no tools.
        // Using a non-existent tool name as a whitelist effectively blocks all tools
        // without hitting the SDK TypeError that an empty list causes.
        "improver" => ["none"],
        _ => null,              // coder, tester, reviewer get all tools
    };

    /// <summary>Returns the permission handler appropriate for the role.</summary>
    internal static PermissionRequestHandler GetPermissionHandlerForRole(string? role) => role switch
    {
        "improver" => DenyAllPermissions,
        "reviewer" => ReviewerPermissions,
        _ => PermissionHandler.ApproveAll,  // coder, tester
    };

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
        await _client.StartAsync();
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxConnectRetries; attempt++)
        {
            try
            {
                _session = await _client.CreateSessionAsync(BuildSessionConfig());
                Console.WriteLine($"[Copilot] Connected to Copilot CLI on port {_port} (attempt {attempt})");
                _log.Debug($"Session config: streaming={false}, customAgents={(_customAgent?.Name ?? "none")}");
                return;
            }
            catch (Exception ex) when (attempt < MaxConnectRetries)
            {
                lastException = ex;
                Console.WriteLine($"[Copilot] Connection attempt {attempt}/{MaxConnectRetries} failed: {ex.Message}");
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

        _session = await _client.CreateSessionAsync(config);

        var modelInfo = string.IsNullOrEmpty(model) ? "default" : model;
        Console.WriteLine($"[Copilot] Session reset (localhost:{_port}, agent={_customAgent?.Name ?? "none"}, model={modelInfo})");
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
                    break;
                case SessionIdleEvent:
                    done.TrySetResult(response);
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(new CopilotRunnerException(err.Data.Message));
                    break;
                // Streaming events for observability
                case AssistantMessageDeltaEvent:
                    // Incremental response text — we capture the final in AssistantMessageEvent
                    break;
                default:
                    // Log unhandled event types at debug level for discovery
                    _log.Debug($"SDK event: {evt.GetType().Name}");
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

        await _client.StopAsync();
    }
}

/// <summary>Represents an error raised by <see cref="CopilotRunner"/>.</summary>
public sealed class CopilotRunnerException(string message) : Exception(message);
