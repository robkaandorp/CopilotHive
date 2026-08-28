using System.Collections;
using System.Security;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Table-driven tests for the validation AND execution seam <see cref="ConfigRepoGitOperations"/>
/// (slices 2c-b1b-i + 2c-b1b-ii). The validation-stage contract tests (grammar messages,
/// containment, snapshot ordering, disposal at entry, canonicalization seam) assert exact
/// messages / exact outcome shapes so that deleting or reordering validation stages breaks
/// the suite (removal-proof). The execution-stage tests (2c-b1b-ii) assert the EXACT
/// <see cref="GitProcessRequest"/> shapes (ref-validation and final execution), the concrete
/// result mapping, the redaction boundary over process output, the cancellation precedence
/// (TCS-gated), the TCS-gated defensive snapshot, and the TCS-gated disposal-vs-in-flight
/// contract — all via the <see cref="GitOperations.ProcessRunner"/> seam (restored in a
/// finally block) and TCS gates for synchronization (no timing-based tests).
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoGitOperationsTests
{
    private const string LaunchFailed = "Git process failed to start.";
    private const string InvalidArguments = "Invalid arguments.";
    private const string NotConfigRepo =
        "Invalid git command: the working directory is not the config repository.";

    private static readonly string RepoDir = OperatingSystem.IsWindows()
        ? @"C:\config-repo"
        : "/config-repo";

    /// <summary>A fully-qualified directory that is NOT the config repo.</summary>
    private static readonly string OutsideDir = OperatingSystem.IsWindows()
        ? @"C:\other\dir"
        : "/other/dir";

    private static WorkerLogger Log() => new("Test");

    private static ConfigRepoGitOperations CreateSeam(
        Action? onDispose = null,
        Func<string, string>? pathCanonicalizer = null,
        string? configRepoDir = null) =>
        new(
            configRepoDir ?? RepoDir,
            static () => null,
            static () => null,
            Log(),
            static () => "/helper",
            onDispose ?? (static () => { }),
            pathCanonicalizer);

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
    // The delegates are NEVER invoked; onDispose is exempt
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunConfigRepoCommandAsync_NeverInvokesResolversOrCredentialHelperPath()
    {
        var urlCalls = 0;
        var credentialCalls = 0;
        var helperCalls = 0;
        using var seam = new ConfigRepoGitOperations(
            RepoDir,
            () => { urlCalls++; return null; },
            () => { credentialCalls++; return null; },
            Log(),
            () => { helperCalls++; return "/helper"; },
            static () => { });

        // The valid rows now REACH the real execution (slice 2c-b1b-ii), so the ProcessRunner
        // seam throws — the launch failure is mapped to the fixed message and the assertions
        // below hold for every row without ever starting a process.
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            foreach (var args in new[]
                     {
                         new[] { "pull", "origin", "main" },
                         new[] { "push", "origin", "main" },
                         new[] { "fetch", "--prune" },
                         new[] { "status" },
                         new[] { "https://x-access-token:tok@github.com/o" },
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

        Assert.Equal(0, urlCalls);
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

    public static TheoryData<string[]> AcceptedCommandCases => new()
    {
        // pull — options in every subset/order; positionals [origin] [ref].
        new[] { "pull" },
        new[] { "pull", "origin" },
        new[] { "pull", "origin", "main" },
        new[] { "pull", "--ff-only" },
        new[] { "pull", "--no-rebase", "--ff-only" },
        new[] { "pull", "--rebase", "--tags", "--prune" },
        new[] { "pull", "--prune", "--depth", "1", "origin", "main" },
        new[] { "pull", "--depth", "10", "--tags" },
        new[] { "pull", "origin", "v1.0" },
        // fetch — options and positionals.
        new[] { "fetch" },
        new[] { "fetch", "origin" },
        new[] { "fetch", "--tags", "--prune" },
        new[] { "fetch", "--depth", "2", "origin" },
        new[] { "fetch", "--prune", "origin", "main" },
        // push — the exact form.
        new[] { "push", "origin", "main" },
        // The credential-free EXACT shapes.
        new[] { "checkout", "--", "agents/" },
        new[] { "add", "agents/*.agents.md" },
        new[] { "diff", "--cached", "--name-only", "-z" },
        new[] { "commit", "-m", "update agents" },
        new[] { "merge", "--abort" },
        new[] { "status" },
    };

    /// <summary>
    /// Every accepted form reaches the real execution — the ProcessRunner seam throws, and the
    /// launch failure surfaces as the FIXED result. (The placeholder was removed by slice
    /// 2c-b1b-ii; the accepted-form grammar contract is now asserted through the execution path.)
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedCommandCases))]
    public async Task Stage7_AcceptedForms_ReachExecution_LaunchFailureReturnsFixedMessage(string[] args)
    {
        var originalRunner = GitOperations.ProcessRunner;
        try
        {
            GitOperations.ProcessRunner = (_, _) =>
                throw new InvalidOperationException("boom");

            using var seam = CreateSeam();
            var result = await RunAsync(seam, args);

            AssertRejected(result, LaunchFailed);
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
            AssertRejected(result, LaunchFailed);
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
    /// variable preserved, no extra entries. The exit-0 subprocess lets the command
    /// continue, so a second (final-execution) request must carry the SNAPSHOT.
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
                return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));
            };

            // Constructor input: the raw RepoDir; call-time spelling: the trailing-separator
            // form. The canonicalizer seam maps BOTH to the DISTINCT CanonicalizedRepoDir —
            // a request built from either raw spelling (constructor input or call-time
            // string) is observably different and fails the WorkingDirectory assertion.
            using var seam = CreateSeam(pathCanonicalizer: _ => CanonicalizedRepoDir);
            var result = await RunInAsync(seam, new[] { "pull", "origin", "main" }, RepoDirWithSeparator);

            // Exit 0 from check-ref-format: the command continues to Stage 7 (two launches).
            Assert.True(result.Success);
            Assert.Equal(2, requests.Count);

            var refValidation = requests[0];
            Assert.Equal("git", refValidation.Executable);
            Assert.Empty(refValidation.Args);
            Assert.Equal(CanonicalizedRepoDir, refValidation.WorkingDirectory);
            Assert.NotEqual(RepoDir, CanonicalizedRepoDir);       // the spellings are distinct
            Assert.NotEqual(RepoDirWithSeparator, CanonicalizedRepoDir);
            Assert.Equal(
                new[] { "check-ref-format", "--allow-onelevel", "main" },
                refValidation.TokenizedArgs!.ToArray());

            // Env = the COMPLETE scrubbed inherited snapshot + GIT_TERMINAL_PROMPT=0.
            AssertChildEnv(refValidation);

            // The final execution carries the snapshot verbatim and the same child env.
            Assert.Equal(new[] { "pull", "origin", "main" },
                requests[1].TokenizedArgs!.ToArray());
            AssertChildEnv(requests[1]);
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
                if (requests.Count == 1)
                {
                    // The ref-validation subprocess blocks until the caller mutates the list.
                    refValidationEntered.TrySetResult();
                    return releaseRefValidation.Task.ContinueWith(
                        _ => new GitProcessResult(0, string.Empty, string.Empty));
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
            Assert.Equal(2, requests.Count);
            Assert.Equal(
                new[] { "check-ref-format", "--allow-onelevel", "main" },
                requests[0].TokenizedArgs!.ToArray());
            Assert.Equal(
                new[] { "pull", "origin", "main" },
                requests[1].TokenizedArgs!.ToArray());
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
    /// ACROSS the Stage 6→Stage 7 boundary: the operation is gated while BLOCKED at Stage 6
    /// (the ref-validation subprocess of a ref-bearing command); <see cref="ConfigRepoGitOperations.Dispose"/>
    /// is called while it is blocked there; the Stage 6 gate is released; the test then
    /// asserts Stage 7 STILL LAUNCHES (a second request is received — a mutant that rechecks
    /// <c>_disposed</c> before Stage 7 would return <c>Seam disposed.</c> and never launch
    /// the second request); the in-flight operation returns its REAL result (not
    /// <c>Seam disposed.</c>), while a SUBSEQUENT call returns exactly
    /// <c>ConfigRepoOpResult(false, -1, "", "Seam disposed.")</c>.
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
                if (requests.Count == 1)
                {
                    // Block DURING Stage 6 (the ref-validation subprocess) — AFTER the
                    // Stage 1 disposed-check has already passed.
                    stage6Entered.TrySetResult();
                    return releaseStage6.Task.ContinueWith(
                        _ => new GitProcessResult(0, string.Empty, string.Empty));
                }

                stage7Entered.TrySetResult();
                return releaseStage7.Task.ContinueWith(
                    _ => new GitProcessResult(0, "in-flight stdout", string.Empty));
            };

            var seam = CreateSeam();
            execution = seam.RunConfigRepoCommandAsync(
                new[] { "pull", "origin", "main" }, RepoDir, CancellationToken.None);

            // The operation is IN FLIGHT at Stage 6 (past the entry disposed-check) — dispose.
            await stage6Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            seam.Dispose();

            // Release Stage 6 — Stage 7 MUST STILL LAUNCH (disposal is NOT rechecked).
            releaseStage6.TrySetResult();
            await stage7Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            releaseStage7.TrySetResult();

            var result = await execution;

            // Both launches happened — the ref-validation subprocess and the final execution.
            Assert.Equal(2, requests.Count);
            Assert.Equal(
                new[] { "check-ref-format", "--allow-onelevel", "main" },
                requests[0].TokenizedArgs!.ToArray());
            Assert.Equal(
                new[] { "pull", "origin", "main" },
                requests[1].TokenizedArgs!.ToArray());

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
}