using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Worker;
using CopilotHive.Workers;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Actual git launches through the worker and executor, not ProcessRunner captures. An ephemeral
/// CONNECT proxy serves git-http-backend behind test-local TLS; URLs and git's challenges retain
/// their real scheme/host/port. The PATH wrapper delegates to real git after running alias.hiveprobe
/// in that child's environment. Its evidence contains digests, never credential literals.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class WorkerTargetTransportIntegrationTests : IAsyncDisposable
{
    private const string Token = "worker-transport-integration-secret-A";
    private const string OtherToken = "worker-transport-integration-secret-B";
    private const string Origin = "https://github.com/org/repo.git";
    private const string CredentialName = "COPILOTHIVE_WORKER_GIT_ASKPASS_CREDENTIAL";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "worker-transport-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _old = new();
    private readonly TextWriter _oldOut = Console.Out;
    private readonly TextWriter _oldErr = Console.Error;
    private readonly StringWriter _output = new();
    private readonly List<string> _helperDirs = [];
    private readonly string _realGit;
    private readonly string _execPath;
    private readonly string _global;
    private readonly string _launchLog;
    private string GlobalContent { get; set; } = "[core]\n\tautocrlf = false\n[commit]\n\tgpgsign = false\n";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkerTargetTransportIntegrationTests()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("POSIX git and CONNECT fixture.");
        Directory.CreateDirectory(_root);
        _realGit = FindGit();
        _execPath = Raw(_root, "--exec-path").Stdout.Trim();
        _global = Path.Combine(_root, "global.gitconfig");
        _launchLog = Path.Combine(_root, "launches.txt");
        File.WriteAllText(_global, GlobalContent);
        Set("GIT_CONFIG_GLOBAL", _global);
        Set("GIT_CONFIG_NOSYSTEM", "1");
        Set("GIT_EXEC_PATH", _execPath);
        Set("HOME", _root);
        Set("XDG_CONFIG_HOME", _root);
        Set("GH_TOKEN", null);
        Set("GITHUB_TOKEN", null);
        Set("GITHUB_CONFIG_REPO_TOKEN", null);
        Set(CredentialName, null);
        Set("GIT_ASKPASS", null);
        Set("SSH_ASKPASS", null);
        Set("GIT_CONFIG_PARAMETERS", null);
        Set("GIT_CONFIG_COUNT", null);
        Set("WORKER_WORK_ROOT", _root);
        Set("LC_ALL", "C");
        Set("LANG", "C");
        foreach (var key in new[] { "NO_PROXY", "no_proxy", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" }) Set(key, null);
        Console.SetOut(_output);
        Console.SetError(_output);
    }

    [Fact]
    public async Task RealAlias_ActualChildEnvironmentAndHelperInvocation_ArePaired()
    {
        RequireLinux();
        Set("GH_TOKEN", "inherited-gh-marker");
        Set("GITHUB_TOKEN", "inherited-github-marker");
        Set("SSH_ASKPASS", "/inherited/ssh-marker");
        Set("GIT_ASKPASS", "/inherited/askpass-marker");
        Set("GIT_CONFIG_PARAMETERS", "'credential.helper=store'");
        using var transport = WorkerGitCredentialTransport.Create(Token, Origin);
        Assert.NotNull(transport);
        var result = await GitOperations.RunTokenizedGitCommandAsync(_root,
            ["-c", "alias.hiveprobe=" + Probe, "hiveprobe"], Ct, transport);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        AssertProbe(result.Stdout, transport.ScriptPath, Token);
        Assert.DoesNotContain(Token, result.Stdout);
        Assert.Equal("inherited-gh-marker", Environment.GetEnvironmentVariable("GH_TOKEN"));
        Assert.Null(Environment.GetEnvironmentVariable(CredentialName));
    }

    [Fact]
    public async Task Executor_TwoSequentialAssignments_CloneAndPushWithOwnCredentialAndSanitizedOrigin()
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token, OtherToken);
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        var calls = new RecordingOperations();
        var turn = 0;
        var runner = new TaskExecutorTests.MockAgentRunner
        {
            PromptResponder = (_, dir, _) =>
            {
                File.WriteAllText(Path.Combine(dir, "assignment.txt"), "assignment-" + ++turn);
                Setup(dir, "add", "assignment.txt");
                Setup(dir, "commit", "-m", "assignment " + turn);
                return Task.FromResult("completed assignment " + turn);
            },
        };
        var executor = new TaskExecutor(runner, gitOperations: calls);
        foreach (var token in new[] { Token, OtherToken })
        {
            var before = server.Snapshot().Count;
            var result = await executor.ExecuteAsync(TaskFor(token), Ct);
            Assert.Equal(TaskOutcome.Completed, result.Status);
            Assert.True(result.GitStatus!.Pushed, result.Output);
            Assert.Equal(Origin, Raw(Path.Combine(_root, "target"), "config", "--get", "remote.origin.url").Stdout.Trim());
            Assert.Contains(server.Snapshot().Skip(before), r => r.Password == token && r.Path.Contains("git-upload-pack"));
            Assert.Contains(server.Snapshot().Skip(before), r => r.Password == token && r.Path.Contains("git-receive-pack"));
            Assert.DoesNotContain(server.Snapshot().Skip(before), r => r.Password == (token == Token ? OtherToken : Token));
            AssertNoSecrets(result.Output, string.Join('\n', result.Metrics!.Issues));
            AssertHelpersGone();
        }
        Assert.Equal(2, calls.CloneUrls.Count);
        Assert.All(calls.CloneUrls, u => Assert.Equal(Origin, u));
        Assert.Equal(2, calls.PushTransports.Count);
        Assert.All(calls.PushTransports, t => Assert.NotNull(t));
        Assert.NotSame(calls.CloneTransports[0], calls.CloneTransports[1]);
        Assert.Same(calls.CloneTransports[0], calls.PushTransports[0]);
        Assert.Same(calls.CloneTransports[1], calls.PushTransports[1]);
        Assert.All(calls.AllArguments, a => AssertNoSecrets(a));
        var blocks = LaunchBlocks();
        Assert.Equal(2, blocks.Count(b => b.StartsWith("clone\n")));
        Assert.Equal(2, blocks.Count(b => b.StartsWith("push\n")));
        for (var i = 0; i < 2; ++i)
        {
            var token = i == 0 ? Token : OtherToken;
            AssertProbe(blocks.Where(b => b.StartsWith("clone\n")).ElementAt(i), calls.HelperPaths[i], token);
            AssertProbe(blocks.Where(b => b.StartsWith("push\n")).ElementAt(i), calls.HelperPaths[i], token);
        }
        Assert.Equal("assignment-2", Raw(bare, "show", "refs/heads/feature:assignment.txt").Stdout.Trim());
        AssertNoSecretsOnDisk();
    }

    [Fact]
    public async Task CreateBranch_RealFetch_RepairsOriginBeforeReleaseAndObtainsRemoteOnlyCommit()
    {
        RequireLinux();
        var bare = CreateBare(includeRelease: false);
        var repo = Path.Combine(_root, "fetch-target");
        Setup(_root, "clone", bare, repo);
        // Advance the remote only AFTER cloning: neither the ref nor the commit object can
        // already exist locally. Fetch must acquire real new content, not merely restore a ref.
        var seed = Path.Combine(_root, "seed");
        Setup(seed, "checkout", "-b", "release");
        File.WriteAllText(Path.Combine(seed, "release.txt"), "remote-only-release");
        Setup(seed, "add", "release.txt"); Setup(seed, "commit", "-m", "remote release");
        Setup(seed, "push", bare, "release");
        var remoteSha = Raw(bare, "rev-parse", "release").Stdout.Trim();
        Assert.NotEqual(0, Raw(repo, "cat-file", "-e", remoteSha).ExitCode);
        Setup(repo, "remote", "set-url", "origin", TokenUrl(Token));
        Assert.Contains(Token, File.ReadAllText(Path.Combine(repo, ".git", "config")));
        await using var server = new GitProxy(bare, Token);
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        using var transport = WorkerGitCredentialTransport.Create(Token, Origin);
        await GitOperations.CreateBranchAsync(repo, "feature", "release", Ct, transport);
        var fetch = Assert.Single(LaunchBlocks(), b => b.StartsWith("fetch\n"));
        AssertProbe(fetch, transport!.ScriptPath, Token);
        Assert.Equal(Origin, Value(fetch, "origin_at_launch"));
        Assert.Equal("remote-only-release", File.ReadAllText(Path.Combine(repo, "release.txt")));
        Assert.Equal(Raw(bare, "rev-parse", "release").Stdout, Raw(repo, "rev-parse", "HEAD").Stdout);
        Assert.Equal(Origin, Raw(repo, "config", "--get", "remote.origin.url").Stdout.Trim());
        Assert.Contains(server.Snapshot(), r => r.Password == Token);
        AssertNoSecretsOnDisk();
    }

    [Fact]
    public async Task Push_EligibleExplicit443PushUrl_RepairsStaleOriginAndPublishesCommit()
    {
        RequireLinux();
        var bare = CreateBare();
        var repo = Path.Combine(_root, "push-target");
        Setup(_root, "clone", bare, repo);
        Setup(repo, "remote", "set-url", "origin", TokenUrl(Token));
        Setup(repo, "config", "remote.origin.pushurl", "https://github.com:443/org/repo.git");
        File.WriteAllText(Path.Combine(repo, "published.txt"), "published-content");
        Setup(repo, "add", "published.txt");
        Setup(repo, "-c", "user.name=Transport", "-c", "user.email=t@test.invalid", "commit", "-m", "publish");
        await using var server = new GitProxy(bare, Token);
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        using var transport = WorkerGitCredentialTransport.Create(Token, Origin);
        await GitOperations.PushBranchAsync(repo, "main", Ct, transport);
        var push = Assert.Single(LaunchBlocks(), b => b.StartsWith("push\n"));
        AssertProbe(push, transport!.ScriptPath, Token);
        Assert.Equal(Origin, Value(push, "origin_at_launch"));
        Assert.Equal("https://github.com:443/org/repo.git", Raw(repo, "config", "--get", "remote.origin.pushurl").Stdout.Trim());
        Assert.True(server.Snapshot().Any(r => r.Destination == "github.com:443" && r.Password == Token),
            "Eligible explicit-port push never authenticated through github.com:443.");
        Assert.Equal("published-content", Raw(bare, "show", "main:published.txt").Stdout.Trim());
        AssertNoSecretsOnDisk();
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("indexed")]
    [InlineData("global")]
    public async Task Executor_InitiallyNoneligible_InheritedHelperChannelsMustNotSelectOrPersist(string channel)
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token) { RefuseAll = true };
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        var recorder = Path.Combine(_root, "ambient-helper.sh");
        var calls = Path.Combine(_root, "ambient-helper-calls.txt");
        // Test-only inherited credential, DISTINCT from the assignment token. Observe only method
        // names on disk, never secret stdin. This helper really is reachable in the control below.
        WriteExecutable(recorder, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> " + ShellQuote(calls) +
            "\ncat >/dev/null\nif [ \"$1\" = get ]; then printf 'username=ambient-user\\npassword=ambient-credential-marker\\n'; fi\n");
        if (channel == "parameters") Set("GIT_CONFIG_PARAMETERS", "'credential.helper=" + recorder + "'");
        else if (channel == "indexed")
        {
            Set("GIT_CONFIG_COUNT", "5");
            Set("GIT_CONFIG_KEY_4", "credential.helper"); Set("GIT_CONFIG_VALUE_4", recorder);
        }
        else if (channel == "global")
        {
            GlobalContent += "[credential]\n\thelper = " + recorder + "\n";
            File.WriteAllText(_global, GlobalContent);
        }
        else throw new InvalidOperationException("Unknown inherited channel.");
        var inherited = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string key && e.Value is string value) inherited[key] = value;
        var selected = await CredentialFill(inherited, "protocol=https\nhost=github.com\n\n");
        Assert.Equal(0, selected.ExitCode);
        Assert.Contains("password=ambient-credential-marker", selected.Stdout);
        Assert.Contains("get", File.ReadAllLines(calls));
        File.Delete(calls);

        var result = await new TaskExecutor(new CallbackAgentRunner()).ExecuteAsync(TaskFor(Token) with
        { Repositories = [new TargetRepository { Name = "target", Url = "https://x-access-token:" + Token + "@gitlab.com/org/repo.git" }] }, Ct);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        var clone = Assert.Single(LaunchBlocks(), b => b.StartsWith("clone\n"));
        Assert.Contains("askpass=[]", clone); Assert.Contains("credential_present=[]", clone);
        Assert.NotEmpty(server.Snapshot());
        AssertNoSecrets(result.Output, clone, string.Join('\n', result.Metrics!.Issues));
        AssertNoSecretsOnDisk();
        Assert.Multiple(
            () => Assert.False(File.Exists(calls), "Credential-free git invoked the inherited helper (get/store/erase)."),
            () => Assert.True(server.Snapshot().All(r => r.Authorization is null), "Noneligible destination received an ambient helper credential."),
            () => Assert.DoesNotContain(recorder, Value(clone, "parameters")));
    }

    [Theory]
    [InlineData("https://github.com:8443/org/repo.git")]
    [InlineData("https://gitlab.com/org/repo.git")]
    public async Task Executor_InitiallyNoneligibleUrl_ChildHasNoTransportAndFailureIsSanitized(string url)
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token);
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        Set("GIT_ASKPASS", "/inherited/askpass-marker");
        Set("SSH_ASKPASS", "/inherited/ssh-marker");
        Set(CredentialName, Token); // a stale inherited transport variable must be removed too
        var result = await new TaskExecutor(new CallbackAgentRunner()).ExecuteAsync(
            TaskFor(Token) with { Repositories = [new TargetRepository { Name = "target", Url = url.Replace("://", $"://x-access-token:{Token}@") }] }, Ct);
        Assert.Equal(TaskOutcome.Failed, result.Status);
        var clone = Assert.Single(LaunchBlocks(), b => b.StartsWith("clone\n"));
        Assert.Contains("askpass=[]", clone);
        Assert.Equal(Token, Environment.GetEnvironmentVariable(CredentialName)); // test-seeded stale parent value
        Assert.NotEmpty(server.Snapshot());
        Assert.All(server.Snapshot(), r => Assert.Null(r.Authorization));
        AssertNoSecrets(clone, result.Output, string.Join('\n', result.Metrics!.Issues));
        AssertNoSecretsOnDisk();
        Assert.Multiple(
            () => Assert.Contains("credential_present=[]", clone),
            () => Assert.Equal("", Value(clone, "ssh")));

    }

    [Theory]
    [InlineData("pushurl", "https://github.com:8443/org/repo.git")]
    [InlineData("pushurl", "https://gitlab.com/org/repo.git")]
    [InlineData("redirect", "https://gitlab.com/org/repo.git")]
    public async Task EffectiveNoneligibleChallenge_IsReachedButReceivesNoCredential(string route, string destination)
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token) { RedirectTo = route == "redirect" ? destination : null };
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        var repo = Path.Combine(_root, "route-target");
        Setup(_root, "clone", bare, repo);
        Setup(repo, "remote", "set-url", "origin", Origin);
        if (route == "pushurl") Setup(repo, "config", "remote.origin.pushurl", destination);
        using var transport = WorkerGitCredentialTransport.Create(Token, Origin);
        var ex = await Assert.ThrowsAsync<GitOperationException>(() => route == "pushurl"
            ? GitOperations.PushBranchAsync(repo, "main", Ct, transport)
            : GitOperations.CloneRepositoryAsync(Origin, Path.Combine(_root, "redirect-target"), Ct, transport));
        Assert.Contains(route == "pushurl" ? "Failed to push" : "Failed to clone", ex.Message);
        var reached = server.Snapshot().Where(r => r.Host == new Uri(destination).Authority).ToList();
        Assert.NotEmpty(reached);
        Assert.All(reached, r => Assert.Null(r.Authorization));
        AssertNoSecrets(ex.ToString());
        AssertNoSecretsOnDisk();
    }

    [Theory]
    [InlineData("path")]
    [InlineData("file")]
    [InlineData("ssh")]
    public async Task Push_NoneligibleScheme_FailsWithoutCredentialInDiagnosticsOrFiles(string scheme)
    {
        RequireLinux();
        var bare = CreateBare();
        var repo = Path.Combine(_root, "scheme-target");
        Setup(_root, "clone", bare, repo);
        Setup(repo, "remote", "set-url", "origin", Origin);
        var receiver = Path.Combine(_root, "missing.git");
        if (scheme == "file") receiver = new Uri(receiver).AbsoluteUri;
        if (scheme == "ssh")
        {
            receiver = "ssh://git@github.com/org/repo.git";
            var ssh = Path.Combine(_root, "refuse-ssh.sh");
            WriteExecutable(ssh, "#!/bin/sh\nprintf 'ssh_credential_present=[%s]\\n' \"${" + CredentialName + "+yes}\"\nexit 1\n");
            Set("GIT_SSH_COMMAND", ssh);
        }
        Setup(repo, "config", "remote.origin.pushurl", receiver);
        using var transport = WorkerGitCredentialTransport.Create(Token, Origin);
        var ex = await Assert.ThrowsAsync<GitOperationException>(() => GitOperations.PushBranchAsync(repo, "main", Ct, transport));
        Assert.Contains("Failed to push", ex.Message);
        // Env availability in git is sanctioned; scheme transports must not receive token material.
        AssertNoSecrets(ex.ToString());
        AssertNoSecretsOnDisk();
    }

    [Fact]
    public async Task LocalAssignment_UnauthenticatedCloneAndPush_RemainUsable()
    {
        RequireLinux();
        var bare = CreateBare();
        InstallWrapper();
        var runner = new TaskExecutorTests.MockAgentRunner { PromptResponder = (_, dir, _) =>
        {
            File.WriteAllText(Path.Combine(dir, "local.txt"), "local-publication");
            Setup(dir, "add", "local.txt");
            Setup(dir, "commit", "-m", "local publication");
            return Task.FromResult("done");
        } };
        var task = TaskFor(Token) with { Repositories = [new TargetRepository { Name = "target", Url = bare }] };
        var result = await new TaskExecutor(runner).ExecuteAsync(task, Ct);
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(result.GitStatus!.Pushed, result.Output);
        Assert.Equal("local-publication", Raw(bare, "show", "feature:local.txt").Stdout.Trim());
        Assert.All(LaunchBlocks(), b => { Assert.Contains("askpass=[]", b); Assert.Contains("credential_present=[]", b); });
        AssertNoSecretsOnDisk();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task Executor_HelperScope_CleansUpOnEveryTerminalOutcome(string outcome)
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token) { RefuseAll = outcome == "failure", HoldFirst = outcome == "cancel" };
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var execution = new TaskExecutor(new CallbackAgentRunner()).ExecuteAsync(TaskFor(Token) with { BranchInfo = null }, cts.Token);
        try
        {
            if (outcome == "cancel")
            {
                await server.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                Assert.False(execution.IsCompleted);
                await cts.CancelAsync();
                server.Release.TrySetResult();
            }
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(outcome switch { "success" => TaskOutcome.Completed, "failure" => TaskOutcome.Failed, "cancel" => TaskOutcome.Cancelled, _ => throw new InvalidOperationException() }, result.Status);
            var clone = Assert.Single(LaunchBlocks(), b => b.StartsWith("clone\n"));
            var path = Value(clone, "askpass");
            Assert.NotEmpty(path);
            _helperDirs.Add(Path.GetDirectoryName(path)!);
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)), "Executor helper survived terminal result.");
            AssertNoSecrets(result.Output, string.Join('\n', result.Metrics!.Issues));
            AssertNoSecretsOnDisk();
        }
        finally
        {
            await cts.CancelAsync();
            server.Release.TrySetResult();
#pragma warning disable xUnit1051 // Cleanup must join even when the test token has been cancelled.
            await execution.WaitAsync(TimeSpan.FromSeconds(30));
#pragma warning restore xUnit1051
        }
    }

    [Fact]
    public async Task Executor_CoderHandoff_FinalCommitReachableFromFreshCloneAndFetchAndHelperRemoved()
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token);
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        var runner = new TaskExecutorTests.MockAgentRunner
        {
            PromptResponder = (_, dir, _) =>
            {
                // Two commits: the handoff must carry the FINAL commit, not merely the first one.
                for (var i = 1; i <= 2; ++i)
                {
                    File.WriteAllText(Path.Combine(dir, "handoff.txt"), "handoff-" + i);
                    Setup(dir, "add", "handoff.txt");
                    Setup(dir, "commit", "-m", "handoff " + i);
                }
                return Task.FromResult("committed handoff");
            },
        };
        var result = await new TaskExecutor(runner).ExecuteAsync(TaskFor(Token), Ct);

        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.True(result.GitStatus!.Pushed, result.Output);
        var finalHead = Raw(Path.Combine(_root, "target"), "rev-parse", "HEAD").Stdout.Trim();
        Assert.Equal("handoff 2", Raw(Path.Combine(_root, "target"), "log", "-1", "--format=%s").Stdout.Trim());

        // Fresh clone of the feature branch — what the Testing phase receives.
        var fresh = Path.Combine(_root, "fresh-clone");
        Setup(_root, "clone", "--branch", "feature", bare, fresh);
        Assert.Equal(finalHead, Raw(fresh, "rev-parse", "HEAD").Stdout.Trim());
        Assert.Equal("handoff-2", File.ReadAllText(Path.Combine(fresh, "handoff.txt")));

        // Fresh fetch into an empty repository resolves the same commit.
        var fetched = Path.Combine(_root, "fresh-fetch");
        Directory.CreateDirectory(fetched);
        Setup(fetched, "init", "-b", "scratch");
        Setup(fetched, "fetch", bare, "refs/heads/feature");
        Assert.Equal(finalHead, Raw(fetched, "rev-parse", "FETCH_HEAD").Stdout.Trim());

        var push = Assert.Single(LaunchBlocks(), b => b.StartsWith("push\n"));
        var helper = Value(push, "askpass");
        Assert.NotEmpty(helper);
        _helperDirs.Add(Path.GetDirectoryName(helper)!);
        Assert.False(Directory.Exists(Path.GetDirectoryName(helper)), "Helper directory survived the handoff.");
        AssertHelpersGone();
        AssertNoSecrets(result.Output, string.Join('\n', result.Metrics!.Issues));
        AssertNoSecretsOnDisk();
    }

    [Fact]
    public async Task Executor_PushAcceptedButBranchNotOnRemote_FailsBeforeTestingAndRemovesHelper()
    {
        RequireLinux();
        var bare = CreateBare();
        // The server ACCEPTS the push (exit 0) but the branch never persists: only the remote
        // verification can catch this, and it must fail the task instead of reporting success.
        WriteExecutable(Path.Combine(bare, "hooks", "post-receive"),
            "#!/bin/sh\nwhile read old new ref; do git update-ref -d \"$ref\"; done\n");
        await using var server = new GitProxy(bare, Token);
        InstallNetworkEnvironment(server.Port);
        InstallWrapper();
        var runner = new TaskExecutorTests.MockAgentRunner
        {
            PromptResponder = (_, dir, _) =>
            {
                File.WriteAllText(Path.Combine(dir, "lost.txt"), "lost");
                Setup(dir, "add", "lost.txt");
                Setup(dir, "commit", "-m", "lost");
                return Task.FromResult("committed");
            },
            WorkerReportToReturn = new WorkerReport { TaskVerdict = TaskVerdict.Pass, Summary = "done", Issues = [] },
        };
        var result = await new TaskExecutor(runner).ExecuteAsync(TaskFor(Token), Ct);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.False(result.GitStatus!.Pushed);
        Assert.Contains(result.Metrics.Issues, i => i.Contains("Push verification failed") && i.Contains("remote head is missing"));
        Assert.NotEqual(0, Raw(bare, "rev-parse", "--verify", "refs/heads/feature").ExitCode);
        AssertHelpersGone();
        AssertNoSecrets(result.Output, string.Join('\n', result.Metrics.Issues));
        AssertNoSecretsOnDisk();
    }

    [Fact]
    public async Task Script_BuiltFromCrlfTemplate_IsLfAndExecutable()
    {
        RequireLinux();
        var crlf = WorkerGitAskpassHelper.ScriptTemplate.ReplaceLineEndings("\r\n");
        var content = WorkerGitAskpassHelper.BuildScriptContent(crlf, CredentialName);
        Assert.DoesNotContain('\r', content);
        Assert.StartsWith("#!/bin/sh\n", content);

        var script = Path.Combine(_root, "crlf-askpass.sh");
        WriteExecutable(script, content);
        using var p = new Process { StartInfo = new ProcessStartInfo(script) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        p.StartInfo.ArgumentList.Add("Password for 'https://x-access-token@github.com': ");
        p.StartInfo.Environment[CredentialName] = Token;
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(Token, stdout);
    }

    [Fact]
    public async Task ConfigSeam_ServiceWrittenHelper_AuthenticatesRealCloneFetchPushAndSuppressesInheritedHelpers()
    {
        RequireLinux();
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token);
        InstallNetworkEnvironment(server.Port);
        var prompts = Path.Combine(_root, "config-real-prompts.txt");
        InstallWrapper(WorkerGitAskpassHelper.ConfigRepoCredentialEnvName, prompts);
        var recorder = Path.Combine(_root, "record-helper.sh");
        var record = Path.Combine(_root, "helper-calls.txt");
        // Recorder observes methods only; even its positive controls never persist token values.
        WriteExecutable(recorder, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> " + ShellQuote(record) + "\ncat >/dev/null\n");
        GlobalContent += "[credential]\n\thelper = " + recorder + "\n";
        File.WriteAllText(_global, GlobalContent);
        Set("GIT_ASKPASS", "/inherited/helper-marker");
        Set("SSH_ASKPASS", "/inherited/ssh-marker");
        Set("GIT_CONFIG_PARAMETERS", "'credential.helper=" + recorder + "'");
        var repo = Path.Combine(_root, "config-target");
        using var service = new WorkerService("http://localhost:9999", "config-transport", ["coder"], repo);
        using var seam = CreateServiceSeam(service, out var script);
        var clone = await seam.CloneAsync(repo, Ct);
        Assert.True(clone.Success, clone.SanitizedError);
        Assert.Equal("fixture payload", File.ReadAllText(Path.Combine(repo, "seed.txt")));
        var fetch = await seam.RunConfigRepoCommandAsync(["fetch", "origin", "main"], repo, Ct);
        Assert.True(fetch.Success, fetch.SanitizedError);
        var push = await seam.RunConfigRepoCommandAsync(["push", "origin", "main"], repo, Ct);
        Assert.True(push.Success, push.SanitizedError);
        foreach (var verb in new[] { "clone", "fetch", "push" })
        {
            var block = Assert.Single(LaunchBlocks(), b => b.StartsWith(verb + "\n"));
            AssertProbe(block, script, Token, WorkerGitAskpassHelper.ConfigRepoCredentialEnvName);
        }
        Assert.False(File.Exists(record), "Inherited helper get/store was invoked.");
        Assert.Equal(Origin, Raw(repo, "config", "--get", "remote.origin.url").Stdout.Trim());
        AssertNoSecretsOnDisk();
        // Exactly the actual git-produced prompts, including trailing spaces, in the service's
        // final launch environment. The forwarder logs prompt bytes then execs the service file.
        var expectedPair = "Username for 'https://github.com': \nPassword for 'https://x-access-token@github.com': \n";
        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(expectedPair, 3))), File.ReadAllBytes(prompts));

        // Independent get/store controls with the same recorder/global config and helper: restore
        // only the inherited PARAMETERS helper entry after both operation resets.
        var control = new Dictionary<string, string?>(WorkerGitAskpassHelper.BuildCredentialChildEnvironment(
            script, WorkerGitAskpassHelper.ConfigRepoCredentialEnvName, Token))
        { ["GIT_CONFIG_PARAMETERS"] = "'credential.helper=" + recorder + "'" };
        var fill = await CredentialFill(control, "protocol=https\nhost=github.com\n\n");
        Assert.Equal(0, fill.ExitCode);
        Assert.Contains("password=" + Token, fill.Stdout);
        Assert.Contains("get", File.ReadAllLines(record));
        var approve = await RunWithInput(control, "protocol=https\nhost=github.com\nusername=x-access-token\npassword=" + Token + "\n\n", "credential", "approve");
        Assert.Equal(0, approve.ExitCode);
        Assert.Contains("store", File.ReadAllLines(record));
        AssertNoSecrets(fill.Stderr, approve.Stdout, approve.Stderr);
        seam.Dispose();
        Assert.False(File.Exists(script));
    }

    [Theory]
    [InlineData("https", "github.com", null, true)]
    [InlineData("https", "github.com:443", "x-access-token", true)]
    [InlineData("https", "gitlab.com", null, false)]
    [InlineData("https", "github.com:8443", null, false)]
    [InlineData("http", "github.com", null, false)]
    [InlineData("ssh", "github.com", null, false)]
    [InlineData("file", "github.com", null, false)]
    [InlineData("https", "github.com", "test'quote", false)]
    [InlineData("https", "github.com", "test/user", false)]
    [InlineData("https", "github.com", "test@user", false)]
    public async Task ConfigServiceScript_GitGeneratedPromptBytes_ReleaseOnlyForEligibleUnambiguousDestination(
        string protocol, string host, string? username, bool allowed)
    {
        RequireLinux();
        using var service = new WorkerService("http://localhost:9999", "config-prompts", ["coder"], _root);
        using var seam = CreateServiceSeam(service, out var script);
        var log = Path.Combine(_root, "prompts.txt");
        var forwarder = Path.Combine(_root, "forward-script.sh");
        WriteExecutable(forwarder, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> " + ShellQuote(log) + "\nexec " + ShellQuote(script) + " \"$@\"\n");
        var env = new Dictionary<string, string?>(WorkerGitAskpassHelper.BuildCredentialChildEnvironment(
            forwarder, WorkerGitAskpassHelper.ConfigRepoCredentialEnvName, Token));
        env["GIT_CONFIG_PARAMETERS"] += " 'credential.sanitizePrompt=false'";
        var input = "protocol=" + protocol + "\nhost=" + host + "\n" + (username is null ? "" : "username=" + username + "\n") + "\n";
        var result = await CredentialFill(env, input);
        var actualPrompts = File.ReadAllBytes(log);
        var expected = allowed && username is null
            ? "Username for '" + protocol + "://" + host + "': \nPassword for '" + protocol + "://x-access-token@" + host + "': \n"
            : username is null ? "Username for '" + protocol + "://" + host + "': \n"
            : "Password for '" + protocol + "://" + username + "@" + host + "': \n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), actualPrompts);
        AssertNoSecrets(Encoding.UTF8.GetString(actualPrompts), File.ReadAllText(script), File.ReadAllText(forwarder), result.Stderr);
        if (allowed) { Assert.Equal(0, result.ExitCode); Assert.Contains("password=" + Token, result.Stdout); }
        else { Assert.NotEqual(0, result.ExitCode); Assert.Equal("", result.Stdout); }
        seam.Dispose();
        Assert.False(File.Exists(script));
    }

    // core.askPass is a separate ambient credential source from credential.helper. These
    // additions leave all 30 approved rows and their fixture helpers unchanged.
    private const string AmbientAskPassCredential = "worker-core-askpass-ambient-secret";
    private const string AmbientAskPassEnv = "TRANSPORT_TEST_AMBIENT_ASKPASS_CREDENTIAL";

    [Theory]
    [InlineData("global", "https://gitlab.com/org/repo.git")]
    [InlineData("global", "https://github.com:8443/org/repo.git")]
    [InlineData("indexed", "https://gitlab.com/org/repo.git")]
    [InlineData("indexed", "https://github.com:8443/org/repo.git")]
    [InlineData("parameters", "https://gitlab.com/org/repo.git")]
    [InlineData("parameters", "https://github.com:8443/org/repo.git")]
    public async Task Executor_InitiallyNoneligible_CoreAskPassNeverInvokedOrReleased(string channel, string url)
    {
        var bare = CreateBare();
        await using var server = new GitProxy(bare, AmbientAskPassCredential) { RefuseAll = true };
        InstallNetworkEnvironment(server.Port);
        InstallCoreAskPassWrapper();
        var (recorder, calls) = InstallAmbientCoreAskPass(channel);
        var operations = new RecordingOperations();
        var runner = new CallbackAgentRunner();
        var result = await new TaskExecutor(runner, gitOperations: operations).ExecuteAsync(
            TaskFor(Token) with
            {
                BranchInfo = null,
                Repositories = [new TargetRepository { Name = "target", Url = url }],
            }, Ct);

        Assert.Equal(TaskOutcome.Failed, result.Status);
        Assert.Equal("FAIL", result.Metrics!.Verdict);
        Assert.Equal(0, runner.PromptCount);
        Assert.Equal(url, Assert.Single(operations.CloneUrls));
        Assert.Null(Assert.Single(operations.CloneTransports));
        Assert.Contains(nameof(GitOperationException), result.Output);
        var clone = Assert.Single(LaunchBlocks(), b => b.StartsWith("clone\n"));
        Assert.Equal("", Value(clone, "askpass"));
        Assert.Equal("", Value(clone, "credential_present"));
        Assert.Equal("", Value(clone, "ssh"));
        Assert.Equal("", Value(clone, "interactive"));
        var attempts = server.Snapshot();
        Assert.NotEmpty(attempts); // an actual 401 challenge, not a pre-network refusal
        Assert.All(attempts, r =>
        {
            Assert.Equal(new Uri(url).Authority, r.Host);
            Assert.Equal(new Uri(url).Host + ":" + new Uri(url).Port, r.Destination);
            Assert.Contains("service=git-upload-pack", r.Path);
        });
        AssertCoreAskPassSecretsAbsent(clone, result.Output, string.Join('\n', result.Metrics.Issues));
        AssertNoSecretsOnDisk();
        AssertCoreAskPassFilesContainNoSecrets();
        Assert.Equal(AmbientAskPassCredential, Environment.GetEnvironmentVariable(AmbientAskPassEnv));
        // Both observations independently discriminate the leak: calling the ambient program is
        // forbidden even if git fails later; a credential on the wire is forbidden in its own right.
        Assert.Multiple(
            () => Assert.Empty(File.Exists(calls) ? File.ReadAllLines(calls) : []),
            () => Assert.True(attempts.All(r => r.Authorization is null), "A noneligible destination received Authorization from ambient core.askPass."),
            () => Assert.True(attempts.All(r => r.Password is null), "A noneligible destination received an ambient password."),
            () => Assert.Equal("", Value(clone, "core_askpass")),
            () => Assert.DoesNotContain(recorder, Value(clone, "parameters")));
    }

    /// <summary>
    /// Same real git clone and challenged destination, but a deliberately pre-fix child envelope.
    /// Proves both ambient prompt invocations and actual credential release; the server still
    /// returns 401 so the control cannot pass through an unauthenticated/no-op success.
    /// </summary>
    [Theory]
    [InlineData("global", "https://gitlab.com/org/repo.git")]
    [InlineData("global", "https://github.com:8443/org/repo.git")]
    [InlineData("indexed", "https://gitlab.com/org/repo.git")]
    [InlineData("indexed", "https://github.com:8443/org/repo.git")]
    [InlineData("parameters", "https://gitlab.com/org/repo.git")]
    [InlineData("parameters", "https://github.com:8443/org/repo.git")]
    public async Task CoreAskPass_AmbientControl_RealCloneReleasesWithoutSuppression(string channel, string url)
    {
        var bare = CreateBare();
        await using var server = new GitProxy(bare, AmbientAskPassCredential) { RefuseAll = true };
        InstallNetworkEnvironment(server.Port);
        InstallCoreAskPassWrapper();
        var (recorder, calls) = InstallAmbientCoreAskPass(channel);
        var envelope = WithoutCoreAskPassReset(WorkerGitAskpassHelper.BuildCredentialFreeChildEnvironment());
        if (channel == "parameters")
        {
            // PARAMETERS-only source is normally removed by whole-channel replacement, not by
            // core.askPass=. Restore just that test source for its independent reachability control.
            envelope["GIT_CONFIG_PARAMETERS"] = "'credential.helper=' 'core.askPass=" + recorder + "'";
        }
        var error = await Assert.ThrowsAsync<GitOperationException>(() =>
            new DefaultGitOperations().CloneRepositoryAsync(url, Path.Combine(_root, "control"), Ct,
                new CoreAskPassControlTransport(url, envelope)));
        Assert.Contains("Failed to clone", error.Message);
        var clone = Assert.Single(LaunchBlocks(), b => b.StartsWith("clone\n"));
        Assert.Equal(recorder, Value(clone, "core_askpass"));
        Assert.Equal("", Value(clone, "askpass"));
        Assert.Equal("", Value(clone, "credential_present"));
        Assert.Equal("", Value(clone, "interactive"));
        Assert.True(File.Exists(calls), "The ambient askpass recorder was never invoked.");
        var host = new Uri(url).Authority;
        Assert.Equal(new[]
        {
            "Username for 'https://" + host + "': ",
            "Password for 'https://ambient-user@" + host + "': ",
        }, File.ReadAllLines(calls));
        var attempts = server.Snapshot();
        Assert.All(attempts, r => Assert.Equal(host, r.Host));
        Assert.Contains(attempts, r => r.Authorization is null);
        Assert.True(attempts.Any(r => r.Password == AmbientAskPassCredential && r.Destination == new Uri(url).Host + ":" + new Uri(url).Port),
            "The ambient control must release its credential to the actual challenged destination.");
        Assert.DoesNotContain(attempts, r => r.Password == Token || r.Password == OtherToken);
        AssertCoreAskPassSecretsAbsent(clone, error.ToString(), File.ReadAllText(calls));
        AssertNoSecretsOnDisk();
        AssertCoreAskPassFilesContainNoSecrets();
    }

    [Theory]
    [InlineData("global")]
    [InlineData("indexed")]
    [InlineData("parameters")]
    public async Task Executor_AuthenticatedCoreAskPassControl_ReleasesOnlyAssignmentCredential(string channel)
    {
        var bare = CreateBare();
        await using var server = new GitProxy(bare, Token, AmbientAskPassCredential);
        InstallNetworkEnvironment(server.Port);
        InstallCoreAskPassWrapper();
        var (recorder, calls) = InstallAmbientCoreAskPass(channel);
        var operations = new RecordingOperations();
        var runner = new CallbackAgentRunner();
        var result = await new TaskExecutor(runner, gitOperations: operations).ExecuteAsync(
            TaskFor(Token) with { BranchInfo = null }, Ct);
        Assert.Equal(TaskOutcome.Completed, result.Status);
        Assert.Equal(1, runner.PromptCount);
        Assert.Equal("fixture payload", File.ReadAllText(Path.Combine(_root, "target", "seed.txt")));
        Assert.Equal(Origin, Assert.Single(operations.CloneUrls));
        Assert.NotNull(Assert.Single(operations.CloneTransports));
        var clone = Assert.Single(LaunchBlocks(), b => b.StartsWith("clone\n"));
        AssertProbe(clone, Assert.Single(operations.HelperPaths), Token);
        // Authenticated indexed/global core.askPass remains configured: GIT_ASKPASS overrides it.
        // PARAMETERS is replaced on both paths, so only that source disappears in this control.
        Assert.Equal(channel == "parameters" ? "" : recorder, Value(clone, "core_askpass"));
        Assert.DoesNotContain("'core.askPass='", Value(clone, "parameters"));
        var attempts = server.Snapshot();
        Assert.Contains(attempts, r => r.Authorization is null);
        Assert.Contains(attempts, r => r.Password == Token && r.Destination == "github.com:443");
        Assert.All(attempts, r =>
        {
            Assert.Equal("github.com", r.Host);
            Assert.True(r.Password is null || r.Password == Token, "Only the assignment credential may be released.");
        });
        Assert.False(File.Exists(calls), "Authenticated git invoked ambient core.askPass instead of GIT_ASKPASS.");
        Assert.Equal(Origin, Raw(Path.Combine(_root, "target"), "config", "--get", "remote.origin.url").Stdout.Trim());
        AssertHelpersGone();
        AssertCoreAskPassSecretsAbsent(clone, result.Output, string.Join('\n', result.Metrics!.Issues));
        AssertNoSecretsOnDisk();
        AssertCoreAskPassFilesContainNoSecrets();
    }

    private (string Recorder, string Calls) InstallAmbientCoreAskPass(string channel)
    {
        var recorder = Path.Combine(_root, "ambient-core-askpass.sh");
        var calls = Path.Combine(_root, "ambient-core-askpass-calls.txt");
        // Token-free program and evidence: the ambient fixture credential lives in the test's
        // inherited env only. Prompt bytes are recorded, but neither the answer nor stdin is logged.
        WriteExecutable(recorder, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> " + ShellQuote(calls) +
            "\ncase \"$1\" in *sername*) printf '%s' 'ambient-user' ;; *) printf '%s' \"$" + AmbientAskPassEnv + "\" ;; esac\n");
        Set(AmbientAskPassEnv, AmbientAskPassCredential);
        switch (channel)
        {
            case "global":
                GlobalContent += "[core]\n\taskPass = " + recorder + "\n";
                File.WriteAllText(_global, GlobalContent);
                break;
            case "indexed":
                // The four existing entries are the fixture's network transport configuration.
                Assert.Equal("4", Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));
                Set("GIT_CONFIG_COUNT", "5");
                Set("GIT_CONFIG_KEY_4", "core.askPass"); Set("GIT_CONFIG_VALUE_4", recorder);
                break;
            case "parameters":
                Set("GIT_CONFIG_PARAMETERS", "'core.askPass=" + recorder + "'");
                break;
            default: throw new InvalidOperationException("Unknown ambient askpass channel.");
        }
        // Independent source premises: no row can silently rely on a different configured channel.
        if (channel != "global") Assert.DoesNotContain("askPass", File.ReadAllText(_global));
        if (channel != "indexed") Assert.Equal("4", Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));
        if (channel != "parameters") Assert.Null(Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS"));
        return (recorder, calls);
    }

    private void InstallCoreAskPassWrapper()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        // Preserve the approved probe. Add an independent read of git's effective core.askPass,
        // inside the real alias child after git has applied BOTH environment precedence channels.
        var probe = Probe + "; printf 'core_askpass=['; git config --get core.askPass; printf ']\\n'";
        WriteExecutable(Path.Combine(bin, "git"), "#!/bin/sh\ncase \"$1\" in clone|fetch|push)\n" +
            "{ printf '===CALL===\\n%s\\n' \"$1\"; printf 'argv=[%s]\\n' \"$@\"; " +
            ShellQuote(_realGit) + " -c " + ShellQuote("alias.hiveprobe=" + probe) + " hiveprobe; } >> " + ShellQuote(_launchLog) +
            "\n;; esac\nexec " + ShellQuote(_realGit) + " \"$@\"\n");
        Set("PATH", bin + ":/usr/bin:/bin");
    }

    private static Dictionary<string, string?> WithoutCoreAskPassReset(IReadOnlyDictionary<string, string?> produced)
    {
        var env = new Dictionary<string, string?>(produced);
        var count = int.Parse(env["GIT_CONFIG_COUNT"]!);
        var entries = Enumerable.Range(0, count)
            .Select(i => (Key: env["GIT_CONFIG_KEY_" + i], Value: env["GIT_CONFIG_VALUE_" + i]))
            .Where(e => !(string.Equals(e.Key, "core.askPass", StringComparison.OrdinalIgnoreCase) && e.Value == ""))
            .ToList();
        for (var i = 0; i < count; ++i) { env.Remove("GIT_CONFIG_KEY_" + i); env.Remove("GIT_CONFIG_VALUE_" + i); }
        for (var i = 0; i < entries.Count; ++i)
        {
            env["GIT_CONFIG_KEY_" + i] = entries[i].Key;
            env["GIT_CONFIG_VALUE_" + i] = entries[i].Value;
        }
        env["GIT_CONFIG_COUNT"] = entries.Count.ToString();
        env["GIT_CONFIG_PARAMETERS"] = "'credential.helper='";
        return env;
    }

    /// <summary>Control only: a fixed pre-fix envelope, never a production credential transport.</summary>
    private sealed class CoreAskPassControlTransport(string origin, IReadOnlyDictionary<string, string?> environment) : IWorkerGitCredentialTransport
    {
        public string SanitizedOriginUrl => origin;
        public IReadOnlyDictionary<string, string?> BuildChildEnvironment() => environment;
    }

    private static void AssertCoreAskPassSecretsAbsent(params string[] values)
    {
        AssertNoSecrets(values);
        foreach (var value in values)
        {
            Assert.DoesNotContain(AmbientAskPassCredential, value);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("ambient-user:" + AmbientAskPassCredential)), value);
        }
    }

    private void AssertCoreAskPassFilesContainNoSecrets()
    {
        AssertCoreAskPassSecretsAbsent(_output.ToString());
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            AssertCoreAskPassSecretsAbsent(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    private ConfigRepoGitOperations CreateServiceSeam(WorkerService service, out string script)
    {
        string? dir = null;
        service.AskpassDirCreate = path => { dir = path; Directory.CreateDirectory(path); };
        var environment = new Dictionary<string, string?>();
        var provisioner = new WorkerConfigProvisioner("cfg-test", (_, _) => throw new InvalidOperationException("No RPC expected"),
            key => environment.GetValueOrDefault(key), (key, value) => environment[key] = value);
        provisioner.Apply(new CopilotHive.Shared.Grpc.GetWorkerConfigResponse { GithubToken = Token, ConfigRepoUrl = Origin });
        // The actual service factory owns script creation and seam cleanup; reflection is only
        // entry into that existing boundary, never a replacement implementation.
        var method = typeof(WorkerService).GetMethod("CreateConfigRepoSeam", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var seam = (ConfigRepoGitOperations)method.Invoke(service, [provisioner])!;
        script = Path.Combine(dir!, "askpass.sh");
        _helperDirs.Add(dir!);
        Assert.True(File.Exists(script));
        // Existing script parity tests pin template equality. These rows intentionally assert
        // behavior of the actual service-created bytes instead (so the legacy-script mutant must
        // reach git's real prompt/refusal assertion, not fail a template self-comparison first).
        AssertNoSecrets(File.ReadAllText(script));
        return seam;
    }

    private Task<GitProcessResult> CredentialFill(IReadOnlyDictionary<string, string?> env, string input) => RunWithInput(env, input, "credential", "fill");
    private async Task<GitProcessResult> RunWithInput(IReadOnlyDictionary<string, string?> env, string input, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(_realGit)
        { WorkingDirectory = _root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.Environment.Clear();
        foreach (var (key, value) in env) if (value is not null) process.StartInfo.Environment[key] = value;
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), Ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            return new GitProcessResult(process.ExitCode,
                await output.WaitAsync(TimeSpan.FromSeconds(10), Ct),
                await error.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
#pragma warning disable xUnit1051 // teardown drains independent of cancelled test token
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1051
        }
    }

    private static string Probe => ProbeFor(CredentialName);
    private static string ProbeFor(string credentialName) => ProbeTemplate.Replace(CredentialName, credentialName, StringComparison.Ordinal);
    private static string ProbeTemplate =>
        "!printf 'askpass=[%s]\\ncredential_present=[%s]\\nssh=[%s]\\ngh=[%s]\\ngithub=[%s]\\nparameters=[%s]\\n' " +
        "\"$GIT_ASKPASS\" \"${" + CredentialName + "+yes}\" \"$SSH_ASKPASS\" \"$GH_TOKEN\" \"$GITHUB_TOKEN\" \"$GIT_CONFIG_PARAMETERS\"; " +
        "printf 'credential_digest=['; printf '%s' \"$" + CredentialName + "\" | sha256sum | cut -d' ' -f1; printf ']\\n'; " +
        "if [ -n \"$GIT_ASKPASS\" ]; then printf 'script_begin\\n'; cat \"$GIT_ASKPASS\"; printf 'script_end\\n'; " +
        "printf 'answer_digest=['; \"$GIT_ASKPASS\" \"Password for 'https://x-access-token@github.com:443': \" | sha256sum | cut -d' ' -f1; printf ']\\n'; fi; " +
        "printf 'interactive=['; git config --get credential.interactive; printf ']\\n'; " +
        "printf 'origin_at_launch=['; git config --get remote.origin.url; printf ']\\n'";

    private void InstallWrapper(string credentialName = CredentialName, string? promptLog = null)
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var forward = "";
        if (promptLog is not null)
        {
            var forwarder = Path.Combine(_root, "forward-askpass.sh");
            WriteExecutable(forwarder, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> " + ShellQuote(promptLog) + "\nexec \"$TRANSPORT_REAL_ASKPASS\" \"$@\"\n");
            forward = "if [ -n \"$GIT_ASKPASS\" ]; then export TRANSPORT_REAL_ASKPASS=\"$GIT_ASKPASS\"; export GIT_ASKPASS=" + ShellQuote(forwarder) + "; fi\n";
        }
        var script = "#!/bin/sh\ncase \"$1\" in clone|fetch|push)\n" +
            "{ printf '===CALL===\\n%s\\n' \"$1\"; printf 'argv=[%s]\\n' \"$@\"; " +
            "'" + _realGit + "' -c " + ShellQuote("alias.hiveprobe=" + ProbeFor(credentialName)) + " hiveprobe; } >> '" + _launchLog + "'\n" + forward + ";; esac\nexec '" + _realGit + "' \"$@\"\n";
        WriteExecutable(Path.Combine(bin, "git"), script);
        Set("PATH", bin + ":/usr/bin:/bin");
    }

    private void InstallNetworkEnvironment(int port)
    {
        var entries = new[] { ("http.proxy", $"http://127.0.0.1:{port}"), ("http.sslVerify", "false"), ("http.version", "HTTP/1.1"), ("http.postBuffer", "10485760") };
        Set("GIT_CONFIG_COUNT", entries.Length.ToString());
        for (var i = 0; i < entries.Length; ++i) { Set("GIT_CONFIG_KEY_" + i, entries[i].Item1); Set("GIT_CONFIG_VALUE_" + i, entries[i].Item2); }
    }

    private string CreateBare(bool includeRelease = true)
    {
        var seed = Path.Combine(_root, "seed");
        var bare = Path.Combine(_root, "remote", "org", "repo.git");
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        Setup(seed, "init", "-b", "main");
        Setup(seed, "config", "user.name", "Transport");
        Setup(seed, "config", "user.email", "transport@test.invalid");
        File.WriteAllText(Path.Combine(seed, "seed.txt"), "fixture payload");
        Setup(seed, "add", "seed.txt"); Setup(seed, "commit", "-m", "seed");
        if (includeRelease)
        {
            Setup(seed, "checkout", "-b", "release");
            File.WriteAllText(Path.Combine(seed, "release.txt"), "remote-only-release");
            Setup(seed, "add", "release.txt"); Setup(seed, "commit", "-m", "remote release");
            Setup(seed, "checkout", "main");
        }
        Setup(_root, "clone", "--bare", seed, bare);
        Setup(bare, "config", "http.receivepack", "true");
        return bare;
    }

    private static WorkTask TaskFor(string token) => new()
    {
        TaskId = "transport-task", GoalId = "transport-goal", GoalDescription = "transport", Prompt = "work", Role = WorkerRole.Coder,
        Repositories = [new TargetRepository { Name = "target", Url = TokenUrl(token), DefaultBranch = "main" }],
        BranchInfo = new BranchSpec { Action = BranchAction.Create, BaseBranch = "main", FeatureBranch = "feature" },
    };
    private static string TokenUrl(string token) => $"https://x-access-token:{token}@github.com/org/repo.git";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static string FindGit() => Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "git")).First(File.Exists);
    private static void RequireLinux() { if (!OperatingSystem.IsLinux()) Assert.Skip("POSIX git and CONNECT fixture."); }
    private static void WriteExecutable(string path, string text)
    {
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private void Set(string key, string? value)
    {
        if (!_old.ContainsKey(key)) _old[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }
    private void Setup(string dir, params string[] args)
    {
        var result = Raw(dir, args);
        Assert.True(result.ExitCode == 0, "Fixture git failed: " + GitUrlRedactor.Redact(result.Stderr));
    }
    private GitProcessResult Raw(string dir, params string[] args)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(_realGit) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        try { p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
        finally
        {
            if (!p.HasExited) p.Kill(true);
            p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        return new GitProcessResult(p.ExitCode,
            stdout.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(),
            stderr.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    }
    private List<string> LaunchBlocks() => File.ReadAllText(_launchLog).Split("===CALL===\n", StringSplitOptions.RemoveEmptyEntries).ToList();
    private static string Value(string block, string key)
    {
        var start = block.LastIndexOf("\n" + key + "=[", StringComparison.Ordinal);
        if (start >= 0) start++;
        else if (block.StartsWith(key + "=[", StringComparison.Ordinal)) start = 0;
        Assert.True(start >= 0, "Missing probe field " + key);
        start += key.Length + 2;
        if (key == "parameters")
        {
            var end = block.IndexOf('\n', start);
            return block[start..(end - 1)]; // final delimiter, not a ] inside the exported alias
        }
        return block[start..block.IndexOf(']', start)].Trim();
    }
    private void AssertProbe(string block, string helper, string token, string credentialName = CredentialName)
    {
        _helperDirs.Add(Path.GetDirectoryName(helper)!);
        Assert.Equal(helper, Value(block, "askpass"));
        Assert.Equal("yes", Value(block, "credential_present"));
        Assert.Equal(Hash(token), Value(block, "credential_digest"));
        Assert.Equal(Hash(token), Value(block, "answer_digest"));
        Assert.Equal("", Value(block, "ssh")); Assert.Equal("", Value(block, "gh")); Assert.Equal("", Value(block, "github"));
        // git exports the -c alias itself in PARAMETERS after the production reset.
        Assert.StartsWith("'credential.helper='", Value(block, "parameters"));
        Assert.DoesNotContain("credential.helper=store", Value(block, "parameters"));
        Assert.Equal("", Value(block, "interactive"));
        Assert.Contains("script_begin\n#!/bin/sh\n", block);
        Assert.Contains("${" + credentialName + "}", block);
        AssertNoSecrets(block);
    }
    private void AssertHelpersGone()
    {
        foreach (var b in LaunchBlocks())
        {
            var path = Value(b, "askpass");
            if (path.Length == 0) continue;
            _helperDirs.Add(Path.GetDirectoryName(path)!);
            Assert.False(File.Exists(path), "Helper survived assignment.");
        }
    }
    private static void AssertNoSecrets(params string[] values) { foreach (var value in values) { Assert.DoesNotContain(Token, value); Assert.DoesNotContain(OtherToken, value); } }
    private void AssertNoSecretsOnDisk()
    {
        AssertNoSecrets(_output.ToString());
        Assert.Equal(GlobalContent, File.ReadAllText(_global));
        Assert.False(File.Exists(Path.Combine(_root, ".gitconfig")));
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            AssertNoSecrets(Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }
    public async ValueTask DisposeAsync()
    {
        Console.SetOut(_oldOut); Console.SetError(_oldErr);
        foreach (var (key, value) in _old) Environment.SetEnvironmentVariable(key, value);
        // Recover helper ownership from actual child evidence even if an earlier assertion failed
        // before AssertProbe registered the path. No assertions or token values in this parser.
        if (File.Exists(_launchLog))
            foreach (var line in File.ReadAllLines(_launchLog))
                if (line.StartsWith("askpass=[", StringComparison.Ordinal) && line.EndsWith(']'))
                {
                    var path = line[9..^1];
                    if (Path.IsPathFullyQualified(path)) _helperDirs.Add(Path.GetDirectoryName(path)!);
                }
        foreach (var dir in _helperDirs.Distinct()) if (Directory.Exists(dir)) await GitOperations.ForceDeleteDirectoryAsync(dir);
        await GitOperations.ForceDeleteDirectoryAsync(_root);
        _output.Dispose();
    }

    private sealed class RecordingOperations : IGitOperations
    {
        private readonly DefaultGitOperations _inner = new();
        internal List<string> CloneUrls { get; } = [];
        internal List<string> HelperPaths { get; } = [];
        internal List<IWorkerGitCredentialTransport?> CloneTransports { get; } = [];
        internal List<IWorkerGitCredentialTransport?> PushTransports { get; } = [];
        internal List<string> AllArguments { get; } = [];
        public Task CloneRepositoryAsync(string url, string dir, CancellationToken ct) => CloneRepositoryAsync(url, dir, ct, null);
        public Task CloneRepositoryAsync(string url, string dir, CancellationToken ct, IWorkerGitCredentialTransport? transport)
        {
            CloneUrls.Add(url); CloneTransports.Add(transport); AllArguments.Add(url);
            HelperPaths.Add(((WorkerGitCredentialTransport?)transport)?.ScriptPath ?? "");
            return _inner.CloneRepositoryAsync(url, dir, ct, transport);
        }
        public Task PushBranchAsync(string dir, string branch, CancellationToken ct) => PushBranchAsync(dir, branch, ct, null);
        public Task PushBranchAsync(string dir, string branch, CancellationToken ct, IWorkerGitCredentialTransport? transport)
        { PushTransports.Add(transport); AllArguments.Add(branch); return _inner.PushBranchAsync(dir, branch, ct, transport); }
        public Task CreateBranchAsync(string dir, string branch, string basis, CancellationToken ct) => CreateBranchAsync(dir, branch, basis, ct, null);
        public Task CreateBranchAsync(string dir, string branch, string basis, CancellationToken ct, IWorkerGitCredentialTransport? transport)
        { AllArguments.Add(branch + " " + basis); return _inner.CreateBranchAsync(dir, branch, basis, ct, transport); }
        public Task CheckoutBranchAsync(string dir, string branch, CancellationToken ct) => _inner.CheckoutBranchAsync(dir, branch, ct);
        public Task<GitChangeSummary> GetGitStatusAsync(string dir, string? basis, CancellationToken ct) => _inner.GetGitStatusAsync(dir, basis, ct);
        public Task<bool> HasUncommittedChangesAsync(string dir, CancellationToken ct) => _inner.HasUncommittedChangesAsync(dir, ct);
        public Task<string?> GetMergeBaseAsync(string dir, string basis, CancellationToken ct) => _inner.GetMergeBaseAsync(dir, basis, ct);
        public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(string dir, string args, CancellationToken ct)
        { AllArguments.Add(args); return _inner.RunGitCommandAsync(dir, args, ct); }
        public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5) => _inner.ForceDeleteDirectoryAsync(path, maxRetries);
    }

    private sealed record Request(string Host, string Path, string? Authorization, string? Password, string Destination);
    private sealed class GitProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _loop;
        private readonly List<Request> _requests = [];
        private readonly string _root;
        private readonly string[] _tokens;
        internal int Port { get; }
        internal bool RefuseAll { get; init; }
        internal bool HoldFirst { get; init; }
        internal string? RedirectTo { get; init; }
        internal TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<Request> Snapshot() { lock (_requests) return [.. _requests]; }
        internal GitProxy(string bare, params string[] tokens)
        {
            _root = Path.GetDirectoryName(Path.GetDirectoryName(bare))!;
            _tokens = tokens;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=github.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    var connect = await ReadHeaders(stream, _stop.Token);
                    Assert.StartsWith("CONNECT ", connect);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), _stop.Token);
                    using var tls = new SslStream(stream, true);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                    var headers = await ReadHeaders(tls, _stop.Token);
                    var lines = headers.Split("\r\n");
                    var first = lines[0].Split(' ');
                    var host = lines.Single(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
                    var auth = lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                    var password = auth?.StartsWith("Basic ", StringComparison.Ordinal) == true ? Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])).Split(':', 2)[1] : null;
                    lock (_requests) _requests.Add(new Request(host, first[1], auth, password, connect.Split(' ')[1]));
                    Arrived.TrySetResult();
                    if (HoldFirst) await Release.Task.WaitAsync(_stop.Token);
                    if (RedirectTo is not null && host == "github.com")
                    {
                        var suffix = first[1]["/org/repo.git".Length..];
                        await Respond(tls, "302 Found", [], "Location: " + RedirectTo + suffix + "\r\n");
                    }
                    else if (RefuseAll || password is null || !_tokens.Contains(password))
                        await Respond(tls, "401 Unauthorized", [], "WWW-Authenticate: Basic realm=\"fixture\"\r\n");
                    else
                    {
                        var lengthText = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                        var body = new byte[lengthText is null ? 0 : int.Parse(lengthText)];
                        await tls.ReadExactlyAsync(body, _stop.Token);
                        var result = await Backend(first[0], first[1], lines, body);
                        var split = result.AsSpan().IndexOf("\r\n\r\n"u8);
                        Assert.True(split >= 0, "Backend returned no CGI headers.");
                        var head = Encoding.ASCII.GetString(result, 0, split);
                        var status = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Status:"))?[7..] ?? "200 OK";
                        var contentType = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Type:")) ?? "Content-Type: application/octet-stream";
                        await Respond(tls, status, result[(split + 4)..], contentType + "\r\n");
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested || HoldFirst) { }
        }
        private async Task<byte[]> Backend(string method, string path, string[] headers, byte[] body)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(FindGit()) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            process.StartInfo.ArgumentList.Add("http-backend");
            process.StartInfo.Environment.Clear();
            process.StartInfo.Environment["PATH"] = "/usr/bin:/bin";
            process.StartInfo.Environment["GIT_PROJECT_ROOT"] = _root;
            process.StartInfo.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
            process.StartInfo.Environment["REQUEST_METHOD"] = method;
            process.StartInfo.Environment["PATH_INFO"] = path.Split('?')[0];
            process.StartInfo.Environment["QUERY_STRING"] = path.Contains('?') ? path.Split('?', 2)[1] : "";
            process.StartInfo.Environment["CONTENT_TYPE"] = headers.FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "";
            process.StartInfo.Environment["CONTENT_LENGTH"] = body.Length.ToString();
            process.Start();
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(body, _stop.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30), _stop.Token);
                await Task.WhenAll(copy, error).WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
                Assert.Equal(0, process.ExitCode);
                return output.ToArray();
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Task.WhenAll(copy, error).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        private async Task Respond(Stream stream, string status, byte[] body, string extra = "")
        {
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extra}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, _stop.Token); await stream.WriteAsync(body, _stop.Token);
        }
        private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
        {
            var text = new StringBuilder(); var one = new byte[1];
            while (text.Length < 65536)
            {
                if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Incomplete fixture headers.");
                text.Append((char)one[0]);
                if (text.Length >= 4 && text.ToString(text.Length - 4, 4) == "\r\n\r\n") return text.ToString();
            }
            throw new IOException("Fixture header limit exceeded.");
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); Release.TrySetResult(); _listener.Stop();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { _certificate.Dispose(); _stop.Dispose(); }
        }
    }
}
