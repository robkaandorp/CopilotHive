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

    // ── WorkerResult ──

    [Fact]
    public void WorkerResult_DefaultSuccess_IsTrue()
    {
        var result = new WorkerResult { Role = "tester", Output = "All good" };
        Assert.True(result.Success);
    }
}
