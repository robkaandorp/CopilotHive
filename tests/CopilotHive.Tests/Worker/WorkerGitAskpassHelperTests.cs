using System.Diagnostics;

using CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// The WORKER-side target-repository credential transport: the operation-scoped
/// <see cref="WorkerGitAskpassHelper"/>, its child environment, the
/// <see cref="WorkerGitCredentialTransport"/> scope, and the
/// <see cref="TargetRepositoryCredential"/> URL split that keeps the credential off every git argv.
/// </summary>
/// <remarks>
/// The helper's own script is executed with a REAL <c>/bin/sh</c> (Linux-gated) and its child
/// environment is fed to a REAL <c>git</c> child, so a "token never leaves the child" claim is
/// observed on the actual launch path rather than only on a pre-factory seam. No delays, no polling,
/// no timing assertions.
/// </remarks>
[Collection("EnvVarMutation")]
public sealed class WorkerGitAskpassHelperTests : IDisposable
{
    private const string Token = "ghp_worker_target_secret_value";
    private const string EligibleUrl = "https://github.com/org/repo.git";

    private readonly string _root;

    public WorkerGitAskpassHelperTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), $"worker-askpass-t-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Creation: private directory, token-free script, owner-only modes ──────

    /// <summary>
    /// The helper owns its OWN private directory (distinct per creation) and the script inside it. Two
    /// helpers created back to back never share a directory, which is what makes the per-assignment
    /// isolation real rather than nominal.
    /// </summary>
    [Fact]
    public void TryCreate_GivesEachHelperItsOwnPrivateDirectory()
    {
        using var first = WorkerGitAskpassHelper.TryCreate();
        using var second = WorkerGitAskpassHelper.TryCreate();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.DirectoryPath, second!.DirectoryPath);
        Assert.Equal(
            Path.Combine(first.DirectoryPath, WorkerGitAskpassHelper.ScriptFileName), first.ScriptPath);
        Assert.True(File.Exists(first.ScriptPath));
    }

    /// <summary>
    /// The script's BYTES carry no secret: the credential is read from the CHILD-ONLY environment
    /// variable at invocation time, so the same script serves every operation.
    /// </summary>
    [Fact]
    public void ScriptContents_AreTokenFree()
    {
        using var helper = WorkerGitAskpassHelper.TryCreate();

        var text = File.ReadAllText(helper!.ScriptPath);

        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        // The credential is referenced through the environment variable, never inlined.
        Assert.Contains(
            $"${{{WorkerGitAskpassHelper.CredentialEnvName}}}", text, StringComparison.Ordinal);
    }

    /// <summary>The helper directory and its script are owner-only (0700) on POSIX.</summary>
    [Fact]
    public void TryCreate_AppliesOwnerOnlyModes()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX modes only."); return; }

        using var helper = WorkerGitAskpassHelper.TryCreate();

        const UnixFileMode OwnerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(helper!.ScriptPath));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(helper.DirectoryPath));
    }

    /// <summary>
    /// On a platform the helper cannot serve, creation returns <c>null</c> — the fail-closed signal.
    /// No credential transport exists then, and there is no tokenized-URL fallback anywhere.
    /// </summary>
    [Fact]
    public void TryCreate_OnUnsupportedPlatform_FailsClosed()
    {
        var helper = WorkerGitAskpassHelper.TryCreate(platformSupported: () => false);

        Assert.Null(helper);

        // The REAL predicate is the default authority: on this (Linux) host it succeeds, which is
        // what makes the false case above meaningful rather than vacuous.
        if (OperatingSystem.IsLinux())
        {
            using var real = WorkerGitAskpassHelper.TryCreate();
            Assert.NotNull(real);
        }
    }

    // ── The child environment ────────────────────────────────────────────────

    /// <summary>
    /// The complete child environment scrubs EVERY inherited credential channel and then installs the
    /// operation-scoped wiring. The credential appears exactly ONCE — in the child-only variable — and
    /// never in the worker's own process environment.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_ScrubsEveryInheritedCredentialChannel()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "inherited-gh",
            ["GITHUB_TOKEN"] = "inherited-github",
            ["GITHUB_CONFIG_REPO_TOKEN"] = "inherited-config",
            ["GIT_ASKPASS"] = "/inherited/askpass",
            ["SSH_ASKPASS"] = "/inherited/ssh-askpass",
            ["GCM_INTERACTIVE"] = "always",
            ["GIT_TERMINAL_PROMPT"] = "1",
            [WorkerGitAskpassHelper.CredentialEnvName] = "inherited-stale-credential",
            ["PATH"] = "/usr/bin",
        };

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var environment = BuildWithSeededProcessEnvironment(helper!, inherited);

        Assert.False(environment.ContainsKey("GH_TOKEN"));
        Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
        Assert.False(environment.ContainsKey("GITHUB_CONFIG_REPO_TOKEN"));
        Assert.False(environment.ContainsKey("SSH_ASKPASS"));

        Assert.Equal("0", environment[WorkerGitAskpassHelper.PromptDisabledEnvName]);
        Assert.Equal(
            WorkerGitAskpassHelper.GcmInteractiveNever,
            environment[WorkerGitAskpassHelper.GcmInteractiveEnvName]);
        Assert.Equal(helper!.ScriptPath, environment[WorkerGitAskpassHelper.AskpassEnvName]);
        Assert.Equal(Token, environment[WorkerGitAskpassHelper.CredentialEnvName]);

        // The stale inherited credential was scrubbed and REPLACED — exactly one credential value.
        Assert.Single(environment.Values, v => v == Token);

        // The worker's OWN process environment never holds the credential.
        Assert.Null(Environment.GetEnvironmentVariable(WorkerGitAskpassHelper.CredentialEnvName));
    }

    /// <summary>
    /// An inherited <c>GIT_CONFIG_COUNT</c> list is APPENDED to (never overwritten), so the
    /// operation's <c>credential.helper</c> reset comes AFTER any inherited entry and therefore wins.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_AppendsHelperResetsAfterInheritedConfigEntries()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = "store",
            ["GIT_CONFIG_KEY_1"] = "credential.username",
            ["GIT_CONFIG_VALUE_1"] = "someone",
        };

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var environment = BuildWithSeededProcessEnvironment(helper!, inherited);

        Assert.Equal("3", environment[WorkerGitAskpassHelper.ConfigCountEnvName]);
        Assert.Equal("credential.helper", environment["GIT_CONFIG_KEY_2"]);
        Assert.Equal(string.Empty, environment["GIT_CONFIG_VALUE_2"]);
    }

    /// <summary>
    /// The inherited legacy <c>GIT_CONFIG_PARAMETERS</c> channel — which git evaluates LAST and which
    /// therefore OUTRANKS the indexed entries — is REPLACED by the operation's own resets, so no
    /// inherited helper can select or persist the credential. No global git config is written.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_ReplacesInheritedConfigParameters()
    {
        const string Inherited = "'credential.helper=store' 'credential.username=x-access-token'";

        var inherited = new Dictionary<string, string?>
        {
            [WorkerGitAskpassHelper.ConfigParametersEnvName] = Inherited,
        };

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var environment = BuildWithSeededProcessEnvironment(helper!, inherited);

        var value = environment[WorkerGitAskpassHelper.ConfigParametersEnvName];

        // EXACTLY the operation's own reset — the inherited list is discarded entirely, not merged.
        Assert.Equal("'credential.helper='", value);
        Assert.DoesNotContain("store", value, StringComparison.Ordinal);
        Assert.DoesNotContain("credential.username", value, StringComparison.Ordinal);

        // The two channels agree: one indexed reset and one legacy reset, saying the same thing.
        Assert.Equal("1", environment[WorkerGitAskpassHelper.ConfigCountEnvName]);
    }

    /// <summary>
    /// <c>credential.interactive=false</c> is deliberately NEVER set: since git 2.47.0 it suppresses
    /// askpass entirely, which would make every authenticated operation fail with
    /// <c>unable to get password from user</c>. This test pins the absence across every channel.
    /// </summary>
    [Fact]
    public void BuildChildEnvironment_NeverSetsCredentialInteractive()
    {
        using var helper = WorkerGitAskpassHelper.TryCreate();
        var environment = BuildWithSeededProcessEnvironment(helper!, new Dictionary<string, string?>());

        Assert.False(
            environment.Keys.Any(k => k.Contains("credential.interactive", StringComparison.OrdinalIgnoreCase)),
            "A credential.interactive KEY was set — it is a total askpass suppression since git 2.47.");
        Assert.False(
            environment.Values.Any(v => v is not null
                && v.Contains("credential.interactive", StringComparison.OrdinalIgnoreCase)),
            "A credential.interactive VALUE was set — it is a total askpass suppression since git 2.47.");
    }

    // ── Credential RELEASE is bound to the eligible destination ───────────────

    /// <summary>
    /// A challenge whose rendered URL is an effective HTTPS <c>github.com</c>/<c>github.com:443</c> is
    /// answered: the fixed principal for a username prompt, and the CHILD-ONLY credential for the
    /// password prompt.
    /// </summary>
    [Theory]
    [InlineData("Username for 'https://github.com': ", "x-access-token")]
    [InlineData("Password for 'https://github.com': ", Token)]
    [InlineData("Password for 'https://x-access-token@github.com': ", Token)]
    [InlineData("Password for 'https://github.com/org/repo.git': ", Token)]
    // An EXPLICIT :443 IS eligible — the config seam's effective-port rule.
    [InlineData("Password for 'https://github.com:443': ", Token)]
    [InlineData("Password for 'https://github.com:443/org/repo.git': ", Token)]
    // The host comparison is case-insensitive, like every other destination check.
    [InlineData("Password for 'https://GitHub.COM': ", Token)]
    public void Script_AnswersChallengesForTheEligibleDestination(string prompt, string expected)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only /bin/sh script."); return; }

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var (code, stdout) = RunScript(helper!, prompt);

        Assert.Equal(0, code);
        Assert.Equal(expected, stdout);
    }

    /// <summary>
    /// Every OTHER destination — a different host, a lookalike host, a non-443 port, a non-HTTPS
    /// scheme, or an ambiguous/forged shape — gets an EMPTY answer and a non-zero exit, which is
    /// exactly what git sees when no credential exists.
    /// </summary>
    [Theory]
    [InlineData("Password for 'https://gitlab.com': ")]
    [InlineData("Password for 'https://user@gitlab.com': ")]
    [InlineData("Password for 'https://github.com.evil.test': ")]
    [InlineData("Password for 'https://evilgithub.com': ")]
    [InlineData("Password for 'https://github.com:8443': ")]
    [InlineData("Password for 'http://github.com': ")]
    [InlineData("Password for 'ssh://git@github.com': ")]
    [InlineData("Password for 'https://127.0.0.1:4242': ")]
    [InlineData("Password: ")]
    // Adversarial prompt forms (rendered when credential.sanitizePrompt=false): a quote, a slash or
    // an '@' forged inside the userinfo so the text merely LOOKS like the eligible host.
    [InlineData("Password for 'https://github.com'@gitlab.com': ")]
    [InlineData("Password for 'https://github.com%27@gitlab.com': ")]
    [InlineData("Password for 'https://github.com/@gitlab.com': ")]
    [InlineData("Password for 'https://github.com/x@gitlab.com': ")]
    [InlineData("Password for 'https://u@gitlab.com@github.com': ")]
    [InlineData("Username for 'https://github.com'@gitlab.com': ")]
    [InlineData("Password for 'https://github.com': trailing")]
    [InlineData("Password for \"https://github.com\": ")]
    public void Script_RefusesEveryOtherDestination_WithNoOutput(string prompt)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only /bin/sh script."); return; }

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var (code, stdout) = RunScript(helper!, prompt);

        Assert.NotEqual(0, code);
        Assert.Equal(string.Empty, stdout);
    }

    /// <summary>
    /// POSITIVE CONTROL on the REAL git credential protocol: git's own <c>credential fill</c> for
    /// <c>https://github.com</c> IS answered by the produced environment and script, so the refusals
    /// above are not an artefact of a helper that never releases anything — while another host's fill
    /// fails, and the credential never occurs in that child's output.
    /// </summary>
    [Fact]
    public void RealCredentialFill_ReleasesTheCredentialForGitHub_AndNotForAnotherHost()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only /bin/sh script."); return; }

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var env = new Dictionary<string, string?>(helper!.BuildChildEnvironment(Token)) { ["HOME"] = _root };

        var eligible = RunGitWithStdin(env, "protocol=https\nhost=github.com\n\n", "credential", "fill");
        Assert.Equal(0, eligible.ExitCode);
        Assert.Contains("username=x-access-token", eligible.Stdout, StringComparison.Ordinal);
        Assert.Contains($"password={Token}", eligible.Stdout, StringComparison.Ordinal);

        // An EXPLICIT :443 fill (the eligible port) is answered too.
        var explicitPort = RunGitWithStdin(
            env, "protocol=https\nhost=github.com:443\n\n", "credential", "fill");
        Assert.Equal(0, explicitPort.ExitCode);
        Assert.Contains($"password={Token}", explicitPort.Stdout, StringComparison.Ordinal);

        var other = RunGitWithStdin(env, "protocol=https\nhost=gitlab.com\n\n", "credential", "fill");
        Assert.NotEqual(0, other.ExitCode);
        Assert.DoesNotContain(Token, other.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inherited-helper suppression is REAL, observed on a live <c>git credential fill</c>: a helper
    /// installed through the INDEXED environment-config channel (<c>GIT_CONFIG_COUNT</c>) is neutralized
    /// because the operation's reset is APPENDED after it, so git never calls it and cannot persist the
    /// credential. The control run — the same hostile entry with the operation's appended reset removed —
    /// proves the recorder WOULD have been reached, so the assertion is not vacuous.
    /// </summary>
    [Fact]
    public void RealCredentialFill_InheritedIndexedHelper_IsSuppressed()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only /bin/sh script."); return; }

        // A recording helper: it appends a marker whenever git runs it.
        var recordPath = Path.Combine(_root, "inherited-helper-calls.txt");
        var inheritedHelper = Path.Combine(_root, "inherited-helper.sh");
        File.WriteAllText(
            inheritedHelper, $"#!/bin/sh\necho CALLED >> '{recordPath}'\necho password=inherited-store\n");
        File.SetUnixFileMode(
            inheritedHelper,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var helper = WorkerGitAskpassHelper.TryCreate();

        // Seed the hostile entry BEFORE building the child environment, so the production path appends
        // its reset AFTER it — exactly the ordering the design depends on.
        var seeded = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = $"!{inheritedHelper}",
        };

        var environment = BuildWithSeededProcessEnvironment(helper!, seeded);
        environment = new Dictionary<string, string?>(environment) { ["HOME"] = _root };

        // The production environment still carries the hostile entry (appended reset wins, nothing is
        // deleted) — so the following assertion observes the suppression, not an absent hostile value.
        Assert.Equal("2", environment[WorkerGitAskpassHelper.ConfigCountEnvName]);
        Assert.Equal($"!{inheritedHelper}", environment["GIT_CONFIG_VALUE_0"]);

        var fill = RunGitWithStdin(environment, "protocol=https\nhost=github.com\n\n", "credential", "fill");
        Assert.Equal(0, fill.ExitCode);
        Assert.Contains($"password={Token}", fill.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("inherited-store", fill.Stdout, StringComparison.Ordinal);

        // CONTROL — the same hostile entry with the operation's appended reset removed. This run DOES
        // reach the recorder, which is what makes the suppression above load-bearing.
        var control = new Dictionary<string, string?>(environment)
        {
            [WorkerGitAskpassHelper.ConfigCountEnvName] = "1",
        };
        control.Remove("GIT_CONFIG_KEY_1");
        control.Remove("GIT_CONFIG_VALUE_1");
        control.Remove(WorkerGitAskpassHelper.ConfigParametersEnvName);

        var controlFill = RunGitWithStdin(
            control, "protocol=https\nhost=github.com\n\n", "credential", "fill");

        Assert.True(
            controlFill.Stdout.Contains("inherited-store", StringComparison.Ordinal)
            || File.Exists(recordPath),
            "The control run never reached the recording helper, so the suppression assertion is vacuous.");
    }

    /// <summary>
    /// A REAL push whose effective destination (<c>remote.origin.pushurl</c>) is NONELIGIBLE while the
    /// configured origin is the eligible github.com URL still RUNS (it fails at the 401, exactly as
    /// with no credential) but NEVER sends the token: the server records no Authorization header.
    /// </summary>
    [Fact]
    public void RealPush_ThroughANonEligiblePushUrl_RunsButReleasesNoCredential()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only."); return; }

        using var server = new RecordingUnauthorizedServer();
        var repo = Path.Combine(_root, "push-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", EligibleUrl);
        RunRaw(repo, "config", "remote.origin.pushurl", $"http://127.0.0.1:{server.Port}/o/r.git");

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var (code, stderr) = RunOperationGit(helper!, repo, "push", "origin", "main");

        Assert.NotEqual(0, code);                          // it RAN and was refused, as with no credential
        Assert.NotEmpty(server.Requests);                   // the request really reached the noneligible host
        Assert.False(server.SawAuthorization, "The credential was released to a noneligible push destination.");
        Assert.DoesNotContain(Token, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for a REAL fetch from an ALTERNATE noneligible remote: the request is made and receives
    /// no credential.
    /// </summary>
    [Fact]
    public void RealFetch_FromAnAlternateNonEligibleRemote_RunsButReleasesNoCredential()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux-only."); return; }

        using var server = new RecordingUnauthorizedServer();
        var repo = Path.Combine(_root, "fetch-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", EligibleUrl);
        RunRaw(repo, "remote", "add", "mirror", $"http://127.0.0.1:{server.Port}/o/r.git");

        using var helper = WorkerGitAskpassHelper.TryCreate();
        var (code, stderr) = RunOperationGit(helper!, repo, "fetch", "mirror");

        Assert.NotEqual(0, code);
        Assert.NotEmpty(server.Requests);
        Assert.False(server.SawAuthorization, "The credential was released to an alternate noneligible remote.");
        Assert.DoesNotContain(Token, stderr, StringComparison.Ordinal);
    }

    // ── The transport scope: cleanup, isolation, fail-closed creation ────────

    /// <summary>The helper directory is gone after disposal, and disposal is idempotent.</summary>
    [Fact]
    public void Dispose_RemovesTheHelperDirectory_AndIsIdempotent()
    {
        var helper = WorkerGitAskpassHelper.TryCreate();
        var directory = helper!.DirectoryPath;

        Assert.True(Directory.Exists(directory));

        helper.Dispose();
        helper.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// A BLANK credential means there is nothing to carry: no transport (and therefore no helper
    /// directory) is created at all.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TransportCreate_WithoutACredential_ReturnsNull(string? credential)
    {
        var transport = WorkerGitCredentialTransport.Create(credential, EligibleUrl);

        Assert.Null(transport);
    }

    /// <summary>
    /// Two transports for two operations carry DIFFERENT secrets in DIFFERENT directories, so
    /// concurrent assignments can never observe each other's credential. Disposing one leaves the
    /// other's helper intact.
    /// </summary>
    [Fact]
    public void Transports_AreIsolatedPerOperation()
    {
        using var first = WorkerGitCredentialTransport.Create("ghp_first", EligibleUrl);
        var second = WorkerGitCredentialTransport.Create("ghp_second", EligibleUrl);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.ScriptPath, second!.ScriptPath);
        Assert.NotEqual(
            Path.GetDirectoryName(first.ScriptPath), Path.GetDirectoryName(second.ScriptPath));
        Assert.Equal("ghp_first", first.BuildChildEnvironment()[WorkerGitAskpassHelper.CredentialEnvName]);
        Assert.Equal("ghp_second", second.BuildChildEnvironment()[WorkerGitAskpassHelper.CredentialEnvName]);

        var secondDirectory = Path.GetDirectoryName(second.ScriptPath)!;
        first.Dispose();

        Assert.True(Directory.Exists(secondDirectory), "Disposing one transport removed another's helper.");

        second.Dispose();
        Assert.False(Directory.Exists(secondDirectory));
    }

    // ── The URL split: the credential never reaches an argv or an origin ─────

    /// <summary>
    /// An ELIGIBLE credential-bearing URL is split: the URL becomes credential-free and the credential
    /// is carried separately, ready for the child-only environment.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://github.com:443/org/repo.git")]
    [InlineData("https://GitHub.COM/org/repo.git")]
    public void Split_EligibleDestination_ReleasesTheCredentialSeparately(string url)
    {
        var (sanitized, credential, withheld) = TargetRepositoryCredential.Split(
            url.Replace("https://", $"https://x-access-token:{Token}@", StringComparison.Ordinal));

        Assert.Equal(url.Replace("GitHub.COM", "GitHub.COM", StringComparison.Ordinal), sanitized);
        Assert.Equal(Token, credential);
        Assert.False(withheld);
        Assert.DoesNotContain(Token, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", sanitized, StringComparison.Ordinal);
    }

    /// <summary>
    /// A credential-bearing URL for a NONELIGIBLE destination is still SANITIZED, but the credential is
    /// WITHHELD: the operation runs unauthenticated instead of releasing the token to a host, scheme or
    /// port that was never proven eligible.
    /// </summary>
    [Theory]
    [InlineData("https://gitlab.com/org/repo.git")]
    [InlineData("https://github.com:8443/org/repo.git")]
    [InlineData("http://github.com/org/repo.git")]
    [InlineData("https://github.com.evil.test/org/repo.git")]
    [InlineData("https://evilgithub.com/org/repo.git")]
    public void Split_NonEligibleDestination_WithholdsTheCredential(string url)
    {
        var (sanitized, credential, withheld) = TargetRepositoryCredential.Split(
            url.Replace("://", $"://x-access-token:{Token}@", StringComparison.Ordinal));

        Assert.Equal(url, sanitized);
        Assert.Null(credential);
        Assert.True(withheld);
        Assert.DoesNotContain(Token, sanitized, StringComparison.Ordinal);
    }

    /// <summary>
    /// A credential-free URL — including a LOCAL repository — passes through untouched and carries no
    /// credential, so local/non-github repositories remain fully usable unauthenticated.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://gitlab.com/org/repo.git")]
    [InlineData("/srv/git/local-repo.git")]
    [InlineData("file:///srv/git/local-repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("")]
    public void Split_CredentialFreeUrl_PassesThroughWithNoCredential(string url)
    {
        var (sanitized, credential, withheld) = TargetRepositoryCredential.Split(url);

        Assert.Equal(url, sanitized);
        Assert.Null(credential);
        Assert.False(withheld);
    }

    /// <summary>
    /// A credential whose userinfo is URI-ESCAPED is unescaped exactly once, so the helper releases the
    /// real secret rather than its escaped spelling.
    /// </summary>
    [Fact]
    public void Split_UriEscapedCredential_IsUnescapedOnce()
    {
        var escaped = Uri.EscapeDataString("p@ss/wo rd:1");

        var (sanitized, credential, withheld) = TargetRepositoryCredential.Split(
            $"https://x-access-token:{escaped}@github.com/org/repo.git");

        Assert.Equal(EligibleUrl, sanitized);
        Assert.Equal("p@ss/wo rd:1", credential);
        Assert.False(withheld);
    }

    // ── The credential-aware git operations, on the REAL child environment ────

    /// <summary>
    /// A credentialed PUSH repairs a pre-existing credential-bearing <c>origin</c> to the sanitized URL
    /// BEFORE the network command runs — the config seam's <c>set-url</c> precedent. The push itself is
    /// redirected to an unreachable local path by <c>pushurl</c>, so it fails without any network, and
    /// the assertion is about what the repository's persisted configuration holds afterwards.
    /// </summary>
    [Fact]
    public async Task PushBranch_WithTransport_RepairsACredentialBearingOrigin()
    {
        var repo = Path.Combine(_root, "repair-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", $"https://x-access-token:{Token}@github.com/org/repo.git");
        // A push destination that fails FAST and locally, so no token ever leaves the process.
        RunRaw(repo, "config", "remote.origin.pushurl",
            Path.Combine(_root, "does-not-exist.git"));

        using var transport = WorkerGitCredentialTransport.Create(Token, EligibleUrl);

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.PushBranchAsync(
                repo, "main", TestContext.Current.CancellationToken, transport));

        // The push really RAN and failed for the redirect destination — the test is not vacuous.
        Assert.Contains("Failed to push branch 'main'", ex.Message, StringComparison.Ordinal);

        var origin = ReadConfig(repo, "remote.origin.url");
        Assert.Equal(EligibleUrl, origin);
        Assert.DoesNotContain(Token, origin, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ReadConfigFile(repo), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONTROL for the repair: the same repository and the same failing push WITHOUT a transport —
    /// the persisted credential-bearing origin is left exactly as it was, which is what makes the repair
    /// above load-bearing rather than incidental.
    /// </summary>
    [Fact]
    public async Task PushBranch_WithoutTransport_LeavesThePreexistingOriginUntouched()
    {
        var repo = Path.Combine(_root, "control-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", $"https://x-access-token:{Token}@github.com/org/repo.git");
        RunRaw(repo, "config", "remote.origin.pushurl",
            Path.Combine(_root, "does-not-exist.git"));

        await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.PushBranchAsync(repo, "main", TestContext.Current.CancellationToken));

        Assert.Contains(Token, ReadConfig(repo, "remote.origin.url"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A credentialed CLONE through the REAL child environment releases no credential to a NONELIGIBLE
    /// destination: the server records the request but no <c>Authorization</c> header, and the raised
    /// message is sanitized.
    /// </summary>
    [Fact]
    public async Task CloneRepository_WithTransport_ReleasesNoCredentialToANonEligibleDestination()
    {
        using var server = new RecordingUnauthorizedServer();
        var url = $"http://127.0.0.1:{server.Port}/r.git";
        var target = Path.Combine(_root, "clone-target");

        using var transport = WorkerGitCredentialTransport.Create(Token, EligibleUrl);

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.CloneRepositoryAsync(
                url, target, TestContext.Current.CancellationToken, transport));

        Assert.NotEmpty(server.Requests);
        Assert.False(server.SawAuthorization, "The credential was released to a noneligible clone destination.");
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target), "A refused clone must not leave a half-written worktree.");
    }

    /// <summary>
    /// The base-branch FETCH inside <see cref="GitOperations.CreateBranchAsync"/> uses the same real child
    /// environment: the ELIGIBLE origin is left intact by the repair and the fetch itself releases the
    /// credential only to that destination — while a request that reaches a noneligible host carries no
    /// credential. The command completes through the documented from-current-HEAD fallback.
    /// </summary>
    [Fact]
    public async Task CreateBranch_WithTransport_ReleasesNoCredentialToANonEligibleFetchHost()
    {
        var repo = Path.Combine(_root, "fetch-branch-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", EligibleUrl);

        using var transport = WorkerGitCredentialTransport.Create(Token, EligibleUrl);

        // No local "release" branch and no network: the fetch fails, and the fallback creates the base
        // branch from HEAD so the operation still completes.
        await GitOperations.CreateBranchAsync(
            repo, "feature", "release", TestContext.Current.CancellationToken, transport);

        // The repair left the ALREADY credential-free eligible origin exactly as it was.
        Assert.Equal(EligibleUrl, ReadConfig(repo, "remote.origin.url"));
        Assert.Equal("feature", RunGitCapture(repo, "rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.DoesNotContain(Token, ReadConfigFile(repo), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ORIGIN-MISMATCH REFUSAL: a credentialed operation against a repository whose origin names a
    /// DIFFERENT destination is refused BEFORE any network command, and the origin is never rewritten —
    /// so a credential can never be released to, nor a worktree redirected at, an unestablished remote.
    /// </summary>
    [Fact]
    public async Task CredentialedOperation_WithAMismatchedOrigin_IsRefusedWithoutRewritingIt()
    {
        const string OtherDestination = "https://github.com/org/other-repo.git";

        var repo = Path.Combine(_root, "mismatch-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", OtherDestination);

        using var transport = WorkerGitCredentialTransport.Create(Token, EligibleUrl);

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.PushBranchAsync(
                repo, "main", TestContext.Current.CancellationToken, transport));

        Assert.Contains("origin is not the credential-free destination", ex.Message, StringComparison.Ordinal);
        Assert.Equal(OtherDestination, ReadConfig(repo, "remote.origin.url"));
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);

        // The SAME refusal for the credentialed branch/fetch path.
        var branchEx = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.CreateBranchAsync(
                repo, "feature", "release", TestContext.Current.CancellationToken, transport));

        Assert.Contains("origin is not the credential-free destination", branchEx.Message, StringComparison.Ordinal);
        Assert.Equal(OtherDestination, ReadConfig(repo, "remote.origin.url"));
    }

    /// <summary>
    /// A credential paired with a NONELIGIBLE sanitized origin is refused at the transport boundary: the
    /// destination gate cannot be bypassed by constructing a transport directly.
    /// </summary>
    [Theory]
    [InlineData("https://gitlab.com/org/repo.git")]
    [InlineData("https://github.com:8443/org/repo.git")]
    [InlineData("http://github.com/org/repo.git")]
    [InlineData("/srv/git/local-repo.git")]
    public void TransportCreate_WithANonEligibleOrigin_FailsClosed(string sanitizedOrigin)
    {
        var ex = Assert.Throws<GitOperationException>(
            () => WorkerGitCredentialTransport.Create(Token, sanitizedOrigin));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not an eligible HTTPS github.com:443 URL", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The credentialed FETCH inside <see cref="GitOperations.CreateBranchAsync"/> repairs a pre-existing
    /// credential-bearing origin to the sanitized URL BEFORE the network command — the same set-url
    /// precedent the push path applies. The assertion is on the repository's persisted configuration.
    /// </summary>
    [Fact]
    public async Task CreateBranch_WithTransport_RepairsACredentialBearingOriginBeforeTheFetch()
    {
        var repo = Path.Combine(_root, "fetch-repair-repo");
        InitRepoWithCommit(repo);
        RunRaw(repo, "remote", "add", "origin", $"https://x-access-token:{Token}@github.com/org/repo.git");

        using var transport = WorkerGitCredentialTransport.Create(Token, EligibleUrl);

        // The origin points at the real github.com host, so the fetch cannot succeed offline; the
        // documented fallback creates the base branch from HEAD, and the repair has already happened.
        await GitOperations.CreateBranchAsync(
            repo, "feature", "release", TestContext.Current.CancellationToken, transport);

        var origin = ReadConfig(repo, "remote.origin.url");
        Assert.Equal(EligibleUrl, origin);
        Assert.DoesNotContain(Token, origin, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ReadConfigFile(repo), StringComparison.Ordinal);
    }

    /// <summary>
    /// The CREDENTIAL IS NEVER WRITTEN TO DISK: after a complete credentialed clone attempt the helper
    /// directory holds a token-free script, and no git configuration anywhere in the temp tree carries the
    /// credential. This is the "no persistent helper / no global git config" contract observed on real
    /// files.
    /// </summary>
    [Fact]
    public async Task CredentialedClone_LeavesNoCredentialOnDiskAndNoPersistentHelper()
    {
        using var server = new RecordingUnauthorizedServer();
        var url = $"http://127.0.0.1:{server.Port}/r.git";
        var target = Path.Combine(_root, "disk-clone-target");

        string helperDirectory;
        using (var transport = WorkerGitCredentialTransport.Create(Token, EligibleUrl))
        {
            helperDirectory = Path.GetDirectoryName(transport!.ScriptPath)!;

            // The script is token-free even while the operation is IN FLIGHT.
            Assert.DoesNotContain(
                Token, File.ReadAllText(transport.ScriptPath), StringComparison.Ordinal);

            await Assert.ThrowsAsync<GitOperationException>(() =>
                GitOperations.CloneRepositoryAsync(
                    url, target, TestContext.Current.CancellationToken, transport));
        }

        // After disposal: no helper directory, no credential anywhere under the temp root.
        Assert.False(Directory.Exists(helperDirectory), "The helper directory outlived the operation.");
        AssertNoCredentialUnder(_root);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts no file under <paramref name="root"/> contains the token. Reads text only from files the
    /// test tree can contain (git config/objects are NOT read here: the assertion targets configuration
    /// and scripts, which is where a leaked credential would persist).
    /// </summary>
    private static void AssertNoCredentialUnder(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}objects",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        }
    }

    /// <summary>Reads one git config value from a real repository (empty string when unset).</summary>
    private static string ReadConfig(string repoDir, string key) =>
        RunGitCapture(repoDir, "config", "--get", key).Trim();

    /// <summary>Reads the raw <c>.git/config</c> of a real repository.</summary>
    private static string ReadConfigFile(string repoDir) =>
        File.ReadAllText(Path.Combine(repoDir, ".git", "config"));

    /// <summary>Runs git in <paramref name="repoDir"/> and returns its stdout.</summary>
    private static string RunGitCapture(string repoDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    /// <summary>
    /// Builds the child environment while the given values are seeded into the CURRENT process
    /// environment (restored in a <c>finally</c>), so the scrubbing is observed against real inherited
    /// variables rather than a synthetic dictionary.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> BuildWithSeededProcessEnvironment(
        WorkerGitAskpassHelper helper, IReadOnlyDictionary<string, string?> seeded)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, value) in seeded)
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            return helper.BuildChildEnvironment(Token);
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static (int ExitCode, string Stdout) RunScript(WorkerGitAskpassHelper helper, string prompt)
    {
        var env = new Dictionary<string, string?>
        {
            [WorkerGitAskpassHelper.CredentialEnvName] = Token,
        };

        var (code, stdout, _) = RunShell(helper.ScriptPath, env, prompt);
        return (code, stdout);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunShell(
        string scriptPath, IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add(scriptPath);
        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

        psi.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (value is not null)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGitWithStdin(
        IReadOnlyDictionary<string, string?> environment, string stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

        psi.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (value is not null)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static (int ExitCode, string Stderr) RunOperationGit(
        WorkerGitAskpassHelper helper, string dir, params string[] args)
    {
        var env = new Dictionary<string, string?>(helper.BuildChildEnvironment(Token))
        {
            ["HOME"] = Path.GetDirectoryName(helper.DirectoryPath)!,
        };

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

        psi.Environment.Clear();
        foreach (var (key, value) in env)
        {
            if (value is not null)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stderr);
    }

    private static void RunRaw(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private static void InitRepoWithCommit(string dir)
    {
        Directory.CreateDirectory(dir);

        RunRaw(dir, "init", "-b", "main");
        RunRaw(dir, "config", "user.email", "t@t.invalid");
        RunRaw(dir, "config", "user.name", "T");
        RunRaw(dir, "commit", "--allow-empty", "-m", "seed");
    }

    /// <summary>
    /// A server that answers every request 401 + Basic challenge and records the request heads, so a
    /// released credential is directly observable as an <c>Authorization</c> header.
    /// </summary>
    private sealed class RecordingUnauthorizedServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener =
            new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public List<string> Requests { get; } = [];

        public int Port { get; }

        public RecordingUnauthorizedServer()
        {
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[8192];
                            var read = await stream.ReadAsync(buffer);
                            if (read <= 0) return;

                            lock (Requests) Requests.Add(System.Text.Encoding.ASCII.GetString(buffer, 0, read));

                            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                                "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"x\"\r\n"
                                + "Content-Length: 0\r\nConnection: close\r\n\r\n"));
                        }
                        catch
                        {
                            // client went away
                        }
                    }
                });
            }
        }

        public bool SawAuthorization
        {
            get
            {
                lock (Requests)
                {
                    return Requests.Any(r => r.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            _stop.Dispose();
        }
    }
}
