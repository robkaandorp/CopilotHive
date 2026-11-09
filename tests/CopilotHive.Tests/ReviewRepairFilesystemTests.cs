using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Independent reproductions of the review's unchanged-link disclosure topologies.
/// Hard-link aliases and link replacement BETWEEN resolution and open remain accepted limits;
/// the iteration-1 hard-link control remains unchanged. These tests deliberately replace NO link
/// while reading: repetition is stability evidence, not a claim about replacement races.
/// </summary>
public sealed class ReviewRepairFilesystemTests : IDisposable
{
    private const string Secret = "review-repair-filesystem-secret";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "review-fs-" + Guid.NewGuid().ToString("N"));
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();

    [Theory]
    [InlineData("absolute-leaf")]
    [InlineData("relative-leaf")]
    [InlineData("directory-target")]
    [InlineData("outside-target")]
    [InlineData("root-ancestor")]
    public async Task UnchangedAliasTopology_IsPhysicallyDeniedOnEveryRead(string topology)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Physical POSIX symlink topology; Windows reparse execution unverified.");
        var physicalRepo = Path.Combine(_temp, "physical-parent", "repo");
        Directory.CreateDirectory(Path.Combine(physicalRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(physicalRepo, "agents"));
        var config = Path.Combine(physicalRepo, ".git", "config");
        File.WriteAllText(config, Secret);
        File.WriteAllText(Path.Combine(physicalRepo, "agents", "coder.agents.md"), "first\nsecond\n");
        File.CreateSymbolicLink(Path.Combine(physicalRepo, "ordinary-link"), "agents/coder.agents.md");
        var configuredRepo = physicalRepo;
        var expectedTarget = config;
        var leaf = Path.Combine(physicalRepo, "leaf");
        var request = "leaf";
        var reason = "git metadata";
        switch (topology)
        {
            case "absolute-leaf":
            case "relative-leaf":
                Directory.CreateSymbolicLink(Path.Combine(physicalRepo, "bridge"), ".git");
                File.CreateSymbolicLink(leaf, topology == "absolute-leaf"
                    ? Path.Combine(physicalRepo, "bridge", "config") : "bridge/config");
                Assert.Equal(Path.Combine(physicalRepo, ".git"),
                    new DirectoryInfo(Path.Combine(physicalRepo, "bridge")).ResolveLinkTarget(true)!.FullName);
                Assert.Equal(topology == "absolute-leaf", Path.IsPathRooted(new FileInfo(leaf).LinkTarget!));
                break;
            case "directory-target":
                // The alias is an ANCESTOR of the directory target, not its final component.
                // A direct link-to-link would be resolved even by the rejected shortcut.
                Directory.CreateDirectory(Path.Combine(physicalRepo, ".git", "nested"));
                expectedTarget = Path.Combine(physicalRepo, ".git", "nested", "config");
                File.WriteAllText(expectedTarget, Secret);
                Directory.CreateSymbolicLink(Path.Combine(physicalRepo, "bridge"), ".git");
                Directory.CreateSymbolicLink(leaf, Path.Combine(physicalRepo, "bridge", "nested"));
                leaf = Path.Combine(leaf, "config");
                request = "leaf/config";
                Assert.Equal(Path.Combine(physicalRepo, ".git"),
                    new DirectoryInfo(Path.Combine(physicalRepo, "bridge")).ResolveLinkTarget(true)!.FullName);
                break;
            case "outside-target":
                var outside = Path.Combine(_temp, "outside");
                Directory.CreateDirectory(outside);
                expectedTarget = Path.GetFullPath(Path.Combine(outside, "config"));
                Assert.False(expectedTarget.StartsWith(physicalRepo + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                File.WriteAllText(expectedTarget, Secret);
                Directory.CreateSymbolicLink(Path.Combine(physicalRepo, "bridge"), outside);
                File.CreateSymbolicLink(leaf, Path.Combine(physicalRepo, "bridge", "config"));
                Assert.Equal(outside, new DirectoryInfo(Path.Combine(physicalRepo, "bridge")).ResolveLinkTarget(true)!.FullName);
                reason = "outside the config repo";
                break;
            case "root-ancestor":
                var aliasParent = Path.Combine(_temp, "aliased-parent");
                Directory.CreateSymbolicLink(aliasParent, Path.GetDirectoryName(physicalRepo)!);
                configuredRepo = Path.Combine(aliasParent, "repo");
                Assert.Equal(Path.GetDirectoryName(physicalRepo), new DirectoryInfo(aliasParent).ResolveLinkTarget(true)!.FullName);
                Directory.CreateSymbolicLink(Path.Combine(physicalRepo, "bridge"), ".git");
                File.CreateSymbolicLink(leaf, Path.Combine(physicalRepo, "bridge", "config"));
                break;
            default: throw new InvalidOperationException(topology);
        }

        // Premises BEFORE production: these are viable disclosure aliases, not broken links or
        // not-found paths. Direct OS reads through the alias reach exactly the secret-bearing file.
        Assert.Equal(Secret, File.ReadAllText(expectedTarget));
        Assert.Equal(File.ReadAllText(expectedTarget), File.ReadAllText(leaf));
        Assert.Equal(Secret, File.ReadAllText(Path.Combine(configuredRepo, request)));
        var composer = new Composer("test-model", NullLogger<Composer>.Instance,
            new GoalStore(_db, NullLogger<GoalStore>.Instance), stateDir: _temp,
            configRepo: new ConfigRepoManager("https://github.com/org/config.git", configuredRepo));
        var ct = TestContext.Current.CancellationToken;
        const string ordinary = "1: first\n2: second";
        Assert.Equal(ordinary, (await composer.ReadConfigFileAsync("agents/coder.agents.md", cancellationToken: ct)).ReplaceLineEndings("\n"));
        Assert.Equal(ordinary, (await composer.ReadConfigFileAsync("ordinary-link", cancellationToken: ct)).ReplaceLineEndings("\n"));
        for (var i = 0; i < 20; i++)
        {
            var response = await composer.ReadConfigFileAsync(request, cancellationToken: ct);
            Assert.StartsWith("❌ Path '", response);
            Assert.Contains(reason, response);
            Assert.EndsWith("Access denied.", response);
            Assert.DoesNotContain(Secret, response);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        TestHelpers.ForceDeleteDirectory(_temp);
    }
}
