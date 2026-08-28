using CopilotHive.Git;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests;

/// <summary>
/// Flow A of the credential-URL redaction goal: the orchestrator Brain's clone path.
/// <para>
/// <c>PipelineHelpers.InjectTokenIntoUrl</c> hands <see cref="BrainRepoManager.EnsureCloneAsync"/>
/// a credential-bearing remote URL. These tests drive the manager through its optional
/// <see cref="BrainGitRequest"/>/<see cref="BrainGitResult"/> runner seam — which returns RAW
/// process results — so the PRODUCTION log/exception construction (and therefore the production
/// redaction) is what gets asserted. No git process, no network, no live credential.
/// </para>
/// </summary>
public sealed class BrainRepoManagerRedactionTests : IDisposable
{
    private const string Token = "ghp_brain_clone_secret";
    private const string CredentialUrl =
        $"https://x-access-token:{Token}@github.com/acme/widgets.git";
    private const string RedactedUrl = "https://github.com/acme/widgets.git";

    private readonly string _tempDir;

    public BrainRepoManagerRedactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    /// <summary>
    /// Records every git invocation the manager makes and replies with scripted RAW results.
    /// </summary>
    private sealed class RecordingGitRunner
    {
        private readonly Func<BrainGitRequest, BrainGitResult> _respond;

        public RecordingGitRunner(Func<BrainGitRequest, BrainGitResult>? respond = null)
            => _respond = respond ?? (_ => new BrainGitResult(0, string.Empty, string.Empty));

        /// <summary>Every request the manager issued, in order.</summary>
        public List<BrainGitRequest> Requests { get; } = [];

        public BrainGitResult Run(BrainGitRequest request)
        {
            Requests.Add(request);
            return _respond(request);
        }
    }

    // ── The runner seam itself ────────────────────────────────────────────────

    /// <summary>
    /// The seam is an OPTIONAL TRAILING parameter: the two-argument constructor every existing
    /// call site uses still compiles and still runs the real process-based runner (null seam).
    /// </summary>
    [Fact]
    public void Constructor_WithoutRunner_IsStillAvailable()
    {
        var manager = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());

        Assert.EndsWith("repos", manager.WorkDirectory.Replace('\\', '/'));
    }

    /// <summary>
    /// The seam receives the RAW, credential-bearing URL — redaction is a message-construction
    /// concern only and must never change what is handed to git.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_PassesRawCredentialUrlToTheRunner()
    {
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        var cloneRequest = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(CredentialUrl, cloneRequest.Arguments);
        Assert.Contains(Token, string.Join(' ', cloneRequest.Arguments));
    }

    // ── Boundary 1: the pre-clone log ─────────────────────────────────────────

    [Fact]
    public async Task EnsureCloneAsync_PreCloneLog_IsCredentialFree()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Creating Brain clone"));
        Assert.DoesNotContain(Token, entry.Message);
        Assert.DoesNotContain("x-access-token", entry.Message);
        // The credential-FREE URL may (and should) still be logged.
        Assert.Contains(RedactedUrl, entry.Message);
    }

    [Fact]
    public async Task EnsureCloneAsync_NoLogEntryAnywhereCarriesTheCredential()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(Token));
    }

    /// <summary>
    /// A query-token clone URL is redacted by the same log boundary.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_QueryTokenUrl_PreCloneLogIsCredentialFree()
    {
        const string QueryUrl = "https://github.com/acme/widgets.git?token=ghp_query_secret&ref=main";
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", QueryUrl, "main", TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Creating Brain clone"));
        Assert.DoesNotContain("ghp_query_secret", entry.Message);
        Assert.Contains("https://github.com/acme/widgets.git?ref=main", entry.Message);
    }

    // ── Boundary 2: the RunGitAsync exception construction ────────────────────

    /// <summary>
    /// The failure message embeds the complete git ARGUMENT LIST, which for a clone contains the
    /// credential-bearing remote. It must be redacted where the message is constructed.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_WhenCloneFails_ExceptionMessageHasNoCredentialFromArguments()
    {
        var runner = new RecordingGitRunner(_ =>
            new BrainGitResult(128, string.Empty, "fatal: repository not found"));
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureCloneAsync(
                "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.Contains(RedactedUrl, ex.Message);
        // Behavior is otherwise unchanged: exit code and stderr still surface.
        Assert.Contains("exit 128", ex.Message);
        Assert.Contains("fatal: repository not found", ex.Message);
    }

    /// <summary>
    /// git echoes the remote it was handed back through STDERR. That copy is redacted too.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_WhenCloneFails_ExceptionMessageHasNoCredentialFromStderr()
    {
        var runner = new RecordingGitRunner(_ => new BrainGitResult(
            128,
            string.Empty,
            $"fatal: unable to access '{CredentialUrl}/': The requested URL returned error: 403"));
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureCloneAsync(
                "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", ex.Message);
        Assert.Contains("The requested URL returned error: 403", ex.Message);
    }

    /// <summary>
    /// A failing <c>fetch</c> on an EXISTING clone goes through the same construction boundary.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_ExistingClone_FetchFailureMessageIsRedacted()
    {
        var manager0 = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());
        Directory.CreateDirectory(Path.Combine(manager0.GetClonePath("widgets"), ".git"));

        var runner = new RecordingGitRunner(_ => new BrainGitResult(
            1, string.Empty, $"fatal: could not read from '{CredentialUrl}'"));
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureCloneAsync(
                "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains(RedactedUrl, ex.Message);
    }

    /// <summary>
    /// The redaction must not disturb the "branch not found in upstream" recovery: that branch
    /// is selected on message CONTENT, and redaction only removes credentials.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_BranchNotFoundInUpstream_StillRetriesWithoutBranchFlag()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("--branch")
                ? new BrainGitResult(
                    128,
                    string.Empty,
                    $"fatal: Remote branch main not found in upstream origin ({CredentialUrl})")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        var path = await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        Assert.Equal(manager.GetClonePath("widgets"), path);
        // The retry happened: a clone WITHOUT --branch was issued.
        Assert.Contains(
            runner.Requests,
            r => r.Arguments.Contains("clone") && !r.Arguments.Contains("--branch"));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(Token));
    }

    // ── Behavior preservation ─────────────────────────────────────────────────

    /// <summary>
    /// A successful run is unchanged: the clone path is returned and the identity configuration
    /// commands still run in order.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_Success_IssuesTheSameGitCommandsAsBefore()
    {
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var path = await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        Assert.Equal(manager.GetClonePath("widgets"), path);
        Assert.Collection(
            runner.Requests.Select(r => string.Join(' ', r.Arguments)),
            first => Assert.Equal($"clone --branch main {CredentialUrl} widgets", first),
            second => Assert.Equal("config user.email copilothive@local", second),
            third => Assert.Equal("config user.name CopilotHive", third));
    }

    /// <summary>
    /// A URL with no credential component is logged verbatim — redaction never rewrites an
    /// innocent URL.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_CredentialFreeUrl_IsLoggedUnchanged()
    {
        const string PlainUrl = "https://github.com/acme/widgets.git";
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", PlainUrl, "main", TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Creating Brain clone"));
        Assert.Contains(PlainUrl, entry.Message);
    }

    // ── Remote-tag boundaries (RunGitCaptureAsync consumers) ──────────────────
    //
    // The Brain clone persists the injected credential-bearing URL as `origin`, so a failing
    // REMOTE tag command (`ls-remote ... origin`, `push origin :refs/tags/...`) echoes that URL
    // through stderr. CreateTagAsync and DeleteTagAsync build exception messages and warning
    // logs from that raw stderr, so each construction point must redact.

    /// <summary>The stderr shape git emits when a remote operation fails against `origin`.</summary>
    private static string RemoteFailureStderr =>
        $"fatal: unable to access '{CredentialUrl}/': The requested URL returned error: 403";

    /// <summary>Creates a manager whose clone directory exists, so the tag paths are reachable.</summary>
    private BrainRepoManager CreateManagerWithClone(
        TestLogger<BrainRepoManager> logger, Func<BrainGitRequest, BrainGitResult> runner)
    {
        var manager = new BrainRepoManager(_tempDir, logger, runner);
        Directory.CreateDirectory(Path.Combine(manager.GetClonePath("widgets"), ".git"));
        return manager;
    }

    [Fact]
    public async Task CreateTagAsync_WhenRemoteTagQueryFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(128, string.Empty, RemoteFailureStderr)
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateTagAsync(
                "widgets", "v1.0.0", "main", "Release", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.StartsWith("Failed to query remote tags for 'widgets':", ex.Message);
        // The credential-FREE remote is still reported, so the message stays diagnostic.
        Assert.Contains($"unable to access '{RedactedUrl}/'", ex.Message);
        Assert.Contains("The requested URL returned error: 403", ex.Message);
    }

    [Fact]
    public async Task DeleteTagAsync_WhenRemoteTagQueryFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(128, string.Empty, RemoteFailureStderr)
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.StartsWith("Failed to query remote tags for 'widgets':", ex.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", ex.Message);
    }

    [Fact]
    public async Task DeleteTagAsync_WhenLocalTagQueryFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") => new BrainGitResult(0, string.Empty, string.Empty),
            var a when a.Contains("tag") && a.Contains("-l") =>
                new BrainGitResult(128, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.StartsWith("Failed to query local tags for 'widgets':", ex.Message);
        Assert.Contains(RedactedUrl, ex.Message);
    }

    /// <summary>
    /// Partial delete: the LOCAL delete succeeds and the REMOTE push fails, so the operation
    /// still returns true and the remote failure is only WARNED about — that warning is built
    /// from the credential-bearing push stderr.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_PartialDelete_RemoteWarningIsRedacted()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            // The tag exists on BOTH sides.
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            var a when a.Contains("-l") => new BrainGitResult(0, "v1.0.0\n", string.Empty),
            // Local delete succeeds…
            var a when a.Contains("-d") => new BrainGitResult(0, string.Empty, string.Empty),
            // …remote push fails, echoing the credential-bearing origin.
            var a when a.Contains("push") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(logger, runner.Run);

        var deleted = await manager.DeleteTagAsync(
            "widgets", "v1.0.0", TestContext.Current.CancellationToken);

        // Behavior is unchanged: a partial delete still reports success.
        Assert.True(deleted);

        var warning = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Remote tag delete failed"));
        Assert.DoesNotContain(Token, warning.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", warning.Message);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(Token));
    }

    /// <summary>
    /// The mirror case: the REMOTE delete succeeds and the LOCAL delete fails, exercising the
    /// local-warning construction point.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_PartialDelete_LocalWarningIsRedacted()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            var a when a.Contains("-l") => new BrainGitResult(0, "v1.0.0\n", string.Empty),
            // Local delete fails with a message that happens to carry the remote…
            var a when a.Contains("-d") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            // …remote push succeeds.
            var a when a.Contains("push") => new BrainGitResult(0, string.Empty, string.Empty),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(logger, runner.Run);

        var deleted = await manager.DeleteTagAsync(
            "widgets", "v1.0.0", TestContext.Current.CancellationToken);

        Assert.True(deleted);

        var warning = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Local tag delete failed"));
        Assert.DoesNotContain(Token, warning.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", warning.Message);
    }

    /// <summary>
    /// BOTH deletions fail, so the final aggregate exception embeds both stderr copies.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_WhenBothDeletionsFail_ExceptionMessageIsRedacted()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            var a when a.Contains("-l") => new BrainGitResult(0, "v1.0.0\n", string.Empty),
            var a when a.Contains("-d") =>
                new BrainGitResult(1, string.Empty, $"error: local delete against {CredentialUrl}"),
            var a when a.Contains("push") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(logger, runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.StartsWith("Failed to delete tag 'v1.0.0' for 'widgets'.", ex.Message);
        // BOTH stderr copies are present, both credential-free.
        Assert.Contains($"Local error: error: local delete against {RedactedUrl}", ex.Message);
        Assert.Contains($"Remote error: fatal: unable to access '{RedactedUrl}/'", ex.Message);
    }

    /// <summary>
    /// The sole-side failure form of the final exception: the tag exists only on the remote and
    /// that single deletion fails, so <c>Local error</c> renders as <c>(n/a)</c>.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_WhenSoleRemoteDeletionFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            // Not present locally.
            var a when a.Contains("-l") => new BrainGitResult(0, string.Empty, string.Empty),
            var a when a.Contains("push") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("Local error: (n/a)", ex.Message);
        Assert.Contains($"Remote error: fatal: unable to access '{RedactedUrl}/'", ex.Message);
    }

    /// <summary>
    /// Raw capture data is NOT mutated: the tag-existence decisions still read git's verbatim
    /// stdout, so a successful create/delete behaves exactly as before.
    /// </summary>
    [Fact]
    public async Task CreateTagAsync_WhenTagAlreadyExistsOnOrigin_StillSkipsUsingRawStdout()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty)
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(logger, runner.Run);

        var created = await manager.CreateTagAsync(
            "widgets", "v1.0.0", "main", "Release", TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Contains(logger.LogEntries, e => e.Message.Contains("already exists on origin"));
        // No tag/push commands were issued after the skip.
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("push"));
    }

    /// <summary>
    /// A tag that exists nowhere is reported as "not found" from RAW stdout, unchanged.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_WhenTagExistsNowhere_ReturnsFalseFromRawStdout()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var deleted = await manager.DeleteTagAsync(
            "widgets", "v1.0.0", TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("push"));
    }

    /// <summary>
    /// A credential-free stderr is reported verbatim through the tag boundaries — redaction
    /// never rewrites an innocent message.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_CredentialFreeStderr_IsReportedUnchanged()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(128, string.Empty, "fatal: not a git repository")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.Equal(
            "Failed to query remote tags for 'widgets': fatal: not a git repository", ex.Message);
    }
}
