using System.Collections;
using System.Security;
using CopilotHive.Configuration;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Table-driven tests for the validation AND execution seam <see cref="ConfigRepoGitOperations"/>
/// (slices 2c-b1b-i, 2c-b1b-ii, 2c-b1c-i and 2c-b1c-ii). The validation-stage contract tests
/// (grammar messages, containment, snapshot ordering, disposal at entry, canonicalization seam)
/// assert exact messages / exact outcome shapes so that deleting or reordering validation stages
/// breaks the suite (removal-proof). The execution-stage tests (2c-b1b-ii) assert the EXACT
/// <see cref="GitProcessRequest"/> shapes (ref-validation and final execution), the concrete
/// result mapping, the redaction boundary over process output, the cancellation precedence
/// (TCS-gated), the TCS-gated defensive snapshot, and the TCS-gated disposal-vs-in-flight
/// contract. The Stage 6a tests (2c-b1c-i) assert the URL resolution outcomes, the transport
/// ELIGIBILITY rule, the canonicalized explicit-origin launch, the exact SEQUENCING
/// (Stage 5 → Stage 6a → Stage 6b → Stage 7) with EXACT subprocess invocation totals, and the
/// resolver-exception policy. The Stage 6c/6d/6e tests (2c-b1c-ii) assert the ORIGIN state
/// machine (inspection → add / repair / reject), the credential + helper resolution policy, the
/// Stage 7 credential env injection and the literal-secret redaction pass — all via the
/// <see cref="GitOperations.ProcessRunner"/> seam (restored in a finally block) and TCS gates
/// for synchronization (no timing-based tests).
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoGitOperationsTests
{
    private const string LaunchFailed = "Git process failed to start.";
    private const string InvalidArguments = "Invalid arguments.";
    private const string NotConfigRepo =
        "Invalid git command: the working directory is not the config repository.";

    /// <summary>The Stage 6a message when the resolved config repo URL is absent.</summary>
    private const string UrlUnavailable = "Config repo URL is not available.";

    /// <summary>The FIXED Stage 6a/6e message for ANY non-cancellation resolver exception.</summary>
    private const string NotProvisioned = "Config repo not provisioned.";

    /// <summary>Stage 6d — the origin could not be INSPECTED.</summary>
    private const string OriginNotVerified = "Config repo origin could not be verified.";

    /// <summary>Stage 6d — <c>git remote add origin</c> failed.</summary>
    private const string OriginNotAdded = "Config repo origin could not be added.";

    /// <summary>Stage 6d — <c>git remote set-url origin</c> failed.</summary>
    private const string OriginNotUpdated = "Config repo origin could not be updated.";

    /// <summary>Stage 6d — the PRESENT origin is neither equivalent nor safely repairable.</summary>
    private const string OriginMismatch = "Config repo origin does not match the configured repository.";

    /// <summary>Stage 6e — the credential helper path is missing or its delegate threw.</summary>
    private const string HelperUnavailable = "Git credential helper path is not available.";

    /// <summary>
    /// A resolved config repo URL that is ELIGIBLE for the canonicalized explicit-origin
    /// launch: HTTPS, host <c>github.com</c>, implicit port 443. It is ALSO its own sanitized
    /// form, so it is exactly what the Stage 6d origin commands must carry.
    /// </summary>
    private const string EligibleUrl = "https://github.com/org/config-repo.git";

    /// <summary>The Stage 6d origin INSPECTION command (step 3a).</summary>
    private static readonly string[] OriginInspect = ["remote", "get-url", "origin"];

    /// <summary>The Stage 6d origin ADD command (step 3b) carrying the SANITIZED URL.</summary>
    private static readonly string[] OriginAdd = ["remote", "add", "origin", EligibleUrl];

    /// <summary>The Stage 6d origin REPAIR command (step 3c) carrying the SANITIZED URL.</summary>
    private static readonly string[] OriginSetUrl = ["remote", "set-url", "origin", EligibleUrl];

    /// <summary>A bound on every await that a mutant could otherwise block forever.</summary>
    private static readonly TimeSpan AwaitTimeout = TimeSpan.FromSeconds(30);

    private static readonly string RepoDir = OperatingSystem.IsWindows()
        ? @"C:\config-repo"
        : "/config-repo";

    /// <summary>A fully-qualified directory that is NOT the config repo.</summary>
    private static readonly string OutsideDir = OperatingSystem.IsWindows()
        ? @"C:\other\dir"
        : "/other/dir";

    private static WorkerLogger Log() => new("Test");

    /// <summary>
    /// Builds the seam. The URL resolver defaults to a VALID, ELIGIBLE HTTPS github.com URL
    /// so every transport (pull/push/fetch) test passes Stage 6a and really reaches the
    /// stages under test; the Stage 6a rejection paths supply their own resolver explicitly.
    /// </summary>
    private static ConfigRepoGitOperations CreateSeam(
        Action? onDispose = null,
        Func<string, string>? pathCanonicalizer = null,
        string? configRepoDir = null,
        Func<string?>? resolvedUrlResolver = null,
        Func<string?>? credentialResolver = null,
        Func<string>? credentialHelperPath = null,
        Func<string>? stagingNonceGenerator = null,
        Func<string, bool>? targetEntryExists = null,
        Func<string, bool>? stagingMarkerCreateNew = null,
        Func<string, bool>? stagingRepoChildCreate = null) =>
        new(
            configRepoDir ?? RepoDir,
            resolvedUrlResolver ?? (static () => EligibleUrl),
            credentialResolver ?? (static () => null),
            Log(),
            credentialHelperPath ?? (static () => "/helper"),
            onDispose ?? (static () => { }),
            pathCanonicalizer,
            stagingNonceGenerator,
            targetEntryExists,
            stagingMarkerCreateNew,
            stagingRepoChildCreate);

    /// <summary>A URL resolver returning a fixed value (possibly <c>null</c>).</summary>
    private static Func<string?> UrlResolver(string? url) => () => url;

    // ------------------------------------------------------------------
    // Execution-stage helpers (2c-b1b-ii)
    // ------------------------------------------------------------------

    /// <summary>
    /// The call-time working-directory spelling: <see cref="RepoDir"/> with a trailing
    /// separator — observably DIFFERENT from the constructor input (<see cref="RepoDir"/>)
    /// while still canonicalizing to the same directory.
    /// </summary>
    private static string RepoDirWithSeparator => RepoDir + Path.DirectorySeparatorChar;

    /// <summary>
    /// The DISTINCT canonical value produced by the canonicalizer seam used by the exact
    /// request tests. It differs from BOTH raw spellings (the constructor input
    /// <see cref="RepoDir"/> and the call-time <see cref="RepoDirWithSeparator"/>), so a
    /// request built from either raw value — a mutant that launches with the call-time
    /// workingDirectory or with the un-canonicalized constructor input — is observably
    /// different and fails the exact-request assertions.
    /// </summary>
    private static readonly string CanonicalizedRepoDir =
        Path.DirectorySeparatorChar + "canonical-config-repo";

    /// <summary>
    /// The four credential environment variable names that the shared
    /// <see cref="GitOperations.SanitizeChildEnv"/> ALWAYS strips and that must NEVER appear
    /// in a launched request's env. <c>GIT_TERMINAL_PROMPT</c> is deliberately NOT in this
    /// list: it is scrubbed from the inherited set and then FORCED to <c>"0"</c> in every
    /// child env (asserted separately).
    /// </summary>
    private static readonly string[] ScrubbedCredentialEnvNames =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GIT_ASKPASS",
        "GITHUB_CONFIG_REPO_TOKEN",
    ];

    /// <summary>A non-scrubbed inherited variable that must survive the scrub untouched.</summary>
    private const string NonScrubbedMarkerName = "CONFIG_REPO_SEAM_MARKER";

    /// <summary>
    /// Seeds ALL five scrubbed variables — including <c>GIT_TERMINAL_PROMPT</c> with a
    /// CONFLICTING non-zero value — plus a non-scrubbed marker. Returns the previous values
    /// for exact restoration.
    /// </summary>
    private static Dictionary<string, string?> SeedChildEnvVariables()
    {
        var previous = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = Environment.GetEnvironmentVariable("GH_TOKEN"),
            ["GITHUB_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            ["GIT_ASKPASS"] = Environment.GetEnvironmentVariable("GIT_ASKPASS"),
            ["GITHUB_CONFIG_REPO_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_CONFIG_REPO_TOKEN"),
            ["GIT_TERMINAL_PROMPT"] = Environment.GetEnvironmentVariable("GIT_TERMINAL_PROMPT"),
            [NonScrubbedMarkerName] = Environment.GetEnvironmentVariable(NonScrubbedMarkerName),
        };

        Environment.SetEnvironmentVariable("GH_TOKEN", "raw-gh-token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "raw-github-token");
        Environment.SetEnvironmentVariable("GIT_ASKPASS", "/usr/bin/askpass");
        Environment.SetEnvironmentVariable("GITHUB_CONFIG_REPO_TOKEN", "raw-config-repo-token");
        Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "1"); // conflicting inherited value
        Environment.SetEnvironmentVariable(NonScrubbedMarkerName, "marker-value");

        return previous;
    }

    private static void RestoreChildEnvVariables(Dictionary<string, string?> previous)
    {
        foreach (var (name, value) in previous)
            Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Builds the EXPECTED child environment for a request: the CURRENT process environment
    /// scrubbed via the SHARED five-variable <see cref="GitOperations.SanitizeChildEnv"/> plus
    /// EXACTLY <c>GIT_TERMINAL_PROMPT=0</c>. The caller seeds all five scrubbed variables
    /// (with a conflicting non-zero prompt) and a marker BEFORE invoking this, so full
    /// equality against the request env proves: every scrubbed variable absent, every
    /// non-scrubbed variable preserved, no extra entries, and the prompt forced to "0".
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ExpectedChildEnv()
    {
        var current = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key)
                continue;

            current[key] = entry.Value as string;
        }

        var expected = new Dictionary<string, string?>(GitOperations.SanitizeChildEnv(current));
        expected["GIT_TERMINAL_PROMPT"] = "0";
        return expected;
    }

    /// <summary>
    /// Asserts a launched request's environment EQUALS the complete scrubbed inherited
    /// snapshot plus <c>GIT_TERMINAL_PROMPT=0</c>: every scrubbed variable absent (the
    /// credential variables), every non-scrubbed variable preserved, no extra entries, and
    /// the prompt forced to <c>"0"</c> (overriding any conflicting inherited value). The
    /// explicit per-name absence checks document the scrub independently of the shared helper.
    /// </summary>
    private static void AssertChildEnv(GitProcessRequest request)
    {
        var expected = ExpectedChildEnv();
        Assert.Equal(expected.Count, request.Env.Count);
        foreach (var (key, value) in expected)
            Assert.Equal(value, request.Env[key]);

        foreach (var scrubbed in ScrubbedCredentialEnvNames)
            Assert.False(request.Env.ContainsKey(scrubbed), $"scrubbed variable '{scrubbed}' leaked");

        Assert.Equal("0", request.Env["GIT_TERMINAL_PROMPT"]);
    }

    /// <summary>
    /// Asserts an INJECTED request's environment equals the scrubbed inherited snapshot plus
    /// <c>GIT_TERMINAL_PROMPT=0</c> plus EXACTLY the two post-scrub exceptions
    /// (<c>GITHUB_CONFIG_REPO_TOKEN</c> and <c>GIT_ASKPASS</c>) with the expected values —
    /// nothing else. The counted equality proves the injection is additive and complete: the
    /// two variables were re-added AFTER the scrub (they are both in the scrubbed set) and no
    /// other inherited credential variable survived.
    /// </summary>
    private static void AssertChildEnvWithCredential(
        GitProcessRequest request, string credential, string helperPath)
    {
        var expected = new Dictionary<string, string?>(ExpectedChildEnv())
        {
            ["GITHUB_CONFIG_REPO_TOKEN"] = credential,
            ["GIT_ASKPASS"] = helperPath,
        };

        Assert.Equal(expected.Count, request.Env.Count);
        foreach (var (key, value) in expected)
            Assert.Equal(value, request.Env[key]);

        Assert.False(request.Env.ContainsKey("GH_TOKEN"));
        Assert.False(request.Env.ContainsKey("GITHUB_TOKEN"));
        Assert.Equal("0", request.Env["GIT_TERMINAL_PROMPT"]);
    }

    /// <summary>
    /// Asserts the launched requests are EXACTLY the given tokenized command sequence, in
    /// order — the count first, so a missing or extra launch fails loudly.
    /// </summary>
    private static void AssertSequence(
        IReadOnlyList<GitProcessRequest> requests, params string[][] expected)
    {
        Assert.Equal(expected.Length, requests.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], requests[i].TokenizedArgs!.ToArray());
    }

    // ------------------------------------------------------------------
    // Constructor validation
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_NullOrWhitespaceConfigRepoDir_Throws()
    {
        Assert.All(
            new[] { null, "", " ", "\t", "\n " },
            dir => Assert.Throws<ArgumentException>(() => new ConfigRepoGitOperations(
                dir!,
                static () => null,
                static () => null,
                Log(),
                static () => "/helper",
                static () => { })));
    }

    [Fact]
    public void Constructor_RelativePath_ThrowsBeforeCanonicalization() =>
        Assert.All(
            new[] { "config-repo", "./config-repo", "../config-repo" },
            dir => Assert.Throws<ArgumentException>(
                () => CreateSeam(configRepoDir: dir)));

    [Fact]
    public void Constructor_NonFullyQualifiedCheckPrecedesCanonicalizer_CanonicalizerNotInvoked()
    {
        var canonicalizerCalls = 0;
        var ex = Assert.Throws<ArgumentException>(() => new ConfigRepoGitOperations(
            "relative/dir",
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            _ => { canonicalizerCalls++; return "/x"; }));

        Assert.Equal(0, canonicalizerCalls);
        Assert.Equal("configRepoDir", Assert.IsType<ArgumentException>(ex).ParamName);
    }

    public static TheoryData<string, Func<Exception>> ThrowingCanonicalizers =>
        new()
        {
            { nameof(ArgumentException), static () => new ArgumentException("bad path") },
            { nameof(NotSupportedException), static () => new NotSupportedException("bad path") },
            { nameof(PathTooLongException), static () => new PathTooLongException("bad path") },
            { nameof(SecurityException), static () => new SecurityException("bad path") },
            { nameof(IOException), static () => new IOException("bad path") },
        };

    [Theory]
    [MemberData(nameof(ThrowingCanonicalizers))]
    public void Constructor_PathExceptionFromCanonicalizer_TranslatedToArgumentException(
        string exceptionTypeName, Func<Exception> factory)
    {
        Assert.False(string.IsNullOrEmpty(exceptionTypeName)); // the type name labels the case

        var ex = Assert.Throws<ArgumentException>(() => new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            _ => throw factory()));

        Assert.Equal(
            "Config repo directory could not be canonicalized. (Parameter 'configRepoDir')",
            ex.Message);
    }

    [Fact]
    public void Constructor_NonPathExceptionFromCanonicalizer_PropagatesAsIs()
    {
        var ex = Assert.Throws<OutOfMemoryException>(() => new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            _ => throw new OutOfMemoryException("boom")));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Constructor_DirectoryNeedNotExist_Succeeds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "config-repo-does-not-exist-" + Guid.NewGuid().ToString("N"));
        using var seam = new ConfigRepoGitOperations(
            dir, static () => null, static () => null, Log(), static () => "/helper", static () => { });
    }

    [Fact]
    public void ProductionConstructor_NullProvisioner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, (WorkerConfigProvisioner)null!, Log(), static () => "/helper", static () => { }));
    }

    [Fact]
    public void ProductionConstructor_NullLog_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, Provisioner(), null!, static () => "/helper", static () => { }));
    }

    [Fact]
    public void ProductionConstructor_NullCredentialHelperPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, Provisioner(), Log(), null!, static () => { }));
    }

    [Fact]
    public void ProductionConstructor_NullOnDispose_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, Provisioner(), Log(), static () => "/helper", null!));
    }

    [Fact]
    public void TestingConstructor_NullLog_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, static () => null, static () => null, null!, static () => "/helper", static () => { }));
    }

    [Fact]
    public void TestingConstructor_NullCredentialHelperPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, static () => null, static () => null, Log(), null!, static () => { }));
    }

    [Fact]
    public void TestingConstructor_NullOnDispose_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, static () => null, static () => null, Log(), static () => "/helper", null!));
    }

    [Fact]
    public void TestingConstructor_NullResolvers_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, (Func<string?>)null!, static () => null, Log(), static () => "/helper", static () => { }));
        Assert.Throws<ArgumentNullException>(() => new ConfigRepoGitOperations(
            RepoDir, static () => null, (Func<string?>)null!, Log(), static () => "/helper", static () => { }));
    }

    // ------------------------------------------------------------------
    // The URL resolver is read exactly once per TRANSPORT command (Stage 6a);
    // the CREDENTIAL delegates are read only AFTER origin verification succeeds
    // ------------------------------------------------------------------

    /// <summary>
    /// The URL resolver IS read — exactly once — for every TRANSPORT command (pull/push/fetch)
    /// that reaches Stage 6a, and never for a local command or for a command rejected before
    /// Stage 6a. The credential resolver and the credential-helper path are Stage 6e delegates
    /// and are read ONLY after the Stage 6d origin verification succeeded — with every launch
    /// failing here, neither is ever reached.
    /// </summary>
    [Fact]
    public async Task RunConfigRepoCommandAsync_CredentialDelegatesNotReachedWhenEveryLaunchFails()
    {
        var urlCalls = 0;
        var credentialCalls = 0;
        var helperCalls = 0;
        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            () => { urlCalls++; return EligibleUrl; },
            () => { credentialCalls++; return null; },
            Log(),
            () => { helperCalls++; return "/helper"; },
            static () => { });

        // The valid rows now REACH the real execution (slice 2c-b1b-ii), so the ProcessRunner
        // seam throws — the launch failure is mapped to a fixed message and the assertions
        // below hold for every row without ever starting a process.
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            foreach (var args in new[]
                     {
                         new[] { "pull", "origin", "main" },   // transport → Stage 6a
                         new[] { "push", "origin", "main" },   // transport → Stage 6a
                         new[] { "fetch", "--prune" },         // transport → Stage 6a
                         new[] { "status" },                   // local → Stage 6a SKIPPED
                         new[] { "https://x-access-token:tok@github.com/o" }, // Stage 4 rejection
                     })
            {
                var result = await seam.RunConfigRepoCommandAsync(args, RepoDir, CancellationToken.None);

                Assert.False(result.Success);
                Assert.Equal(-1, result.ExitCode);
                Assert.Equal("", result.Stdout);
                Assert.NotEqual("", result.SanitizedError);
            }
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }

        // EXACTLY one read per transport command; the local command and the Stage 4 rejection
        // never touch the resolver.
        Assert.Equal(3, urlCalls);

        // Stage 6e is gated behind Stage 6d: every eligible row failed its origin inspection
        // (or its ref validation), so neither credential delegate was ever reached.
        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    // ------------------------------------------------------------------
    // Stage ordering — the rejection table with EXACT messages
    // ------------------------------------------------------------------

    private static Task<ConfigRepoOpResult> RunAsync(
        ConfigRepoGitOperations seam, IReadOnlyList<string>? args) =>
        RunInAsync(seam, args, RepoDir);

    /// <summary>
    /// Sends <paramref name="workingDirectory"/> to the SUT VERBATIM — including an explicit
    /// <c>null</c>. Nothing is substituted here, otherwise the Stage 2b null row would never
    /// reach production.
    /// </summary>
    private static Task<ConfigRepoOpResult> RunInAsync(
        ConfigRepoGitOperations seam, IReadOnlyList<string>? args, string? workingDirectory) =>
        seam.RunConfigRepoCommandAsync(args!, workingDirectory!, CancellationToken.None);

    private static void AssertRejected(ConfigRepoOpResult result, string expectedError)
    {
        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal(expectedError, result.SanitizedError);
    }

    [Fact]
    public async Task Stage1_PostDisposal_ReturnsSeamDisposed()
    {
        using var seam = CreateSeam();
        seam.Dispose();

        AssertRejected(await RunAsync(seam, new[] { "pull", "origin", "main" }), "Seam disposed.");
    }

    [Fact]
    public async Task Stage2a_ArgsNull_ReturnsInvalidArguments() =>
        AssertRejected(await RunAsync(CreateSeam(), null!), InvalidArguments);

    [Fact]
    public async Task Stage2a_ArgsEmpty_ReturnsInvalidArguments() =>
        AssertRejected(await RunAsync(CreateSeam(), Array.Empty<string>()), InvalidArguments);

    // Stage 2b — the working directory is sent VERBATIM (the null row really sends null).
    // A [Theory] is used deliberately: an `Assert.All` with an async lambda binds to
    // Action<T> and becomes async void, so its assertions would never be awaited.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public async Task Stage2b_WorkingDirectoryNullOrWhitespace_ReturnsInvalidArguments(string? workingDirectory)
    {
        using var seam = CreateSeam();
        AssertRejected(
            await RunInAsync(seam, new[] { "status" }, workingDirectory),
            InvalidArguments);
    }

    /// <summary>
    /// Stage 2b must reject BEFORE any canonicalization is attempted, so a whitespace or null
    /// working directory can never reach the canonicalizer.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage2b_PrecedesCanonicalization_CanonicalizerNotInvokedForWorkingDirectory(
        string? workingDirectory)
    {
        var workingDirCanonicalizations = 0;
        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            p =>
            {
                if (p != RepoDir)
                    workingDirCanonicalizations++;
                return p;
            });

        AssertRejected(await RunInAsync(seam, new[] { "status" }, workingDirectory), InvalidArguments);
        Assert.Equal(0, workingDirCanonicalizations);
    }

    public static TheoryData<string?[]> NullElementCases => new()
    {
        new string?[] { "pull", null },
        new string?[] { null, "pull" },
        new string?[] { "pull", "origin", "main", null },
        new string?[] { null },
        new string?[] { "status", null },
    };

    [Theory]
    [MemberData(nameof(NullElementCases))]
    public async Task Stage2d_NullElementOverSnapshot_ReturnsInvalidArguments(string?[] args)
    {
        using var seam = CreateSeam();
        var result = await seam.RunConfigRepoCommandAsync(args!, RepoDir, CancellationToken.None);
        AssertRejected(result, InvalidArguments);
    }

    // ------------------------------------------------------------------
    // Stage 2 SUB-ORDER — adversarial instrumentation.
    // Stable arrays alone would still pass if the snapshot moved ahead of the empty-args or
    // whitespace-working-directory checks, or if call-time canonicalization moved ahead of
    // null-element validation. These cases pin each boundary.
    // ------------------------------------------------------------------

    /// <summary>
    /// An <see cref="IReadOnlyList{T}"/> that records how many times it was enumerated and how
    /// many times <see cref="Count"/> / the indexer were read, and can be made to THROW on
    /// enumeration. Any snapshot (<c>ToArray</c>) enumerates it exactly once.
    /// </summary>
    private sealed class InstrumentedArgs(IReadOnlyList<string?> items, bool throwOnEnumerate = false)
        : IReadOnlyList<string>
    {
        public int EnumerationCount { get; private set; }

        public int CountReads { get; private set; }

        public int Count
        {
            get
            {
                CountReads++;
                return items.Count;
            }
        }

        public string this[int index] => items[index]!;

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            if (throwOnEnumerate)
                throw new InvalidOperationException("the snapshot must not have been taken");

            foreach (var item in items)
                yield return item!;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Yields a null first, but records completion only after the entire sequence has been
    /// materialized. A premature Stage 2d scan would stop at the null and never reach the end;
    /// the required Stage 2c snapshot consumes the whole sequence before Stage 2d inspects it.
    /// </summary>
    private sealed class NullThenCompletionArgs : IReadOnlyList<string>
    {
        public bool ReachedEnd { get; private set; }

        public int Count => 2;

        public string this[int index] => index switch
        {
            0 => null!,
            1 => "ignored",
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<string> GetEnumerator()
        {
            yield return null!;
            yield return "ignored";
            ReachedEnd = true;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Stage 2a (args EMPTY) is decided BEFORE the Stage 2c snapshot: the list is never
    /// enumerated. A throwing enumerator makes a premature snapshot fail loudly.
    /// </summary>
    [Fact]
    public async Task Stage2a_EmptyArgs_RejectsBeforeTheSnapshotIsTaken()
    {
        using var seam = CreateSeam();
        var args = new InstrumentedArgs([], throwOnEnumerate: true);

        AssertRejected(await RunAsync(seam, args), InvalidArguments);
        Assert.Equal(0, args.EnumerationCount);
    }

    /// <summary>
    /// Stage 2b (whitespace working directory) is decided BEFORE the Stage 2c snapshot.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Stage2b_RejectsBeforeTheSnapshotIsTaken(string? workingDirectory)
    {
        using var seam = CreateSeam();
        var args = new InstrumentedArgs(["status"], throwOnEnumerate: true);

        AssertRejected(await RunInAsync(seam, args, workingDirectory), InvalidArguments);
        Assert.Equal(0, args.EnumerationCount);
    }

    /// <summary>
    /// Stage 2c fully materializes the snapshot BEFORE Stage 2d checks it for nulls. A scan of
    /// the source before snapshotting would stop on the first element and leave
    /// <see cref="NullThenCompletionArgs.ReachedEnd"/> false.
    /// </summary>
    [Fact]
    public async Task Stage2c_CompletesSnapshotBeforeStage2dNullElementValidation()
    {
        using var seam = CreateSeam();
        var args = new NullThenCompletionArgs();

        AssertRejected(await RunAsync(seam, args), InvalidArguments);
        Assert.True(args.ReachedEnd);
    }

    /// <summary>
    /// Stage 2c takes the snapshot EXACTLY ONCE — the source list is enumerated a single time
    /// no matter how deep the command travels through the pipeline. The accepted rows
    /// (<c>status</c>, <c>pull origin main</c>) now REACH the real execution, so the
    /// ProcessRunner seam throws (the launch failure is mapped to the fixed message).
    /// </summary>
    [Theory]
    [InlineData("status")]
    [InlineData("pull")]
    [InlineData("rebase")]
    public async Task Stage2c_SnapshotIsTakenExactlyOnce(string subcommand)
    {
        using var seam = CreateSeam();
        string?[] tokens = subcommand == "pull"
            ? ["pull", "origin", "main"]
            : [subcommand];
        var args = new InstrumentedArgs(tokens);

        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            await RunAsync(seam, args);

            Assert.Equal(1, args.EnumerationCount);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A MUTATING source list proves the snapshot is authoritative: everything downstream reads
    /// the snapshot, so post-snapshot mutation of the source cannot change the outcome. If the
    /// implementation re-read <c>args</c> instead of the snapshot, the mutated list would turn
    /// the accepted <c>status</c> into a form-mismatch rejection. (The accepted form now REACHES
    /// the real execution, so the ProcessRunner seam throws and the launch-failure result is
    /// returned instead of the removed placeholder.)
    /// </summary>
    [Fact]
    public async Task Stage2c_SnapshotIsAuthoritative_SourceMutationAfterSnapshotIsIgnored()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            var source = new MutatingArgs(["status"], mutation: ["status", "--porcelain"]);

            var result = await RunAsync(seam, source);

            Assert.Equal(LaunchFailed, result.SanitizedError);
            Assert.True(source.Mutated);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A list that REPLACES its contents the moment it is enumerated, so a second read of the
    /// source would observe different tokens than the snapshot did.
    /// </summary>
    private sealed class MutatingArgs(IReadOnlyList<string> initial, IReadOnlyList<string> mutation)
        : IReadOnlyList<string>
    {
        private IReadOnlyList<string> _items = initial;

        public bool Mutated { get; private set; }

        public int Count => _items.Count;

        public string this[int index] => _items[index];

        public IEnumerator<string> GetEnumerator()
        {
            var snapshot = _items;
            _items = mutation;
            Mutated = true;
            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Stage 2d (null ELEMENT) is decided BEFORE Stage 2e: the working-directory canonicalizer
    /// is never invoked when an element is null, even though the working directory would throw
    /// a path exception. If 2e ran first, the same "Invalid arguments." message would be
    /// produced — the canonicalizer call count is what distinguishes the two orderings.
    /// </summary>
    [Fact]
    public async Task Stage2d_PrecedesStage2e_CanonicalizerNotInvokedWhenAnElementIsNull()
    {
        var workingDirCanonicalizations = 0;
        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            p =>
            {
                if (p == RepoDir)
                    return p;
                workingDirCanonicalizations++;
                throw new IOException("bad path");
            });

        var result = await seam.RunConfigRepoCommandAsync(
            new string?[] { "pull", null }!, "/somewhere/else", CancellationToken.None);

        AssertRejected(result, InvalidArguments);
        Assert.Equal(0, workingDirCanonicalizations);
    }

    /// <summary>
    /// Stage 2d runs over the SNAPSHOT, not over the source list: a source that hides its null
    /// behind a mutation is still rejected because the snapshot captured the null.
    /// </summary>
    [Fact]
    public async Task Stage2d_ValidatesTheSnapshotNotTheSource()
    {
        using var seam = CreateSeam();
        var source = new MutatingArgs(["pull", null!], mutation: ["status"]);

        AssertRejected(await RunAsync(seam, source), InvalidArguments);
        Assert.True(source.Mutated);
    }

    public static TheoryData<string, Func<Exception>> ThrowingCanonicalizersForCallTime =>
        new()
        {
            { nameof(ArgumentException), static () => new ArgumentException("bad path") },
            { nameof(NotSupportedException), static () => new NotSupportedException("bad path") },
            { nameof(PathTooLongException), static () => new PathTooLongException("bad path") },
            { nameof(SecurityException), static () => new SecurityException("bad path") },
            { nameof(IOException), static () => new IOException("bad path") },
        };

    [Theory]
    [MemberData(nameof(ThrowingCanonicalizersForCallTime))]
    public async Task Stage2e_PathExceptionFromCanonicalizerAtCallTime_ReturnsInvalidArguments(
        string exceptionTypeName, Func<Exception> factory)
    {
        Assert.False(string.IsNullOrEmpty(exceptionTypeName)); // the type name labels the case

        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            p => p == RepoDir ? RepoDir : throw factory());

        // Construction canonicalizes configRepoDir; the call canonicalizes workingDirectory.
        AssertRejected(await RunInAsync(seam, new[] { "status" }, OutsideDir), InvalidArguments);
    }

    /// <summary>
    /// The call-time canonicalization boundary uses the SAME five-exception filter with NO
    /// catch-all: a non-path exception (the required <see cref="OutOfMemoryException"/> vector)
    /// propagates AS-IS instead of being translated into "Invalid arguments.".
    /// </summary>
    [Fact]
    public async Task Stage2e_OutOfMemoryExceptionFromCanonicalizerAtCallTime_PropagatesAsIs()
    {
        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            p => p == RepoDir ? RepoDir : throw new OutOfMemoryException("boom"));

        var ex = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => RunInAsync(seam, new[] { "status" }, OutsideDir));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Stage2e_NonPathExceptionFromCanonicalizerAtCallTime_PropagatesAsIs()
    {
        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            static () => { },
            p => p == RepoDir ? RepoDir : throw new InvalidOperationException("not a path problem"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunInAsync(seam, new[] { "status" }, OutsideDir));

        Assert.Equal("not a path problem", ex.Message);
    }

    // Stage 3 — containment is EQUALITY-only: a DESCENDANT of the config repo is rejected too.
    // A [Theory] is used deliberately (an async lambda inside Assert.All is async void).
    public static TheoryData<string> OutsideWorkingDirectoryCases
    {
        get
        {
            var data = new TheoryData<string>
            {
                Path.Combine(RepoDir, "sub"),           // descendant — MUST be rejected
                Path.Combine(RepoDir, "sub", "dir"),    // deeper descendant
                Path.Combine(RepoDir, ".git"),          // descendant
                Path.GetTempPath(),                     // unrelated
            };

            // A sibling whose canonical path shares the config repo as a STRING PREFIX: this
            // is what makes a StartsWith-based containment check fail the suite.
            data.Add(RepoDir + "-sibling");
            data.Add(Path.GetDirectoryName(RepoDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!); // the PARENT
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OutsideWorkingDirectoryCases))]
    public async Task Stage3_WorkingDirectoryOutside_ReturnsContainmentRejection(string workingDirectory)
    {
        using var seam = CreateSeam();
        AssertRejected(await RunInAsync(seam, new[] { "status" }, workingDirectory), NotConfigRepo);
    }

    public static TheoryData<string, string> UnknownSubcommandCases => new()
    {
        { "https://x-access-token:tok@github.com/o", "Invalid git command: unknown subcommand 'https://github.com/o'." },
        { "rebase", "Invalid git command: unknown subcommand 'rebase'." },
        { "Pull", "Invalid git command: unknown subcommand 'Pull'." },
        { "", "Invalid git command: unknown subcommand ''." },
    };

    [Theory]
    [MemberData(nameof(UnknownSubcommandCases))]
    public async Task Stage4_UnknownSubcommand_ReturnsUnknownSubcommandMessage(
        string subcommand, string expected)
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, new[] { subcommand }), expected);
    }

    // The FINAL vector table — exact messages per the acceptance matrix.
    public static TheoryData<string[], string> ClassificationVectorCases => new()
    {
        { new[] { "pull", "origin", "--rebase" }, "Invalid git command: options must precede positionals." },
        { new[] { "pull", "origin", "main", "--rebase" }, "Invalid git command: options must precede positionals." },
        { new[] { "push", "origin", "-bad" }, "Invalid git ref: '-bad'." },
        { new[] { "pull", "origin", "-bad" }, "Invalid git ref: '-bad'." },
        { new[] { "pull", "origin", "--squash" }, "Invalid git ref: '--squash'." },
        { new[] { "pull", "origin", "main", "--squash" }, "Invalid git command: too many arguments." },
        { new[] { "pull", "--squash", "origin" }, "Invalid git command: unknown option '--squash'." },
    };

    [Theory]
    [MemberData(nameof(ClassificationVectorCases))]
    public async Task Stage5_ClassificationVectors_ReturnExactMessage(
        string[] args, string expected)
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, args), expected);
    }

    public static TheoryData<string[], string> PushPrecedenceCases => new()
    {
        { new[] { "push" }, "Invalid git command: push requires 'origin <ref>'." },
        { new[] { "push", "origin" }, "Invalid git command: push requires 'origin <ref>'." },
        { new[] { "push", "badremote" }, "Invalid git command: push requires 'origin <ref>'." },
        { new[] { "push", "badremote", "main" }, "Invalid git command: the remote must be 'origin'." },
        { new[] { "push", "-bad", "origin" }, "Invalid git command: unknown option '-bad'." },
    };

    [Theory]
    [MemberData(nameof(PushPrecedenceCases))]
    public async Task Stage5_PushPrecedenceMatrix_ReturnsExactMessage(string[] args, string expected)
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, args), expected);
    }

    [Fact]
    public async Task Stage5_DuplicateOption_ReturnsDuplicateOptionMessage()
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, new[] { "pull", "--tags", "--tags" }),
            "Invalid git command: duplicate option '--tags'.");
    }

    [Fact]
    public async Task Stage5_ConflictingOptionPair_ReturnsMutuallyExclusiveMessage()
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, new[] { "pull", "--rebase", "--no-rebase" }),
            "Invalid git command: --rebase and --no-rebase are mutually exclusive.");
        AssertRejected(await RunAsync(seam, new[] { "pull", "--no-rebase", "--rebase" }),
            "Invalid git command: --rebase and --no-rebase are mutually exclusive.");
    }

    [Fact]
    public async Task Stage5_DepthFailures_ReturnPositiveIntegerMessage()
    {
        using var seam = CreateSeam();
        foreach (var args in new[]
                 {
                     new[] { "pull", "--depth" },                 // missing value
                     new[] { "pull", "--depth", "--tags" },        // option-like value
                     new[] { "pull", "--depth", "0" },
                     new[] { "pull", "--depth", "-5" },
                     new[] { "pull", "--depth", " 5" },
                     new[] { "pull", "--depth", "5 " },
                     new[] { "pull", "--depth", "99999999999999999999" }, // overflow
                     new[] { "pull", "--depth", "0x10" },          // hex
                     new[] { "pull", "--depth", "" },
                     new[] { "fetch", "--depth", "abc" },
                 })
        {
            AssertRejected(await RunAsync(seam, args),
                "Invalid git command: --depth requires a positive integer.");
        }
    }

    [Fact]
    public async Task Stage5_MalformedCredentialFreeForms_ReturnFormMismatchMessage()
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, new[] { "checkout", "agents/" }),
            "Invalid git command: the arguments do not match the allowed form for 'checkout'.");
        AssertRejected(await RunAsync(seam, new[] { "add", "other.txt" }),
            "Invalid git command: the arguments do not match the allowed form for 'add'.");
        AssertRejected(await RunAsync(seam, new[] { "diff", "--cached" }),
            "Invalid git command: the arguments do not match the allowed form for 'diff'.");
        AssertRejected(await RunAsync(seam, new[] { "commit", "-m", "" }),
            "Invalid git command: the arguments do not match the allowed form for 'commit'.");
        AssertRejected(await RunAsync(seam, new[] { "merge" }),
            "Invalid git command: the arguments do not match the allowed form for 'merge'.");
        AssertRejected(await RunAsync(seam, new[] { "status", "--porcelain" }),
            "Invalid git command: the arguments do not match the allowed form for 'status'.");
        AssertRejected(await RunAsync(seam, new[] { "commit", "-m" }),
            "Invalid git command: the arguments do not match the allowed form for 'commit'.");
    }

    public static TheoryData<string[]> RefPrecheckFailureCases => new()
    {
        new[] { "pull", "origin", "a..b" },
        new[] { "pull", "origin", "a...b" },
        new[] { "pull", "origin", "https://github.com/o/r" },
        new[] { "pull", "origin", "-bad" },
        new[] { "pull", "origin", "+ref" },
        new[] { "pull", "origin", "" },
        new[] { "pull", "origin", " " },
        new[] { "pull", "origin", "re f" },
        new[] { "pull", "origin", "re\tf" },
        // Non-whitespace CONTROL characters: tab is both control AND whitespace, so these are
        // what make the char.IsControl check independently removal-proof.
        new[] { "pull", "origin", "re\u0001f" },
        new[] { "pull", "origin", "re\u007Ff" },
        new[] { "pull", "origin", "\u0001" },
        new[] { "pull", "origin", "*" },
        new[] { "pull", "origin", "v*" },
        new[] { "push", "origin", "a..b" },
        new[] { "push", "origin", "re\u0001f" },
        new[] { "fetch", "origin", "re f" },
        new[] { "fetch", "origin", "re\u0001f" },
    };

    [Theory]
    [MemberData(nameof(RefPrecheckFailureCases))]
    public async Task Stage6_RefPrecheckFailures_ReturnRedactedRefMessage(string[] args)
    {
        using var seam = CreateSeam();
        var refToken = args[^1];
        var expected = $"Invalid git ref: '{refToken}'.";

        AssertRejected(await RunAsync(seam, args), expected);
    }

    /// <summary>
    /// Independent proof that the CONTROL-character rule exists on its own: these refs contain
    /// a non-whitespace control character and NOTHING else that any other precheck rejects
    /// (no <c>..</c>, no <c>://</c>, no leading <c>-</c>/<c>+</c>, no <c>*</c>, non-empty, and
    /// no whitespace). Deleting <c>char.IsControl</c> makes them reach the placeholder.
    /// </summary>
    [Theory]
    [InlineData("\u0001")]
    [InlineData("\u007F")]
    [InlineData("\u001B")]
    [InlineData("\u0000")]
    public async Task Stage6_NonWhitespaceControlCharacterInRef_IsRejected(string control)
    {
        Assert.True(char.IsControl(control[0]));
        Assert.False(char.IsWhiteSpace(control[0])); // the case is NOT covered by the whitespace rule

        using var seam = CreateSeam();
        var refToken = "main" + control;

        AssertRejected(await RunAsync(seam, new[] { "pull", "origin", refToken }),
            $"Invalid git ref: '{refToken}'.");
    }

    /// <summary>
    /// The seam's RAW rejected-ref contract: a ref carrying a NEWLINE is rejected by the
    /// Stage 6b character scan, and the returned <see cref="ConfigRepoOpResult.SanitizedError"/>
    /// embeds that newline VERBATIM. "Sanitized" here means REDACTED
    /// (<see cref="CopilotHive.Services.GitUrlRedactor"/>) — the seam deliberately performs NO
    /// control-character pass, because raw fidelity is what its own callers assert against.
    /// Sanitizing control characters for a log line is the CALLER's boundary (TaskExecutor),
    /// not the seam's.
    /// </summary>
    [Fact]
    public async Task Stage6_NewlineBearingRef_IsRejectedWithTheRawNewlineInTheError()
    {
        using var seam = CreateSeam();
        const string refToken = "bad\nref";

        var result = await RunAsync(seam, new[] { "pull", "--ff-only", "origin", refToken });

        AssertRejected(result, $"Invalid git ref: '{refToken}'.");

        // The newline survives the seam's redaction VERBATIM — the seam does not
        // control-sanitize, so the raw value is exactly what a caller receives.
        Assert.Contains("\n", result.SanitizedError, StringComparison.Ordinal);
        Assert.Contains(refToken, result.SanitizedError, StringComparison.Ordinal);

        // Proof the value really is un-sanitized: routing it through the log-sanitization
        // boundary would CHANGE it.
        Assert.NotEqual(
            result.SanitizedError,
            CopilotHive.Services.LogSanitizer.SanitizeText(result.SanitizedError));
    }

    // ------------------------------------------------------------------
    // Stage 5 STRICTLY PRECEDES Stage 6 — the ref prechecks run only after the WHOLE
    // grammar scan completed. A Stage 6 message appearing for any of these rows means the
    // ref was validated mid-scan.
    // ------------------------------------------------------------------

    public static TheoryData<string[], string> Stage5BeatsStage6Cases => new()
    {
        // A later Stage 5 "too many arguments" beats an earlier bad ref candidate.
        { new[] { "pull", "origin", "+bad", "extra" }, "Invalid git command: too many arguments." },
        { new[] { "pull", "origin", "a..b", "extra" }, "Invalid git command: too many arguments." },
        { new[] { "pull", "origin", "-bad", "extra" }, "Invalid git command: too many arguments." },
        { new[] { "pull", "origin", "*", "extra" }, "Invalid git command: too many arguments." },
        { new[] { "push", "origin", "a..b", "extra" }, "Invalid git command: too many arguments." },
        { new[] { "fetch", "origin", "+bad", "extra" }, "Invalid git command: too many arguments." },
        // The post-scan remote-identity check is still Stage 5 and beats the ref prechecks.
        { new[] { "pull", "badremote", "+bad" }, "Invalid git command: the remote must be 'origin'." },
        { new[] { "pull", "badremote", "a..b" }, "Invalid git command: the remote must be 'origin'." },
        { new[] { "fetch", "badremote", "re f" }, "Invalid git command: the remote must be 'origin'." },
        { new[] { "push", "badremote", "a..b" }, "Invalid git command: the remote must be 'origin'." },
        // A KNOWN option after positionals is a Stage 5 misplacement, never a ref candidate.
        { new[] { "pull", "origin", "a..b", "--rebase" }, "Invalid git command: options must precede positionals." },
    };

    [Theory]
    [MemberData(nameof(Stage5BeatsStage6Cases))]
    public async Task Stage5_CompletesBeforeStage6_GrammarErrorWins(string[] args, string expected)
    {
        using var seam = CreateSeam();
        var result = await RunAsync(seam, args);

        AssertRejected(result, expected);
        Assert.DoesNotContain("Invalid git ref", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The converse of <see cref="Stage5_CompletesBeforeStage6_GrammarErrorWins"/>: when the
    /// Stage 5 scan completes cleanly, Stage 6 DOES reject the selected ref candidate — the
    /// ordering fix must not turn the ref prechecks into dead code.
    /// </summary>
    public static TheoryData<string[], string> Stage6RunsAfterCleanScanCases => new()
    {
        { new[] { "pull", "origin", "-bad" }, "Invalid git ref: '-bad'." },
        { new[] { "push", "origin", "-bad" }, "Invalid git ref: '-bad'." },
        { new[] { "pull", "origin", "--squash" }, "Invalid git ref: '--squash'." },
        { new[] { "pull", "--tags", "origin", "+bad" }, "Invalid git ref: '+bad'." },
        { new[] { "fetch", "--depth", "3", "origin", "a..b" }, "Invalid git ref: 'a..b'." },
    };

    [Theory]
    [MemberData(nameof(Stage6RunsAfterCleanScanCases))]
    public async Task Stage6_RunsAfterACleanStage5Scan(string[] args, string expected)
    {
        using var seam = CreateSeam();
        AssertRejected(await RunAsync(seam, args), expected);
    }

    // ------------------------------------------------------------------
    // Grammar acceptance — the allowed forms (all reach the real execution)
    // ------------------------------------------------------------------

    /// <summary>
    /// Every accepted form, paired with the message a THROWING ProcessRunner produces at the
    /// FIRST launch that form reaches. The first launch differs by shape (2c-b1c-ii):
    /// <list type="bullet">
    ///   <item><description>
    ///   a REF-bearing form reaches the Stage 6b <c>check-ref-format</c> subprocess first →
    ///   <c>Git process failed to start.</c>;
    ///   </description></item>
    ///   <item><description>
    ///   an ELIGIBLE ref-less transport form reaches the Stage 6d origin INSPECTION first →
    ///   <c>Config repo origin could not be verified.</c>;
    ///   </description></item>
    ///   <item><description>
    ///   a LOCAL form reaches its final command first → <c>Git process failed to start.</c>.
    ///   </description></item>
    /// </list>
    /// </summary>
    public static TheoryData<string[], string> AcceptedCommandCases => new()
    {
        // pull — options in every subset/order; positionals [origin] [ref].
        { new[] { "pull" }, OriginNotVerified },
        { new[] { "pull", "origin" }, OriginNotVerified },
        { new[] { "pull", "origin", "main" }, LaunchFailed },
        { new[] { "pull", "--ff-only" }, OriginNotVerified },
        { new[] { "pull", "--no-rebase", "--ff-only" }, OriginNotVerified },
        { new[] { "pull", "--rebase", "--tags", "--prune" }, OriginNotVerified },
        { new[] { "pull", "--prune", "--depth", "1", "origin", "main" }, LaunchFailed },
        { new[] { "pull", "--depth", "10", "--tags" }, OriginNotVerified },
        { new[] { "pull", "origin", "v1.0" }, LaunchFailed },
        // fetch — options and positionals.
        { new[] { "fetch" }, OriginNotVerified },
        { new[] { "fetch", "origin" }, OriginNotVerified },
        { new[] { "fetch", "--tags", "--prune" }, OriginNotVerified },
        { new[] { "fetch", "--depth", "2", "origin" }, OriginNotVerified },
        { new[] { "fetch", "--prune", "origin", "main" }, LaunchFailed },
        // push — the exact form.
        { new[] { "push", "origin", "main" }, LaunchFailed },
        // The credential-free EXACT shapes (local commands — no origin state machine).
        { new[] { "checkout", "--", "agents/" }, LaunchFailed },
        { new[] { "add", "agents/*.agents.md" }, LaunchFailed },
        { new[] { "diff", "--cached", "--name-only", "-z" }, LaunchFailed },
        { new[] { "commit", "-m", "update agents" }, LaunchFailed },
        { new[] { "merge", "--abort" }, LaunchFailed },
        { new[] { "status" }, LaunchFailed },
    };

    /// <summary>
    /// Every accepted form reaches the real execution — the ProcessRunner seam throws, and the
    /// launch failure surfaces as the FIXED result for whichever launch that form reaches
    /// first. (The placeholder was removed by slice 2c-b1b-ii; the accepted-form grammar
    /// contract is now asserted through the execution path.)
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedCommandCases))]
    public async Task Stage7_AcceptedForms_ReachExecution_LaunchFailureReturnsFixedMessage(
        string[] args, string expected)
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            var result = await RunAsync(seam, args);

            AssertRejected(result, expected);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    [Fact]
    public async Task Stage7_DepthPlus5_IsAccepted()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "pull", "--depth", "+5" });

            // An eligible, ref-less pull: the Stage 6d origin inspection is the first launch.
            AssertRejected(result, OriginNotVerified);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // Cancellation PROPAGATES at the first launch reached (2c-b1b-ii)
    // ------------------------------------------------------------------

    /// <summary>
    /// A command WITH a ref observes the cancellation at Stage 6 — the ref-validation
    /// subprocess is the FIRST <c>ExecuteProcessAsync</c> reached.
    /// </summary>
    [Fact]
    public async Task Stage6_CancelledToken_PropagatesAtTheRefValidationSubprocess()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.RunConfigRepoCommandAsync(
                    new[] { "pull", "origin", "main" }, RepoDir, cts.Token));

            Assert.Equal(cts.Token, ex.CancellationToken);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A command WITHOUT a ref (e.g. <c>status</c>) observes the cancellation at Stage 7 —
    /// the first (and only) <c>ExecuteProcessAsync</c> reached.
    /// </summary>
    [Fact]
    public async Task Stage7_CancelledToken_PropagatesAtTheFinalExecution()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.RunConfigRepoCommandAsync(
                    new[] { "status" }, RepoDir, cts.Token));

            Assert.Equal(cts.Token, ex.CancellationToken);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // Execution stage (2c-b1b-ii) — the EXACT GitProcessRequests
    // ------------------------------------------------------------------

    /// <summary>
    /// The ref-validation subprocess request: tokenized args EXACTLY
    /// <c>check-ref-format --allow-onelevel &lt;ref&gt;</c>, empty <c>Args</c>, the
    /// CONSTRUCTOR-canonicalized working directory (NOT the call-time spelling), and an env
    /// EXACTLY equal to the scrubbed inherited snapshot plus <c>GIT_TERMINAL_PROMPT=0</c> —
    /// every one of the FIVE scrubbed variables (GH_TOKEN, GITHUB_TOKEN, GIT_ASKPASS,
    /// GITHUB_CONFIG_REPO_TOKEN, GIT_TERMINAL_PROMPT) absent, every non-scrubbed inherited
    /// variable preserved, no extra entries. The exit-0 subprocess lets the command continue
    /// into the Stage 6d origin inspection and then the final execution, which must carry
    /// the SNAPSHOT.
    /// </summary>
    [Fact]
    public async Task Stage6_RefValidationRequest_HasExactShape()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var previousEnv = SeedChildEnvVariables();
        var requests = new List<GitProcessRequest>();

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));
            };

            // Constructor input: the raw RepoDir; call-time spelling: the trailing-separator
            // form. The canonicalizer seam maps BOTH to the DISTINCT CanonicalizedRepoDir —
            // a request built from either raw spelling (constructor input or call-time
            // string) is observably different and fails the WorkingDirectory assertion.
            using var seam = CreateSeam(pathCanonicalizer: _ => CanonicalizedRepoDir);
            var result = await RunInAsync(seam, new[] { "pull", "origin", "main" }, RepoDirWithSeparator);

            // Exit 0 from check-ref-format: the command continues through the Stage 6d origin
            // inspection (an equivalent, credential-free origin needs NO repair) to Stage 7.
            Assert.True(result.Success);
            AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
                ["pull", "origin", "main"]);

            var refValidation = requests[0];
            Assert.Equal("git", refValidation.Executable);
            Assert.Empty(refValidation.Args);
            Assert.Equal(CanonicalizedRepoDir, refValidation.WorkingDirectory);
            Assert.NotEqual(RepoDir, CanonicalizedRepoDir);       // the spellings are distinct
            Assert.NotEqual(RepoDirWithSeparator, CanonicalizedRepoDir);

            // Env = the COMPLETE scrubbed inherited snapshot + GIT_TERMINAL_PROMPT=0 for the
            // ref validation, the origin inspection AND the final command (no credential was
            // resolved, so nothing is injected anywhere).
            foreach (var request in requests)
            {
                Assert.Equal(CanonicalizedRepoDir, request.WorkingDirectory);
                AssertChildEnv(request);
            }
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// A non-zero exit from the ref-validation subprocess REJECTS the ref — and the final
    /// command is NEVER launched (exactly one <c>ExecuteProcessAsync</c> invocation).
    /// </summary>
    [Fact]
    public async Task Stage6_RefValidationNonZeroExit_RejectsRef()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invocations = 0;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
            {
                invocations++;
                return Task.FromResult(new GitProcessResult(2, "ignored", "ignored"));
            };

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "pull", "origin", "main" });

            AssertRejected(result, "Invalid git ref: 'main'.");
            Assert.Equal(1, invocations); // Stage 7 never reached
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A launch failure at Stage 6 (throwing ProcessRunner delegate) maps to the FIXED
    /// message — the exception's own text is never propagated — and Stage 7 is never reached.
    /// </summary>
    [Fact]
    public async Task Stage6_RefValidationLaunchFailure_ReturnsFixedMessage()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invocations = 0;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
            {
                invocations++;
                throw new InvalidOperationException("git executable missing");
            };

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "pull", "origin", "main" });

            AssertRejected(result, LaunchFailed);
            Assert.Equal(1, invocations);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // Execution stage (2c-b1b-ii) — the final-execution request and result mapping
    // ------------------------------------------------------------------

    /// <summary>
    /// The final-execution request for a REF-LESS command (<c>status</c>): the ONLY launch.
    /// Tokenized args = the snapshot verbatim; empty <c>Args</c>; the CONSTRUCTOR-canonicalized
    /// working directory (NOT the call-time spelling); and an env EXACTLY equal to the
    /// scrubbed inherited snapshot plus <c>GIT_TERMINAL_PROMPT=0</c> — all FIVE scrubbed
    /// variables absent, non-scrubbed variables preserved, no extra entries, and NO
    /// credential injection at the final launch.
    /// </summary>
    [Fact]
    public async Task Stage7_FinalExecutionRequest_HasExactShape()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var previousEnv = SeedChildEnvVariables();
        GitProcessRequest? captured = null;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));
            };

            // Constructor input: the raw RepoDir; call-time spelling: the trailing-separator
            // form. The canonicalizer seam maps BOTH to the DISTINCT CanonicalizedRepoDir —
            // a request built from the call-time string is observably different.
            using var seam = CreateSeam(pathCanonicalizer: _ => CanonicalizedRepoDir);
            var result = await RunInAsync(seam, new[] { "status" }, RepoDirWithSeparator);

            Assert.True(result.Success);
            Assert.NotNull(captured);
            Assert.Equal("git", captured!.Executable);
            Assert.Empty(captured.Args);
            Assert.Equal(CanonicalizedRepoDir, captured.WorkingDirectory);
            Assert.NotEqual(RepoDir, CanonicalizedRepoDir);       // the spellings are distinct
            Assert.NotEqual(RepoDirWithSeparator, CanonicalizedRepoDir);
            Assert.Equal(new[] { "status" }, captured.TokenizedArgs!.ToArray());

            // Env = the COMPLETE scrubbed inherited snapshot + GIT_TERMINAL_PROMPT=0.
            AssertChildEnv(captured);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// Exit 0 → <c>Success=true, ExitCode=0, Stdout=redacted stdout, SanitizedError=""</c>.
    /// </summary>
    [Fact]
    public async Task Stage7_ExitZero_MapsToSuccessWithRedactedStdout()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) => Task.FromResult(
                new GitProcessResult(0, "clean stdout", "ignored stderr"));

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "status" });

            Assert.True(result.Success);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("clean stdout", result.Stdout);
            Assert.Equal("", result.SanitizedError);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A git failure (exit ≠ 0, in range) → <c>Success=false</c>, the exit code preserved
    /// VERBATIM, stdout preserved, and stderr redacted-then-TrimEnd'd.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(255)]
    public async Task Stage7_NonZeroExitInRange_PreservesExitCodeAndMapsRedactedTrimmedError(int exitCode)
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) => Task.FromResult(
                new GitProcessResult(exitCode, "some stdout", "fatal: bad thing  \n"));

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "status" });

            Assert.False(result.Success);
            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal("some stdout", result.Stdout);
            Assert.Equal("fatal: bad thing", result.SanitizedError); // redacted, TrimEnd'd
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// Exit codes OUTSIDE 0-255 are mapped to -1 (the clamp). In-range codes are preserved
    /// verbatim (covered by <see cref="Stage7_NonZeroExitInRange_PreservesExitCodeAndMapsRedactedTrimmedError"/>).
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(300)]
    [InlineData(-1)]
    [InlineData(-5)]
    public async Task Stage7_ExitCodeOutsideByteRange_MappedToMinusOne(int exitCode)
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) => Task.FromResult(
                new GitProcessResult(exitCode, "stdout", "stderr"));

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "status" });

            Assert.False(result.Success);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal("stdout", result.Stdout);
            Assert.Equal("stderr", result.SanitizedError);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A throwing ProcessRunner delegate at Stage 7 → the catch-ALL:
    /// <c>SanitizedError="Git process failed to start."</c> with <c>Stdout=""</c> and
    /// <c>ExitCode=-1</c>. The exception's own text is NEVER propagated. (A throwing delegate
    /// is also covered for Stage 6 by <see cref="Stage6_RefValidationLaunchFailure_ReturnsFixedMessage"/>.)
    /// </summary>
    [Fact]
    public async Task Stage7_FinalExecutionLaunchFailure_ReturnsFixedMessageWithEmptyStdout()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "status" });

            AssertRejected(result, LaunchFailed);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // Cancellation PRECEDENCE (TCS-gated) — the first launch reached wins
    // ------------------------------------------------------------------

    /// <summary>
    /// A command WITH a ref observes the cancellation at Stage 6: the FIRST
    /// <c>ExecuteProcessAsync</c> reached is the ref-validation subprocess; the token is
    /// cancelled while the delegate is BLOCKED on the gate; the OCE propagates and Stage 7
    /// is never reached.
    /// </summary>
    [Fact]
    public async Task CancellationPrecedence_RefBearingCommand_CancellationObservedAtStage6()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<ConfigRepoOpResult> execution = null!;

        try
        {
            GitOperations.ProcessRunner = async (request, ct) =>
            {
                requests.Add(request);
                entered.TrySetResult();
                try
                {
                    await gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // The delegate owns its own cancellation semantics: normalize any
                    // cancellation (TaskCanceledException included) to an exact
                    // OperationCanceledException carrying the caller's token.
                    throw new OperationCanceledException(ct);
                }

                return new GitProcessResult(0, string.Empty, string.Empty);
            };

            using var seam = CreateSeam();
            execution = seam.RunConfigRepoCommandAsync(
                new[] { "pull", "origin", "main" }, RepoDir, cts.Token);

            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The FIRST launch reached is the ref-validation subprocess — NOT the command.
            Assert.Single(requests);
            Assert.Equal(
                new[] { "check-ref-format", "--allow-onelevel", "main" },
                requests[0].TokenizedArgs!.ToArray());

            cts.Cancel();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
            Assert.Equal(cts.Token, ex.CancellationToken);

            // Stage 7 was never reached.
            Assert.Single(requests);
        }
        finally
        {
            // Settle the outstanding operation BEFORE restoring the static seam so a
            // failing assertion cannot leave a blocked delegate that later advances to
            // Stage 7 with the real (un-faked) runner installed.
            gate.TrySetResult();
            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
                // Expected — the operation was cancelled.
            }

            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> thrown by the fake ProcessRunner delegate
    /// ITSELF — with the caller token LIVE (not pre-cancelled, no gated cancellation) —
    /// propagates UNCONDITIONALLY from Stage 6 (the ref-validation subprocess, the FIRST
    /// launch a ref-bearing command reaches). An implementation that only rethrows OCE when
    /// the token is cancelled but maps a delegate-thrown OCE (live token) to the fixed
    /// launch-failure result FAILS this test.
    /// </summary>
    [Fact]
    public async Task Stage6_DelegateThrowsOceWithLiveToken_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invoked = 0;
        var delegateOce = new OperationCanceledException("delegate cancelled");

        try
        {
            GitOperations.ProcessRunner = (_, _) =>
            {
                invoked++;
                throw delegateOce;
            };

            using var seam = CreateSeam();
            using var liveCts = new CancellationTokenSource(); // token stays LIVE

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.RunConfigRepoCommandAsync(
                    new[] { "pull", "origin", "main" }, RepoDir, liveCts.Token));

            // The DELEGATE was actually reached (not a pre-launch token check) and its OCE
            // propagated verbatim — the exact instance, never the fixed launch-failure result.
            Assert.Same(delegateOce, ex);
            Assert.Equal(1, invoked);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A command WITHOUT a ref (<c>status</c>) observes the cancellation at Stage 7: the
    /// FIRST (and only) <c>ExecuteProcessAsync</c> reached is the final execution.
    /// </summary>
    [Fact]
    public async Task CancellationPrecedence_ReflessCommand_CancellationObservedAtStage7()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<ConfigRepoOpResult> execution = null!;

        try
        {
            GitOperations.ProcessRunner = async (request, ct) =>
            {
                requests.Add(request);
                entered.TrySetResult();
                try
                {
                    await gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // The delegate owns its own cancellation semantics: normalize any
                    // cancellation (TaskCanceledException included) to an exact
                    // OperationCanceledException carrying the caller's token.
                    throw new OperationCanceledException(ct);
                }

                return new GitProcessResult(0, string.Empty, string.Empty);
            };

            using var seam = CreateSeam();
            execution = seam.RunConfigRepoCommandAsync(
                new[] { "status" }, RepoDir, cts.Token);

            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Single(requests);
            Assert.Equal(new[] { "status" }, requests[0].TokenizedArgs!.ToArray());

            cts.Cancel();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
            Assert.Equal(cts.Token, ex.CancellationToken);

            Assert.Single(requests);
        }
        finally
        {
            // Settle the outstanding operation BEFORE restoring the static seam so a
            // failing assertion cannot leave a blocked delegate that later completes with
            // the real (un-faked) runner installed.
            gate.TrySetResult();
            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
                // Expected — the operation was cancelled.
            }

            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> thrown by the fake ProcessRunner delegate
    /// ITSELF — with the caller token LIVE (not pre-cancelled, no gated cancellation) —
    /// propagates UNCONDITIONALLY from Stage 7 (the final execution, the first and only
    /// launch a ref-less command reaches). An implementation that only rethrows OCE when the
    /// token is cancelled but maps a delegate-thrown OCE (live token) to the fixed
    /// launch-failure result FAILS this test.
    /// </summary>
    [Fact]
    public async Task Stage7_DelegateThrowsOceWithLiveToken_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invoked = 0;
        var delegateOce = new OperationCanceledException("delegate cancelled");

        try
        {
            GitOperations.ProcessRunner = (_, _) =>
            {
                invoked++;
                throw delegateOce;
            };

            using var seam = CreateSeam();
            using var liveCts = new CancellationTokenSource(); // token stays LIVE

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.RunConfigRepoCommandAsync(
                    new[] { "status" }, RepoDir, liveCts.Token));

            // The DELEGATE was actually reached (not a pre-launch token check) and its OCE
            // propagated verbatim — the exact instance, never the fixed launch-failure result.
            Assert.Same(delegateOce, ex);
            Assert.Equal(1, invoked);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A Stage 3 containment rejection returns its result REGARDLESS of the token state —
    /// the token is only observed at the first <c>ExecuteProcessAsync</c>, which is never
    /// reached here.
    /// </summary>
    [Fact]
    public async Task CancellationPrecedence_ContainmentRejection_ReturnsResultRegardlessOfTokenState()
    {
        using var seam = CreateSeam();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await seam.RunConfigRepoCommandAsync(
            new[] { "status" }, OutsideDir, cts.Token);

        AssertRejected(result, NotConfigRepo);
    }

    /// <summary>
    /// A Stage 5 grammar rejection returns its result REGARDLESS of the token state.
    /// </summary>
    [Fact]
    public async Task CancellationPrecedence_GrammarRejection_ReturnsResultRegardlessOfTokenState()
    {
        using var seam = CreateSeam();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await seam.RunConfigRepoCommandAsync(
            new[] { "status", "--porcelain" }, RepoDir, cts.Token);

        AssertRejected(result,
            "Invalid git command: the arguments do not match the allowed form for 'status'.");
    }

    /// <summary>
    /// A Stage 6 ref PRECHECK rejection (no subprocess) returns its result REGARDLESS of the
    /// token state — the subprocess is only launched after the prechecks pass.
    /// </summary>
    [Fact]
    public async Task CancellationPrecedence_RefPrecheckRejection_ReturnsResultRegardlessOfTokenState()
    {
        using var seam = CreateSeam();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await seam.RunConfigRepoCommandAsync(
            new[] { "pull", "origin", "-bad" }, RepoDir, cts.Token);

        AssertRejected(result, "Invalid git ref: '-bad'.");
    }

    // ------------------------------------------------------------------
    // The TCS-gated defensive snapshot (mutation while in flight)
    // ------------------------------------------------------------------

    /// <summary>
    /// The caller passes a <see cref="List{T}"/> as the <see cref="IReadOnlyList{T}"/>. The
    /// fake runner BLOCKS during the ref-validation subprocess; the caller MUTATES the list
    /// while blocked; after release the final execution must use the SNAPSHOT — the mutated
    /// tokens NEVER reach the launched command. A production implementation that re-read the
    /// caller's list at Stage 7 would launch the mutated tokens and fail this test.
    /// </summary>
    [Fact]
    public async Task Stage2c_DefensiveSnapshot_MutationWhileBlocked_NeverReachesTheLaunch()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var refValidationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalExecutionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ConfigRepoOpResult> execution = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                var tokens = request.TokenizedArgs!;
                if (tokens[0] == "check-ref-format")
                {
                    // The ref-validation subprocess blocks until the caller mutates the list.
                    refValidationEntered.TrySetResult();
                    return releaseRefValidation.Task.ContinueWith(
                        _ => new GitProcessResult(0, string.Empty, string.Empty));
                }

                if (tokens[0] == "remote")
                {
                    // The Stage 6d origin inspection: a PRESENT, equivalent, credential-free
                    // origin — no repair follows.
                    return Task.FromResult(new GitProcessResult(0, EligibleUrl, string.Empty));
                }

                finalExecutionEntered.TrySetResult();
                return releaseFinalExecution.Task.ContinueWith(
                    _ => new GitProcessResult(0, "executed", string.Empty));
            };

            using var seam = CreateSeam();
            var args = new List<string> { "pull", "origin", "main" };
            execution = seam.RunConfigRepoCommandAsync(args, RepoDir, CancellationToken.None);

            // The ref-validation subprocess is in flight — mutate the caller's list NOW.
            await refValidationEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            args[2] = "MUTATED-REF";

            releaseRefValidation.TrySetResult();
            await finalExecutionEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            releaseFinalExecution.TrySetResult();

            var result = await execution;

            // The final execution launched the SNAPSHOT, never the mutated tokens.
            Assert.True(result.Success);
            AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
                ["pull", "origin", "main"]);
            Assert.DoesNotContain("MUTATED-REF",
                requests.SelectMany(r => r.TokenizedArgs!), StringComparer.Ordinal);
        }
        finally
        {
            // Settle the outstanding operation BEFORE restoring the static seam so a failing
            // assertion cannot leave a blocked delegate that later advances to Stage 7 with
            // the real (un-faked) runner installed.
            releaseRefValidation.TrySetResult();
            releaseFinalExecution.TrySetResult();
            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
                // Expected only if the test failed mid-flight.
            }

            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // The TCS-gated disposal-vs-in-flight contract
    // ------------------------------------------------------------------

    /// <summary>
    /// An operation that has ALREADY begun (past the Stage 1 disposed-check) completes
    /// NORMALLY — disposal is checked ONLY at entry, and this test proves the contract
    /// ACROSS the Stage 6b→6d→Stage 7 boundary: the operation is gated while BLOCKED at
    /// Stage 6b (the ref-validation subprocess of a ref-bearing command);
    /// <see cref="ConfigRepoGitOperations.Dispose"/> is called while it is blocked there; the
    /// Stage 6b gate is released; the test then asserts the Stage 6d origin inspection AND
    /// Stage 7 STILL LAUNCH (a mutant that rechecks <c>_disposed</c> after Stage 6b would
    /// return <c>Seam disposed.</c> and never launch them); the in-flight operation returns
    /// its REAL result (not <c>Seam disposed.</c>), while a SUBSEQUENT call returns exactly
    /// <c>ConfigRepoOpResult(false, -1, "", "Seam disposed.")</c>. It also proves the Stage 6c
    /// semaphore is NEVER disposed by <c>Dispose()</c>: an in-flight operation holding it must
    /// be able to release it after disposal without faulting.
    /// </summary>
    [Fact]
    public async Task Disposal_InFlightOperationCompletesNormally_SubsequentCallReturnsSeamDisposed()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var stage6Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStage6 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage7Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStage7 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ConfigRepoOpResult> execution = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                var tokens = request.TokenizedArgs!;
                if (tokens[0] == "check-ref-format")
                {
                    // Block DURING Stage 6b (the ref-validation subprocess) — AFTER the
                    // Stage 1 disposed-check has already passed.
                    stage6Entered.TrySetResult();
                    return releaseStage6.Task.ContinueWith(
                        _ => new GitProcessResult(0, string.Empty, string.Empty));
                }

                if (tokens[0] == "remote")
                    return Task.FromResult(new GitProcessResult(0, EligibleUrl, string.Empty));

                stage7Entered.TrySetResult();
                return releaseStage7.Task.ContinueWith(
                    _ => new GitProcessResult(0, "in-flight stdout", string.Empty));
            };

            var seam = CreateSeam();
            execution = seam.RunConfigRepoCommandAsync(
                new[] { "pull", "origin", "main" }, RepoDir, CancellationToken.None);

            // The operation is IN FLIGHT at Stage 6b (past the entry disposed-check) — dispose.
            await stage6Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            seam.Dispose();

            // Release Stage 6b — Stage 6d and Stage 7 MUST STILL LAUNCH (disposal is NOT
            // rechecked, and the Stage 6c semaphore was NOT disposed underneath the operation).
            releaseStage6.TrySetResult();
            await stage7Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            releaseStage7.TrySetResult();

            var result = await execution;

            // All THREE launches happened — the ref validation, the origin inspection and the
            // final execution.
            AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
                ["pull", "origin", "main"]);

            // The in-flight operation COMPLETES NORMALLY with its REAL result.
            Assert.True(result.Success);
            Assert.Equal("in-flight stdout", result.Stdout);
            Assert.Equal("", result.SanitizedError);

            // A SUBSEQUENT call is rejected at entry — the EXACT post-disposal result.
            AssertRejected(await seam.RunConfigRepoCommandAsync(
                new[] { "status" }, RepoDir, CancellationToken.None), "Seam disposed.");
        }
        finally
        {
            // Settle the outstanding operation BEFORE restoring the static seam so a failing
            // assertion cannot leave a blocked delegate that later advances to Stage 7 with
            // the real (un-faked) runner installed.
            releaseStage6.TrySetResult();
            releaseStage7.TrySetResult();
            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
                // Expected only if the test failed mid-flight.
            }

            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // The redaction boundary over PROCESS OUTPUT (universal-redaction rule)
    // ------------------------------------------------------------------

    /// <summary>
    /// A credential-bearing URL in the process STDOUT is redacted in <see cref="ConfigRepoOpResult.Stdout"/>.
    /// </summary>
    [Fact]
    public async Task RedactionBoundary_StdoutWithCredentialUrl_IsRedacted()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) => Task.FromResult(
                new GitProcessResult(0,
                    "https://x-access-token:tok@github.com/org/repo.git stdout",
                    string.Empty));

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "status" });

            Assert.True(result.Success);
            Assert.Equal("https://github.com/org/repo.git stdout", result.Stdout);
            Assert.DoesNotContain("tok", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A credential-bearing URL in the process STDERR is redacted and THEN TrimEnd'd in
    /// <see cref="ConfigRepoOpResult.SanitizedError"/> — redaction FIRST, trim second.
    /// </summary>
    [Fact]
    public async Task RedactionBoundary_StderrWithCredentialUrl_IsRedactedAndTrimmed()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) => Task.FromResult(
                new GitProcessResult(1,
                    string.Empty,
                    "fatal: https://x-access-token:tok@github.com/org/repo.git failed  \n"));

            using var seam = CreateSeam();
            var result = await RunAsync(seam, new[] { "status" });

            Assert.False(result.Success);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("fatal: https://github.com/org/repo.git failed", result.SanitizedError);
            Assert.DoesNotContain("tok", result.SanitizedError, StringComparison.Ordinal);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // Universal redaction — the CORRECTED vector
    // ------------------------------------------------------------------

    [Fact]
    public async Task UniversalRedaction_UrlAsSubcommand_IsRedactedInReturnedError()
    {
        using var seam = CreateSeam();
        var result = await RunAsync(seam, new[] { "https://x-access-token:tok@github.com/o" });

        AssertRejected(result,
            "Invalid git command: unknown subcommand 'https://github.com/o'.");
    }

    [Fact]
    public async Task UniversalRedaction_UrlAsFirstPositional_RemoteMustBeOriginAndRedacted()
    {
        using var seam = CreateSeam();
        var result = await RunAsync(seam, new[] { "pull", "https://x-access-token:tok@github.com/o" });

        AssertRejected(result,
            "Invalid git command: the remote must be 'origin'.");
    }

    [Fact]
    public async Task UniversalRedaction_UrlInRefPrecheck_IsRedacted()
    {
        using var seam = CreateSeam();
        var result = await RunAsync(seam, new[] { "pull", "origin", "https://x-access-token:tok@github.com/o/r" });

        AssertRejected(result, "Invalid git ref: 'https://github.com/o/r'.");
    }

    // ------------------------------------------------------------------
    // Stage 6a (slice 2c-b1c-i) — URL resolution, eligibility, canonicalization
    // ------------------------------------------------------------------

    /// <summary>An absolute local path used as an INELIGIBLE resolved config repo URL.</summary>
    private static readonly string LocalPathUrl = OperatingSystem.IsWindows()
        ? @"C:\srv\config-repo.git"
        : "/srv/config-repo.git";

    /// <summary>A <c>file:</c> URL used as an INELIGIBLE resolved config repo URL.</summary>
    private static readonly string FileUrl = OperatingSystem.IsWindows()
        ? "file:///C:/srv/config-repo.git"
        : "file:///srv/config-repo.git";

    /// <summary>Bounds an await so a mutant can never block the suite forever.</summary>
    private static Task<T> Bounded<T>(Task<T> task) =>
        task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs a command against a RECORDING ProcessRunner (restored in a finally block) and
    /// returns the result together with EVERY captured request, so the exact invocation TOTAL
    /// — not merely the last request's shape — can be asserted.
    /// <para>
    /// The Stage 6d origin commands are answered separately from the command under test:
    /// <c>remote get-url origin</c> reports <paramref name="originStdout"/> (defaulting to the
    /// configured <see cref="EligibleUrl"/> — a PRESENT, credential-free, fully equivalent
    /// origin, so no <c>remote add</c>/<c>set-url</c> follows), and every other
    /// <c>remote …</c> command succeeds. <paramref name="exitCode"/> applies to the
    /// ref-validation subprocess and the final command only.
    /// </para>
    /// </summary>
    private static async Task<(ConfigRepoOpResult Result, List<GitProcessRequest> Requests)> RunCapturingAsync(
        Func<string?> resolvedUrlResolver,
        string[] args,
        int exitCode = 0,
        Func<string?>? credentialResolver = null,
        string? originStdout = EligibleUrl,
        Func<string>? credentialHelperPath = null)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(OriginAwareResult(request, exitCode, originStdout));
            };

            using var seam = CreateSeam(
                resolvedUrlResolver: resolvedUrlResolver,
                credentialResolver: credentialResolver,
                credentialHelperPath: credentialHelperPath);
            var result = await Bounded(
                seam.RunConfigRepoCommandAsync(args, RepoDir, CancellationToken.None));
            return (result, requests);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// The shared fake response: the Stage 6d origin commands always succeed (the inspection
    /// reporting <paramref name="originStdout"/>), everything else reports
    /// <paramref name="exitCode"/>.
    /// </summary>
    private static GitProcessResult OriginAwareResult(
        GitProcessRequest request, int exitCode, string? originStdout)
    {
        var tokens = request.TokenizedArgs!;
        if (tokens.Count > 0 && tokens[0] == "remote")
        {
            var stdout = tokens.Count > 1 && tokens[1] == "get-url"
                ? originStdout ?? string.Empty
                : string.Empty;
            return new GitProcessResult(0, stdout, string.Empty);
        }

        return new GitProcessResult(exitCode, string.Empty, string.Empty);
    }

    /// <summary>
    /// ELIGIBLE resolved URLs: HTTPS, host <c>github.com</c> (case-insensitively), effective
    /// port 443 — implicit, or EXPLICIT <c>:443</c>.
    /// </summary>
    public static TheoryData<string> EligibleUrlCases => new()
    {
        "https://github.com/org/config-repo.git",
        "https://github.com:443/org/config-repo.git",
        "https://GITHUB.COM/org/config-repo.git",
        "https://GitHub.Com/org/config-repo.git",
    };

    /// <summary>
    /// An eligible URL makes a POSITIONAL-FREE pull the CANONICALIZED explicit-origin launch.
    /// Branch A always runs the Stage 6d origin INSPECTION first; the fixture reports an
    /// already-equivalent, credential-free origin, so no repair follows and the sequence is
    /// exactly inspection → the canonicalized command.
    /// </summary>
    [Theory]
    [MemberData(nameof(EligibleUrlCases))]
    public async Task Stage6a_EligibleUrl_BarePull_LaunchesExplicitOrigin(string url)
    {
        // The origin reported by the fixture must be the SANITIZED form of the resolved URL,
        // otherwise the equivalence check would (correctly) reject it.
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(url), ["pull"], originStdout: ConfigRepoUrlSanitizer.Sanitize(url));

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect, ["pull", "origin"]);
    }

    /// <summary>
    /// INELIGIBLE resolved URLs — every one of them is a value the sanitizer ACCEPTS, so the
    /// command still runs; it simply launches the snapshot VERBATIM (Branch B).
    /// </summary>
    public static TheoryData<string> IneligibleUrlCases => new()
    {
        "https://github.com:8443/org/config-repo.git",  // explicit NON-443 port
        "https://github.com:8080/org/config-repo.git",
        "ssh://git@github.com/org/config-repo.git",     // ssh
        // ssh on the HTTPS default port: host github.com AND effective port 443, so ONLY the
        // scheme rule keeps it ineligible.
        "ssh://git@github.com:443/org/config-repo.git",
        "git@github.com:org/config-repo.git",           // scp-style → ssh
        FileUrl,                                        // file:
        LocalPathUrl,                                   // bare local path
    };

    /// <summary>
    /// A Branch B (ineligible) transport command launches the SNAPSHOT verbatim — no
    /// <c>origin</c> is inserted.
    /// </summary>
    [Theory]
    [MemberData(nameof(IneligibleUrlCases))]
    public async Task Stage6a_IneligibleUrl_BarePull_LaunchesSnapshotVerbatim(string url)
    {
        var (result, requests) = await RunCapturingAsync(UrlResolver(url), ["pull"]);

        Assert.True(result.Success);
        Assert.Single(requests);
        Assert.Equal(new[] { "pull" }, requests[0].TokenizedArgs!.ToArray());
    }

    /// <summary>
    /// Every ineligible vector really IS accepted by the sanitizer — otherwise the verbatim
    /// launch above would be passing for the wrong reason (a Sanitize rejection instead of a
    /// Branch B launch).
    /// </summary>
    [Theory]
    [MemberData(nameof(IneligibleUrlCases))]
    public void Stage6a_IneligibleUrlVectors_AreAcceptedBySanitizer(string url) =>
        Assert.False(string.IsNullOrWhiteSpace(ConfigRepoUrlSanitizer.Sanitize(url)));

    /// <summary>
    /// The full canonicalization table for ELIGIBLE transport commands: a positional-free
    /// pull/fetch gets the literal <c>origin</c> APPENDED as the remote argument; a form that
    /// already carries positionals — and EVERY push form — launches VERBATIM.
    /// </summary>
    public static TheoryData<string[], string[]> CanonicalizationCases => new()
    {
        // Positional-free pull/fetch → the explicit origin is INSERTED.
        { ["pull"], ["pull", "origin"] },
        { ["pull", "--ff-only"], ["pull", "--ff-only", "origin"] },
        { ["pull", "--rebase", "--tags", "--prune"], ["pull", "--rebase", "--tags", "--prune", "origin"] },
        { ["pull", "--depth", "10", "--tags"], ["pull", "--depth", "10", "--tags", "origin"] },
        { ["fetch"], ["fetch", "origin"] },
        { ["fetch", "--tags", "--prune"], ["fetch", "--tags", "--prune", "origin"] },
        // Forms that ALREADY have positionals launch VERBATIM.
        { ["pull", "origin"], ["pull", "origin"] },
        { ["fetch", "origin"], ["fetch", "origin"] },
        { ["fetch", "--depth", "2", "origin"], ["fetch", "--depth", "2", "origin"] },
        { ["pull", "--prune", "--depth", "1", "origin"], ["pull", "--prune", "--depth", "1", "origin"] },
    };

    [Theory]
    [MemberData(nameof(CanonicalizationCases))]
    public async Task Stage6a_EligibleUrl_CanonicalizesPositionalFreeFormsOnly(
        string[] args, string[] expectedLaunch)
    {
        var (result, requests) = await RunCapturingAsync(UrlResolver(EligibleUrl), args);

        Assert.True(result.Success);
        // EXACTLY two launches: the Stage 6d origin inspection (the origin is already
        // equivalent and credential-free, so no repair follows) and the final command. No ref
        // candidate, so no check-ref-format and no duplicate final launch.
        AssertSequence(requests, OriginInspect, expectedLaunch);
    }

    /// <summary>
    /// The very same positional-free forms launch VERBATIM under Branch B — proving the
    /// insertion is gated on ELIGIBILITY and not applied unconditionally.
    /// </summary>
    [Theory]
    [InlineData("pull")]
    [InlineData("fetch")]
    public async Task Stage6a_IneligibleUrl_PositionalFreeForm_LaunchesVerbatim(string subcommand)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver("https://github.com:8443/org/config-repo.git"), [subcommand, "--tags"]);

        Assert.True(result.Success);
        Assert.Single(requests);
        Assert.Equal(new[] { subcommand, "--tags" }, requests[0].TokenizedArgs!.ToArray());
    }

    /// <summary>
    /// A ref-bearing form launches the ref-validation subprocess FIRST, then the Stage 6d
    /// origin inspection, then the SNAPSHOT verbatim — exactly three launches, and the
    /// <c>origin</c> insertion never applies.
    /// </summary>
    [Theory]
    [InlineData("pull")]
    [InlineData("fetch")]
    [InlineData("push")]
    public async Task Stage6a_EligibleUrl_RefBearingForm_LaunchesSnapshotVerbatim(string subcommand)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), [subcommand, "origin", "main"]);

        Assert.True(result.Success);
        AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
            [subcommand, "origin", "main"]);
    }

    /// <summary>
    /// The launches carry the SAME child env as every other launch: the scrubbed inherited
    /// snapshot plus <c>GIT_TERMINAL_PROMPT=0</c>, and NOTHING else — with a null credential
    /// (the default resolver) NOTHING is ever injected, on either branch.
    /// </summary>
    [Theory]
    [InlineData(EligibleUrl)]
    [InlineData("ssh://git@github.com/org/config-repo.git")]
    public async Task Stage6a_LaunchEnv_IsScrubbedEnvPlusTerminalPromptOnly(string url)
    {
        var previousEnv = SeedChildEnvVariables();
        try
        {
            var (result, requests) = await RunCapturingAsync(UrlResolver(url), ["pull"]);

            Assert.True(result.Success);
            Assert.NotEmpty(requests);
            foreach (var request in requests)
                AssertChildEnv(request);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    // ── Stage 6a rejections ───────────────────────────────────────────────

    /// <summary>
    /// A null/whitespace resolved URL rejects the transport command with the fixed message —
    /// the command NEVER runs (ZERO subprocess launches).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData("\n ")]
    public async Task Stage6a_MissingResolvedUrl_RejectsWithoutLaunching(string? url)
    {
        foreach (var args in new[]
                 {
                     new[] { "pull" },
                     new[] { "fetch", "--tags" },
                     new[] { "push", "origin", "main" },
                 })
        {
            var (result, requests) = await RunCapturingAsync(UrlResolver(url), args);

            AssertRejected(result, UrlUnavailable);
            Assert.Empty(requests);
        }
    }

    /// <summary>
    /// Vectors the sanitizer REJECTS, with the EXACT surfaced message: the fixed
    /// <c>Invalid config repo URL: </c> prefix plus the sanitizer's full (already redacted)
    /// message.
    /// </summary>
    public static TheoryData<string, string> SanitizeRejectedUrlCases => new()
    {
        {
            "https://x-access-token:ghp_supersecret@github.com/org/config-repo.git",
            "https URL carries userinfo credentials, which are not allowed"
        },
        {
            "https://evil.example.com/org/config-repo.git",
            "host must be exactly 'github.com'"
        },
        {
            "ftp://github.com/org/config-repo.git",
            "unsupported scheme (only https, ssh and file are allowed)"
        },
        {
            "ssh://mallory@github.com/org/config-repo.git",
            "ssh URL username must be exactly 'git'"
        },
        {
            "relative/config-repo.git",
            "relative local paths are not allowed (use an absolute path)"
        },
        {
            "https://github.com/org/config-repo.git?token=secret",
            "URL must not contain a query string"
        },
    };

    [Theory]
    [MemberData(nameof(SanitizeRejectedUrlCases))]
    public async Task Stage6a_SanitizeRejectedUrl_ReturnsInvalidConfigRepoUrlMessage(
        string url, string expectedReason)
    {
        var sanitizerMessage =
            "Invalid --config-repo value: "
            + expectedReason
            + ". (The supplied value is redacted because it may contain credentials.)";

        // The vector really IS a Sanitize rejection, and its message is exactly the reason —
        // so the assertion below cannot pass for the wrong reason.
        var rejection = Assert.ThrowsAny<ArgumentException>(() => ConfigRepoUrlSanitizer.Sanitize(url));
        Assert.Equal(sanitizerMessage, rejection.Message);

        var (result, requests) = await RunCapturingAsync(UrlResolver(url), ["pull"]);

        AssertRejected(result, "Invalid config repo URL: " + sanitizerMessage);
        Assert.Empty(requests);                       // the command NEVER runs
        Assert.DoesNotContain("ghp_supersecret", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// ANY non-cancellation exception from the URL resolver maps to the FIXED
    /// <c>Config repo not provisioned.</c> message — the resolver's own text (the production
    /// provisioner throws <see cref="InvalidOperationException"/> when the provisioning
    /// snapshot is absent) NEVER escapes, and nothing is launched.
    /// </summary>
    [Theory]
    [InlineData("pull")]
    [InlineData("fetch")]
    [InlineData("push")]
    public async Task Stage6a_ThrowingUrlResolver_ReturnsNotProvisionedWithoutLaunching(string subcommand)
    {
        string[] args = subcommand == "push" ? [subcommand, "origin", "main"] : [subcommand];

        foreach (var thrower in new Func<string?>[]
                 {
                     static () => throw new InvalidOperationException(
                         "config repo provisioning snapshot for https://x-access-token:ghp_leak@github.com/o is absent"),
                     static () => throw new NullReferenceException("resolver blew up"),
                     static () => throw new TimeoutException("resolver timed out"),
                 })
        {
            var (result, requests) = await RunCapturingAsync(thrower, args);

            AssertRejected(result, NotProvisioned);
            Assert.Empty(requests);
            Assert.DoesNotContain("ghp_leak", result.SanitizedError, StringComparison.Ordinal);
            Assert.DoesNotContain("absent", result.SanitizedError, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> thrown by the URL resolver PROPAGATES
    /// unconditionally — it is NEVER mapped to <c>Config repo not provisioned.</c> — and no
    /// subprocess is launched. The caller token stays LIVE, so a mutant that only rethrows
    /// when the token is cancelled still fails.
    /// </summary>
    [Fact]
    public async Task Stage6a_UrlResolverThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invocations = 0;
        var resolverOce = new OperationCanceledException("resolver cancelled");

        try
        {
            GitOperations.ProcessRunner = (_, _) =>
            {
                invocations++;
                return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));
            };

            using var seam = CreateSeam(resolvedUrlResolver: () => throw resolverOce);
            using var liveCts = new CancellationTokenSource(); // the token stays LIVE

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => Bounded(seam.RunConfigRepoCommandAsync(
                    new[] { "pull", "origin", "main" }, RepoDir, liveCts.Token)));

            Assert.Same(resolverOce, ex);
            Assert.Equal(0, invocations); // Stage 6 never reached
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 6a SEQUENCING — mutation-resistant ─────────────────────────

    /// <summary>
    /// The adversarial vector: a Stage 6a failure combined with a ref that WOULD reach the
    /// check-ref-format subprocess. The URL error WINS and NOT A SINGLE subprocess is launched.
    /// A mutant that moves Stage 6a after the ref validation launches one subprocess and fails.
    /// </summary>
    public static TheoryData<Func<string?>, string> Stage6aBeatsStage6Cases => new()
    {
        { static () => null, UrlUnavailable },
        { static () => "   ", UrlUnavailable },
        {
            static () => "https://x-access-token:ghp_supersecret@github.com/org/config-repo.git",
            "Invalid config repo URL: Invalid --config-repo value: https URL carries userinfo "
            + "credentials, which are not allowed. (The supplied value is redacted because it "
            + "may contain credentials.)"
        },
        { static () => throw new InvalidOperationException("not provisioned"), NotProvisioned },
    };

    [Theory]
    [MemberData(nameof(Stage6aBeatsStage6Cases))]
    public async Task Stage6a_PrecedesStage6_UrlErrorWinsAndNoSubprocessLaunches(
        Func<string?> resolver, string expected)
    {
        // Exit 2 would REJECT the ref — but the subprocess must never run at all.
        var (result, requests) = await RunCapturingAsync(
            resolver, ["pull", "origin", "main"], exitCode: 2);

        AssertRejected(result, expected);
        Assert.Empty(requests); // ZERO check-ref-format launches
        Assert.DoesNotContain("Invalid git ref", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stage 6a also precedes the PURE ref prechecks (the part of Stage 6 that needs no
    /// subprocess): with a failing Stage 6a the URL error wins over <c>Invalid git ref</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stage6aBeatsStage6Cases))]
    public async Task Stage6a_PrecedesTheRefPrechecks_UrlErrorWins(
        Func<string?> resolver, string expected)
    {
        var (result, requests) = await RunCapturingAsync(resolver, ["pull", "origin", "-bad"]);

        AssertRejected(result, expected);
        Assert.Empty(requests);
        Assert.DoesNotContain("Invalid git ref", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The converse for the pure prechecks: with a VALID eligible URL the very same command
    /// surfaces the ref precheck rejection and still launches NOTHING.
    /// </summary>
    [Fact]
    public async Task Stage6a_ValidUrl_RefPrecheckStillRejects()
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull", "origin", "-bad"]);

        AssertRejected(result, "Invalid git ref: '-bad'.");
        Assert.Empty(requests);
    }

    /// <summary>
    /// The converse: with a VALID eligible URL the very same command DOES reach Stage 6 and
    /// the ref rejection surfaces after EXACTLY ONE check-ref-format subprocess. Without this
    /// row a mutant that rejects every transport command at Stage 6a would survive.
    /// </summary>
    [Fact]
    public async Task Stage6a_ValidUrl_Stage6StillRejectsTheRefAfterExactlyOneSubprocess()
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull", "origin", "main"], exitCode: 2);

        AssertRejected(result, "Invalid git ref: 'main'.");
        Assert.Single(requests);
        Assert.Equal(
            new[] { "check-ref-format", "--allow-onelevel", "main" },
            requests[0].TokenizedArgs!.ToArray());
    }

    /// <summary>
    /// Stage 5 STRICTLY PRECEDES Stage 6a: a grammar rejection wins and the URL resolver is
    /// NEVER read. The resolver THROWS, so a mutant that resolved the URL first would surface
    /// <c>Config repo not provisioned.</c> instead of the grammar message.
    /// </summary>
    public static TheoryData<string[], string> Stage5BeatsStage6aCases => new()
    {
        { ["pull", "main"], "Invalid git command: the remote must be 'origin'." },
        { ["fetch", "badremote"], "Invalid git command: the remote must be 'origin'." },
        { ["push"], "Invalid git command: push requires 'origin <ref>'." },
        { ["pull", "--squash"], "Invalid git command: unknown option '--squash'." },
        { ["pull", "--tags", "--tags"], "Invalid git command: duplicate option '--tags'." },
    };

    [Theory]
    [MemberData(nameof(Stage5BeatsStage6aCases))]
    public async Task Stage5_PrecedesStage6a_GrammarErrorWinsAndUrlResolverIsNotRead(
        string[] args, string expected)
    {
        var urlCalls = 0;
        var (result, requests) = await RunCapturingAsync(
            () => { urlCalls++; throw new InvalidOperationException("resolver must not be read"); },
            args);

        AssertRejected(result, expected);
        Assert.Equal(0, urlCalls);
        Assert.Empty(requests);
    }

    /// <summary>
    /// The Stage 5 grammar is UNCHANGED by this slice: the bare <c>pull</c>/<c>fetch</c> forms
    /// remain ACCEPTED (they reach the canonicalized launch) while <c>[pull, main]</c> remains
    /// a validated rejection — with a VALID eligible URL supplied, so the rejection cannot be
    /// a Stage 6a artefact.
    /// </summary>
    [Fact]
    public async Task Stage5_GrammarUnchanged_BareFormsAcceptedAndBadRemoteStillRejected()
    {
        var (bareResult, bareRequests) = await RunCapturingAsync(UrlResolver(EligibleUrl), ["pull"]);
        Assert.True(bareResult.Success);
        AssertSequence(bareRequests, OriginInspect, ["pull", "origin"]);

        var (badRemote, badRemoteRequests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull", "main"]);
        AssertRejected(badRemote, "Invalid git command: the remote must be 'origin'.");
        Assert.Empty(badRemoteRequests);
    }

    /// <summary>
    /// LOCAL commands skip Stage 6a ENTIRELY: a THROWING URL resolver still lets every local
    /// form succeed, and the resolver is never read.
    /// </summary>
    public static TheoryData<string[]> LocalCommandCases => new()
    {
        new[] { "checkout", "--", "agents/" },
        new[] { "add", "agents/*.agents.md" },
        new[] { "diff", "--cached", "--name-only", "-z" },
        new[] { "commit", "-m", "update agents" },
        new[] { "merge", "--abort" },
        new[] { "status" },
    };

    [Theory]
    [MemberData(nameof(LocalCommandCases))]
    public async Task Stage6a_LocalCommand_NeverReadsTheUrlResolver(string[] args)
    {
        var urlCalls = 0;
        var (result, requests) = await RunCapturingAsync(
            () => { urlCalls++; throw new InvalidOperationException("the resolver must not be read"); },
            args);

        Assert.True(result.Success);
        Assert.Equal(0, urlCalls);
        Assert.Single(requests);
        Assert.Equal(args, requests[0].TokenizedArgs!.ToArray()); // verbatim, never canonicalized
    }

    /// <summary>
    /// The URL resolver is read EXACTLY ONCE per transport command, no matter how many
    /// subprocesses the command launches. The totals are the migrated Branch A sequences: an
    /// eligible ref-less form launches the origin inspection + the command; a ref-bearing form
    /// launches the ref validation + the origin inspection + the command.
    /// </summary>
    [Theory]
    [InlineData(new[] { "pull" }, 2)]
    [InlineData(new[] { "pull", "origin", "main" }, 3)]
    [InlineData(new[] { "push", "origin", "main" }, 3)]
    [InlineData(new[] { "fetch", "--tags" }, 2)]
    public async Task Stage6a_UrlResolverIsReadExactlyOnce(string[] args, int expectedLaunches)
    {
        var urlCalls = 0;
        var (result, requests) = await RunCapturingAsync(
            () => { urlCalls++; return EligibleUrl; }, args);

        Assert.True(result.Success);
        Assert.Equal(1, urlCalls);
        Assert.Equal(expectedLaunches, requests.Count);
    }

    /// <summary>
    /// The CREDENTIAL resolver is NOT invoked on ANY Stage 6a REJECTION path, nor on Branch B
    /// (an ineligible transport command runs unauthenticated and never reaches Stage 6e).
    /// Branch A is covered separately by the Stage 6e tests.
    /// </summary>
    public static TheoryData<Func<string?>> CredentialFreeStage6aResolvers => new()
    {
        static () => "ssh://git@github.com/org/config-repo.git",      // Branch B
        static () => null,                                            // missing
        static () => "   ",                                           // whitespace
        static () => "https://x-access-token:tok@github.com/o/r.git", // Sanitize rejection
        static () => throw new InvalidOperationException("boom"),     // resolver failure
    };

    [Theory]
    [MemberData(nameof(CredentialFreeStage6aResolvers))]
    public async Task Stage6a_CredentialResolverIsNeverInvoked(Func<string?> resolver)
    {
        var credentialCalls = 0;

        foreach (var args in new[]
                 {
                     new[] { "pull" },
                     new[] { "fetch", "--tags" },
                     new[] { "push", "origin", "main" },
                     new[] { "pull", "origin", "main" },
                 })
        {
            await RunCapturingAsync(
                resolver,
                args,
                credentialResolver: () => { credentialCalls++; return "token"; });
        }

        Assert.Equal(0, credentialCalls);
    }

    /// <summary>
    /// Cancellation still PROPAGATES from both launch sites for a transport command that has
    /// passed Stage 6a — the Stage 6a insertion must not swallow it. The gate is TCS-based
    /// (no timing) and every await is bounded.
    /// </summary>
    [Theory]
    [InlineData(new[] { "pull", "origin", "main" }, new[] { "check-ref-format", "--allow-onelevel", "main" })]
    [InlineData(new[] { "pull" }, new[] { "remote", "get-url", "origin" })]
    public async Task Stage6a_PassedTransportCommand_CancellationPropagatesAtTheFirstLaunch(
        string[] args, string[] expectedFirstLaunch)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<ConfigRepoOpResult> execution = null!;

        try
        {
            GitOperations.ProcessRunner = async (request, ct) =>
            {
                requests.Add(request);
                entered.TrySetResult();
                try
                {
                    await gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw new OperationCanceledException(ct);
                }

                return new GitProcessResult(0, string.Empty, string.Empty);
            };

            using var seam = CreateSeam(resolvedUrlResolver: UrlResolver(EligibleUrl));
            execution = seam.RunConfigRepoCommandAsync(args, RepoDir, cts.Token);

            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            Assert.Single(requests);
            Assert.Equal(expectedFirstLaunch, requests[0].TokenizedArgs!.ToArray());

            await cts.CancelAsync();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => Bounded(execution));
            Assert.Equal(cts.Token, ex.CancellationToken);

            Assert.Single(requests);
        }
        finally
        {
            // Settle the outstanding operation BEFORE restoring the static seam.
            gate.TrySetResult();
            try
            {
                await execution;
            }
            catch (OperationCanceledException)
            {
                // Expected — the operation was cancelled.
            }

            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ------------------------------------------------------------------
    // Stage 6c/6d/6e (slice 2c-b1c-ii) — the origin state machine, the
    // credential + helper resolution, the env injection and the literal pass
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs a command against a SCRIPTED ProcessRunner (restored in a finally block). The
    /// script may throw to model a launch failure.
    /// </summary>
    private static async Task<(ConfigRepoOpResult Result, List<GitProcessRequest> Requests)> RunScriptedAsync(
        Func<GitProcessRequest, GitProcessResult> respond,
        string[] args,
        Func<string?>? credentialResolver = null,
        Func<string>? credentialHelperPath = null,
        Func<string?>? resolvedUrlResolver = null)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(respond(request));
            };

            using var seam = CreateSeam(
                resolvedUrlResolver: resolvedUrlResolver ?? UrlResolver(EligibleUrl),
                credentialResolver: credentialResolver,
                credentialHelperPath: credentialHelperPath);
            var result = await Bounded(
                seam.RunConfigRepoCommandAsync(args, RepoDir, CancellationToken.None));
            return (result, requests);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 6d step 3a — the origin INSPECTION ──────────────────────────

    /// <summary>
    /// The origin-inspection request has the EXACT credential-free shape:
    /// <c>remote get-url origin</c>, empty <c>Args</c>, the CONSTRUCTOR-canonicalized working
    /// directory, and the scrubbed env plus <c>GIT_TERMINAL_PROMPT=0</c> — even though a
    /// NON-WHITESPACE credential is resolvable for the final command.
    /// </summary>
    [Fact]
    public async Task Stage6d_OriginInspectionRequest_IsCredentialFreeAndHasExactShape()
    {
        var previousEnv = SeedChildEnvVariables();
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));
            };

            using var seam = CreateSeam(
                pathCanonicalizer: _ => CanonicalizedRepoDir,
                credentialResolver: static () => "ghp_secret");
            var result = await Bounded(seam.RunConfigRepoCommandAsync(
                ["pull"], RepoDirWithSeparator, CancellationToken.None));

            Assert.True(result.Success);
            AssertSequence(requests, OriginInspect, ["pull", "origin"]);

            var inspection = requests[0];
            Assert.Equal("git", inspection.Executable);
            Assert.Empty(inspection.Args);
            Assert.Equal(CanonicalizedRepoDir, inspection.WorkingDirectory);
            AssertChildEnv(inspection); // credential-free

            // The FINAL command is the ONLY one that carries the credential.
            AssertChildEnvWithCredential(requests[1], "ghp_secret", "/helper");
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// A nonzero inspection exit whose stderr matches the ABSENCE classification (checked
    /// case-INSENSITIVELY) means an ABSENT origin → <c>remote add origin &lt;sanitized&gt;</c>.
    /// </summary>
    [Theory]
    [InlineData("error: No such remote 'origin'")]
    [InlineData("fatal: no such remote 'origin'")]
    [InlineData("fatal: NOT A GIT REPOSITORY (or any of the parent directories): .git")]
    [InlineData("fatal: does not appear to be a git repository")]
    [InlineData("fatal: DOES NOT APPEAR TO BE A GIT REPOSITORY")]
    public async Task Stage6d_AbsenceStderrClassification_AddsTheOrigin(string stderr)
    {
        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote" && request.TokenizedArgs![1] == "get-url"
                ? new GitProcessResult(128, string.Empty, stderr)
                : new GitProcessResult(0, string.Empty, string.Empty),
            ["pull"]);

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect, OriginAdd, ["pull", "origin"]);
    }

    /// <summary>
    /// An exit-0 inspection with EMPTY (or whitespace-only) stdout is ALSO an ABSENT origin.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public async Task Stage6d_ExitZeroWithEmptyStdout_AddsTheOrigin(string stdout)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull"], originStdout: stdout);

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect, OriginAdd, ["pull", "origin"]);
    }

    /// <summary>
    /// ANY OTHER nonzero inspection exit is an inspection FAILURE — the fixed message, exit
    /// code ALWAYS -1, empty stdout, and the internal output DISCARDED. Nothing else launches.
    /// </summary>
    [Theory]
    [InlineData(1, "fatal: something else went wrong")]
    [InlineData(128, "fatal: unable to read config")]
    [InlineData(2, "")]
    public async Task Stage6d_OtherNonZeroInspectionExit_ReturnsOriginNotVerified(
        int exitCode, string stderr)
    {
        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(exitCode, "internal stdout", stderr)
                : new GitProcessResult(0, string.Empty, string.Empty),
            ["pull"]);

        AssertRejected(result, OriginNotVerified);
        AssertSequence(requests, OriginInspect);
    }

    /// <summary>
    /// A LAUNCH failure at the origin inspection maps to the SAME fixed message — the
    /// exception's own text never escapes — and nothing else launches.
    /// </summary>
    [Fact]
    public async Task Stage6d_InspectionLaunchFailure_ReturnsOriginNotVerified()
    {
        var (result, requests) = await RunScriptedAsync(
            _ => throw new InvalidOperationException("git executable missing"),
            ["pull"]);

        AssertRejected(result, OriginNotVerified);
        AssertSequence(requests, OriginInspect);
    }

    // ── Stage 6d step 3b — ADD an absent origin ───────────────────────────

    /// <summary>
    /// A nonzero <c>remote add</c> exit rejects with the fixed message; the final command
    /// NEVER launches, and NEITHER Stage 6e delegate is reached — Stage 6e runs only after
    /// origin verification SUCCEEDS, so a mutant that resolved the credential ahead of the
    /// failed reconciliation fails the invocation counts.
    /// </summary>
    [Fact]
    public async Task Stage6d_AddNonZeroExit_ReturnsOriginNotAdded()
    {
        var credentialCalls = 0;
        var helperCalls = 0;

        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![1] switch
            {
                "get-url" => new GitProcessResult(0, string.Empty, string.Empty),
                _ => new GitProcessResult(3, "internal stdout", "internal stderr"),
            },
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(result, OriginNotAdded);
        AssertSequence(requests, OriginInspect, OriginAdd);
        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    /// <summary>
    /// A LAUNCH failure at <c>remote add</c> maps to the same fixed message, and neither
    /// Stage 6e delegate is reached.
    /// </summary>
    [Fact]
    public async Task Stage6d_AddLaunchFailure_ReturnsOriginNotAdded()
    {
        var credentialCalls = 0;
        var helperCalls = 0;

        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![1] == "get-url"
                ? new GitProcessResult(0, string.Empty, string.Empty)
                : throw new InvalidOperationException("boom"),
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(result, OriginNotAdded);
        AssertSequence(requests, OriginInspect, OriginAdd);
        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    /// <summary>
    /// The ABSENCE path end-to-end: the inspection classifies the origin as ABSENT, the seam
    /// ADDS it with the SANITIZED url, and the final command then proceeds WITH the credential
    /// injected — while the inspection and the add themselves stay CREDENTIAL-FREE.
    /// </summary>
    [Fact]
    public async Task Stage6d_AbsentOrigin_AddsThenProceedsWithTheCredential()
    {
        var previousEnv = SeedChildEnvVariables();
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(
                    request.TokenizedArgs![1] == "get-url"
                        ? new GitProcessResult(128, string.Empty, "fatal: No Such Remote 'origin'")
                        : new GitProcessResult(0, string.Empty, string.Empty));
            };

            using var seam = CreateSeam(
                credentialResolver: static () => "ghp_secret",
                credentialHelperPath: static () => "/tmp/askpass.sh");
            var result = await Bounded(
                seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None));

            Assert.True(result.Success);
            AssertSequence(requests, OriginInspect, OriginAdd, ["pull", "origin"]);

            // The inspection AND the add are credential-free; only the final command carries it.
            AssertChildEnv(requests[0]);
            AssertChildEnv(requests[1]);
            AssertChildEnvWithCredential(requests[2], "ghp_secret", "/tmp/askpass.sh");

            // The seam NEVER WRITES a credential: the add argument is the SANITIZED URL.
            Assert.DoesNotContain("ghp_secret", requests[1].TokenizedArgs!, StringComparer.Ordinal);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// The REPAIR path end-to-end: the <c>set-url</c> subprocess itself is CREDENTIAL-FREE
    /// even though the final command that follows it is injected.
    /// </summary>
    [Fact]
    public async Task Stage6d_RepairSubprocess_IsCredentialFreeWhileTheFinalCommandIsInjected()
    {
        var previousEnv = SeedChildEnvVariables();
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(OriginAwareResult(
                    request, 0, "https://x-access-token:ghp_secret@github.com/org/config-repo.git"));
            };

            using var seam = CreateSeam(
                credentialResolver: static () => "ghp_secret",
                credentialHelperPath: static () => "/tmp/askpass.sh");
            var result = await Bounded(
                seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None));

            Assert.True(result.Success);
            AssertSequence(requests, OriginInspect, OriginSetUrl, ["pull", "origin"]);

            AssertChildEnv(requests[0]);
            AssertChildEnv(requests[1]);
            AssertChildEnvWithCredential(requests[2], "ghp_secret", "/tmp/askpass.sh");
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            RestoreChildEnvVariables(previousEnv);
        }
    }

    // ── Stage 6d step 3c — the rejection classes, one test per class ──────

    /// <summary>
    /// The repair predicate requires EVERY component to match. These tests pin each rejection
    /// class SEPARATELY, so deleting any single conjunct of the predicate fails a dedicated
    /// test rather than merely thinning a shared table. Each origin is credential-BEARING and
    /// differs from the configured URL in EXACTLY ONE respect, so the only reason it is not
    /// repaired is the conjunct under test.
    /// </summary>
    private static async Task AssertOriginRejectedAsync(string origin)
    {
        var previousEnv = SeedChildEnvVariables();
        var credentialCalls = 0;
        var helperCalls = 0;
        try
        {
            var (result, requests) = await RunCapturingAsync(
                UrlResolver(EligibleUrl),
                ["pull"],
                credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
                originStdout: origin,
                credentialHelperPath: () => { helperCalls++; return "/helper"; });

            AssertRejected(result, OriginMismatch);

            // NO add, NO set-url and NO final command: the seam never rewrites nor runs.
            AssertSequence(requests, OriginInspect);

            // Neither credential delegate is reached by an origin-rejected operation, and the
            // one subprocess that DID run carries no credential env.
            Assert.Equal(0, credentialCalls);
            Assert.Equal(0, helperCalls);
            AssertChildEnv(requests[0]);

            Assert.DoesNotContain("ghp_secret", result.SanitizedError, StringComparison.Ordinal);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>A credential-bearing origin over <c>http://</c> is REJECTED — never upgraded.</summary>
    [Fact]
    public Task Stage6d_CredentialBearingOriginOverHttp_IsRejected() =>
        AssertOriginRejectedAsync("http://x-access-token:ghp_secret@github.com/org/config-repo.git");

    /// <summary>A credential-bearing origin on a NON-443 port is REJECTED — never re-pointed.</summary>
    [Theory]
    [InlineData("https://x-access-token:ghp_secret@github.com:8443/org/config-repo.git")]
    [InlineData("https://x-access-token:ghp_secret@github.com:8080/org/config-repo.git")]
    public Task Stage6d_CredentialBearingOriginOnNon443Port_IsRejected(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>A credential-bearing origin at a DIFFERENT HOST is REJECTED.</summary>
    [Theory]
    [InlineData("https://x-access-token:ghp_secret@gitlab.com/org/config-repo.git")]
    [InlineData("https://x-access-token:ghp_secret@github.example.com/org/config-repo.git")]
    public Task Stage6d_CredentialBearingOriginAtDifferentHost_IsRejected(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>A credential-bearing origin at a DIFFERENT PATH is REJECTED.</summary>
    [Theory]
    [InlineData("https://x-access-token:ghp_secret@github.com/other/config-repo.git")]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/other-repo.git")]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/config-repo/extra.git")]
    public Task Stage6d_CredentialBearingOriginAtDifferentPath_IsRejected(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>
    /// A credential-bearing origin carrying a QUERY or a FRAGMENT is REJECTED — the seam never
    /// drops either component in the course of a "repair".
    /// </summary>
    [Theory]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/config-repo.git?token=other")]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/config-repo.git?x=1")]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/config-repo.git#frag")]
    public Task Stage6d_CredentialBearingOriginWithQueryOrFragment_IsRejected(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>An SSH origin — in URL and scp-style spelling — is REJECTED.</summary>
    [Theory]
    [InlineData("ssh://git@github.com/org/config-repo.git")]
    [InlineData("ssh://git@github.com:443/org/config-repo.git")]
    [InlineData("git@github.com:org/config-repo.git")]
    public Task Stage6d_SshOrigin_IsRejected(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>A <c>file://</c> origin and a bare local absolute path are REJECTED.</summary>
    [Fact]
    public async Task Stage6d_FileAndLocalPathOrigin_AreRejected()
    {
        await AssertOriginRejectedAsync(FileUrl);
        await AssertOriginRejectedAsync(LocalPathUrl);
    }

    /// <summary>A credential-FREE origin on an explicit non-443 port is REJECTED.</summary>
    [Fact]
    public Task Stage6d_CredentialFreeOriginOnNon443Port_IsRejected() =>
        AssertOriginRejectedAsync("https://github.com:8443/org/config-repo.git");

    /// <summary>
    /// A credential-FREE STALE origin (the right SHAPE — https, github.com, 443 — but the
    /// WRONG repository) is REJECTED with NO rewrite: there is no <c>set-url</c> in the
    /// sequence, and no subprocess ever carries credential env. Rewriting a stale origin
    /// would silently retarget the worker at a different repository.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/other-org/config-repo.git")]
    [InlineData("https://github.com/org/some-other-repo.git")]
    [InlineData("https://github.com/org/config-repo/nested.git")]
    public Task Stage6d_CredentialFreeStaleOrigin_IsRejectedWithoutRewriting(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>
    /// The NON-ASCII percent-escape vector. Adjacent escapes are decoded BYTE-TO-CODE-POINT
    /// (Latin-1 style), NEVER re-assembled as UTF-8. The discriminating row is
    /// <c>r%E9po</c> vs <c>r%EF%BF%BDpo</c>: a UTF-8 decoder with a replacement fallback maps
    /// BOTH to <c>r\uFFFDpo</c> (the lone <c>0xE9</c> is invalid UTF-8, and <c>EF BF BD</c>
    /// IS the replacement character) and would call them EQUIVALENT — so a UTF-8-decoding
    /// implementation issues a <c>set-url</c> and rewrites the origin. The Latin-1 rule keeps
    /// them DISTINCT, which is what the REJECTION asserts.
    /// </summary>
    [Theory]
    // The discriminator: equal under UTF-8-with-replacement, DISTINCT under Latin-1.
    [InlineData("https://github.com/org/r%E9po.git", "https://github.com/org/r%EF%BF%BDpo.git", false)]
    [InlineData("https://github.com/org/r%EF%BF%BDpo.git", "https://github.com/org/r%E9po.git", false)]
    // A two-escape UTF-8 sequence never collapses into the single code point it would encode.
    [InlineData("https://github.com/org/r%C3%A9po.git", "https://github.com/org/r%E9po.git", false)]
    // Identical escapes ARE equivalent — the decoding is not simply broken for every input.
    [InlineData("https://github.com/org/r%E9po.git", "https://github.com/org/r%E9po.git", true)]
    [InlineData("https://github.com/org/r%C3%A9po.git", "https://github.com/org/r%C3%A9po.git", true)]
    public async Task Stage6d_NonAsciiEscapesAreDecodedByteToCodePoint(
        string configuredUrl, string origin, bool equivalent)
    {
        // The vectors really are distinct SANITIZED urls, so the outcome cannot be an artefact
        // of the sanitizer normalizing them to the same string.
        var sanitized = ConfigRepoUrlSanitizer.Sanitize(configuredUrl);
        Assert.False(string.IsNullOrWhiteSpace(sanitized));

        var (result, requests) = await RunCapturingAsync(
            UrlResolver(configuredUrl), ["pull"], originStdout: origin);

        if (equivalent)
        {
            Assert.True(result.Success);
            AssertSequence(requests, OriginInspect, ["pull", "origin"]);
        }
        else
        {
            AssertRejected(result, OriginMismatch);
            AssertSequence(requests, OriginInspect);
        }
    }

    /// <summary>
    /// The path comparison runs over the RAW url string, NEVER over a
    /// <see cref="System.Uri"/>-canonicalized path. <see cref="System.Uri"/> silently applies
    /// transformations that are NOT among the four permitted normalization steps — dot-segment
    /// collapse (<c>/a/../b</c> → <c>/b</c>), <c>\</c>→<c>/</c> normalization, and unescaping
    /// of unreserved characters — so an implementation that read the path through
    /// <see cref="System.Uri"/> would treat each of these ALIASES as equivalent to the
    /// configured URL and, for the credential-bearing rows, silently "repair" an origin whose
    /// raw components do not match. Every row here must be REJECTED.
    /// </summary>
    [Theory]
    // Dot-segment aliases: the raw components include the literal ".." / "." segments.
    [InlineData("https://github.com/org/other/../config-repo.git")]
    [InlineData("https://github.com/org/./config-repo.git")]
    [InlineData("https://github.com/./org/config-repo.git")]
    [InlineData("https://github.com/org/a/b/../../config-repo.git")]
    // The credential-bearing dot-segment alias must NOT be "safely repaired" either.
    [InlineData("https://x-access-token:ghp_secret@github.com/org/other/../config-repo.git")]
    // Backslash alias: Uri normalizes '\' to '/', the raw algorithm does not.
    [InlineData("https://github.com/org\\config-repo.git")]
    [InlineData("https://x-access-token:ghp_secret@github.com/org\\config-repo.git")]
    // A duplicate-slash alias is a genuinely different raw component list (an EMPTY component).
    [InlineData("https://github.com/org//config-repo.git")]
    public Task Stage6d_UriCanonicalizationAliases_AreRejected(string origin) =>
        AssertOriginRejectedAsync(origin);

    /// <summary>
    /// The converse of <see cref="Stage6d_UriCanonicalizationAliases_AreRejected"/>: the
    /// EXACT raw path still matches, so the alias rejection is not simply "reject everything".
    /// </summary>
    [Fact]
    public async Task Stage6d_ExactRawPath_IsStillEquivalent()
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull"], originStdout: EligibleUrl);

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect, ["pull", "origin"]);
    }

    /// <summary>
    /// PERCENT-DECODING discriminators. Every row is chosen so that DELETING the decode step
    /// (comparing the raw components directly) flips the outcome:
    /// <list type="bullet">
    ///   <item><description>
    ///   ENCODED-vs-LITERAL: the sanitized configured path holds the literal <c>echo</c> while
    ///   the origin spells it <c>%65cho</c>. The raw components DIFFER and only the decode
    ///   makes them equal — so without the decode the row is rejected and the test fails.
    ///   </description></item>
    ///   <item><description>
    ///   MALFORMED escapes stay LITERAL: the sanitizer re-escapes the stray <c>%</c> to
    ///   <c>%25</c>, so the configured side reads <c>a%25zzb</c> and the origin <c>a%zzb</c>.
    ///   One decode pass turns the former into <c>a%zzb</c> and leaves the latter's malformed
    ///   <c>%zz</c> exactly as it is — they match only because BOTH rules hold.
    ///   </description></item>
    ///   <item><description>
    ///   SINGLE-PASS, not recursive: <c>%252F</c> decodes ONCE to the literal four-character
    ///   string <c>%2F</c>, never onwards to <c>/</c>. A recursive decoder would equate
    ///   <c>x%252Fy</c> with <c>x%2Fy</c> (both reaching <c>x/y</c>) and wrongly accept the
    ///   origin; the rejection pins exactly one pass.
    ///   </description></item>
    /// </list>
    /// </summary>
    [Theory]
    // (a) ENCODED vs LITERAL — decode-EQUAL though raw-DIFFERENT → EQUIVALENT, no set-url.
    [InlineData("https://github.com/org/%65cho.git", "https://github.com/org/%65cho.git", true)]
    [InlineData("https://github.com/org/echo.git", "https://github.com/org/%65cho.git", true)]
    [InlineData("https://github.com/org/%65cho.git", "https://github.com/org/echo.git", true)]
    // A DIFFERENT decoded character is still a mismatch — the decode is not "accept anything".
    [InlineData("https://github.com/org/%65cho.git", "https://github.com/org/%66cho.git", false)]
    // (b) MALFORMED escapes remain literal (and %25 decodes in the same single pass).
    [InlineData("https://github.com/org/a%zzb.git", "https://github.com/org/a%zzb.git", true)]
    [InlineData("https://github.com/org/a%2.git", "https://github.com/org/a%2.git", true)]
    // (c) SINGLE-PASS vs recursive: %252F must NOT reach the separator.
    [InlineData("https://github.com/org/x%252Fy.git", "https://github.com/org/x%2Fy.git", false)]
    [InlineData("https://github.com/org/x%252Fy.git", "https://github.com/org/x%252Fy.git", true)]
    public async Task Stage6d_PercentDecodingDiscriminators(
        string configuredUrl, string origin, bool equivalent)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(configuredUrl), ["pull"], originStdout: origin);

        if (equivalent)
        {
            Assert.True(result.Success);
            AssertSequence(requests, OriginInspect, ["pull", "origin"]);
        }
        else
        {
            AssertRejected(result, OriginMismatch);
            AssertSequence(requests, OriginInspect);
        }
    }

    /// <summary>
    /// The ENCODED-vs-LITERAL equivalence also holds for a CREDENTIAL-BEARING origin, which is
    /// therefore safely REPAIRED rather than rejected — proving the decode participates in the
    /// repair predicate, not merely in the leave-as-is branch.
    /// </summary>
    [Fact]
    public async Task Stage6d_PercentDecoding_AppliesToTheRepairPredicate()
    {
        const string configured = "https://github.com/org/echo.git";
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(configured),
            ["pull"],
            originStdout: "https://x-access-token:ghp_secret@github.com/org/%65cho.git");

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect,
            ["remote", "set-url", "origin", configured], ["pull", "origin"]);
    }

    /// <summary>
    /// THE ORIGIN WRITE TARGET IS THE **SANITIZED** URL, NEVER THE RAW RESOLVED ONE. Every
    /// other add/set-url expectation uses <see cref="EligibleUrl"/>, whose raw and sanitized
    /// spellings are identical, so a mutant forwarding the RAW resolver value would survive
    /// them all. These rows resolve URLs the sanitizer demonstrably REWRITES — an explicit
    /// default <c>:443</c> it strips, and a host casing it lower-cases — and assert the EXACT
    /// argument string handed to <c>git remote add origin</c> / <c>git remote set-url origin</c>.
    /// </summary>
    public static TheoryData<string, string> RawVersusSanitizedUrlCases => new()
    {
        // The sanitizer strips the explicit default port.
        { "https://github.com:443/org/config-repo.git", "https://github.com/org/config-repo.git" },
        // The sanitizer lower-cases the host.
        { "https://GitHub.Com/org/config-repo.git", "https://github.com/org/config-repo.git" },
        { "https://GITHUB.COM/org/config-repo.git", "https://github.com/org/config-repo.git" },
        // Both at once.
        { "https://GITHUB.COM:443/org/config-repo.git", "https://github.com/org/config-repo.git" },
    };

    /// <summary>
    /// The vectors really ARE rewritten by the sanitizer — otherwise the assertions below
    /// would pass for the wrong reason (raw == sanitized).
    /// </summary>
    [Theory]
    [MemberData(nameof(RawVersusSanitizedUrlCases))]
    public void Stage6d_RawVersusSanitizedVectors_AreGenuinelyRewritten(string raw, string sanitized)
    {
        Assert.NotEqual(raw, sanitized);
        Assert.Equal(sanitized, ConfigRepoUrlSanitizer.Sanitize(raw));
    }

    /// <summary>
    /// An ABSENT origin is added with the SANITIZED url — not the raw resolved spelling.
    /// </summary>
    [Theory]
    [MemberData(nameof(RawVersusSanitizedUrlCases))]
    public async Task Stage6d_AddUsesTheSanitizedUrlNotTheRawResolvedUrl(string raw, string sanitized)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(raw), ["pull"], originStdout: "");

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect,
            ["remote", "add", "origin", sanitized], ["pull", "origin"]);

        // Belt and braces: the RAW spelling never reaches any launched argument.
        Assert.DoesNotContain(raw, requests.SelectMany(r => r.TokenizedArgs!), StringComparer.Ordinal);
    }

    /// <summary>
    /// A credential-bearing origin is repaired to the SANITIZED url — not the raw resolved
    /// spelling. The origin here is the credential-bearing form of the RAW url, so it is
    /// equivalent under the repair predicate and the write target is the only variable.
    /// </summary>
    [Theory]
    [MemberData(nameof(RawVersusSanitizedUrlCases))]
    public async Task Stage6d_SetUrlUsesTheSanitizedUrlNotTheRawResolvedUrl(string raw, string sanitized)
    {
        var credentialBearingOrigin = raw.Replace(
            "https://", "https://x-access-token:ghp_secret@", StringComparison.Ordinal);

        var (result, requests) = await RunCapturingAsync(
            UrlResolver(raw), ["pull"], originStdout: credentialBearingOrigin);

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect,
            ["remote", "set-url", "origin", sanitized], ["pull", "origin"]);

        Assert.DoesNotContain(raw, requests.SelectMany(r => r.TokenizedArgs!), StringComparer.Ordinal);
        Assert.DoesNotContain("ghp_secret", requests.SelectMany(r => r.TokenizedArgs!), StringComparer.Ordinal);
    }

    // ── Stage 6d step 3c — a PRESENT origin ───────────────────────────────

    /// <summary>
    /// A credential-free origin that is FULLY equivalent is LEFT AS IS — no <c>set-url</c>,
    /// even when it differs lexically by a <c>.git</c> suffix, a trailing slash, an explicit
    /// <c>:443</c> or host casing.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/org/config-repo.git")]
    [InlineData("https://github.com/org/config-repo")]
    [InlineData("https://github.com/org/config-repo/")]
    [InlineData("https://github.com/org/config-repo///")]
    [InlineData("https://github.com:443/org/config-repo.git")]
    [InlineData("https://GITHUB.COM/org/config-repo.git")]
    public async Task Stage6d_EquivalentCredentialFreeOrigin_IsLeftUntouched(string origin)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull"], originStdout: origin);

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect, ["pull", "origin"]);
    }

    /// <summary>
    /// A credential-BEARING origin that is otherwise fully equivalent is SAFELY REPAIRED with
    /// <c>remote set-url origin &lt;sanitized&gt;</c> — a credential-free URL — before the
    /// final command runs.
    /// </summary>
    [Theory]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/config-repo.git")]
    [InlineData("https://x-access-token:ghp_secret@github.com/org/config-repo")]
    [InlineData("https://x-access-token:ghp_secret@github.com:443/org/config-repo/")]
    [InlineData("https://ghp_secret@GITHUB.COM/org/config-repo.git")]
    public async Task Stage6d_CredentialBearingEquivalentOrigin_IsRepairedWithSetUrl(string origin)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl), ["pull"], originStdout: origin);

        Assert.True(result.Success);
        AssertSequence(requests, OriginInspect, OriginSetUrl, ["pull", "origin"]);

        // The seam NEVER WRITES a credential: the set-url argument is the SANITIZED URL.
        Assert.DoesNotContain("ghp_secret", requests[1].TokenizedArgs!, StringComparer.Ordinal);
    }

    /// <summary>
    /// A nonzero <c>remote set-url</c> exit rejects with the fixed message; the final command
    /// NEVER launches, and NEITHER Stage 6e delegate is reached.
    /// </summary>
    [Fact]
    public async Task Stage6d_SetUrlNonZeroExit_ReturnsOriginNotUpdated()
    {
        var credentialCalls = 0;
        var helperCalls = 0;

        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![1] switch
            {
                "get-url" => new GitProcessResult(
                    0, "https://x-access-token:ghp_secret@github.com/org/config-repo.git", string.Empty),
                _ => new GitProcessResult(4, "internal stdout", "internal stderr"),
            },
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(result, OriginNotUpdated);
        AssertSequence(requests, OriginInspect, OriginSetUrl);
        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    /// <summary>
    /// A LAUNCH failure at <c>remote set-url</c> maps to the same fixed message, and neither
    /// Stage 6e delegate is reached.
    /// </summary>
    [Fact]
    public async Task Stage6d_SetUrlLaunchFailure_ReturnsOriginNotUpdated()
    {
        var credentialCalls = 0;
        var helperCalls = 0;

        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![1] == "get-url"
                ? new GitProcessResult(
                    0, "https://x-access-token:ghp_secret@github.com/org/config-repo.git", string.Empty)
                : throw new InvalidOperationException("boom"),
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(result, OriginNotUpdated);
        AssertSequence(requests, OriginInspect, OriginSetUrl);
        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    /// <summary>
    /// The <c>get-url</c> reconciliation failures also reach NEITHER Stage 6e delegate.
    /// </summary>
    [Fact]
    public async Task Stage6d_InspectionFailures_ReachNeitherCredentialDelegate()
    {
        var credentialCalls = 0;
        var helperCalls = 0;

        // (a) an unclassifiable nonzero exit.
        var (nonZero, nonZeroRequests) = await RunScriptedAsync(
            _ => new GitProcessResult(1, string.Empty, "fatal: something else went wrong"),
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(nonZero, OriginNotVerified);
        AssertSequence(nonZeroRequests, OriginInspect);

        // (b) a launch failure.
        var (launchFailure, launchRequests) = await RunScriptedAsync(
            _ => throw new InvalidOperationException("boom"),
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(launchFailure, OriginNotVerified);
        AssertSequence(launchRequests, OriginInspect);

        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    /// <summary>
    /// THE POSITIVE ORDERING PROOF: Stage 6e runs strictly AFTER the Stage 6d reconciliation
    /// COMPLETES. The reconciliation is gated at a TCS; while the gate is held the credential
    /// resolver has been invoked ZERO times (and the helper likewise); after release the
    /// resolver is invoked EXACTLY ONCE and the helper exactly once. An implementation that
    /// resolved the credential before or during reconciliation fails the held-gate assertion.
    /// </summary>
    /// <param name="gateAdd">
    /// When true the gate sits on the <c>remote add</c> (an ABSENT origin), otherwise on the
    /// <c>remote get-url</c> inspection — so both reconciliation subprocesses are covered.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage6e_DelegatesResolveOnlyAfterReconciliationCompletes(bool gateAdd)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var commands = new List<string[]>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        var credentialCalls = 0;
        var helperCalls = 0;
        var helperCallsWhenCredentialResolved = -1;
        var gatedSubcommand = gateAdd ? "add" : "get-url";
        Task<ConfigRepoOpResult> operation = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                var tokens = request.TokenizedArgs!;
                bool block;
                lock (sync)
                {
                    commands.Add(tokens.ToArray());
                    block = tokens[0] == "remote" && tokens[1] == gatedSubcommand
                        && !entered.Task.IsCompleted;
                }

                if (!block)
                {
                    // An ABSENT origin when the ADD is the gated step, otherwise an already
                    // equivalent one.
                    return Task.FromResult(OriginAwareResult(
                        request, 0, gateAdd ? string.Empty : EligibleUrl));
                }

                entered.TrySetResult();
                return gate.Task.ContinueWith(_ => OriginAwareResult(
                    request, 0, gateAdd ? string.Empty : EligibleUrl));
            };

            using var seam = CreateSeam(
                credentialResolver: () =>
                {
                    // Captured so the helper's ordering relative to the resolver is provable.
                    helperCallsWhenCredentialResolved = Volatile.Read(ref helperCalls);
                    Interlocked.Increment(ref credentialCalls);
                    return "ghp_secret";
                },
                credentialHelperPath: () =>
                {
                    Interlocked.Increment(ref helperCalls);
                    return "/helper";
                });

            operation = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);

            // The operation is parked INSIDE the gated reconciliation subprocess.
            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // Stage 6d has NOT completed — Stage 6e must not have run.
            Assert.Equal(0, Volatile.Read(ref credentialCalls));
            Assert.Equal(0, Volatile.Read(ref helperCalls));

            gate.TrySetResult();
            Assert.True((await Bounded(operation)).Success);

            // After the reconciliation completed: EXACTLY once each.
            Assert.Equal(1, Volatile.Read(ref credentialCalls));
            Assert.Equal(1, Volatile.Read(ref helperCalls));

            // The helper is resolved only AFTER the credential (it is gated on a
            // non-whitespace credential having been resolved first).
            Assert.Equal(0, helperCallsWhenCredentialResolved);

            lock (sync)
            {
                Assert.Equal(
                    gateAdd
                        ? [OriginInspect, OriginAdd, ["pull", "origin"]]
                        : [OriginInspect, ["pull", "origin"]],
                    commands);
            }
        }
        finally
        {
            gate.TrySetResult();
            await SettleAsync(operation);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// Every REJECTED origin: a credential-bearing origin over <c>http://</c>, on a non-443
    /// port, at a different host or path, or carrying a query/fragment; AND a credential-free
    /// STALE origin (which is never rewritten). The seam never upgrades a scheme or a port,
    /// never retargets a repository, and never drops a query/fragment.
    /// </summary>
    public static TheoryData<string> RejectedOriginCases => new()
    {
        // Credential-bearing but NOT safely repairable.
        "http://x-access-token:ghp_secret@github.com/org/config-repo.git",     // http
        "https://x-access-token:ghp_secret@github.com:8443/org/config-repo.git", // non-443 port
        "https://x-access-token:ghp_secret@gitlab.com/org/config-repo.git",    // different host
        "https://x-access-token:ghp_secret@github.com/other/config-repo.git",  // different path
        "https://x-access-token:ghp_secret@github.com/org/config-repo.git#frag", // fragment
        // Credential-FREE and stale/mismatched — REJECTED, never rewritten.
        "https://github.com/other/config-repo.git",
        "https://github.com/org/other-repo.git",
        "https://github.com/org/config-repo/extra.git",
        "https://github.com:8443/org/config-repo.git",
        "http://github.com/org/config-repo.git",
        "ssh://git@github.com/org/config-repo.git",
        "git@github.com:org/config-repo.git",
        "/srv/config-repo.git",
        // The percent-encoding vector: a DECODED %2F never becomes a path separator.
        "https://github.com/org/config%2Frepo.git",
        // The .git strip is case-SENSITIVE.
        "https://github.com/org/config-repo.GIT",
    };

    [Theory]
    [MemberData(nameof(RejectedOriginCases))]
    public async Task Stage6d_RejectedOrigin_ReturnsMismatchAndNeverRewrites(string origin)
    {
        var credentialCalls = 0;
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl),
            ["pull"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            originStdout: origin);

        AssertRejected(result, OriginMismatch);

        // NO set-url, NO add, and NO final command — the inspection is the only launch.
        AssertSequence(requests, OriginInspect);

        // An origin-rejected operation invokes the credential resolver ZERO times.
        Assert.Equal(0, credentialCalls);

        // The rejection message never carries the origin or its credential.
        Assert.DoesNotContain("ghp_secret", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PATH-NORMALIZATION test vectors, asserted through the seam: a raw <c>%2F</c> never
    /// acts as a separator, the <c>.git</c> strip is lower-case only, and every trailing slash
    /// is trimmed. The MATCHING rows must NOT produce a <c>set-url</c> (they are equivalent);
    /// the NON-matching rows must be REJECTED.
    /// </summary>
    [Theory]
    // configured .../org/repo%2Fname.git vs an origin with a REAL separator → NOT equivalent.
    [InlineData("https://github.com/org/repo%2Fname.git", "https://github.com/org/repo/name.git", false)]
    [InlineData("https://github.com/org/repo/name.git", "https://github.com/org/repo%2Fname.git", false)]
    // The identical percent-encoded form IS equivalent.
    [InlineData("https://github.com/org/repo%2Fname.git", "https://github.com/org/repo%2Fname.git", true)]
    // Trailing slashes and the .git suffix all normalize away.
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo/", true)]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo", true)]
    [InlineData("https://github.com/org/repo", "https://github.com/org/repo.git", true)]
    // The .git strip is case-SENSITIVE — .GIT is NOT stripped.
    [InlineData("https://github.com/org/repo", "https://github.com/org/repo.GIT", false)]
    // THE ORDER: the .git strip runs on the LAST component BEFORE the trailing-slash trim, so
    // ".git/" keeps its suffix (the last component is the EMPTY one) and does NOT match "repo".
    [InlineData("https://github.com/org/repo", "https://github.com/org/repo.git/", false)]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo.git/", false)]
    // The path comparison itself is case-SENSITIVE.
    [InlineData("https://github.com/org/repo.git", "https://github.com/ORG/repo.git", false)]
    public async Task Stage6d_PathNormalizationVectors(
        string configuredUrl, string origin, bool equivalent)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(configuredUrl), ["pull"], originStdout: origin);

        if (equivalent)
        {
            Assert.True(result.Success);
            AssertSequence(requests, OriginInspect, ["pull", "origin"]);
        }
        else
        {
            AssertRejected(result, OriginMismatch);
            AssertSequence(requests, OriginInspect);
        }
    }

    // ── Stage 6e — the credential + helper resolution ─────────────────────

    /// <summary>
    /// The credential resolver is read EXACTLY ONCE per eligible operation, and ONLY after the
    /// origin has been verified.
    /// </summary>
    [Fact]
    public async Task Stage6e_CredentialResolverIsReadExactlyOncePerEligibleOperation()
    {
        var credentialCalls = 0;
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl),
            ["pull", "origin", "main"],
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; });

        Assert.True(result.Success);
        Assert.Equal(1, credentialCalls);
        AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
            ["pull", "origin", "main"]);
    }

    /// <summary>
    /// ANY non-cancellation credential-resolver exception maps to the FIXED
    /// <c>Config repo not provisioned.</c> — the resolver's text NEVER escapes — and the final
    /// command never launches.
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(TimeoutException))]
    public async Task Stage6e_ThrowingCredentialResolver_ReturnsNotProvisioned(Type exceptionType)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl),
            ["pull"],
            credentialResolver: () => throw (Exception)Activator.CreateInstance(
                exceptionType, "resolver leaked ghp_secret")!);

        AssertRejected(result, NotProvisioned);
        AssertSequence(requests, OriginInspect); // the final command never launched
        Assert.DoesNotContain("ghp_secret", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> from the credential resolver PROPAGATES
    /// unconditionally with the caller token LIVE — it is never mapped to the fixed message.
    /// </summary>
    [Fact]
    public async Task Stage6e_CredentialResolverThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var resolverOce = new OperationCanceledException("credential resolver cancelled");
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));
            };

            using var seam = CreateSeam(credentialResolver: () => throw resolverOce);
            using var liveCts = new CancellationTokenSource(); // the token stays LIVE

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => Bounded(seam.RunConfigRepoCommandAsync(["pull"], RepoDir, liveCts.Token)));

            Assert.Same(resolverOce, ex);
            AssertSequence(requests, OriginInspect);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A null/whitespace credential runs the operation UNAUTHENTICATED: no token, no askpass,
    /// and the helper-path delegate is NEVER invoked.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Stage6e_AbsentCredential_RunsUnauthenticatedAndNeverReadsTheHelper(
        string? credential)
    {
        var previousEnv = SeedChildEnvVariables();
        var helperCalls = 0;
        try
        {
            var (result, requests) = await RunCapturingAsync(
                UrlResolver(EligibleUrl),
                ["pull"],
                credentialResolver: () => credential,
                credentialHelperPath: () => { helperCalls++; return "/helper"; });

            Assert.True(result.Success);
            Assert.Equal(0, helperCalls);
            AssertSequence(requests, OriginInspect, ["pull", "origin"]);

            // No injection anywhere — GIT_TERMINAL_PROMPT=0 still forces the fail-fast.
            foreach (var request in requests)
                AssertChildEnv(request);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// A null/empty/whitespace helper path — or a THROWING helper delegate — rejects with the
    /// fixed message, and the final command NEVER launches.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Stage6e_UnusableHelperPath_ReturnsHelperUnavailable(string? helperPath)
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl),
            ["pull"],
            credentialResolver: static () => "ghp_secret",
            credentialHelperPath: () => helperPath!);

        AssertRejected(result, HelperUnavailable);
        AssertSequence(requests, OriginInspect);
    }

    [Fact]
    public async Task Stage6e_ThrowingHelperDelegate_ReturnsHelperUnavailable()
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl),
            ["pull"],
            credentialResolver: static () => "ghp_secret",
            credentialHelperPath: static () => throw new InvalidOperationException("helper ghp_secret blew up"));

        AssertRejected(result, HelperUnavailable);
        AssertSequence(requests, OriginInspect);
        Assert.DoesNotContain("ghp_secret", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> from the helper-path delegate PROPAGATES
    /// unconditionally with the caller token LIVE.
    /// </summary>
    [Fact]
    public async Task Stage6e_HelperDelegateThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var helperOce = new OperationCanceledException("helper cancelled");
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
                Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));

            using var seam = CreateSeam(
                credentialResolver: static () => "ghp_secret",
                credentialHelperPath: () => throw helperOce);
            using var liveCts = new CancellationTokenSource();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => Bounded(seam.RunConfigRepoCommandAsync(["pull"], RepoDir, liveCts.Token)));

            Assert.Same(helperOce, ex);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── The Stage 7 injection ─────────────────────────────────────────────

    /// <summary>
    /// The FINAL command of an eligible operation with a NON-WHITESPACE credential gains
    /// EXACTLY <c>GITHUB_CONFIG_REPO_TOKEN</c> and <c>GIT_ASKPASS</c> AFTER the scrub — and
    /// NOTHING else. The ref-validation subprocess AND every origin command stay
    /// credential-free. The credential NEVER appears in the launched ARGS.
    /// </summary>
    [Fact]
    public async Task Stage7_EligibleOperationWithCredential_InjectsOnlyIntoTheFinalCommand()
    {
        var previousEnv = SeedChildEnvVariables();
        try
        {
            // An ABSENT origin so the add command is exercised too.
            var (result, requests) = await RunCapturingAsync(
                UrlResolver(EligibleUrl),
                ["pull", "origin", "main"],
                credentialResolver: static () => "ghp_secret",
                credentialHelperPath: static () => "/tmp/askpass.sh",
                originStdout: "");

            Assert.True(result.Success);
            AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
                OriginAdd, ["pull", "origin", "main"]);

            // Every subprocess BEFORE the final command is credential-free.
            for (var i = 0; i < requests.Count - 1; i++)
                AssertChildEnv(requests[i]);

            AssertChildEnvWithCredential(requests[^1], "ghp_secret", "/tmp/askpass.sh");

            // The credential is ENV-ONLY: never an argument of any launch.
            Assert.DoesNotContain("ghp_secret",
                requests.SelectMany(r => r.TokenizedArgs!), StringComparer.Ordinal);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// A Branch B (ineligible) transport command NEVER gets the injection, never runs the
    /// origin state machine, and never reads the credential resolver.
    /// </summary>
    [Fact]
    public async Task Stage7_BranchB_NeverInjectsAndNeverRunsTheOriginStateMachine()
    {
        var previousEnv = SeedChildEnvVariables();
        var credentialCalls = 0;
        try
        {
            var (result, requests) = await RunCapturingAsync(
                UrlResolver("ssh://git@github.com/org/config-repo.git"),
                ["pull"],
                credentialResolver: () => { credentialCalls++; return "ghp_secret"; });

            Assert.True(result.Success);
            AssertSequence(requests, ["pull"]);
            Assert.Equal(0, credentialCalls);
            AssertChildEnv(requests[0]);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    // ── The literal-secret redaction pass ─────────────────────────────────

    /// <summary>
    /// For an operation with a resolved NON-WHITESPACE credential, EVERY ordinal occurrence of
    /// the credential in the returned <c>Stdout</c>/<c>SanitizedError</c> becomes
    /// <c>[redacted]</c> — AFTER the structural <c>GitUrlRedactor</c> pass.
    /// </summary>
    [Fact]
    public async Task LiteralRedaction_CredentialInProcessOutput_IsReplacedEverywhere()
    {
        var (result, requests) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(0, EligibleUrl, string.Empty)
                : new GitProcessResult(
                    1,
                    "ghp_secret leaked once and ghp_secret twice",
                    "fatal: ghp_secret rejected  \n"),
            ["pull"],
            credentialResolver: static () => "ghp_secret");

        Assert.Equal(2, requests.Count);
        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("[redacted] leaked once and [redacted] twice", result.Stdout);
        Assert.Equal("fatal: [redacted] rejected", result.SanitizedError);
        Assert.DoesNotContain("ghp_secret", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_secret", result.SanitizedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The literal pass runs AFTER the structural <c>GitUrlRedactor.Redact</c>: a credential-bearing
    /// URL loses its userinfo structurally, and any remaining bare occurrence of the credential
    /// is then literally replaced — so neither the credential nor a credential-bearing URL
    /// survives.
    /// </summary>
    [Fact]
    public async Task LiteralRedaction_RunsAfterTheStructuralRedaction()
    {
        var (result, _) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(0, EligibleUrl, string.Empty)
                : new GitProcessResult(
                    0,
                    "https://x-access-token:ghp_secret@github.com/org/config-repo.git and ghp_secret",
                    string.Empty),
            ["pull"],
            credentialResolver: static () => "ghp_secret");

        Assert.True(result.Success);
        Assert.Equal(
            "https://github.com/org/config-repo.git and [redacted]", result.Stdout);
        Assert.DoesNotContain("ghp_secret", result.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The literal pass applies to a resolved credential even when it was NEVER injected: the
    /// helper-path failure path still runs it. The credential here is a substring of the fixed
    /// message, so its replacement is directly observable.
    /// </summary>
    [Fact]
    public async Task LiteralRedaction_AppliesEvenWhenTheCredentialWasNotInjected()
    {
        var (result, requests) = await RunCapturingAsync(
            UrlResolver(EligibleUrl),
            ["pull"],
            credentialResolver: static () => "available",
            credentialHelperPath: static () => "  ");

        AssertSequence(requests, OriginInspect);
        Assert.Equal("Git credential helper path is not [redacted].", result.SanitizedError);
    }

    /// <summary>
    /// A WHITESPACE credential does NOT trigger the literal pass (it is scoped to a
    /// non-whitespace credential), so ordinary output survives verbatim.
    /// </summary>
    [Fact]
    public async Task LiteralRedaction_WhitespaceCredential_DoesNotRewriteOutput()
    {
        var (result, _) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(0, EligibleUrl, string.Empty)
                : new GitProcessResult(0, "   spaced   output   ", string.Empty),
            ["pull"],
            credentialResolver: static () => "   ");

        Assert.True(result.Success);
        Assert.Equal("   spaced   output   ", result.Stdout);
    }

    /// <summary>
    /// The two returned text fields are redacted INDEPENDENTLY. The first case is a SUCCESS
    /// (exit 0), where only <c>Stdout</c> is populated; the second is a runner-produced FAILURE
    /// whose credential appears ONLY in stderr, so only <c>SanitizedError</c> can carry it.
    /// Asserting each exactly proves both fields go through the literal pass — an
    /// implementation that redacted just one of them fails exactly one of these cases.
    /// </summary>
    [Fact]
    public async Task LiteralRedaction_StdoutAndSanitizedErrorAreRedactedIndependently()
    {
        // (a) exit 0 — the credential is echoed in STDOUT only.
        var (success, _) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(0, EligibleUrl, string.Empty)
                : new GitProcessResult(0, "branch ghp_secret up to date", "ghp_secret in ignored stderr"),
            ["pull"],
            credentialResolver: static () => "ghp_secret");

        Assert.True(success.Success);
        Assert.Equal(0, success.ExitCode);
        Assert.Equal("branch [redacted] up to date", success.Stdout);
        Assert.Equal("", success.SanitizedError); // exit 0 discards stderr entirely

        // (b) a runner-produced FAILURE — the credential is echoed in STDERR only.
        var (failure, _) = await RunScriptedAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(0, EligibleUrl, string.Empty)
                : new GitProcessResult(128, "clean stdout", "fatal: authentication failed for ghp_secret  \n"),
            ["pull"],
            credentialResolver: static () => "ghp_secret");

        Assert.False(failure.Success);
        Assert.Equal(128, failure.ExitCode);
        Assert.Equal("clean stdout", failure.Stdout);
        Assert.Equal("fatal: authentication failed for [redacted]", failure.SanitizedError);
    }

    /// <summary>
    /// EVERY subprocess that precedes the final command is credential-free in the FULL
    /// three-subprocess path (ref validation → origin inspection → origin add → the injected
    /// final command), asserted with the full-env equality helpers so a stray post-scrub
    /// addition on ANY earlier request fails.
    /// </summary>
    [Fact]
    public async Task Stage6e_OnlyTheFinalCommandCarriesCredentialEnvAcrossTheWholePath()
    {
        var previousEnv = SeedChildEnvVariables();
        try
        {
            var (result, requests) = await RunCapturingAsync(
                UrlResolver(EligibleUrl),
                ["pull", "origin", "main"],
                credentialResolver: static () => "ghp_secret",
                credentialHelperPath: static () => "/tmp/askpass.sh",
                originStdout: ""); // ABSENT → the add runs too

            Assert.True(result.Success);
            AssertSequence(requests, ["check-ref-format", "--allow-onelevel", "main"], OriginInspect,
                OriginAdd, ["pull", "origin", "main"]);

            AssertChildEnv(requests[0]); // ref validation
            AssertChildEnv(requests[1]); // origin inspection
            AssertChildEnv(requests[2]); // origin add
            AssertChildEnvWithCredential(requests[3], "ghp_secret", "/tmp/askpass.sh");
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    // ── Stage 6c — the per-instance serialization semaphore ───────────────

    /// <summary>
    /// A recording runner whose ORIGIN INSPECTION blocks on a per-call gate, so an eligible
    /// operation can be parked while holding the Stage 6c semaphore. Every other command
    /// completes SYNCHRONOUSLY, which is what makes the concurrency assertions deterministic:
    /// an operation's <c>RunConfigRepoCommandAsync</c> call returns to the test only once the
    /// operation has reached a genuinely incomplete await.
    /// </summary>
    private sealed class GatedOriginRunner
    {
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<TaskCompletionSource> _entered = [];
        private readonly Lock _sync = new();

        public List<string[]> Commands { get; } = [];

        public int InspectionCount { get; private set; }

        public Task<GitProcessResult> Respond(GitProcessRequest request)
        {
            TaskCompletionSource gate;
            lock (_sync)
            {
                Commands.Add(request.TokenizedArgs!.ToArray());
                if (request.TokenizedArgs![0] != "remote")
                    return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));

                var index = InspectionCount++;
                while (_gates.Count <= index)
                {
                    _gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    _entered.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                gate = _gates[index];
                _entered[index].TrySetResult();
            }

            return gate.Task.ContinueWith(_ => new GitProcessResult(0, EligibleUrl, string.Empty));
        }

        public Task Entered(int index)
        {
            lock (_sync)
            {
                while (_entered.Count <= index)
                {
                    _gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    _entered.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _entered[index].Task;
            }
        }

        public void Release(int index)
        {
            lock (_sync)
            {
                while (_gates.Count <= index)
                {
                    _gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    _entered.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                _gates[index].TrySetResult();
            }
        }

        public void ReleaseAll()
        {
            lock (_sync)
            {
                foreach (var gate in _gates)
                    gate.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Stage 6c SERIALIZES eligible operations on the SAME instance. The first operation is
    /// parked inside its origin inspection while HOLDING the semaphore; the second operation's
    /// ref validation (which precedes Stage 6c) has already completed synchronously, so when
    /// its <c>RunConfigRepoCommandAsync</c> call returns it is provably parked ON THE
    /// SEMAPHORE — and its origin inspection has NOT run. Removing the semaphore makes the
    /// second inspection appear immediately and fails the assertion.
    /// </summary>
    [Fact]
    public async Task Stage6c_EligibleOperationsAreSerializedOnThePerInstanceSemaphore()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedOriginRunner();
        Task<ConfigRepoOpResult> first = null!;
        Task<ConfigRepoOpResult> second = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam();
            first = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // The second operation runs its Stage 6b ref validation SYNCHRONOUSLY and then
            // parks: the call below returns only once it has hit an incomplete await, which
            // — with the semaphore held — is the Stage 6c wait.
            second = seam.RunConfigRepoCommandAsync(
                ["pull", "origin", "main"], RepoDir, CancellationToken.None);

            Assert.Equal(
                [["remote", "get-url", "origin"], ["check-ref-format", "--allow-onelevel", "main"]],
                runner.Commands);
            Assert.Equal(1, runner.InspectionCount); // the second inspection has NOT run

            runner.Release(0);
            var firstResult = await Bounded(first);
            Assert.True(firstResult.Success);

            await runner.Entered(1).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            runner.Release(1);
            var secondResult = await Bounded(second);
            Assert.True(secondResult.Success);

            // The two operations never interleaved.
            Assert.Equal(
                [
                    ["remote", "get-url", "origin"],
                    ["check-ref-format", "--allow-onelevel", "main"],
                    ["pull", "origin"],
                    ["remote", "get-url", "origin"],
                    ["pull", "origin", "main"],
                ],
                runner.Commands);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleAsync(first, second);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A Branch B (ineligible) operation does NOT take the Stage 6c lock: it runs to
    /// completion while an eligible operation is parked inside its origin inspection holding
    /// the semaphore.
    /// </summary>
    [Fact]
    public async Task Stage6c_BranchBOperationDoesNotTakeTheLock()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedOriginRunner();
        Task<ConfigRepoOpResult> eligible = null!;

        // The FIRST read is the eligible URL; every later read is the ineligible one.
        var urlReads = 0;
        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam(resolvedUrlResolver: () =>
                Interlocked.Increment(ref urlReads) == 1
                    ? EligibleUrl
                    : "ssh://git@github.com/org/config-repo.git");

            eligible = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // Branch B: no origin state machine, no lock — it completes right now.
            var branchB = await Bounded(seam.RunConfigRepoCommandAsync(
                ["fetch", "--tags"], RepoDir, CancellationToken.None));

            Assert.True(branchB.Success);
            Assert.Equal(
                [["remote", "get-url", "origin"], ["fetch", "--tags"]],
                runner.Commands);

            runner.Release(0);
            Assert.True((await Bounded(eligible)).Success);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleAsync(eligible);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// THE ACQUIRED-FLAG RULE: a cancellation BEFORE acquisition propagates and releases
    /// NOTHING. The proof is that the semaphore's count is unchanged afterwards: a bug that
    /// released a semaphore it never owned would leave TWO permits, letting two later eligible
    /// operations run their origin inspections concurrently. The final assertion shows only
    /// one does.
    /// </summary>
    [Fact]
    public async Task Stage6c_CancellationBeforeAcquisition_PropagatesAndReleasesNothing()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedOriginRunner();
        Task<ConfigRepoOpResult> holder = null!;
        Task<ConfigRepoOpResult> waiter = null!;
        Task<ConfigRepoOpResult> third = null!;
        Task<ConfigRepoOpResult> fourth = null!;
        using var cts = new CancellationTokenSource();

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam();

            holder = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // The waiter parks ON the semaphore (the call returns at that incomplete await).
            waiter = seam.RunConfigRepoCommandAsync(["fetch"], RepoDir, cts.Token);
            Assert.Equal(1, runner.InspectionCount);

            await cts.CancelAsync();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => Bounded(waiter));
            Assert.Equal(cts.Token, ex.CancellationToken);

            // The cancelled waiter never ran anything.
            Assert.Equal(1, runner.InspectionCount);

            runner.Release(0);
            Assert.True((await Bounded(holder)).Success);

            // The count is intact: the next operation acquires, and the one after it WAITS.
            third = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(1).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            fourth = seam.RunConfigRepoCommandAsync(["fetch"], RepoDir, CancellationToken.None);
            Assert.Equal(2, runner.InspectionCount); // NOT 3 — the fourth is still waiting

            runner.Release(1);
            Assert.True((await Bounded(third)).Success);
            await runner.Entered(2).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            runner.Release(2);
            Assert.True((await Bounded(fourth)).Success);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleAsync(holder, waiter, third, fourth);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A cancellation AFTER acquisition RELEASES the semaphore in the finally: the operation
    /// is cancelled while parked inside its origin inspection, and a later operation still
    /// acquires the gate and completes.
    /// </summary>
    [Fact]
    public async Task Stage6c_CancellationAfterAcquisition_ReleasesTheSemaphore()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<ConfigRepoOpResult> cancelled = null!;

        try
        {
            GitOperations.ProcessRunner = async (request, ct) =>
            {
                requests.Add(request);
                if (request.TokenizedArgs![0] == "remote" && !entered.Task.IsCompleted)
                {
                    entered.TrySetResult();
                    try
                    {
                        await gate.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new OperationCanceledException(ct);
                    }
                }

                return OriginAwareResult(request, 0, EligibleUrl);
            };

            using var seam = CreateSeam();
            cancelled = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, cts.Token);
            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            await cts.CancelAsync();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => Bounded(cancelled));
            Assert.Equal(cts.Token, ex.CancellationToken);

            // The semaphore was RELEASED by the finally: a fresh operation completes.
            var next = await Bounded(seam.RunConfigRepoCommandAsync(
                ["fetch"], RepoDir, CancellationToken.None));
            Assert.True(next.Success);
        }
        finally
        {
            gate.TrySetResult();
            await SettleAsync(cancelled);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// Concurrent eligible calls SERIALIZE their origin reconciliation, so the second call
    /// OBSERVES the first's result rather than racing it. The fake models a real remote: the
    /// origin starts ABSENT, and the first operation's <c>remote add</c> makes it PRESENT and
    /// equivalent. Because the second operation's inspection runs only AFTER the first
    /// operation's final command, it observes the ADDED origin and issues NO second
    /// <c>add</c>. Without the Stage 6c gate both inspections would observe the ABSENT origin
    /// and BOTH would issue an <c>add</c> — the exact sequence assertion catches that.
    /// </summary>
    [Fact]
    public async Task Stage6c_SecondOperationObservesTheFirstOperationsOriginReconciliation()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var commands = new List<string[]>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        var originPresent = false; // the fake remote's observable state
        Task<ConfigRepoOpResult> first = null!;
        Task<ConfigRepoOpResult> second = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                var tokens = request.TokenizedArgs!;
                bool block;
                bool present;
                lock (sync)
                {
                    commands.Add(tokens.ToArray());

                    if (tokens[0] == "remote" && tokens[1] == "add")
                        originPresent = true; // the add MUTATES the fake remote

                    present = originPresent;
                    block = tokens[0] == "remote" && tokens[1] == "get-url" && !entered.Task.IsCompleted;
                }

                if (block)
                {
                    // The FIRST inspection parks while holding the semaphore. It reports the
                    // state as observed at ENTRY: ABSENT.
                    entered.TrySetResult();
                    return gate.Task.ContinueWith(
                        _ => new GitProcessResult(0, string.Empty, string.Empty));
                }

                if (tokens[0] == "remote" && tokens[1] == "get-url")
                    return Task.FromResult(new GitProcessResult(
                        0, present ? EligibleUrl : string.Empty, string.Empty));

                return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));
            };

            using var seam = CreateSeam();

            first = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // Issued while the first operation HOLDS the gate: it must park on the semaphore.
            second = seam.RunConfigRepoCommandAsync(["fetch"], RepoDir, CancellationToken.None);
            lock (sync)
            {
                // Only the FIRST inspection has run — the second is still waiting.
                Assert.Equal([["remote", "get-url", "origin"]], commands);
            }

            gate.TrySetResult();
            Assert.True((await Bounded(first)).Success);
            Assert.True((await Bounded(second)).Success);

            // The second operation's get-url runs AFTER the first's final command and OBSERVES
            // the added origin, so it issues NO second add.
            lock (sync)
            {
                Assert.Equal(
                    [
                        ["remote", "get-url", "origin"],
                        ["remote", "add", "origin", EligibleUrl],
                        ["pull", "origin"],
                        ["remote", "get-url", "origin"],
                        ["fetch", "origin"],
                    ],
                    commands);
            }
        }
        finally
        {
            gate.TrySetResult();
            await SettleAsync(first, second);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// The Stage 6c gate is PER-INSTANCE, not static/global. Seam instance A's eligible
    /// operation is parked inside its origin inspection while HOLDING A's semaphore; a SECOND,
    /// DISTINCT seam instance then runs its own eligible operation to COMPLETION. Replacing
    /// the per-instance <c>SemaphoreSlim</c> with a static one makes B block behind A, so the
    /// bounded await expires and the test FAILS FAST rather than hanging the suite. Together
    /// with <see cref="Stage6c_EligibleOperationsAreSerializedOnThePerInstanceSemaphore"/>
    /// (which proves the SAME instance DOES serialize) this pins the ownership scope exactly.
    /// </summary>
    [Fact]
    public async Task Stage6c_GateIsPerInstance_ASecondSeamInstanceIsNotBlocked()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var commands = new List<string[]>();
        var seamAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seamAGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        Task<ConfigRepoOpResult> seamAOperation = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                var tokens = request.TokenizedArgs!;
                bool block;
                lock (sync)
                {
                    commands.Add(tokens.ToArray());
                    // Only the FIRST inspection (seam A's) is gated; seam B's runs freely.
                    block = tokens[0] == "remote" && !seamAEntered.Task.IsCompleted;
                }

                if (!block)
                    return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));

                seamAEntered.TrySetResult();
                return seamAGate.Task.ContinueWith(
                    _ => new GitProcessResult(0, EligibleUrl, string.Empty));
            };

            using var seamA = CreateSeam();
            using var seamB = CreateSeam();

            // Seam A acquires ITS gate and parks inside the origin inspection.
            seamAOperation = seamA.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await seamAEntered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // Seam B is a DISTINCT instance: its eligible operation must run to completion
            // even though seam A still holds seam A's semaphore. With a STATIC gate this
            // await times out (a bounded await, so the test fails instead of hanging).
            var seamBResult = await seamB
                .RunConfigRepoCommandAsync(["fetch"], RepoDir, CancellationToken.None)
                .WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            Assert.True(seamBResult.Success);

            // Seam B ran its FULL Branch A sequence while seam A was still parked.
            lock (sync)
            {
                Assert.Equal(
                    [OriginInspect, OriginInspect, ["fetch", "origin"]],
                    commands);
            }

            // Seam A then completes normally on its own gate.
            seamAGate.TrySetResult();
            Assert.True((await Bounded(seamAOperation)).Success);
        }
        finally
        {
            seamAGate.TrySetResult();
            await SettleAsync(seamAOperation);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// The per-instance scope holds for the WAITING side too: seam A's operation is parked
    /// holding A's gate, a SECOND operation on seam A parks behind it (proving A serializes),
    /// and meanwhile seam B completes — so the gate is neither global nor absent.
    /// </summary>
    [Fact]
    public async Task Stage6c_GateIsPerInstance_SameInstanceWaitsWhileOtherInstanceProceeds()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var commands = new List<string[]>();
        var seamAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seamAGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        Task<ConfigRepoOpResult> seamAFirst = null!;
        Task<ConfigRepoOpResult> seamASecond = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                var tokens = request.TokenizedArgs!;
                bool block;
                lock (sync)
                {
                    commands.Add(tokens.ToArray());
                    block = tokens[0] == "remote" && !seamAEntered.Task.IsCompleted;
                }

                if (!block)
                    return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));

                seamAEntered.TrySetResult();
                return seamAGate.Task.ContinueWith(
                    _ => new GitProcessResult(0, EligibleUrl, string.Empty));
            };

            using var seamA = CreateSeam();
            using var seamB = CreateSeam();

            seamAFirst = seamA.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await seamAEntered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // A SECOND operation on the SAME instance parks on A's gate — no new inspection.
            seamASecond = seamA.RunConfigRepoCommandAsync(["fetch"], RepoDir, CancellationToken.None);
            lock (sync)
            {
                Assert.Equal([OriginInspect], commands);
            }

            // The OTHER instance proceeds regardless.
            var seamBResult = await seamB
                .RunConfigRepoCommandAsync(["fetch"], RepoDir, CancellationToken.None)
                .WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            Assert.True(seamBResult.Success);

            seamAGate.TrySetResult();
            Assert.True((await Bounded(seamAFirst)).Success);
            Assert.True((await Bounded(seamASecond)).Success);
        }
        finally
        {
            seamAGate.TrySetResult();
            await SettleAsync(seamAFirst, seamASecond);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 6c — the gate is RELEASED on EVERY post-acquisition exit ────



    /// <summary>
    /// The scripted responses for a Branch A operation, keyed by which post-acquisition exit
    /// the operation must take. The first operation is GATED at its origin inspection so it
    /// provably HOLDS the semaphore while the follow-up is issued; releasing the gate lets it
    /// run on to its exit, and the follow-up must then acquire and complete. A gate leak on
    /// ANY of these exits hangs the follow-up, which the bounded await turns into a failure.
    /// </summary>
    private sealed class GatedExitRunner(
        Func<GitProcessRequest, GitProcessResult> firstOperationResponder)
    {
        private readonly Lock _sync = new();

        /// <summary>Completed once the FIRST operation's origin inspection has been entered.</summary>
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Held until the test releases the FIRST operation's origin inspection.</summary>
        public TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string[]> Commands { get; } = [];

        /// <summary>Set once the first operation has finished; later calls are the follow-up.</summary>
        public bool FirstOperationDone { get; set; }

        public Task<GitProcessResult> Respond(GitProcessRequest request)
        {
            bool followUp;
            lock (_sync)
            {
                Commands.Add(request.TokenizedArgs!.ToArray());
                followUp = FirstOperationDone;
            }

            // The FOLLOW-UP operation always sees a present, equivalent, credential-free
            // origin and a clean final command, so its only possible failure is a gate leak.
            if (followUp)
                return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));

            if (request.TokenizedArgs![0] == "remote" && request.TokenizedArgs![1] == "get-url"
                && !Entered.Task.IsCompleted)
            {
                Entered.TrySetResult();
                return Gate.Task.ContinueWith(_ => firstOperationResponder(request));
            }

            return Task.FromResult(firstOperationResponder(request));
        }
    }

    /// <summary>
    /// The shared driver for the "no deadlock after a post-acquisition exit" family. The first
    /// operation acquires the Stage 6c gate and parks inside its origin inspection; the gate
    /// is then released so it runs on to the exit under test; finally a follow-up eligible
    /// operation must ACQUIRE and COMPLETE. Because the follow-up's own script is always
    /// clean, its only possible failure mode is an unreleased semaphore.
    /// </summary>
    /// <remarks>
    /// The credential/helper delegates are scoped to the FIRST operation via the runner's
    /// <see cref="GatedExitRunner.FirstOperationDone"/> flag: the follow-up must see WORKING
    /// delegates, otherwise it would fail for its own reason and mask a gate leak.
    /// </remarks>
    private static async Task AssertGateReleasedAfterExitAsync(
        Func<GitProcessRequest, GitProcessResult> firstOperationResponder,
        string expectedError,
        Func<string?>? credentialResolver = null,
        Func<string>? credentialHelperPath = null)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedExitRunner(firstOperationResponder);
        Task<ConfigRepoOpResult> failing = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam(
                credentialResolver: credentialResolver is null
                    ? null
                    : () => runner.FirstOperationDone ? null : credentialResolver(),
                credentialHelperPath: credentialHelperPath is null
                    ? null
                    : () => runner.FirstOperationDone ? "/helper" : credentialHelperPath());

            failing = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);

            // The first operation is parked INSIDE the gate — it HOLDS the semaphore.
            await runner.Entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            runner.Gate.TrySetResult();
            AssertRejected(await Bounded(failing), expectedError);
            runner.FirstOperationDone = true;

            // The follow-up must acquire the gate the failed operation released.
            var followUp = await Bounded(seam.RunConfigRepoCommandAsync(
                ["fetch"], RepoDir, CancellationToken.None));

            Assert.True(followUp.Success);
            Assert.Equal(
                ["remote", "get-url", "origin"],
                runner.Commands[^2]);
            Assert.Equal(["fetch", "origin"], runner.Commands[^1]);
        }
        finally
        {
            runner.Gate.TrySetResult();
            await SettleAsync(failing);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>A <c>get-url</c> FAILURE exit releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterGetUrlFailure() =>
        AssertGateReleasedAfterExitAsync(
            _ => new GitProcessResult(1, string.Empty, "fatal: something else went wrong"),
            OriginNotVerified);

    /// <summary>An <c>add</c> FAILURE exit releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterAddFailure() =>
        AssertGateReleasedAfterExitAsync(
            request => request.TokenizedArgs![1] == "get-url"
                ? new GitProcessResult(0, string.Empty, string.Empty)   // ABSENT → add
                : new GitProcessResult(9, string.Empty, string.Empty),  // the add fails
            OriginNotAdded);

    /// <summary>A <c>set-url</c> FAILURE exit releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterSetUrlFailure() =>
        AssertGateReleasedAfterExitAsync(
            request => request.TokenizedArgs![1] == "get-url"
                ? new GitProcessResult(
                    0, "https://x-access-token:ghp_secret@github.com/org/config-repo.git", string.Empty)
                : new GitProcessResult(9, string.Empty, string.Empty), // the set-url fails
            OriginNotUpdated);

    /// <summary>An origin MISMATCH rejection releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterOriginMismatch() =>
        AssertGateReleasedAfterExitAsync(
            _ => new GitProcessResult(0, "https://github.com/other/repo.git", string.Empty),
            OriginMismatch);

    /// <summary>A credential-RESOLVER failure releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterCredentialResolverFailure() =>
        AssertGateReleasedAfterExitAsync(
            _ => new GitProcessResult(0, EligibleUrl, string.Empty),
            NotProvisioned,
            credentialResolver: static () => throw new InvalidOperationException("boom"));

    /// <summary>A HELPER-path failure releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterHelperFailure() =>
        AssertGateReleasedAfterExitAsync(
            _ => new GitProcessResult(0, EligibleUrl, string.Empty),
            HelperUnavailable,
            credentialResolver: static () => "ghp_secret",
            credentialHelperPath: static () => throw new InvalidOperationException("boom"));

    /// <summary>A FINAL-COMMAND launch failure releases the gate.</summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterFinalCommandLaunchFailure() =>
        AssertGateReleasedAfterExitAsync(
            request => request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(0, EligibleUrl, string.Empty)
                : throw new InvalidOperationException("boom"),
            LaunchFailed);

    /// <summary>
    /// A CANCELLATION while RECONCILING (parked inside the origin inspection) releases the
    /// gate: the OCE propagates, and a follow-up eligible operation still acquires and
    /// completes. Distinct from
    /// <see cref="Stage6c_CancellationAfterAcquisition_ReleasesTheSemaphore"/> in that it also
    /// asserts the follow-up's FULL command sequence.
    /// </summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterCancellationWhileReconciling() =>
        AssertGateReleasedByCancellationAsync(cancelDuringFinalCommand: false);

    /// <summary>
    /// A CANCELLATION while EXECUTING the final command (i.e. AFTER the origin has been
    /// reconciled) releases the gate too.
    /// </summary>
    [Fact]
    public Task Stage6c_GateReleasedAfterCancellationWhileExecuting() =>
        AssertGateReleasedByCancellationAsync(cancelDuringFinalCommand: true);

    /// <summary>
    /// Cancels a Branch A operation at one of the two post-acquisition blocking points and
    /// proves the semaphore was released: the follow-up operation runs its own origin
    /// inspection and final command to completion.
    /// </summary>
    private static async Task AssertGateReleasedByCancellationAsync(bool cancelDuringFinalCommand)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var commands = new List<string[]>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedCommand = cancelDuringFinalCommand ? "pull" : "remote";
        var sync = new Lock();
        using var cts = new CancellationTokenSource();
        Task<ConfigRepoOpResult> cancelled = null!;

        try
        {
            GitOperations.ProcessRunner = async (request, ct) =>
            {
                bool block;
                lock (sync)
                {
                    commands.Add(request.TokenizedArgs!.ToArray());
                    block = request.TokenizedArgs![0] == blockedCommand && !entered.Task.IsCompleted;
                }

                if (block)
                {
                    entered.TrySetResult();
                    try
                    {
                        await gate.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // The delegate owns its cancellation semantics: normalize to an exact
                        // OperationCanceledException carrying the caller's token.
                        throw new OperationCanceledException(ct);
                    }
                }

                return OriginAwareResult(request, 0, EligibleUrl);
            };

            using var seam = CreateSeam();
            cancelled = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, cts.Token);
            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // The operation is parked PAST acquisition — at the inspection or at the final
            // command, per the parameter.
            lock (sync)
            {
                Assert.Equal(
                    cancelDuringFinalCommand ? 2 : 1,
                    commands.Count);
            }

            await cts.CancelAsync();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => Bounded(cancelled));
            Assert.Equal(cts.Token, ex.CancellationToken);

            // The gate was released by the finally: the follow-up acquires and completes.
            var followUp = await Bounded(seam.RunConfigRepoCommandAsync(
                ["fetch"], RepoDir, CancellationToken.None));

            Assert.True(followUp.Success);
            lock (sync)
            {
                Assert.Equal(["remote", "get-url", "origin"], commands[^2]);
                Assert.Equal(["fetch", "origin"], commands[^1]);
            }
        }
        finally
        {
            gate.TrySetResult();
            await SettleAsync(cancelled);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// DISPOSAL while an eligible operation HOLDS the Stage 6c semaphore: the semaphore is
    /// never disposed, so the in-flight operation completes NORMALLY (its final command still
    /// launches and its real result is returned) and its release does not throw. A subsequent
    /// call is rejected at entry with the exact post-disposal result. A mutant that disposed
    /// the semaphore in <c>Dispose()</c> would fault the in-flight release with an
    /// <see cref="ObjectDisposedException"/> instead of returning the real result.
    /// </summary>
    [Fact]
    public async Task Stage6c_DisposalWhileHoldingTheSemaphore_InFlightOperationStillCompletes()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var commands = new List<string[]>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        Task<ConfigRepoOpResult> inFlight = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                bool block;
                lock (sync)
                {
                    commands.Add(request.TokenizedArgs!.ToArray());
                    block = request.TokenizedArgs![0] == "remote" && !entered.Task.IsCompleted;
                }

                if (!block)
                    return Task.FromResult(OriginAwareResult(request, 0, EligibleUrl));

                entered.TrySetResult();
                return gate.Task.ContinueWith(
                    _ => new GitProcessResult(0, EligibleUrl, string.Empty));
            };

            var seam = CreateSeam();
            inFlight = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);

            // The operation HOLDS the semaphore (it is parked inside its origin inspection).
            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            seam.Dispose();

            gate.TrySetResult();
            var result = await Bounded(inFlight);

            // It completed NORMALLY: the final command launched and the real result came back.
            Assert.True(result.Success);
            lock (sync)
            {
                Assert.Equal(
                    [["remote", "get-url", "origin"], ["pull", "origin"]],
                    commands);
            }

            // A SUBSEQUENT call is rejected at ENTRY — the exact post-disposal result.
            AssertRejected(
                await Bounded(seam.RunConfigRepoCommandAsync(
                    ["pull"], RepoDir, CancellationToken.None)),
                "Seam disposed.");
        }
        finally
        {
            gate.TrySetResult();
            await SettleAsync(inFlight);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// Awaits outstanding operations so a failing assertion can never leave a blocked delegate
    /// that later advances with the real (un-faked) runner installed.
    /// </summary>
    private static async Task SettleAsync(params Task<ConfigRepoOpResult>?[] operations)
    {
        foreach (var operation in operations)
        {
            if (operation is null)
                continue;

            try
            {
                await operation;
            }
            catch (OperationCanceledException)
            {
                // Expected for the cancellation fixtures.
            }
            catch (Exception)
            {
                // Expected only if the test already failed mid-flight.
            }
        }
    }

    // ------------------------------------------------------------------
    // Disposal
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_InvokesOnDisposeExactlyOnce()
    {
        var calls = 0;
        var seam = CreateSeam(() => Interlocked.Increment(ref calls));
        seam.Dispose();
        Assert.Equal(1, calls);
        seam.Dispose();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_OnDisposeThrows_IsSwallowed()
    {
        var calls = 0;
        var seam = CreateSeam(() =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("cleanup blew up");
        });

        seam.Dispose();
        seam.Dispose();

        Assert.Equal(1, calls);
    }

    // ------------------------------------------------------------------
    // Canonical path comparison / case handling helpers
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stage3_TrailingSeparatorInWorkingDirectory_StillContained()
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            var withSeparator = OperatingSystem.IsWindows() ? @"C:\config-repo\" : "/config-repo/";
            using var seam = CreateSeam();
            var result = await RunInAsync(seam, new[] { "status" }, withSeparator);
            Assert.Equal(LaunchFailed, result.SanitizedError);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    [Fact]
    public void ProductionConstructor_DelegatesToTestingConstructor_ValidationIdentical()
    {
        // The production constructor validates configRepoDir identically (whitespace).
        Assert.Throws<ArgumentException>(() => new ConfigRepoGitOperations(
            " ", Provisioner(), Log(), static () => "/helper", static () => { }));
        // ... and rejects a relative path before any canonicalization.
        Assert.Throws<ArgumentException>(() => new ConfigRepoGitOperations(
            "relative", Provisioner(), Log(), static () => "/helper", static () => { }));
    }

    private static WorkerConfigProvisioner Provisioner() =>
        new("test-worker", static (_, _) =>
            Task.FromResult(new GetWorkerConfigResponse()), static _ => null, static (_, _) => { });

    // ==================================================================
    // Slice 2c-b2 — ProbeAndEnsureRepoHealthyAsync:
    // the worktree probe, the top-level containment verification, the
    // best-effort origin reconciliation-as-API and the local identity.
    // ==================================================================

    /// <summary>Stage 4 — a bare repository, a nonzero exit, or a probe that never ran.</summary>
    private const string NotAWorktree = "Not a git worktree.";

    /// <summary>Stage 4 — an exit-0 probe whose stdout is neither <c>true</c> nor <c>false</c>.</summary>
    private const string UnrecognizedRevParse = "Unrecognized rev-parse output.";

    /// <summary>Stage 5 — <c>rev-parse --show-toplevel</c> produced no usable answer.</summary>
    private const string ToplevelUnknown = "Could not determine worktree root.";

    /// <summary>Stage 5 — the reported worktree root is NOT the configured directory.</summary>
    private const string ToplevelMismatch =
        "Config repo worktree root does not match the configured directory.";

    /// <summary>Stage 6 — a Branch B (ineligible) config repo needs no origin repair.</summary>
    private const string ReconciliationSkipped =
        "Config repo origin reconciliation skipped: the configured repository is not HTTPS github.com.";

    /// <summary>Stage 6 — the NOTE prefix applied to an origin state-machine rejection.</summary>
    private const string ReconciliationFailed = "Origin reconciliation failed: ";

    /// <summary>
    /// The four EXACT reconciliation notes. They are spelled out as LITERALS rather than
    /// composed from the command-path constants: the note continues the prefix's sentence, so
    /// the rejection's leading capital is lowered, and that is part of the contract.
    /// </summary>
    private const string NoteOriginNotVerified =
        "Origin reconciliation failed: config repo origin could not be verified.";

    private const string NoteOriginNotAdded =
        "Origin reconciliation failed: config repo origin could not be added.";

    private const string NoteOriginNotUpdated =
        "Origin reconciliation failed: config repo origin could not be updated.";

    private const string NoteOriginMismatch =
        "Origin reconciliation failed: config repo origin does not match the configured repository.";

    /// <summary>Stage 6b — the FIXED note for a failed <c>git config user.email</c>.</summary>
    private const string IdentityEmailFailed = "Identity configuration failed: user.email.";

    /// <summary>Stage 6b — the FIXED note for a failed <c>git config user.name</c>.</summary>
    private const string IdentityNameFailed = "Identity configuration failed: user.name.";

    /// <summary>Stage 4 — the worktree probe command.</summary>
    private static readonly string[] ProbeWorktree = ["rev-parse", "--is-inside-work-tree"];

    /// <summary>Stage 5 — the top-level query command.</summary>
    private static readonly string[] ProbeToplevel = ["rev-parse", "--show-toplevel"];

    /// <summary>Stage 6b — the EXACT identity commands, in their required order.</summary>
    private static readonly string[] IdentityEmailCommand =
        ["config", "user.email", "copilothive-worker@local"];

    private static readonly string[] IdentityNameCommand =
        ["config", "user.name", "CopilotHive Worker"];

    /// <summary>
    /// A credential-BEARING origin that is otherwise structurally equivalent to
    /// <see cref="EligibleUrl"/>: the state machine REPAIRS it with <c>remote set-url</c>.
    /// </summary>
    private const string CredentialBearingOrigin =
        "https://x-access-token:ghp_secret@github.com/org/config-repo.git";

    /// <summary>The outcome of one probe run: the health plus everything observable about it.</summary>
    private sealed record ProbeRun(
        ConfigRepoHealth Health,
        List<GitProcessRequest> Requests,
        int CredentialCalls,
        int HelperCalls);

    /// <summary>
    /// The default HEALTHY fake response for the health API's commands: the worktree probe
    /// reports <c>true</c>, the top-level query reports <paramref name="toplevel"/> (defaulting
    /// to <see cref="RepoDir"/> — a MATCHING root), <c>remote get-url</c> reports
    /// <paramref name="originStdout"/> (defaulting to the already-equivalent, credential-free
    /// <see cref="EligibleUrl"/>) and every other command succeeds silently.
    /// </summary>
    private static GitProcessResult HealthAwareResult(
        GitProcessRequest request, string? toplevel = null, string? originStdout = EligibleUrl)
    {
        var tokens = request.TokenizedArgs!;
        if (tokens[0] == "rev-parse")
        {
            return tokens[1] == "--is-inside-work-tree"
                ? new GitProcessResult(0, "true", string.Empty)
                : new GitProcessResult(0, toplevel ?? RepoDir, string.Empty);
        }

        if (tokens[0] == "remote")
        {
            return new GitProcessResult(
                0,
                tokens[1] == "get-url" ? originStdout ?? string.Empty : string.Empty,
                string.Empty);
        }

        // The identity commands (`config user.email` / `config user.name`).
        return new GitProcessResult(0, string.Empty, string.Empty);
    }

    /// <summary>
    /// Runs the health probe against a SCRIPTED ProcessRunner (restored in a finally block),
    /// capturing EVERY request so the exact command sequence AND the exact invocation TOTAL can
    /// be asserted. The credential resolver and the credential-helper delegate are instrumented
    /// so that "the health API never invokes them" is checkable on every vector.
    /// <para>
    /// <paramref name="targetDir"/> is sent to the SUT VERBATIM — including an explicit
    /// <c>null</c> — otherwise the Stage 2 null row would never reach production.
    /// </para>
    /// </summary>
    private static async Task<ProbeRun> ProbeInAsync(
        Func<GitProcessRequest, GitProcessResult> respond,
        string? targetDir,
        Func<string?>? resolvedUrlResolver = null,
        Func<string, string>? pathCanonicalizer = null,
        string? configRepoDir = null)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var credentialCalls = 0;
        var helperCalls = 0;
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(respond(request));
            };

            using var seam = CreateSeam(
                pathCanonicalizer: pathCanonicalizer,
                configRepoDir: configRepoDir,
                resolvedUrlResolver: resolvedUrlResolver,
                credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
                credentialHelperPath: () => { helperCalls++; return "/helper"; });

            var health = await Bounded(
                seam.ProbeAndEnsureRepoHealthyAsync(targetDir!, CancellationToken.None));
            return new ProbeRun(health, requests, credentialCalls, helperCalls);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>The common case: the target directory IS the configured config repo.</summary>
    private static Task<ProbeRun> ProbeAsync(
        Func<GitProcessRequest, GitProcessResult> respond,
        Func<string?>? resolvedUrlResolver = null,
        Func<string, string>? pathCanonicalizer = null) =>
        ProbeInAsync(respond, RepoDir, resolvedUrlResolver, pathCanonicalizer);

    /// <summary>The default healthy script.</summary>
    private static Task<ProbeRun> ProbeHealthyAsync(
        Func<string?>? resolvedUrlResolver = null,
        string? originStdout = EligibleUrl) =>
        ProbeAsync(request => HealthAwareResult(request, originStdout: originStdout),
            resolvedUrlResolver);

    /// <summary>
    /// A NEGATIVE health report: no repo, NO directories, and the exact reason.
    /// </summary>
    private static void AssertUnhealthy(ConfigRepoHealth health, string expectedReason)
    {
        Assert.False(health.HasRepo);
        Assert.Null(health.RepoDir);
        Assert.Null(health.AgentsWorkDir);
        Assert.Equal(expectedReason, health.SanitizedReason);
    }

    /// <summary>
    /// The health API resolves NO credential and NO helper path: every one of its subprocesses
    /// is credential-free, so neither Stage 6e delegate may ever be touched.
    /// </summary>
    private static void AssertNoCredentialDelegates(ProbeRun run)
    {
        Assert.Equal(0, run.CredentialCalls);
        Assert.Equal(0, run.HelperCalls);
    }

    /// <summary>
    /// A HEALTHY report for the default fixture: the reported root and its agents directory,
    /// with <paramref name="expectedReason"/> as the (possibly null) aggregated note.
    /// </summary>
    private static void AssertHealthy(
        ConfigRepoHealth health, string? expectedReason, string? expectedRepoDir = null)
    {
        var repoDir = expectedRepoDir ?? RepoDir;
        Assert.True(health.HasRepo);
        Assert.Equal(repoDir, health.RepoDir);
        Assert.Equal(Path.Combine(repoDir, "agents"), health.AgentsWorkDir);
        Assert.Equal(expectedReason, health.SanitizedReason);
    }

    /// <summary>
    /// The four reconciliation notes really ARE the fixed prefix followed by the command
    /// path's OWN rejection message, lower-cased at its first character to continue the
    /// sentence. This pins the note literals to the state machine's messages, so a note that
    /// drifted away from its rejection (or a rejection message that changed) is caught here
    /// rather than silently accepted by the vector tests.
    /// </summary>
    [Fact]
    public void Health_ReconciliationNotes_ArePrefixPlusTheStateMachineMessage()
    {
        Assert.All(
            new[]
            {
                (Note: NoteOriginNotVerified, Rejection: OriginNotVerified),
                (Note: NoteOriginNotAdded, Rejection: OriginNotAdded),
                (Note: NoteOriginNotUpdated, Rejection: OriginNotUpdated),
                (Note: NoteOriginMismatch, Rejection: OriginMismatch),
            },
            pair => Assert.Equal(
                ReconciliationFailed
                + char.ToLowerInvariant(pair.Rejection[0])
                + pair.Rejection[1..],
                pair.Note));
    }

    /// <summary>
    /// THE SECRECY SWEEP: across EVERY health vector — each terminal outcome of every stage,
    /// each reconciliation branch (absent/add-failure/unclassifiable/repair/mismatch/skipped),
    /// each URL-resolver failure mode and each identity failure — the health API resolves NO
    /// credential and NO helper path. A SINGLE seam instance is reused so the counters
    /// accumulate across all of them: one stray resolution anywhere in the matrix fails here.
    /// </summary>
    /// <remarks>
    /// This is deliberately a sweep rather than an assertion bolted onto the concurrency
    /// fixtures: those drive the COMMAND path as well, which legitimately resolves a credential
    /// for its final launch, so a zero assertion there would be wrong (and would pass for the
    /// wrong reason if the health API started resolving too).
    /// </remarks>
    [Fact]
    public async Task Health_AcrossEveryVector_NeverResolvesACredentialOrHelperPath()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var credentialCalls = 0;
        var helperCalls = 0;
        var url = EligibleUrl;
        Func<GitProcessRequest, GitProcessResult> respond = HealthScript();

        try
        {
            GitOperations.ProcessRunner = (request, _) => Task.FromResult(respond(request));

            using var seam = new ConfigRepoGitOperations(
                RepoDir,
                () => url is null ? throw new InvalidOperationException("snapshot absent") : url,
                () => { credentialCalls++; return "ghp_secret"; },
                Log(),
                () => { helperCalls++; return "/helper"; },
                static () => { });

            // Every vector: (the resolved URL — null means a THROWING resolver, the script).
            var vectors = new (string? Url, Func<GitProcessRequest, GitProcessResult> Respond)[]
            {
                // Stage 3 — a foreign directory (asserted separately below).
                (EligibleUrl, HealthScript()),
                // Stage 4 terminals.
                (EligibleUrl, r => r.TokenizedArgs![1] == "--is-inside-work-tree"
                    ? new GitProcessResult(0, "false", "") : HealthAwareResult(r)),
                (EligibleUrl, r => r.TokenizedArgs![1] == "--is-inside-work-tree"
                    ? new GitProcessResult(0, "", "") : HealthAwareResult(r)),
                (EligibleUrl, r => r.TokenizedArgs![1] == "--is-inside-work-tree"
                    ? new GitProcessResult(1, "", "fatal") : HealthAwareResult(r)),
                (EligibleUrl, _ => throw new InvalidOperationException("boom")),
                // Stage 5 terminals.
                (EligibleUrl, HealthScript(toplevel: OutsideDir)),
                (EligibleUrl, HealthScript(toplevel: "")),
                (EligibleUrl, r => r.TokenizedArgs![1] == "--show-toplevel"
                    ? new GitProcessResult(1, "", "fatal") : HealthAwareResult(r)),
                (EligibleUrl, r => r.TokenizedArgs![1] == "--show-toplevel"
                    ? throw new InvalidOperationException("boom") : HealthAwareResult(r)),
                // Stage 6 — the URL-resolution branches.
                (null, HealthScript()),                                   // a THROWING resolver
                ("", HealthScript()),                                     // a MISSING url
                ("ftp://github.com/o/r.git", HealthScript()),             // a SANITIZER rejection
                ("ssh://git@github.com/org/config-repo.git", HealthScript()), // INELIGIBLE
                // Stage 6 — every reconciliation branch.
                (EligibleUrl, HealthScript()),                            // equivalent, untouched
                (EligibleUrl, HealthScript(originStdout: "")),            // ABSENT → add
                (EligibleUrl, r => r.TokenizedArgs![0] == "remote" && r.TokenizedArgs![1] != "get-url"
                    ? new GitProcessResult(1, "", "fatal")
                    : HealthAwareResult(r, originStdout: "")),            // an ADD failure
                (EligibleUrl, r => r.TokenizedArgs![0] == "remote"
                    ? new GitProcessResult(1, "", "fatal: unrelated") : HealthAwareResult(r)),
                (EligibleUrl, r => r.TokenizedArgs![0] == "remote"
                    ? throw new InvalidOperationException("boom") : HealthAwareResult(r)),
                (EligibleUrl, HealthScript(originStdout: CredentialBearingOrigin)), // REPAIR
                (EligibleUrl, r => r.TokenizedArgs![0] == "remote" && r.TokenizedArgs![1] != "get-url"
                    ? new GitProcessResult(1, "", "fatal")
                    : HealthAwareResult(r, originStdout: CredentialBearingOrigin)), // set-url fail
                (EligibleUrl, HealthScript(originStdout: "https://github.com/org/other.git")),
                // Stage 6b — the identity failures.
                (EligibleUrl, r => r.TokenizedArgs![0] == "config"
                    ? new GitProcessResult(1, "", "fatal") : HealthAwareResult(r)),
                (EligibleUrl, r => r.TokenizedArgs![0] == "config"
                    ? throw new InvalidOperationException("boom") : HealthAwareResult(r)),
            };

            foreach (var (vectorUrl, vectorRespond) in vectors)
            {
                url = vectorUrl;
                respond = vectorRespond;
                await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None));
            }

            // The Stage 2/3 rejections too.
            url = EligibleUrl;
            respond = HealthScript();
            await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(OutsideDir, CancellationToken.None));
            await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(null!, CancellationToken.None));

            // The vector list really did exercise the whole matrix.
            Assert.Equal(23, vectors.Length);

            Assert.Equal(0, credentialCalls);
            Assert.Equal(0, helperCalls);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 1 — disposal runs FIRST ─────────────────────────────────────

    /// <summary>
    /// The disposal check is the FIRST stage: a disposed seam reports the fixed result and
    /// launches NOTHING — even for a perfectly valid target directory.
    /// </summary>
    [Fact]
    public async Task Health_PostDisposal_ReturnsSeamDisposedWithoutLaunching()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(HealthAwareResult(request));
            };

            using var seam = CreateSeam();
            seam.Dispose();

            AssertUnhealthy(
                await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None)),
                "Seam disposed.");
            Assert.Empty(requests);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// Stage 1 PRECEDES Stage 2: a disposed seam handed a null target directory still reports
    /// <c>Seam disposed.</c>, never <c>Invalid arguments.</c>. Reordering the two stages flips
    /// this message and fails the test.
    /// </summary>
    [Fact]
    public async Task Health_PostDisposal_PrecedesArgumentValidation()
    {
        using var seam = CreateSeam();
        seam.Dispose();

        AssertUnhealthy(
            await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(null!, CancellationToken.None)),
            "Seam disposed.");
    }

    // ── Stage 2/3 — the argument basics and the containment ───────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData("\n ")]
    public async Task Health_NullOrWhitespaceTargetDir_ReturnsInvalidArgumentsWithoutLaunching(
        string? targetDir)
    {
        var run = await ProbeInAsync(HealthScript(), targetDir);

        AssertUnhealthy(run.Health, InvalidArguments);
        Assert.Empty(run.Requests);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A PATH-RELATED exception from the containment's <c>Canonicalize(targetDir)</c> maps to
    /// the same fixed <c>Invalid arguments.</c> — for every one of the five path exception
    /// types — and nothing is launched.
    /// </summary>
    [Theory]
    [MemberData(nameof(ThrowingCanonicalizers))]
    public async Task Health_PathExceptionCanonicalizingTargetDir_ReturnsInvalidArguments(
        string exceptionTypeName, Func<Exception> factory)
    {
        Assert.False(string.IsNullOrEmpty(exceptionTypeName)); // the type name labels the case

        var badTarget = Path.DirectorySeparatorChar + "bad-target-dir";
        var run = await ProbeInAsync(
            HealthScript(),
            badTarget,
            pathCanonicalizer: p => p == badTarget ? throw factory() : Path.GetFullPath(p));

        AssertUnhealthy(run.Health, InvalidArguments);
        Assert.Empty(run.Requests);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A target directory OUTSIDE the configured config repo is rejected by the SAME exact
    /// containment as the command path — and, critically, NO probe subprocess is launched: the
    /// seam never reconciles or identity-mutates a repository it does not own.
    /// </summary>
    [Fact]
    public async Task Health_TargetDirOutsideConfigRepo_RejectsWithZeroSubprocesses()
    {
        var run = await ProbeInAsync(HealthScript(), OutsideDir);

        AssertUnhealthy(run.Health, NotConfigRepo);
        Assert.Empty(run.Requests);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A DESCENDANT of the config repo is not the config repo: the containment is exact
    /// directory equality, never a prefix check, so a nested path is rejected with the same
    /// message and launches nothing.
    /// </summary>
    [Fact]
    public async Task Health_TargetDirNestedInsideConfigRepo_RejectsWithZeroSubprocesses()
    {
        var run = await ProbeInAsync(HealthScript(), Path.Combine(RepoDir, "agents"));

        AssertUnhealthy(run.Health, NotConfigRepo);
        Assert.Empty(run.Requests);
    }

    /// <summary>A trailing separator still canonicalizes to the same directory.</summary>
    [Fact]
    public async Task Health_TargetDirWithTrailingSeparator_IsContained()
    {
        var run = await ProbeInAsync(HealthScript(), RepoDirWithSeparator);

        AssertHealthy(run.Health, null);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>The default healthy script, as a reusable responder.</summary>
    private static Func<GitProcessRequest, GitProcessResult> HealthScript(
        string? toplevel = null, string? originStdout = EligibleUrl) =>
        request => HealthAwareResult(request, toplevel, originStdout);

    // ── Stage 4 — the worktree probe ──────────────────────────────────────

    /// <summary>
    /// The probe request has the EXACT credential-free shape: <c>rev-parse
    /// --is-inside-work-tree</c>, empty <c>Args</c>, the CONSTRUCTOR-canonicalized working
    /// directory (never the call-time spelling), and the scrubbed env plus
    /// <c>GIT_TERMINAL_PROMPT=0</c>.
    /// </summary>
    [Fact]
    public async Task Health_EverySubprocessIsCredentialFreeAndUsesTheCanonicalWorkingDirectory()
    {
        var previousEnv = SeedChildEnvVariables();
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        var credentialCalls = 0;
        var helperCalls = 0;
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(
                    HealthAwareResult(request, CanonicalizedRepoDir, CredentialBearingOrigin));
            };

            using var seam = CreateSeam(
                pathCanonicalizer: _ => CanonicalizedRepoDir,
                credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
                credentialHelperPath: () => { helperCalls++; return "/helper"; });

            var health = await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(
                RepoDirWithSeparator, CancellationToken.None));

            Assert.True(health.HasRepo);
            Assert.Null(health.SanitizedReason);

            // The repair path is exercised too, so the set-url launch is covered as well.
            AssertSequence(
                requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginSetUrl,
                IdentityEmailCommand, IdentityNameCommand);

            foreach (var request in requests)
            {
                Assert.Equal("git", request.Executable);
                Assert.Empty(request.Args);
                Assert.Equal(CanonicalizedRepoDir, request.WorkingDirectory);
                AssertChildEnv(request); // credential-free, GIT_TERMINAL_PROMPT=0
            }

            // The health API resolves NO credential and NO helper path, ever.
            Assert.Equal(0, credentialCalls);
            Assert.Equal(0, helperCalls);

            // The seam never WRITES a credential: the repair carries the SANITIZED url.
            Assert.DoesNotContain("ghp_secret", requests[3].TokenizedArgs!, StringComparer.Ordinal);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// An exit-0 probe reporting <c>false</c> is a BARE repository: not a worktree. The probe
    /// stops immediately — the top-level query never runs.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("false\n")]
    [InlineData("  false  ")]
    public async Task Health_ProbeReportsFalse_ReturnsNotAWorktreeAfterOneSubprocess(string stdout)
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![1] == "--is-inside-work-tree"
            ? new GitProcessResult(0, stdout, string.Empty)
            : HealthAwareResult(request));

        AssertUnhealthy(run.Health, NotAWorktree);
        AssertSequence(run.Requests, ProbeWorktree);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An exit-0 probe whose trimmed stdout is neither <c>true</c> nor <c>false</c> — INCLUDING
    /// empty and whitespace-only output, and including a case variant, since the comparison is
    /// ORDINAL — is unrecognized output. It is deliberately NOT folded into
    /// <c>Not a git worktree.</c>: the seam must never act on output it cannot read.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\t \r\n")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("true false")]
    [InlineData("truex")]
    public async Task Health_ProbeUnrecognizedStdout_ReturnsUnrecognizedRevParseOutput(string stdout)
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![1] == "--is-inside-work-tree"
            ? new GitProcessResult(0, stdout, string.Empty)
            : HealthAwareResult(request));

        AssertUnhealthy(run.Health, UnrecognizedRevParse);
        AssertSequence(run.Requests, ProbeWorktree);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A NONZERO probe exit is <c>Not a git worktree.</c> — regardless of what it printed on
    /// stdout, which is exactly what keeps a nonzero exit distinct from unrecognized output.
    /// </summary>
    [Theory]
    [InlineData(1, "")]
    [InlineData(128, "fatal: not a git repository")]
    [InlineData(128, "true")]
    [InlineData(255, "garbage")]
    public async Task Health_ProbeNonZeroExit_ReturnsNotAWorktree(int exitCode, string stdout)
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![1] == "--is-inside-work-tree"
            ? new GitProcessResult(exitCode, stdout, "fatal: nope")
            : HealthAwareResult(request));

        AssertUnhealthy(run.Health, NotAWorktree);
        AssertSequence(run.Requests, ProbeWorktree);
    }

    /// <summary>
    /// A LAUNCH failure (any non-cancellation exception from the runner) maps to the fixed
    /// <c>Git process failed to start.</c> — the exception's own text NEVER escapes.
    /// </summary>
    [Fact]
    public async Task Health_ProbeLaunchFailure_ReturnsFixedLaunchFailureMessage()
    {
        var run = await ProbeAsync(
            _ => throw new InvalidOperationException("boom: https://x-access-token:tok@github.com/o"));

        AssertUnhealthy(run.Health, LaunchFailed);
        AssertSequence(run.Requests, ProbeWorktree);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> from the probe subprocess PROPAGATES — the
    /// EXACT instance, never mapped to a launch failure and never recorded as a note.
    /// </summary>
    [Fact]
    public async Task Health_ProbeThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invoked = 0;
        var delegateOce = new OperationCanceledException("delegate cancelled");
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
            {
                invoked++;
                throw delegateOce;
            };

            using var seam = CreateSeam();
            using var liveCts = new CancellationTokenSource(); // the token stays LIVE

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, liveCts.Token));

            Assert.Same(delegateOce, ex);
            Assert.Equal(1, invoked);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>A PRE-cancelled token is observed at the probe launch.</summary>
    [Fact]
    public async Task Health_PreCancelledToken_PropagatesAtTheProbe()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(HealthAwareResult(request));
            };

            using var seam = CreateSeam();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, cts.Token));

            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.Empty(requests); // ExecuteProcessAsync observed it before the delegate
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 5 — the top-level containment verification ──────────────────

    /// <summary>
    /// A MATCHING top-level reports <c>RepoDir</c> as the TRIMMED git stdout VERBATIM — the
    /// canonicalization exists only for the COMPARISON. The fixture reports the root with
    /// surrounding whitespace AND a trailing separator: the whitespace is trimmed, the trailing
    /// separator SURVIVES (proving no re-canonicalization), and <c>AgentsWorkDir</c> is derived
    /// from that exact string.
    /// </summary>
    [Fact]
    public async Task Health_MatchingToplevel_ReportsTrimmedGitStdoutVerbatim()
    {
        var reported = RepoDir + Path.DirectorySeparatorChar;
        var run = await ProbeAsync(HealthScript(toplevel: "  " + reported + " \n"));

        Assert.True(run.Health.HasRepo);
        Assert.Equal(reported, run.Health.RepoDir);
        Assert.Equal(Path.Combine(reported, "agents"), run.Health.AgentsWorkDir);
        Assert.Null(run.Health.SanitizedReason);

        // The canonical form is observably DIFFERENT — a mutant re-canonicalizing the output
        // would report this instead.
        Assert.NotEqual(RepoDir, run.Health.RepoDir);
    }

    /// <summary>
    /// The same rule proved through the canonicalizer SEAM: every path canonicalizes to
    /// <see cref="CanonicalizedRepoDir"/>, so the reported root passes containment while being
    /// a completely different string — and it is THAT string the health report carries.
    /// </summary>
    [Fact]
    public async Task Health_MatchingToplevel_IsNotReCanonicalized()
    {
        var reported = Path.DirectorySeparatorChar + "reported-worktree-root";
        var run = await ProbeAsync(
            HealthScript(toplevel: reported + "\n"),
            pathCanonicalizer: _ => CanonicalizedRepoDir);

        Assert.True(run.Health.HasRepo);
        Assert.Equal(reported, run.Health.RepoDir);
        Assert.Equal(Path.Combine(reported, "agents"), run.Health.AgentsWorkDir);
        Assert.NotEqual(CanonicalizedRepoDir, run.Health.RepoDir);
        Assert.Null(run.Health.SanitizedReason);
    }

    /// <summary>
    /// A top-level resolving OUTSIDE the configured directory keeps <c>HasRepo=true</c> — the
    /// repository genuinely exists — but reports NO directories and SKIPS the reconciliation
    /// and the identity entirely: exactly TWO subprocesses ran. This is the unrelated-repository
    /// case: the seam must never reconcile or identity-mutate a repo it does not own.
    /// </summary>
    [Fact]
    public async Task Health_ToplevelOutsideConfigRepo_RetainsHasRepoAndSkipsReconciliationAndIdentity()
    {
        var run = await ProbeAsync(HealthScript(toplevel: OutsideDir));

        Assert.True(run.Health.HasRepo);
        Assert.Null(run.Health.RepoDir);
        Assert.Null(run.Health.AgentsWorkDir);
        Assert.Equal(ToplevelMismatch, run.Health.SanitizedReason);

        AssertSequence(run.Requests, ProbeWorktree, ProbeToplevel);
        Assert.Equal(2, run.Requests.Count);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An exit-0 top-level with EMPTY or whitespace-only stdout cannot identify a root.
    /// <c>HasRepo</c> drops to false and nothing further runs.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("\t \r\n")]
    public async Task Health_ToplevelEmptyOrWhitespace_ReturnsCouldNotDetermineWorktreeRoot(
        string stdout)
    {
        var run = await ProbeAsync(HealthScript(toplevel: stdout));

        AssertUnhealthy(run.Health, ToplevelUnknown);
        AssertSequence(run.Requests, ProbeWorktree, ProbeToplevel);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// The EMPTY-top-level guard is pinned INDEPENDENTLY of the canonicalizer. With the real
    /// <see cref="Path.GetFullPath(string)"/> an empty path happens to throw, which would let a
    /// deleted guard fall through to the identical message for the wrong reason. Here the
    /// canonicalizer seam maps EVERY input to the configured directory and never throws, so a
    /// missing guard would sail through the containment and report a HEALTHY repo whose root is
    /// the empty string. The guard must fire FIRST and report the fixed message.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public async Task Health_EmptyToplevel_GuardPrecedesCanonicalization(string stdout)
    {
        var run = await ProbeAsync(
            HealthScript(toplevel: stdout),
            pathCanonicalizer: _ => RepoDir); // never throws, always "contained"

        AssertUnhealthy(run.Health, ToplevelUnknown);
        AssertSequence(run.Requests, ProbeWorktree, ProbeToplevel);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A PATH-RELATED exception while canonicalizing the reported top-level maps to the same
    /// fixed message — for every one of the five path exception types.
    /// </summary>
    [Theory]
    [MemberData(nameof(ThrowingCanonicalizers))]
    public async Task Health_PathExceptionCanonicalizingToplevel_ReturnsCouldNotDetermineWorktreeRoot(
        string exceptionTypeName, Func<Exception> factory)
    {
        Assert.False(string.IsNullOrEmpty(exceptionTypeName)); // the type name labels the case

        var reported = Path.DirectorySeparatorChar + "unreadable-root";
        var run = await ProbeAsync(
            HealthScript(toplevel: reported),
            pathCanonicalizer: p => p == reported ? throw factory() : Path.GetFullPath(p));

        AssertUnhealthy(run.Health, ToplevelUnknown);
        AssertSequence(run.Requests, ProbeWorktree, ProbeToplevel);
    }

    /// <summary>A NONZERO top-level exit maps to the same fixed message.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task Health_ToplevelNonZeroExit_ReturnsCouldNotDetermineWorktreeRoot(int exitCode)
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![1] == "--show-toplevel"
            ? new GitProcessResult(exitCode, RepoDir, "fatal: nope")
            : HealthAwareResult(request));

        AssertUnhealthy(run.Health, ToplevelUnknown);
        AssertSequence(run.Requests, ProbeWorktree, ProbeToplevel);
    }

    /// <summary>
    /// A LAUNCH failure on the top-level query maps to the SAME fixed message — deliberately
    /// NOT to <c>Git process failed to start.</c>: once the worktree probe succeeded, an
    /// unreadable root is a root problem.
    /// </summary>
    [Fact]
    public async Task Health_ToplevelLaunchFailure_ReturnsCouldNotDetermineWorktreeRoot()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![1] == "--show-toplevel"
            ? throw new InvalidOperationException("boom")
            : HealthAwareResult(request));

        AssertUnhealthy(run.Health, ToplevelUnknown);
        AssertSequence(run.Requests, ProbeWorktree, ProbeToplevel);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>An OCE from the top-level subprocess PROPAGATES — the exact instance.</summary>
    [Fact]
    public async Task Health_ToplevelThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var delegateOce = new OperationCanceledException("delegate cancelled");
        var commands = new List<string[]>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                commands.Add(request.TokenizedArgs!.ToArray());
                return request.TokenizedArgs![1] == "--show-toplevel"
                    ? throw delegateOce
                    : Task.FromResult(HealthAwareResult(request));
            };

            using var seam = CreateSeam();
            using var liveCts = new CancellationTokenSource();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, liveCts.Token));

            Assert.Same(delegateOce, ex);
            Assert.Equal([ProbeWorktree, ProbeToplevel], commands);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 6 — the best-effort origin reconciliation ───────────────────

    /// <summary>
    /// FULL SUCCESS: an already-equivalent credential-free origin needs no repair, both
    /// identity commands succeed, and the report carries NO reason at all.
    /// </summary>
    [Fact]
    public async Task Health_FullSuccess_ReportsRepoDirsAndNullReason()
    {
        var run = await ProbeHealthyAsync();

        AssertHealthy(run.Health, null);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An INELIGIBLE (Branch B) sanitized URL — SSH, file, a local path, or an explicit
    /// non-443 port — SKIPS the origin reconciliation with the fixed note: the equivalence
    /// logic requires HTTPS/443 and the health API does not extend it. The IDENTITY still runs,
    /// and NO <c>get-url</c>/<c>add</c>/<c>set-url</c> is ever launched.
    /// </summary>
    [Theory]
    [MemberData(nameof(IneligibleUrlCases))]
    public async Task Health_IneligibleUrl_SkipsReconciliationButStillConfiguresIdentity(string url)
    {
        var run = await ProbeHealthyAsync(UrlResolver(url));

        AssertHealthy(run.Health, ReconciliationSkipped);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An ABSENT origin — every case-insensitive no-such-remote stderr pattern — is ADDED with
    /// the SANITIZED url, and a successful reconciliation produces NO note.
    /// </summary>
    [Theory]
    [InlineData("error: No such remote 'origin'")]
    [InlineData("fatal: no such remote 'origin'")]
    [InlineData("fatal: NOT A GIT REPOSITORY (or any of the parent directories): .git")]
    [InlineData("fatal: does not appear to be a git repository")]
    [InlineData("fatal: DOES NOT APPEAR TO BE A GIT REPOSITORY")]
    public async Task Health_AbsentOriginByStderr_AddsTheOriginWithNoNote(string stderr)
    {
        var run = await ProbeAsync(request =>
            request.TokenizedArgs![0] == "remote" && request.TokenizedArgs![1] == "get-url"
                ? new GitProcessResult(128, string.Empty, stderr)
                : HealthAwareResult(request));

        AssertHealthy(run.Health, null);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginAdd,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>An exit-0 inspection with EMPTY/whitespace stdout is ALSO an absent origin.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public async Task Health_AbsentOriginByEmptyStdout_AddsTheOriginWithNoNote(string stdout)
    {
        var run = await ProbeHealthyAsync(originStdout: stdout);

        AssertHealthy(run.Health, null);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginAdd,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A <c>remote add</c> failure — a nonzero exit — becomes a NOTE, never an abort: the
    /// identity still runs and <c>HasRepo</c> stays true with the directories reported.
    /// </summary>
    [Fact]
    public async Task Health_AddNonZeroExit_RecordsOriginNotAddedNote()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] switch
        {
            "remote" when request.TokenizedArgs![1] == "get-url" =>
                new GitProcessResult(0, string.Empty, string.Empty),
            "remote" => new GitProcessResult(1, string.Empty, "fatal: cannot add"),
            _ => HealthAwareResult(request),
        });

        AssertHealthy(run.Health, NoteOriginNotAdded);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginAdd,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>A <c>remote add</c> LAUNCH failure maps to the same note.</summary>
    [Fact]
    public async Task Health_AddLaunchFailure_RecordsOriginNotAddedNote()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] switch
        {
            "remote" when request.TokenizedArgs![1] == "get-url" =>
                new GitProcessResult(0, string.Empty, string.Empty),
            "remote" => throw new InvalidOperationException("boom"),
            _ => HealthAwareResult(request),
        });

        AssertHealthy(run.Health, NoteOriginNotAdded);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginAdd,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An UNCLASSIFIABLE <c>get-url</c> failure (a nonzero exit whose stderr matches no
    /// absence pattern) records the verification note — and NEVER adds an origin.
    /// </summary>
    [Theory]
    [InlineData("fatal: something else entirely")]
    [InlineData("")]
    [InlineData("error: unable to read config")]
    public async Task Health_UnclassifiableGetUrlFailure_RecordsOriginNotVerifiedNote(string stderr)
    {
        var run = await ProbeAsync(request =>
            request.TokenizedArgs![0] == "remote"
                ? new GitProcessResult(1, string.Empty, stderr)
                : HealthAwareResult(request));

        AssertHealthy(run.Health, NoteOriginNotVerified);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>A <c>get-url</c> LAUNCH failure records the same verification note.</summary>
    [Fact]
    public async Task Health_GetUrlLaunchFailure_RecordsOriginNotVerifiedNote()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] == "remote"
            ? throw new InvalidOperationException("boom")
            : HealthAwareResult(request));

        AssertHealthy(run.Health, NoteOriginNotVerified);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A credential-BEARING but structurally equivalent origin is REPAIRED with
    /// <c>remote set-url</c> carrying the sanitized url — a successful repair produces no note.
    /// </summary>
    [Fact]
    public async Task Health_CredentialBearingEquivalentOrigin_IsRepairedWithSetUrlAndNoNote()
    {
        var run = await ProbeHealthyAsync(originStdout: CredentialBearingOrigin);

        AssertHealthy(run.Health, null);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginSetUrl,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>A failing <c>set-url</c> — nonzero exit — records the update note.</summary>
    [Fact]
    public async Task Health_SetUrlNonZeroExit_RecordsOriginNotUpdatedNote()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] switch
        {
            "remote" when request.TokenizedArgs![1] == "get-url" =>
                new GitProcessResult(0, CredentialBearingOrigin, string.Empty),
            "remote" => new GitProcessResult(1, string.Empty, "fatal: cannot set-url"),
            _ => HealthAwareResult(request),
        });

        AssertHealthy(run.Health, NoteOriginNotUpdated);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginSetUrl,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>A <c>set-url</c> LAUNCH failure records the same note.</summary>
    [Fact]
    public async Task Health_SetUrlLaunchFailure_RecordsOriginNotUpdatedNote()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] switch
        {
            "remote" when request.TokenizedArgs![1] == "get-url" =>
                new GitProcessResult(0, CredentialBearingOrigin, string.Empty),
            "remote" => throw new InvalidOperationException("boom"),
            _ => HealthAwareResult(request),
        });

        AssertHealthy(run.Health, NoteOriginNotUpdated);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect, OriginSetUrl,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A PRESENT origin that is neither equivalent nor safely repairable records the MISMATCH
    /// note — and the seam NEVER rewrites it: no <c>add</c> and no <c>set-url</c> is launched.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/org/other-repo.git")]
    [InlineData("https://evil.example.com/org/config-repo.git")]
    [InlineData("https://github.com:8443/org/config-repo.git")]
    [InlineData("ssh://git@github.com/org/config-repo.git")]
    [InlineData("git@github.com:org/config-repo.git")]
    [InlineData("http://github.com/org/config-repo.git")]
    public async Task Health_OriginMismatch_RecordsMismatchNoteAndNeverRewrites(string origin)
    {
        var run = await ProbeHealthyAsync(originStdout: origin);

        AssertHealthy(run.Health, NoteOriginMismatch);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// An UNINITIALIZED provisioner (a non-cancellation resolver throw) records the fixed
    /// <c>Config repo not provisioned.</c> note — the resolver's own text NEVER escapes — the
    /// reconciliation is skipped, and the IDENTITY still runs.
    /// </summary>
    [Fact]
    public async Task Health_ThrowingUrlResolver_RecordsNotProvisionedAndStillConfiguresIdentity()
    {
        var run = await ProbeHealthyAsync(
            static () => throw new InvalidOperationException("config snapshot absent"));

        AssertHealthy(run.Health, NotProvisioned);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A MISSING resolved URL records the fixed absence note; the identity still runs.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t\n")]
    public async Task Health_MissingResolvedUrl_RecordsUrlUnavailableAndStillConfiguresIdentity(
        string? url)
    {
        var run = await ProbeHealthyAsync(UrlResolver(url));

        AssertHealthy(run.Health, UrlUnavailable);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// A SANITIZER-rejected URL records the <c>Invalid config repo URL: </c> note with the
    /// sanitizer's own (already redacted) message; the identity still runs.
    /// </summary>
    [Theory]
    [MemberData(nameof(SanitizeRejectedUrlCases))]
    public async Task Health_SanitizerRejectedUrl_RecordsInvalidUrlNoteAndStillConfiguresIdentity(
        string url, string expectedReason)
    {
        var sanitizerMessage =
            "Invalid --config-repo value: "
            + expectedReason
            + ". (The supplied value is redacted because it may contain credentials.)";

        var run = await ProbeHealthyAsync(UrlResolver(url));

        AssertHealthy(run.Health, "Invalid config repo URL: " + sanitizerMessage);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>An OCE from the URL resolver PROPAGATES — the exact instance.</summary>
    [Fact]
    public async Task Health_UrlResolverThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var resolverOce = new OperationCanceledException("resolver cancelled");
        var commands = new List<string[]>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                commands.Add(request.TokenizedArgs!.ToArray());
                return Task.FromResult(HealthAwareResult(request));
            };

            using var seam = CreateSeam(resolvedUrlResolver: () => throw resolverOce);
            using var liveCts = new CancellationTokenSource();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, liveCts.Token));

            Assert.Same(resolverOce, ex);

            // The probe ran; the identity did NOT (the OCE was not swallowed into a note).
            Assert.Equal([ProbeWorktree, ProbeToplevel], commands);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>An OCE from an ORIGIN subprocess PROPAGATES — never recorded as a note.</summary>
    [Fact]
    public async Task Health_OriginSubprocessThrowsOperationCanceled_Propagates()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var delegateOce = new OperationCanceledException("delegate cancelled");
        var commands = new List<string[]>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                commands.Add(request.TokenizedArgs!.ToArray());
                return request.TokenizedArgs![0] == "remote"
                    ? throw delegateOce
                    : Task.FromResult(HealthAwareResult(request));
            };

            using var seam = CreateSeam();
            using var liveCts = new CancellationTokenSource();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, liveCts.Token));

            Assert.Same(delegateOce, ex);
            Assert.Equal([ProbeWorktree, ProbeToplevel, OriginInspect], commands);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── Stage 6b — the local identity configuration ───────────────────────

    /// <summary>
    /// The identity commands are EXACTLY <c>config user.email copilothive-worker@local</c>
    /// followed by <c>config user.name "CopilotHive Worker"</c> — the email FIRST. The seam
    /// uses its OWN identity, deliberately not the shared
    /// <c>GitOperations.ConfigureLocalIdentity</c> pair.
    /// </summary>
    [Fact]
    public async Task Health_Identity_UsesTheExactCommandsInEmailThenNameOrder()
    {
        var run = await ProbeHealthyAsync();

        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);

        // The exact tokens — and NOT the legacy GitOperations identity.
        Assert.Equal(
            ["config", "user.email", "copilothive-worker@local"],
            run.Requests[3].TokenizedArgs!.ToArray());
        Assert.Equal(
            ["config", "user.name", "CopilotHive Worker"],
            run.Requests[4].TokenizedArgs!.ToArray());
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// The two identity commands are attempted INDEPENDENTLY: a failing <c>user.email</c> does
    /// NOT prevent the <c>user.name</c> attempt (this is where the new implementation diverges
    /// from the shared one, which stops after an email failure).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_IdentityEmailFailure_StillAttemptsTheNameCommand(bool launchFailure)
    {
        var run = await ProbeAsync(request =>
            request.TokenizedArgs![0] == "config" && request.TokenizedArgs![1] == "user.email"
                ? launchFailure
                    ? throw new InvalidOperationException("boom")
                    : new GitProcessResult(1, string.Empty, "fatal: cannot write config")
                : HealthAwareResult(request));

        AssertHealthy(run.Health, IdentityEmailFailed);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>A failing <c>user.name</c> alone records only its own fixed note.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_IdentityNameFailure_RecordsOnlyTheNameNote(bool launchFailure)
    {
        var run = await ProbeAsync(request =>
            request.TokenizedArgs![0] == "config" && request.TokenizedArgs![1] == "user.name"
                ? launchFailure
                    ? throw new InvalidOperationException("boom")
                    : new GitProcessResult(1, string.Empty, "fatal: cannot write config")
                : HealthAwareResult(request));

        AssertHealthy(run.Health, IdentityNameFailed);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// BOTH identity failures record BOTH fixed notes, joined in EXECUTION order.
    /// </summary>
    [Fact]
    public async Task Health_BothIdentityCommandsFail_RecordsBothNotesInOrder()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] == "config"
            ? new GitProcessResult(1, string.Empty, "fatal: cannot write config")
            : HealthAwareResult(request));

        AssertHealthy(run.Health, IdentityEmailFailed + "; " + IdentityNameFailed);
        Assert.Equal(
            "Identity configuration failed: user.email.; Identity configuration failed: user.name.",
            run.Health.SanitizedReason);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// The identity notes are FIXED text: neither the process stderr nor an exception message
    /// ever reaches the reported reason.
    /// </summary>
    [Fact]
    public async Task Health_IdentityFailureNotes_CarryNoStderrOrExceptionText()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] == "config"
            ? request.TokenizedArgs![1] == "user.email"
                ? new GitProcessResult(1, string.Empty, "fatal: SECRET-STDERR-MARKER")
                : throw new InvalidOperationException("SECRET-EXCEPTION-MARKER")
            : HealthAwareResult(request));

        AssertHealthy(run.Health, IdentityEmailFailed + "; " + IdentityNameFailed);
        Assert.DoesNotContain("SECRET-STDERR-MARKER", run.Health.SanitizedReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-EXCEPTION-MARKER", run.Health.SanitizedReason!, StringComparison.Ordinal);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>An OCE from an IDENTITY subprocess PROPAGATES — never recorded as a note.</summary>
    [Theory]
    [InlineData("user.email")]
    [InlineData("user.name")]
    public async Task Health_IdentitySubprocessThrowsOperationCanceled_Propagates(string key)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var delegateOce = new OperationCanceledException("delegate cancelled");
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
                request.TokenizedArgs![0] == "config" && request.TokenizedArgs![1] == key
                    ? throw delegateOce
                    : Task.FromResult(HealthAwareResult(request));

            using var seam = CreateSeam();
            using var liveCts = new CancellationTokenSource();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(
                () => seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, liveCts.Token));

            Assert.Same(delegateOce, ex);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ── The reason aggregation and the redaction scope ────────────────────

    /// <summary>
    /// THE AGGREGATION VECTOR: an unclassifiable origin inspection plus both identity failures
    /// produce the three notes joined in EXECUTION order with <c>"; "</c> — the origin note
    /// FIRST, then user.email, then user.name.
    /// </summary>
    [Fact]
    public async Task Health_FullFailureSequence_JoinsEveryNoteInExecutionOrder()
    {
        var run = await ProbeAsync(request => request.TokenizedArgs![0] switch
        {
            "remote" => new GitProcessResult(1, string.Empty, "fatal: something else entirely"),
            "config" => new GitProcessResult(1, string.Empty, "fatal: cannot write config"),
            _ => HealthAwareResult(request),
        });

        Assert.Equal(
            "Origin reconciliation failed: config repo origin could not be verified."
            + "; Identity configuration failed: user.email."
            + "; Identity configuration failed: user.name.",
            run.Health.SanitizedReason);

        // The report itself is still HEALTHY with both directories present.
        AssertHealthy(
            run.Health,
            run.Health.SanitizedReason);
        AssertSequence(
            run.Requests, ProbeWorktree, ProbeToplevel, OriginInspect,
            IdentityEmailCommand, IdentityNameCommand);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// The SKIPPED-reconciliation note also aggregates with the identity notes, in order.
    /// </summary>
    [Fact]
    public async Task Health_SkippedReconciliationPlusIdentityFailures_JoinInOrder()
    {
        var run = await ProbeAsync(
            request => request.TokenizedArgs![0] == "config"
                ? new GitProcessResult(1, string.Empty, string.Empty)
                : HealthAwareResult(request),
            UrlResolver("ssh://git@github.com/org/config-repo.git"));

        Assert.Equal(
            ReconciliationSkipped + "; " + IdentityEmailFailed + "; " + IdentityNameFailed,
            run.Health.SanitizedReason);
        AssertNoCredentialDelegates(run);
    }

    /// <summary>
    /// REDACTION SCOPE: <c>RepoDir</c> and <c>AgentsWorkDir</c> are PATHS returned as the
    /// trimmed git output and a derived path — they are NOT run through
    /// <see cref="CopilotHive.Services.GitUrlRedactor"/>. The fixture configures a config repo
    /// directory that literally embeds a credential-bearing URL shape, so a mutant that redacted
    /// either path would mangle it and fail here.
    /// </summary>
    [Fact]
    public async Task Health_RepoDirAndAgentsWorkDir_AreNotRedacted()
    {
        var prefix = OperatingSystem.IsWindows() ? @"C:\repo\" : "/repo/";
        var urlShapedDir = prefix + "https://x-access-token:ghp_secret@github.com/org";

        // The value really IS something the redactor would rewrite — so the assertion below
        // cannot pass for the wrong reason.
        Assert.NotEqual(urlShapedDir, CopilotHive.Services.GitUrlRedactor.Redact(urlShapedDir));

        var run = await ProbeInAsync(
            HealthScript(toplevel: urlShapedDir),
            urlShapedDir,
            configRepoDir: urlShapedDir,
            pathCanonicalizer: p => p);

        Assert.True(run.Health.HasRepo);
        Assert.Equal(urlShapedDir, run.Health.RepoDir);
        Assert.Equal(Path.Combine(urlShapedDir, "agents"), run.Health.AgentsWorkDir);
        Assert.Null(run.Health.SanitizedReason);
    }

    // ── Stage 6 — the SHARED per-instance semaphore ───────────────────────

    /// <summary>
    /// A gated runner whose <c>remote</c> commands block on a per-call TCS while every other
    /// command (the two rev-parse probes and the two identity commands) completes
    /// SYNCHRONOUSLY. That is what makes the concurrency assertions deterministic: an
    /// operation's call returns to the test only once it has reached a genuinely incomplete
    /// await.
    /// </summary>
    private sealed class GatedHealthRunner
    {
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly List<TaskCompletionSource> _entered = [];
        private readonly Lock _sync = new();

        public List<string[]> Commands { get; } = [];

        public int InspectionCount { get; private set; }

        public Task<GitProcessResult> Respond(GitProcessRequest request)
        {
            TaskCompletionSource gate;
            lock (_sync)
            {
                Commands.Add(request.TokenizedArgs!.ToArray());
                if (request.TokenizedArgs![0] != "remote")
                    return Task.FromResult(HealthAwareResult(request));

                var index = InspectionCount++;
                Grow(index);
                gate = _gates[index];
                _entered[index].TrySetResult();
            }

            return gate.Task.ContinueWith(_ => new GitProcessResult(0, EligibleUrl, string.Empty));
        }

        public Task Entered(int index)
        {
            lock (_sync)
            {
                Grow(index);
                return _entered[index].Task;
            }
        }

        public void Release(int index)
        {
            lock (_sync)
            {
                Grow(index);
                _gates[index].TrySetResult();
            }
        }

        public void ReleaseAll()
        {
            lock (_sync)
            {
                foreach (var gate in _gates)
                    gate.TrySetResult();
            }
        }

        private void Grow(int index)
        {
            while (_gates.Count <= index)
            {
                _gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                _entered.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }
    }

    /// <summary>
    /// Settles outstanding health probes so a failing assertion can never leave a blocked
    /// delegate that later advances with the real (un-faked) runner installed.
    /// </summary>
    private static async Task SettleHealthAsync(params Task<ConfigRepoHealth>?[] probes)
    {
        foreach (var probe in probes)
        {
            if (probe is null)
                continue;

            try
            {
                await probe;
            }
            catch (OperationCanceledException)
            {
                // Expected for the cancellation fixtures.
            }
            catch (Exception)
            {
                // Expected only if the test already failed mid-flight.
            }
        }
    }

    /// <summary>
    /// The health probe's ELIGIBLE reconciliation takes the SAME per-instance gate as the
    /// command path. A command-path operation is parked inside its origin inspection while
    /// HOLDING the semaphore; the probe then runs both rev-parse commands (they precede the
    /// gate) and parks — its origin inspection has NOT run. Removing the gate from the health
    /// API makes the second inspection appear immediately and fails the assertion.
    /// </summary>
    [Fact]
    public async Task Health_EligibleReconciliation_SerializesBehindTheCommandPath()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedHealthRunner();
        Task<ConfigRepoOpResult> command = null!;
        Task<ConfigRepoHealth> probe = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam();
            command = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            probe = seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None);

            // The probe reached its Stage 6 gate wait — and not one step further.
            Assert.Equal([OriginInspect, ProbeWorktree, ProbeToplevel], runner.Commands);
            Assert.Equal(1, runner.InspectionCount);

            runner.Release(0);
            Assert.True((await Bounded(command)).Success);

            await runner.Entered(1).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            runner.Release(1);
            var health = await Bounded(probe);
            AssertHealthy(health, null);

            // The two operations never interleaved.
            Assert.Equal(
                [
                    OriginInspect,
                    ProbeWorktree,
                    ProbeToplevel,
                    ["pull", "origin"],
                    OriginInspect,
                    IdentityEmailCommand,
                    IdentityNameCommand,
                ],
                runner.Commands);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleAsync(command);
            await SettleHealthAsync(probe);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// The reverse direction: the PROBE holds the gate while a command-path operation waits.
    /// The command's Stage 6b ref validation runs synchronously and then it parks on the
    /// semaphore, so its origin inspection has not run.
    /// </summary>
    [Fact]
    public async Task Health_ProbeHoldingTheGate_BlocksTheCommandPath()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedHealthRunner();
        Task<ConfigRepoHealth> probe = null!;
        Task<ConfigRepoOpResult> command = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam();
            probe = seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            command = seam.RunConfigRepoCommandAsync(
                ["pull", "origin", "main"], RepoDir, CancellationToken.None);

            Assert.Equal(
                [
                    ProbeWorktree,
                    ProbeToplevel,
                    OriginInspect,
                    ["check-ref-format", "--allow-onelevel", "main"],
                ],
                runner.Commands);
            Assert.Equal(1, runner.InspectionCount); // the command's inspection has NOT run

            runner.Release(0);
            AssertHealthy(await Bounded(probe), null);

            await runner.Entered(1).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            runner.Release(1);
            Assert.True((await Bounded(command)).Success);

            // The GATE-PROTECTED commands never interleaved: the probe's whole reconciliation
            // precedes the command's. The identity commands run OUTSIDE the gate, so their
            // position relative to the command's inspection is deliberately not pinned here —
            // only their own order is (asserted below).
            Assert.Equal(
                [OriginInspect, OriginInspect, ["pull", "origin", "main"]],
                runner.Commands.Where(c => c[0] is "remote" or "pull").ToList());

            var identity = runner.Commands.Where(c => c[0] == "config").ToList();
            Assert.Equal([IdentityEmailCommand, IdentityNameCommand], identity);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleHealthAsync(probe);
            await SettleAsync(command);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// An INELIGIBLE probe never takes the gate: it completes while a command-path operation
    /// is parked inside its origin inspection holding the semaphore.
    /// </summary>
    [Fact]
    public async Task Health_IneligibleProbe_DoesNotTakeTheGate()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedHealthRunner();
        Task<ConfigRepoOpResult> holder = null!;
        var helperCalls = 0;

        // The FIRST read is the eligible URL (the command path); every later read is ineligible.
        var urlReads = 0;
        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam(
                resolvedUrlResolver: () =>
                    Interlocked.Increment(ref urlReads) == 1
                        ? EligibleUrl
                        : "ssh://git@github.com/org/config-repo.git",
                credentialHelperPath: () => { helperCalls++; return "/helper"; });

            holder = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            var health = await Bounded(
                seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None));

            AssertHealthy(health, ReconciliationSkipped);
            Assert.Equal(
                [OriginInspect, ProbeWorktree, ProbeToplevel, IdentityEmailCommand, IdentityNameCommand],
                runner.Commands);

            // The probe completed WITHOUT the command path's credential resolution having run
            // (the holder is still parked before Stage 6e), so this zero is the PROBE's.
            Assert.Equal(0, helperCalls);

            runner.Release(0);
            Assert.True((await Bounded(holder)).Success);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleAsync(holder);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// THE ACQUIRED-FLAG RULE for the health API: a cancellation BEFORE acquisition propagates
    /// and releases NOTHING. The proof is that the gate's count is unchanged afterwards — a bug
    /// that released a semaphore it never owned would leave TWO permits and let two later
    /// eligible operations reconcile concurrently. The final assertions show only one does.
    /// </summary>
    [Fact]
    public async Task Health_CancellationBeforeGateAcquisition_PropagatesAndReleasesNothing()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedHealthRunner();
        Task<ConfigRepoOpResult> holder = null!;
        Task<ConfigRepoHealth> waiter = null!;
        Task<ConfigRepoHealth> third = null!;
        Task<ConfigRepoOpResult> fourth = null!;
        using var cts = new CancellationTokenSource();

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            using var seam = CreateSeam();

            holder = seam.RunConfigRepoCommandAsync(["pull"], RepoDir, CancellationToken.None);
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            // The probe runs both rev-parse commands and then parks ON the gate.
            waiter = seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, cts.Token);
            Assert.Equal([OriginInspect, ProbeWorktree, ProbeToplevel], runner.Commands);
            Assert.Equal(1, runner.InspectionCount);

            await cts.CancelAsync();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => Bounded(waiter));
            Assert.Equal(cts.Token, ex.CancellationToken);

            // The cancelled waiter reconciled nothing and configured no identity.
            Assert.Equal([OriginInspect, ProbeWorktree, ProbeToplevel], runner.Commands);
            Assert.Equal(1, runner.InspectionCount);

            runner.Release(0);
            Assert.True((await Bounded(holder)).Success);

            // The count is intact: the next operation acquires, and the one after it WAITS.
            third = seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None);
            await runner.Entered(1).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            fourth = seam.RunConfigRepoCommandAsync(["fetch"], RepoDir, CancellationToken.None);
            Assert.Equal(2, runner.InspectionCount); // NOT 3 — the fourth is still waiting

            runner.Release(1);
            AssertHealthy(await Bounded(third), null);
            await runner.Entered(2).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            runner.Release(2);
            Assert.True((await Bounded(fourth)).Success);
        }
        finally
        {
            runner.ReleaseAll();
            await SettleAsync(holder, fourth);
            await SettleHealthAsync(waiter, third);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// A cancellation AFTER acquisition RELEASES the gate in the finally: the probe is
    /// cancelled while parked inside its origin inspection, and a later operation still
    /// acquires the gate and completes.
    /// </summary>
    [Fact]
    public async Task Health_CancellationAfterGateAcquisition_ReleasesTheGate()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        Task<ConfigRepoHealth> cancelled = null!;

        try
        {
            GitOperations.ProcessRunner = async (request, ct) =>
            {
                if (request.TokenizedArgs![0] == "remote" && !entered.Task.IsCompleted)
                {
                    entered.TrySetResult();
                    try
                    {
                        await gate.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new OperationCanceledException(ct);
                    }
                }

                return HealthAwareResult(request);
            };

            using var seam = CreateSeam();
            cancelled = seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, cts.Token);
            await entered.Task.WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);

            await cts.CancelAsync();
            var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => Bounded(cancelled));
            Assert.Equal(cts.Token, ex.CancellationToken);

            // The gate was RELEASED by the finally: a fresh eligible operation completes.
            var next = await Bounded(seam.RunConfigRepoCommandAsync(
                ["fetch"], RepoDir, CancellationToken.None));
            Assert.True(next.Success);
        }
        finally
        {
            gate.TrySetResult();
            await SettleHealthAsync(cancelled);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// DISPOSAL while the probe HOLDS the gate: the semaphore is never disposed, so the
    /// in-flight probe completes NORMALLY (its identity commands still run and the real health
    /// is returned) and its release does not throw. A subsequent probe is rejected at entry.
    /// </summary>
    [Fact]
    public async Task Health_DisposalWhileHoldingTheGate_InFlightProbeStillCompletes()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var runner = new GatedHealthRunner();
        Task<ConfigRepoHealth> inFlight = null!;

        try
        {
            GitOperations.ProcessRunner = (request, _) => runner.Respond(request);

            var seam = CreateSeam();
            inFlight = seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None);

            // The probe HOLDS the gate (it is parked inside its origin inspection).
            await runner.Entered(0).WaitAsync(AwaitTimeout, TestContext.Current.CancellationToken);
            seam.Dispose();

            runner.Release(0);
            AssertHealthy(await Bounded(inFlight), null);

            Assert.Equal(
                [ProbeWorktree, ProbeToplevel, OriginInspect, IdentityEmailCommand, IdentityNameCommand],
                runner.Commands);

            // A SUBSEQUENT probe is rejected at ENTRY.
            AssertUnhealthy(
                await Bounded(seam.ProbeAndEnsureRepoHealthyAsync(RepoDir, CancellationToken.None)),
                "Seam disposed.");
        }
        finally
        {
            runner.ReleaseAll();
            await SettleHealthAsync(inFlight);
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    // ==================================================================
    // Slice 2c-b3 — CloneAsync: the OWNED-CONTAINER staging + atomic move.
    // ==================================================================

    /// <summary>Stage 3 / Stage 8 — an entry already occupies the clone target.</summary>
    private const string CloneTargetExists = "Config repo clone target already exists.";

    /// <summary>Stage 5 / Stage 8 — no owned staging container, or the move failed.</summary>
    private const string StagingUnavailable =
        "Config repo clone staging directory could not be created.";

    /// <summary>Stage 7 — the mandatory clone-time identity configuration failed.</summary>
    private const string CloneIdentityFailed = "Config repo clone identity configuration failed.";

    /// <summary>The staging container name parts and the ownership marker leaf.</summary>
    private const string ContainerInfix = ".copilothive-clone-";
    private const string ContainerSuffix = ".copilothive-work";
    private const string OwnerMarker = ".copilothive-owner";

    /// <summary>The container child the clone writes into — it must be EMPTY at clone time.</summary>
    private const string RepoChild = "repo";

    /// <summary>The file the fake <c>git clone</c> writes, standing in for a cloned worktree.</summary>
    private const string ClonedMarker = "cloned.txt";

    /// <summary>The leaf name of the clone target inside a <see cref="CloneFixture"/>.</summary>
    private const string TargetLeaf = "config-repo";

    /// <summary>An INELIGIBLE (Branch B) config repo URL: ssh, so no credential is ever read.</summary>
    private const string IneligibleUrl = "ssh://git@github.com/org/config-repo.git";

    /// <summary>
    /// A REAL temporary directory tree for the clone tests: the parent (<see cref="Root"/>) and
    /// the not-yet-existing clone target inside it. The staging containers, the ownership
    /// markers and the moved worktree are all real filesystem entries, so the marker-iff cleanup
    /// rule and the atomic move are observed rather than mocked.
    /// </summary>
    private sealed class CloneFixture : IDisposable
    {
        public CloneFixture(bool createParent = true)
        {
            Root = Path.Combine(
                Path.GetTempPath(), "copilothive-clone-" + Guid.NewGuid().ToString("N"));
            if (createParent)
                Directory.CreateDirectory(Root);

            TargetDir = Path.Combine(Root, TargetLeaf);
        }

        /// <summary>The canonical PARENT of the clone target.</summary>
        public string Root { get; }

        /// <summary>The clone target itself — deliberately absent until a clone succeeds.</summary>
        public string TargetDir { get; }

        /// <summary>Every staging container currently present in the parent.</summary>
        public string[] Containers =>
            Directory.Exists(Root)
                ? Directory.GetFileSystemEntries(Root, "*" + ContainerInfix + "*" + ContainerSuffix)
                : [];

        /// <summary>The container path this fixture's target produces for a given nonce.</summary>
        public string Container(string nonce) =>
            Path.Combine(Root, TargetLeaf + ContainerInfix + nonce + ContainerSuffix);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort test cleanup only.
            }
        }
    }

    /// <summary>
    /// A scripted nonce generator. It records its call count and flags an OVERRUN (a call past
    /// the script), so a test can prove the seam made EXACTLY the attempts it expects.
    /// </summary>
    private sealed class ScriptedNonces(params string[] nonces)
    {
        private int _index;

        public int Calls => _index;

        public bool Overrun { get; private set; }

        public string Next()
        {
            var index = _index++;
            if (index < nonces.Length)
                return nonces[index];

            Overrun = true;
            return "ffffffffffff";
        }
    }

    /// <summary>
    /// The default fake <c>git</c> for the clone path: a <c>clone</c> POPULATES its destination
    /// (the third token) exactly as the real git would, so a successful move really carries a
    /// worktree onto the target; every other command succeeds silently.
    /// </summary>
    private static GitProcessResult CloneAwareResult(GitProcessRequest request)
    {
        var tokens = request.TokenizedArgs!;
        if (tokens[0] == "clone")
        {
            Directory.CreateDirectory(tokens[2]);
            File.WriteAllText(Path.Combine(tokens[2], ClonedMarker), "cloned");
        }

        return new GitProcessResult(0, string.Empty, string.Empty);
    }

    /// <summary>The outcome of one clone run: the result plus every captured request.</summary>
    private sealed record CloneRun(ConfigRepoOpResult Result, List<GitProcessRequest> Requests);

    /// <summary>
    /// Runs <c>CloneAsync</c> against a RECORDING ProcessRunner (restored in a finally block),
    /// with the seam configured for a real <see cref="CloneFixture"/> target. Every clone seam is
    /// forwarded verbatim so a test can drive the nonce, the two target checks and the two
    /// staging primitives deterministically.
    /// <para><paramref name="targetDir"/> is sent to the SUT VERBATIM — including null.</para>
    /// </summary>
    private static async Task<CloneRun> CloneCapturingAsync(
        string? targetDir,
        string configRepoDir,
        Func<GitProcessRequest, GitProcessResult>? respond = null,
        Func<string?>? resolvedUrlResolver = null,
        Func<string?>? credentialResolver = null,
        Func<string>? credentialHelperPath = null,
        Func<string>? stagingNonceGenerator = null,
        Func<string, bool>? targetEntryExists = null,
        Func<string, bool>? stagingMarkerCreateNew = null,
        Func<string, bool>? stagingRepoChildCreate = null,
        Func<string, string>? pathCanonicalizer = null)
    {
        var originalRunner = GitOperations.ProcessRunner;
        var requests = new List<GitProcessRequest>();
        try
        {
            GitOperations.ProcessRunner = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult((respond ?? CloneAwareResult)(request));
            };

            using var seam = CreateSeam(
                pathCanonicalizer: pathCanonicalizer,
                configRepoDir: configRepoDir,
                resolvedUrlResolver: resolvedUrlResolver,
                credentialResolver: credentialResolver,
                credentialHelperPath: credentialHelperPath,
                stagingNonceGenerator: stagingNonceGenerator,
                targetEntryExists: targetEntryExists,
                stagingMarkerCreateNew: stagingMarkerCreateNew,
                stagingRepoChildCreate: stagingRepoChildCreate);

            var result = await Bounded(seam.CloneAsync(targetDir!, CancellationToken.None));
            return new CloneRun(result, requests);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>The common case: the clone target IS the configured config repo directory.</summary>
    private static Task<CloneRun> CloneAsync(
        CloneFixture fixture,
        Func<GitProcessRequest, GitProcessResult>? respond = null,
        Func<string?>? resolvedUrlResolver = null,
        Func<string?>? credentialResolver = null,
        Func<string>? credentialHelperPath = null,
        Func<string>? stagingNonceGenerator = null,
        Func<string, bool>? targetEntryExists = null,
        Func<string, bool>? stagingMarkerCreateNew = null,
        Func<string, bool>? stagingRepoChildCreate = null,
        string? targetDir = null) =>
        CloneCapturingAsync(
            targetDir ?? fixture.TargetDir,
            fixture.TargetDir,
            respond,
            resolvedUrlResolver,
            credentialResolver,
            credentialHelperPath,
            stagingNonceGenerator,
            targetEntryExists,
            stagingMarkerCreateNew,
            stagingRepoChildCreate);

    /// <summary>The clone's tokenized request: <c>clone &lt;sanitized-url&gt; &lt;container&gt;/repo</c>.</summary>
    private static string[] CloneCommand(string url, string container) =>
        ["clone", url, Path.Combine(container, RepoChild)];

    /// <summary>Stage 7 — the clone-time identity commands, in their required order.</summary>
    private static readonly string[] CloneIdentityEmail =
        ["config", "user.email", "copilothive-worker@local"];

    private static readonly string[] CloneIdentityName =
        ["config", "user.name", "CopilotHive Worker"];

    // ── Stage 1 / Stage 2 — disposal, the arguments and the containment ───

    [Fact]
    public async Task Clone_PostDisposal_ReturnsSeamDisposedAndStagesNothing()
    {
        using var fixture = new CloneFixture();
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("no subprocess may run");

            using var seam = CreateSeam(configRepoDir: fixture.TargetDir);
            seam.Dispose();

            AssertRejected(
                await Bounded(seam.CloneAsync(fixture.TargetDir, CancellationToken.None)),
                "Seam disposed.");
            Assert.Empty(fixture.Containers);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public async Task Clone_TargetDirNullOrWhitespace_ReturnsInvalidArguments(string? targetDir)
    {
        using var fixture = new CloneFixture();
        var run = await CloneCapturingAsync(targetDir, fixture.TargetDir);

        AssertRejected(run.Result, InvalidArguments);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    [Fact]
    public async Task Clone_TargetDirIsNotTheConfigRepo_ReturnsContainmentRejection()
    {
        using var fixture = new CloneFixture();
        var run = await CloneCapturingAsync(OutsideDir, fixture.TargetDir);

        AssertRejected(run.Result, NotConfigRepo);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    [Theory]
    [MemberData(nameof(ThrowingCanonicalizers))]
    public async Task Clone_PathExceptionFromCanonicalizer_ReturnsInvalidArguments(
        string exceptionTypeName, Func<Exception> factory)
    {
        Assert.False(string.IsNullOrEmpty(exceptionTypeName));

        using var fixture = new CloneFixture();
        var constructed = false;
        var run = await CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            pathCanonicalizer: path =>
            {
                // The CONSTRUCTOR canonicalization must succeed; only the call-time one throws.
                if (!constructed)
                {
                    constructed = true;
                    return path;
                }

                throw factory();
            });

        AssertRejected(run.Result, InvalidArguments);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    // ── Stage 3 — the clone-target validation ─────────────────────────────

    /// <summary>
    /// EVERY kind of existing entry at the target — a directory, a file, a live symlink and a
    /// DANGLING symlink — rejects the clone with the fixed message, launches NOTHING and stages
    /// NOTHING. The dangling link is the reason the real algorithm falls back to enumerating the
    /// parent: <c>File.GetAttributes</c> cannot resolve it.
    /// </summary>
    public static TheoryData<string> ExistingTargetKinds => new()
    {
        "directory",
        "file",
        "symlink",
        "dangling-symlink",
    };

    [Theory]
    [MemberData(nameof(ExistingTargetKinds))]
    public async Task Clone_TargetAlreadyExists_RejectsWithoutStagingOrResolving(string kind)
    {
        using var fixture = new CloneFixture();
        switch (kind)
        {
            case "directory":
                Directory.CreateDirectory(fixture.TargetDir);
                break;
            case "file":
                File.WriteAllText(fixture.TargetDir, "occupied");
                break;
            case "symlink":
                var linkTarget = Path.Combine(fixture.Root, "link-target");
                Directory.CreateDirectory(linkTarget);
                Directory.CreateSymbolicLink(fixture.TargetDir, linkTarget);
                break;
            case "dangling-symlink":
                Directory.CreateSymbolicLink(
                    fixture.TargetDir, Path.Combine(fixture.Root, "missing-target"));
                break;
            default:
                throw new InvalidOperationException($"Unhandled target kind '{kind}'.");
        }

        var urlCalls = 0;
        var credentialCalls = 0;
        var helperCalls = 0;
        var run = await CloneAsync(
            fixture,
            resolvedUrlResolver: () => { urlCalls++; return EligibleUrl; },
            credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
            credentialHelperPath: () => { helperCalls++; return "/helper"; });

        AssertRejected(run.Result, CloneTargetExists);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);

        // Stage 3 precedes Stage 4 entirely: no resolver and no helper was ever consulted.
        Assert.Equal(0, urlCalls);
        Assert.Equal(0, credentialCalls);
        Assert.Equal(0, helperCalls);
    }

    /// <summary>
    /// An ABSENT parent directory is an argument failure — the seam never creates the parent.
    /// </summary>
    [Fact]
    public async Task Clone_ParentDirectoryMissing_ReturnsInvalidArguments()
    {
        using var fixture = new CloneFixture(createParent: false);
        var urlCalls = 0;
        var run = await CloneAsync(
            fixture, resolvedUrlResolver: () => { urlCalls++; return EligibleUrl; });

        AssertRejected(run.Result, InvalidArguments);
        Assert.Empty(run.Requests);
        Assert.False(Directory.Exists(fixture.Root));
        Assert.Equal(0, urlCalls);
    }

    // ── The CANONICAL-PATH rule ───────────────────────────────────────────

    /// <summary>
    /// A trailing-separator or dot-segment spelling of <c>targetDir</c> produces EXACTLY the
    /// same canonical parent (the clone's working directory), the same container name, the same
    /// tokenized clone destination and the same move destination as the plain spelling.
    /// </summary>
    [Theory]
    [InlineData("separator")]
    [InlineData("dot-segment")]
    public async Task Clone_AlternateTargetSpelling_UsesTheCanonicalParentAndName(string spelling)
    {
        using var fixture = new CloneFixture();
        var targetDir = spelling switch
        {
            "separator" => fixture.TargetDir + Path.DirectorySeparatorChar,
            "dot-segment" => Path.Combine(fixture.Root, ".", TargetLeaf),
            _ => throw new InvalidOperationException($"Unhandled spelling '{spelling}'."),
        };

        var nonces = new ScriptedNonces("abc123def456");
        var run = await CloneAsync(
            fixture, stagingNonceGenerator: nonces.Next, targetDir: targetDir);

        Assert.True(run.Result.Success);
        Assert.False(nonces.Overrun);

        var container = fixture.Container("abc123def456");
        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, container),
            CloneIdentityEmail,
            CloneIdentityName);

        Assert.Equal(fixture.Root, run.Requests[0].WorkingDirectory);

        // The MOVE landed on the canonical target — not on the alternate spelling.
        Assert.True(File.Exists(Path.Combine(fixture.TargetDir, ClonedMarker)));
        Assert.Empty(fixture.Containers);
    }

    // ── Stage 4 — the URL / credential gating ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clone_MissingUrl_ReturnsUrlUnavailableWithoutStaging(string? url)
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(fixture, resolvedUrlResolver: UrlResolver(url));

        AssertRejected(run.Result, UrlUnavailable);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    [Theory]
    [MemberData(nameof(SanitizeRejectedUrlCases))]
    public async Task Clone_SanitizeRejectedUrl_ReturnsInvalidConfigRepoUrlWithoutStaging(
        string url, string expectedReason)
    {
        using var fixture = new CloneFixture();
        var sanitizerMessage =
            "Invalid --config-repo value: "
            + expectedReason
            + ". (The supplied value is redacted because it may contain credentials.)";

        var run = await CloneAsync(fixture, resolvedUrlResolver: UrlResolver(url));

        AssertRejected(run.Result, "Invalid config repo URL: " + sanitizerMessage);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
        Assert.DoesNotContain("ghp_supersecret", run.Result.SanitizedError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_ThrowingUrlResolver_ReturnsNotProvisionedWithoutStaging()
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture,
            resolvedUrlResolver: static () => throw new InvalidOperationException("snapshot absent"));

        AssertRejected(run.Result, NotProvisioned);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    [Fact]
    public async Task Clone_CancelledUrlResolver_PropagatesWithoutStaging()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            resolvedUrlResolver: () => throw new OperationCanceledException(cts.Token)));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Empty(fixture.Containers);
    }

    /// <summary>
    /// An INELIGIBLE (Branch B) URL clones with the PLAIN scrubbed environment: no credential is
    /// resolved, no helper path is read, and nothing is injected into any launch.
    /// </summary>
    [Fact]
    public async Task Clone_IneligibleUrl_UsesPlainScrubbedEnvAndResolvesNoCredential()
    {
        using var fixture = new CloneFixture();
        var previousEnv = SeedChildEnvVariables();
        try
        {
            var credentialCalls = 0;
            var helperCalls = 0;
            var nonces = new ScriptedNonces("0123456789ab");
            var run = await CloneAsync(
                fixture,
                resolvedUrlResolver: UrlResolver(IneligibleUrl),
                credentialResolver: () => { credentialCalls++; return "ghp_secret"; },
                credentialHelperPath: () => { helperCalls++; return "/helper"; },
                stagingNonceGenerator: nonces.Next);

            Assert.True(run.Result.Success);
            AssertSequence(
                run.Requests,
                CloneCommand(IneligibleUrl, fixture.Container("0123456789ab")),
                CloneIdentityEmail,
                CloneIdentityName);

            foreach (var request in run.Requests)
                AssertChildEnv(request);

            Assert.Equal(0, credentialCalls);
            Assert.Equal(0, helperCalls);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// An ELIGIBLE URL with a credential injects <c>GITHUB_CONFIG_REPO_TOKEN</c> and
    /// <c>GIT_ASKPASS</c> into the CLONE launch only — the identity commands stay
    /// credential-free — and the credential NEVER appears in any tokenized argument.
    /// </summary>
    [Fact]
    public async Task Clone_EligibleUrlWithCredential_InjectsIntoTheCloneLaunchOnly()
    {
        using var fixture = new CloneFixture();
        var previousEnv = SeedChildEnvVariables();
        try
        {
            var nonces = new ScriptedNonces("0123456789ab");
            var run = await CloneAsync(
                fixture,
                credentialResolver: static () => "ghp_supersecret",
                credentialHelperPath: static () => "/helper",
                stagingNonceGenerator: nonces.Next);

            Assert.True(run.Result.Success);
            var container = fixture.Container("0123456789ab");
            AssertSequence(
                run.Requests,
                CloneCommand(EligibleUrl, container),
                CloneIdentityEmail,
                CloneIdentityName);

            AssertChildEnvWithCredential(run.Requests[0], "ghp_supersecret", "/helper");
            AssertChildEnv(run.Requests[1]);
            AssertChildEnv(run.Requests[2]);

            // SECRECY: the sanitized, credential-free URL is the ONLY URL git ever sees.
            foreach (var request in run.Requests)
            {
                Assert.DoesNotContain(
                    "ghp_supersecret",
                    string.Join('\u0000', request.TokenizedArgs!),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    /// <summary>
    /// A null/whitespace credential runs the clone UNAUTHENTICATED: the plain scrubbed env, and
    /// the credential helper path is never read.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clone_EligibleUrlWithoutCredential_RunsUnauthenticated(string? credential)
    {
        using var fixture = new CloneFixture();
        var previousEnv = SeedChildEnvVariables();
        try
        {
            var helperCalls = 0;
            var run = await CloneAsync(
                fixture,
                credentialResolver: () => credential,
                credentialHelperPath: () => { helperCalls++; return "/helper"; });

            Assert.True(run.Result.Success);
            foreach (var request in run.Requests)
                AssertChildEnv(request);

            Assert.Equal(0, helperCalls);
        }
        finally
        {
            RestoreChildEnvVariables(previousEnv);
        }
    }

    [Fact]
    public async Task Clone_ThrowingCredentialResolver_ReturnsNotProvisionedWithoutStaging()
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture,
            credentialResolver: static () => throw new InvalidOperationException("no credential"));

        AssertRejected(run.Result, NotProvisioned);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Clone_MissingHelperPath_ReturnsHelperUnavailableWithoutStaging(string? helper)
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture,
            credentialResolver: static () => "ghp_secret",
            credentialHelperPath: () => helper!);

        AssertRejected(run.Result, HelperUnavailable);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    [Fact]
    public async Task Clone_ThrowingHelperDelegate_ReturnsHelperUnavailableWithoutStaging()
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture,
            credentialResolver: static () => "ghp_supersecret",
            credentialHelperPath: static () => throw new InvalidOperationException("no helper"));

        AssertRejected(run.Result, HelperUnavailable);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
        Assert.DoesNotContain("ghp_supersecret", run.Result.SanitizedError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_CancelledHelperDelegate_PropagatesWithoutStaging()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            credentialResolver: static () => "ghp_secret",
            credentialHelperPath: () => throw new OperationCanceledException(cts.Token)));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Empty(fixture.Containers);
    }

    // ── Stage 5 / 6 — the staging container and the clone subprocess ──────

    /// <summary>
    /// The EXACT staging + clone shape: the container carries the target's name plus the
    /// high-entropy infix/suffix, the ownership marker is its DIRECT CHILD, the git destination
    /// is the SIBLING <c>repo</c> child and it is EMPTY when git is launched, the tokenized
    /// request is exactly <c>[clone, &lt;sanitized-url&gt;, &lt;container&gt;/repo]</c>, and the
    /// working directory is the canonical PARENT.
    /// </summary>
    [Fact]
    public async Task Clone_StagingAndCloneRequest_HaveTheExactOwnedContainerShape()
    {
        using var fixture = new CloneFixture();
        var nonces = new ScriptedNonces("00aabb11ccdd");
        var markerWasSibling = false;
        var destinationWasEmpty = false;
        var destinationWasDirectory = false;

        var run = await CloneAsync(
            fixture,
            respond: request =>
            {
                if (request.TokenizedArgs![0] == "clone")
                {
                    var destination = request.TokenizedArgs[2];
                    destinationWasDirectory = Directory.Exists(destination);
                    destinationWasEmpty = destinationWasDirectory
                        && !Directory.EnumerateFileSystemEntries(destination).Any();
                    markerWasSibling = File.Exists(
                        Path.Combine(Path.GetDirectoryName(destination)!, OwnerMarker));
                }

                return CloneAwareResult(request);
            },
            stagingNonceGenerator: nonces.Next);

        Assert.True(run.Result.Success);
        Assert.False(nonces.Overrun);
        Assert.Equal(1, nonces.Calls);

        var container = fixture.Container("00aabb11ccdd");
        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, container),
            CloneIdentityEmail,
            CloneIdentityName);

        Assert.Equal("git", run.Requests[0].Executable);
        Assert.Empty(run.Requests[0].Args);
        Assert.Equal(fixture.Root, run.Requests[0].WorkingDirectory);

        // The identity commands run INSIDE the cloned worktree.
        Assert.Equal(Path.Combine(container, RepoChild), run.Requests[1].WorkingDirectory);
        Assert.Equal(Path.Combine(container, RepoChild), run.Requests[2].WorkingDirectory);

        Assert.True(destinationWasDirectory);
        Assert.True(destinationWasEmpty);
        Assert.True(markerWasSibling);
    }

    /// <summary>
    /// A SUCCESSFUL clone: exit 0, exit code 0, empty stdout and empty error; the cloned
    /// worktree sits at the target, the container is gone, and unrelated siblings are untouched.
    /// </summary>
    [Fact]
    public async Task Clone_Success_MovesTheRepoChildAndRemovesTheContainer()
    {
        using var fixture = new CloneFixture();
        var sibling = Path.Combine(fixture.Root, "unrelated-sibling");
        Directory.CreateDirectory(sibling);
        var siblingFile = Path.Combine(sibling, "keep.txt");
        File.WriteAllText(siblingFile, "keep");

        var run = await CloneAsync(fixture);

        Assert.True(run.Result.Success);
        Assert.Equal(0, run.Result.ExitCode);
        Assert.Equal("", run.Result.Stdout);
        Assert.Equal("", run.Result.SanitizedError);

        Assert.True(Directory.Exists(fixture.TargetDir));
        Assert.True(File.Exists(Path.Combine(fixture.TargetDir, ClonedMarker)));
        Assert.Empty(fixture.Containers);
        Assert.False(File.Exists(Path.Combine(fixture.TargetDir, OwnerMarker)));

        Assert.True(File.Exists(siblingFile)); // the pre-existing sibling is untouched
    }

    /// <summary>
    /// An OCCUPIED first candidate retries with a NEW nonce and succeeds — and the occupying
    /// FOREIGN container (unmarked) is left completely alone.
    /// </summary>
    [Fact]
    public async Task Clone_OccupiedNonce_RetriesAndNeverDeletesTheForeignContainer()
    {
        using var fixture = new CloneFixture();
        var occupied = fixture.Container("aaaaaaaaaaaa");
        Directory.CreateDirectory(occupied);
        var foreignFile = Path.Combine(occupied, "foreign.txt");
        File.WriteAllText(foreignFile, "not ours");

        var nonces = new ScriptedNonces("aaaaaaaaaaaa", "bbbbbbbbbbbb");
        var run = await CloneAsync(fixture, stagingNonceGenerator: nonces.Next);

        Assert.True(run.Result.Success);
        Assert.Equal(2, nonces.Calls);
        Assert.False(nonces.Overrun);
        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, fixture.Container("bbbbbbbbbbbb")),
            CloneIdentityEmail,
            CloneIdentityName);

        // The FOREIGN, unmarked container survives untouched; only OURS was cleaned up.
        Assert.True(File.Exists(foreignFile));
        Assert.Equal([occupied], fixture.Containers);
    }

    /// <summary>
    /// FIVE occupied candidates exhaust the bounded retry: the fixed staging message, no
    /// subprocess, and every foreign container still present.
    /// </summary>
    [Fact]
    public async Task Clone_FiveOccupiedNonces_ReturnsStagingUnavailable()
    {
        using var fixture = new CloneFixture();
        var scripted = new[] { "aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc", "dddddddddddd", "eeeeeeeeeeee" };
        foreach (var nonce in scripted)
            Directory.CreateDirectory(fixture.Container(nonce));

        var nonces = new ScriptedNonces(scripted);
        var run = await CloneAsync(fixture, stagingNonceGenerator: nonces.Next);

        AssertRejected(run.Result, StagingUnavailable);
        Assert.Empty(run.Requests);
        Assert.Equal(5, nonces.Calls);
        Assert.False(nonces.Overrun);
        Assert.Equal(5, fixture.Containers.Length);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    /// <summary>
    /// THE MARKER SEAM returning <c>false</c> on the first attempt — with the first candidate
    /// GENUINELY free, so nothing pre-created it — retries with a NEW nonce and succeeds. The
    /// unclaimed first container is NEVER deleted: without a successful CreateNew this
    /// invocation cannot prove it owns it.
    /// </summary>
    [Fact]
    public async Task Clone_MarkerSeamCollisionOnFirstAttempt_RetriesWithANewNonce()
    {
        using var fixture = new CloneFixture();
        var first = fixture.Container("aaaaaaaaaaaa");
        Assert.False(Directory.Exists(first)); // the candidate really is free

        var markerCalls = new List<string>();
        var nonces = new ScriptedNonces("aaaaaaaaaaaa", "bbbbbbbbbbbb");
        var run = await CloneAsync(
            fixture,
            stagingNonceGenerator: nonces.Next,
            stagingMarkerCreateNew: container =>
            {
                markerCalls.Add(container);
                if (markerCalls.Count == 1)
                    return false;

                File.WriteAllText(Path.Combine(container, OwnerMarker), "");
                return true;
            });

        Assert.True(run.Result.Success);
        Assert.Equal(2, nonces.Calls);
        Assert.Equal([first, fixture.Container("bbbbbbbbbbbb")], markerCalls);
        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, fixture.Container("bbbbbbbbbbbb")),
            CloneIdentityEmail,
            CloneIdentityName);

        // The UNCLAIMED container is never deleted; the OWNED one is gone.
        Assert.Equal([first], fixture.Containers);
        Assert.True(File.Exists(Path.Combine(fixture.TargetDir, ClonedMarker)));
    }

    /// <summary>
    /// THE REPO-CHILD SEAM receives the fully-joined <c>&lt;container&gt;/repo</c> path. When it
    /// fails AFTER the marker succeeded, the attempt's OWNED container is cleaned up before the
    /// retry — so no <c>.copilothive-clone-*</c> container survives the successful second attempt.
    /// </summary>
    [Fact]
    public async Task Clone_RepoChildSeamFailsAfterOwnership_CleansUpThenRetries()
    {
        using var fixture = new CloneFixture();
        var repoChildCalls = new List<string>();
        var nonces = new ScriptedNonces("aaaaaaaaaaaa", "bbbbbbbbbbbb");
        var run = await CloneAsync(
            fixture,
            stagingNonceGenerator: nonces.Next,
            stagingRepoChildCreate: path =>
            {
                repoChildCalls.Add(path);
                if (repoChildCalls.Count == 1)
                    return false;

                Directory.CreateDirectory(path);
                return true;
            });

        Assert.True(run.Result.Success);
        Assert.Equal(2, nonces.Calls);

        // The seam received the JOINED repo path — never the bare container path.
        Assert.Equal(
            [
                Path.Combine(fixture.Container("aaaaaaaaaaaa"), RepoChild),
                Path.Combine(fixture.Container("bbbbbbbbbbbb"), RepoChild),
            ],
            repoChildCalls);

        // The OWNED first container was deleted before the retry, and the second after the move.
        Assert.Empty(fixture.Containers);
        Assert.True(File.Exists(Path.Combine(fixture.TargetDir, ClonedMarker)));
    }

    /// <summary>
    /// EVERY invalid nonce shape counts as a COLLISION: empty, over-long, and outside the safe
    /// <c>[0-9a-f]</c> leaf alphabet (upper case, a separator, a dot segment, a wildcard).
    /// </summary>
    public static TheoryData<string> InvalidNonces => new()
    {
        "",
        new string('a', 33),
        "ABCDEF",
        "abc-def",
        "abc.def",
        "abc/def",
        "abc def",
        "ghijkl",
        "..",
    };

    [Theory]
    [MemberData(nameof(InvalidNonces))]
    public async Task Clone_InvalidNonce_CountsAsACollision(string nonce)
    {
        using var fixture = new CloneFixture();

        // FIVE invalid outputs exhaust the bound: nothing is staged and nothing is launched.
        var exhausted = await CloneAsync(fixture, stagingNonceGenerator: () => nonce);
        AssertRejected(exhausted.Result, StagingUnavailable);
        Assert.Empty(exhausted.Requests);
        Assert.Empty(fixture.Containers);

        // One invalid output followed by a valid one still succeeds.
        var nonces = new ScriptedNonces(nonce, "abcdefabcdef");
        var run = await CloneAsync(fixture, stagingNonceGenerator: nonces.Next);

        Assert.True(run.Result.Success);
        Assert.Equal(2, nonces.Calls);
        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, fixture.Container("abcdefabcdef")),
            CloneIdentityEmail,
            CloneIdentityName);
    }

    [Fact]
    public async Task Clone_ThrowingNonceGenerator_CountsAsACollision()
    {
        using var fixture = new CloneFixture();

        var exhausted = await CloneAsync(
            fixture,
            stagingNonceGenerator: static () => throw new InvalidOperationException("no entropy"));

        AssertRejected(exhausted.Result, StagingUnavailable);
        Assert.Empty(exhausted.Requests);
        Assert.Empty(fixture.Containers);

        var calls = 0;
        var run = await CloneAsync(
            fixture,
            stagingNonceGenerator: () =>
                ++calls == 1 ? throw new InvalidOperationException("no entropy") : "abcdefabcdef");

        Assert.True(run.Result.Success);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Clone_CancelledNonceGenerator_Propagates()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            stagingNonceGenerator: () => throw new OperationCanceledException(cts.Token)));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Empty(fixture.Containers);
    }

    // ── Stage 6 — the clone failures ──────────────────────────────────────

    /// <summary>
    /// A NON-ZERO clone exit preserves the exit code, redacts the output, and DELETES the owned
    /// container — nothing is moved and no identity command runs.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task Clone_NonZeroExit_PreservesTheCodeRedactsOutputAndCleansUp(int exitCode)
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture,
            respond: _ => new GitProcessResult(
                exitCode,
                "cloning https://x-access-token:ghp_secret@github.com/org/config-repo.git",
                "fatal: could not read from https://x-access-token:ghp_secret@github.com/o/r  \n"));

        Assert.False(run.Result.Success);
        Assert.Equal(exitCode, run.Result.ExitCode);
        Assert.Equal("cloning https://github.com/org/config-repo.git", run.Result.Stdout);
        Assert.Equal(
            "fatal: could not read from https://github.com/o/r", run.Result.SanitizedError);

        Assert.Single(run.Requests); // no identity command ran
        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    [Fact]
    public async Task Clone_LaunchFailure_ReturnsFixedMessageAndCleansUp()
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture, respond: static _ => throw new InvalidOperationException("git missing"));

        AssertRejected(run.Result, LaunchFailed);
        Assert.Single(run.Requests);
        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    /// <summary>
    /// A cancellation observed DURING the clone cleans the owned container up BEFORE the
    /// <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    [Fact]
    public async Task Clone_CancelledDuringTheClone_CleansUpBeforePropagating()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();
        var containerExistedDuringTheClone = false;

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            respond: request =>
            {
                containerExistedDuringTheClone =
                    Directory.Exists(Path.GetDirectoryName(request.TokenizedArgs![2])!);
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.True(containerExistedDuringTheClone);
        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    // ── Stage 7 — the clone-time identity ─────────────────────────────────

    /// <summary>
    /// An identity failure on EITHER command — a non-zero exit or a launch failure — aborts the
    /// move, deletes the container and reports the fixed message. BOTH commands are attempted:
    /// a failed <c>user.email</c> does NOT prevent the <c>user.name</c> attempt.
    /// </summary>
    [Theory]
    [InlineData("user.email", "nonzero")]
    [InlineData("user.name", "nonzero")]
    [InlineData("user.email", "launch")]
    [InlineData("user.name", "launch")]
    public async Task Clone_IdentityFailure_AbortsTheMoveAndCleansUp(string setting, string mode)
    {
        using var fixture = new CloneFixture();
        var run = await CloneAsync(
            fixture,
            respond: request =>
            {
                var tokens = request.TokenizedArgs!;
                if (tokens[0] == "config" && tokens[1] == setting)
                {
                    return mode switch
                    {
                        "nonzero" => new GitProcessResult(1, "", "fatal: identity"),
                        "launch" => throw new InvalidOperationException("git missing"),
                        _ => throw new InvalidOperationException($"Unhandled mode '{mode}'."),
                    };
                }

                return CloneAwareResult(request);
            });

        AssertRejected(run.Result, CloneIdentityFailed);

        // BOTH identity commands were attempted, in order, after the clone.
        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, Path.GetDirectoryName(run.Requests[0].TokenizedArgs![2])!),
            CloneIdentityEmail,
            CloneIdentityName);

        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    /// <summary>
    /// THE CANCELLATION PRECEDENCE: an <see cref="OperationCanceledException"/> from
    /// <c>user.email</c> ABORTS IMMEDIATELY — <c>user.name</c> never runs — and the container is
    /// cleaned up before the exception propagates.
    /// </summary>
    [Fact]
    public async Task Clone_CancelledDuringUserEmail_AbortsBeforeUserName()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();
        var requests = new List<string[]>();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            respond: request =>
            {
                requests.Add([.. request.TokenizedArgs!]);
                if (request.TokenizedArgs![0] == "config"
                    && request.TokenizedArgs[1] == "user.email")
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return CloneAwareResult(request);
            }));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Equal(2, requests.Count);              // clone + user.email ONLY
        Assert.Equal(CloneIdentityEmail, requests[1]);
        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    /// <summary>
    /// A cancellation from <c>user.name</c> also cleans up and propagates — the move never runs.
    /// </summary>
    [Fact]
    public async Task Clone_CancelledDuringUserName_CleansUpBeforePropagating()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            respond: request =>
            {
                if (request.TokenizedArgs![0] == "config"
                    && request.TokenizedArgs[1] == "user.name")
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return CloneAwareResult(request);
            }));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    // ── Stage 8 — the absence re-check and the atomic move ────────────────

    /// <summary>
    /// THE ENTRY-EXISTENCE DELEGATE'S SCOPE: it is invoked EXACTLY TWICE — the Stage 3 initial
    /// check and the Stage 8 re-check — always on the canonical target. The staging occupancy
    /// checks use the REAL algorithm, so the scripted <c>false → true</c> sequence lets the
    /// staging succeed and then fails the re-check: the container is deleted and the fixed
    /// message is returned.
    /// </summary>
    [Fact]
    public async Task Clone_TargetAppearsBeforeTheMove_RejectsAndCleansUp()
    {
        using var fixture = new CloneFixture();
        var checks = new List<string>();
        var nonces = new ScriptedNonces("aaaaaaaaaaaa");

        var run = await CloneAsync(
            fixture,
            stagingNonceGenerator: nonces.Next,
            targetEntryExists: path =>
            {
                checks.Add(path);
                return checks.Count > 1; // absent initially, PRESENT at the re-check
            });

        AssertRejected(run.Result, CloneTargetExists);

        // EXACTLY the two target checks, both on the canonical target.
        Assert.Equal([fixture.TargetDir, fixture.TargetDir], checks);

        AssertSequence(
            run.Requests,
            CloneCommand(EligibleUrl, fixture.Container("aaaaaaaaaaaa")),
            CloneIdentityEmail,
            CloneIdentityName);

        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    /// <summary>
    /// The delegate is consulted at Stage 3 too: a <c>true</c> on the FIRST call rejects before
    /// anything is staged, resolved or launched — one call and no more.
    /// </summary>
    [Fact]
    public async Task Clone_EntryExistenceDelegateTrueAtStage3_RejectsImmediately()
    {
        using var fixture = new CloneFixture();
        var checks = 0;
        var run = await CloneAsync(fixture, targetEntryExists: _ => { checks++; return true; });

        AssertRejected(run.Result, CloneTargetExists);
        Assert.Equal(1, checks);
        Assert.Empty(run.Requests);
        Assert.Empty(fixture.Containers);
    }

    /// <summary>
    /// A NON-cancellation exception from the entry-existence delegate PROPAGATES out of
    /// <c>CloneAsync</c> — it is never mapped to a fixed result.
    /// </summary>
    [Fact]
    public async Task Clone_ThrowingEntryExistenceDelegate_Propagates()
    {
        using var fixture = new CloneFixture();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            targetEntryExists: static _ => throw new InvalidOperationException("probe failed")));

        Assert.Equal("probe failed", ex.Message);
        Assert.Empty(fixture.Containers);
    }

    [Fact]
    public async Task Clone_CancelledEntryExistenceDelegate_Propagates()
    {
        using var fixture = new CloneFixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => CloneCapturingAsync(
            fixture.TargetDir,
            fixture.TargetDir,
            targetEntryExists: _ => throw new OperationCanceledException(cts.Token)));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Empty(fixture.Containers);
    }

    // ── The SANITIZED-URL selection (removal-proof) ───────────────────────

    /// <summary>
    /// Config repo URLs the sanitizer ACCEPTS but observably REWRITES, paired with their exact
    /// sanitized form. Each row's raw spelling differs from its sanitized spelling BYTE-FOR-BYTE,
    /// so a clone that launched with the raw resolver value instead of the sanitized one is
    /// observably different — which is exactly what makes the assertion removal-proof.
    /// </summary>
    /// <remarks>
    /// A credential-bearing HTTPS URL is deliberately NOT among these rows: the sanitizer
    /// REJECTS https userinfo outright (see <see cref="SanitizeRejectedUrlCases"/>), so it can
    /// never reach a successful clone. The credential-bearing case that IS accepted — and
    /// stripped — is the ssh form, whose password the sanitizer removes; it is the last row and
    /// carries <see cref="StrippedCredential"/>.
    /// </remarks>
    public static TheoryData<string, string> AcceptedButRewrittenUrlCases => new()
    {
        // The explicit default port is dropped.
        { "https://github.com:443/org/config-repo.git", "https://github.com/org/config-repo.git" },
        // The host is lower-cased.
        { "https://GITHUB.COM/org/config-repo.git", "https://github.com/org/config-repo.git" },
        { "https://GitHub.Com/org/config-repo.git", "https://github.com/org/config-repo.git" },
        // The dot segment is collapsed.
        { "https://github.com/org/./config-repo.git", "https://github.com/org/config-repo.git" },
        // Surrounding whitespace is trimmed.
        { "  https://github.com/org/config-repo.git  ", "https://github.com/org/config-repo.git" },
        // THE CREDENTIAL-BEARING accepted form: the ssh password is STRIPPED.
        {
            "ssh://git:" + StrippedCredential + "@github.com/org/config-repo.git",
            "ssh://git@github.com/org/config-repo.git"
        },
        // A credential-bearing ssh URL whose host is ALSO rewritten.
        {
            "ssh://git:" + StrippedCredential + "@GITHUB.COM/org/config-repo.git",
            "ssh://git@github.com/org/config-repo.git"
        },
        // The scp-style form normalizes to an ssh URL.
        { "git@github.com:org/config-repo.git", "ssh://git@github.com/org/config-repo.git" },
    };

    /// <summary>
    /// The credential embedded in the accepted-but-stripped ssh rows. It must never survive
    /// into a tokenized argument, an environment value, or a returned message.
    /// </summary>
    private const string StrippedCredential = "ghp_urlembeddedsecret";

    /// <summary>
    /// THE SANITIZED-URL GUARANTEE: the clone's SECOND token is the SANITIZED URL byte-for-byte
    /// — never the raw resolver value. Every row is a URL the sanitizer accepts but REWRITES, so
    /// a mutation that passed the raw resolved URL into the clone arguments changes the captured
    /// token and fails here. For the credential-bearing rows this is also the origin-secrecy
    /// proof: git writes its remote from this very argument, so a raw-URL clone would persist
    /// the credential into <c>.git/config</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedButRewrittenUrlCases))]
    public async Task Clone_AcceptedButRewrittenUrl_LaunchesWithTheSanitizedUrlNotTheRawValue(
        string rawUrl, string sanitizedUrl)
    {
        // The row really IS an accepted-but-rewritten pair — so the assertion below cannot pass
        // for the wrong reason (a row where raw == sanitized would be vacuous).
        Assert.Equal(sanitizedUrl, ConfigRepoUrlSanitizer.Sanitize(rawUrl));
        Assert.NotEqual(rawUrl, sanitizedUrl);

        using var fixture = new CloneFixture();
        var nonces = new ScriptedNonces("00112233445f");
        var run = await CloneAsync(
            fixture,
            resolvedUrlResolver: UrlResolver(rawUrl),
            stagingNonceGenerator: nonces.Next);

        Assert.True(run.Result.Success);

        var container = fixture.Container("00112233445f");
        AssertSequence(
            run.Requests,
            CloneCommand(sanitizedUrl, container),
            CloneIdentityEmail,
            CloneIdentityName);

        // The SECOND token is the sanitized URL byte-for-byte — never the raw spelling.
        Assert.Equal(sanitizedUrl, run.Requests[0].TokenizedArgs![1]);
        Assert.NotEqual(rawUrl, run.Requests[0].TokenizedArgs![1]);

        // SECRECY: no tokenized argument of ANY launch carries the stripped credential.
        foreach (var request in run.Requests)
        {
            Assert.DoesNotContain(
                StrippedCredential,
                string.Join('\u0000', request.TokenizedArgs!),
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(StrippedCredential, run.Result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(
            StrippedCredential, run.Result.SanitizedError, StringComparison.Ordinal);
    }

    // ── The marker-iff AND-condition: BOTH halves, in their MIXED states ──

    /// <summary>
    /// HALF A of the marker-iff rule (flag=TRUE, marker file ABSENT): the cleanup must NOT
    /// delete a container whose marker is gone, even though this invocation's CreateNew
    /// succeeded. The marker seam reports success WITHOUT creating the file, so the owned
    /// attempt reaches its cleanup with the flag set and no marker on disk.
    /// <para>
    /// A mutation that drops the <c>File.Exists</c> half (deleting on the flag ALONE) deletes
    /// the container here and fails this test.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The vector is driven through the REPO-CHILD failure path, which is the staging-loop
    /// cleanup invocation. The five attempts then exhaust the bound, so the fixed staging
    /// message is returned and no subprocess ever runs.
    /// </remarks>
    [Fact]
    public async Task Clone_OwnedAttemptWithoutAMarkerFile_RetainsTheContainer()
    {
        using var fixture = new CloneFixture();
        var nonces = new ScriptedNonces(FiveNonces);

        var run = await CloneAsync(
            fixture,
            stagingNonceGenerator: nonces.Next,
            // The flag is SET (true) but NO marker file is ever written to disk.
            stagingMarkerCreateNew: static _ => true,
            stagingRepoChildCreate: static _ => false);

        AssertRejected(run.Result, StagingUnavailable);
        Assert.Empty(run.Requests);
        Assert.Equal(5, nonces.Calls);

        // EVERY owned-but-unmarked container SURVIVES: the marker half of the AND is required.
        Assert.Equal(5, fixture.Containers.Length);
        foreach (var container in fixture.Containers)
        {
            Assert.True(Directory.Exists(container));
            Assert.False(File.Exists(Path.Combine(container, OwnerMarker)));
        }
    }

    /// <summary>
    /// HALF A at the CloneAsync-level cleanup: the owned container is handed to the clone stage
    /// and its marker is removed before a failure forces the finally to run. The container is
    /// RETAINED — the flag alone must never authorize a deletion.
    /// </summary>
    [Fact]
    public async Task Clone_MarkerRemovedBeforeTheCloneFails_RetainsTheContainer()
    {
        using var fixture = new CloneFixture();
        var nonces = new ScriptedNonces("aaaaaaaaaaaa");
        var container = fixture.Container("aaaaaaaaaaaa");

        var run = await CloneAsync(
            fixture,
            respond: request =>
            {
                // The marker exists up to this point (the real File.Open created it); deleting
                // it here puts the CloneAsync finally into the flag=true / file=absent state.
                File.Delete(Path.Combine(container, OwnerMarker));
                return new GitProcessResult(128, string.Empty, "fatal: clone failed");
            },
            stagingNonceGenerator: nonces.Next);

        Assert.False(run.Result.Success);
        Assert.Equal(128, run.Result.ExitCode);

        // The unmarked container SURVIVES even though this invocation owned it.
        Assert.Equal([container], fixture.Containers);
        Assert.False(File.Exists(Path.Combine(container, OwnerMarker)));
        Assert.False(Directory.Exists(fixture.TargetDir));
    }

    /// <summary>
    /// HALF B of the marker-iff rule (flag=FALSE, marker file PRESENT): a FOREIGN/forged marker
    /// exists inside the container, but THIS invocation's CreateNew did not succeed — the marker
    /// seam forges the file and then reports a collision-category failure, so the attempt's
    /// cleanup runs with the flag CLEAR and the marker present. The container must be RETAINED.
    /// <para>
    /// A mutation that drops the ownership-flag half (deleting on the marker file ALONE) deletes
    /// this foreign container and fails this test.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The seam THROWS a collision-category <see cref="IOException"/> rather than returning
    /// <c>false</c>: the <c>false</c> return takes the branch that deliberately abandons the
    /// container without invoking the cleanup at all, whereas the throw routes the attempt
    /// through the staging loop's catch and its finally — the invocation that genuinely observes
    /// the (flag=false, marker present) state.
    /// </remarks>
    [Fact]
    public async Task Clone_ForgedMarkerWithoutOwnership_RetainsTheForeignContainer()
    {
        using var fixture = new CloneFixture();
        var nonces = new ScriptedNonces(FiveNonces);
        var forged = new List<string>();

        var run = await CloneAsync(
            fixture,
            stagingNonceGenerator: nonces.Next,
            stagingMarkerCreateNew: container =>
            {
                // A FOREIGN actor's marker: present on disk, but NOT this invocation's.
                File.WriteAllText(Path.Combine(container, OwnerMarker), "foreign");
                forged.Add(container);
                throw new IOException("the marker already exists");
            });

        AssertRejected(run.Result, StagingUnavailable);
        Assert.Empty(run.Requests);
        Assert.Equal(5, nonces.Calls);
        Assert.Equal(5, forged.Count);

        // EVERY forged container SURVIVES with its marker: the ownership half is required.
        Assert.Equal(5, fixture.Containers.Length);
        foreach (var container in fixture.Containers)
        {
            Assert.True(Directory.Exists(container));
            Assert.True(File.Exists(Path.Combine(container, OwnerMarker)));
        }
    }

    /// <summary>
    /// The marker-iff rule stated as a MATRIX over its two independent inputs, so neither half
    /// can be removed without a failure: only (flag=true, marker present) deletes.
    /// </summary>
    [Fact]
    public async Task Clone_MarkerIffMatrix_OnlyOwnedAndMarkedContainersAreDeleted()
    {
        // (flag=true, marker PRESENT) → DELETED. The ordinary owned repo-child failure.
        using (var fixture = new CloneFixture())
        {
            var nonces = new ScriptedNonces("aaaaaaaaaaaa", "bbbbbbbbbbbb");
            var run = await CloneAsync(
                fixture,
                stagingNonceGenerator: nonces.Next,
                stagingRepoChildCreate: BuildFirstFailingRepoChild());

            Assert.True(run.Result.Success);
            Assert.Empty(fixture.Containers); // the owned, MARKED container was deleted
        }

        // (flag=true, marker ABSENT) → RETAINED.
        using (var fixture = new CloneFixture())
        {
            var nonces = new ScriptedNonces(FiveNonces);
            var run = await CloneAsync(
                fixture,
                stagingNonceGenerator: nonces.Next,
                stagingMarkerCreateNew: static _ => true,      // flag set, no file written
                stagingRepoChildCreate: static _ => false);

            AssertRejected(run.Result, StagingUnavailable);
            Assert.Equal(5, fixture.Containers.Length);
        }

        // (flag=false, marker PRESENT) → RETAINED.
        using (var fixture = new CloneFixture())
        {
            var nonces = new ScriptedNonces(FiveNonces);
            var run = await CloneAsync(
                fixture,
                stagingNonceGenerator: nonces.Next,
                stagingMarkerCreateNew: static container =>
                {
                    File.WriteAllText(Path.Combine(container, OwnerMarker), "foreign");
                    throw new IOException("the marker already exists");
                });

            AssertRejected(run.Result, StagingUnavailable);
            Assert.Equal(5, fixture.Containers.Length);
        }

        // (flag=false, marker ABSENT) → RETAINED.
        using (var fixture = new CloneFixture())
        {
            var nonces = new ScriptedNonces(FiveNonces);
            var run = await CloneAsync(
                fixture,
                stagingNonceGenerator: nonces.Next,
                stagingMarkerCreateNew: static _ => throw new IOException("collision"));

            AssertRejected(run.Result, StagingUnavailable);
            Assert.Equal(5, fixture.Containers.Length);
        }
    }

    /// <summary>
    /// Five DISTINCT valid nonces — one per staging attempt — so an exhausting vector leaves one
    /// observable container per attempt rather than reusing a name a later attempt would find
    /// already occupied.
    /// </summary>
    private static readonly string[] FiveNonces =
        ["aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc", "dddddddddddd", "eeeeeeeeeeee"];

    /// <summary>
    /// A repo-child seam that FAILS the first attempt and creates the child thereafter — the
    /// ordinary owned-cleanup-then-retry vector.
    /// </summary>
    private static Func<string, bool> BuildFirstFailingRepoChild()
    {
        var calls = 0;
        return path =>
        {
            if (++calls == 1)
                return false;

            Directory.CreateDirectory(path);
            return true;
        };
    }
}
