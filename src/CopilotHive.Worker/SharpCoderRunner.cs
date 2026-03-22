#pragma warning disable CS1591
#pragma warning disable OPENAI001 // ResponsesClient.AsIChatClient is experimental
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        _log.Info($"AgentResult: status={result.Status}, toolCalls={result.ToolCallCount}, model={result.ModelId}, finish={result.FinishReason}");
        if (result.Messages != null)
        {
            _log.Info($"AgentResult: {result.Messages.Count} messages total");
            foreach (var msg in result.Messages)
            {
                var textPreview = msg.Text?.Length > 200 ? msg.Text.Substring(0, 200) + "..." : msg.Text;
                _log.Info($"  [{msg.Role}] {textPreview}");
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

    private IChatClient CreateChatClient(string? modelOverride = null)
    {
        // Model override can include a provider prefix: "copilot/claude-sonnet-4.6"
        var (provider, model) = ParseProviderAndModel(modelOverride);

        _log.Info($"Creating chat client: provider={provider}, model={model ?? "default"}");

        switch (provider)
        {
            case "ollama-cloud":
            {
                var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
                if (string.IsNullOrEmpty(apiKey)) throw new Exception("OLLAMA_API_KEY is required for ollama-cloud");

                var httpClient = new HttpClient { BaseAddress = new Uri("https://ollama.com") };
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "gpt-oss:120b";
                var ollamaClient = new OllamaApiClient(httpClient);
                ollamaClient.SelectedModel = model;
                return ollamaClient;
            }

            case "ollama-local":
            {
                var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
                model ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3";
                var ollamaClient = new OllamaApiClient(new Uri(url));
                ollamaClient.SelectedModel = model;
                return ollamaClient;
            }

            case "github":
            {
                var token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (string.IsNullOrEmpty(token)) throw new Exception("GH_TOKEN or GITHUB_TOKEN is required for github provider");

                model ??= Environment.GetEnvironmentVariable("GITHUB_MODEL") ?? "openai/gpt-4.1";

                var openAiClient = new OpenAIClient(
                    new ApiKeyCredential(token),
                    new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai") }
                );

                return openAiClient.GetChatClient(model).AsIChatClient();
            }

            case "copilot":
                return CreateCopilotClient(model ?? Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.6");

            default:
                throw new InvalidOperationException($"Unknown LLM provider: '{provider}'");
        }
    }

    /// <summary>
    /// Extracts an optional provider prefix from the model string.
    /// "copilot/claude-sonnet-4.6" → ("copilot", "claude-sonnet-4.6")
    /// "claude-sonnet-4.6" → (env LLM_PROVIDER, "claude-sonnet-4.6")
    /// null → (env LLM_PROVIDER, null)
    /// </summary>
    private static (string provider, string? model) ParseProviderAndModel(string? modelOverride)
    {
        var defaultProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        if (string.IsNullOrEmpty(modelOverride))
            return (defaultProvider, null);

        var slashIndex = modelOverride.IndexOf('/');
        if (slashIndex <= 0)
            return (defaultProvider, modelOverride);

        var prefix = modelOverride.Substring(0, slashIndex).ToLowerInvariant();

        // Only treat as provider prefix if it matches a known provider.
        // This avoids misinterpreting model names like "openai/gpt-4.1".
        if (prefix is "copilot" or "ollama-cloud" or "ollama-local" or "github")
            return (prefix, modelOverride.Substring(slashIndex + 1));

        return (defaultProvider, modelOverride);
    }

    private IChatClient CreateCopilotClient(string model)
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(ghToken)) throw new Exception("GH_TOKEN or GITHUB_TOKEN is required for copilot provider");

        if (RequiresResponsesEndpoint(model))
        {
            // Models like gpt-5.4 use the /responses endpoint.
            // The Copilot API doesn't support previous_response_id, so we
            // inline the previous response's output into follow-up requests.
            var handler = new CopilotResponsesHandler(new HttpClientHandler());
            var httpClient = new HttpClient(handler);

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(ghToken),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://api.githubcopilot.com"),
                    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
                }
            );
            return openAiClient.GetResponsesClient().AsIChatClient(model);
        }
        else
        {
            // Claude and older GPT models use /chat/completions
            // The Copilot API returns tool_calls in a separate choice from text content.
            // The OpenAI SDK only reads choices[0], so we merge them before parsing.
            var handler = new CopilotChoiceMergingHandler(new HttpClientHandler());
            var httpClient = new HttpClient(handler);

            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(ghToken),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri("https://api.githubcopilot.com"),
                    Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
                }
            );
            return openAiClient.GetChatClient(model).AsIChatClient();
        }
    }

    /// <summary>
    /// Models that must use the /responses endpoint instead of /chat/completions.
    /// </summary>
    private static bool RequiresResponsesEndpoint(string model)
    {
        return model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The GitHub Copilot API splits tool_calls and text content into separate choices.
    /// The OpenAI SDK only reads choices[0], losing the tool_calls. This handler
    /// merges all choices into a single choice so the SDK sees both text and tool_calls.
    /// </summary>
    private sealed class CopilotChoiceMergingHandler : DelegatingHandler
    {
        public CopilotChoiceMergingHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return response;

            var body = await response.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(body);
            var choices = json?["choices"]?.AsArray();

            if (choices == null || choices.Count <= 1) return ReplaceContent(response, body);

            // Merge: take the choice with tool_calls, add text from the other
            JsonObject? toolChoice = null;
            string? textContent = null;

            foreach (var c in choices)
            {
                var msg = c?["message"];
                if (msg?["tool_calls"] != null)
                    toolChoice = c!.AsObject();
                else if (msg?["content"]?.GetValue<string>() is { Length: > 0 } text)
                    textContent = text;
            }

            if (toolChoice != null)
            {
                // Add text content to the tool_calls choice if present
                if (textContent != null && toolChoice["message"] is JsonObject merged)
                {
                    merged["content"] = textContent;
                }

                // Detach from parent array before adding to new one
                toolChoice.Parent?.AsArray().Remove(toolChoice);
                json!["choices"] = new JsonArray(toolChoice);
                body = json.ToJsonString();
            }

            return ReplaceContent(response, body);
        }

        private static HttpResponseMessage ReplaceContent(HttpResponseMessage response, string body)
        {
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    /// <summary>
    /// The Copilot API doesn't support previous_response_id on the /responses endpoint.
    /// When the SDK sends a follow-up request referencing a previous response, this handler
    /// inlines the previous response's output (tool calls) into the input array so the API
    /// has full conversation context without needing server-side state.
    /// </summary>
    private sealed class CopilotResponsesHandler : DelegatingHandler
    {
        private JsonNode? _lastResponseOutput;

        public CopilotResponsesHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content != null && request.RequestUri?.AbsolutePath?.Contains("responses") == true)
            {
                var body = await request.Content.ReadAsStringAsync();
                var json = JsonNode.Parse(body);

                if (json is JsonObject obj && obj.ContainsKey("previous_response_id"))
                {
                    obj.Remove("previous_response_id");

                    if (_lastResponseOutput is JsonArray prevOutput && obj["input"] is JsonArray input)
                    {
                        var combined = new JsonArray();
                        foreach (var item in prevOutput)
                            combined.Add(item!.DeepClone());
                        foreach (var item in input)
                            combined.Add(item!.DeepClone());
                        obj["input"] = combined;
                    }

                    body = obj.ToJsonString();
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }
            }

            var response = await base.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                var respJson = JsonNode.Parse(respBody);
                _lastResponseOutput = respJson?["output"]?.DeepClone();
                response.Content = new StringContent(respBody, System.Text.Encoding.UTF8, "application/json");
            }

            return response;
        }
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
