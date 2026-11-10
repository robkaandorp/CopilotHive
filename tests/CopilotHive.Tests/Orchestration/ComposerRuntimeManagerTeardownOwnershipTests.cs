using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerRuntimeManagerTests
{
    private sealed record LifecycleLogEntry(LogLevel Level, string Message, Exception? Exception,
        KeyValuePair<string, object?>[] Properties);

    // Records the actual manager ILogger sink, including exception identity and structured user id.
    // No hooks throw or alter scheduling; snapshots are read after the lifecycle producer is joined.
    private sealed class LifecycleWarningLogger : ILogger<ComposerRuntimeManager>
    {
        private readonly ConcurrentQueue<LifecycleLogEntry> _entries = new();
        public LifecycleLogEntry[] Entries => _entries.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToArray() : [];
            _entries.Enqueue(new(logLevel, formatter(state, exception), exception, properties));
        }
    }

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
        var warningLog = new LifecycleWarningLogger();
        var manager = NewManager(BuildProvider(), warningLog);
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
            // Removed closes this entry's admission but retains ownership. NoRuntime here would
            // mean early eviction. Shutdown's established ShuttingDown admission is expected.
            Assert.Equal(shutdown ? ComposerRuntimeStatus.Refused : ComposerRuntimeStatus.Removed, entryProbe.Status);
            Assert.Equal(shutdown ? ComposerRuntimeRefusal.ShuttingDown : ComposerRuntimeRefusal.None, entryProbe.Refusal);
            Assert.Null(entryProbe.Runtime);
            Assert.Null(entryProbe.Failure);
            release.TrySetResult();
            await GuardAsync(lifecycle, "lifecycle completes only after owned operation and runtime cleanup");
            Assert.True(operation.IsCompleted);
            Assert.True(connectExited.Task.IsCompleted);
            Assert.True(composer!.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(ActorCompleted(composer));
            var creatorResult = await Bounded(creator, "callback-fault lifecycle creator result");
            Assert.Equal(ComposerRuntimeStatus.Removed, creatorResult.Status);
            Assert.Null(creatorResult.Runtime);
            Assert.Null(creatorResult.Failure);
            Assert.Equal(ComposerRuntimeRefusal.None, creatorResult.Refusal);

            // The public lifecycle task succeeds: its Exception property in the original failure
            // diagnostic is NOT the surfaced channel. The actual Warning must carry this fault.
            var warning = Assert.Single(warningLog.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal($"A cancellation callback of the shared operation of user {second.UserId} threw; teardown continues", warning.Message);
            var aggregate = Assert.IsType<AggregateException>(warning.Exception);
            Assert.Same(callbackFault, Assert.Single(aggregate.InnerExceptions));
            var userId = Assert.Single(warning.Properties, property => property.Key == "UserId");
            Assert.Equal(second.UserId, Assert.IsType<int>(userId.Value));
            Assert.DoesNotContain(warningLog.Entries, entry => entry.Level >= LogLevel.Error);
            var afterTeardown = await Bounded(Retry(manager, second), "entry admission after actual callback-fault teardown");
            Assert.Equal(ComposerRuntimeStatus.Refused, afterTeardown.Status);
            Assert.Equal(shutdown ? ComposerRuntimeRefusal.ShuttingDown : ComposerRuntimeRefusal.NoRuntime, afterTeardown.Refusal);
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
                // A regressed lifecycle can claim success and abandon the now-settled runtime.
                // Only after that lifecycle and the request have been joined may this fallback
                // take ownership; it never races a manager that is still performing teardown.
                if (composer is not null && lifecycle?.IsCompletedSuccessfully == true && !composer.TeardownCompletion.IsCompleted)
                    yield return async () =>
                    {
                        await GuardAsync(composer.DisposeAsync().AsTask(), "dispose runtime abandoned by completed lifecycle");
                        Field<ComposerEventSubscriber?>(composer, "_eventSubscriber")?.Dispose();
                        Field<LlmSessionRegistry?>(composer, "_sessionRegistry")?.Unregister(
                            ComposerUserState.RegistryId(Field<ComposerUserState>(composer, "_userState")));
                    };
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
        var (second, first) = await OriginalAsSecondAsync();
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
                Assert.Equal([$"composer:{first.UserId}"], provider.GetRequiredService<LlmSessionRegistry>().GetAll().Select(row => row.SessionId));
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
