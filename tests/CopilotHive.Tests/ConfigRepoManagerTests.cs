using System.Diagnostics;
using System.Reflection;
using CopilotHive.Configuration;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

public class ConfigRepoManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigRepoManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-cfgtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── YAML parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseConfig_FullConfig_ParsesAllFields()
    {
        const string yaml = """
            version: "2.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: develop
              - name: my-api
                url: https://github.com/org/my-api.git
            workers:
              coder:
                model: claude-opus-4.6
              tester:
                model: gpt-5-mini
            orchestrator:
              model: gpt-5.4
              max_iterations: 5
              max_retries_per_task: 2
              always_improve: true
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("2.0", config.Version);
        Assert.Equal(2, config.Repositories.Count);
        Assert.Equal("my-app", config.Repositories[0].Name);
        Assert.Equal("https://github.com/org/my-app.git", config.Repositories[0].Url);
        Assert.Equal("develop", config.Repositories[0].DefaultBranch);
        Assert.Equal("my-api", config.Repositories[1].Name);
        Assert.Equal("main", config.Repositories[1].DefaultBranch); // default

        Assert.Equal(2, config.Workers.Count);
        Assert.Equal("claude-opus-4.6", config.Workers["coder"].Model);
        Assert.Equal("gpt-5-mini", config.Workers["tester"].Model);

        Assert.Equal("gpt-5.4", config.Orchestrator.Model);
        Assert.Equal(5, config.Orchestrator.MaxIterations);
        Assert.Equal(2, config.Orchestrator.MaxRetriesPerTask);
        Assert.True(config.Orchestrator.AlwaysImprove);
    }

    [Fact]
    public void ParseConfig_MinimalConfig_UsesDefaults()
    {
        const string yaml = """
            version: "1.0"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.Workers);
        Assert.Equal("claude-sonnet-4.6", config.Orchestrator.Model);
        Assert.Equal(10, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
        Assert.False(config.Orchestrator.AlwaysImprove);
    }

    [Fact]
    public void ParseConfig_EmptyYaml_ReturnsDefaults()
    {
        var config = ConfigRepoManager.ParseConfig("");

        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.Workers);
    }

    [Fact]
    public void ParseConfig_MissingFields_FallsBackToDefaults()
    {
        const string yaml = """
            repositories:
              - name: only-repo
                url: https://github.com/org/only-repo.git
            orchestrator:
              max_iterations: 20
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Single(config.Repositories);
        Assert.Equal("main", config.Repositories[0].DefaultBranch);
        Assert.Equal("claude-sonnet-4.6", config.Orchestrator.Model);
        Assert.Equal(20, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
    }

    [Fact]
    public void ParseConfig_UnknownFields_AreIgnored()
    {
        const string yaml = """
            version: "1.0"
            some_future_field: true
            orchestrator:
              model: gpt-5.4
              unknown_setting: 42
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("gpt-5.4", config.Orchestrator.Model);
    }

    // ── IsRepositoryAllowed ──────────────────────────────────────────────────

    [Fact]
    public async Task IsRepositoryAllowed_AllowedUrl_ReturnsTrue()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_DisallowedUrl_ReturnsFalse()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.False(manager.IsRepositoryAllowed("https://github.com/other/repo.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_TrailingSlashVariation_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git/"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_CaseInsensitive_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/Org/My-App.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_WhitespaceVariation_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("  https://github.com/org/my-app.git  "));
    }

    [Fact]
    public void IsRepositoryAllowed_NoConfigLoaded_ReturnsFalse()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    // ── AGENTS.md loading ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAgentsMdAsync_ExistingRole_ReturnsContent()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "coder.agents.md"),
            "# Coder\nYou write great code.",
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.Coder, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Contains("You write great code.", content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_NonexistentRole_ReturnsNull()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.MergeWorker, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_CaseInsensitiveRole()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "tester.agents.md"),
            "# Tester instructions",
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.Tester, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Contains("Tester instructions", content);
    }

    // ── LoadConfigAsync from file ────────────────────────────────────────────

    [Fact]
    public async Task LoadConfigAsync_ReadsFromDisk()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: test-repo
                url: https://github.com/test/repo.git
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var config = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.0", config.Version);
        Assert.Single(config.Repositories);
        Assert.Equal("test-repo", config.Repositories[0].Name);
    }

    [Fact]
    public async Task LoadConfigAsync_MissingFile_ThrowsFileNotFound()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.LoadConfigAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadConfigAsync_CachesResult()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: cached-repo
                url: https://github.com/test/cached.git
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
    }

    // ── WriteConfigAsync tests ────────────────────────────────────────────────

    [Fact]
    public async Task WriteConfigAsync_SerializesWithSnakeCaseKeys()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Models = new ModelsConfig { CompactionModel = "mini-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("compaction_model:", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_OmitsNullDefaultValues()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "my-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("available_models", yaml);
        Assert.DoesNotContain("compaction_model", yaml);
        Assert.DoesNotContain("composer", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_UpdatesCachedConfig()
    {
        // Write initial config to disk so LoadConfigAsync can read it
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            "version: \"1.0\"",
            TestContext.Current.CancellationToken);

        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        // Load once to populate cache
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.0", first.Version);

        // Write new config
        var updated = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "updated-model" },
        };
        await manager.WriteConfigAsync(updated, TestContext.Current.CancellationToken);

        // Load again — should return the updated config from cache
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("updated-model", second.Orchestrator.Model);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private async Task<ConfigRepoManager> CreateManagerWithConfigAsync(string yaml)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), yaml, TestContext.Current.CancellationToken);
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        return manager;
    }

    // ── Semaphore serialization ───────────────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        // Pre-acquire the semaphore to block any git operation.
        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start a SyncRepoAsync that will block waiting for the semaphore.
        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            // This will block on WaitAsync until we release the semaphore below.
            await manager.SyncRepoAsync(ct);
        }, ct);

        // Wait until the task has at least started.
        await started.Task;

        // The task should NOT complete while the semaphore is held.
        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "SyncRepoAsync should be blocked while the semaphore is held");

        // Release the semaphore — note: the call will fail (no .git, no real remote),
        // but it will at least proceed past the lock.
        gitLock.Release();

        // The task should now run and eventually throw (no real git repo), but it must
        // have been unblocked by the semaphore release.
        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    [Fact]
    public async Task CommitAllChangesAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await manager.CommitAllChangesAsync("test commit", ct);
        }, ct);

        await started.Task;

        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "CommitAllChangesAsync should be blocked while the semaphore is held");

        gitLock.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    [Fact]
    public async Task CommitFileAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await manager.CommitFileAsync("goals.yaml", "update goals", ct);
        }, ct);

        await started.Task;

        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "CommitFileAsync should be blocked while the semaphore is held");

        gitLock.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    // ── Merge conflict abort ──────────────────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_WhenPullFails_AttemptsMergeAbortAndRethrows()
    {
        // Arrange — set up a local bare remote and two clones that create a conflict.
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test"]);

            // Write initial file and push.
            var filePath = Path.Combine(clone1Dir, "conflict.txt");
            await File.WriteAllTextAsync(filePath, "line1\n", TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare, modify conflict.txt, commit but DON'T push yet.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "conflict.txt"), "clone2 change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "clone2 local commit"]);

            // Clone1: push a conflicting change to the same file.
            await File.WriteAllTextAsync(filePath, "clone1 different change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "clone1 conflict"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Now clone2 has a local commit that conflicts with the remote.
            // Set merge strategy to always merge (not fast-forward) so a conflict occurs.
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — SyncRepoAsync on clone2 should fail (merge conflict) and
            // attempt git merge --abort before rethrowing.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

            // Assert — the exception comes from the pull failure (not abort).
            Assert.Contains("git exited with code", ex.Message);

            // After TryAbortMergeAsync the repo should be in a clean non-merging state.
            // git status should not show "MERGING".
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Conflict recovery (PushWithConflictRecoveryAsync via CommitFileAsync) ──

    [Fact]
    public async Task CommitFileAsync_WhenPullConflicts_RebasesAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the same file (different line, rebase-friendly).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base content\nremote line\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to a DIFFERENT line so rebase can succeed.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local content\nbase content\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitFileAsync should detect the conflict, abort merge, reset,
            // rebase onto remote, and push. No exception should be thrown.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.CommitFileAsync("config.txt", "local change", TestContext.Current.CancellationToken);

            // Assert — the remote now has both commits (rebase succeeded).
            // Clone the remote to a fresh clone to verify state.
            var verifyDir = Path.Combine(Path.GetTempPath(), $"cfgtest-verify-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(verifyDir);
                await RunGitCommandAsync(Path.GetDirectoryName(verifyDir)!,
                    ["clone", bareDir, Path.GetFileName(verifyDir)]);
                var remoteContent = await File.ReadAllTextAsync(
                    Path.Combine(verifyDir, "config.txt"), TestContext.Current.CancellationToken);

                // After a successful rebase, both lines should be present since they
                // modified different parts of the file.
                Assert.Contains("base content", remoteContent);
                Assert.Contains("local content", remoteContent);
                Assert.Contains("remote line", remoteContent);

                // Verify both commits exist on the remote.
                var (logOutput, _) = await RunGitCommandRawAsync(verifyDir, ["log", "--oneline"]);
                Assert.Contains("local change", logOutput);
                Assert.Contains("remote change", logOutput);
            }
            finally
            {
                if (Directory.Exists(verifyDir))
                    try { Directory.Delete(verifyDir, recursive: true); } catch { }
            }

            // Clone2 should be in a clean state (not mid-merge or mid-rebase).
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("working tree clean", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitFileAsync_WhenPullAndRebaseBothFail_ResetsAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "line1\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the SAME line.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote-only-content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to the SAME line (will conflict on rebase too).
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local-only-content\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitFileAsync should: pull fails → abort merge → reset →
            // pull --rebase fails → abort rebase → reset hard → push local commit.
            // The push will also fail (non-fast-forward) since the local commit diverged.
            // The exception from git push propagates, but the repo must be in a clean state.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.CommitFileAsync("config.txt", "local change", TestContext.Current.CancellationToken));

            // The exception should come from the push failure (not a merge/rebase conflict).
            Assert.Contains("git exited with code", ex.Message);

            // The repo should be in a clean state — not mid-merge or mid-rebase.
            // This is the key assertion: even though the push failed, the recovery
            // logic (abort merge, reset hard, abort rebase, reset hard) ensured the
            // repo is not stuck in a conflicted state.
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);

            // Verify the local file still contains the local commit's content
            // (reset --hard HEAD preserved the local commit).
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Contains("local-only-content", localContent);

            // Verify the local commit exists in the local log.
            var (logOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["log", "--oneline"]);
            Assert.Contains("local change", logOutput);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitAllChangesAsync_WhenPullConflicts_RebasesAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "data.txt"), "base\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "data.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the same file (different line for rebase success).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "data.txt"), "base\nremote line\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "data.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to a DIFFERENT line so rebase can succeed.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "data.txt"), "local line\nbase\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitAllChangesAsync should detect the conflict, abort merge,
            // reset, rebase onto remote, and push. No exception should be thrown.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.CommitAllChangesAsync("local change via commit-all", TestContext.Current.CancellationToken);

            // Assert — the remote now has both commits (rebase succeeded).
            var verifyDir = Path.Combine(Path.GetTempPath(), $"cfgtest-verify-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(verifyDir);
                await RunGitCommandAsync(Path.GetDirectoryName(verifyDir)!,
                    ["clone", bareDir, Path.GetFileName(verifyDir)]);
                var remoteContent = await File.ReadAllTextAsync(
                    Path.Combine(verifyDir, "data.txt"), TestContext.Current.CancellationToken);

                // After a successful rebase, both lines should be present.
                Assert.Contains("base", remoteContent);
                Assert.Contains("local line", remoteContent);
                Assert.Contains("remote line", remoteContent);

                // Verify the local commit exists on the remote.
                var (logOutput, _) = await RunGitCommandRawAsync(verifyDir, ["log", "--oneline"]);
                Assert.Contains("local change via commit-all", logOutput);
                Assert.Contains("remote change", logOutput);
            }
            finally
            {
                if (Directory.Exists(verifyDir))
                    try { Directory.Delete(verifyDir, recursive: true); } catch { }
            }

            // Clone2 should be in a clean state.
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── ResetToRemoteAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResetToRemoteAsync_ResetsToRemoteState()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote commit"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare, then make local commits.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Make a local commit that diverges from remote.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "local commit"]);

            // Act — ResetToRemoteAsync should discard local commits and match remote.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

            // Assert — local file matches remote content.
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Equal("remote content\n", localContent);

            // Git status should be clean (no local changes, working tree clean).
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.Contains("working tree clean", statusOutput, StringComparison.OrdinalIgnoreCase);

            // Local log should match remote log — no "local commit".
            var (logOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["log", "--oneline"]);
            Assert.Contains("remote commit", logOutput);
            Assert.DoesNotContain("local commit", logOutput);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ResetToRemoteAsync_AbortsActiveMerge()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare (at "initial" commit), then make a local conflicting commit.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "local change"]);

            // Clone1: push a conflicting change to the SAME line (diverges from clone2).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: fetch the remote change, then start a merge that will conflict.
            await RunGitCommandAsync(clone2Dir, ["fetch", "origin"]);
            // merge will fail with a conflict — this puts the repo in a merging state.
            var (_, _) = await RunGitCommandRawAsync(clone2Dir, ["merge", "origin/HEAD"]);
            // Verify we're in a merging state (git status shows "unmerged paths").
            var (statusBefore, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.Contains("unmerged", statusBefore, StringComparison.OrdinalIgnoreCase);

            // Act — ResetToRemoteAsync should abort the merge and reset to remote.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

            // Assert — repo is no longer in a merging state.
            var (statusAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("unmerged", statusAfter, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MERGING", statusAfter, StringComparison.OrdinalIgnoreCase);

            // The local content should now match the remote.
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Equal("remote change\n", localContent);

            // Status should be clean.
            Assert.Contains("working tree clean", statusAfter, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── Git helper for tests ──────────────────────────────────────────────────

    private static SemaphoreSlim GetGitLock(ConfigRepoManager manager)
    {
        var field = typeof(ConfigRepoManager).GetField(
            "_gitLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var semaphore = field!.GetValue(manager) as SemaphoreSlim;
        Assert.NotNull(semaphore);
        return semaphore!;
    }

    private static async Task RunGitCommandAsync(string workingDir, string[] args)
    {
        var (_, error) = await RunGitCommandRawAsync(workingDir, args);
        _ = error; // errors allowed during test setup
    }

    private static async Task<(string output, string error)> RunGitCommandRawAsync(
        string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
        return (stdoutTask.Result, stderrTask.Result);
    }
}

// Fake subclass that no-ops CommitFileAsync to avoid real git calls in WriteConfigAsync tests
internal sealed class FakeConfigRepoManager(string url, string path) : ConfigRepoManager(url, path)
{
    public List<(string File, string Message)> Commits { get; } = [];

    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        Commits.Add((filePath, commitMessage));
        return Task.CompletedTask;
    }
}
