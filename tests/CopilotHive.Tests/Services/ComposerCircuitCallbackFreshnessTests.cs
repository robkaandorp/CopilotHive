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

using Moq;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE FRESHNESS CONTRACT FOR QUEUED WORK: a subscription callback or an asynchronous upload/paste completion
/// that is still PENDING when the caller is demoted/disabled, or when the runtime is removed and recreated, must
/// NOT restore content, must NOT re-subscribe handlers and must NOT re-attach the component to a runtime the
/// caller is no longer authorized for (or to an instance that is no longer the current one).
/// <para>
/// Every row here is DETERMINISTIC: the stale window is created by queueing the callback/completion and only then
/// changing the directory or replacing the runtime, and the queued work is observed through the page's own
/// <c>LastCallbackCompletion</c> task (or the completion's returned task). There is no polling, no timer and no
/// sleep anywhere in this file — the assertions are about what the production code did, not about how long it
/// took.
/// </para>
/// <para>
/// The host is the REAL one: the production <see cref="ComposerChat"/> page, the REAL
/// <see cref="IComposerRuntimeAccessor"/> (over the REAL manager, the REAL resolver/policy and a REAL SQLite
/// directory) and a REAL cascading <see cref="AuthenticationState"/>.
/// </para>
/// </summary>
public sealed class ComposerCircuitCallbackFreshnessTests : IAsyncLifetime, IDisposable
{
    private const string Model = "callback-freshness-model";

    private readonly FacadeAuthorizationHarness _auth = new();
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-callback-{Guid.NewGuid():N}");

    private int _memberId;

    public ComposerCircuitCallbackFreshnessTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The ORIGINAL identity is the lowest-id activated row (it may claim legacy history).
        _ = (await _auth.SeedAsync("callback-original", UserRole.Admin)).Id;
        _memberId = (await _auth.SeedAsync("callback-member", UserRole.Member)).Id;
    }

    // ── The AUTHORIZATION half of the guard, isolated from the binding half ─────────────────────

    /// <summary>
    /// A DEMOTION IS CAUGHT EVEN WHEN THE BINDING RECORD IS UNCHANGED: the queued body is captured and held, the
    /// account is demoted in the directory, and — crucially — NO operation runs to replace the binding, so the
    /// binding-identity check alone would still pass. Only the FRESH AUTHORIZATION re-check inside the guard can
    /// drop this callback.
    /// <para>
    /// This is the row that isolates the authorization half: replacing <c>DropStaleCallbackAsync</c>'s authority
    /// check with a binding-identity-only check makes it fail, while the other stale rows still pass (they change
    /// the binding). Without it, a demotion that no user-visible operation has observed yet would let a queued
    /// callback restore content from a runtime the caller may no longer use.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AQueuedCallback_IsDroppedByTheFreshAuthorizationCheck_WhenTheBindingIsUnchanged()
    {
        await using var host = NewHost();
        await SeedMemberSessionAsync(host, "member-thread-marker");

        await using var rendered = await RenderAsync(host, "callback-member");
        var bindingBefore = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(bindingBefore);

        // HOLD the callback, raise the real production handler, and confirm nothing was applied yet.
        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleStreamingUpdate");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        // The demotion lands in the DIRECTORY while the body is pending. NO page operation runs, so the binding
        // record is NOT replaced — only the directory changed.
        await _auth.SetRoleAsync("callback-member", UserRole.Viewer);
        try
        {
            hold.Release();
            await rendered.QueuedCallbacksAsync();

            // NO page operation ran before the release, so the binding record could not have been replaced by one:
            // the ONLY thing that changed is the DIRECTORY. The AUTHORIZATION half of the guard is therefore what
            // dropped the callback — a binding-identity-only guard would have APPLIED it.
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
            Assert.Equal(1, rendered.Page.DroppedCallbackCount);
            Assert.NotSame(bindingBefore, rendered.Page.CurrentCallbackBinding);

            // ...and the guard's own re-selection already observed the demotion, so nothing is bound any more.
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));
            Assert.Null(BoundComposerOf(rendered.Page));

            // ...and the stale body did not restore the old thread.
            Assert.DoesNotContain("member-thread-marker", rendered.Render(), StringComparison.Ordinal);
        }
        finally
        {
            await _auth.SetRoleAsync("callback-member", UserRole.Member);
        }
    }

    /// <summary>
    /// A DISABLEMENT IS CAUGHT THE SAME WAY — the second fail-closed directory change, isolated from the binding:
    /// a queued callback for a since-DISABLED account is dropped by the authorization check.
    /// </summary>
    [Fact]
    public async Task AQueuedCallback_IsDroppedByTheFreshAuthorizationCheck_WhenTheAccountIsDisabled()
    {
        await using var host = NewHost();
        await SeedMemberSessionAsync(host, "member-thread-marker");

        await using var rendered = await RenderAsync(host, "callback-member");
        var bindingBefore = rendered.Page.CurrentCallbackBinding;

        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleStreamingUpdate");

        await _auth.SetEnabledAsync("callback-member", enabled: false);
        try
        {
            hold.Release();
            await rendered.QueuedCallbacksAsync();

            // The DISABLEMENT alone (no operation ran first) dropped the callback...
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
            Assert.Equal(1, rendered.Page.DroppedCallbackCount);
            // ...and the guard's re-selection observed it.
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));
            Assert.Null(BoundComposerOf(rendered.Page));
        }
        finally
        {
            await _auth.SetEnabledAsync("callback-member", enabled: true);
        }
    }

    // ── AC2 / scope item 8: a QUEUED subscription callback is stale after a demotion ─────────────

    /// <summary>
    /// A QUEUED CALLBACK CANNOT RESTORE CONTENT AFTER A DEMOTION: the Member's page renders its own runtime and
    /// its own thread; a streaming callback is then QUEUED, the account is demoted in the DIRECTORY, and only
    /// then does the queued callback run. It must observe the stale binding, drop its work — no history reload,
    /// no re-render — and the component must NOT keep the runtime it may no longer use.
    /// <para>
    /// THE DISCRIMINATOR is the rendered content: after the stale callback runs, the page's displayed thread is
    /// still gone (the demotion's own operation cleared it) — a callback that restored content from the old
    /// runtime would put the marker back on screen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AQueuedCallback_CannotRestoreContent_AfterADemotion()
    {
        await using var host = NewHost();
        await SeedMemberSessionAsync(host, "member-thread-marker");

        await using var rendered = await RenderAsync(host, "callback-member");
        Assert.Contains("member-thread-marker", rendered.Html, StringComparison.Ordinal);

        // HOLD the callbacks: the production handler's body stays genuinely PENDING.
        await using var hold = rendered.HoldCallbacks();

        // The production handler runs (exactly as the Composer's own event does) and queues its body behind the
        // hold, so it has NOT been applied yet.
        rendered.Raise("HandleStreamingUpdate");
        var capturedBinding = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(capturedBinding);
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        // The account is demoted in the DIRECTORY while the queued body is still pending — and the page's own
        // operation observes it too (a separate, ordinary operation, not part of the callback).
        await _auth.SetRoleAsync("callback-member", UserRole.Viewer);
        try
        {
            await rendered.ConnectRuntimeAsync();
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));

            // RELEASE: the queued body runs NOW, against the demoted circuit.
            hold.Release();
            await rendered.QueuedCallbacksAsync();

            // The page is forbidden and unbound, and the old marker never came back.
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));
            Assert.Null(BoundComposerOf(rendered.Page));
            Assert.DoesNotContain("member-thread-marker", rendered.Render(), StringComparison.Ordinal);
            Assert.Contains("You do not have access to the Composer.", rendered.Render(), StringComparison.Ordinal);

            // ...and the stale callback did not RE-SUBSCRIBE the component to the old runtime.
            Assert.Null(SubscribedComposerOf(rendered.Page));

            // THE GUARD REALLY DECIDED: the queued body was not applied. (The positive control below proves the
            // same path DOES apply when nothing changed, so these two counters are load-bearing together.)
            Assert.Equal(0, rendered.Page.AppliedCallbackCount);
        }
        finally
        {
            await _auth.SetRoleAsync("callback-member", UserRole.Member);
        }
    }

    /// <summary>
    /// THE SAME STALE WINDOW AFTER A RUNTIME REPLACEMENT: the queued callback captured the ORIGINAL runtime; the
    /// manager's runtime is then removed and a FRESH one is created for the same account, and the page re-selects
    /// it. The queued callback must not act on the OLD runtime — and, decisively, must not re-subscribe the
    /// component to it.
    /// </summary>
    [Fact]
    public async Task AQueuedCallback_CannotReattachToAReplacedRuntime()
    {
        await using var host = NewHost();
        await SeedMemberSessionAsync(host, "first-runtime-marker");

        await using var rendered = await RenderAsync(host, "callback-member");
        var firstRuntime = BoundComposerOf(rendered.Page);
        Assert.NotNull(firstRuntime);

        // HOLD the callbacks so the queued body is genuinely pending across the replacement.
        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleStreamingUpdate");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        // The runtime is REMOVED and RECREATED for the same account, and the page re-selects it.
        await host.Manager.RemoveAsync(_memberId);
        await rendered.ConnectRuntimeAsync();
        hold.Release();

        var secondRuntime = BoundComposerOf(rendered.Page);
        Assert.NotNull(secondRuntime);
        Assert.NotSame(firstRuntime, secondRuntime!);

        // The queued callback for the OLD runtime has now run: it must have been dropped.
        await rendered.QueuedCallbacksAsync();

        // The component is still bound to the FRESH runtime, and subscribed to THAT one — never the removed one.
        Assert.Same(secondRuntime, BoundComposerOf(rendered.Page));
        Assert.Same(secondRuntime, SubscribedComposerOf(rendered.Page));
        Assert.NotSame(firstRuntime, SubscribedComposerOf(rendered.Page));

        // The queued body was dropped as stale: nothing was applied from the removed runtime.
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);
    }

    /// <summary>
    /// A QUEUED CALLBACK IS NOT STALE WHEN NOTHING CHANGED: the same window, but no demotion and no replacement —
    /// the queued callback runs normally and the component keeps its binding. Without this control the rows above
    /// would pass even if EVERY callback were silently dropped.
    /// </summary>
    [Fact]
    public async Task AQueuedCallback_StillRuns_WhenNothingChanged()
    {
        await using var host = NewHost();
        await SeedMemberSessionAsync(host, "stable-runtime-marker");

        await using var rendered = await RenderAsync(host, "callback-member");
        var bound = BoundComposerOf(rendered.Page);
        Assert.NotNull(bound);

        var bindingBefore = rendered.Page.CurrentCallbackBinding;

        // The SAME hold, and the same production handler: nothing changes while it is queued.
        await using var hold = rendered.HoldCallbacks();
        rendered.Raise("HandleStreamingUpdate");
        Assert.Equal(0, rendered.Page.AppliedCallbackCount);

        hold.Release();
        await rendered.QueuedCallbacksAsync();

        // The binding record was not replaced, the component is still bound and subscribed...
        Assert.Same(bindingBefore, rendered.Page.CurrentCallbackBinding);
        Assert.Same(bound, BoundComposerOf(rendered.Page));
        Assert.Same(bound, SubscribedComposerOf(rendered.Page));
        Assert.Contains("stable-runtime-marker", rendered.Render(), StringComparison.Ordinal);

        // ...and the very same queued path APPLIED the callback, so the stale rows above cannot be satisfied by
        // dropping every callback unconditionally.
        Assert.Equal(1, rendered.Page.AppliedCallbackCount);
    }

    /// <summary>
    /// AN OPERATION THAT RE-SELECTS THE SAME RUNTIME KEEPS THE BINDING RECORD: this is what makes the freshness
    /// guard usable at all — a normal operation (a reload, a connect, a read) must not invalidate callbacks that
    /// are legitimately pending for the same runtime. Only a DIFFERENT runtime (or a fail-closed status) replaces
    /// the record.
    /// </summary>
    [Fact]
    public async Task AnOperationThatReselectsTheSameRuntime_PreservesTheBindingRecord()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, "callback-member");

        var before = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(before);

        // A normal operation re-runs the whole selection.
        await rendered.ConnectRuntimeAsync();

        Assert.Same(before, rendered.Page.CurrentCallbackBinding);
    }

    // ── The asynchronous upload/paste completion ─────────────────────────────────────────────────

    /// <summary>
    /// A PENDING UPLOAD COMPLETION IS RE-AUTHORIZED BEFORE ITS OUTCOME IS APPLIED: the attachment is saved while
    /// the caller is still authorized, but the completion guard is asked on the way out — and the row proves the
    /// guard really runs on the completion path (not merely at the start).
    /// </summary>
    [Fact]
    public async Task AUploadCompletion_IsReAuthorizedBeforeItsOutcomeIsApplied()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, "callback-member");

        var before = rendered.Page.CurrentCallbackBinding;
        var host_ = rendered.NewPasteHost();
        await ComposerChat.SaveAttachmentAsync(
            host_,
            "authorized.png",
            new MemoryStream([1, 2, 3]));

        // The guard was asked exactly once, and it approved (nothing changed).
        Assert.Equal(1, host_.CompletionGuardChecks);
        Assert.True(host_.CompletionApproved);
        Assert.NotNull(host_.Pending);

        // ...and the completion did not replace the binding record.
        Assert.Same(before, rendered.Page.CurrentCallbackBinding);
    }

    /// <summary>
    /// AN UPLOAD THAT COMPLETES AFTER A DEMOTION IS NOT ATTACHED: the guard refuses, so the finished write is NOT
    /// reflected as the component's pending attachment (and no error is invented either — the write itself
    /// succeeded; it simply is not this circuit's attachment any more).
    /// <para>
    /// This is the deterministic stale-completion vector: the refusal is produced by the real component state
    /// (the binding was replaced by the page's own demotion operation) rather than by a sleep.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AUploadCompletingAfterADemotion_IsNotAttachedToTheCircuit()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, "callback-member");

        var pasteHost = rendered.NewPasteHost();

        // The demotion lands while the "upload" is in flight: the page's own operation replaces the binding with
        // the fail-closed forbidden one (the real production transition).
        await _auth.SetRoleAsync("callback-member", UserRole.Viewer);
        try
        {
            await rendered.ConnectRuntimeAsync();
            Assert.Equal(ComposerChat.ComposerCircuitStatus.Forbidden, BoundStatusOf(rendered.Page));

            // The completion runs now: it must be refused.
            await ComposerChat.SaveAttachmentAsync(pasteHost, "stale.png", new MemoryStream([1, 2, 3]));

            Assert.Equal(1, pasteHost.CompletionGuardChecks);
            Assert.False(pasteHost.CompletionApproved, "the stale completion must be refused");
            Assert.Null(pasteHost.Pending);
            Assert.Null(pasteHost.AttachmentError);
        }
        finally
        {
            await _auth.SetRoleAsync("callback-member", UserRole.Member);
        }
    }

    // ── The guard's own rules ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FRESHNESS GUARD IS A REFERENCE IDENTITY CHECK: the captured binding is accepted only while it IS the
    /// circuit's binding, and only when a binding was really captured — a null token is never "current".
    /// </summary>
    [Fact]
    public async Task TheFreshnessGuard_RequiresTheVerySameBindingRecord()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, "callback-member");

        var current = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(current);

        Assert.True(rendered.Page.IsCurrentCallbackBinding(current));
        // A null token is never current — an unbound component cannot validate stale work.
        Assert.False(rendered.Page.IsCurrentCallbackBinding(null));

        // A record with the same CONTENT but a different identity is NOT the current binding.
        var copy = new ComposerChat.ComposerCircuitBinding(
            current!.Status, current.Composer, current.Attachments, current.IsOpenModeLegacy);
        Assert.False(rendered.Page.IsCurrentCallbackBinding(copy));
    }

    /// <summary>
    /// THE "UNCHANGED SELECTION" RULE IS EXACTLY THE PRESERVATION RULE: the same status, the same Composer, the
    /// same attachment store and the same open-mode flag preserve the record; ANY difference replaces it.
    /// </summary>
    [Fact]
    public async Task TheUnchangedSelectionRule_IsExactlyThePreservationRule()
    {
        await using var host = NewHost();
        await using var rendered = await RenderAsync(host, "callback-member");

        var current = rendered.Page.CurrentCallbackBinding;
        Assert.NotNull(current);

        var same = new ComposerChat.ComposerCircuitBinding(
            current!.Status, current.Composer, current.Attachments, current.IsOpenModeLegacy);
        Assert.True(ComposerChat.IsUnchangedSelection(current, same));

        // A different STATUS replaces the record.
        Assert.False(ComposerChat.IsUnchangedSelection(current, new ComposerChat.ComposerCircuitBinding(
            ComposerChat.ComposerCircuitStatus.Failed, current.Composer, current.Attachments, current.IsOpenModeLegacy)));

        // A different COMPOSER replaces the record.
        Assert.False(ComposerChat.IsUnchangedSelection(current, new ComposerChat.ComposerCircuitBinding(
            current.Status, NewComposer(), current.Attachments, current.IsOpenModeLegacy)));

        // A different ATTACHMENT store replaces the record (an attachment operation must never cross stores).
        Assert.False(ComposerChat.IsUnchangedSelection(current, new ComposerChat.ComposerCircuitBinding(
            current.Status, current.Composer, NewAttachments(), current.IsOpenModeLegacy)));

        // A different open-mode flag replaces the record.
        Assert.False(ComposerChat.IsUnchangedSelection(current, new ComposerChat.ComposerCircuitBinding(
            current.Status, current.Composer, current.Attachments, !current.IsOpenModeLegacy)));
    }

    /// <summary>
    /// EVERY SUBSCRIPTION HANDLER APPLIES THE GUARD: the production source of each of the six handlers captures the
    /// binding synchronously and re-checks it inside the queued body. Deleting the guard from ONE handler fails
    /// this row, so no handler can lose its protection silently.
    /// </summary>
    [Fact]
    public void EverySubscriptionHandler_AppliesTheFreshnessGuard()
    {
        var repoRoot = TestRepoRoot.Find();
        var source = File.ReadAllText(Path.Combine(
            repoRoot, "src", "CopilotHive", "Components", "Pages", "ComposerChat.razor")).ReplaceLineEndings("\n");

        var handlers = new (string Signature, string Guard)[]
        {
            ("private void HandleStreamingUpdate()", "await DropStaleCallbackAsync(captured, CancellationToken.None)"),
            ("private void HandleSubAgentChanged(SubAgentInfo info)", "await DropStaleCallbackAsync(captured, CancellationToken.None)"),
            ("private void HandleCompactingStarted()", "await DropStaleCallbackAsync(captured, CancellationToken.None)"),
            ("private void HandleCompacted()", "await DropStaleCallbackAsync(captured, CancellationToken.None)"),
            ("private void HandleQuestionAsked()", "await DropStaleCallbackAsync(captured, CancellationToken.None)"),
            // The content-restoring callback applies the SAME full guard (binding identity + fresh authorization).
            ("private void HandleActiveInjection(string _)", "await DropStaleCallbackAsync(captured, CancellationToken.None)"),
        };

        foreach (var (signature, guard) in handlers)
        {
            var body = ExtractMethodSource(source, signature);

            // The token is CAPTURED SYNCHRONOUSLY (on the raising thread), before the body is queued.
            Assert.Contains("var captured = CurrentCallbackBinding;", body, StringComparison.Ordinal);
            // ...and the body re-checks it when it finally runs, returning early when it is stale.
            Assert.Contains(guard, body, StringComparison.Ordinal);
            Assert.Contains("return;", body, StringComparison.Ordinal);
            // No handler queues its body directly any more: they all go through the recording queue.
            Assert.DoesNotContain("InvokeAsync(", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE UPLOAD COMPLETION APPLIES THE SAME GUARD: the shared save funnel asks
    /// <see cref="ComposerChat.IPasteHost.IsCompletionStillAuthorizedAsync"/> AFTER the write and BEFORE it
    /// reflects the outcome — and both production fakes the flow uses implement it.
    /// </summary>
    [Fact]
    public void TheUploadCompletion_AppliesTheGuardBeforeReflectingTheOutcome()
    {
        var repoRoot = TestRepoRoot.Find();
        var source = File.ReadAllText(Path.Combine(
            repoRoot, "src", "CopilotHive", "Components", "Pages", "ComposerChat.razor")).ReplaceLineEndings("\n");

        var funnel = ExtractMethodSource(
            source, "internal static async Task SaveAttachmentAsync(IPasteHost host, string originalName, Stream stream)");

        var guardIndex = funnel.IndexOf("IsCompletionStillAuthorizedAsync(", StringComparison.Ordinal);
        var pendingIndex = funnel.IndexOf("host.Pending = result.Attachment;", StringComparison.Ordinal);
        var errorIndex = funnel.IndexOf("host.AttachmentError = result.Error", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "the save funnel must ask the completion guard");
        Assert.True(pendingIndex > guardIndex, "the guard must run BEFORE the pending attachment is set");
        Assert.True(errorIndex > guardIndex, "the guard must run BEFORE the error is surfaced");

        // The write itself still happens (the guard is about ATTACHING the result, never about skipping the save).
        var saveIndex = funnel.IndexOf("await host.SaveAsync(", StringComparison.Ordinal);
        Assert.True(saveIndex >= 0 && saveIndex < guardIndex,
            "the write runs first; the guard decides whether its outcome may be applied");
    }

    // ── Host / rendering ────────────────────────────────────────────────────────────────────────

    /// <summary>Writes the Member's OWN per-user session so a rendered thread really shows.</summary>
    private async Task SeedMemberSessionAsync(Host host, string marker)
    {
        var state = await ComposerUserState.ResolveAsync(_auth.Directory.Service, _memberId, Ct);
        var path = ComposerUserState.SessionFilePath(host.StateDir, state);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var session = SharpCoder.AgentSession.Create("composer");
        session.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, marker));
        await session.SaveAsync(path, Ct);
    }

    /// <summary>
    /// Renders the REAL <see cref="ComposerChat"/> page over the PRODUCTION circuit seam for one circuit identity,
    /// and returns the markup plus the handles the rows need (the page, its scope, its renderer).
    /// </summary>
    private async Task<Rendered> RenderAsync(Host host, string gitHubId)
    {
        var state = new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies")));

        var scope = host.Provider.CreateScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);
        ComposerChat? page = null;
        Func<string>? rootHtml = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = Task.FromResult(state),
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<ComposerChat>(0);
                    builder.AddComponentReferenceCapture(1, captured => page = (ComposerChat)captured);
                    builder.CloseComponent();
                }),
            });

            // THE RETAINED ROOT: kept for the page's whole life, so every later markup read is the ORIGINAL page's
            // own output (a freshly created page refusing would prove nothing about the callback's page).
            var root = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            rootHtml = root.ToHtmlString;
            return 0;
        });

        Assert.NotNull(page);
        return new Rendered(page!, scope, renderer, rootHtml!);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real Composer over an isolated state directory.</summary>
    private Composer NewComposer()
    {
        var stateDir = Path.Combine(_dir, $"composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        return new Composer(
            Model,
            NullLogger<Composer>.Instance,
            new GoalStore(_db, NullLogger<GoalStore>.Instance),
            stateDir: stateDir,
            availableModels: [Model],
            chatClientFactory: _ => new Mock<IChatClient>().Object);
    }

    /// <summary>A real attachment store under this fixture's isolated directory.</summary>
    private ComposerAttachmentService NewAttachments() =>
        new(_dir, NullLogger<ComposerAttachmentService>.Instance);

    /// <summary>Builds a host over the PRODUCTION registration seam.</summary>
    private Host NewHost()
    {
        var stateDir = Path.Combine(_dir, $"host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<HiveConfigFile>(new HiveConfigFile
        {
            Orchestrator = OrchestratorConfig.CreateEmptyModelFallback(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        });
        services.AddSingleton<IGoalStore>(new GoalStore(_db, NullLogger<GoalStore>.Instance));
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(_auth.Directory.Service);
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddScoped(sp => new CircuitUserPrincipalAccessor(
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));
        services.AddSingleton(sp => new ComposerFactory(
            sp, stateDir, ollamaApiKey: null,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            isTokenAvailable: () => true,
            providerOf: _ => "ollama-local"));
        services.AddSingleton(sp => sp.GetRequiredService<ComposerFactory>().CreateLegacy());
        services.AddSingleton(sp => new ComposerRuntimeManager(
            sp.GetRequiredService<ComposerFactory>(),
            sp.GetRequiredService<UserService>(),
            NullLogger<ComposerRuntimeManager>.Instance));
        services.AddSingleton(new ComposerAttachmentService(stateDir, NullLogger<ComposerAttachmentService>.Instance));
        services.AddSingleton<IConfigFacade>(sp => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance,
            sp.GetRequiredService<HivePrincipalResolver>(),
            sp.GetRequiredService<HivePolicy>()));

        // THE PRODUCTION SEAM.
        services.AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.PerUserRuntime);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        return new Host(provider, provider.GetRequiredService<ComposerRuntimeManager>(), stateDir);
    }

    /// <summary>The Composer the page is currently bound to, or <c>null</c>.</summary>
    private static Composer? BoundComposerOf(ComposerChat page)
    {
        var binding = typeof(ComposerChat)
            .GetField("_circuit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);
        return (Composer?)binding?.GetType().GetProperty("Composer")!.GetValue(binding);
    }

    /// <summary>The circuit status the page currently holds.</summary>
    private static ComposerChat.ComposerCircuitStatus BoundStatusOf(ComposerChat page)
    {
        var binding = typeof(ComposerChat)
            .GetField("_circuit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);
        Assert.NotNull(binding);
        return (ComposerChat.ComposerCircuitStatus)binding!.GetType().GetProperty("Status")!.GetValue(binding)!;
    }

    /// <summary>The Composer the component is CURRENTLY SUBSCRIBED to (its own bookkeeping field), or <c>null</c>.</summary>
    private static Composer? SubscribedComposerOf(ComposerChat page) =>
        (Composer?)typeof(ComposerChat)
            .GetField("_subscribedComposer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(page);

    /// <summary>Extracts the brace-bounded source of a method by its signature prefix.</summary>
    private static string ExtractMethodSource(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{signature}' was not found.");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Opening brace for '{signature}' was not found.");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(start, i - start + 1);
            }
        }

        Assert.Fail($"Could not find the matching closing brace for '{signature}'.");
        return string.Empty;
    }

    /// <summary>
    /// THE RUNNER-INVOKED CLEANUP: xUnit v3 calls <c>IAsyncLifetime.DisposeAsync</c> (never a synchronous
    /// <c>Dispose</c>), so every cleanup step lives here.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _db.Dispose();
        _auth.Dispose();
        try
        {
            if (Directory.Exists(_dir))
                TestHelpers.ForceDeleteDirectory(_dir);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated state directory.
        }
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>
    /// ONE RENDERED CIRCUIT: the page instance, the scope it lives in and the renderer that dispatches its work.
    /// <see cref="QueuedCallbacksAsync"/> awaits the page's own recorded callback task, which is how a row waits
    /// for queued work to FINISH without polling or sleeping.
    /// </summary>
    private sealed class Rendered(
        ComposerChat page,
        IServiceScope scope,
        HtmlRenderer renderer,
        Func<string> rootHtml) : IAsyncDisposable
    {
        public ComposerChat Page { get; } = page;

        /// <summary>
        /// THE MARKUP AS IT STANDS NOW, read from the RETAINED original root — the component's own latest render,
        /// never a freshly created page.
        /// </summary>
        public string Render() => renderer.Dispatcher.InvokeAsync(rootHtml).GetAwaiter().GetResult();

        /// <summary>The current markup.</summary>
        public string Html => Render();

        /// <summary>Waits for the page's most recently queued callback body to finish.</summary>
        public Task QueuedCallbacksAsync() => renderer.Dispatcher.InvokeAsync(() => Page.LastCallbackCompletion);

        /// <summary>
        /// THE DETERMINISTIC STALE WINDOW: installs the page's own <c>CallbackGate</c> so every queued subscription
        /// body stays genuinely PENDING until the returned gate is released. No polling, no timer and no
        /// dispatcher trick is involved — the body is suspended inside the gate, which is exactly the window a
        /// demotion or a runtime replacement must be safe against.
        /// </summary>
        internal CallbackHold HoldCallbacks()
        {
            var gate = new CallbackHold();
            Page.CallbackGate = gate.InvokeAsync;
            return gate;
        }

        /// <summary>
        /// Invokes ONE production subscription handler by name, OFF the dispatcher, exactly as the Composer's own
        /// event would. Because the handler queues its body through <c>InvokeAsync</c>, the body stays pending
        /// while the dispatcher is held.
        /// </summary>
        internal void Raise(string handlerName)
        {
            var method = typeof(ComposerChat).GetMethod(
                handlerName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);

            var parameters = method!.GetParameters().Length == 0
                ? null
                : new object?[] { handlerName == "HandleActiveInjection" ? "injected" : null };

            // A parameter whose type is a reference/value type the handler expects: only HandleActiveInjection
            // (string) and HandleSubAgentChanged (SubAgentInfo) take one, and only the former is raised here.
            method.Invoke(Page, parameters);
        }

        /// <summary>
        /// A recording <see cref="ComposerChat.IPasteHost"/> whose COMPLETION GUARD is answered by the page's own
        /// production freshness seam, so the row exercises the real re-authorization rather than a flag.
        /// </summary>
        public RecordingCompletionPasteHost NewPasteHost() => new(Page);

        /// <summary>Invokes the page's own deferred-banner connect control through its dispatcher.</summary>
        public async Task ConnectRuntimeAsync()
        {
            var method = typeof(ComposerChat).GetMethod(
                "ConnectRuntimeAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);

            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var task = (Task?)method!.Invoke(Page, null);
                Assert.NotNull(task);
                await task!;
            });
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>
    /// A HELD CALLBACK GATE: bodies handed to it PEND until <see cref="Release"/> is called, so a row can change
    /// the directory or replace the runtime while the queued work is genuinely still in flight.
    /// </summary>
    private sealed class CallbackHold : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many bodies have been handed to the gate.</summary>
        internal int Invocations { get; private set; }

        /// <summary>The gate itself: suspends the body until <see cref="Release"/>.</summary>
        internal async Task InvokeAsync(Func<Task> body)
        {
            Invocations++;
            await _release.Task;
            await body();
        }

        /// <summary>Releases every held body.</summary>
        internal void Release() => _release.TrySetResult();

        /// <summary>
        /// ALWAYS RELEASES, even when the row throws: a held body would otherwise hang the renderer's teardown
        /// (and the row would report a timeout instead of its real assertion failure).
        /// </summary>
        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A paste host that RECORDS the completion guard's answer and delegates it to the page's production
    /// freshness seam — so the row observes the real re-authorization decision.
    /// </summary>
    private sealed class RecordingCompletionPasteHost : ComposerChat.IPasteHost
    {
        private readonly ComposerChat _owner;
        private ComposerChat.ComposerCircuitBinding? _captured;

        internal RecordingCompletionPasteHost(ComposerChat owner) => _owner = owner;

        /// <inheritdoc />
        public ComposerAttachment? Pending { get; set; }

        /// <inheritdoc />
        public bool Uploading { get; set; }

        /// <inheritdoc />
        public string? AttachmentError { get; set; }

        /// <inheritdoc />
        public bool IsStreaming => false;

        /// <summary>How many times the completion guard was asked.</summary>
        internal int CompletionGuardChecks { get; private set; }

        /// <summary>The guard's most recent answer.</summary>
        internal bool CompletionApproved { get; private set; }

        /// <inheritdoc />
        public Task<AttachmentSaveResult> SaveAsync(string originalName, Stream content)
        {
            // The token is captured when the write STARTS — exactly like the production host.
            _captured = _owner.CurrentCallbackBinding;

            return Task.FromResult(new AttachmentSaveResult(
                Success: true,
                Attachment: new ComposerAttachment(
                    Id: Guid.NewGuid().ToString("N"),
                    DisplayName: originalName,
                    SavedRelativePath: "saved.png",
                    State: AttachmentState.Pending),
                Error: null));
        }

        /// <inheritdoc />
        public void Notify()
        {
        }

        /// <inheritdoc />
        public Task SyncPasteAllowedAsync() => Task.CompletedTask;

        /// <inheritdoc />
        public Task<bool> IsCompletionStillAuthorizedAsync(CancellationToken ct)
        {
            CompletionGuardChecks++;

            // THE PRODUCTION DECISION: the page's own freshness seam (binding identity + fresh authorization).
            var authorized = _owner
                .IsCompletionStillAuthorizedAsync(_captured, ct)
                .GetAwaiter()
                .GetResult();
            CompletionApproved = authorized;
            return Task.FromResult(authorized);
        }
    }

    /// <summary>The provider, the manager and the state directory of one host.</summary>
    private sealed class Host(
        ServiceProvider provider,
        ComposerRuntimeManager manager,
        string stateDir) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ComposerRuntimeManager Manager { get; } = manager;

        public string StateDir { get; } = stateDir;

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();

            async Task Step(Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add(ex); }
            }

            // ORDER IS THE CONTRACT: the manager's actual teardown is AWAITED before the provider goes away.
            await Step(() => Manager.ShutdownAsync());
            await Step(() => Provider.DisposeAsync().AsTask());

            if (failures.Count > 0)
                throw new AggregateException("Callback freshness host cleanup failed", failures);
        }
    }
}
