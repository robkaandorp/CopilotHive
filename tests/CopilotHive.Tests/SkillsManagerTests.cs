using CopilotHive.Skills;

namespace CopilotHive.Tests;

public sealed class SkillsManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SkillsManager _manager;

    public SkillsManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"skills-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new SkillsManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void GetSkill_ExistingFile_ReturnsContent()
    {
        File.WriteAllText(Path.Combine(_tempDir, "build.md"), "# Build\nRun `make`");

        var content = _manager.GetSkill("build");

        Assert.NotNull(content);
        Assert.Contains("Run `make`", content);
    }

    [Fact]
    public void GetSkill_MissingFile_ReturnsNull()
    {
        var content = _manager.GetSkill("nonexistent");

        Assert.Null(content);
    }

    [Fact]
    public void ListSkills_ReturnsAllSkillNames()
    {
        File.WriteAllText(Path.Combine(_tempDir, "build.md"), "build");
        File.WriteAllText(Path.Combine(_tempDir, "test.md"), "test");
        File.WriteAllText(Path.Combine(_tempDir, "install-sdk.md"), "sdk");

        var skills = _manager.ListSkills();

        Assert.Equal(3, skills.Length);
        Assert.Contains("build", skills);
        Assert.Contains("test", skills);
        Assert.Contains("install-sdk", skills);
    }

    [Fact]
    public void ListSkills_EmptyDirectory_ReturnsEmpty()
    {
        var skills = _manager.ListSkills();

        Assert.Empty(skills);
    }

    [Fact]
    public void ListSkills_MissingDirectory_ReturnsEmpty()
    {
        var manager = new SkillsManager(Path.Combine(_tempDir, "does-not-exist"));

        var skills = manager.ListSkills();

        Assert.Empty(skills);
    }

    [Fact]
    public void GetBuildAndTestInstructions_BothSkillsPresent_CombinesBoth()
    {
        File.WriteAllText(Path.Combine(_tempDir, "build.md"), "Run `cargo build`");
        File.WriteAllText(Path.Combine(_tempDir, "test.md"), "Run `cargo test`");

        var instructions = _manager.GetBuildAndTestInstructions();

        Assert.Contains("cargo build", instructions);
        Assert.Contains("cargo test", instructions);
    }

    [Fact]
    public void GetBuildAndTestInstructions_OnlyBuildSkill_ReturnsBuildOnly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "build.md"), "Run `go build ./...`");

        var instructions = _manager.GetBuildAndTestInstructions();

        Assert.Contains("go build", instructions);
        Assert.DoesNotContain("test", instructions.ToLowerInvariant().Replace("build and test", ""));
    }

    [Fact]
    public void GetBuildAndTestInstructions_NoSkills_ReturnsFallbackMessage()
    {
        var instructions = _manager.GetBuildAndTestInstructions();

        Assert.Contains("skills/", instructions);
    }

    [Fact]
    public void Names_ContainsExpectedValues()
    {
        Assert.Equal("build", SkillsManager.Names.Build);
        Assert.Equal("test", SkillsManager.Names.Test);
        Assert.Equal("install-sdk", SkillsManager.Names.InstallSdk);
    }
}
