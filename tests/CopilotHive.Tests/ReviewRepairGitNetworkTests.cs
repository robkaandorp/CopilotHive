using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CopilotHive.Configuration;
using CopilotHive.Git;

namespace CopilotHive.Tests;

/// <summary>
/// Real HTTPS git, not copied environments or a rewritten destination: a loopback CONNECT proxy
/// terminates test-only TLS while git's URL/challenge remains https://github.com (or gitlab.com).
/// All proxy/helper configuration is local to disposable fixture repos/child environments. No
/// user's global config is written. The server serves an actual bare repo's dumb-HTTP objects.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ReviewRepairGitNetworkTests : IDisposable
{
    private const string Token = "review-network-askpass-token";
    private const string Selected = "review-inherited-helper-credential";
    private const string Origin = "https://github.com/org/config.git";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-network-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("credential.helper")]
    [InlineData("credential.https://github.com.helper")]
    public async Task ConfigParameters_NetworkGetAndStoreSuppressed_WithIndependentReachableControls(string key)
    {
        RequireLinux();
        var bare = CreateBareRepository();
        await using var server = new GitHttpsProxy(bare, Token, Selected);
        var repo = CreateWorkingRepository("operation", bare, server.Port);
        var log = Path.Combine(_root, "helper.log");
        var script = Path.Combine(_root, "recorder.sh");
        File.WriteAllText(script, "#!/bin/sh\n" +
            $"printf 'CALL %s\\n' \"$1\" >> '{log}'\ncat >> '{log}'\n" +
            "if [ \"$1\" = get ] && [ \"$REVIEW_SUPPLY_CREDENTIAL\" = yes ]; then\n" +
            $"printf 'username=inherited-user\\npassword={Selected}\\n'\nfi\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var inherited = $"'{key}={script}'";
        var old = Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS");
        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        IReadOnlyDictionary<string, string?> operation;
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", inherited);
            Assert.Equal(inherited, Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS"));
            operation = helper.BuildChildEnvironment(Token);
            // Constructed from the REAL inherited channel; parent is not rewritten by the builder.
            Assert.Equal(inherited, Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS"));
        }
        finally { Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", old); }
        using var auth = new BrainGitAuth(Token, Origin, helper);
        var manager = new ConfigRepoManager(Origin, repo) { TokenResolver = _ => Task.FromResult<string?>(Token) };
        var persistentConfig = File.ReadAllBytes(Path.Combine(repo, ".git", "config"));
        // Exercise BOTH real manager launch boundaries with the hostile parent channel present,
        // not only the helper builder. Neither uses a fake runner or modifies global config.
        using (new EnvironmentScope(new Dictionary<string, string?> { ["GIT_CONFIG_PARAMETERS"] = inherited }))
        {
            await manager.SyncRepoAsync(TestContext.Current.CancellationToken);
            var brainRoot = Path.Combine(_root, "brain");
            var brainClone = Path.Combine(brainRoot, "repos", "review");
            Directory.CreateDirectory(Path.GetDirectoryName(brainClone)!);
            await SetupGit(Path.GetDirectoryName(brainClone)!, "clone", repo, "review");
            await SetupGit(brainClone, "remote", "set-url", "origin", Origin);
            await SetupGit(brainClone, "config", "http.proxy", $"http://127.0.0.1:{server.Port}");
            await SetupGit(brainClone, "config", "http.sslVerify", "false");
            var brain = new BrainRepoManager(brainRoot,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BrainRepoManager>.Instance,
                tokenLookup: _ => Task.FromResult<string?>(Token), configuredUrlLookup: _ => Origin);
            var fetched = await brain.FetchOriginAsync("review", ct: TestContext.Current.CancellationToken);
            Assert.True(fetched.Success, fetched.Error);
            Assert.Equal(inherited, Environment.GetEnvironmentVariable("GIT_CONFIG_PARAMETERS"));
        }
        Assert.Equal(persistentConfig, File.ReadAllBytes(Path.Combine(repo, ".git", "config")));
        // Also install this exact operation env on a REAL child; leave the indexed reset in
        // place in both controls to isolate the legacy channel's higher precedence.
        var produced = await RunGit(repo, operation, "ls-remote", "origin", "refs/heads/main");
        Assert.Equal(0, produced.ExitCode);
        Assert.Contains("refs/heads/main", produced.Stdout);
        Assert.Contains(server.Snapshot(), r => r.Host == "github.com" && r.Password == Token);
        Assert.False(File.Exists(log), "Inherited get/store was reached during the operation.");

        // Selection control: restore ONLY the higher-precedence PARAMETERS channel. Same helper,
        // same real server, indexed reset still present. Helper supplies a DIFFERENT credential.
        var control = new Dictionary<string, string?>(operation)
        {
            [HostGitAskpassHelper.ConfigParametersEnvName] = inherited,
            ["REVIEW_SUPPLY_CREDENTIAL"] = "yes",
        };
        Assert.Equal(operation["GIT_CONFIG_COUNT"], control["GIT_CONFIG_COUNT"]);
        var selected = await RunGit(repo, control, "ls-remote", "origin", "refs/heads/main");
        Assert.Equal(0, selected.ExitCode);
        Assert.Contains("refs/heads/main", selected.Stdout);
        var selectionLog = File.ReadAllText(log);
        Assert.Contains("CALL get", selectionLog);
        Assert.Contains("host=github.com", selectionLog);
        Assert.Contains(server.Snapshot(), r => r.Host == "github.com" && r.Password == Selected);

        // Persistence control: helper no longer supplies a credential, so the REAL eligible-host
        // askpass fallback works and git calls store with exactly the operation's token.
        File.Delete(log);
        control.Remove("REVIEW_SUPPLY_CREDENTIAL");
        var stored = await RunGit(repo, control, "ls-remote", "origin", "refs/heads/main");
        Assert.Equal(0, stored.ExitCode);
        Assert.Contains("refs/heads/main", stored.Stdout);
        var persisted = File.ReadAllText(log);
        Assert.Contains("CALL get", persisted);
        var storeStart = persisted.IndexOf("CALL store", StringComparison.Ordinal);
        Assert.True(storeStart >= 0, "Eligible success must reach the store callback in the control.");
        Assert.Contains("host=github.com", persisted[storeStart..]);
        Assert.Contains($"password={Token}", persisted[storeStart..]);
    }

    [Theory]
    [InlineData("pushurl")]
    [InlineData("alternate-tracking")]
    public async Task ConfigManager_EffectiveNoneligibleChallengeRunsButGetsNoToken(string route)
    {
        RequireLinux();
        var bare = CreateBareRepository();
        await using var server = new GitHttpsProxy(bare, Token, Selected);
        var repo = CreateWorkingRepository("config", bare, server.Port);
        const string other = "https://gitlab.com/org/config.git";
        if (route == "pushurl")
        {
            await SetupGit(repo, "config", "remote.origin.pushurl", other);
            Assert.Equal(other, (await SetupGit(repo, "remote", "get-url", "--push", "origin")).Stdout.Trim());
        }
        else
        {
            await SetupGit(repo, "remote", "add", "mirror", other);
            await SetupGit(repo, "config", "branch.main.remote", "mirror");
            Assert.Equal("mirror", (await SetupGit(repo, "config", "--get", "branch.main.remote")).Stdout.Trim());
            Assert.Equal(other, (await SetupGit(repo, "remote", "get-url", "mirror")).Stdout.Trim());
        }
        Assert.Equal(Origin, (await SetupGit(repo, "remote", "get-url", "origin")).Stdout.Trim());
        Assert.False(other.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase));
        var manager = new ConfigRepoManager(Origin, repo) { TokenResolver = _ => Task.FromResult<string?>(Token) };
        var observed = await Record.ExceptionAsync(() => route == "pushurl"
            ? manager.CommitFileAsync("seed.txt", "no changes", TestContext.Current.CancellationToken)
            : manager.SyncRepoAsync(TestContext.Current.CancellationToken));
        var error = Assert.IsType<InvalidOperationException>(observed);
        Assert.Contains("git exited with code", error.Message); // not a destination-policy rejection
        Assert.Contains("gitlab.com", error.Message);
        Assert.DoesNotContain(Token, error.ToString());
        var requests = server.Snapshot();
        Assert.NotEmpty(requests); // viable network path, not missing tracking/no-op/local failure
        Assert.All(requests, r =>
        {
            Assert.Equal("gitlab.com", r.Host);
            Assert.Null(r.Authorization);
            Assert.DoesNotContain(Token, r.Headers);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + Token)), r.Headers);
        });
        Assert.Contains(requests, r => r.Path.Contains(route == "pushurl" ? "git-receive-pack" : "git-upload-pack", StringComparison.Ordinal));

        // Viable success control through the SAME manager/runtime/server: switch only the effective
        // route back to github.com, pull a genuine clone, and observe credential release + success.
        if (route == "pushurl") await SetupGit(repo, "config", "--unset-all", "remote.origin.pushurl");
        await SetupGit(repo, "config", "branch.main.remote", "origin");
        var before = server.Snapshot().Count;
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);
        Assert.Contains(server.Snapshot().Skip(before), r => r.Host == "github.com" && r.Password == Token);
        Assert.Equal("fixture payload", File.ReadAllText(Path.Combine(repo, "seed.txt")));
    }


    /// <summary>
    /// ADVERSARIAL effective destinations whose userinfo forges the eligible host inside git's prompt
    /// (rendered verbatim because <c>credential.sanitizePrompt=false</c>, a setting the operation
    /// leaves intact). The operation still RUNS its no-credential outcome against gitlab.com, but the
    /// recording server observes NO Authorization/token material — the parser, not the manager,
    /// refuses to release.
    /// </summary>
    [Theory]
    [InlineData("quoted-userinfo", "https://github.com%27@gitlab.com/org/config.git", "github.com'")]
    [InlineData("slash-username", "https://github.com%2F@gitlab.com/org/config.git", "github.com/")]
    public async Task ConfigManager_ForgedEligibleHostInUserinfo_RunsButReleasesNoToken(
        string vector, string pushUrl, string forgedUser)
    {
        RequireLinux();
        var bare = CreateBareRepository();
        await using var server = new GitHttpsProxy(bare, Token, Selected);
        var repo = CreateWorkingRepository("forged-" + vector, bare, server.Port);
        await SetupGit(repo, "config", "credential.sanitizePrompt", "false");
        await SetupGit(repo, "config", "remote.origin.pushurl", pushUrl);
        Assert.Equal(pushUrl, (await SetupGit(repo, "remote", "get-url", "--push", "origin")).Stdout.Trim());

        var manager = new ConfigRepoManager(Origin, repo) { TokenResolver = _ => Task.FromResult<string?>(Token) };
        var observed = await Record.ExceptionAsync(() =>
            manager.CommitFileAsync("seed.txt", "no changes", TestContext.Current.CancellationToken));

        var error = Assert.IsType<InvalidOperationException>(observed);
        Assert.Contains("git exited with code", error.Message);   // it RAN; not a policy rejection
        Assert.DoesNotContain(Token, error.ToString());

        var requests = server.Snapshot();
        Assert.NotEmpty(requests);                                 // the request really reached gitlab.com
        Assert.All(requests, r =>
        {
            Assert.Equal("gitlab.com", r.Host);
            // git may send the push URL's OWN username with an EMPTY password once the askpass
            // refuses; what must never appear is any password or token material.
            Assert.True(string.IsNullOrEmpty(r.Password), "A password/token was released to gitlab.com.");
            Assert.DoesNotContain(Token, r.Headers);
            Assert.DoesNotContain(
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{forgedUser}:{Token}")), r.Headers);
        });

        // VIABLE ELIGIBLE CONTROL through the SAME manager/server/config (sanitizePrompt=false still on):
        // restore the real route and the credential IS released for the genuine github.com challenge.
        await SetupGit(repo, "config", "--unset-all", "remote.origin.pushurl");
        var before = server.Snapshot().Count;
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);
        Assert.Contains(server.Snapshot().Skip(before), r => r.Host == "github.com" && r.Password == Token);
        Assert.Equal("fixture payload", File.ReadAllText(Path.Combine(repo, "seed.txt")));
    }

    /// <summary>
    /// Independent review probes. A token-free forwarding askpass logs Git's REAL prompt before
    /// executing the production script, so failure cannot pass because Git never rendered the
    /// adversarial username (or because a global sanitizePrompt setting silently sanitized it).
    /// </summary>
    [Theory]
    [InlineData("https://github.com%27@gitlab.com/org/config.git", "github.com'")]
    [InlineData("https://github.com%2F@gitlab.com/org/config.git", "github.com/")]
    public async Task AdversarialPushUrl_RealUnsanitizedPrompt_IsRefusedBeforeTokenRelease(string pushUrl, string username)
    {
        RequireLinux();
        var bare = CreateBareRepository();
        await using var server = new GitHttpsProxy(bare, Token, Selected);
        var repo = CreateWorkingRepository("independent-prompt", bare, server.Port);
        var target = new Uri(pushUrl);
        Assert.Equal("gitlab.com", target.Host);
        Assert.Equal(username, Uri.UnescapeDataString(target.UserInfo));
        await SetupGit(repo, "config", "credential.sanitizePrompt", "false");
        await SetupGit(repo, "config", "remote.origin.pushurl", pushUrl);
        Assert.Equal("false", (await SetupGit(repo, "config", "--bool", "--get", "credential.sanitizePrompt")).Stdout.Trim());
        Assert.Equal(pushUrl, (await SetupGit(repo, "remote", "get-url", "--push", "origin")).Stdout.Trim());
        Assert.Equal(Origin, (await SetupGit(repo, "remote", "get-url", "origin")).Stdout.Trim());
        var (wrapper, promptLog) = CreatePromptForwarder();
        var manager = new ConfigRepoManager(Origin, repo) { TokenResolver = _ => Task.FromResult<string?>(Token) };
        manager.GitLaunchObserver = (_, argv, env) =>
        {
            Assert.All(argv, a => Assert.DoesNotContain(Token, a));
            if (env is null) return;
            var child = Assert.IsAssignableFrom<IDictionary<string, string?>>(env);
            var productionScript = child[HostGitAskpassHelper.AskpassEnvName]!;
            Assert.True(File.Exists(productionScript));
            Assert.DoesNotContain(Token, File.ReadAllText(productionScript));
            child["REVIEW_REAL_ASKPASS"] = productionScript;
            child["REVIEW_PROMPT_LOG"] = promptLog;
            child[HostGitAskpassHelper.AskpassEnvName] = wrapper;
        };
        var observed = await Record.ExceptionAsync(() =>
            manager.CommitFileAsync("seed.txt", "no changes", TestContext.Current.CancellationToken));

        // Premise before the no-release assertions: exactly the review's decoded attack reached
        // the REAL production script, with the wrong destination independently observed on wire.
        var prompts = File.ReadAllLines(promptLog);
        Assert.Contains($"Password for 'https://{username}@gitlab.com': ", prompts);
        Assert.All(prompts, p => Assert.DoesNotContain(Token, p));
        var attempts = server.Snapshot();
        Assert.NotEmpty(attempts);
        Assert.All(attempts, r => Assert.Equal("gitlab.com", r.Host));
        Assert.Contains(attempts, r => r.Path.Contains("service=git-receive-pack", StringComparison.Ordinal));
        var failure = Assert.IsType<InvalidOperationException>(observed);
        Assert.Contains("git exited with code", failure.Message);
        Assert.DoesNotContain(Token, failure.ToString());
        Assert.All(attempts, r =>
        {
            // Git can send the URL's own username with an EMPTY password; do not broaden the
            // requirement into no Authorization header, nor confuse that empty password with a leak.
            Assert.True(string.IsNullOrEmpty(r.Password), "Gitlab received password material.");
            Assert.DoesNotContain(Token, r.Headers);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + Token)), r.Headers);
        });

        // Same child-launch observer and real TLS server, but restore the eligible route. This
        // proves the forwarding observer neither hides nor disables actual credential release.
        await SetupGit(repo, "config", "--unset-all", "remote.origin.pushurl");
        var before = server.Snapshot().Count;
        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);
        Assert.Contains(server.Snapshot().Skip(before), r => r.Host == "github.com" && r.Password == Token);
        Assert.Equal("fixture payload", File.ReadAllText(Path.Combine(repo, "seed.txt")));
    }

    [Theory]
    [InlineData("gitlab.com", "github.com'", false)]
    [InlineData("gitlab.com", "github.com/", false)]
    [InlineData("gitlab.com", "github.com'", true)]
    [InlineData("gitlab.com", "github.com/", true)]
    // These have an otherwise ELIGIBLE host: they isolate the ambiguous-userinfo refusal from
    // the downstream host check, which would mask a removed refusal in the cross-host cases.
    [InlineData("github.com", "review'quote", false)]
    [InlineData("github.com", "review/user", false)]
    [InlineData("github.com", "review@user", false)]
    public async Task CredentialFill_RealPrompt_RefusesAmbiguousOrCrossHostUsername(string host, string username, bool sanitize)
    {
        RequireLinux();
        var bare = CreateBareRepository();
        var repo = Path.Combine(_root, "fill");
        await SetupGit(_root, "clone", bare, repo);
        await SetupGit(repo, "config", "credential.sanitizePrompt", sanitize ? "true" : "false");
        Assert.Equal(sanitize ? "true" : "false", (await SetupGit(repo, "config", "--bool", "--get", "credential.sanitizePrompt")).Stdout.Trim());
        var (wrapper, promptLog) = CreatePromptForwarder();
        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        Assert.DoesNotContain(Token, File.ReadAllText(helper.ScriptPath));
        Dictionary<string, string?> env;
        // Supply sanitizePrompt through the inherited indexed channel BEFORE building the operation
        // environment. The helper must be safe with either inherited value, not just local defaults.
        using (new EnvironmentScope(new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "credential.sanitizePrompt",
            ["GIT_CONFIG_VALUE_0"] = sanitize ? "true" : "false",
        }))
        {
            env = new Dictionary<string, string?>(helper.BuildChildEnvironment(Token))
            {
                [HostGitAskpassHelper.AskpassEnvName] = wrapper,
                ["REVIEW_REAL_ASKPASS"] = helper.ScriptPath,
                ["REVIEW_PROMPT_LOG"] = promptLog,
            };
        }
        var effective = await RunGit(repo, env, "config", "--bool", "--get", "credential.sanitizePrompt");
        Assert.Equal(0, effective.ExitCode);
        Assert.Equal(sanitize ? "true" : "false", effective.Stdout.Trim());
        var refused = await RunCredentialProtocol(repo, env, $"protocol=https\nhost={host}\nusername={username}\n\n");
        var prompts = File.ReadAllLines(promptLog);
        Assert.NotEmpty(prompts);
        if (!sanitize) Assert.Contains($"Password for 'https://{username}@{host}': ", prompts);
        else Assert.All(prompts, p => Assert.Contains("gitlab.com", p));
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Equal(string.Empty, refused.Stdout);
        Assert.DoesNotContain(Token, refused.Stderr);

        var accepted = await RunCredentialProtocol(repo, env,
            "protocol=https\nhost=github.com\nusername=x-access-token\n\n");
        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("username=x-access-token", accepted.Stdout);
        Assert.Contains("password=" + Token, accepted.Stdout);
    }

    /// <summary>
    /// A successful fill must actually invoke the production askpass with Git's exact prompt
    /// bytes, including the trailing space. This pins the git >= 2.47 interactive=false regression
    /// without relying on a version check or only testing manually constructed shell prompts.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("x-access-token")]
    public async Task CredentialFill_EligibleRealPrompts_InvokeProductionHelper_WithExactBytes(string? username)
    {
        RequireLinux();
        Directory.CreateDirectory(_root);
        var (wrapper, promptLog) = CreatePromptForwarder();
        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        Assert.DoesNotContain(Token, File.ReadAllText(wrapper));
        Assert.DoesNotContain(Token, File.ReadAllText(helper.ScriptPath));
        var env = new Dictionary<string, string?>(helper.BuildChildEnvironment(Token))
        {
            [HostGitAskpassHelper.AskpassEnvName] = wrapper,
            ["REVIEW_REAL_ASKPASS"] = helper.ScriptPath,
            ["REVIEW_PROMPT_LOG"] = promptLog,
        };
        Assert.Equal("0", env[HostGitAskpassHelper.PromptDisabledEnvName]);
        Assert.Equal(HostGitAskpassHelper.GcmInteractiveNever, env[HostGitAskpassHelper.GcmInteractiveEnvName]);

        var input = "protocol=https\nhost=github.com\n"
            + (username is null ? "" : $"username={username}\n") + "\n";
        var result = await RunCredentialProtocol(_root, env, input);
        // Include full stderr in a failing control, but never render a credential-bearing stdout.
        Assert.True(result.ExitCode == 0, $"git credential fill exited {result.ExitCode}: {result.Stderr}");
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Contains("protocol=https\n", result.Stdout);
        Assert.Contains("host=github.com\n", result.Stdout);
        Assert.Contains("username=x-access-token\n", result.Stdout);
        Assert.Contains($"password={Token}\n", result.Stdout);

        // The token-free forwarder adds ONE LF per invocation; comparison is byte-exact, so it
        // also pins one call when the username is supplied, two otherwise, and no extra prompts.
        var expected = (username is null ? "Username for 'https://github.com': \n" : "")
            + "Password for 'https://x-access-token@github.com': \n";
        Assert.True(File.Exists(promptLog), "Git never invoked the production askpass forwarder.");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(promptLog));
    }

    private (string Script, string Log) CreatePromptForwarder()
    {
        var script = Path.Combine(_root, "forward-askpass.sh");
        var log = Path.Combine(_root, "real-prompts.log");
        File.WriteAllText(script, "#!/bin/sh\nprintf '%s\\n' \"$1\" >> \"$REVIEW_PROMPT_LOG\"\nexec \"$REVIEW_REAL_ASKPASS\" \"$@\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.DoesNotContain(Token, File.ReadAllText(script));
        return (script, log);
    }

    private static async Task<BrainGitResult> RunCredentialProtocol(
        string repo, IReadOnlyDictionary<string, string?> environment, string input)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        process.StartInfo.ArgumentList.Add("credential");
        process.StartInfo.ArgumentList.Add("fill");
        process.StartInfo.Environment.Clear();
        foreach (var (key, value) in environment) if (value is not null) process.StartInfo.Environment[key] = value;
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        return new BrainGitResult(process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task EligibleGitClone_AuthenticatesAndMaterializesRealRepository()
    {
        RequireLinux();
        var bare = CreateBareRepository();
        await using var server = new GitHttpsProxy(bare, Token, Selected);
        var destination = Path.Combine(_root, "clone");
        var manager = new ConfigRepoManager(Origin, destination) { TokenResolver = _ => Task.FromResult<string?>(Token) };
        manager.GitLaunchObserver = (_, _, env) =>
        {
            // No fake runner: the real child still installs this SAME environment after observation.
            if (env is null) return;
            Assert.Equal(Token, env[HostGitAskpassHelper.CredentialEnvName]);
            Assert.DoesNotContain(Token, File.ReadAllText(env[HostGitAskpassHelper.AskpassEnvName]!));
        };
        // Clone has no local config yet. Supply proxy settings ONLY in this child's inherited
        // indexed env; the operation helper appends its resets but preserves these transport settings.
        using (new EnvironmentScope(new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "3",
            ["GIT_CONFIG_KEY_0"] = "http.proxy", ["GIT_CONFIG_VALUE_0"] = $"http://127.0.0.1:{server.Port}",
            ["GIT_CONFIG_KEY_1"] = "http.sslVerify", ["GIT_CONFIG_VALUE_1"] = "false",
            ["GIT_CONFIG_KEY_2"] = "http.version", ["GIT_CONFIG_VALUE_2"] = "HTTP/1.1",
        }))
        {
            await manager.SyncRepoAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal("fixture payload", File.ReadAllText(Path.Combine(destination, "seed.txt")));
        Assert.Contains(server.Snapshot(), r => r.Host == "github.com" && r.Password == Token);
        Assert.Equal(Origin, (await SetupGit(destination, "remote", "get-url", "origin")).Stdout.Trim());
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(destination, ".git", "config")));
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux askpass and real HTTPS proxy fixture.");
    }

    private string CreateBareRepository()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        SetupGit(seed, "init", "-b", "main").GetAwaiter().GetResult();
        SetupGit(seed, "config", "user.name", "Review").GetAwaiter().GetResult();
        SetupGit(seed, "config", "user.email", "review@test.invalid").GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(seed, "seed.txt"), "fixture payload");
        SetupGit(seed, "add", "seed.txt").GetAwaiter().GetResult();
        SetupGit(seed, "-c", "commit.gpgsign=false", "commit", "-m", "seed").GetAwaiter().GetResult();
        var bare = Path.Combine(_root, "bare.git");
        SetupGit(_root, "clone", "--bare", seed, bare).GetAwaiter().GetResult();
        SetupGit(bare, "update-server-info").GetAwaiter().GetResult();
        Assert.True(File.Exists(Path.Combine(bare, "info", "refs")));
        return bare;
    }

    private string CreateWorkingRepository(string name, string bare, int port)
    {
        var repo = Path.Combine(_root, name);
        SetupGit(_root, "clone", bare, repo).GetAwaiter().GetResult();
        SetupGit(repo, "remote", "set-url", "origin", Origin).GetAwaiter().GetResult();
        SetupGit(repo, "config", "http.proxy", $"http://127.0.0.1:{port}").GetAwaiter().GetResult();
        SetupGit(repo, "config", "http.sslVerify", "false").GetAwaiter().GetResult();
        SetupGit(repo, "config", "http.version", "HTTP/1.1").GetAwaiter().GetResult();
        return repo;
    }

    private static async Task<BrainGitResult> SetupGit(string dir, params string[] args)
    {
        var result = await RunGit(dir, null, args);
        Assert.True(result.ExitCode == 0, $"Fixture git {string.Join(' ', args)} failed: {result.Stderr}");
        return result;
    }

    private static async Task<BrainGitResult> RunGit(string dir, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo("git")
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        if (env is not null)
        {
            p.StartInfo.Environment.Clear();
            foreach (var (k, v) in env) if (v is not null) p.StartInfo.Environment[k] = v;
        }
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch
        {
            if (!p.HasExited) p.Kill(true);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw;
        }
        return new BrainGitResult(p.ExitCode, await output, await error);
    }

    public void Dispose() => TestHelpers.ForceDeleteDirectory(_root);

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _old;
        public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            _old = values.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
        }
        public void Dispose()
        {
            foreach (var (key, value) in _old) Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed record Request(string Host, string Path, string Headers, string? Authorization, string? Password);

    /// <summary>Owned single accept loop: every connection is joined; cancellation interrupts reads.
    /// Ephemeral certificate + sslVerify=false are fixture-local, never system/global mutations.</summary>
    private sealed class GitHttpsProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _loop;
        private readonly List<Request> _requests = [];
        private readonly string _bare;
        private readonly string[] _passwords;
        internal int Port { get; }
        internal List<Request> Snapshot() { lock (_requests) return [.. _requests]; }

        internal GitHttpsProxy(string bare, params string[] passwords)
        {
            _bare = bare;
            _passwords = passwords;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=github.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeLoop();
        }

        private async Task ServeLoop()
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
                    using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                    var headers = await ReadHeaders(tls, _stop.Token);
                    var lines = headers.Split("\r\n");
                    var path = lines[0].Split(' ')[1];
                    var host = lines.Single(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
                    var authorization = lines.FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                    var password = authorization?.StartsWith("Basic ", StringComparison.Ordinal) == true
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..])).Split(':', 2)[1] : null;
                    lock (_requests) _requests.Add(new Request(host, path, headers, authorization, password));
                    if (host != "github.com" || password is null || !_passwords.Contains(password))
                    {
                        await Respond(tls, "401 Unauthorized", [], "WWW-Authenticate: Basic realm=\"review\"\r\n");
                        continue;
                    }
                    const string prefix = "/org/config.git/";
                    var relative = path.Split('?')[0];
                    var file = relative.StartsWith(prefix, StringComparison.Ordinal)
                        ? Path.GetFullPath(Path.Combine(_bare, relative[prefix.Length..])) : "";
                    if (file.StartsWith(_bare + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(file))
                        await Respond(tls, "200 OK", await File.ReadAllBytesAsync(file, _stop.Token));
                    else await Respond(tls, "404 Not Found", []);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task Respond(Stream stream, string status, byte[] body, string extra = "")
        {
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extra}Content-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }

        private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
        {
            var result = new StringBuilder();
            var one = new byte[1];
            while (result.Length < 65536)
            {
                var count = await stream.ReadAsync(one, ct);
                if (count == 0) throw new IOException("Git closed connection before complete headers.");
                result.Append((char)one[0]);
                if (result.Length >= 4 && result.ToString(result.Length - 4, 4) == "\r\n\r\n") return result.ToString();
            }
            throw new IOException("Fixture request header bound exceeded.");
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { _certificate.Dispose(); _stop.Dispose(); }
        }
    }
}
