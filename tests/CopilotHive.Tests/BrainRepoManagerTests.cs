using CopilotHive.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class BrainRepoManagerTests : IDisposable
{
    private readonly string _tempDir;

    public BrainRepoManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetClonePath_ReturnsExpectedPath()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var path = manager.GetClonePath("copilothive");

        var normalised = path.Replace('\\', '/');
        Assert.EndsWith("repos/copilothive", normalised);
    }

    [Fact]
    public void GetClonePath_DifferentRepos_ReturnDifferentPaths()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var path1 = manager.GetClonePath("repo-a");
        var path2 = manager.GetClonePath("repo-b");

        Assert.NotEqual(path1, path2);
        Assert.Contains("repo-a", path1);
        Assert.Contains("repo-b", path2);
    }

    [Fact]
    public void GetClonePath_UsesBasePath()
    {
        var customPath = Path.Combine(_tempDir, "custom");
        var manager = new BrainRepoManager(customPath, NullLogger<BrainRepoManager>.Instance);

        var path = manager.GetClonePath("myrepo");

        Assert.StartsWith(Path.GetFullPath(customPath), path);
    }

    [Fact]
    public void WorkDirectory_PointsToReposFolder()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var workDir = manager.WorkDirectory;

        var normalised = workDir.Replace('\\', '/');
        Assert.EndsWith("repos", normalised);
    }

    [Fact]
    public void GetClonePath_IsChildOfWorkDirectory()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var clonePath = manager.GetClonePath("myrepo");

        Assert.StartsWith(manager.WorkDirectory, clonePath);
    }
}
