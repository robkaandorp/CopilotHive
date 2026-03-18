using CopilotHive.Agents;
using CopilotHive.Workers;

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
        var result = _manager.GetAgentsMd(WorkerRole.MergeWorker);
        Assert.Empty(result);
    }

    [Fact]
    public void UpdateAgentsMd_CreatesFile()
    {
        _manager.UpdateAgentsMd(WorkerRole.Coder, "# Coder\nYou write code.");

        var content = _manager.GetAgentsMd(WorkerRole.Coder);
        Assert.Contains("You write code.", content);
    }

    [Fact]
    public void UpdateAgentsMd_ArchivesPreviousVersion()
    {
        _manager.UpdateAgentsMd(WorkerRole.Coder, "Version 1");
        _manager.UpdateAgentsMd(WorkerRole.Coder, "Version 2");

        var history = _manager.GetHistory(WorkerRole.Coder);
        Assert.Single(history);
        Assert.Equal("v001.agents.md", history[0]);
    }

    [Fact]
    public void UpdateAgentsMd_MultipleUpdates_TracksAllVersions()
    {
        _manager.UpdateAgentsMd(WorkerRole.Coder, "V1");
        _manager.UpdateAgentsMd(WorkerRole.Coder, "V2");
        _manager.UpdateAgentsMd(WorkerRole.Coder, "V3");

        var history = _manager.GetHistory(WorkerRole.Coder);
        Assert.Equal(2, history.Length);

        var current = _manager.GetAgentsMd(WorkerRole.Coder);
        Assert.Equal("V3", current);
    }

    [Fact]
    public void RollbackAgentsMd_RestoresPreviousVersion()
    {
        _manager.UpdateAgentsMd(WorkerRole.Coder, "Version 1");
        _manager.UpdateAgentsMd(WorkerRole.Coder, "Version 2");

        var rolled = _manager.RollbackAgentsMd(WorkerRole.Coder);
        Assert.True(rolled);

        var content = _manager.GetAgentsMd(WorkerRole.Coder);
        Assert.Equal("Version 1", content);
    }

    [Fact]
    public void RollbackAgentsMd_ReturnsFalse_WhenNoHistory()
    {
        var rolled = _manager.RollbackAgentsMd(WorkerRole.Coder);
        Assert.False(rolled);
    }

    [Fact]
    public void GetAgentsMdPath_ReturnsExpectedPath()
    {
        var path = _manager.GetAgentsMdPath(WorkerRole.Tester);
        Assert.EndsWith("tester.agents.md", path);
    }

    [Fact]
    public void DifferentRoles_HaveIndependentHistory()
    {
        _manager.UpdateAgentsMd(WorkerRole.Coder, "Coder V1");
        _manager.UpdateAgentsMd(WorkerRole.Coder, "Coder V2");
        _manager.UpdateAgentsMd(WorkerRole.Tester, "Tester V1");

        Assert.Single(_manager.GetHistory(WorkerRole.Coder));
        Assert.Empty(_manager.GetHistory(WorkerRole.Tester));
    }
}
