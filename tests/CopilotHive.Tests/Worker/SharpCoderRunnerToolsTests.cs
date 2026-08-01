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
        public Task<string> RequestClarificationAsync(string taskId, string question, CancellationToken ct)
            => Task.FromResult(string.Empty);

        public Task ReportProgressAsync(string taskId, string status, string details, CancellationToken ct)
            => Task.CompletedTask;

        public Task ReportNarrativeAsync(string taskId, string narrative, CancellationToken ct)
            => Task.CompletedTask;

        public Task<string> GetGoalAsync(string taskId, string goalId, CancellationToken ct)
            => Task.FromResult(string.Empty);
    }
}
