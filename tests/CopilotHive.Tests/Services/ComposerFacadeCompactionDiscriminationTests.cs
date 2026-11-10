using System.Reflection;

using CopilotHive.Actors;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE FORBIDDEN-vs-COMPACTION DISCRIMINATION: the authorization answer and the operational
/// failure answer must stay DISTINCT, and a cancellation must never be turned into either.
/// <para>
/// Three outcomes share one operation (<see cref="IComposerFacade.CompactAsync"/>):
/// </para>
/// <list type="bullet">
/// <item>an UNAUTHORIZED caller gets <see cref="FacadeErrorKind.Forbidden"/> — decided BEFORE any
/// Composer work and OUTSIDE the compaction catch-all;</item>
/// <item>an OPERATIONAL failure (a real Composer that throws) gets
/// <see cref="FacadeErrorKind.BadRequest"/> carrying the exception message;</item>
/// <item>a CANCELLATION propagates as an <see cref="OperationCanceledException"/> — never mapped to
/// a result at all.</item>
/// </list>
/// <para>
/// The three rows are pinned against a REAL Composer fixture (a real agent, a real actor), not a
/// stub, so the operational message and the cancellation are the ones the production code
/// actually produces.
/// </para>
/// </summary>
public sealed class ComposerFacadeCompactionDiscriminationTests : IDisposable
{
    private const string MemberId = "disc-member";
    private const string ViewerId = "disc-viewer";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];

    public ComposerFacadeCompactionDiscriminationTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ComposerFacade Facade(Composer? composer) =>
        new(
            composer,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
            _auth.Policy,
            NullLogger<ComposerFacade>.Instance);

    /// <summary>A real, CONNECTED Composer whose chat client answers a compaction summary.</summary>
    private async Task<Composer> CreateConnectedComposerAsync()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation")));

        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => mockClient.Object);

        await composer.ConnectAsync(Ct);
        return composer;
    }

    /// <summary>
    /// THE OPERATIONAL ROW: a REAL Composer that throws while compacting still answers
    /// <see cref="FacadeErrorKind.BadRequest"/> carrying the EXACT exception message — the
    /// pre-facade endpoint's 400 contract, unchanged.
    /// <para>
    /// The failure is produced on a REAL Composer fixture: the runtime is put into the
    /// "streaming" state, so its own compaction entry point refuses with
    /// <c>"Cannot compact while streaming."</c> The message is the runtime's own, so a facade that
    /// fabricated a generic error would fail this assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OperationalCompactionFailure_OnARealComposer_StaysBadRequestWithItsOwnMessage()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            // The runtime's own refusal: it is streaming, so it refuses to compact.
            typeof(Composer)
                .GetField("_isStreaming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(composer, true);

            var result = await Facade(composer)
                .CompactAsync(FacadeAuthorizationHarness.Principal(MemberId), Ct);

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
            Assert.Equal("Cannot compact while streaming.", result.Error);
        }
    }

    /// <summary>
    /// THE FORBIDDEN ROW: the SAME operation, the SAME real Composer fixture and the SAME
    /// operational failure — but for an unauthorized caller — answers
    /// <see cref="FacadeErrorKind.Forbidden"/> with the sanitized denial text and NEVER the
    /// operational message.
    /// <para>
    /// This is the discriminating pair: if the Forbidden return were moved INSIDE the compaction
    /// catch-all, the denial would be reported as a BadRequest (or the operational message would
    /// leak into it) and this test fails.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnauthorizedCaller_OnTheSameFailingComposer_GetsForbidden_NotBadRequest()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            typeof(Composer)
                .GetField("_isStreaming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(composer, true);

            var denied = await Facade(composer)
                .CompactAsync(FacadeAuthorizationHarness.Principal(ViewerId), Ct);

            Assert.False(denied.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, denied.Kind);
            Assert.NotEqual(FacadeErrorKind.BadRequest, denied.Kind);
            Assert.Equal("The acting account is not authorized to perform this operation.", denied.Error);
            Assert.DoesNotContain("streaming", denied.Error!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// THE CANCELLATION ROW: an <see cref="OperationCanceledException"/> raised by the COMPOSER
    /// itself propagates out of the facade as an exception — it is never converted into a
    /// <see cref="FacadeResult{T}"/> (which would let a caller turn a cancelled compaction into a
    /// 400 or a 403).
    /// <para>
    /// The cancellation is produced by the REAL runtime: the compaction message is queued in the
    /// actor's mailbox and the actor is then disposed, which cancels the pending reply. The
    /// facade's own catch-all would swallow that into a BadRequest — only the explicit
    /// cancellation re-throw lets it through.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancellationRaisedByTheComposer_Propagates_AndIsNeverMapped()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            var actor = GetActor(composer);

            // Park the actor's mailbox loop so the compaction message stays QUEUED (never handled).
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            SetBeforeReadHook(actor, async () =>
            {
                if (Interlocked.Increment(ref reads) >= 1)
                {
                    parked.TrySetResult();
                    await gate.Task;
                }
            });

            // Wake the loop so it reaches the hook and parks there.
            actor.Tell(new ComposerCancelStreamingMessage());
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            var compactTask = Facade(composer)
                .CompactAsync(FacadeAuthorizationHarness.Principal(MemberId), Ct);

            // The compaction message really reached the mailbox (the Tell was accepted).
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!MailboxHasItem(actor) && DateTime.UtcNow < deadline)
                await Task.Delay(10, Ct);
            Assert.True(MailboxHasItem(actor), "the compaction message must have been queued");

            // Disposing the actor cancels the pending reply — the real runtime's own cancellation.
            var disposeTask = actor.DisposeAsync().AsTask();
            gate.TrySetResult();
            await disposeTask;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compactTask);
        }
    }

    /// <summary>
    /// THE SAME ROW FOR THE PARTIAL COMPACTION: a cancellation raised by the Composer propagates
    /// out of <see cref="IComposerFacade.CompactPartialAsync"/> too, so the catch-all cannot leak
    /// a cancellation into a BadRequest on that path either.
    /// </summary>
    [Fact]
    public async Task CancellationRaisedByTheComposer_Propagates_ForPartialCompaction()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            var actor = GetActor(composer);

            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            SetBeforeReadHook(actor, async () =>
            {
                if (Interlocked.Increment(ref reads) >= 1)
                {
                    parked.TrySetResult();
                    await gate.Task;
                }
            });

            actor.Tell(new ComposerCancelStreamingMessage());
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            var partialTask = Facade(composer)
                .CompactPartialAsync(FacadeAuthorizationHarness.Principal(MemberId), 50, Ct);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!MailboxHasItem(actor) && DateTime.UtcNow < deadline)
                await Task.Delay(10, Ct);
            Assert.True(MailboxHasItem(actor), "the compaction message must have been queued");

            var disposeTask = actor.DisposeAsync().AsTask();
            gate.TrySetResult();
            await disposeTask;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => partialTask);
        }
    }

    /// <summary>
    /// THE RESOLVER ROW: a cancellation raised by the RESOLVER call (the directory read) also
    /// propagates — the authorization seam never converts the caller's cancellation into a
    /// refusal, so a cancelled request can never be reported as a 403.
    /// </summary>
    [Fact]
    public async Task CancellationRaisedByTheResolver_Propagates_AndIsNeverAForbidden()
    {
        await _auth.SeedAsync(MemberId, UserRole.Member);
        var composer = await CreateConnectedComposerAsync();
        await using (composer)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Facade(composer).CompactAsync(
                    FacadeAuthorizationHarness.Principal(MemberId), cts.Token));

            // A cancellation is NOT the authorization answer.
            Assert.IsNotType<InvalidOperationException>(ex);
        }
    }

    /// <summary>The Composer's actor, reached by reflection.</summary>
    private static ComposerActor GetActor(Composer composer) =>
        (ComposerActor)typeof(Composer)
            .GetField("_actor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;

    /// <summary>Installs the actor's mailbox-loop parking hook.</summary>
    private static void SetBeforeReadHook(ComposerActor actor, Func<Task> hook) =>
        typeof(Actor<IComposerMessage>)
            .GetProperty("OnBeforeReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(actor, hook);

    /// <summary>
    /// Whether the actor's mailbox really holds a message (the Tell was accepted and the loop has
    /// not dequeued it). Uses the channel reader's peek, never a timing assumption.
    /// </summary>
    private static bool MailboxHasItem(ComposerActor actor)
    {
        var mailbox = typeof(Actor<IComposerMessage>)
            .GetField("_mailbox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(actor)!;
        var reader = mailbox.GetType().GetProperty("Reader")!.GetValue(mailbox)!;
        var tryPeek = reader.GetType().GetMethod("TryPeek")!;
        return (bool)tryPeek.Invoke(reader, [null])!;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _auth.Dispose();
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the isolated state directory.
            }
        }
    }
}
