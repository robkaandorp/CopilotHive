#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CopilotHive.Shared;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using SharpCoder;
using OllamaSharp;

namespace CopilotHive.Worker;

public sealed class SharpCoderRunner : IAgentRunner
{
    private readonly WorkerLogger _log = new("SharpCoder");
    private readonly bool _verboseLogging = Environment.GetEnvironmentVariable("VERBOSE_LOGGING") == "true";
    private readonly Func<string?, IChatClient>? _clientFactory;
    private IChatClient? _chatClient;
    private string _currentModel = "(default)";
    private ReasoningEffort? _currentReasoning;

    /// <summary>
    /// Initializes a new <see cref="SharpCoderRunner"/>. Call <see cref="ConnectAsync"/>
    /// before invoking <see cref="SendPromptAsync"/> to create the chat client.
    /// </summary>
    public SharpCoderRunner() { }

    /// <summary>
    /// Internal constructor for unit testing: injects a pre-created <see cref="IChatClient"/>
    /// and model name, bypassing <see cref="CopilotHive.SDK.ChatClientFactory"/> so that tests
    /// can run without real LLM credentials.
    /// </summary>
    /// <param name="chatClient">The chat client to use for agent execution.</param>
    /// <param name="model">The model identifier to record in log output.</param>
    internal SharpCoderRunner(IChatClient chatClient, string model)
    {
        _chatClient = chatClient;
        _currentModel = model;
        _clientFactory = _ => chatClient;
    }

    private IToolCallBridge? _toolBridge;
    private string? _currentTaskId;
    private WorkerRole _currentRole;
    private string? _customAgentSystemPrompt;

    /// <summary>Current agent session; set via <see cref="SetSession"/> before <see cref="SendPromptAsync"/>.</summary>
    private AgentSession? _session;

    private TestResultReport? _lastTestReport;
    private WorkerReport? _lastWorkerReport;

    public TestResultReport? LastTestReport => _lastTestReport;
    public WorkerReport? LastWorkerReport => _lastWorkerReport;

    public void ClearTestReport() => _lastTestReport = null;
    public void ClearWorkerReport() => _lastWorkerReport = null;

    public void SetToolBridge(IToolCallBridge? bridge) => _toolBridge = bridge;
    public void SetCurrentTaskId(string? taskId) => _currentTaskId = taskId;

    public void SetCustomAgent(WorkerRole role, string agentsMdContent)
    {
        _currentRole = role;
        _customAgentSystemPrompt = agentsMdContent;
    }

    /// <summary>
    /// Builds the full system prompt for the given <paramref name="role"/> by combining the
    /// hardcoded role prompt with any learned heuristics from <paramref name="agentsMdContent"/>.
    /// </summary>
    /// <param name="role">The worker role whose hardcoded prompt to use.</param>
    /// <param name="agentsMdContent">Optional AGENTS.md content to append as learned heuristics.</param>
    /// <returns>
    /// The combined system prompt string. If <paramref name="agentsMdContent"/> is non-empty,
    /// it is appended after a <c>\n\n# Learned Heuristics\n\n</c> separator.
    /// </returns>
    internal static string BuildRoleSystemPrompt(WorkerRole role, string? agentsMdContent)
    {
        const string SharedPreamble = """
            INFRASTRUCTURE RULES (these are enforced by the system and cannot be overridden):
            - NEVER run `git push` — the infrastructure handles pushing automatically.
            - NEVER run `git checkout`, `git branch`, or `git switch` — the infrastructure handles branching.
            - When the goal description is ambiguous, files-to-change seem incomplete, or acceptance criteria conflict, call `request_clarification` instead of guessing.
            """;

        var roleSpecific = role switch
        {
            WorkerRole.Coder => $"""
                {SharedPreamble}

                # Coder

                You are a software developer. **Implement changes by editing files** — not describing them.
                Every task requires you to edit files, build, test, and commit.

                A text-only response without file edits is a **failure**.

                ## Reporting Your Changes (MANDATORY)

                After edits, builds, tests, and commits, you MUST call the `report_code_changes` tool with:
                - `verdict`: "PASS" if you successfully implemented and committed, "FAIL" if you could not
                - `filesModified`: array of files you changed (e.g. ["src/module.ext", "tests/moduleTests.ext"])
                - `summary`: brief description of what you changed and why
                """,

            WorkerRole.Tester => $"""
                {SharedPreamble}

                # Tester

                You are a QA engineer responsible for comprehensive testing of the codebase. You go
                beyond unit tests — you verify that the system actually works as a whole.

                ## Reporting Your Results (MANDATORY)

                After all testing, you MUST call the `report_test_results` tool with:
                - `verdict`: "PASS" or "FAIL"
                - `totalTests`: total number of tests run
                - `passedTests`: number that passed
                - `failedTests`: number that failed
                - `coveragePercent`: coverage percentage, or -1 if not measured
                - `buildSuccess`: true if the build succeeded
                - `issues`: array of issue descriptions (empty if none)

                NEVER report PASS if any test is failing.
                """,

            WorkerRole.Reviewer => $"""
                {SharedPreamble}

                # Reviewer

                You are a senior code reviewer. Review diffs for correctness, quality, and convention
                adherence. Focus on bugs, security, logic errors, and maintainability — not style.

                Do NOT modify code or run `git push`.

                ## Reporting Your Verdict (MANDATORY)

                After reviewing, you MUST call the `report_review_verdict` tool with:
                - `verdict`: "APPROVE" or "REQUEST_CHANGES"
                - `issues`: array of issue descriptions (prefix each with [CRITICAL], [MAJOR], or [MINOR])
                - `summary`: one-paragraph overview of your findings

                - **APPROVE**: Code correct, ready for testing. Zero critical issues.
                - **REQUEST_CHANGES**: Critical or major issues must be fixed first.
                - **CRITICAL**: Bugs, security, data loss, missing files. Must fix.
                - **MAJOR**: Missing error handling, missing tests, API violations. Should fix.
                - **MINOR**: Naming, refactoring suggestions, doc gaps. Nice-to-have.
                """,

            WorkerRole.DocWriter => $"""
                {SharedPreamble}

                # Doc Writer

                You are a technical documentation specialist. Your job is to update project documentation
                to reflect code changes made on the current feature branch.

                Do NOT edit source code files. Do NOT write or modify test code. Do NOT run tests or build.

                ## Reporting Your Changes (MANDATORY)

                After your work, you MUST call the `report_doc_changes` tool with:
                - `verdict`: "PASS" if you successfully updated documentation, "FAIL" if you could not
                - `filesUpdated`: array of files you changed (e.g. ["CHANGELOG.md", "README.md"])
                - `summary`: brief description of what you documented
                """,

            WorkerRole.Improver => $"""
                {SharedPreamble}

                # Improver

                You are an expert at analysing software development iteration outcomes and improving
                agent instructions to produce better results in the next iteration.

                You have direct access to the `agents/` folder containing `*.agents.md` files.
                Use the file tools (view, edit) to read and modify these files directly.
                You **cannot** run shell commands — file reading and editing only.

                The updated agents.md file MUST NOT exceed 4000 characters. Count characters before finalising.

                **Never remove or weaken safety constraints** — do not remove instructions about git workflow,
                test requirements, output format compliance, or tool call contracts.

                Only edit `*.agents.md` files — do not create new files, rename files, or touch anything
                outside the agents/ folder.
                """,

            WorkerRole.Unspecified => SharedPreamble,

            _ => throw new InvalidOperationException($"No hardcoded system prompt defined for WorkerRole '{role}'."),
        };

        if (string.IsNullOrWhiteSpace(agentsMdContent))
            return roleSpecific;

        return roleSpecific + "\n\n# Learned Heuristics\n\n" + agentsMdContent;
    }

    /// <inheritdoc/>
    public void SetSession(object? session) => _session = session as AgentSession;

    /// <inheritdoc/>
    public object? GetSession() => _session;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _log.Info("Initializing SharpCoderRunner IChatClient...");
        _chatClient = CreateChatClient();
        return Task.CompletedTask;
    }

    public Task ResetSessionAsync(string? model = null, CancellationToken ct = default)
    {
        _log.Info($"Resetting session. Requested model: {model ?? "default"}");
        _chatClient?.Dispose();
        _chatClient = _clientFactory != null ? _clientFactory(model) : CreateChatClient(model);
        _session = null;
        return Task.CompletedTask;
    }

    public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        if (_chatClient == null) throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var stopwatch = Stopwatch.StartNew();
        _log.Info($"Executing task as {_currentRole} with model {_currentModel}. WorkDir: {workDir}");
        
        var options = new AgentOptions
        {
            WorkDirectory = workDir,
            MaxSteps = 500,
            SystemPrompt = BuildRoleSystemPrompt(_currentRole, _customAgentSystemPrompt),
            CustomTools = BuildCustomTools(),
            EnableBash = _currentRole != WorkerRole.Improver,
            EnableFileWrites = _currentRole != WorkerRole.Reviewer,
            ReasoningEffort = _currentReasoning,
        };

        // Write pre-execution diagnostics so we can inspect inputs even if the LLM call hangs or is killed
        WriteDiagnosticsFile(null, prompt, TimeSpan.Zero, options, "pre");

        var agent = new CodingAgent(_chatClient, options);
        AgentResult result;
        if (_session != null)
        {
            result = await agent.ExecuteAsync(_session, prompt, ct);
        }
        else
        {
            _session = AgentSession.Create(Guid.NewGuid().ToString("N"));
            result = await agent.ExecuteAsync(_session, prompt, ct);
        }

        stopwatch.Stop();
        var elapsedSecs = stopwatch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _log.Info($"Task finished in {elapsedSecs}s (status={result.Status}, toolCalls={result.ToolCallCount})");

        // Log diagnostics when available
        if (result.Diagnostics is { } diag)
        {
            _log.Info($"Diagnostics: systemPrompt={diag.SystemPrompt.Length} chars, userMessage={diag.UserMessage.Length} chars, " +
                      $"historyMessages={diag.SessionHistoryCount}, totalMessages={diag.TotalMessageCount}");
            _log.Info($"Diagnostics: tools=[{string.Join(", ", diag.ToolNames)}], bash={diag.EnableBash}, " +
                      $"fileWrites={diag.EnableFileWrites}, skills={diag.SkillsEnabled}, autoWorkspace={diag.AutoLoadedWorkspaceInstructions}");
        }

        // Write post-execution diagnostics with full result
        WriteDiagnosticsFile(result, prompt, stopwatch.Elapsed, options, "post");

        _log.Info($"AgentResult: status={result.Status}, toolCalls={result.ToolCallCount}, model={result.ModelId}, finish={result.FinishReason}");
        if (result.Usage != null)
        {
            _log.Info($"Context: inputTokens={result.Usage.InputTokenCount}, outputTokens={result.Usage.OutputTokenCount}, totalTokens={result.Usage.TotalTokenCount}");
        }
        if (result.Messages != null)
        {
            _log.Info($"AgentResult: {result.Messages.Count} messages total");
            foreach (var msg in result.Messages)
            {
                _log.Info($"  [{msg.Role}] {SummarizeMessage(msg)}");
            }
        }

        if (result.Status != "Success")
        {
            _log.Error($"Agent finished with non-success status: {result.Status} - {result.Message}");
        }

        return result.Message;
    }

    public ValueTask DisposeAsync()
    {
        _chatClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string SummarizeMessage(ChatMessage msg)
    {
        const int ArgValueMaxLength = 100;
        const int PreviewMaxLength = 200;

        var functionCall = msg.Contents?.OfType<FunctionCallContent>().FirstOrDefault();
        if (functionCall != null)
        {
            var firstArg = functionCall.Arguments?.FirstOrDefault();
            if (firstArg.HasValue)
            {
                var argValue = firstArg.Value.Value?.ToString() ?? string.Empty;
                if (argValue.Length > ArgValueMaxLength)
                    argValue = argValue.Substring(0, ArgValueMaxLength);
                return $"tool:{functionCall.Name}({firstArg.Value.Key}=\"{argValue}\")";
            }
            return $"tool:{functionCall.Name}()";
        }

        var functionResult = msg.Contents?.OfType<FunctionResultContent>().FirstOrDefault();
        if (functionResult != null)
        {
            var raw = functionResult.Result?.ToString() ?? string.Empty;
            return $"result:{functionResult.CallId} \u2192 {SummarizeToolResult(raw)}";
        }

        var text = msg.Text;
        if (text != null && text.Length > PreviewMaxLength)
            text = text.Substring(0, PreviewMaxLength);
        return text ?? string.Empty;
    }

    /// <summary>
    /// Produces a compact one-line summary of a tool result instead of dumping raw content.
    /// </summary>
    private static string SummarizeToolResult(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "(empty)";

        var lines = raw.Split('\n');
        var lineCount = lines.Length;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(raw);

        // Short single-line results can be shown inline
        if (lineCount == 1 && raw.Length <= 120)
            return $"\"{raw}\"";

        return $"{byteCount} bytes, {lineCount} lines";
    }

    private static readonly string DiagnosticsDir =
        Environment.GetEnvironmentVariable("DIAGNOSTICS_DIR") ?? Path.Combine(Path.GetTempPath(), "copilothive-diagnostics");

    private void WriteDiagnosticsFile(AgentResult? result, string userPrompt, TimeSpan elapsed, AgentOptions options, string phase)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsDir);

            var taskId = _currentTaskId ?? "unknown";
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var fileName = $"{timestamp}_{taskId}_{phase}.json";
            var filePath = Path.Combine(DiagnosticsDir, fileName);

            var toolNames = options.CustomTools
                .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
                .ToList();

            // For pre-execution: use options directly; for post: use diagnostics from result
            var diag = result?.Diagnostics;
            var doc = new
            {
                phase,
                taskId,
                role = _currentRole.ToString(),
                model = _currentModel,
                reasoning = _currentReasoning?.ToString(),
                timestamp = DateTimeOffset.UtcNow,
                elapsedSeconds = elapsed.TotalSeconds,
                status = result?.Status,
                toolCallCount = result?.ToolCallCount,
                finishReason = result?.FinishReason?.ToString(),
                usage = result?.Usage is { } u ? new
                {
                    inputTokens = u.InputTokenCount,
                    outputTokens = u.OutputTokenCount,
                    totalTokens = u.TotalTokenCount
                } : null,
                session = new
                {
                    sessionHistoryCount = diag?.SessionHistoryCount ?? 0,
                    totalMessageCount = diag?.TotalMessageCount ?? 0,
                    maxSteps = options.MaxSteps,
                    enableBash = options.EnableBash,
                    enableFileWrites = options.EnableFileWrites,
                    autoLoadedWorkspaceInstructions = options.AutoLoadWorkspaceInstructions,
                    skillsEnabled = options.EnableSkills,
                    reasoningEffort = options.ReasoningEffort?.ToString(),
                    workDirectory = options.WorkDirectory,
                    customToolNames = toolNames,
                    allToolNames = diag?.ToolNames ?? (IReadOnlyList<string>)toolNames
                },
                systemPrompt = diag?.SystemPrompt ?? options.SystemPrompt ?? "(not yet assembled)",
                userMessage = userPrompt,
                agentResponse = result?.Message
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(filePath, json);
            _log.Info($"Diagnostics ({phase}) written to {filePath} ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to write diagnostics file: {ex.Message}");
        }
    }

    private IChatClient CreateChatClient(string? modelOverride = null)
    {
        var (provider, model, reasoning) = CopilotHive.SDK.ChatClientFactory.ParseProviderModelAndReasoning(modelOverride);
        _currentModel = model ?? "(default)";
        _currentReasoning = reasoning;
        _log.Info($"Creating chat client: provider={provider}, model={_currentModel}" +
            (reasoning.HasValue ? $", reasoning={reasoning.Value}" : ""));
        return CopilotHive.SDK.ChatClientFactory.Create(modelOverride);
    }

    private IList<AITool> BuildCustomTools()
    {
        var tools = new List<AITool>();

        if (_toolBridge != null)
        {
            tools.Add(AIFunctionFactory.Create(
                async ([Description("Short status summary")] string status, 
                       [Description("Detailed progress explanation")] string details) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: report_progress({status})");
                    await _toolBridge.ReportProgressAsync(_currentTaskId, status, details, CancellationToken.None);
                    return "Progress reported.";
                },
                "report_progress",
                "Report current progress to the orchestrator."
            ));

            tools.Add(AIFunctionFactory.Create(
                async ([Description("The question to ask the orchestrator")] string question) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: request_clarification({question})");
                    var response = await _toolBridge.RequestClarificationAsync(_currentTaskId, question, CancellationToken.None);
                    return response;
                },
                "request_clarification",
                "Ask the orchestrator for clarification when the goal description is ambiguous, files-to-change seem incomplete, or acceptance criteria conflict. Do NOT silently work around ambiguities — ask first."
            ));
        }

        if (_currentRole == WorkerRole.Tester)
            tools.Add(BuildTestResultsTool());

        if (_currentRole == WorkerRole.Reviewer)
            tools.Add(BuildReviewVerdictTool());

        if (_currentRole == WorkerRole.Coder)
            tools.Add(BuildCodeChangesTool());

        if (_currentRole == WorkerRole.DocWriter)
            tools.Add(BuildDocChangesTool());

        if (_currentRole == WorkerRole.Improver)
            tools.Add(BuildFileSizesTool());

        return tools;
    }

    private AITool BuildTestResultsTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("Total number of tests")] int totalTests,
         [Description("Number of tests that passed")] int passedTests,
         [Description("Number of tests that failed")] int failedTests,
         [Description("Code coverage percentage (0-100), or -1 if not available")] double coveragePercent,
         [Description("Build succeeded (true/false)")] bool buildSuccess,
         [Description("List of issues found, empty if none")] string[] issues) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"),
                (totalTests >= 0, "totalTests must be >= 0"),
                (passedTests >= 0, "passedTests must be >= 0"),
                (failedTests >= 0, "failedTests must be >= 0"),
                (passedTests + failedTests <= totalTests,
                    $"passedTests ({passedTests}) + failedTests ({failedTests}) must not exceed totalTests ({totalTests})"),
                (coveragePercent is >= -1 and <= 100,
                    $"coveragePercent must be -1 (unavailable) or 0-100, got {coveragePercent}"));
            if (error != null) return error;

            _log.Info($"Tool call: report_test_results(verdict={verdict}, total={totalTests}, passed={passedTests}, failed={failedTests}, coverage={coveragePercent})");
            _lastTestReport = new TestResultReport
            {
                Verdict = parsed!.Value,
                TotalTests = totalTests,
                PassedTests = passedTests,
                FailedTests = failedTests,
                CoveragePercent = coveragePercent >= 0 ? coveragePercent : null,
                BuildSuccess = buildSuccess,
                Issues = issues.ToList(),
            };
            return "Test results recorded.";
        },
        "report_test_results",
        "Report structured test results. REQUIRED for testers after running tests.");

    private AITool BuildReviewVerdictTool() => AIFunctionFactory.Create(
        ([Description("APPROVE or REQUEST_CHANGES")] string verdict,
         [Description("List of issues found, empty if none")] string[] issues,
         [Description("Overall review summary")] string summary) =>
        {
            var parsed = ReviewVerdictExtensions.ParseReviewVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is not null, "verdict must be exactly 'APPROVE' or 'REQUEST_CHANGES'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_review_verdict(verdict={verdict}, issues={issues.Length})");
            _lastWorkerReport = new WorkerReport
            {
                ReviewVerdict = parsed!.Value,
                Issues = issues.ToList(),
                Summary = summary,
            };
            return "Review verdict recorded.";
        },
        "report_review_verdict",
        "Report your code review verdict. REQUIRED for reviewers after completing the review.");

    private AITool BuildCodeChangesTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("List of files modified")] string[] filesModified,
         [Description("Summary of changes made")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_code_changes(verdict={verdict}, files={filesModified.Length})");
            _lastWorkerReport = new WorkerReport
            {
                TaskVerdict = parsed!.Value,
                FilesChanged = filesModified.ToList(),
                Summary = summary,
            };
            return "Code changes recorded.";
        },
        "report_code_changes",
        "Report your code changes. REQUIRED for coders after implementing and committing.");

    private AITool BuildDocChangesTool() => AIFunctionFactory.Create(
        ([Description("PASS or FAIL")] string verdict,
         [Description("List of documentation files updated")] string[] filesUpdated,
         [Description("Summary of documentation changes")] string summary) =>
        {
            var parsed = TaskVerdictExtensions.ParseTaskVerdict(verdict);
            var error = ToolValidation.Check(
                (!string.IsNullOrEmpty(verdict), "verdict is required"),
                (parsed is TaskVerdict.Pass or TaskVerdict.Fail, "verdict must be exactly 'PASS' or 'FAIL'"));
            if (error != null) return error;

            _log.Info($"Tool call: report_doc_changes(verdict={verdict}, files={filesUpdated.Length})");
            _lastWorkerReport = new WorkerReport
            {
                TaskVerdict = parsed!.Value,
                FilesChanged = filesUpdated.ToList(),
                Summary = summary,
            };
            return "Documentation changes recorded.";
        },
        "report_doc_changes",
        "Report your documentation changes. REQUIRED for doc-writers after updating docs.");

    private AITool BuildFileSizesTool() => AIFunctionFactory.Create(
        ([Description("Glob pattern to match files, e.g. '*.md' or '**/*.agents.md'. Leave empty for all files.")] string pattern) =>
        {
            _log.Info($"Tool call: get_file_sizes(pattern={pattern})");
            try
            {
                var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
                var searchOption = searchPattern.Contains("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var normalizedPattern = searchPattern.Replace("**/", "");

                var files = Directory.GetFiles("/config-repo/agents", normalizedPattern, searchOption);
                if (files.Length == 0)
                    return "No files matched the pattern.";

                var lines = files.Select(f =>
                {
                    var info = new FileInfo(f);
                    var content = File.ReadAllText(f);
                    return $"{Path.GetFileName(f)}: {content.Length} chars, {info.Length} bytes";
                });
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        },
        "get_file_sizes",
        "Get character and byte counts for files in the agents directory. Use before editing to check against the 4000-character limit.");
}
