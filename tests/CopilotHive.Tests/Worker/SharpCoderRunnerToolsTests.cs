using CopilotHive.Worker;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for the custom tool list built by <see cref="SharpCoderRunner"/>.
/// </summary>
public sealed class SharpCoderRunnerToolsTests
{
    /// <summary>
    /// When a tool bridge is set, <c>BuildCustomTools</c> must include a tool named
    /// <c>report_narrative</c> with the expected description metadata.
    /// </summary>
    [Fact]
    public void BuildCustomTools_WithToolBridge_ContainsReportNarrativeTool()
    {
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(new FakeToolBridge());

        var tools = InvokeBuildCustomTools(runner);

        var narrativeTool = Assert.Single(tools, t => t.Name == "report_narrative");
        Assert.Contains("narrative summary", narrativeTool.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The report_narrative tool descriptor should expose a single parameter named
    /// <c>narrative</c>.
    /// </summary>
    [Fact]
    public void BuildCustomTools_WithToolBridge_ReportNarrativeHasNarrativeParameter()
    {
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(new FakeToolBridge());

        var tools = InvokeBuildCustomTools(runner);
        var narrativeTool = Assert.Single(tools, t => t.Name == "report_narrative");

        var descriptor = narrativeTool.GetType().GetProperty("FunctionDescriptor")?.GetValue(narrativeTool);
        Assert.NotNull(descriptor);

        var expectedNames = descriptor!.GetType().GetProperty("ExpectedArgumentNames")?.GetValue(descriptor) as HashSet<string>;
        Assert.NotNull(expectedNames);
        Assert.Contains("narrative", expectedNames);
    }

    /// <summary>
    /// When a tool bridge is set, <c>BuildCustomTools</c> must include a tool named
    /// <c>raise_issue</c> with the expected description metadata.
    /// </summary>
    [Fact]
    public void BuildCustomTools_WithToolBridge_ContainsRaiseIssueTool()
    {
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(new FakeToolBridge());

        var tools = InvokeBuildCustomTools(runner);

        var raiseIssueTool = Assert.Single(tools, t => t.Name == "raise_issue");
        Assert.Contains("code quality", raiseIssueTool.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The raise_issue tool descriptor should expose the expected parameter names:
    /// <c>type</c>, <c>title</c>, <c>description</c>, and <c>severity</c>.
    /// </summary>
    [Fact]
    public void BuildCustomTools_WithToolBridge_RaiseIssueHasExpectedParameters()
    {
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(new FakeToolBridge());

        var tools = InvokeBuildCustomTools(runner);
        var raiseIssueTool = Assert.Single(tools, t => t.Name == "raise_issue");

        var descriptor = raiseIssueTool.GetType().GetProperty("FunctionDescriptor")?.GetValue(raiseIssueTool);
        Assert.NotNull(descriptor);

        var expectedNames = descriptor!.GetType().GetProperty("ExpectedArgumentNames")?.GetValue(descriptor) as HashSet<string>;
        Assert.NotNull(expectedNames);
        Assert.Contains("type", expectedNames);
        Assert.Contains("title", expectedNames);
        Assert.Contains("description", expectedNames);
        Assert.Contains("severity", expectedNames);
    }

    /// <summary>
    /// Invoking the <c>raise_issue</c> tool must forward the exact arguments to the
    /// tool bridge's <c>RaiseIssueAsync</c> and return the bridge's response JSON.
    /// </summary>
    [Fact]
    public async Task RaiseIssueTool_Invocation_ForwardsArgumentsToBridgeAndReturnsResponse()
    {
        var bridge = new FakeToolBridge();
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCurrentTaskId("task-42");

        var tools = InvokeBuildCustomTools(runner);
        var raiseIssueTool = Assert.Single(tools, t => t.Name == "raise_issue");
        var raiseIssueFunction = Assert.IsAssignableFrom<AIFunction>(raiseIssueTool);

        var result = (await raiseIssueFunction.InvokeAsync(
            new AIFunctionArguments
            {
                ["type"] = "code_quality",
                ["title"] = "Parser naming",
                ["description"] = "Poorly named variables",
                ["severity"] = "medium",
            },
            TestContext.Current.CancellationToken))?.ToString() ?? "";

        Assert.Equal("{\"acknowledged\":true,\"issue_id\":\"test-id\"}", result);
        var call = Assert.Single(bridge.RaiseIssueCalls);
        Assert.Equal("task-42", call.TaskId);
        Assert.Equal("code_quality", call.Type);
        Assert.Equal("Parser naming", call.Title);
        Assert.Equal("Poorly named variables", call.Description);
        Assert.Equal("medium", call.Severity);
    }

    /// <summary>
    /// Invoking the <c>raise_issue</c> tool WITHOUT the severity parameter must
    /// forward the default <c>"low"</c> severity to the tool bridge. This protects
    /// the optional-severity default from regressing.
    /// </summary>
    [Fact]
    public async Task RaiseIssueTool_Invocation_WithoutSeverity_DefaultsToLow()
    {
        var bridge = new FakeToolBridge();
        var runner = new SharpCoderRunner();
        runner.SetToolBridge(bridge);
        runner.SetCurrentTaskId("task-42");

        var tools = InvokeBuildCustomTools(runner);
        var raiseIssueTool = Assert.Single(tools, t => t.Name == "raise_issue");
        var raiseIssueFunction = Assert.IsAssignableFrom<AIFunction>(raiseIssueTool);

        // Omit the severity argument entirely — the tool's default (null → "low") must apply.
        var result = (await raiseIssueFunction.InvokeAsync(
            new AIFunctionArguments
            {
                ["type"] = "bug",
                ["title"] = "Parser crash",
                ["description"] = "Crashes on empty input",
            },
            TestContext.Current.CancellationToken))?.ToString() ?? "";

        Assert.Equal("{\"acknowledged\":true,\"issue_id\":\"test-id\"}", result);
        var call = Assert.Single(bridge.RaiseIssueCalls);
        Assert.Equal("task-42", call.TaskId);
        Assert.Equal("bug", call.Type);
        Assert.Equal("Parser crash", call.Title);
        Assert.Equal("Crashes on empty input", call.Description);
        Assert.Equal("low", call.Severity);
    }

    /// <summary>
    /// REGRESSION: BuildFileSizesTool must resolve the agents directory from the injected
    /// config-repo path, not from the hardcoded <c>/config-repo/agents</c> path. This lets
    /// CI/non-Docker environments run the worker improver flow with a temp config repo.
    /// </summary>
    [Fact]
    public async Task GetFileSizesTool_UsesInjectedConfigRepoDirectory()
    {
        // Arrange: create a temp config repo with one agents.md file.
        var configRepoDir = Path.Combine(Path.GetTempPath(), $"copilothive-test-config-{Guid.NewGuid():N}");
        var agentsDir = Path.Combine(configRepoDir, "agents");
        Directory.CreateDirectory(agentsDir);
        var filePath = Path.Combine(agentsDir, "tester.agents.md");
        await File.WriteAllTextAsync(filePath, "short content", TestContext.Current.CancellationToken);

        try
        {
            var runner = new SharpCoderRunner(configRepoDir);
            runner.SetCustomAgent(WorkerRole.Improver, "");

            // Use reflection to invoke the private BuildFileSizesTool, which is only added for Improver.
            var buildMethod = typeof(SharpCoderRunner).GetMethod("BuildFileSizesTool", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var tool = (AIFunction)buildMethod.Invoke(runner, [])!;

            var result = await tool.InvokeAsync(
                new AIFunctionArguments { ["pattern"] = "*.agents.md" }, TestContext.Current.CancellationToken);
            var text = result?.ToString() ?? string.Empty;

            Assert.Contains("tester.agents.md", text);
            Assert.Contains("13 chars", text);
        }
        finally
        {
            Directory.Delete(configRepoDir, recursive: true);
        }
    }

    private static IList<AITool> InvokeBuildCustomTools(SharpCoderRunner runner)
    {
        var method = typeof(SharpCoderRunner).GetMethod("BuildCustomTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IList<AITool>)method.Invoke(runner, [])!;
    }

    private sealed class FakeToolBridge : IToolCallBridge
    {
        public List<(string TaskId, string Type, string Title, string Description, string Severity)> RaiseIssueCalls { get; } = [];

        public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
            => Task.CompletedTask;

        public Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
            => Task.CompletedTask;

        public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public Task<string> RaiseIssueAsync(string taskId, string type, string title, string description, string severity, CancellationToken ct)
        {
            RaiseIssueCalls.Add((taskId, type, title, description, severity));
            return Task.FromResult("{\"acknowledged\":true,\"issue_id\":\"test-id\"}");
        }
    }
}
