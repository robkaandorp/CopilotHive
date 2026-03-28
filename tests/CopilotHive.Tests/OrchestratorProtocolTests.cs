using CopilotHive.Orchestration;

namespace CopilotHive.Tests;

public class OrchestratorProtocolTests
{
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

    [Fact]
    public void ConversationEntry_DefaultMetadata_IsNull()
    {
        var entry = new ConversationEntry("user", "Hello");

        Assert.Null(entry.Iteration);
        Assert.Null(entry.Purpose);
    }

    [Fact]
    public void ConversationEntry_WithMetadata_StoresValues()
    {
        var entry = new ConversationEntry("user", "Plan this", Iteration: 3, Purpose: "planning");

        Assert.Equal(3, entry.Iteration);
        Assert.Equal("planning", entry.Purpose);
    }

    [Fact]
    public void ConversationEntry_RecordEquality_IncludesMetadata()
    {
        var a = new ConversationEntry("user", "Hello", 1, "planning");
        var b = new ConversationEntry("user", "Hello", 1, "planning");
        var c = new ConversationEntry("user", "Hello", 2, "planning");

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
