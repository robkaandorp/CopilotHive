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
