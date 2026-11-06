using System.Runtime.ExceptionServices;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OperationObservation_UnsetOrThrowingHook_DoesNotChangeRemovalOrShutdown(bool shutdown, bool throwingHook)
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        await using var gate = Park();
        var observed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (throwingHook)
        {
            manager.OnJoiningOperation = (userId, operation) =>
            {
                Assert.Equal(second.UserId, userId);
                observed.TrySetResult(operation);
                throw new InvalidOperationException("deliberate observation-only hook failure");
            };
        }
        else Assert.Null(manager.OnJoiningOperation); // control: the production default is inert
        var creator = Request(manager, second);
        await GuardAsync(gate.Entered.Task, "normal lifecycle control entered shared connect");
        Task lifecycle = shutdown ? manager.ShutdownAsync() : Remove(manager, second.UserId);
        try
        {
            await GuardAsync(gate.CancelObserved.Task, "normal lifecycle control cancelled only its owned operation");
            if (throwingHook)
            {
                var operation = await Bounded(observed.Task, "throwing observation hook saw the exact operation join");
                Assert.False(operation.IsCompleted);
            }
            Assert.False(creator.IsCompleted);
            Assert.False(lifecycle.IsCompleted);
            gate.Release();
            await GuardAsync(lifecycle, "unset/throwing observation does not prevent actual teardown");
            Assert.Equal(ComposerRuntimeStatus.Removed, (await Bounded(creator, "normal lifecycle creator settled")).Status);
            Assert.True(gate.Composer0.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(ActorCompleted(gate.Composer0));
            if (throwingHook) Assert.True((await observed.Task).IsCompletedSuccessfully);
            Assert.Equal(1, gate.Calls);
        }
        finally
        {
            gate.Release();
            await GuardAsync(lifecycle, "observation-control cleanup joins lifecycle");
            await JoinRequestAsync(creator);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_WhenCancellationCallbackThrows_StillJoinsLiveOperationBeforeCompleting(bool shutdown)
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joiningOperation = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFault = new InvalidOperationException("deliberate entry-token cancellation callback fault");
        Composer? composer = null;
        manager.OnJoiningOperation = (userId, operation) =>
        {
            Assert.Equal(second.UserId, userId);
            joiningOperation.TrySetResult(operation);
        };
        _connectHook = async (value, token) =>
        {
            composer = value;
            using var registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                throw callbackFault;
            });
            entered.TrySetResult();
            try
            {
                await GuardAsync(release.Task, "release live operation after throwing cancellation callback");
                token.ThrowIfCancellationRequested();
                await value.ConnectAsync(token);
            }
            finally { connectExited.TrySetResult(); }
        };
        var creator = Request(manager, second);
        Task? lifecycle = null;
        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            await GuardAsync(entered.Task, "shared connect is genuinely in flight before cancellation callback fault");
            Assert.False(creator.IsCompleted);
            Assert.False(connectExited.Task.IsCompleted);
            lifecycle = shutdown ? manager.ShutdownAsync() : Remove(manager, second.UserId);
            await GuardAsync(callbackEntered.Task, "entry cancellation dispatched the deliberately throwing callback");
            await GuardAsync(Task.WhenAny(joiningOperation.Task, lifecycle), "lifecycle joins live operation or incorrectly completes");
            // Correct teardown reaches the exact-operation join; the defective outer catch
            // instead swallows the callback fault, evicts the entry, and completes successfully.
            var entryProbe = await Bounded(Retry(manager, second), "inspect entry ownership after cancellation callback fault");
            Assert.False(lifecycle.IsCompleted,
                $"Lifecycle completed before its live shared operation: shutdown={shutdown}, lifecycleStatus={lifecycle.Status}, " +
                $"callbackFaultSurfaced={lifecycle.Exception is not null}, connectExited={connectExited.Task.IsCompleted}, " +
                $"creatorStatus={creator.Status}, entryProbe={entryProbe.Status}/{entryProbe.Refusal}");
            var operation = await Bounded(joiningOperation.Task, "lifecycle retained ownership after cancellation callback fault");
            Assert.False(operation.IsCompleted);
            Assert.False(connectExited.Task.IsCompleted);
            release.TrySetResult();
            await GuardAsync(lifecycle, "lifecycle completes only after owned operation and runtime cleanup");
            Assert.True(operation.IsCompleted);
            Assert.True(connectExited.Task.IsCompleted);
            Assert.True(composer!.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(ActorCompleted(composer));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            release.TrySetResult();
            try
            {
                await CompleteCleanupAsync(CleanupSteps());
            }
            catch (Exception ex) { cleanupFailure = ex; }
            IEnumerable<Func<Task>> CleanupSteps()
            {
                if (lifecycle is not null) yield return () => GuardAsync(lifecycle, "callback-fault lifecycle cleanup");
                yield return () => JoinRequestAsync(creator);
                if (composer is not null)
                    yield return () => GuardAsync(composer.TeardownCompletion, "orphan-operation actual teardown cleanup");
                yield return () => GuardAsync(manager.ShutdownAsync(), "callback-fault manager cleanup");
            }
        }
        if (primary is not null)
        {
            if (cleanupFailure is not null) primary.SourceException.Data["CleanupFailure"] = cleanupFailure;
            primary.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lifecycle_DeferredRuntime_PreservesUploadedAttachmentsAndSessions(bool shutdown, bool connecting)
    {
        var (first, second) = await TwoUsersAsync();
        var provider = BuildProvider();
        var manager = NewManager(provider);
        var other = (await Request(manager, first)).Runtime!;
        using var otherContent = new MemoryStream([9, 7, 5, 3]);
        var otherSaved = await other.Attachments.SaveAsync("unrelated-retained.png", otherContent, Ct);
        Assert.True(otherSaved.Success, otherSaved.Error);
        var otherFile = Path.Combine(other.Attachments.AttachmentsRootPath, otherSaved.Attachment!.SavedRelativePath);
        await other.Composer.SaveSessionAsync(Ct);
        var otherSession = ComposerUserState.SessionFilePath(StateDir, other.State);
        var otherSessionBefore = await File.ReadAllBytesAsync(otherSession, Ct);
        _provider = "copilot";
        _tokenAvailable = false;
        var deferred = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Deferred, deferred.Status);
        var runtime = deferred.Runtime!;
        Assert.NotSame(other.Attachments, runtime.Attachments);
        Assert.Equal(ComposerUserState.AttachmentsRootPath(StateDir, runtime.State), runtime.Attachments.AttachmentsRootPath);
        using var content = new MemoryStream([2, 4, 6, 8]);
        var saved = await runtime.Attachments.SaveAsync("retained-on-lifecycle.png", content, Ct);
        Assert.True(saved.Success, saved.Error);
        var file = Path.Combine(runtime.Attachments.AttachmentsRootPath, saved.Attachment!.SavedRelativePath);
        var session = ComposerUserState.SessionFilePath(StateDir, runtime.State);
        await WriteSessionAsync(session, "retained-deferred-user-history-marker");
        var sessionBefore = await File.ReadAllBytesAsync(session, Ct);
        Assert.Equal(new byte[] { 2, 4, 6, 8 }, await File.ReadAllBytesAsync(file, Ct));
        Assert.Equal(new byte[] { 9, 7, 5, 3 }, await File.ReadAllBytesAsync(otherFile, Ct));
        await using var gate = Park();
        Task<ComposerRuntimeResult>? connect = null;
        Task? lifecycle = null;
        try
        {
            if (connecting)
            {
                _tokenAvailable = true;
                connect = Retry(manager, second);
                await GuardAsync(gate.Entered.Task, "deferred connect in flight with existing uploaded user content");
                Assert.False(connect.IsCompleted);
                Assert.Same(runtime.Composer, gate.Composer0);
            }
            lifecycle = shutdown ? manager.ShutdownAsync() : Remove(manager, second.UserId);
            if (connecting)
            {
                await GuardAsync(gate.CancelObserved.Task, "lifecycle cancelled the deferred connection it owns");
                Assert.False(lifecycle.IsCompleted);
                gate.Release();
            }
            await GuardAsync(lifecycle, "deferred-runtime lifecycle completed actual cleanup");
            if (connect is not null)
            {
                var result = await Bounded(connect, "lifecycle-cancelled deferred connector settled");
                Assert.Equal(ComposerRuntimeStatus.Removed, result.Status);
                Assert.Null(result.Runtime);
            }
            Assert.True(runtime.Composer.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(ActorCompleted(runtime.Composer));
            Assert.Equal(sessionBefore, await File.ReadAllBytesAsync(session, Ct));
            Assert.Equal(otherSessionBefore, await File.ReadAllBytesAsync(otherSession, Ct));
            Assert.Equal(new byte[] { 9, 7, 5, 3 }, await File.ReadAllBytesAsync(otherFile, Ct));
            Assert.True(other.Attachments.MarkAsSent(otherSaved.Attachment).Success);
            if (!shutdown)
            {
                Assert.Same(other, (await Request(manager, first)).Runtime);
                Assert.True(other.Composer.IsConnected);
                Assert.Equal(["composer:1"], provider.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
            }
            else Assert.True(other.Composer.TeardownCompletion.IsCompletedSuccessfully);
            var fileSurvived = File.Exists(file);
            var recordSurvived = runtime.Attachments.MarkAsSent(saved.Attachment).Success;
            Assert.True(fileSurvived && recordSurvived,
                $"Manager lifecycle must preserve existing deferred user content: shutdown={shutdown}, connecting={connecting}, " +
                $"fileSurvived={fileSurvived}, trackedRecordSurvived={recordSurvived}");
            Assert.Equal(new byte[] { 2, 4, 6, 8 }, await File.ReadAllBytesAsync(file, Ct));
        }
        finally
        {
            gate.Release();
            if (lifecycle is not null) await GuardAsync(lifecycle, "deferred-content lifecycle cleanup");
            if (connect is not null) await JoinRequestAsync(connect);
            await GuardAsync(manager.ShutdownAsync(), "deferred-content manager cleanup");
        }
    }
}
