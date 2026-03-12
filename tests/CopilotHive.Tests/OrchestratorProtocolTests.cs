using System.Text.Json;
using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

public class OrchestratorProtocolTests
{
    // ── OrchestratorActionType serialization ──

    [Theory]
    [InlineData(OrchestratorActionType.SpawnCoder, "\"spawn_coder\"")]
    [InlineData(OrchestratorActionType.SpawnReviewer, "\"spawn_reviewer\"")]
    [InlineData(OrchestratorActionType.SpawnTester, "\"spawn_tester\"")]
    [InlineData(OrchestratorActionType.Merge, "\"merge\"")]
    [InlineData(OrchestratorActionType.Done, "\"done\"")]
    [InlineData(OrchestratorActionType.Skip, "\"skip\"")]
    public void ActionType_SerializesToSnakeCase(OrchestratorActionType action, string expectedJson)
    {
        var json = JsonSerializer.Serialize(action, ProtocolJson.Options);
        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData("\"spawn_coder\"", OrchestratorActionType.SpawnCoder)]
    [InlineData("\"done\"", OrchestratorActionType.Done)]
    [InlineData("\"skip\"", OrchestratorActionType.Skip)]
    public void ActionType_DeserializesFromSnakeCase(string json, OrchestratorActionType expected)
    {
        var result = JsonSerializer.Deserialize<OrchestratorActionType>(json, ProtocolJson.Options);
        Assert.Equal(expected, result);
    }

    // ── OrchestratorDecision round-trip ──

    [Fact]
    public void Decision_RoundTrip_PreservesAllFields()
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            Prompt = "Write a hello world program",
            Reason = "New code needed",
            Verdict = "PASS",
            ReviewVerdict = "APPROVE",
            Issues = ["Missing error handling", "No tests"],
            TestMetrics = new ExtractedTestMetrics
            {
                BuildSuccess = true,
                TotalTests = 42,
                PassedTests = 40,
                FailedTests = 2,
                CoveragePercent = 85.5,
            },
        };

        var json = JsonSerializer.Serialize(decision, ProtocolJson.Options);
        var deserialized = JsonSerializer.Deserialize<OrchestratorDecision>(json, ProtocolJson.Options);

        Assert.NotNull(deserialized);
        Assert.Equal(OrchestratorActionType.SpawnCoder, deserialized.Action);
        Assert.Equal("Write a hello world program", deserialized.Prompt);
        Assert.Equal("New code needed", deserialized.Reason);
        Assert.Equal("PASS", deserialized.Verdict);
        Assert.Equal("APPROVE", deserialized.ReviewVerdict);
        Assert.Equal(2, deserialized.Issues!.Count);
        Assert.Equal(42, deserialized.TestMetrics!.TotalTests);
        Assert.Equal(85.5, deserialized.TestMetrics.CoveragePercent);
    }

    [Fact]
    public void Decision_DefaultAction_IsDone()
    {
        var decision = new OrchestratorDecision();
        Assert.Equal(OrchestratorActionType.Done, decision.Action);
    }

    [Fact]
    public void Decision_NullFieldsOmittedFromJson()
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.Done,
            Reason = "Goal complete",
        };

        var json = JsonSerializer.Serialize(decision, ProtocolJson.Options);

        Assert.Contains("\"reason\"", json);
        Assert.DoesNotContain("\"prompt\"", json);
        Assert.DoesNotContain("\"verdict\"", json);
        Assert.DoesNotContain("\"test_metrics\"", json);
        Assert.DoesNotContain("\"issues\"", json);
    }

    // ── ExtractedTestMetrics ──

    [Fact]
    public void TestMetrics_RoundTrip()
    {
        var metrics = new ExtractedTestMetrics
        {
            BuildSuccess = true,
            TotalTests = 100,
            PassedTests = 95,
            FailedTests = 5,
            CoveragePercent = 73.2,
        };

        var json = JsonSerializer.Serialize(metrics, ProtocolJson.Options);
        var deserialized = JsonSerializer.Deserialize<ExtractedTestMetrics>(json, ProtocolJson.Options);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.BuildSuccess);
        Assert.Equal(100, deserialized.TotalTests);
        Assert.Equal(95, deserialized.PassedTests);
        Assert.Equal(5, deserialized.FailedTests);
        Assert.Equal(73.2, deserialized.CoveragePercent);
    }

    // ── ParseFromLlmResponse ──

    [Fact]
    public void ParseFromLlmResponse_CleanJson_Succeeds()
    {
        var json = """{"action":"spawn_coder","reason":"Start coding"}""";
        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(json);

        Assert.NotNull(result);
        Assert.Equal(OrchestratorActionType.SpawnCoder, result.Action);
        Assert.Equal("Start coding", result.Reason);
    }

    [Fact]
    public void ParseFromLlmResponse_MarkdownCodeBlock_ExtractsJson()
    {
        var response = """
            Here is my analysis:

            ```json
            {"action": "done", "reason": "Goal already complete"}
            ```

            That's my recommendation.
            """;

        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(response);

        Assert.NotNull(result);
        Assert.Equal(OrchestratorActionType.Done, result.Action);
        Assert.Equal("Goal already complete", result.Reason);
    }

    [Fact]
    public void ParseFromLlmResponse_JsonWithSurroundingText_ExtractsJson()
    {
        var response = """Based on the output, here is the verdict: {"verdict": "PASS", "test_metrics": {"total_tests": 50, "passed_tests": 50}} Let me know if you need more details.""";

        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(response);

        Assert.NotNull(result);
        Assert.Equal("PASS", result.Verdict);
        Assert.Equal(50, result.TestMetrics!.TotalTests);
    }

    [Fact]
    public void ParseFromLlmResponse_InvalidJson_ReturnsNull()
    {
        var response = "This is not JSON at all, just regular text.";
        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(response);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFromLlmResponse_EmptyJson_ReturnsDefaults()
    {
        var json = "{}";
        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(json);

        Assert.NotNull(result);
        Assert.Equal(OrchestratorActionType.Done, result.Action);
        Assert.Null(result.Prompt);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ParseFromLlmResponse_ReviewInterpretation_ParsesWithoutAction()
    {
        var json = """{"review_verdict": "APPROVE", "issues": []}""";
        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(json);

        Assert.NotNull(result);
        Assert.Equal(OrchestratorActionType.Done, result.Action);
        Assert.Equal("APPROVE", result.ReviewVerdict);
        Assert.Empty(result.Issues!);
    }

    [Fact]
    public void ParseFromLlmResponse_TestInterpretation_WithMetrics()
    {
        var json = """
            {
                "verdict": "PASS",
                "test_metrics": {
                    "build_success": true,
                    "total_tests": 158,
                    "passed_tests": 158,
                    "failed_tests": 0,
                    "coverage_percent": 72.86
                }
            }
            """;

        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(json);

        Assert.NotNull(result);
        Assert.Equal("PASS", result.Verdict);
        Assert.True(result.TestMetrics!.BuildSuccess);
        Assert.Equal(158, result.TestMetrics.TotalTests);
        Assert.Equal(0, result.TestMetrics.FailedTests);
        Assert.Equal(72.86, result.TestMetrics.CoveragePercent);
    }

    [Fact]
    public void ParseFromLlmResponse_WhitespaceAroundJson_Handled()
    {
        var json = "   \n  {\"action\": \"skip\", \"reason\": \"Docs only\"}\n  ";
        var result = ProtocolJson.ParseFromLlmResponse<OrchestratorDecision>(json);

        Assert.NotNull(result);
        Assert.Equal(OrchestratorActionType.Skip, result.Action);
    }

    // ── ProtocolJson.Options configuration ──

    [Fact]
    public void Options_UsesSnakeCaseNaming()
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            ReviewVerdict = "APPROVE",
            TestMetrics = new ExtractedTestMetrics { TotalTests = 10 },
        };

        var json = JsonSerializer.Serialize(decision, ProtocolJson.Options);

        Assert.Contains("\"review_verdict\"", json);
        Assert.Contains("\"test_metrics\"", json);
        Assert.Contains("\"total_tests\"", json);
        Assert.DoesNotContain("ReviewVerdict", json);
        Assert.DoesNotContain("TestMetrics", json);
    }

    // ── ConversationEntry ──

    [Fact]
    public void ConversationEntry_RecordEquality()
    {
        var a = new ConversationEntry("user", "Hello");
        var b = new ConversationEntry("user", "Hello");
        var c = new ConversationEntry("assistant", "Hello");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── WorkerResult ──

    [Fact]
    public void WorkerResult_DefaultSuccess_IsTrue()
    {
        var result = new WorkerResult { Role = "tester", Output = "All good" };
        Assert.True(result.Success);
    }
}
