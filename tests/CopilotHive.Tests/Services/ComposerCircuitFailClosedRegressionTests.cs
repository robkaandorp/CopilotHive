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

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-failclosed-{Guid.NewGuid():N}");

    private int _originalId;
    private int _memberId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _originalId = (await _auth.SeedAsync(OriginalGh, UserRole.Admin)).Id;
        _memberId = (await _auth.SeedAsync(MemberGh, UserRole.Member)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        _auth.Dispose();
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            rendered.Raise("HandleStreamingUpdate");
            await rendered.QueuedCallbacksAsync();

            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            var html = rendered.Html; // the RETAINED root's own output
            Assert.DoesNotContain(Marker, html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", html, StringComparison.Ordinal);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("RemoveAttachment");

            Assert.True(File.Exists(file), "a refused caller's staged file must not be deleted");
            Assert.NotEqual(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.PasteAsync(new MemoryStream([1, 2, 3, 4]));

            Assert.Empty(Directory.GetFiles(root));
            Assert.Null(rendered.Pending);
            Assert.NotEqual(ComposerChat.ComposerCircuitStatus.Ready, rendered.Status);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("CompactSession");

            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Html, StringComparison.Ordinal);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
    }

    /// <summary>A DEMOTED REASONING SWITCH hides the content and unsubscribes.</summary>
    [Fact]
    public async Task ADemotedReasoningSwitch_HidesTheContent_AndUnsubscribes()
    {
        await using var host = NewHost();
        await SeedThreadAsync(host);
        await using var rendered = await RenderAsync(host, MemberGh);

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        try
        {
            await rendered.InvokeAsync("HandleReasoningChange", new ChangeEventArgs { Value = "high" });

            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, rendered.Status);
            Assert.Null(rendered.Subscribed);
            Assert.DoesNotContain(Marker, rendered.Html, StringComparison.Ordinal);
        }
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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
    /// </summary>
    [Fact]
    public async Task AFacadeOperation_OnAReplacedRuntime_UnifiesTheBindingAndSubscription()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, MemberGh);
        var old = rendered.Page.CurrentCallbackBinding!.Composer;

        Assert.True(await host.Manager.RemoveAsync(_memberId));
        await rendered.InvokeAsync("CompactSession");

        var current = (await host.Scope(rendered).Accessor.AcquireForCircuitAsync(rendered.State, Ct)).Runtime!.Composer;
        Assert.NotSame(old, current);
        Assert.Same(current, rendered.Page.CurrentCallbackBinding!.Composer);
        Assert.Same(current, rendered.Subscribed);
    }

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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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

        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
        await rendered.InvokeAsync("ConnectRuntimeAsync");
        await rendered.AfterRenderAsync();
        await _auth.SetRoleAsync(MemberGh, UserRole.Member);
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
        if (disabled) await _auth.SetEnabledAsync(MemberGh, false);
        else await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
            if (disabled) await _auth.SetEnabledAsync(MemberGh, true);
            else await _auth.SetRoleAsync(MemberGh, UserRole.Member);
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
            await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
            await _auth.SetRoleAsync(MemberGh, UserRole.Member);
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
        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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
        await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
        finally { await _auth.SetRoleAsync(MemberGh, UserRole.Member); }
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
            await _auth.SetRoleAsync(MemberGh, UserRole.Viewer);
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
            await _auth.SetRoleAsync(MemberGh, UserRole.Member);
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
        var state = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
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
        services.AddSingleton(_auth.Directory.Service);
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

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            releaseGates();
            foreach (var write in _storageWrites)
                await Step(() => write.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => Page.LastCallbackCompletion.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            await Step(() => renderer.DisposeAsync().AsTask());
            await Step(() => { ServiceScope.Dispose(); return Task.CompletedTask; });
            if (failures.Count > 0) throw new AggregateException("Retained circuit cleanup failed", failures);
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
