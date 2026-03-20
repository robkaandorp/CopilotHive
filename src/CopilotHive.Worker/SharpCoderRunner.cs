#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CopilotHive.Shared;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using SharpCoder;
using OllamaSharp;

namespace CopilotHive.Worker;

public sealed class SharpCoderRunner : IAgentRunner
{
    private readonly WorkerLogger _log = new("SharpCoder");
    private IChatClient? _chatClient;

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

        _log.Info($"Executing task in SharpCoder. WorkDir: {workDir}");
        
        var options = new AgentOptions
        {
            WorkDirectory = workDir,
            MaxSteps = 30,
            SystemPrompt = _customAgentSystemPrompt,
            CustomTools = BuildCustomTools(),
            EnableBash = _currentRole != WorkerRole.Improver,
            EnableFileWrites = _currentRole != WorkerRole.Reviewer
        };

        var agent = new CodingAgent(_chatClient, options);
        var result = await agent.ExecuteAsync(prompt, ct);

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

    private IChatClient CreateChatClient(string? modelOverride = null)
    {
        var provider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        if (provider == "ollama-cloud")
        {
            var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
            if (string.IsNullOrEmpty(apiKey)) throw new Exception("OLLAMA_API_KEY is required for ollama-cloud");

            var httpClient = new HttpClient { BaseAddress = new Uri("https://ollama.com") };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var model = modelOverride ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "gpt-oss:120b";
            var ollamaClient = new OllamaApiClient(httpClient);
            ollamaClient.SelectedModel = model;
            return ollamaClient;
        }
        else if (provider == "ollama-local")
        {
            var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
            var model = modelOverride ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3";
            var ollamaClient = new OllamaApiClient(new Uri(url));
            ollamaClient.SelectedModel = model;
            return ollamaClient;
        }
        else if (provider == "github")
        {
            var token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(token)) throw new Exception("GH_TOKEN or GITHUB_TOKEN is required for github provider");

            var model = modelOverride ?? Environment.GetEnvironmentVariable("GITHUB_MODEL") ?? "openai/gpt-4.1";

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(token),
                new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai") }
            );

            return openAiClient.GetChatClient(model).AsIChatClient();
        }
        else // default to copilot
        {
            var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(ghToken)) throw new Exception("GH_TOKEN or GITHUB_TOKEN is required for copilot provider");

            var model = modelOverride ?? Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.6";
            var copilotToken = ExchangeForCopilotToken(ghToken);

            _log.Info($"Copilot token obtained, using model: {model}");

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(copilotToken),
                new OpenAIClientOptions { Endpoint = new Uri("https://api.githubcopilot.com") }
            );

            return openAiClient.GetChatClient(model).AsIChatClient();
        }
    }

    private string ExchangeForCopilotToken(string ghToken)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", ghToken);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Add("editor-version", "vscode/1.95.0");
        httpClient.DefaultRequestHeaders.Add("editor-plugin-version", "copilot/1.0.0");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GithubCopilot/1.0.0");

        var response = httpClient.GetAsync("https://api.github.com/copilot_internal/v2/token").Result;
        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content.ReadAsStringAsync().Result;
            throw new Exception($"Copilot token exchange failed ({response.StatusCode}): {body}");
        }

        var json = response.Content.ReadAsStringAsync().Result;
        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("token").GetString()
            ?? throw new Exception("Copilot token response missing 'token' field");

        return token;
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
}
