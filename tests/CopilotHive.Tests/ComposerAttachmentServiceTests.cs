using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for <see cref="ComposerAttachmentService"/> covering the frozen public API:
/// allowlisting, stream-bounded saving, containment of stored paths, the caller-input-trust
/// rule, atomic replace with rollback, sent-state transitions and the serialized clear-all.
/// All simulation goes through the internal <c>IAttachmentFileSystem</c> seam so the tests are
/// portable and never write 20 MiB to disk.
/// </summary>
public sealed class ComposerAttachmentServiceTests
{
    private const string StateDir = "/tmp/copilothive-attachment-tests";

    /// <summary>A stored path in the generated <c>&lt;guid&gt;.&lt;lowercase-ext&gt;</c> form.</summary>
    private const string SeedPath = "a1b2c3d4-e5f6-7890-abcd-ef1234567890.png";

    /// <summary>A second, distinct stored path in the generated form.</summary>
    private const string OtherSeedPath = "b2c3d4e5-f6a7-8901-bcde-f12345678901.png";

    /// <summary>The service's 20 MiB cap, mirrored locally for readable assertions.</summary>
    private const long MaxBytes = 20_971_520;

    private static (ComposerAttachmentService Service, FakeAttachmentFileSystem Fs, CapturingLogger<ComposerAttachmentService> Logger)
        CreateService(IReadOnlyDictionary<string, ComposerAttachment>? seed = null)
    {
        var fs = new FakeAttachmentFileSystem();
        var logger = new CapturingLogger<ComposerAttachmentService>();
        var service = new ComposerAttachmentService(
            Path.Combine(StateDir, Guid.NewGuid().ToString("N")), logger, fs, seed);
        return (service, fs, logger);
    }

    private static MemoryStream Content(int bytes = 16) => new(new byte[bytes]);

    /// <summary>Probes whether a record is still retained without deleting anything.</summary>
    private static bool IsRetained(ComposerAttachmentService service, string id) =>
        service.MarkAsSent(new ComposerAttachment(id, "probe.png", "probe.png", AttachmentState.Pending)).Success;

    /// <summary>
    /// The attachment id the service generated for the single file it asked the fake to open —
    /// the stem of <c>&lt;guid&gt;.&lt;ext&gt;</c>. Lets a test assert directly on the retained
    /// records rather than inferring absence from a later operation's side effects.
    /// </summary>
    private static string GeneratedIdFromOpenedPath(FakeAttachmentFileSystem fs)
    {
        var opened = Assert.Single(fs.OpenedPaths);
        return Path.GetFileNameWithoutExtension(opened);
    }

    // ── SaveAsync: allowlist ────────────────────────────────────────────────

    [Theory]
    [InlineData("photo.png")]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("photo.gif")]
    [InlineData("photo.webp")]
    [InlineData("report.pdf")]
    [InlineData("SHOUTING.PNG")]
    public async Task SaveAsync_AllowlistedExtension_Succeeds(string name)
    {
        var (service, fs, _) = CreateService();

        var result = await service.SaveAsync(name, Content(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.NotNull(result.Attachment);
        Assert.Equal(AttachmentState.Pending, result.Attachment.State);
        Assert.Contains(
            Path.Combine(service.AttachmentsRootPath, result.Attachment.SavedRelativePath),
            fs.Files.Keys);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("payload.exe")]
    [InlineData("script.js")]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    public async Task SaveAsync_NonAllowlistedExtension_Rejected(string name)
    {
        var (service, fs, _) = CreateService();

        var result = await service.SaveAsync(name, Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.NotNull(result.Error);
        Assert.Empty(fs.Files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.png")]
    [InlineData("dir/escape.png")]
    [InlineData("dir\\escape.png")]
    [InlineData("bell\u0007.png")]
    [InlineData("newline\n.png")]
    public async Task SaveAsync_DangerousName_RejectedAndNoFileCreated(string name)
    {
        var (service, fs, _) = CreateService();

        var result = await service.SaveAsync(name, Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.NotNull(result.Error);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task SaveAsync_Success_KeepsDisplayNameUnchangedAndUsesGuidFileName()
    {
        var (service, _, _) = CreateService();

        var result = await service.SaveAsync(
            "My Vacation Photo.PNG", Content(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var attachment = Assert.IsType<ComposerAttachment>(result.Attachment);
        Assert.Equal("My Vacation Photo.PNG", attachment.DisplayName);
        Assert.Equal($"{attachment.Id}.png", attachment.SavedRelativePath);
        Assert.True(Guid.TryParseExact(attachment.Id, "N", out _));
    }

    [Fact]
    public async Task SaveAsync_SameOriginalNameTwice_ProducesDistinctStoredFileNames()
    {
        var (service, fs, _) = CreateService();

        var first = await service.SaveAsync("same.png", Content(), TestContext.Current.CancellationToken);
        var second = await service.SaveAsync("same.png", Content(), TestContext.Current.CancellationToken);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.Attachment!.Id, second.Attachment!.Id);
        Assert.NotEqual(first.Attachment.SavedRelativePath, second.Attachment.SavedRelativePath);
        Assert.Equal(2, fs.Files.Count);
    }

    // ── SaveAsync: oversize (exercises the PRODUCTION bounded-copy algorithm) ─

    [Fact]
    public async Task SaveAsync_ContentExceedsLimitByOneByte_DeletesPartialAndReportsOversize()
    {
        var (service, fs, _) = CreateService();
        // Exactly one byte over the cap — produced lazily, never materialised in one buffer.
        var source = new SyntheticStream(MaxBytes + 1);

        var result = await service.SaveAsync("huge.png", source, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("attachment exceeds 20 MiB", result.Error);

        // Partial file deleted → nothing retained on disk, and no record committed.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);

        // The invariant: never more than the cap was WRITTEN, and the source was never
        // consumed a whole chunk past the cap (at most a single 1-byte probe).
        Assert.True(fs.MaxBytesWritten <= MaxBytes, $"wrote {fs.MaxBytesWritten} bytes");
        Assert.True(
            source.TotalBytesRead <= MaxBytes + 1,
            $"read {source.TotalBytesRead} bytes, cap is {MaxBytes}");

        // No full-buffer read: chunked reads only.
        Assert.True(source.MaxRequestedCount <= 81920, $"chunk was {source.MaxRequestedCount}");
    }

    [Fact]
    public async Task SaveAsync_ContentWellOverLimit_StopsWithoutConsumingWholeStream()
    {
        var (service, fs, _) = CreateService();
        // 21 MiB — the copy must stop at the cap rather than draining the source.
        var source = new SyntheticStream(21 * 1024 * 1024);

        var result = await service.SaveAsync("huge.png", source, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("attachment exceeds 20 MiB", result.Error);
        Assert.Empty(fs.Files);

        Assert.True(fs.MaxBytesWritten <= MaxBytes, $"wrote {fs.MaxBytesWritten} bytes");
        Assert.True(
            source.TotalBytesRead <= MaxBytes + 1,
            $"read {source.TotalBytesRead} bytes, cap is {MaxBytes}");
    }

    [Fact]
    public async Task SaveAsync_ContentExactlyAtLimit_Succeeds()
    {
        var (service, fs, _) = CreateService();
        var source = new SyntheticStream(MaxBytes);

        var result = await service.SaveAsync("exact.png", source, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Attachment);
        Assert.Null(result.Error);

        // The full payload was written, and not a byte more.
        var savedPath = Path.Combine(service.AttachmentsRootPath, result.Attachment.SavedRelativePath);
        Assert.Equal(MaxBytes, fs.Files[savedPath]);
        Assert.Equal(MaxBytes, source.TotalBytesRead);
    }

    // ── SaveAsync: flush/close failures must not commit a record ─────────────

    [Fact]
    public async Task SaveAsync_FlushFails_ReturnsFailureDeletesPartialAndCommitsNoRecord()
    {
        var (service, fs, _) = CreateService();
        // A late disk-full surfacing while draining buffered bytes.
        fs.FailFlush = () => new IOException("no space left on device");

        var result = await service.SaveAsync("x.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("no space left on device", result.Error);

        // Partial deleted.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);

        // NO Pending record: asserted DIRECTLY against the generated id the fs was asked to open,
        // rather than inferred from a later operation's side effects.
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    [Fact]
    public async Task SaveAsync_CloseFails_ReturnsFailureDeletesPartialAndCommitsNoRecord()
    {
        var (service, fs, _) = CreateService();
        // The copy and the explicit flush both succeed; only the CLOSE fails — a stream that
        // still had unpersisted bytes. This must not be swallowed into a successful save.
        fs.FailDispose = () => new IOException("close failed: input/output error");

        var result = await service.SaveAsync("x.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("close failed: input/output error", result.Error);

        // Partial deleted.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);

        // NO Pending record was committed for the truncated file.
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    [Fact]
    public async Task SaveAsync_CopyFailsAndCloseAlsoFails_PreservesCopyErrorAsPrimaryOutcome()
    {
        var (service, fs, _) = CreateService();
        // A GENUINE copy-stage fault: the source faults mid-read, so CopyBoundedAsync throws
        // before any flush is ever attempted. FailFlush is deliberately NOT configured.
        var source = new ThrowingSourceStream(new IOException("copy failed: source read error"));
        fs.FailDispose = () => new IOException("close failed: input/output error");

        var result = await service.SaveAsync("x.png", source, TestContext.Current.CancellationToken);

        // The COPY error wins — not the close error, and not a flush error.
        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("copy failed: source read error", result.Error);

        // The close was genuinely attempted AND genuinely failed on this already-failed path.
        Assert.True(fs.DisposeAttemptCount >= 1);
        Assert.True(fs.DisposeThrew);

        // Partial cleaned up, and no Pending record committed.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    [Fact]
    public async Task SaveAsync_DestinationWriteFailsAndCloseAlsoFails_PreservesWriteErrorAsPrimaryOutcome()
    {
        var (service, fs, _) = CreateService();
        // The other genuine copy-stage fault: the DESTINATION faults mid-write.
        fs.FailWrite = () => new IOException("copy failed: destination write error");
        fs.FailDispose = () => new IOException("close failed: input/output error");

        var result = await service.SaveAsync("x.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("copy failed: destination write error", result.Error);

        Assert.True(fs.DisposeAttemptCount >= 1);
        Assert.True(fs.DisposeThrew);

        Assert.Empty(fs.Files);
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    [Fact]
    public async Task SaveAsync_FlushFailsAndCloseAlsoFails_PreservesFlushErrorAsPrimaryOutcome()
    {
        var (service, fs, _) = CreateService();
        fs.FailFlush = () => new IOException("no space left on device");
        fs.FailDispose = () => new IOException("close failed: input/output error");

        var result = await service.SaveAsync("x.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("no space left on device", result.Error);

        Assert.True(fs.DisposeAttemptCount >= 1);
        Assert.True(fs.DisposeThrew);

        Assert.Empty(fs.Files);
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    [Fact]
    public async Task SaveAsync_OversizeAndCloseAlsoFails_PreservesOversizeAsPrimaryOutcome()
    {
        var (service, fs, _) = CreateService();
        fs.FailDispose = () => new IOException("close failed: input/output error");
        var source = new SyntheticStream(MaxBytes + 1);

        var result = await service.SaveAsync("huge.png", source, TestContext.Current.CancellationToken);

        // Oversize is the earlier (copy-stage) outcome and wins over the close fault.
        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("attachment exceeds 20 MiB", result.Error);

        // The close was genuinely attempted AND genuinely failed.
        Assert.True(fs.DisposeAttemptCount >= 1);
        Assert.True(fs.DisposeThrew);

        // Partial cleaned up, and no Pending record committed.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    [Fact]
    public async Task SaveAsync_CancelledAndCloseAlsoFails_PreservesCancellationAsPrimaryOutcome()
    {
        var (service, fs, _) = CreateService();
        fs.FailDispose = () => new IOException("close failed: input/output error");
        using var cts = new CancellationTokenSource();
        var source = new SyntheticStream(4 * 1024 * 1024, onFirstRead: cts.Cancel);

        // The OCE wins: the close fault must not replace it with an IOException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SaveAsync("cancelled.png", source, cts.Token));

        // The close was genuinely attempted AND genuinely failed.
        Assert.True(fs.DisposeAttemptCount >= 1);
        Assert.True(fs.DisposeThrew);

        // Partial cleaned up, and no Pending record committed.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);
        Assert.False(IsRetained(service, GeneratedIdFromOpenedPath(fs)));
    }

    // ── SaveAsync: cancellation + cleanup failure ───────────────────────────

    [Fact]
    public async Task SaveAsync_CancelledDuringCopy_ThrowsDeletesPartialAndReleasesGate()
    {
        var (service, fs, _) = CreateService();
        using var cts = new CancellationTokenSource();
        var source = new SyntheticStream(4 * 1024 * 1024, onFirstRead: cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SaveAsync("cancelled.png", source, cts.Token));

        // Partial file deleted.
        Assert.Empty(fs.Files);
        Assert.Single(fs.DeletedPaths);

        // Gate released in finally → a subsequent operation is not blocked.
        var next = await service.SaveAsync("after.png", Content(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(next.Success);
    }

    [Fact]
    public async Task SaveAsync_CancelledAndPartialDeleteFails_StillThrowsAndLogsStray()
    {
        var (service, fs, logger) = CreateService();
        fs.FailDeleteFor = _ => new IOException("delete denied");
        using var cts = new CancellationTokenSource();
        var source = new SyntheticStream(4 * 1024 * 1024, onFirstRead: cts.Cancel);

        // Primary outcome (the OCE) is preserved despite the secondary cleanup failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SaveAsync("cancelled.png", source, cts.Token));

        Assert.Single(fs.Files); // stray partial left behind
        Assert.Contains(logger.Entries, e => e.Contains("Stray partial attachment file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_WriteFails_ReturnsErrorResultAndNoRecord()
    {
        var (service, fs, _) = CreateService();
        fs.FailOpenWriteFor = _ => new IOException("disk full");

        var result = await service.SaveAsync("x.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("disk full", result.Error);
        Assert.Empty(fs.Files);
    }

    // ── Remove ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_PendingAttachment_DeletesFileAndDropsRecord()
    {
        var (service, fs, _) = CreateService();
        var saved = await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken);
        var attachment = saved.Attachment!;

        var result = service.Remove(attachment);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Empty(fs.Files);
        Assert.False(IsRetained(service, attachment.Id));
    }

    [Fact]
    public void Remove_UnknownId_ReturnsUnknownAttachment()
    {
        var (service, fs, _) = CreateService();

        var result = service.Remove(
            new ComposerAttachment("nope", "a.png", "a.png", AttachmentState.Pending));

        Assert.False(result.Success);
        Assert.Equal("unknown attachment", result.Error);
        Assert.Empty(fs.DeletedPaths);
    }

    [Fact]
    public async Task Remove_SentAttachment_RefusedAndFileKept()
    {
        var (service, fs, _) = CreateService();
        var saved = await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken);
        var attachment = saved.Attachment!;
        Assert.True(service.MarkAsSent(attachment).Success);

        var result = service.Remove(attachment);

        Assert.False(result.Success);
        Assert.Equal("cannot remove a sent attachment", result.Error);
        Assert.Empty(fs.DeletedPaths);
        Assert.Single(fs.Files);
    }

    [Fact]
    public async Task Remove_DeleteThrows_ReturnsErrorAndRetainsRecord()
    {
        var (service, fs, _) = CreateService();
        var saved = await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken);
        var attachment = saved.Attachment!;
        fs.FailDeleteFor = _ => new IOException("delete denied");

        var result = service.Remove(attachment);

        Assert.False(result.Success);
        Assert.Equal("delete denied", result.Error);
        Assert.Single(fs.Files);
        Assert.True(IsRetained(service, attachment.Id));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\config")]
    [InlineData("nested/dir/file.png")]
    [InlineData("/etc/passwd.png")]
    [InlineData("evil.exe")]
    // Not in the generated <guid>.<lowercase-ext> form — rejected even though they look harmless.
    [InlineData("my-file.png")]
    [InlineData("real.png")]
    [InlineData("subdir/file.png")]
    [InlineData(".png")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890.PNG")]
    [InlineData("A1B2C3D4-E5F6-7890-ABCD-EF1234567890.PNG")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890.tar.png")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890.")]
    [InlineData("not-a-guid.png")]
    public void Remove_StoredPathNotInGeneratedForm_RejectedAndNothingDeleted(string storedPath)
    {
        var malicious = new ComposerAttachment("seed-id", "innocent.png", storedPath, AttachmentState.Pending);
        var (service, fs, _) = CreateService(
            new Dictionary<string, ComposerAttachment> { ["seed-id"] = malicious });

        var result = service.Remove(malicious);

        Assert.False(result.Success);
        Assert.Equal("invalid stored path", result.Error);
        Assert.Empty(fs.DeletedPaths);
        Assert.True(IsRetained(service, "seed-id"));
    }

    [Theory]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890.png")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890.jpeg")]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef1234567890.pdf")]
    public void Remove_StoredPathInGeneratedForm_Accepted(string storedPath)
    {
        var stored = new ComposerAttachment("seed-id", "innocent.png", storedPath, AttachmentState.Pending);
        var (service, fs, _) = CreateService(
            new Dictionary<string, ComposerAttachment> { ["seed-id"] = stored });
        fs.SeedFile(Path.Combine(service.AttachmentsRootPath, storedPath), 0);

        var result = service.Remove(stored);

        Assert.True(result.Success);
        Assert.Empty(fs.Files);
        Assert.False(IsRetained(service, "seed-id"));
    }

    [Fact]
    public void Remove_CallerSuppliedPathFabricated_UsesStoredPath()
    {
        var stored = new ComposerAttachment("seed-id", "real.png", SeedPath, AttachmentState.Pending);
        var (service, fs, _) = CreateService(
            new Dictionary<string, ComposerAttachment> { ["seed-id"] = stored });
        var realPath = Path.Combine(service.AttachmentsRootPath, SeedPath);
        var fabricatedPath = Path.Combine(service.AttachmentsRootPath, OtherSeedPath);
        fs.SeedFile(realPath, 0);
        fs.SeedFile(fabricatedPath, 0);

        // Same id, but a different (also well-formed) SavedRelativePath supplied by the caller.
        var result = service.Remove(
            new ComposerAttachment("seed-id", "real.png", OtherSeedPath, AttachmentState.Pending));

        Assert.True(result.Success);
        Assert.Equal([realPath], fs.DeletedPaths);
        Assert.Contains(fabricatedPath, fs.Files.Keys);
    }

    [Fact]
    public void Remove_CallerSuppliedSentStateIgnored_StoredPendingStateWins()
    {
        var stored = new ComposerAttachment("seed-id", "real.png", SeedPath, AttachmentState.Pending);
        var (service, fs, _) = CreateService(
            new Dictionary<string, ComposerAttachment> { ["seed-id"] = stored });
        fs.SeedFile(Path.Combine(service.AttachmentsRootPath, SeedPath), 0);

        var result = service.Remove(stored with { State = AttachmentState.Sent });

        Assert.True(result.Success);
        Assert.Empty(fs.Files);
    }

    // ── ReplaceAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceAsync_HappyPath_SwapsRecordAndFilesAtomically()
    {
        var (service, fs, _) = CreateService();
        var old = (await service.SaveAsync("old.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var oldPath = Path.Combine(service.AttachmentsRootPath, old.SavedRelativePath);

        var result = await service.ReplaceAsync(
            old, "new.pdf", Content(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var replacement = Assert.IsType<ComposerAttachment>(result.Attachment);
        Assert.Equal("new.pdf", replacement.DisplayName);
        Assert.Equal(AttachmentState.Pending, replacement.State);
        Assert.DoesNotContain(oldPath, fs.Files.Keys);
        Assert.Single(fs.Files);
        Assert.Contains(Path.Combine(service.AttachmentsRootPath, replacement.SavedRelativePath), fs.Files.Keys);
        Assert.False(IsRetained(service, old.Id));
        Assert.True(IsRetained(service, replacement.Id));
    }

    [Fact]
    public async Task ReplaceAsync_SaveFails_KeepsOldRecordAndFile()
    {
        var (service, fs, _) = CreateService();
        var old = (await service.SaveAsync("old.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var oldPath = Path.Combine(service.AttachmentsRootPath, old.SavedRelativePath);
        fs.FailOpenWriteFor = _ => new IOException("disk full");

        var result = await service.ReplaceAsync(
            old, "new.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("disk full", result.Error);
        Assert.Contains(oldPath, fs.Files.Keys);
        Assert.True(IsRetained(service, old.Id));
    }

    [Fact]
    public async Task ReplaceAsync_OldDeleteFails_RollsBackNewAndKeepsOld()
    {
        var (service, fs, _) = CreateService();
        var old = (await service.SaveAsync("old.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var oldPath = Path.Combine(service.AttachmentsRootPath, old.SavedRelativePath);
        fs.FailDeleteFor = path => path == oldPath ? new IOException("old locked") : null;

        var result = await service.ReplaceAsync(
            old, "new.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("old locked", result.Error);

        // New file rolled back, old file + record intact.
        Assert.Equal([oldPath], fs.Files.Keys.ToArray());
        Assert.True(IsRetained(service, old.Id));
    }

    [Fact]
    public async Task ReplaceAsync_RollbackAlsoFails_KeepsOldAndLogsStrayNew()
    {
        var (service, fs, logger) = CreateService();
        var old = (await service.SaveAsync("old.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var oldPath = Path.Combine(service.AttachmentsRootPath, old.SavedRelativePath);
        fs.FailDeleteFor = _ => new IOException("everything locked");

        var result = await service.ReplaceAsync(
            old, "new.png", Content(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("everything locked", result.Error);
        Assert.True(IsRetained(service, old.Id));
        Assert.Contains(oldPath, fs.Files.Keys);

        // Leftover new file tolerated, but logged — never silently ignored.
        Assert.Equal(2, fs.Files.Count);
        Assert.Contains(logger.Entries, e => e.Contains("stray attachment file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceAsync_UnknownOld_ReturnsErrorWithoutSaving()
    {
        var (service, fs, _) = CreateService();
        var source = new SyntheticStream(1024);

        var result = await service.ReplaceAsync(
            new ComposerAttachment("nope", "old.png", "old.png", AttachmentState.Pending),
            "new.png", source, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("unknown attachment", result.Error);
        Assert.Empty(fs.Files);
        Assert.Equal(0L, source.TotalBytesRead);
    }

    [Fact]
    public async Task ReplaceAsync_SentOld_ReturnsErrorWithoutSaving()
    {
        var (service, fs, _) = CreateService();
        var old = (await service.SaveAsync("old.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        Assert.True(service.MarkAsSent(old).Success);
        var source = new SyntheticStream(1024);

        var result = await service.ReplaceAsync(
            old, "new.png", source, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("cannot replace a sent attachment", result.Error);
        Assert.Single(fs.Files);
        Assert.Equal(0L, source.TotalBytesRead);
    }

    [Fact]
    public async Task ReplaceAsync_OldPathNotInGeneratedForm_RejectedBeforeAnyNewFileIsSaved()
    {
        // A seeded record whose stored path is not in the generated form must be rejected
        // BEFORE the new file is created, otherwise a replacement would strand both attachments.
        var stored = new ComposerAttachment("seed-id", "old.png", "not-a-guid.png", AttachmentState.Pending);
        var (service, fs, _) = CreateService(
            new Dictionary<string, ComposerAttachment> { ["seed-id"] = stored });
        var source = new SyntheticStream(1024);

        var result = await service.ReplaceAsync(
            stored, "new.png", source, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Equal("invalid stored path", result.Error);

        // No new file was even opened, nothing was read, and the old record is retained.
        Assert.Empty(fs.OpenedPaths);
        Assert.Empty(fs.Files);
        Assert.Equal(0L, source.TotalBytesRead);
        Assert.True(IsRetained(service, "seed-id"));
    }

    [Fact]
    public async Task ReplaceAsync_SameIdConcurrently_SecondBlocksOnGateAndNeverStartsMidFirstSave()
    {
        var (service, fs, _) = CreateService();
        var old = (await service.SaveAsync("old.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var savesBeforeConcurrency = fs.OpenedPaths.Count;

        // The first ReplaceAsync blocks inside OpenWrite while holding the gate.
        var firstSaveReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedOnce = false;
        fs.BeforeOpenWrite = _ =>
        {
            if (blockedOnce)
                return Task.CompletedTask;

            blockedOnce = true;
            firstSaveReached.SetResult();
            return releaseFirstSave.Task;
        };

        Task<AttachmentSaveResult> first;
        Task<AttachmentSaveResult> second;
        try
        {
            first = Task.Run(
                () => service.ReplaceAsync(old, "first.png", Content(), TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

            // Deterministic handshake: the first call now holds the gate and is parked in OpenWrite.
            await firstSaveReached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // The second call is invoked DIRECTLY on this thread — not queued via Task.Run. The
            // call therefore cannot return until its synchronous prefix has run all the way to
            // the gate's WaitAsync, so an incomplete Task here proves the operation STARTED and
            // is BLOCKED on the gate (it cannot be explained by an unscheduled delegate).
            second = service.ReplaceAsync(old, "second.png", Content(), TestContext.Current.CancellationToken);

            Assert.False(second.IsCompleted);

            // It also never began its own save: only the first call's OpenWrite is in flight.
            Assert.Equal(savesBeforeConcurrency + 1, fs.OpenedPaths.Count);
        }
        finally
        {
            // Always release, so an assertion failure above cannot hang the test.
            releaseFirstSave.TrySetResult(true);
        }

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The two calls never overlapped inside the file system.
        Assert.Equal(1, fs.MaxConcurrentOperations);

        // The first replacement succeeded; the second saw the already-swapped record.
        Assert.True(firstResult.Success);
        Assert.False(secondResult.Success);
        Assert.Equal("unknown attachment", secondResult.Error);

        // Exactly one attachment is retained, and its file is the first replacement's.
        Assert.Single(fs.Files);
        Assert.Contains(
            Path.Combine(service.AttachmentsRootPath, firstResult.Attachment!.SavedRelativePath),
            fs.Files.Keys);
        Assert.False(IsRetained(service, old.Id));
    }

    // ── MarkAsSent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsSent_PendingAttachment_TransitionsToSent()
    {
        var (service, _, _) = CreateService();
        var attachment = (await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken)).Attachment!;

        var result = service.MarkAsSent(attachment);

        Assert.True(result.Success);
        Assert.Null(result.Error);

        // Proven by the stored state: removal of a sent attachment is refused.
        Assert.Equal("cannot remove a sent attachment", service.Remove(attachment).Error);
    }

    [Fact]
    public void MarkAsSent_UnknownId_ReturnsError()
    {
        var (service, _, _) = CreateService();

        var result = service.MarkAsSent(
            new ComposerAttachment("nope", "a.png", "a.png", AttachmentState.Pending));

        Assert.False(result.Success);
        Assert.Equal("unknown attachment", result.Error);
    }

    [Fact]
    public async Task MarkAsSent_AlreadySent_IsIdempotent()
    {
        var (service, _, _) = CreateService();
        var attachment = (await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        Assert.True(service.MarkAsSent(attachment).Success);

        var second = service.MarkAsSent(attachment);

        Assert.True(second.Success);
        Assert.Null(second.Error);
    }

    // ── ClearAllAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ClearAllAsync_AllDeleted_ClearsEveryRecord()
    {
        var (service, fs, _) = CreateService();
        var a = (await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var b = (await service.SaveAsync("b.pdf", Content(), TestContext.Current.CancellationToken)).Attachment!;

        var result = await service.ClearAllAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Empty(fs.Files);
        Assert.False(IsRetained(service, a.Id));
        Assert.False(IsRetained(service, b.Id));
    }

    [Fact]
    public async Task ClearAllAsync_SomeDeletesFail_ReportsCountAndRetainsFailedRecords()
    {
        var (service, fs, _) = CreateService();
        var a = (await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var b = (await service.SaveAsync("b.pdf", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var c = (await service.SaveAsync("c.gif", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var lockedPath = Path.Combine(service.AttachmentsRootPath, b.SavedRelativePath);
        fs.FailDeleteFor = path => path == lockedPath ? new IOException("locked") : null;

        var result = await service.ClearAllAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("1 file(s) could not be deleted", result.Error);

        // Every file was attempted: the two deletable ones are gone.
        Assert.Equal(3, fs.DeletedPaths.Count);
        Assert.Equal([lockedPath], fs.Files.Keys.ToArray());

        // Records match the file system: only the failed one is retained.
        Assert.False(IsRetained(service, a.Id));
        Assert.True(IsRetained(service, b.Id));
        Assert.False(IsRetained(service, c.Id));
    }

    [Fact]
    public async Task ClearAllAsync_CancelledWhileAwaitingGate_ThrowsAndDeletesNothing()
    {
        var (service, fs, _) = CreateService();
        _ = await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken);

        // Occupy the gate with a save that blocks mid-copy.
        var blocking = new SyntheticStream(1024, blockOnFirstRead: true);
        var blocked = service.SaveAsync("blocking.png", blocking, TestContext.Current.CancellationToken);
        await blocking.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ClearAllAsync(cts.Token));
        Assert.Empty(fs.DeletedPaths);

        blocking.Release();
        _ = await blocked;
    }

    [Fact]
    public async Task ClearAllAsync_HoldsGateForWholeOperation_OtherOpsCannotInterleave()
    {
        var (service, fs, _) = CreateService();
        _ = await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken);

        // A streamed save holds the gate...
        var blocking = new SyntheticStream(1024, blockOnFirstRead: true);
        var blockedSave = service.SaveAsync("blocking.png", blocking, TestContext.Current.CancellationToken);
        await blocking.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // ...so ClearAll cannot start: it is still pending and has deleted nothing.
        var clear = service.ClearAllAsync(TestContext.Current.CancellationToken);
        Assert.False(clear.IsCompleted);
        Assert.Empty(fs.DeletedPaths);

        blocking.Release();
        Assert.True((await blockedSave).Success);

        var result = await clear.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task ClearAllAsync_SecondClearWaitsForFirst_DeletionsNeverOverlap()
    {
        var (service, fs, _) = CreateService();
        var a = (await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var b = (await service.SaveAsync("b.pdf", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var c = (await service.SaveAsync("c.gif", Content(), TestContext.Current.CancellationToken)).Attachment!;

        // The first delete of the FIRST ClearAll blocks while holding the gate.
        var firstDeleteReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDelete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedOnce = false;
        fs.BeforeDelete = _ =>
        {
            if (blockedOnce)
                return Task.CompletedTask;

            blockedOnce = true;
            firstDeleteReached.SetResult();
            return releaseFirstDelete.Task;
        };

        var firstClear = Task.Run(
            () => service.ClearAllAsync(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Task<AttachmentResult> secondClear;
        try
        {
            // Deterministic handshake: the first clear now holds the gate mid-deletion.
            await firstDeleteReached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // The second clear is invoked DIRECTLY on this thread — not queued via Task.Run — so
            // it cannot return until its synchronous prefix has reached the gate's WaitAsync.
            // An incomplete Task therefore proves it STARTED and is BLOCKED on the occupied gate,
            // which an unscheduled delegate could not explain.
            secondClear = service.ClearAllAsync(TestContext.Current.CancellationToken);

            Assert.False(secondClear.IsCompleted);

            // It has deleted nothing: the only delete on record is the first call's in-flight one.
            Assert.Single(fs.DeletedPaths);
        }
        finally
        {
            // Always release, so an assertion failure above cannot hang the test.
            releaseFirstDelete.TrySetResult(true);
        }

        var firstResult = await firstClear.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var secondResult = await secondClear.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);

        // The two clears never overlapped inside the file system.
        Assert.Equal(1, fs.MaxConcurrentOperations);

        // Every file was deleted exactly once — by the first call. The second found nothing.
        Assert.Equal(3, fs.DeletedPaths.Count);
        Assert.Equal(3, fs.DeletedPaths.Distinct().Count());
        Assert.Empty(fs.Files);
        Assert.False(IsRetained(service, a.Id));
        Assert.False(IsRetained(service, b.Id));
        Assert.False(IsRetained(service, c.Id));
    }

    [Fact]
    public async Task ClearAllAsync_CancelledBetweenFiles_ThrowsAndRetainsUndeletedRecords()
    {
        var (service, fs, _) = CreateService();
        var a = (await service.SaveAsync("a.png", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var b = (await service.SaveAsync("b.pdf", Content(), TestContext.Current.CancellationToken)).Attachment!;
        var c = (await service.SaveAsync("c.gif", Content(), TestContext.Current.CancellationToken)).Attachment!;

        using var cts = new CancellationTokenSource();
        fs.OnDelete = _ => cts.Cancel(); // cancel after the very first delete

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ClearAllAsync(cts.Token));

        // Stopped between files: exactly one delete happened, the rest are retained.
        Assert.Single(fs.DeletedPaths);
        Assert.Equal(2, fs.Files.Count);
        var retained = new[] { a, b, c }.Count(x => IsRetained(service, x.Id));
        Assert.Equal(2, retained);
    }

    [Fact]
    public async Task ClearAllAsync_InvalidStoredPath_CountsAsFailureRetainsRecordAndContinues()
    {
        // A valid record is mixed with one whose stored path is not in the generated form.
        // ClearAll must count the bad one as a failure, retain its record, and STILL delete
        // the good one — proving it does not throw or abort on a bad path.
        var goodId = "good-id";
        var badId = "bad-id";
        var seed = new Dictionary<string, ComposerAttachment>
        {
            [goodId] = new(goodId, "good.png", SeedPath, AttachmentState.Pending),
            [badId] = new(badId, "innocent.png", "not-a-guid.png", AttachmentState.Pending),
        };
        var (service, fs, _) = CreateService(seed);
        var goodPath = Path.Combine(service.AttachmentsRootPath, SeedPath);
        fs.SeedFile(goodPath, 0);

        var result = await service.ClearAllAsync(TestContext.Current.CancellationToken);

        // The good file was deleted; the bad record was counted as a failure and retained.
        Assert.False(result.Success);
        Assert.Contains("1 file(s) could not be deleted", result.Error);
        Assert.DoesNotContain(goodPath, fs.Files.Keys);
        Assert.False(IsRetained(service, goodId));
        Assert.True(IsRetained(service, badId));
    }

    [Fact]
    public async Task ClearAllAsync_AllPathsInvalid_ReportsFailureCountAndRetainsAll()
    {
        var seed = new Dictionary<string, ComposerAttachment>
        {
            ["bad1"] = new("bad1", "a.png", "not-a-guid.png", AttachmentState.Pending),
            ["bad2"] = new("bad2", "b.png", "real.pdf", AttachmentState.Pending),
        };
        var (service, fs, logger) = CreateService(seed);

        var result = await service.ClearAllAsync(TestContext.Current.CancellationToken);

        // No files were deleted, both records retained, failure count reported.
        Assert.False(result.Success);
        Assert.Equal("2 file(s) could not be deleted", result.Error);
        Assert.Empty(fs.DeletedPaths);
        Assert.True(IsRetained(service, "bad1"));
        Assert.True(IsRetained(service, "bad2"));
        Assert.Contains(
            logger.Entries,
            e => e.Contains("not in the generated contained form", StringComparison.Ordinal));
    }

    // ── Real file system round-trip (production constructor) ────────────────

    [Fact]
    public async Task ProductionConstructor_CreatesRootAndRoundTripsAnAttachment()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"copilothive-attach-{Guid.NewGuid():N}");
        try
        {
            var service = new ComposerAttachmentService(
                stateDir, NullLogger<ComposerAttachmentService>.Instance);

            Assert.True(Directory.Exists(service.AttachmentsRootPath));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(stateDir, "composer-attachments")),
                service.AttachmentsRootPath);

            var saved = await service.SaveAsync(
                "photo.png", new MemoryStream([1, 2, 3]), TestContext.Current.CancellationToken);
            Assert.True(saved.Success);
            var filePath = Path.Combine(service.AttachmentsRootPath, saved.Attachment!.SavedRelativePath);
            Assert.True(File.Exists(filePath));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken));

            Assert.True(service.Remove(saved.Attachment).Success);
            Assert.False(File.Exists(filePath));
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }
}

/// <summary>
/// Verifies that <see cref="ComposerAttachmentService"/> is registered in DI as a singleton
/// using the factory form (so it receives the resolved <c>stateDir</c>).
/// </summary>
[Collection("HiveIntegration")]
public sealed class ComposerAttachmentServiceRegistrationTests
{
    [Fact]
    public void ComposerAttachmentService_RegisteredAsSingletonFactory()
    {
        using var baseFactory = new HiveTestFactory();
        ServiceDescriptor? captured = null;

        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                captured = services.Single(d => d.ServiceType == typeof(ComposerAttachmentService))));
        using var client = factory.CreateClient();

        Assert.NotNull(captured);
        Assert.Equal(ServiceLifetime.Singleton, captured.Lifetime);
        Assert.NotNull(captured.ImplementationFactory);
        Assert.Null(captured.ImplementationInstance);

        // Same instance every resolution, rooted under the configured state directory.
        var first = factory.Services.GetRequiredService<ComposerAttachmentService>();
        var second = factory.Services.GetRequiredService<ComposerAttachmentService>();
        Assert.Same(first, second);
        Assert.EndsWith("composer-attachments", first.AttachmentsRootPath, StringComparison.Ordinal);
        Assert.True(Directory.Exists(first.AttachmentsRootPath));
    }
}

// ── Test doubles ────────────────────────────────────────────────────────────

/// <summary>
/// In-memory <see cref="IAttachmentFileSystem"/> with injectable failures, used to simulate
/// IO errors, oversize sources and cleanup faults without touching the real disk.
/// </summary>
internal sealed class FakeAttachmentFileSystem : IAttachmentFileSystem
{
    // All observable state is guarded by this lock. The serialization tests deliberately invoke
    // two operations concurrently, and must still behave deterministically (rather than corrupt
    // a dictionary and crash) if the production gate were ever removed.
    private readonly Lock _sync = new();
    private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly List<string> _deletedPaths = [];
    private readonly List<string> _openedPaths = [];
    private long _maxBytesWritten;
    private int _inFlight;
    private int _maxConcurrentOperations;

    /// <summary>Snapshot of files that currently "exist", keyed by absolute path, with their byte count.</summary>
    public Dictionary<string, long> Files
    {
        get
        {
            lock (_sync)
                return new Dictionary<string, long>(_files, StringComparer.Ordinal);
        }
    }

    /// <summary>Snapshot of directories created through the seam.</summary>
    public HashSet<string> Directories
    {
        get
        {
            lock (_sync)
                return new HashSet<string>(_directories, StringComparer.Ordinal);
        }
    }

    /// <summary>Snapshot of every path passed to <see cref="DeleteFile"/>, in order (including failures).</summary>
    public List<string> DeletedPaths
    {
        get
        {
            lock (_sync)
                return [.. _deletedPaths];
        }
    }

    /// <summary>Snapshot of every path passed to <see cref="OpenWrite"/>, in order (including failures).</summary>
    public List<string> OpenedPaths
    {
        get
        {
            lock (_sync)
                return [.. _openedPaths];
        }
    }

    /// <summary>
    /// The high-water mark of bytes written to any single destination stream. Proves the
    /// bounded copy never writes past the cap, even for a partial file that is later deleted.
    /// </summary>
    public long MaxBytesWritten
    {
        get
        {
            lock (_sync)
                return _maxBytesWritten;
        }
    }

    /// <summary>
    /// Highest number of file operations observed executing simultaneously. The service's single
    /// gate must keep this at 1 — anything higher proves two operations interleaved.
    /// </summary>
    public int MaxConcurrentOperations
    {
        get
        {
            lock (_sync)
                return _maxConcurrentOperations;
        }
    }

    /// <summary>Seeds a file directly, as if it already existed on disk.</summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="length">Byte count to report for the file.</param>
    public void SeedFile(string path, long length)
    {
        lock (_sync)
            _files[path] = length;
    }

    /// <summary>Returns an exception to throw for the given delete, or <c>null</c> to allow it.</summary>
    public Func<string, Exception?>? FailDeleteFor { get; set; }

    /// <summary>Returns an exception to throw for the given open-write, or <c>null</c> to allow it.</summary>
    public Func<string, Exception?>? FailOpenWriteFor { get; set; }

    /// <summary>Returns an exception to throw from <see cref="FlushAsync"/>, or <c>null</c> to allow it.</summary>
    public Func<Exception?>? FailFlush { get; set; }

    /// <summary>
    /// Returns an exception to throw while WRITING to a destination stream, or <c>null</c> to
    /// allow it. Simulates a genuine copy-stage failure originating at the destination.
    /// </summary>
    public Func<Exception?>? FailWrite { get; set; }

    /// <summary>
    /// Returns an exception to throw while CLOSING a destination stream, or <c>null</c> to allow it.
    /// Simulates a stream that copies and flushes cleanly but fails to persist during close.
    /// </summary>
    public Func<Exception?>? FailDispose { get; set; }

    /// <summary>
    /// Number of times a destination stream's <see cref="Stream.DisposeAsync"/> was actually
    /// invoked. Proves the close was ATTEMPTED — if the service stopped disposing on an
    /// already-failed path this would stay at 0.
    /// </summary>
    public int DisposeAttemptCount => Volatile.Read(ref _disposeAttemptCount);

    /// <summary>
    /// Whether a configured close failure was actually THROWN from a destination stream's
    /// <see cref="Stream.DisposeAsync"/>. Proves the fault was really raised on the code path
    /// under test, rather than merely configured and never reached.
    /// </summary>
    public bool DisposeThrew => Volatile.Read(ref _disposeThrew) != 0;

    private int _disposeAttemptCount;
    private int _disposeThrew;

    /// <summary>Records a close attempt and whether it raised the configured fault.</summary>
    /// <param name="threw">Whether the close threw.</param>
    internal void RecordDisposeAttempt(bool threw)
    {
        Interlocked.Increment(ref _disposeAttemptCount);
        if (threw)
            Interlocked.Exchange(ref _disposeThrew, 1);
    }

    /// <summary>Invoked after a successful delete (used to trigger cancellation between files).</summary>
    public Action<string>? OnDelete { get; set; }

    /// <summary>
    /// Awaited at the START of every <see cref="DeleteFile"/> call, before the delete happens.
    /// Lets a test block a call deterministically while it holds the gate.
    /// </summary>
    public Func<string, Task>? BeforeDelete { get; set; }

    /// <summary>
    /// Awaited at the START of every <see cref="OpenWrite"/> call, before the file is created.
    /// Lets a test block a save deterministically while it holds the gate.
    /// </summary>
    public Func<string, Task>? BeforeOpenWrite { get; set; }

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        lock (_sync)
            _directories.Add(path);
    }

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        lock (_sync)
            return _files.ContainsKey(path);
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        // Recorded BEFORE blocking so a test can observe the in-flight call.
        lock (_sync)
            _deletedPaths.Add(path);

        EnterOperation();
        try
        {
            // Synchronous seam member: block deterministically on the test-supplied gate.
            BeforeDelete?.Invoke(path).GetAwaiter().GetResult();

            var failure = FailDeleteFor?.Invoke(path);
            if (failure is not null)
                throw failure;

            lock (_sync)
                _files.Remove(path);

            OnDelete?.Invoke(path);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc />
    public Stream OpenWrite(string path)
    {
        // Recorded BEFORE blocking so a test can observe the in-flight call.
        lock (_sync)
            _openedPaths.Add(path);

        EnterOperation();
        try
        {
            BeforeOpenWrite?.Invoke(path).GetAwaiter().GetResult();

            var failure = FailOpenWriteFor?.Invoke(path);
            if (failure is not null)
                throw failure;

            // A real file system creates (truncates) the file immediately, so a cancelled or
            // oversize copy leaves a partial file behind that must be cleaned up.
            lock (_sync)
                _files[path] = 0;

            return new CountingFileStream(this, path);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void EnterOperation()
    {
        lock (_sync)
        {
            _inFlight++;
            _maxConcurrentOperations = Math.Max(_maxConcurrentOperations, _inFlight);
        }
    }

    private void ExitOperation()
    {
        lock (_sync)
            _inFlight--;
    }

    /// <summary>Records a destination stream's byte count under the shared lock.</summary>
    /// <param name="path">Absolute file path being written.</param>
    /// <param name="written">Bytes written so far.</param>
    internal void PublishWrite(string path, long written)
    {
        lock (_sync)
        {
            _maxBytesWritten = Math.Max(_maxBytesWritten, written);

            // Only publish while the file still "exists" — a delete during cleanup wins.
            if (_files.ContainsKey(path))
                _files[path] = written;
        }
    }

    /// <inheritdoc />
    public Task FlushAsync(Stream destination, CancellationToken ct)
    {
        var failure = FailFlush?.Invoke();
        return failure is not null ? Task.FromException(failure) : destination.FlushAsync(ct);
    }

    /// <inheritdoc />
    // Delegates to the PRODUCTION algorithm so the bound is exercised for real, not re-implemented.
    public Task<bool> CopyBoundedAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct) =>
        AttachmentFileSystem.CopyBoundedCoreAsync(source, destination, maxBytes, ct);

    /// <summary>
    /// Write-only sink that records how many bytes were written without buffering them,
    /// so oversize scenarios never allocate 20 MiB.
    /// </summary>
    private sealed class CountingFileStream(FakeAttachmentFileSystem fs, string path) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => Publish();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var failure = fs.FailWrite?.Invoke();
            if (failure is not null)
                throw failure;

            _written += count;
            Publish();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var failure = fs.FailWrite?.Invoke();
            if (failure is not null)
                return ValueTask.FromException(failure);

            _written += buffer.Length;
            Publish();
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Publish();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Publish();

            // A close that fails AFTER a clean copy+flush must surface, not be swallowed.
            // Every attempt is recorded so tests can prove the close was reached AND threw.
            var failure = fs.FailDispose?.Invoke();
            fs.RecordDisposeAttempt(failure is not null);
            return failure is not null
                ? ValueTask.FromException(failure)
                : ValueTask.CompletedTask;
        }

        private void Publish() => fs.PublishWrite(path, _written);
    }
}

/// <summary>
/// Read-only stream that yields a few bytes and then throws from <see cref="ReadAsync"/>,
/// simulating a genuine COPY-stage failure (a faulted upload source) as opposed to a
/// flush or close failure.
/// </summary>
/// <param name="failure">The exception to raise once <paramref name="bytesBeforeFailure"/> bytes have been read.</param>
/// <param name="bytesBeforeFailure">Number of bytes to yield before faulting.</param>
internal sealed class ThrowingSourceStream(Exception failure, int bytesBeforeFailure = 8) : Stream
{
    private long _position;

    /// <summary>Total bytes handed out before the fault.</summary>
    public long TotalBytesRead => _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => bytesBeforeFailure;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = bytesBeforeFailure - _position;
        if (remaining <= 0)
            return ValueTask.FromException<int>(failure);

        var count = (int)Math.Min(buffer.Length, remaining);
        buffer.Span[..count].Clear();
        _position += count;
        return ValueTask.FromResult(count);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Read-only stream that produces a fixed number of synthetic bytes lazily, so tests can
/// exercise the 20 MiB bound without allocating 20 MiB, and can hook the first read to
/// cancel or block the copy.
/// </summary>
internal sealed class SyntheticStream : Stream
{
    private readonly long _length;
    private readonly Action? _onFirstRead;
    private readonly bool _blockOnFirstRead;
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _position;
    private bool _firstReadDone;

    /// <summary>Creates a synthetic source of <paramref name="length"/> zero bytes.</summary>
    /// <param name="length">Total number of bytes the stream yields.</param>
    /// <param name="onFirstRead">Optional callback invoked when the first read completes.</param>
    /// <param name="blockOnFirstRead">When <c>true</c>, the first read blocks until <see cref="Release"/>.</param>
    public SyntheticStream(long length, Action? onFirstRead = null, bool blockOnFirstRead = false)
    {
        _length = length;
        _onFirstRead = onFirstRead;
        _blockOnFirstRead = blockOnFirstRead;
    }

    /// <summary>Completes once the first read has started (the gate is held by then).</summary>
    public TaskCompletionSource FirstReadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Total bytes handed out so far.</summary>
    public long TotalBytesRead => _position;

    /// <summary>Largest single read request observed — proves chunked (non full-buffer) reads.</summary>
    public int MaxRequestedCount { get; private set; }

    /// <summary>Unblocks a stream created with <c>blockOnFirstRead</c>.</summary>
    public void Release() => _release.TrySetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        MaxRequestedCount = Math.Max(MaxRequestedCount, buffer.Length);
        FirstReadStarted.TrySetResult();

        if (!_firstReadDone)
        {
            _firstReadDone = true;
            if (_blockOnFirstRead)
                await _release.Task;
            _onFirstRead?.Invoke();
        }

        var remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        var count = (int)Math.Min(buffer.Length, remaining);
        buffer.Span[..count].Clear();
        _position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Logger that records formatted messages so tests can assert on stray-file diagnostics.</summary>
/// <typeparam name="T">Category type.</typeparam>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Formatted log messages in emission order.</summary>
    public List<string> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add(formatter(state, exception));
    }
}
