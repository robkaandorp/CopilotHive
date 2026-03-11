using CopilotHive.Agents;

namespace CopilotHive.Tests;

public class AgentsManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentsManager _manager;

    public AgentsManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new AgentsManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void GetAgentsMd_ReturnsEmpty_WhenFileDoesNotExist()
    {
        var result = _manager.GetAgentsMd("nonexistent");
        Assert.Empty(result);
    }

    [Fact]
    public void UpdateAgentsMd_CreatesFile()
    {
        _manager.UpdateAgentsMd("coder", "# Coder\nYou write code.");

        var content = _manager.GetAgentsMd("coder");
        Assert.Contains("You write code.", content);
    }

    [Fact]
    public void UpdateAgentsMd_ArchivesPreviousVersion()
    {
        _manager.UpdateAgentsMd("coder", "Version 1");
        _manager.UpdateAgentsMd("coder", "Version 2");

        var history = _manager.GetHistory("coder");
        Assert.Single(history);
        Assert.Equal("v001.agents.md", history[0]);
    }

    [Fact]
    public void UpdateAgentsMd_MultipleUpdates_TracksAllVersions()
    {
        _manager.UpdateAgentsMd("coder", "V1");
        _manager.UpdateAgentsMd("coder", "V2");
        _manager.UpdateAgentsMd("coder", "V3");

        var history = _manager.GetHistory("coder");
        Assert.Equal(2, history.Length);

        var current = _manager.GetAgentsMd("coder");
        Assert.Equal("V3", current);
    }

    [Fact]
    public void RollbackAgentsMd_RestoresPreviousVersion()
    {
        _manager.UpdateAgentsMd("coder", "Version 1");
        _manager.UpdateAgentsMd("coder", "Version 2");

        var rolled = _manager.RollbackAgentsMd("coder");
        Assert.True(rolled);

        var content = _manager.GetAgentsMd("coder");
        Assert.Equal("Version 1", content);
    }

    [Fact]
    public void RollbackAgentsMd_ReturnsFalse_WhenNoHistory()
    {
        var rolled = _manager.RollbackAgentsMd("coder");
        Assert.False(rolled);
    }

    [Fact]
    public void GetAgentsMdPath_ReturnsExpectedPath()
    {
        var path = _manager.GetAgentsMdPath("tester");
        Assert.EndsWith("tester.agents.md", path);
    }

    [Fact]
    public void DifferentRoles_HaveIndependentHistory()
    {
        _manager.UpdateAgentsMd("coder", "Coder V1");
        _manager.UpdateAgentsMd("coder", "Coder V2");
        _manager.UpdateAgentsMd("tester", "Tester V1");

        Assert.Single(_manager.GetHistory("coder"));
        Assert.Empty(_manager.GetHistory("tester"));
    }
}
