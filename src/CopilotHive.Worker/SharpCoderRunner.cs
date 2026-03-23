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
    private IChatClient? _chatClient;
    private string _currentModel = "(default)";

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
    }

    private IToolCallBridge? _toolBridge;
    private string? _currentTaskId;
    private WorkerRole _currentRole;
    private string? _customAgentSystemPrompt;

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
        _chatClient = CreateChatClient(model);
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
            SystemPrompt = _customAgentSystemPrompt,
            CustomTools = BuildCustomTools(),
            EnableBash = _currentRole != WorkerRole.Improver,
            EnableFileWrites = _currentRole != WorkerRole.Reviewer
        };

        var agent = new CodingAgent(_chatClient, options);
        var result = await agent.ExecuteAsync(prompt, ct);

        stopwatch.Stop();
        var elapsedSecs = stopwatch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _log.Info($"Task finished in {elapsedSecs}s (status={result.Status}, toolCalls={result.ToolCallCount})");

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

    private IChatClient CreateChatClient(string? modelOverride = null)
    {
        var (provider, model) = CopilotHive.SDK.ChatClientFactory.ParseProviderAndModel(modelOverride);
        _currentModel = model ?? "(default)";
        _log.Info($"Creating chat client: provider={provider}, model={_currentModel}");
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
                async ([Description("The question to ask the user or orchestrator")] string question) =>
                {
                    if (string.IsNullOrEmpty(_currentTaskId)) return "Error: Task ID not set.";
                    _log.Info($"Tool call: ask_user({question})");
                    var response = await _toolBridge.AskOrchestratorAsync(_currentTaskId, question, CancellationToken.None);
                    return response;
                },
                "ask_user",
                "Ask a question to the user or orchestrator to get clarification or additional information."
            ));
        }

        if (_currentRole == WorkerRole.Tester)
            tools.Add(BuildTestResultsTool());

        if (_currentRole == WorkerRole.Reviewer)
            tools.Add(BuildReviewVerdictTool());

        if (_currentRole == WorkerRole.Coder || _currentRole == WorkerRole.Reviewer)
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
