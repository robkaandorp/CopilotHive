using CopilotHive.Shared;
using CopilotHive.Worker;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder.Tools;
using System.ComponentModel;
using System.Globalization;
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

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);

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

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);
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

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);

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

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);
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

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);
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

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);
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
    /// The file-size tool shown to the Improver must advertise the same centrally configured
    /// character limit that executor enforcement uses.
    /// </summary>
    [Fact]
    public void GetFileSizesTool_DescriptionAdvertisesConfiguredAgentsMdLimit()
    {
        var runner = new SharpCoderRunner();
        var buildMethod = typeof(SharpCoderRunner).GetMethod(
            "BuildFileSizesTool", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tool = Assert.IsAssignableFrom<AIFunction>(buildMethod.Invoke(runner, []));
        var configuredLimit = WorkerConstants.AgentsMdMaxCharacters.ToString(CultureInfo.InvariantCulture);

        Assert.Contains($"{configuredLimit}-character limit", tool.Description, StringComparison.Ordinal);
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

    /// <summary>
    /// The size-reporting agents-file tools are registered for the Improver ONLY. Other roles must
    /// keep their existing tool set. Removal-proof: fails if either tool stops being registered for
    /// the Improver, or if a non-Improver role gains one.
    /// </summary>
    [Theory]
    [InlineData(WorkerRole.Improver, true)]
    [InlineData(WorkerRole.Coder, false)]
    [InlineData(WorkerRole.Tester, false)]
    [InlineData(WorkerRole.Reviewer, false)]
    [InlineData(WorkerRole.DocWriter, false)]
    public void BuildCustomTools_AgentsFileToolsRegistered_OnlyForImprover(WorkerRole role, bool expected)
    {
        var runner = new SharpCoderRunner();
        runner.SetCustomAgent(role, "");

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).ToList();

        Assert.Equal(expected, names.Contains("edit_agents_file"));
        Assert.Equal(expected, names.Contains("write_agents_file"));

        // The size-check tool keeps the same role scope as before.
        Assert.Equal(expected, names.Contains("get_file_sizes"));
    }

    /// <summary>
    /// The new tools' descriptions must name the exact rules of the built-in tools they wrap and
    /// advertise the centrally configured limit, so the model knows they are the preferred path for
    /// <c>*.agents.md</c> and that they report the file's size.
    /// </summary>
    [Fact]
    public void BuildCustomTools_AgentsFileToolDescriptions_PointAtBuiltInRulesAndTheLimit()
    {
        var runner = new SharpCoderRunner();
        runner.SetCustomAgent(WorkerRole.Improver, "");

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);
        var editTool = Assert.Single(tools, t => t.Name == "edit_agents_file");
        var writeTool = Assert.Single(tools, t => t.Name == "write_agents_file");
        var configuredLimit = WorkerConstants.AgentsMdMaxCharacters.ToString(CultureInfo.InvariantCulture);

        Assert.Contains("edit_file", editTool.Description, StringComparison.Ordinal);
        Assert.Contains("write_file", writeTool.Description, StringComparison.Ordinal);
        Assert.Contains("*.agents.md", editTool.Description, StringComparison.Ordinal);
        Assert.Contains("*.agents.md", writeTool.Description, StringComparison.Ordinal);
        Assert.Contains($"{configuredLimit}-character limit", editTool.Description, StringComparison.Ordinal);
        Assert.Contains($"{configuredLimit}-character limit", writeTool.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool descriptors must expose exactly the wrapped tools' parameter names, so the model
    /// can call them the same way as <c>edit_file</c> / <c>write_file</c>.
    /// </summary>
    [Fact]
    public void BuildCustomTools_AgentsFileTools_ExposeExpectedParameters()
    {
        var runner = new SharpCoderRunner();
        runner.SetCustomAgent(WorkerRole.Improver, "");

        var tools = InvokeBuildCustomTools(runner, TestContext.Current.CancellationToken);

        var editNames = ExpectedArgumentNames(Assert.Single(tools, t => t.Name == "edit_agents_file"));
        Assert.Equal(new HashSet<string> { "filePath", "oldString", "newString" }, editNames);

        var writeNames = ExpectedArgumentNames(Assert.Single(tools, t => t.Name == "write_agents_file"));
        Assert.Equal(new HashSet<string> { "filePath", "content" }, writeNames);
    }

    /// <summary>
    /// A successful <c>edit_agents_file</c> must report the EXACT full post-edit file length. The
    /// content contains the non-ASCII character <c>é</c>, so the character count (UTF-16 code units)
    /// differs from the byte count — proof the number comes from the file's text, not its byte size.
    /// Removal-proof: passes only when the size is read from the written file after the edit.
    /// </summary>
    [Fact]
    public async Task EditAgentsFile_Success_ReportsExactPostEditCharacterLength()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TempAgentsDir();
        var filePath = Path.Combine(fixture.AgentsDir, "coder.agents.md");
        const string original = "Ligne é\r\nRègle stable\r\n";
        await File.WriteAllTextAsync(filePath, original, ct);

        var replacement = "Règle stable et vérifiée";
        var expectedContent = original.Replace("Règle stable", replacement, StringComparison.Ordinal);

        var runner = new SharpCoderRunner(fixture.ConfigRepoDir);
        runner.SetCustomAgent(WorkerRole.Improver, "");
        var tools = InvokeBuildCustomTools(runner, ct);

        var result = await InvokeAsync(tools, "edit_agents_file", new AIFunctionArguments
        {
            ["filePath"] = "coder.agents.md",
            ["oldString"] = "Règle stable",
            ["newString"] = replacement,
        }, ct);

        Assert.Equal(expectedContent, await File.ReadAllTextAsync(filePath, ct));

        // Characters ≠ bytes for this content, so a byte-based implementation cannot pass.
        var expectedLength = expectedContent.Length;
        Assert.NotEqual(System.Text.Encoding.UTF8.GetByteCount(expectedContent), expectedLength);

        Assert.Contains("Successfully replaced 1 occurrence", result, StringComparison.Ordinal);
        Assert.Contains($"File is now {expectedLength} characters (limit 8000).", result, StringComparison.Ordinal);

        // The replacement length alone would be a different (smaller) number.
        Assert.NotEqual(replacement.Length, expectedLength);
        Assert.DoesNotContain($"File is now {replacement.Length} characters", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>write_agents_file</c> must create a MISSING file (the wrapped write_file creates parent
    /// directories) and report the new file's exact length.
    /// </summary>
    [Fact]
    public async Task WriteAgentsFile_MissingFile_CreatesItAndReportsLength()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TempAgentsDir();
        var filePath = Path.Combine(fixture.AgentsDir, "improver.agents.md");
        Assert.False(File.Exists(filePath));

        const string content = "# Improver\nAlways check the size before appending.";
        var runner = new SharpCoderRunner(fixture.ConfigRepoDir);
        runner.SetCustomAgent(WorkerRole.Improver, "");
        var tools = InvokeBuildCustomTools(runner, ct);

        var result = await InvokeAsync(tools, "write_agents_file", new AIFunctionArguments
        {
            ["filePath"] = "improver.agents.md",
            ["content"] = content,
        }, ct);

        Assert.True(File.Exists(filePath));
        Assert.Equal(content, await File.ReadAllTextAsync(filePath, ct));
        Assert.Contains("Successfully wrote", result, StringComparison.Ordinal);
        Assert.Contains($"File is now {content.Length} characters (limit 8000).", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing content over the limit must be reported as over the limit, with the exact overage —
    /// that is the signal the Improver needs to compress the file afterwards.
    /// </summary>
    [Fact]
    public async Task WriteAgentsFile_ContentOverLimit_ReportsOverTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TempAgentsDir();
        var content = new string('x', AgentsMdSize.MaxCharacters + 7);
        var runner = new SharpCoderRunner(fixture.ConfigRepoDir);
        runner.SetCustomAgent(WorkerRole.Improver, "");
        var tools = InvokeBuildCustomTools(runner, ct);

        var result = await InvokeAsync(tools, "write_agents_file", new AIFunctionArguments
        {
            ["filePath"] = "tester.agents.md",
            ["content"] = content,
        }, ct);

        Assert.Contains(
            $"File is now {AgentsMdSize.MaxCharacters + 7} characters — 7 over the {AgentsMdSize.MaxCharacters}-character limit. Bring it back within the limit.",
            result,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Delegated FAILURES must be returned VERBATIM with NO size line. The expected text is
    /// produced by invoking the wrapped <c>FileTools</c> operation directly (the oracle), so the
    /// assertion proves the wrapper neither rewrites nor decorates an error — and that it delegates
    /// instead of reimplementing the containment/uniqueness rules.
    /// <para>
    /// Removal-proof: fails if the size line is appended unconditionally, if the wrapper swallows the
    /// error, or if it starts doing its own checks with different wording.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EditAgentsFile_OldStringNotFound_ReturnsDelegatedErrorUnchangedWithNoSizeLine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TempAgentsDir();
        var filePath = Path.Combine(fixture.AgentsDir, "coder.agents.md");
        const string original = "known content";
        await File.WriteAllTextAsync(filePath, original, ct);

        var tools = BuildImproverTools(fixture.ConfigRepoDir, ct);
        var result = await InvokeAsync(tools, "edit_agents_file", new AIFunctionArguments
        {
            ["filePath"] = "coder.agents.md",
            ["oldString"] = "text that is not in the file",
            ["newString"] = "replacement",
        }, ct);

        // Oracle: the wrapped tool's own answer for the same failing edit.
        var expected = await new FileTools(fixture.AgentsDir)
            .edit_file("coder.agents.md", "text that is not in the file", "replacement", ct);

        Assert.Contains("oldString not found", expected, StringComparison.Ordinal);
        Assert.Equal(expected, result);
        Assert.DoesNotContain("File is now", result, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(filePath, ct));
    }

    /// <summary>
    /// A path escaping the agents folder must fail through the delegated containment check and must
    /// NEVER touch anything outside, with no size line appended. The oracle is the wrapped tool's own
    /// error for the same path.
    /// <para>
    /// Containment is proved against a REAL file just outside the agents folder: the edit must leave
    /// its content untouched and the write must not create its target.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("edit_agents_file", "../outside.agents.md")]
    [InlineData("write_agents_file", "../outside.agents.md")]
    [InlineData("write_agents_file", "nested/../../outside.agents.md")]
    public async Task AgentsFileTools_PathEscapingAgentsFolder_ReturnsDelegatedErrorAndTouchesNothing(
        string toolName, string escapingPath)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TempAgentsDir();
        var escapedFullPath = Path.GetFullPath(Path.Combine(fixture.AgentsDir, escapingPath));
        Assert.False(
            escapedFullPath.StartsWith(fixture.AgentsDir, StringComparison.Ordinal),
            "The fixture path must really escape the agents folder.");
        Assert.False(File.Exists(escapedFullPath));

        // A decoy file at the escaped location: an edit that somehow got through would rewrite it.
        if (toolName == "edit_agents_file")
            await File.WriteAllTextAsync(escapedFullPath, "decoy content that must survive", ct);

        var fileTools = new FileTools(fixture.AgentsDir);
        var expected = toolName == "edit_agents_file"
            ? await fileTools.edit_file(escapingPath, "decoy content that must survive", "hacked", ct)
            : await fileTools.write_file(escapingPath, "should never be written", ct);
        Assert.StartsWith("Error", expected, StringComparison.Ordinal);

        var tools = BuildImproverTools(fixture.ConfigRepoDir, ct);
        var result = toolName == "edit_agents_file"
            ? await InvokeAsync(tools, toolName, new AIFunctionArguments
            {
                ["filePath"] = escapingPath,
                ["oldString"] = "decoy content that must survive",
                ["newString"] = "hacked",
            }, ct)
            : await InvokeAsync(tools, toolName, new AIFunctionArguments
            {
                ["filePath"] = escapingPath,
                ["content"] = "should never be written",
            }, ct);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("File is now", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Successfully", result, StringComparison.Ordinal);

        // Nothing outside the agents folder changed, however the path was spelled.
        if (toolName == "edit_agents_file")
            Assert.Equal("decoy content that must survive", await File.ReadAllTextAsync(escapedFullPath, ct));
        else
            Assert.False(File.Exists(escapedFullPath), $"'{escapingPath}' must not be written outside the agents folder.");
    }

    /// <summary>Builds the Improver tool set for a temp config repo.</summary>
    private static IList<AITool> BuildImproverTools(string configRepoDir, CancellationToken ct)
    {
        var runner = new SharpCoderRunner(configRepoDir);
        runner.SetCustomAgent(WorkerRole.Improver, "");
        return InvokeBuildCustomTools(runner, ct);
    }


    /// <summary>
    /// A successful edit must also be reported as over the limit once the post-edit file exceeds
    /// the cap, so the Improver learns about the violation without a second call.
    /// </summary>
    [Fact]
    public async Task EditAgentsFile_PostEditOverLimit_ReportsOverTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new TempAgentsDir();
        var filePath = Path.Combine(fixture.AgentsDir, "reviewer.agents.md");
        const string marker = "MARKER";
        await File.WriteAllTextAsync(filePath, $"Before {marker} after", ct);
        var expectedLength = $"Before {marker} after".Length - marker.Length + (AgentsMdSize.MaxCharacters + 3);

        var runner = new SharpCoderRunner(fixture.ConfigRepoDir);
        runner.SetCustomAgent(WorkerRole.Improver, "");
        var tools = InvokeBuildCustomTools(runner, ct);

        var result = await InvokeAsync(tools, "edit_agents_file", new AIFunctionArguments
        {
            ["filePath"] = "reviewer.agents.md",
            ["oldString"] = marker,
            ["newString"] = new string('y', AgentsMdSize.MaxCharacters + 3),
        }, ct);

        Assert.Equal(expectedLength, (await File.ReadAllTextAsync(filePath, ct)).Length);
        Assert.Contains(
            $"File is now {expectedLength} characters — {expectedLength - AgentsMdSize.MaxCharacters} over the {AgentsMdSize.MaxCharacters}-character limit.",
            result,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Invokes a named tool function from a built tool list and returns its textual result.
    /// </summary>
    private static async Task<string> InvokeAsync(
        IList<AITool> tools, string name, AIFunctionArguments args, CancellationToken ct)
    {
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools, t => t.Name == name));
        var result = await function.InvokeAsync(args, ct);
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>Reads a tool descriptor's expected argument names.</summary>
    private static HashSet<string> ExpectedArgumentNames(AITool tool)
    {
        var descriptor = tool.GetType().GetProperty("FunctionDescriptor")?.GetValue(tool);
        Assert.NotNull(descriptor);
        var names = descriptor!.GetType().GetProperty("ExpectedArgumentNames")?.GetValue(descriptor) as HashSet<string>;
        Assert.NotNull(names);
        return names!;
    }

    /// <summary>
    /// A temporary config repo with an <c>agents/</c> folder, cleaned up on disposal.
    /// </summary>
    private sealed class TempAgentsDir : IDisposable
    {
        public TempAgentsDir()
        {
            ConfigRepoDir = Path.Combine(Path.GetTempPath(), $"copilothive-test-config-{Guid.NewGuid():N}");
            AgentsDir = Path.Combine(ConfigRepoDir, "agents");
            Directory.CreateDirectory(AgentsDir);
        }

        public string ConfigRepoDir { get; }

        public string AgentsDir { get; }

        public void Dispose()
        {
            try { Directory.Delete(ConfigRepoDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Invokes the private <c>BuildCustomTools(CancellationToken)</c>. The token is the
    /// ASSIGNMENT'S token in production and is forwarded to every bridge call, so tests that care
    /// about cancellation pass a real one.
    /// </summary>
    private static IList<AITool> InvokeBuildCustomTools(SharpCoderRunner runner, CancellationToken ct)
    {
        var method = typeof(SharpCoderRunner).GetMethod("BuildCustomTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IList<AITool>)method.Invoke(runner, [ct])!;
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
