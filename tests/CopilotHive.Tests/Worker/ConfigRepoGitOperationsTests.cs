using System.Collections;
using System.Security;
using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;
using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Table-driven tests for the PURE VALIDATION seam <see cref="ConfigRepoGitOperations"/>
/// (slice 2c-b1b-i). No process execution exists in this slice: a fully-valid command
/// returns the "Execution deferred to slice 2c-b1b-ii." placeholder. Every test asserts
/// exact messages / exact outcome shapes so that deleting or reordering validation stages
/// breaks the suite (removal-proof).
/// </summary>
[Collection("EnvVarMutation")]
public sealed class ConfigRepoGitOperationsTests
{
    private const string PlaceholderError = "Execution deferred to slice 2c-b1b-ii.";
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
    /// no matter how deep the command travels through the pipeline.
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

        await RunAsync(seam, args);

        Assert.Equal(1, args.EnumerationCount);
    }

    /// <summary>
    /// A MUTATING source list proves the snapshot is authoritative: everything downstream reads
    /// the snapshot, so post-snapshot mutation of the source cannot change the outcome. If the
    /// implementation re-read <c>args</c> instead of the snapshot, the mutated list would turn
    /// the accepted <c>status</c> into a form-mismatch rejection.
    /// </summary>
    [Fact]
    public async Task Stage2c_SnapshotIsAuthoritative_SourceMutationAfterSnapshotIsIgnored()
    {
        using var seam = CreateSeam();
        var source = new MutatingArgs(["status"], mutation: ["status", "--porcelain"]);

        var result = await RunAsync(seam, source);

        Assert.Equal(PlaceholderError, result.SanitizedError);
        Assert.True(source.Mutated);
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
    // Grammar acceptance — the allowed forms (all reach the placeholder)
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

    [Theory]
    [MemberData(nameof(AcceptedCommandCases))]
    public async Task Stage7_AcceptedForms_ReturnPlaceholder(string[] args)
    {
        using var seam = CreateSeam();
        var result = await RunAsync(seam, args);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal(PlaceholderError, result.SanitizedError);
    }

    [Fact]
    public async Task Stage7_DepthPlus5_IsAccepted()
    {
        using var seam = CreateSeam();
        var result = await RunAsync(seam, new[] { "pull", "--depth", "+5" });
        Assert.Equal(PlaceholderError, result.SanitizedError);
    }

    // ------------------------------------------------------------------
    // Cancellation does NOT propagate in this slice
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stage7_CancelledToken_StillReturnsPlaceholder()
    {
        using var seam = CreateSeam();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await seam.RunConfigRepoCommandAsync(
            new[] { "pull", "origin", "main" }, RepoDir, cts.Token);

        Assert.Equal(PlaceholderError, result.SanitizedError);
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
        var withSeparator = OperatingSystem.IsWindows() ? @"C:\config-repo\" : "/config-repo/";
        using var seam = CreateSeam();
        var result = await RunInAsync(seam, new[] { "status" }, withSeparator);
        Assert.Equal(PlaceholderError, result.SanitizedError);
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