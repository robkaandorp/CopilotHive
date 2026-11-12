using System.Security.Claims;

using CopilotHive.Components.Pages;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE FAIL-CLOSED REGRESSIONS of the circuit: one named row per reviewer probe, each with a positive control.
/// <para>
/// Every row drives the REAL <see cref="ComposerChat"/> through a RETAINED root component over the real accessor,
/// manager, resolver and a SQLite directory. Assertions on markup read the retained root's OWN output — never a
/// freshly created page — so they prove that the ORIGINAL page changed. Races are made deterministic with explicit
/// task gates (a gated accessor answer, a blocked upload stream); nothing sleeps or polls.
/// </para>
/// </summary>
public sealed class ComposerCircuitFailClosedRegressionTests : IAsyncLifetime
{
    private const string Model = "failclosed-model";
    private const string CopilotModel = "copilot/failclosed-primary";
    private const string MemberGh = "failclosed-member";
    private const string OriginalGh = "failclosed-original";
    private const string Marker = "failclosed-thread-marker";

    /// <summary>
    /// THE USER DIRECTORY of this class: a REAL directory (production <see cref="UserService"/> over the
    /// production EF model) whose EVERY context opens its OWN connection — see
    /// <see cref="ComposerTestUserDirectory"/> for why a single shared connection is not usable here.
    /// </summary>
    private readonly ComposerTestUserDirectory _users = new();

    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-failclosed-{Guid.NewGuid():N}");

    private int _originalId;
    private int _memberId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _originalId = (await _users.SeedActivatedAccountAsync(OriginalGh, UserRole.Admin)).Id;
        _memberId = (await _users.SeedActivatedAccountAsync(MemberGh, UserRole.Member)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        _users.Dispose();
        try { if (Directory.Exists(_dir)) TestHelpers.ForceDeleteDirectory(_dir); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    // ── (a) FINDING 1: a held re-query must never overwrite a NEWER binding ──────────────────────

    /// <summary>
    /// THE OLDER CALLBACK LEAVES A NEWER FORBIDDEN BINDING UNTOUCHED: the callback's re-query answer (Ready) is
    /// HELD; a newer demotion lands and a newer operation applies its own Forbidden selection; only then is the
    /// older answer released. The callback must be dropped WITHOUT adopting that answer — the binding record stays
    /// the very same Forbidden record and no subscription is reinstalled.
    /// </summary>
    [Fact]
    public async Task AHeldRequery_ReleasedAfterANewerRefusal_LeavesTheNewerBindingAndSubscriptionsUntouched()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        Assert.NotNull(rendered.Subscribed);

        var hold = host.Gate.Arm();
        rendered.Raise("HandleStreamingUpdate");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // the re-query ANSWERED (Ready) and is held

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("ConnectRuntimeAsync"); // the NEWER operation applies Forbidden
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            var newer = rendered.Page.CurrentCallbackBinding;
            Assert.Null(rendered.Subscribed);

            hold.Release();
            await rendered.QueuedCallbacksAsync();

            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.Null(rendered.Page.CurrentCallbackBinding!.Composer);
            Assert.Equal(1, rendered.Page.DroppedCallbackCount);
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>POSITIVE CONTROL: the same held re-query with NO newer change is APPLIED and keeps the subscription.</summary>
    [Fact]
    public async Task AHeldRequery_WithNoNewerChange_IsApplied()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        var composer = rendered.Page.CurrentCallbackBinding!.Composer;

        var hold = host.Gate.Arm();
        rendered.Raise("HandleStreamingUpdate");
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        hold.Release();
        await rendered.QueuedCallbacksAsync();

        Assert.Equal(1, rendered.Page.AppliedCallbackCount);
        Assert.Equal(0, rendered.Page.DroppedCallbackCount);
        Assert.Same(composer, rendered.Subscribed);
        Assert.Contains(Marker, rendered.Html, StringComparison.Ordinal);
    }

    // ── (b) FINDING 2: the retained page re-renders fail-closed ──────────────────────────────────

    /// <summary>
    /// A DEMOTED SUBSCRIPTION CALLBACK RE-RENDERS THE RETAINED ROOT: the callback is the FIRST thing to discover the
    /// demotion; the original page's own rendered output must lose the thread and show the forbidden notice (a new
    /// page refusing would prove nothing).
    /// </summary>
    [Fact]
    public async Task ADemotedSubscriptionCallback_ReRendersTheRetainedRoot_AsForbidden()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        Assert.Contains(Marker, rendered.Html, StringComparison.Ordinal);

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            rendered.Raise("HandleStreamingUpdate");
            await rendered.QueuedCallbacksAsync();

            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            var html = rendered.Html; // the RETAINED root's own output
            Assert.DoesNotContain(Marker, html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>POSITIVE CONTROL: an authorized callback keeps the thread and shows no forbidden notice.</summary>
    [Fact]
    public async Task AnAuthorizedSubscriptionCallback_KeepsTheRetainedRootReady()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);

        rendered.Raise("HandleStreamingUpdate");
        await rendered.QueuedCallbacksAsync();

        Assert.Equal(1, rendered.Page.AppliedCallbackCount);
        Assert.Contains(Marker, rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
    }

    // ── (c) FINDING 3: storage mutations are authorized BEFORE they start ────────────────────────

    /// <summary>
    /// A STAGED FILE IS NOT REMOVED AFTER A DEMOTION: removal deletes a file, so it needs a fresh selection first.
    /// The file stays exactly where it is and the page leaves Ready.
    /// </summary>
    [Fact]
    public async Task RemovingAStagedFile_AfterADemotion_IsRefused_AndThePageLeavesReady()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
        var file = rendered.StagedFilePath();
        Assert.True(File.Exists(file));

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("RemoveAttachment");

            Assert.True(File.Exists(file), "a refused caller's staged file must not be deleted");
            Assert.NotEqual(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>POSITIVE CONTROL: an authorized removal deletes the file and the page stays Ready.</summary>
    [Fact]
    public async Task RemovingAStagedFile_WhenAuthorized_DeletesIt_AndThePageStaysReady()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
        var file = rendered.StagedFilePath();

        await rendered.InvokeAsync("RemoveAttachment");

        Assert.False(File.Exists(file));
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
    }

    /// <summary>
    /// A WRITE BEGUN AFTER ACCESS WAS REVOKED NEVER STARTS: the paste is attempted after the demotion; nothing may
    /// reach the store (not even an orphan the completion guard would later ignore).
    /// </summary>
    [Fact]
    public async Task APasteBegunAfterADemotion_NeverWritesToTheStore()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var root = rendered.Page.CurrentCallbackBinding!.Attachments!.AttachmentsRootPath;
        Assert.Empty(Directory.GetFiles(root));

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));

            Assert.Empty(Directory.GetFiles(root));
            Assert.Null(rendered.Pending);
            Assert.NotEqual(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>POSITIVE CONTROL: an authorized paste writes the file and stages it.</summary>
    [Fact]
    public async Task AnAuthorizedPaste_WritesTheFile_AndStagesIt()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);

        await rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));

        Assert.NotNull(rendered.Pending);
        Assert.True(File.Exists(rendered.StagedFilePath()));
    }

    // ── (f) FINDING 6: genuinely IN-FLIGHT writes ────────────────────────────────────────────────

    /// <summary>
    /// A WRITE HELD IN FLIGHT ACROSS A DEMOTION: the write STARTS under authorization (its stream is blocked
    /// mid-copy), the demotion and a page operation land while it is pending, and only then does it finish. The
    /// outcome is NOT attached to the page; the accepted boundary holds — the file stays in the store that
    /// accepted it.
    /// </summary>
    [Fact]
    public async Task AWriteInFlightAcrossADemotion_IsNotAttached_AndItsFileStaysInItsStore()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var root = rendered.Page.CurrentCallbackBinding!.Attachments!.AttachmentsRootPath;

        using var stream = new BlockingReadStream([9, 9, 9]);
        var paste = rendered.PasteAsync(stream);
        await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // the write STARTED, authorized

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("ConnectRuntimeAsync"); // the demotion is applied by a page operation
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);

            stream.Release();
            await paste;

            Assert.Null(rendered.Pending);
            Assert.Single(Directory.GetFiles(root)); // retained in the store that accepted it
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>POSITIVE CONTROL: the same in-flight write with NO change is attached.</summary>
    [Fact]
    public async Task AWriteInFlight_WithNoChange_IsAttached()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);

        using var stream = new BlockingReadStream([9, 9, 9]);
        var paste = rendered.PasteAsync(stream);
        await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        stream.Release();
        await paste;

        Assert.NotNull(rendered.Pending);
    }

    // ── (d) FINDING 4: facade operations adopt the fresh selection ───────────────────────────────

    /// <summary>A DEMOTED COMPACTION hides the content and unsubscribes (the facade refused; the page adopts that).</summary>
    [Fact]
    public async Task ADemotedCompaction_HidesTheContent_AndUnsubscribes()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        Assert.NotNull(rendered.Subscribed);

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("CompactSession");

            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>A DEMOTED REASONING SWITCH hides the content and unsubscribes.</summary>
    [Fact]
    public async Task ADemotedReasoningSwitch_HidesTheContent_AndUnsubscribes()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("HandleReasoningChange", new ChangeEventArgs { Value = "high" });

            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>POSITIVE CONTROL: an authorized compaction leaves the page Ready, bound and subscribed.</summary>
    [Fact]
    public async Task AnAuthorizedCompaction_LeavesThePageReadyAndSubscribed()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        var composer = rendered.Page.CurrentCallbackBinding!.Composer;

        await rendered.InvokeAsync("CompactSession");

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        Assert.Same(composer, rendered.Subscribed);
    }

    /// <summary>
    /// A RUNTIME REPLACEMENT UNIFIES THE PAGE WITH THE RUNTIME THE FACADE ACTED ON: the runtime is removed and
    /// recreated; after the facade operation the binding AND the subscription are the NEW runtime's — never the
    /// removed one.
    /// <para>
    /// THE COMPETING PATHS ARE CONTROLLED, NOT HOPED AWAY: disposing runtime A raises its Composer events, and every
    /// such event QUEUES a subscription callback whose own selection runs the freshness guard <b>on the dispatcher,
    /// concurrently with this scenario</b> — it can replace the binding record under the operation that is in flight
    /// and can therefore supersede the adoption this row asserts. (That supersession is exactly what made the old
    /// post-hoc re-query read a null runtime on CI.) The callbacks are therefore PARKED — drained first, then parked —
    /// BEFORE the removal, so none of them can bind, drop or re-render while the scenario runs, and the assertions are
    /// made on the state the ACTUAL facade operation (<c>CompactSession</c>, awaited to completion) leaves behind,
    /// never on a second re-query. <c>RemoveAsync</c> already awaits the entry's full teardown AND its cache eviction,
    /// so no extra teardown wait is added here, and <see cref="ComposerChat.ComposerCircuitStatus.Removed"/> is not
    /// accepted as a success.
    /// </para>
    /// <para>
    /// DISCRIMINATION, STATED HONESTLY: this row proves the UNIFICATION. On its own it does NOT kill an unconditional
    /// stale-adoption mutant, because nothing stale is released while it runs — parking only removes interference.
    /// The mutant evidence for the ownership guard is the adversarial row below,
    /// <c>AHeldOlderSelection_ReleasedAfterTheReplacement_DoesNotRebindTheRemovedRuntime</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFacadeOperation_OnAReplacedRuntime_UnifiesTheBindingAndSubscription()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var old = rendered.Page.CurrentCallbackBinding!.Composer;
        Assert.NotNull(old);

        // PARK every subscription callback BEFORE the removal: the callbacks the initial render already queued are
        // DRAINED, every later body (the removal's own teardown notifications) stands parked until disposal, and each
        // parked invocation is tracked so cleanup can join all of them.
        await rendered.ParkCallbacksAsync();

        Assert.True(await host.Manager.RemoveAsync(_memberId));

        // THE ACTUAL FACADE OPERATION, AWAITED TO COMPLETION: the task is the milestone (the operation returns only
        // after its own adoption/bind), so no post-hoc re-query is needed — or allowed — to observe the outcome.
        await rendered.InvokeAsync("CompactSession");

        var newer = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(newer);
        var current = newer!.Composer;
        Assert.NotNull(current); // a null Composer here is precisely the CI flake this row removes
        Assert.NotSame(old, current);
        Assert.Same(current, rendered.Subscribed);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, newer.Status);

        // NO PARKED CALLBACK RAN MID-SCENARIO: none of them could supersede the binding the operation adopted.
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);
        Assert.Equal(0, rendered.Page.DroppedCallbackCount);
    }

    /// <summary>
    /// THE OWNERSHIP GUARD FOR AN OLDER <b>OPERATION</b> ANSWER: an ordinary page operation's circuit acquisition is
    /// HELD after the accessor computed its answer for runtime A; A is then removed (its real teardown awaited by
    /// <c>RemoveAsync</c>), a NEWER facade operation adopts and binds the fresh runtime B, and only then is the older
    /// answer released. That answer belongs to a binding this circuit no longer shows, so it must NOT be adopted: the
    /// newer binding record and the subscription to B survive it, the removed runtime A is never re-subscribed, and
    /// the held paste's write never starts (no file in A's store).
    /// <para>
    /// THE MECHANISM IT PINS: <c>AcquireCircuitRuntimeAsync</c> captures the binding it started under BEFORE its first
    /// await and, once the accessor answered, refuses to bind an answer whose captured binding is no longer the
    /// circuit's — the same pre-await capture plus post-await condition <c>RevalidateCompletionAsync</c> applies to
    /// callbacks. The callback path has its own row in this class
    /// (<c>HeldCallbackRequery_AfterNewerReplacement_PreservesItsBindingHistoryAndSubscriptions</c>); this row covers
    /// an ordinary OPERATION's answer, which is the path the removal's competing callbacks can supersede.
    /// </para>
    /// <para>
    /// THE DISCRIMINATOR, AND WHY THE ROW ABOVE IS NOT IT: with unconditional stale adoption restored in the
    /// operation path, the released answer re-binds the REMOVED runtime A — the binding record is replaced, the
    /// subscription returns to A — and the held paste's write starts in A's store, so this row fails on both. The
    /// replacement row above still passes under that mutant. Parking the incidental callbacks alone therefore proves
    /// nothing about this guard.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AHeldOlderSelection_ReleasedAfterTheReplacement_DoesNotRebindTheRemovedRuntime()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var old = rendered.Page.CurrentCallbackBinding!.Composer;
        var removedStore = rendered.Page.CurrentCallbackBinding!.Attachments!.AttachmentsRootPath;
        Assert.NotNull(old);

        // The incidental teardown callbacks are controlled FIRST, so the hold armed next is taken by the paste's own
        // acquisition and never by a subscription callback's re-query.
        await rendered.ParkCallbacksAsync();

        // PATH 1: HOLD the older operation's answer — the paste's START gate is an ordinary circuit acquisition
        // through the REAL accessor, so its answer (runtime A, Ready) is computed and then parked here.
        var hold = host.Gate.Arm();
        var held = rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(held.IsCompleted, "the older operation's answer must be genuinely held across the replacement");

            // A's real teardown and eviction are awaited by RemoveAsync itself.
            Assert.True(await host.Manager.RemoveAsync(_memberId));

            // PATH 2: the NEWER facade operation runs to COMPLETION and the page adopts/binds the fresh runtime B.
            await rendered.InvokeAsync("CompactSession");
            var newer = rendered.Page.CurrentCallbackBinding;
            Assert.NotNull(newer);
            var b = newer!.Composer;
            Assert.NotNull(b);
            Assert.NotSame(old, b);
            Assert.Same(b, rendered.Subscribed);

            // PATH 3: RELEASE the older answer, which is about the removed runtime A.
            hold.Release();
            await held.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // The newer binding record and the subscription to B SURVIVE the stale release...
            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.NotNull(rendered.Page.CurrentCallbackBinding!.Composer);
            Assert.NotSame(old, rendered.Page.CurrentCallbackBinding!.Composer);
            Assert.Same(b, rendered.Page.CurrentCallbackBinding!.Composer);
            Assert.Same(b, rendered.Subscribed);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);

            // ...and the held paste never started a write: nothing reached the removed runtime's store.
            Assert.Empty(Directory.GetFiles(removedStore));
            Assert.Null(rendered.Pending);
        }
        finally
        {
            // Failure-safe: a failing assertion must never leave the operation parked (disposal joins it either way).
            hold.Release();
        }
    }

    /// <summary>
    /// EVERY PARKED INVOCATION IS JOINED BY CLEANUP, NOT ONLY THE LAST ONE (the SUCCESSFUL path): several
    /// subscription callbacks are parked (each queued body is a SEPARATE invocation), the page's
    /// <c>LastCallbackCompletion</c> is OVERWRITTEN by each new callback and therefore aggregates nothing, and the
    /// cleanup must still await EVERY tracked invocation.
    /// <para>
    /// WHAT IT EXERCISES, EXACTLY: the row calls <c>rendered.DisposeAsync()</c> EXPLICITLY (the later
    /// <c>await using</c> disposal is then an idempotent no-op) so it can read the join observations afterwards. It
    /// proves the join-all behaviour of the cleanup body on a scenario that did NOT fail. It does NOT exercise the
    /// runner/<c>await using</c> unwind and says nothing about failure precedence — those are the two rows below.
    /// </para>
    /// <para>
    /// WHY THE COUNT MATTERS: the shared release latch completes every parked invocation whether or not it is
    /// joined, so a terminal state alone proves nothing about joining. The join loop therefore counts the
    /// invocations it actually awaited (<c>JoinedParkedCount</c>) against the number tracked; a cleanup that joined
    /// only the last one fails the count. It is non-vacuous: at least three invocations are parked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParkedSubscriptionCallbacks_AreAllJoinedByCleanup_NotOnlyTheLastOne()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);

        await rendered.ParkCallbacksAsync();

        // THREE SEPARATE queued bodies: each one is its own parked invocation, and each one overwrites the
        // page's LastCallbackCompletion — so that property alone can observe only the LAST of them.
        rendered.Raise("HandleStreamingUpdate");
        rendered.Raise("HandleStreamingUpdate");
        rendered.Raise("HandleStreamingUpdate");

        // The invocations are queued on the DISPATCHER; wait until all three are parked.
        await rendered.WaitForParkedCountAsync(3);
        Assert.True(rendered.ParkedCount >= 3,
            $"the scenario must park MORE THAN ONE invocation, or the join-all contract is not exercised (parked {rendered.ParkedCount})");

        // The page's own record points at the LAST invocation only — which is exactly why the helper tracks them all.
        Assert.NotNull(rendered.Page.LastCallbackCompletion);

        // EXPLICIT disposal (see the remarks): the join observations are read right after it.
        await rendered.DisposeAsync();

        Assert.Equal(rendered.ParkedCount, rendered.JoinedParkedCount);
        Assert.All(rendered.ParkedStatuses(), status =>
            Assert.Equal(TaskStatus.RanToCompletion, status));
    }

    /// <summary>
    /// THE REAL EXCEPTIONAL PATH: a scenario failure UNWINDS through the retained circuit's own <c>await using</c>
    /// (the scope's compiler-generated disposal — there is NO explicit <c>DisposeAsync()</c> call in the body), and
    /// that unwinding cleanup releases AND individually joins every parked invocation.
    /// <para>
    /// WHAT IT PROVES: (1) the thrown exception is exactly the scenario's own sentinel — cleanup neither replaced
    /// nor wrapped it (the cleanup had no fault of its own here); (2) after the unwind, the join loop counted every
    /// tracked invocation (<c>JoinedParkedCount == ParkedCount</c>, with more than two parked), and none is left
    /// waiting. The count is the observation at the join: the release latch completes every task whether or not it
    /// is joined, so the terminal states are only a secondary check.
    /// </para>
    /// <para>
    /// WHAT IT DOES NOT PROVE: it says nothing about a cleanup fault coexisting with the scenario failure — that
    /// is <c>AScenarioFailure_IsNotReplacedByACleanupFault_AndTheCleanupFaultIsStillReported</c>. (Claim changed
    /// from the previous revision, which awaited <c>DisposeAsync()</c> explicitly, never threw, and so did not
    /// exercise this path.)
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFailingRow_StillReleasesAndJoinsEveryParkedInvocation()
    {
        await using var host = NewHost();
        Rendered? captured = null;

        var thrown = await Assert.ThrowsAsync<ScenarioSentinelException>(async () =>
        {
            await using var rendered = await RenderAsync(host, MemberGh);
            captured = rendered;

            await rendered.ParkCallbacksAsync();
            rendered.Raise("HandleStreamingUpdate");
            rendered.Raise("HandleStreamingUpdate");
            rendered.Raise("HandleStreamingUpdate");
            await rendered.WaitForParkedCountAsync(3);
            Assert.True(rendered.ParkedCount >= 3);

            // THE SCENARIO FAILS HERE, after parking: the `await using` unwinds through its disposal.
            throw new ScenarioSentinelException("scenario failed after parking three invocations");
        });

        Assert.Equal("scenario failed after parking three invocations", thrown.Message);

        // Observed AFTER the unwind: the cleanup that ran was the scope's own disposal.
        Assert.NotNull(captured);
        Assert.True(captured!.ParkedCount >= 3);
        Assert.Equal(captured.ParkedCount, captured.JoinedParkedCount);
        Assert.All(captured.ParkedStatuses(), status => Assert.Equal(TaskStatus.RanToCompletion, status));
    }

    /// <summary>
    /// A SCENARIO FAILURE IS NOT REPLACED BY A CLEANUP FAULT, AND THE CLEANUP FAULT IS STILL REPORTED: the scenario
    /// (run through <c>RunScenarioAsync</c>) fails with one sentinel; the unwinding <c>await using</c> disposal then
    /// meets a DIFFERENT fault from a parked invocation. Cleanup must still release and join ALL invocations (the
    /// faulting one sits in the middle), and the exception that escapes must keep the scenario failure identifiable
    /// — the very same instance, first — with the cleanup fault alongside it.
    /// <para>
    /// SCOPE OF THE CLAIM: this precedence applies to a scenario run through <c>Rendered.RunScenarioAsync</c> (a
    /// <c>DisposeAsync</c> cannot see the exception of an <c>await using</c> body by itself). Existing rows that do
    /// not use that wrapper are unchanged: a cleanup fault there is still reported as the cleanup aggregate.
    /// </para>
    /// <para>
    /// LOAD-BEARING: with the previous behaviour restored (the cleanup aggregate thrown without the primary
    /// failure) this row fails at <c>Assert.Same(scenario, escaped.InnerExceptions[0])</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AScenarioFailure_IsNotReplacedByACleanupFault_AndTheCleanupFaultIsStillReported()
    {
        await using var host = NewHost();
        Rendered? captured = null;
        var scenario = new ScenarioSentinelException("the scenario failure");
        var cleanup = new CleanupSentinelException("the cleanup fault");

        var escaped = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await using var rendered = await RenderAsync(host, MemberGh);
            captured = rendered;

            await rendered.RunScenarioAsync(async () =>
            {
                await rendered.ParkCallbacksAsync();
                rendered.Raise("HandleStreamingUpdate");
                rendered.ParkFaultingInvocation(cleanup); // faults during cleanup, in the MIDDLE of the tracked set
                rendered.Raise("HandleStreamingUpdate");
                await rendered.WaitForParkedCountAsync(3);
                throw scenario;
            });
        });

        // The primary is identifiable: the very same instance, first; the cleanup fault is visible, not swallowed.
        Assert.Same(scenario, escaped.InnerExceptions[0]);
        Assert.Contains(cleanup, escaped.InnerExceptions);
        Assert.Equal(2, escaped.InnerExceptions.Count);

        // The fault did not stop the cleanup: every invocation was still joined, the faulting one observed as such.
        Assert.NotNull(captured);
        Assert.Equal(3, captured!.ParkedCount);
        Assert.Equal(captured.ParkedCount, captured.JoinedParkedCount);
        var statuses = captured.ParkedStatuses();
        Assert.Equal(1, statuses.Count(s => s == TaskStatus.Faulted));
        Assert.Equal(2, statuses.Count(s => s == TaskStatus.RanToCompletion));
    }

    private sealed class ScenarioSentinelException(string message) : Exception(message);

    private sealed class CleanupSentinelException(string message) : Exception(message);

    /// <summary>
    /// ADOPTION ADDS NO SECOND CONNECT: a deferred runtime's facade operation drives EXACTLY ONE request-path
    /// connect (the typed recovery contract: one connect, then the actionable 503) and the page's own adoption
    /// performs NO circuit connect.
    /// </summary>
    [Fact]
    public async Task ADeferredFacadeOperation_DrivesExactlyOneConnect_AndAdoptionAddsNone()
    {
        await using var host = NewHost(primaryModel: CopilotModel, tokenAvailable: () => false);
        await using var rendered = await RenderAsync(host, OriginalGh);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Deferred, rendered.Status);
        var circuitConnectsBefore = host.Gate.CircuitConnects;
        var requestConnectsBefore = host.Gate.RequestConnects;

        await rendered.InvokeAsync("CompactSession");

        Assert.Equal(requestConnectsBefore + 1, host.Gate.RequestConnects);
        Assert.Equal(circuitConnectsBefore, host.Gate.CircuitConnects);
        Assert.Equal(ComposerRuntimeFacade.RuntimeDeferredError, rendered.SendError);
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Deferred, rendered.Status);
    }

    // ── (e) FINDING 5: the paste listener follows the textarea ───────────────────────────────────

    /// <summary>CONTROL: a Ready page installs the paste listener exactly once.</summary>
    [Fact]
    public async Task AReadyPage_InstallsThePasteListener_Once()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.AfterRenderAsync();
        await rendered.AfterRenderAsync(); // a second render must not stack a second listener

        Assert.Equal(1, host.Js.Count("install"));
        Assert.Equal(0, host.Js.Count("uninstall"));
    }

    /// <summary>
    /// A REFUSAL RELEASES THE PASTE LISTENER AT ONCE (not at disposal): the connected textarea is gone, so the JS
    /// module must not keep it — and a page with nothing bound can never mirror allowed=true.
    /// </summary>
    [Fact]
    public async Task ARefusal_ReleasesThePasteListener_AndAnUnboundPageNeverMirrorsAllowed()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.AfterRenderAsync();
        Assert.Equal(1, host.Js.Count("install"));

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("ConnectRuntimeAsync");
            await rendered.AfterRenderAsync();

            Assert.Equal(1, host.Js.Count("uninstall"));
            Assert.True(host.Js.Modules[0].Disposed);

            // An unbound idle page must not tell JS that paste is allowed.
            var before = host.Js.AllowedValues.Count;
            await rendered.InvokeAsync("SyncPasteAllowedAsync");
            Assert.DoesNotContain(true, host.Js.AllowedValues.Skip(before));
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>
    /// THE TEXTAREA RETURNS, THE LISTENER IS FRESH: Ready -> refused -> restored creates a NEW textarea, which must
    /// get a NEW install (the first listener is gone with the old textarea).
    /// </summary>
    [Fact]
    public async Task WhenTheTextareaReturns_ANewPasteListenerIsInstalled()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.AfterRenderAsync();

        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        await rendered.InvokeAsync("ConnectRuntimeAsync");
        await rendered.AfterRenderAsync();
        await _users.SetRoleAsync(MemberGh, UserRole.Member);
        await rendered.InvokeAsync("ConnectRuntimeAsync");
        await rendered.AfterRenderAsync();

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        Assert.Equal(2, host.Js.Count("install"));
        Assert.Equal(1, host.Js.Count("uninstall"));
    }

    /// <summary>
    /// DEFERRED -> CONNECT -> READY ATTACHES PASTE TO THE NEW TEXTAREA: a deferred page has no textarea (no
    /// install); the connect creates one, and exactly then the listener is installed.
    /// </summary>
    [Fact]
    public async Task DeferredConnectReady_InstallsThePasteListenerOnTheNewTextarea()
    {
        var token = false;
        await using var host = NewHost(primaryModel: CopilotModel, tokenAvailable: () => token);
        await using var rendered = await RenderAsync(host, OriginalGh);
        await rendered.AfterRenderAsync();
        Assert.Equal(0, host.Js.Count("install"));

        token = true;
        await rendered.InvokeAsync("ConnectRuntimeAsync");
        await rendered.AfterRenderAsync();

        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        Assert.Equal(1, host.Js.Count("install"));
    }

    // QA additions: reviewer probes not exercised by the original 19 rows.

    [Fact]
    public async Task HeldCallbackRequery_AfterNewerReplacement_PreservesItsBindingHistoryAndSubscriptions()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        var old = rendered.Page.CurrentCallbackBinding!.Composer;
        var hold = host.Gate.Arm();
        rendered.Raise("HandleStreamingUpdate");
        var callback = rendered.Page.LastCallbackCompletion;
        var disposalCallbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(callback.IsCompleted);
            // Ignore incidental teardown notifications until the overlapping-requery probe has finished.
            // The original callback is already OUTSTANDING inside its accessor, outside this new gate.
            rendered.Page.CallbackGate = async body => { await disposalCallbackRelease.Task; await body(); };
            Assert.True(await host.Manager.RemoveAsync(_memberId));
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            var newer = rendered.Page.CurrentCallbackBinding;
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, newer!.Status);
            Assert.NotSame(old, newer.Composer);
            Assert.Same(newer.Composer, rendered.Subscribed);
            var html = rendered.Html;
            Assert.Contains(Marker, html, StringComparison.Ordinal);
            Assert.False(callback.IsCompleted);
            var droppedBeforeRelease = rendered.Page.DroppedCallbackCount;
            hold.Release();
            await callback.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Same(newer, rendered.Page.CurrentCallbackBinding);
            Assert.Same(newer.Composer, rendered.Subscribed);
            Assert.Equal(html, rendered.Html);
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
            Assert.Equal(droppedBeforeRelease + 1, rendered.Page.DroppedCallbackCount);
        }
        finally
        {
            hold.Release();
            disposalCallbackRelease.TrySetResult();
            await callback.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await rendered.QueuedCallbacksAsync();
            rendered.Page.CallbackGate = null;
        }
    }

    [Fact]
    public async Task AppliedSubscriptionCallback_UpdatesTheOriginalRootWithNewHistory()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        const string update = "retained-root-new-content";
        Assert.DoesNotContain(update, rendered.Html, StringComparison.Ordinal);
        var composer = rendered.Page.CurrentCallbackBinding!.Composer!;
        // Add to the live production session, not to component fields or a new root.
        var agent = (ComposerAgentService)typeof(Composer).GetField("_agentService", PrivateInstance)!.GetValue(composer)!;
        agent.Session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, update));
        rendered.Raise("HandleStreamingUpdate");
        await rendered.QueuedCallbacksAsync();
        Assert.Contains(update, rendered.Html, StringComparison.Ordinal);
        Assert.Contains(Marker, rendered.Html, StringComparison.Ordinal);
        Assert.Equal(1, rendered.Page.AppliedCallbackCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickerAttemptAfterRevocation_DoesNotReadOrWriteStorage(bool disabled)
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        var root = rendered.Page.CurrentCallbackBinding!.Attachments!.AttachmentsRootPath;
        var file = new ObservedBrowserFile(new MemoryStream([10, 20, 30, 40]));
        if (disabled) await _users.SetEnabledAsync(MemberGh, false);
        else await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.PickAsync(file);
            Assert.Equal(0, file.ReadCalls);
            Assert.Empty(Directory.GetFiles(root));
            Assert.Null(rendered.Pending);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
        }
        finally
        {
            if (disabled) await _users.SetEnabledAsync(MemberGh, true);
            else await _users.SetRoleAsync(MemberGh, UserRole.Member);
        }
    }

    [Fact]
    public async Task AuthorizedPicker_ReadsAndStagesTheExactBytesInItsOwnStore()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        byte[] payload = [10, 20, 30, 40];
        var file = new ObservedBrowserFile(new MemoryStream(payload));
        await rendered.PickAsync(file);
        Assert.True(file.ReadCalls > 0);
        Assert.NotNull(rendered.Pending);
        Assert.Equal(payload, await File.ReadAllBytesAsync(rendered.StagedFilePath(), Ct));
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealWritePendingAcrossReplacement_KeepsBytesInAcceptingStoreAndNeverStagesInNewRuntime(bool picker)
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var oldBinding = rendered.Page.CurrentCallbackBinding!;
        var oldRoot = oldBinding.Attachments!.AttachmentsRootPath;
        using var stream = new BlockingReadStream([7, 8, 9, 10]);
        var write = picker ? rendered.PickAsync(new ObservedBrowserFile(stream)) : rendered.PasteAsync(stream);
        try
        {
            await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(write.IsCompleted);
            Assert.True(await host.Manager.RemoveAsync(_memberId));
            await rendered.InvokeAsync("ConnectRuntimeAsync");
            var replacement = rendered.Page.CurrentCallbackBinding!;
            Assert.NotSame(oldBinding.Composer, replacement.Composer);
            Assert.Same(replacement.Composer, rendered.Subscribed);
            Assert.False(write.IsCompleted);
            stream.Release();
            await write.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Same(replacement, rendered.Page.CurrentCallbackBinding);
            Assert.Same(replacement.Composer, rendered.Subscribed);
            Assert.Null(rendered.Pending);
            var saved = Assert.Single(Directory.GetFiles(oldRoot));
            Assert.Equal(new byte[] { 7, 8, 9, 10 }, await File.ReadAllBytesAsync(saved, Ct));
            Assert.DoesNotContain("chat-attach-chip-name", rendered.Html, StringComparison.Ordinal);
        }
        finally
        {
            stream.Release();
            await write.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealWritePendingAcrossDemotion_NoInterveningPageOperation_CompletesFailClosed(bool picker)
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        var accepting = rendered.Page.CurrentCallbackBinding!;
        using var stream = new BlockingReadStream([7, 8, 9, 10]);
        var write = picker ? rendered.PickAsync(new ObservedBrowserFile(stream)) : rendered.PasteAsync(stream);
        try
        {
            await stream.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(write.IsCompleted);
            await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
            Assert.Same(accepting, rendered.Page.CurrentCallbackBinding); // no page operation observed it yet
            stream.Release();
            await write.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Null(rendered.Pending);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
            var saved = Assert.Single(Directory.GetFiles(accepting.Attachments!.AttachmentsRootPath));
            Assert.Equal(new byte[] { 7, 8, 9, 10 }, await File.ReadAllBytesAsync(saved, Ct));
        }
        finally
        {
            stream.Release();
            await write.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await _users.SetRoleAsync(MemberGh, UserRole.Member);
        }
    }

    [Fact]
    public async Task UnboundCallbackBeforeAfterRender_MirrorsFalseToTheStillInstalledListener()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.AfterRenderAsync();
        Assert.Contains(true, host.Js.AllowedValues); // non-vacuous ready control
        var before = host.Js.AllowedValues.Count;
        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            rendered.Raise("HandleStreamingUpdate");
            await rendered.QueuedCallbacksAsync();
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            // Static renderer has not invoked OnAfterRender yet: the listener still EXISTS.
            Assert.Equal(0, host.Js.Count("uninstall"));
            var mirrored = host.Js.AllowedValues.Skip(before).ToArray();
            Assert.NotEmpty(mirrored);
            Assert.All(mirrored, value => Assert.False(value));
            await rendered.AfterRenderAsync();
            Assert.Equal(1, host.Js.Count("uninstall"));
            Assert.True(host.Js.Modules[0].Disposed);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    [Fact]
    public async Task ReadyToReplacementDeferredToConnected_UninstallsAndReinstallsOnTheNewTextarea()
    {
        var token = true;
        await using var host = NewHost(tokenAvailable: () => token);
        await using var rendered = await RenderAsync(host, OriginalGh);
        await rendered.AfterRenderAsync();
        Assert.Equal(1, host.Js.Count("install"));
        var old = rendered.Page.CurrentCallbackBinding!.Composer;
        host.Provider.GetRequiredService<HiveConfigFile>().Composer!.Model = CopilotModel;
        token = false;
        Assert.True(await host.Manager.RemoveAsync(_originalId));
        await rendered.InvokeAsync("CompactSession"); // facade returns deferred; its adoption binds replacement
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Deferred, rendered.Status);
        Assert.NotSame(old, rendered.Page.CurrentCallbackBinding!.Composer);
        Assert.DoesNotContain("composer-input", rendered.Html, StringComparison.Ordinal);
        await rendered.AfterRenderAsync();
        Assert.Equal(1, host.Js.Count("uninstall"));
        Assert.True(host.Js.Modules[0].Disposed);
        token = true;
        await rendered.InvokeAsync("ConnectRuntimeAsync");
        Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        Assert.Contains("composer-input", rendered.Html, StringComparison.Ordinal);
        await rendered.AfterRenderAsync();
        await rendered.AfterRenderAsync();
        Assert.Equal(2, host.Js.Count("install"));
        Assert.Equal(1, host.Js.Count("uninstall"));
        Assert.Equal(2, host.Js.Modules.Count);
        Assert.False(host.Js.Modules[1].Disposed);
        Assert.True(host.Js.AllowedValues.Last());
    }

    [Theory]
    [InlineData("CompactPartial")]
    [InlineData("HandleModelChange")]
    public async Task OtherFacadeOperations_AfterDemotion_AdoptRefusalAndUnsubscribe(string operation)
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);
        await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            if (operation == "HandleModelChange")
                await rendered.UiEventAsync(operation, new ChangeEventArgs { Value = CopilotModel });
            else await rendered.InvokeAsync(operation);
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
        }
        finally { await _users.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    [Theory]
    [InlineData("HandleReasoningChange")]
    [InlineData("CompactSession")]
    public async Task FacadeOperationOnReplacement_ActsOnCurrentRuntimeAndAdoptsItsRestoredHistory(string operation)
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var old = rendered.Page.CurrentCallbackBinding!.Composer;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rendered.Page.CallbackGate = async body => { await release.Task; await body(); };
        try
        {
            Assert.True(await host.Manager.RemoveAsync(_memberId));
            await SeedThreadAsync(host);
            Assert.Same(old, rendered.Page.CurrentCallbackBinding!.Composer);
            if (operation == "HandleReasoningChange")
                await rendered.UiEventAsync(operation, new ChangeEventArgs { Value = "high" });
            else await rendered.UiEventAsync(operation);
            var current = (await host.Scope(rendered).Accessor.AcquireForCircuitAsync(rendered.State, Ct)).Runtime!.Composer;
            Assert.NotSame(old, current);
            if (operation == "HandleReasoningChange") Assert.Equal(ReasoningEffort.High, current.ReasoningEffort);
            else Assert.Null(rendered.SendError); // real compaction completed normally, not against disposed A
            Assert.Same(current, rendered.Page.CurrentCallbackBinding!.Composer);
            Assert.Same(current, rendered.Subscribed);
            Assert.Contains(Marker, rendered.Html, StringComparison.Ordinal);
            Assert.Contains(current.GetChatHistory(), message => message.Content.Contains(Marker, StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
            await rendered.QueuedCallbacksAsync();
            rendered.Page.CallbackGate = null;
        }
    }

    [Fact]
    public async Task ReadyFacadeReplacement_ClearingPendingAttachment_ResynchronizesExistingPasteListener()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        await rendered.AfterRenderAsync();
        Assert.True(host.Js.AllowedValues.Last());
        await rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));
        Assert.NotNull(rendered.Pending);
        Assert.False(host.Js.AllowedValues.Last());
        var old = rendered.Page.CurrentCallbackBinding!.Composer;
        // This probe is a FACADE operation; unrelated teardown callbacks must not hide its JS effect.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rendered.Page.CallbackGate = async body => { await release.Task; await body(); };
        try
        {
            Assert.True(await host.Manager.RemoveAsync(_memberId));
            await rendered.UiEventAsync("CompactSession");
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
            Assert.NotSame(old, rendered.Page.CurrentCallbackBinding!.Composer);
            Assert.Null(rendered.Pending);
            await rendered.AfterRenderAsync();
            Assert.Equal(1, host.Js.Count("install")); // same connected input, no duplicate listener
            Assert.Equal(0, host.Js.Count("uninstall"));
            Assert.True(host.Js.AllowedValues.Last(),
                "Ready replacement has no staged attachment; the surviving input's paste mirror must become allowed.");
        }
        finally
        {
            release.TrySetResult();
            await rendered.QueuedCallbacksAsync();
            rendered.Page.CallbackGate = null;
        }
    }

    [Fact]
    public async Task PendingPasteImport_ResumingAfterRefusal_DoesNotInstallAndReleasesItsModule()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var hold = host.Js.ArmImport();
        var installation = rendered.AfterRenderAsync();
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(installation.IsCompleted);
            await _users.SetRoleAsync(MemberGh, UserRole.Viewer);
            await rendered.UiEventAsync("ConnectRuntimeAsync");
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            // A real circuit renders again while the prior import is outstanding.
            await rendered.AfterRenderAsync();
            Assert.False(installation.IsCompleted);
            hold.Release();
            await installation.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(0, host.Js.Count("install"));
            Assert.True(Assert.Single(host.Js.Modules).Disposed,
                "The late module belongs to a removed textarea and must be disposed, not installed or retained.");
            Assert.False((bool)typeof(ComposerChat).GetField("_pasteInstalled", PrivateInstance)!.GetValue(rendered.Page)!);
            Assert.Null(typeof(ComposerChat).GetField("_pasteModule", PrivateInstance)!.GetValue(rendered.Page));
            Assert.Null(rendered.Subscribed);
        }
        finally
        {
            hold.Release();
            await installation.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await _users.SetRoleAsync(MemberGh, UserRole.Member);
        }
    }

    [Fact]
    public async Task PendingPasteImport_WithNoSelectionChange_InstallsExactlyOnceAndAllowsPaste()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var hold = host.Js.ArmImport();
        var installation = rendered.AfterRenderAsync();
        try
        {
            await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.False(installation.IsCompleted);
            hold.Release();
            await installation.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await rendered.AfterRenderAsync();
            Assert.Equal(1, host.Js.Count("install"));
            Assert.Equal(0, host.Js.Count("uninstall"));
            Assert.False(Assert.Single(host.Js.Modules).Disposed);
            Assert.True(host.Js.AllowedValues.Last());
        }
        finally
        {
            hold.Release();
            await installation.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }

    private const System.Reflection.BindingFlags PrivateInstance =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    // The picker and paste use their REAL production hosts/funnels; this file only supplies bytes.
    private sealed class ObservedBrowserFile(Stream content) : Microsoft.AspNetCore.Components.Forms.IBrowserFile
    {
        public int ReadCalls { get; private set; }
        public string Name => "observed-picker.png";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => 4;
        public string ContentType => "image/png";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new ReadObserver(content, () => ReadCalls++);
    }

    private sealed class ReadObserver(Stream content, Action onRead) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return content.ReadAsync(buffer, cancellationToken);
        }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── Host / rendering ─────────────────────────────────────────────────────────────────────────

    private async Task SeedThreadAsync(Host host)
    {
        var state = await ComposerUserState.ResolveAsync(_users.Service, _memberId, Ct);
        var path = ComposerUserState.SessionFilePath(host.StateDir, state);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, Marker));
        await session.SaveAsync(path, Ct);
    }

    private Host NewHost(string primaryModel = Model, Func<bool>? tokenAvailable = null)
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        var js = new RecordingJsRuntime();
        var gate = new GatingAccessor();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<IJSRuntime>(js);
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }, new ModelEntry { Name = CopilotModel }] },
            Composer = new ComposerConfig { Model = primaryModel },
        });
        services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_users.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(), sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: tokenAvailable ?? (() => true),
            providerOf: m => m.StartsWith("copilot/", StringComparison.Ordinal) ? "copilot" : "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(), sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(), sp.GetRequiredService<HivePolicy>()));
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);
        // The gating wrapper decorates the REAL accessor of the same scope; the facades resolve the same wrapper.
        services.AddScoped<IComposerRuntimeAccessor>(sp => gate.Wrap(sp.GetRequiredService<ComposerRuntimeAccessor>()));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), stateDir, js, gate);
    }

    private async Task<Rendered> RenderAsync(Host host, string gitHubId)
    {
        var state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies"))));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;

        Func<string>? rootHtml = null;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = state,
                ["ChildContent"] = (RenderFragment)(b =>
                {
                    b.OpenComponent<ComposerChat>(0);
                    b.AddComponentReferenceCapture(1, c => page = (ComposerChat)c);
                    b.CloseComponent();
                }),
            });
            // THE RETAINED ROOT: kept for the page's whole life; every later markup read is ITS output.
            var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            rootHtml = root.ToHtmlString;
        });

        Assert.NotNull(page);
        host.Scopes[page!] = scope;
        return new Rendered(page!, scope, renderer, rootHtml!, state, host.Gate.ReleaseAll);
    }

    // ── Doubles ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>ONE RETAINED CIRCUIT: the original root is kept, so markup assertions read ITS output.</summary>
    private sealed class Rendered(
        ComposerChat page, IServiceScope scope, HtmlRenderer renderer,
        Func<string> rootHtml, Task<AuthenticationState> state, Action releaseGates) : IAsyncDisposable
    {
        private readonly List<Task> _storageWrites = [];
        private Task TrackStorage(Task task) { _storageWrites.Add(task); return task; }

        /// <summary>EVERY parked subscription callback invocation, so cleanup can join ALL of them.</summary>
        private readonly List<Task> _parkedCallbacks = [];

        /// <summary>The one release latch for the parked subscription callbacks; set by <see cref="ParkCallbacksAsync"/>.</summary>
        private TaskCompletionSource? _parkedRelease;

        /// <summary>Whether the retained circuit was already disposed (the cleanup is idempotent).</summary>
        private bool _disposed;

        public ComposerChat Page { get; } = page;
        public Task<AuthenticationState> State { get; } = state;
        public IServiceScope ServiceScope { get; } = scope;

        public string Html => renderer.Dispatcher.InvokeAsync(rootHtml).GetAwaiter().GetResult();

        public ComposerChat.ComposerCircuitStatus Status => Page.CurrentCallbackBinding!.Status;

        public Composer? Subscribed => (Composer?)typeof(ComposerChat)
            .GetField("_subscribedComposer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(Page);

        public ComposerAttachment? Pending => (ComposerAttachment?)typeof(ComposerChat)
            .GetField("_pending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(Page);

        public string? SendError => (string?)typeof(ComposerChat)
            .GetField("_sendError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(Page);

        public string StagedFilePath() =>
            Path.Combine(Page.CurrentCallbackBinding!.Attachments!.AttachmentsRootPath, Pending!.SavedRelativePath);

        public Task QueuedCallbacksAsync() => renderer.Dispatcher.InvokeAsync(() => Page.LastCallbackCompletion);

        public void Raise(string handler) => typeof(ComposerChat)
            .GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(Page, null);

        public async Task InvokeAsync(string method, params object?[] args)
        {
            var m = typeof(ComposerChat).GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.NotNull(m);
            await renderer.Dispatcher.InvokeAsync(async () => await (Task)m!.Invoke(Page, args.Length == 0 ? null : args)!);
        }

        public Task UiEventAsync(string method, params object?[] args) => renderer.Dispatcher.InvokeAsync(() =>
            EventCallback.Factory.Create(Page, (Func<Task>)(() => InvokeAsync(method, args))).InvokeAsync());

        /// <summary>
        /// Runs the component's own <c>OnAfterRenderAsync</c>, which the STATIC html renderer never invokes but a real
        /// circuit invokes after every render — the lifecycle moment where the paste listener is installed/released.
        /// </summary>
        public Task AfterRenderAsync() => InvokeAsync("OnAfterRenderAsync", false);

        public Task PickAsync(Microsoft.AspNetCore.Components.Forms.IBrowserFile file) =>
            TrackStorage(InvokeAsync("HandleFileSelected", new Microsoft.AspNetCore.Components.Forms.InputFileChangeEventArgs([file])));

        public Task PasteAsync(Stream content) => TrackStorage(renderer.Dispatcher.InvokeAsync(() =>
            Page.HandlePasteAsync(new FakeJSStreamReference(content), "image/png", 4)));

        /// <summary>
        /// CONTROLS THE INCIDENTAL SUBSCRIPTION PATHS of a replacement scenario: first DRAINS the callbacks the initial
        /// render already queued (with no gate installed they run now, so they can neither take a hold armed for a
        /// scenario path nor supersede a binding asserted later), then PARKS every LATER callback behind the page's
        /// <see cref="ComposerChat.CallbackGate"/> so none of them can acquire, bind, drop or re-render until disposal
        /// releases them.
        /// <para>
        /// EVERY PARKED INVOCATION IS TRACKED: the gate records the task of EACH invocation it holds, and
        /// <see cref="DisposeAsync"/> joins all of them — <see cref="ComposerChat.LastCallbackCompletion"/> alone is
        /// overwritten by each new callback and therefore aggregates nothing.
        /// </para>
        /// </summary>
        public async Task ParkCallbacksAsync()
        {
            // DRAIN: everything queued so far runs to completion here (no gate is installed yet).
            await renderer.Dispatcher.InvokeAsync(() => Page.LastCallbackCompletion);

            var parked = _parkedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Page.CallbackGate = body =>
            {
                var invocation = RunParkedAsync(parked.Task, body);
                lock (_parkedCallbacks) _parkedCallbacks.Add(invocation);
                return invocation;
            };
        }

        /// <summary>One parked callback invocation: waits for the release latch, then runs its body.</summary>
        private static async Task RunParkedAsync(Task release, Func<Task> body)
        {
            await release;
            await body();
        }

        /// <summary>How many parked invocations this helper is tracking (the ones cleanup must join).</summary>
        public int ParkedCount { get { lock (_parkedCallbacks) return _parkedCallbacks.Count; } }

        /// <summary>
        /// Waits until at least <paramref name="count"/> invocations are parked. The queued bodies are dispatched
        /// on the renderer's dispatcher, so the count grows there — this waits on the SAME dispatcher, which
        /// serializes it with the queuing, so no polling of private state is needed.
        /// </summary>
        public async Task WaitForParkedCountAsync(int count)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                // The dispatcher is the serialization point: the queued bodies are already registered by the
                // time this continuation runs, because each Raise() ran ON the dispatcher too.
                while (ParkedCount < count)
                    await Task.Yield();
            });
        }

        /// <summary>
        /// How many parked invocations cleanup actually JOINED. It must equal <see cref="ParkedCount"/>:
        /// a cleanup that joined only the page's <c>LastCallbackCompletion</c> would leave the EARLIER
        /// invocations un-awaited (and their faults unobserved).
        /// </summary>
        public int JoinedParkedCount { get; private set; }

        /// <summary>
        /// The state of every tracked parked invocation AFTER cleanup: <c>null</c> when none was tracked,
        /// otherwise one entry per invocation. A tracked invocation that cleanup did not join is left
        /// <see cref="TaskStatus.WaitingForActivation"/> or <see cref="TaskStatus.Running"/>; one it joined
        /// is <see cref="TaskStatus.RanToCompletion"/> or <see cref="TaskStatus.Faulted"/>.
        /// </summary>
        public TaskStatus[] ParkedStatuses()
        {
            lock (_parkedCallbacks) return [.. _parkedCallbacks.Select(t => t.Status)];
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            var failures = new List<Exception>();
            async Task<bool> Step(Func<Task> action)
            {
                try { await action(); return true; }
                catch (Exception ex) { failures.Add(ex); return false; }
            }

            // FAILURE-SAFE RELEASE, in a finally-shaped sequence: the parked callbacks are released FIRST (so no
            // invocation stays blocked), then every hold/gate is released, then each tracked task is joined.
            _parkedRelease?.TrySetResult();
            releaseGates();
            foreach (var write in _storageWrites)
                await Step(() => write.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));

            // JOIN EVERY PARKED INVOCATION, not only the last one the page recorded.
            Task[] parked;
            lock (_parkedCallbacks) parked = [.. _parkedCallbacks];
            foreach (var invocation in parked)
            {
                var joined = await Step(() => invocation.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
                // A FAULTED invocation was still JOINED (awaited to its end); only a wait that TIMED OUT means
                // the invocation was not joined.
                if (joined || invocation.IsCompleted)
                    JoinedParkedCount++;
            }
            await Step(() => Page.LastCallbackCompletion.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => renderer.DisposeAsync().AsTask());
            await Step(() => { ServiceScope.Dispose(); return Task.CompletedTask; });

            if (failures.Count == 0)
                return; // a primary scenario failure (if any) keeps propagating from the `await using` untouched

            // PRECEDENCE: an exception thrown by DisposeAsync REPLACES whatever the `await using` body threw, and a
            // DisposeAsync cannot observe that body exception by itself. So when the scenario was run through
            // RunScenarioAsync (which captured its failure), the PRIMARY failure is thrown FIRST in the aggregate and
            // the cleanup failures ride alongside it — never silently dropped, never replacing it. A scenario NOT run
            // through RunScenarioAsync has no known primary, so only the cleanup failures can be reported.
            if (_primaryFailure is { } primary)
                throw new AggregateException("Scenario failed; retained circuit cleanup ALSO failed", [primary, .. failures]);

            throw new AggregateException("Retained circuit cleanup failed", failures);
        }

        private Exception? _primaryFailure;

        /// <summary>
        /// Runs a scenario body and CAPTURES its failure (then rethrows it unchanged), so that a later cleanup fault
        /// can be reported alongside it instead of replacing it. Only a scenario run through this method gets the
        /// primary-failure precedence in <see cref="DisposeAsync"/>.
        /// </summary>
        public async Task RunScenarioAsync(Func<Task> scenario)
        {
            try { await scenario(); }
            catch (Exception ex) { _primaryFailure = ex; throw; }
        }

        /// <summary>
        /// TEST SEAM for the cleanup-fault path: tracks one more parked invocation, on the SAME release latch and
        /// through the SAME tracking list as every real parked callback, whose body FAULTS with
        /// <paramref name="fault"/> once released — i.e. a cleanup-time fault that cleanup must observe by joining it.
        /// </summary>
        public void ParkFaultingInvocation(Exception fault)
        {
            var release = (_parkedRelease ?? throw new InvalidOperationException("ParkCallbacksAsync was not called.")).Task;
            var invocation = RunParkedAsync(release, () => throw fault);
            lock (_parkedCallbacks) _parkedCallbacks.Add(invocation);
        }
    }

    private sealed class Host(
        ServiceProvider provider, ComposerRuntimeManager manager, string stateDir,
        RecordingJsRuntime js, GatingAccessor gate) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public ComposerRuntimeManager Manager { get; } = manager;
        public string StateDir { get; } = stateDir;
        public RecordingJsRuntime Js { get; } = js;
        public GatingAccessor Gate { get; } = gate;
        public Dictionary<ComposerChat, IServiceScope> Scopes { get; } = [];

        public (IComposerRuntimeAccessor Accessor, int _) Scope(Rendered r) =>
            (Scopes[r.Page].ServiceProvider.GetRequiredService<ComposerRuntimeAccessor>(), 0);

        public async ValueTask DisposeAsync()
        {
            Gate.ReleaseAll();
            var failures = new List<Exception>();
            try { await Manager.ShutdownAsync(); } catch (Exception ex) { failures.Add(ex); }
            try { await Provider.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0) throw new AggregateException("Regression host cleanup failed", failures);
        }
    }

    /// <summary>
    /// Decorates the REAL scoped accessor: counts connects on both paths and can HOLD one acquisition's answer
    /// after it was computed, so a newer change can land while an older answer is in flight.
    /// </summary>
    private sealed class GatingAccessor
    {
        private Hold? _armed;
        private readonly List<Hold> _holds = [];
        public void ReleaseAll() { foreach (var hold in _holds) hold.Release(); }
        public int CircuitConnects;
        public int RequestConnects;

        public IComposerRuntimeAccessor Wrap(IComposerRuntimeAccessor inner) => new Wrapper(this, inner);

        public Hold Arm()
        {
            var hold = new Hold();
            _holds.Add(hold);
            _armed = hold;
            return hold;
        }

        public sealed class Hold
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Gate => _release.Task;
            public void Release() => _release.TrySetResult();
        }

        private sealed class Wrapper(GatingAccessor owner, IComposerRuntimeAccessor inner) : IComposerRuntimeAccessor
        {
            public bool AuthEnabled => inner.AuthEnabled;

            public async Task<ComposerRuntimeAccess> AcquireForCircuitAsync(Task<AuthenticationState>? s, CancellationToken ct)
            {
                var answer = await inner.AcquireForCircuitAsync(s, ct);
                var hold = Interlocked.Exchange(ref owner._armed, null);
                if (hold is not null)
                {
                    hold.Entered.TrySetResult();
                    await hold.Gate.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                return answer;
            }

            public Task<ComposerRuntimeAccess> ConnectForCircuitAsync(Task<AuthenticationState>? s, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.CircuitConnects);
                return inner.ConnectForCircuitAsync(s, ct);
            }

            public Task<ComposerRuntimeAccess> AcquireForRequestAsync(ClaimsPrincipal? u, CancellationToken ct) =>
                inner.AcquireForRequestAsync(u, ct);

            public Task<ComposerRuntimeAccess> ConnectForRequestAsync(ClaimsPrincipal? u, CancellationToken ct)
            {
                Interlocked.Increment(ref owner.RequestConnects);
                return inner.ConnectForRequestAsync(u, ct);
            }
        }
    }

    /// <summary>A JS runtime whose <c>import</c> yields a recording module (install / uninstall / setAllowed).</summary>
    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<RecordingModule> Modules { get; } = [];
        public List<bool> AllowedValues { get; } = [];
        private ImportHold? _importHold;
        public ImportHold ArmImport() => _importHold = new ImportHold();
        public sealed class ImportHold
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Gate => _release.Task;
            public void Release() => _release.TrySetResult();
        }
        private static async Task<TValue> CompleteImportAsync<TValue>(RecordingModule module, ImportHold hold, CancellationToken ct)
        {
            hold.Entered.TrySetResult();
            await hold.Gate.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return (TValue)(object)module;
        }

        public int Count(string call) => Modules.Sum(m => m.Calls.Count(c => c == call));

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "import")
            {
                var module = new RecordingModule(this);
                Modules.Add(module);
                var hold = Interlocked.Exchange(ref _importHold, null);
                return hold is null ? ValueTask.FromResult((TValue)(object)module)
                    : new ValueTask<TValue>(CompleteImportAsync<TValue>(module, hold, ct));
            }
            return default!;
        }
    }

    private sealed class RecordingModule(RecordingJsRuntime owner) : IJSObjectReference
    {
        public List<string> Calls { get; } = [];
        public bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "setAllowed") owner.AllowedValues.Add((bool)args![0]!);
            return default!;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A stream whose first read BLOCKS until released — a genuinely in-flight write.</summary>
    private sealed class BlockingReadStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadEntered.TrySetResult();
            await _release.Task;
            var n = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            Release();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
