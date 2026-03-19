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
        Assert.DoesNotContain("\"issues\"", json);
    }

    // ── ProtocolJson.Options configuration ──

    [Fact]
    public void Options_UsesSnakeCaseNaming()
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            ReviewVerdict = "APPROVE",
            ModelTier = "standard",
        };

        var json = JsonSerializer.Serialize(decision, ProtocolJson.Options);

        Assert.Contains("\"review_verdict\"", json);
        Assert.Contains("\"model_tier\"", json);
        Assert.DoesNotContain("ReviewVerdict", json);
        Assert.DoesNotContain("ModelTier", json);
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
