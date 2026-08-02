// Manual verification required: paste a real screenshot in a modern browser
// supporting clipboard image items (Chrome/Edge). PDF clipboard items are
// browser/OS-dependent. The JS module's DOM paste event handling and the
// actual IJSStreamReference streaming interop require a live Blazor circuit
// and cannot be unit-tested without bUnit DOM event simulation.
using CopilotHive.Components.Pages;
using CopilotHive.Services;

using Microsoft.JSInterop;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the .NET side of the Composer clipboard-paste flow in <see cref="ComposerChat"/>.
/// Two layers: focused tests of the internal helpers (<c>ValidatePaste</c>,
/// <c>ComputePasteAllowed</c>, <c>BuildPasteFileName</c>), and handler-level tests that drive the
/// PRODUCTION body — <c>ComposerChat.HandlePasteCoreAsync</c>, which the <c>[JSInvokable]</c>
/// <c>HandlePasteAsync</c> delegates to — through the internal <c>IPasteHost</c> seam with a fake
/// <see cref="IJSStreamReference"/>. Everything is reached via <c>InternalsVisibleTo</c>; no bUnit
/// rendering is required.
/// </summary>
/// <remarks>
/// MANUAL VERIFICATION REQUIRED: the browser half of this feature (the <c>paste</c> event, the
/// clipboard item filter and the <c>DotNet.createJSStreamReference</c> streaming interop) cannot be
/// covered here — DOM paste events need a real browser and the stream interop needs a live Blazor
/// circuit. Verify by hand in a clipboard-image-capable browser (Chrome/Edge): copy a screenshot,
/// press Ctrl+V in the Composer textarea, confirm the attachment chip appears and that a plain-text
/// paste is still inserted normally. PDF clipboard items are browser/OS-dependent and may not be
/// offered as <c>application/pdf</c> at all on some platforms.
///
/// PLAIN-TEXT PASTE: a paste whose clipboard event carries no file items (or only unsupported
/// items) is handled ENTIRELY in the JS module — the <c>paste</c> listener finds no supported
/// file item and returns without <c>preventDefault</c>, so the text is inserted normally and no
/// error is produced. That path never reaches .NET, is never intercepted, and must be verified
/// manually (paste text into the Composer textarea while idle and while an attachment is pending).
///
/// ZERO-LENGTH BLOB: a supported file item whose blob resolves to zero bytes must likewise NOT
/// be intercepted — the JS module returns BEFORE <c>preventDefault</c> and before any
/// <c>invokeMethodAsync</c>, so the paste falls through to normal browser behaviour with no
/// .NET call and no error, and <c>allowed</c> is left untouched. This is JS-side behaviour
/// requiring manual verification in a browser; the .NET-side <c>declaredLength &lt;= 0</c>
/// rejection is covered by the tests below.
///
/// MODULE CLEANUP: <see cref="ComposerChat.DisposeAsync"/> attempts <c>uninstall</c> and
/// <c>module.DisposeAsync()</c> as two INDEPENDENT steps, each in its own try/catch — an
/// <c>uninstall</c> fault (circuit teardown or otherwise) never skips the module disposal and
/// vice versa. <c>JSDisconnectedException</c> is expected during circuit teardown and is
/// swallowed; every OTHER exception is LOGGED via <c>Console.Error.WriteLine</c>
/// ("[ComposerChat] Paste module uninstall failed: …" / "[ComposerChat] Paste module disposal
/// failed: …") rather than silently swallowed, so teardown faults are observable. The
/// allowed-state sync failures are likewise logged ("[ComposerChat] Paste allowed-state sync
/// failed: …") via the same best-effort pattern. This cannot be unit-tested without a live
/// Blazor circuit (the module reference is created via JS interop), so it is verified by code
/// inspection and manual teardown.
/// </remarks>
public sealed class ComposerChatPasteTests
{
    private const string StateDir = "/tmp/copilothive-paste-tests";
    private const long MaxBytes = 20_971_520;

    private static (ComposerAttachmentService Service, FakeAttachmentFileSystem Fs) CreateService()
    {
        var fs = new FakeAttachmentFileSystem();
        var service = new ComposerAttachmentService(
            Path.Combine(StateDir, Guid.NewGuid().ToString("N")),
            new CapturingLogger<ComposerAttachmentService>(),
            fs);
        return (service, fs);
    }

    /// <summary>
    /// Mirrors the ordering inside <c>HandlePasteAsync</c>: the state guard and the
    /// MIME/size validation both run BEFORE any save, so a rejected paste never touches
    /// the attachment service.
    /// </summary>
    private static async Task<(bool Saved, string? Error, ComposerAttachment? Pending)> SimulatePasteAsync(
        ComposerAttachmentService service,
        string? mimeType,
        long declaredLength,
        Stream content,
        bool isStreaming = false,
        bool uploading = false,
        ComposerAttachment? pending = null)
    {
        if (!ComposerChat.ComputePasteAllowed(isStreaming, uploading, pending))
            return (false, null, pending);

        var ext = ComposerChat.ValidatePaste(mimeType, declaredLength, out var validationError);
        if (ext is null)
            return (false, validationError, pending);

        var name = ComposerChat.BuildPasteFileName(ext, DateTime.UtcNow);
        var result = await service.SaveAsync(name, content, TestContext.Current.CancellationToken);

        return result.Success && result.Attachment is not null
            ? (true, null, result.Attachment)
            : (true, result.Error ?? "Failed to save attachment.", pending);
    }

    // ── MIME validation ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/gif", "gif")]
    [InlineData("image/webp", "webp")]
    [InlineData("application/pdf", "pdf")]
    public void ValidatePaste_SupportedMime_ReturnsMappedExtension(string mime, string expectedExtension)
    {
        var ext = ComposerChat.ValidatePaste(mime, 128, out var error);

        Assert.Equal(expectedExtension, ext);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/bmp")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidatePaste_UnsupportedMime_RejectedWithUnsupportedError(string? mime)
    {
        var ext = ComposerChat.ValidatePaste(mime, 128, out var error);

        Assert.Null(ext);
        Assert.Equal("Unsupported clipboard item.", error);
    }

    [Fact]
    public async Task Paste_UnsupportedMime_NeverReachesSaveAsync()
    {
        var (service, fs) = CreateService();

        var (saved, error, pending) = await SimulatePasteAsync(
            service, "text/plain", 128, new MemoryStream(new byte[128]));

        Assert.False(saved);
        Assert.Equal("Unsupported clipboard item.", error);
        Assert.Null(pending);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.OpenedPaths);
    }

    // ── Declared-length validation ──────────────────────────────────────────

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void ValidatePaste_NonPositiveDeclaredLength_RejectedWithEmptyFileError(long declaredLength)
    {
        var ext = ComposerChat.ValidatePaste("image/png", declaredLength, out var error);

        Assert.Null(ext);
        Assert.Equal("No file selected or file is empty.", error);
    }

    [Fact]
    public async Task Paste_ZeroDeclaredLength_NeverReachesSaveAsync()
    {
        var (service, fs) = CreateService();

        var (saved, error, _) = await SimulatePasteAsync(
            service, "image/png", 0, new MemoryStream([]));

        Assert.False(saved);
        Assert.Equal("No file selected or file is empty.", error);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.OpenedPaths);
    }

    [Fact]
    public void ValidatePaste_PositiveDeclaredLength_Accepted()
    {
        var ext = ComposerChat.ValidatePaste("application/pdf", 1, out var error);

        Assert.Equal("pdf", ext);
        Assert.Null(error);
    }

    // ── Paste state race guard ──────────────────────────────────────────────

    [Fact]
    public void ComputePasteAllowed_IdleWithNoPending_ReturnsTrue()
    {
        Assert.True(ComposerChat.ComputePasteAllowed(false, false, null));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ComputePasteAllowed_BusyState_ReturnsFalse(bool isStreaming, bool uploading, bool hasPending)
    {
        Assert.False(ComposerChat.ComputePasteAllowed(
            isStreaming, uploading, hasPending ? new object() : null));
    }

    [Fact]
    public async Task Paste_WhilePendingAttachmentExists_DropsPasteWithoutSavingOrReplacing()
    {
        var (service, fs) = CreateService();
        var existing = await service.SaveAsync(
            "existing.png", new MemoryStream(new byte[16]), TestContext.Current.CancellationToken);
        Assert.True(existing.Success);
        var pending = existing.Attachment!;
        var filesBefore = fs.Files.Count;

        var (saved, error, stillPending) = await SimulatePasteAsync(
            service, "image/png", 64, new MemoryStream(new byte[64]), pending: pending);

        Assert.False(saved);
        Assert.Null(error);                       // Silent drop — no error is surfaced.
        Assert.Same(pending, stillPending);       // The existing attachment is untouched.
        Assert.Equal(filesBefore, fs.Files.Count); // No new (orphan) file was written.
        Assert.Empty(fs.DeletedPaths);            // And the existing one was not removed.
    }

    [Fact]
    public async Task Paste_WhileUploading_DropsPasteWithoutSaving()
    {
        var (service, fs) = CreateService();

        var (saved, error, _) = await SimulatePasteAsync(
            service, "image/png", 64, new MemoryStream(new byte[64]), uploading: true);

        Assert.False(saved);
        Assert.Null(error);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.OpenedPaths);
    }

    [Fact]
    public async Task Paste_WhileStreaming_DropsPasteWithoutSaving()
    {
        var (service, fs) = CreateService();

        var (saved, error, _) = await SimulatePasteAsync(
            service, "image/png", 64, new MemoryStream(new byte[64]), isStreaming: true);

        Assert.False(saved);
        Assert.Null(error);
        Assert.Empty(fs.Files);
        Assert.Empty(fs.OpenedPaths);
    }

    // ── Oversize paste ──────────────────────────────────────────────────────

    [Fact]
    public async Task Paste_DeclaredLengthOverCap_ReachesSaveAsyncAndSurfacesExactOversizeError()
    {
        var (service, fs) = CreateService();
        var declaredLength = MaxBytes + 1;

        // Validation lets it through: the declared length is only a stream bound, SaveAsync is
        // the size authority.
        var ext = ComposerChat.ValidatePaste("image/png", declaredLength, out var validationError);
        Assert.Equal("png", ext);
        Assert.Null(validationError);

        var (saved, error, pending) = await SimulatePasteAsync(
            service, "image/png", declaredLength, new SyntheticStream(declaredLength));

        Assert.True(saved);                                    // SaveAsync WAS called.
        Assert.Equal("attachment exceeds 20 MiB", error);      // Exact frozen error string.
        Assert.Null(pending);                                  // Nothing became pending.
        Assert.Empty(fs.Files);                                // Partial file deleted.
    }

    [Fact]
    public async Task Paste_ContentWithinCap_BecomesPendingAttachmentWithGeneratedName()
    {
        var (service, fs) = CreateService();

        var (saved, error, pending) = await SimulatePasteAsync(
            service, "image/webp", 32, new MemoryStream(new byte[32]));

        Assert.True(saved);
        Assert.Null(error);
        Assert.NotNull(pending);
        Assert.Equal(AttachmentState.Pending, pending.State);
        Assert.StartsWith("paste-", pending.DisplayName);
        Assert.EndsWith(".webp", pending.DisplayName);
        Assert.Single(fs.Files);
    }

    // ── Generated file name ─────────────────────────────────────────────────

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("pdf")]
    public void BuildPasteFileName_UsesPastePrefixTimestampAndExtension(string extension)
    {
        var utcNow = new DateTime(2024, 3, 9, 7, 5, 4, DateTimeKind.Utc);

        var name = ComposerChat.BuildPasteFileName(extension, utcNow);

        Assert.Equal($"paste-20240309-070504.{extension}", name);
    }

    [Fact]
    public void BuildPasteFileName_ForEverySupportedMime_ProducesAllowlistedExtension()
    {
        var utcNow = new DateTime(2030, 12, 31, 23, 59, 58, DateTimeKind.Utc);

        foreach (var (mime, expectedExtension) in ComposerChat.PasteMimeExtensions)
        {
            var ext = ComposerChat.ValidatePaste(mime, 1, out var error);

            Assert.Null(error);
            Assert.Equal(expectedExtension, ext);
            Assert.Equal($"paste-20301231-235958.{expectedExtension}", ComposerChat.BuildPasteFileName(ext!, utcNow));
        }
    }

    // ── PRODUCTION handler: ComposerChat.HandlePasteCoreAsync ───────────────
    //
    // These drive the REAL handler body through the internal IPasteHost seam (the same code the
    // [JSInvokable] HandlePasteAsync delegates to) with a fake IJSStreamReference, so the guard
    // order, the _uploading claim, the stream-reference disposal and the allowed-state re-sync
    // are all verified as wired in production rather than re-simulated.

    [Fact]
    public async Task HandlePasteCoreAsync_ValidPngPaste_SavesWithGeneratedNameAndDisposesStreamRef()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[64]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 64);

        Assert.NotNull(host.Pending);
        Assert.Equal(AttachmentState.Pending, host.Pending.State);
        Assert.StartsWith("paste-", host.Pending.DisplayName);
        Assert.EndsWith(".png", host.Pending.DisplayName);
        Assert.Equal(host.Pending.DisplayName, Assert.Single(host.SavedNames));
        Assert.Null(host.AttachmentError);
        Assert.Single(fs.Files);

        // Every path disposes the stream reference and re-syncs the JS mirror.
        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > 0);

        // The upload claim is released once the save completes, so paste is available again.
        Assert.False(host.Uploading);
        Assert.False(host.LastSyncedAllowed); // A pending attachment still blocks the next paste.
    }

    [Fact]
    public async Task HandlePasteCoreAsync_ClaimsUploadingBeforeOpeningTheStream()
    {
        var (service, _) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[16]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/jpeg", 16);

        // The race window this closes: a second paste arriving while the stream is being opened
        // must see _uploading == true and be refused by the guard.
        Assert.True(streamRef.UploadingWhenOpened);
        Assert.False(host.Uploading);
    }

    [Fact]
    public async Task HandlePasteCoreAsync_PassesDeclaredLengthAsTheStreamBound()
    {
        var (service, _) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[8]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/gif", 12_345);

        Assert.Equal(12_345, streamRef.MaxAllowedSize);
    }

    [Fact]
    public async Task HandlePasteCoreAsync_AcceptedPaste_MirrorsUploadStartToJsBeforeOpeningTheStream()
    {
        var (service, _) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[16]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 16);

        // Accepting the paste is itself a transition: JS must learn allowed = false from .NET
        // BEFORE the stream is opened, not only once the save has finished.
        var beforeOpen = streamRef.SyncedValuesWhenOpened;
        Assert.NotEmpty(beforeOpen);
        Assert.False(beforeOpen[^1]);

        // And the final sync still restores the mirror once the flow completes.
        Assert.True(host.SyncCount > beforeOpen.Count);

        // NOTE: the file-picker path (HandleFileSelected) mirrors the SAME production sync —
        // it sets _uploading = true, calls TrySyncPasteAllowedAsync(PasteHost) BEFORE opening
        // its stream, and re-syncs in the finally. The upload state itself is covered by
        // ComputePasteAllowed_BusyState_ReturnsFalse((false, true, false)): with _uploading true
        // the computed mirror value is allowed=false. The picker cannot be driven here without
        // a live InputFileChangeEventArgs/IBrowserFile (bUnit), so this production-path test
        // stands in for the shared sync-before-open ordering.
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/bmp")]
    [InlineData("")]
    public async Task HandlePasteCoreAsync_UnsupportedMime_NoSaveNoStreamOpenButStillDisposesAndSyncs(string mime)
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[64]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, mime, 64);

        Assert.Equal("Unsupported clipboard item.", host.AttachmentError);
        Assert.Null(host.Pending);
        Assert.Empty(host.SavedNames);
        Assert.False(streamRef.Opened);   // Validation runs BEFORE the stream is opened.
        Assert.Empty(fs.Files);
        Assert.Empty(fs.OpenedPaths);

        Assert.True(streamRef.Disposed);  // Rejection must not leak the reference.
        Assert.True(host.SyncCount > 0);  // JS set allowed=false; .NET must restore it.
        Assert.True(host.LastSyncedAllowed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task HandlePasteCoreAsync_NonPositiveDeclaredLength_NoSaveButStillDisposesAndSyncs(long declaredLength)
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", declaredLength);

        Assert.Equal("No file selected or file is empty.", host.AttachmentError);
        Assert.Null(host.Pending);
        Assert.Empty(host.SavedNames);
        Assert.False(streamRef.Opened);
        Assert.Empty(fs.Files);

        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > 0);
        Assert.True(host.LastSyncedAllowed);
    }

    [Fact]
    public async Task HandlePasteCoreAsync_StaleMirrorWithPendingAttachment_DropsSilentlyAndKeepsPending()
    {
        var (service, fs) = CreateService();
        var existing = await service.SaveAsync(
            "existing.png", new MemoryStream(new byte[16]), TestContext.Current.CancellationToken);
        Assert.True(existing.Success);

        var host = new RecordingPasteHost(service) { Pending = existing.Attachment };
        var filesBefore = fs.Files.Count;
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[64]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 64);

        Assert.Same(existing.Attachment, host.Pending); // Never replaced.
        Assert.Empty(host.SavedNames);                  // Never saved.
        Assert.False(streamRef.Opened);
        Assert.Null(host.AttachmentError);              // Silent drop, no error surfaced.
        Assert.Equal(filesBefore, fs.Files.Count);      // No orphan file.
        Assert.Empty(fs.DeletedPaths);

        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > 0);
        Assert.False(host.LastSyncedAllowed);           // Still blocked by the pending attachment.
    }

    [Fact]
    public async Task HandlePasteCoreAsync_StaleMirrorWhileUploading_DropsSilentlyAndStillDisposes()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service) { Uploading = true };
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[64]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 64);

        Assert.Empty(host.SavedNames);
        Assert.False(streamRef.Opened);
        Assert.Null(host.AttachmentError);
        Assert.Empty(fs.Files);
        Assert.True(host.Uploading); // The in-flight upload's claim is left untouched.
        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > 0);
    }

    [Fact]
    public async Task HandlePasteCoreAsync_StaleMirrorWhileStreaming_DropsSilentlyAndStillDisposes()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service) { IsStreamingValue = true };
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[64]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 64);

        Assert.Empty(host.SavedNames);
        Assert.False(streamRef.Opened);
        Assert.Null(host.AttachmentError);
        Assert.Empty(fs.Files);
        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > 0);
    }

    [Fact]
    public async Task HandlePasteCoreAsync_OversizeContent_ReachesSaveAsyncAndSurfacesExactOversizeError()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);
        var declaredLength = MaxBytes + 1;
        var streamRef = new FakeJSStreamReference(new SyntheticStream(declaredLength), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", declaredLength);

        Assert.True(streamRef.Opened);                             // SaveAsync WAS reached.
        Assert.Equal(declaredLength, streamRef.MaxAllowedSize);    // Bounded by the declared length.
        Assert.Single(host.SavedNames);
        Assert.Equal("attachment exceeds 20 MiB", host.AttachmentError); // Exact frozen string.
        Assert.Null(host.Pending);
        Assert.Empty(fs.Files);                                    // Partial file deleted.

        Assert.True(streamRef.Disposed);
        Assert.True(host.LastSyncedAllowed);                       // Paste is available again.
    }

    [Fact]
    public async Task HandlePasteCoreAsync_StreamOpenFails_SurfacesErrorReleasesUploadAndDisposes()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[16]), host)
        {
            OpenFailure = new IOException("interop stream failed"),
        };

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 16);

        Assert.Equal("interop stream failed", host.AttachmentError);
        Assert.Null(host.Pending);
        Assert.Empty(host.SavedNames);
        Assert.Empty(fs.Files);

        Assert.False(host.Uploading);        // The claim is released even though the save never ran.
        Assert.True(streamRef.Disposed);
        Assert.True(host.LastSyncedAllowed); // Paste is available again after the failure.
    }

    // ── Exceptional cleanup paths ───────────────────────────────────────────

    [Fact]
    public async Task HandlePasteCoreAsync_StreamRefDisposalThrows_StillSyncsAndPropagates()
    {
        var (service, _) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[16]), host)
        {
            DisposeFailure = new InvalidOperationException("dispose exploded"),
        };

        var syncsBefore = host.SyncCount;

        // A non-JSDisconnectedException disposal fault is NOT swallowed…
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 16));
        Assert.Equal("dispose exploded", ex.Message);

        // …but it must never leave the JS mirror stuck at allowed = false.
        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > syncsBefore);
        Assert.True(host.LastSyncedAllowed || host.Pending is not null);

        // The save itself still succeeded before the cleanup fault.
        Assert.NotNull(host.Pending);
        Assert.False(host.Uploading);
    }

    [Fact]
    public async Task HandlePasteCoreAsync_StreamRefDisposalThrowsOnRejectedPaste_StillSyncsMirrorBackOn()
    {
        var (service, _) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(), host)
        {
            DisposeFailure = new InvalidOperationException("dispose exploded"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerChat.HandlePasteCoreAsync(host, streamRef, "text/plain", 16));

        Assert.Equal("Unsupported clipboard item.", host.AttachmentError);
        Assert.True(streamRef.Disposed);
        Assert.True(host.SyncCount > 0);
        Assert.True(host.LastSyncedAllowed); // Paste is available again despite the fault.
    }

    [Fact]
    public async Task HandlePasteCoreAsync_SaveThrows_StillDisposesReleasesUploadingAndSyncs()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service)
        {
            SaveFailure = new IOException("destination write failed"),
        };
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[16]), host);

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 16);

        Assert.Single(host.SavedNames);                    // SaveAsync WAS reached.
        Assert.Equal("destination write failed", host.AttachmentError); // Caught, not fatal.
        Assert.Null(host.Pending);
        Assert.False(host.Uploading);                      // The claim is released.
        Assert.True(streamRef.Disposed);                   // Stream ref still disposed.
        Assert.True(host.LastSyncedAllowed);               // JS mirror restored.
        Assert.Empty(fs.Files);                            // Nothing was written.
    }

    [Fact]
    public async Task HandlePasteCoreAsync_SyncThrows_DoesNotMaskThePasteOutcome()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);
        var streamRef = new FakeJSStreamReference(new MemoryStream(new byte[16]), host);

        // The mirror update is best-effort: a failing sync must not surface as a paste failure.
        host.SyncFailure = new InvalidOperationException("sync exploded");

        await ComposerChat.HandlePasteCoreAsync(host, streamRef, "image/png", 16);

        Assert.NotNull(host.Pending);
        Assert.Null(host.AttachmentError);
        Assert.Single(fs.Files);
        Assert.True(streamRef.Disposed);
    }

    // ── PRODUCTION shared funnel: ComposerChat.SaveAttachmentAsync ──────────

    [Fact]
    public async Task SaveAttachmentAsync_Success_SetsPendingAndReleasesUploading()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);

        await ComposerChat.SaveAttachmentAsync(host, "picked.png", new MemoryStream(new byte[32]));

        Assert.NotNull(host.Pending);
        Assert.Equal("picked.png", host.Pending.DisplayName);
        Assert.Null(host.AttachmentError);
        Assert.False(host.Uploading);
        Assert.Single(fs.Files);
    }

    [Fact]
    public async Task SaveAttachmentAsync_RejectedByService_SetsErrorAndLeavesPendingNull()
    {
        var (service, fs) = CreateService();
        var host = new RecordingPasteHost(service);

        // Not allowlisted → the service rejects before writing anything.
        await ComposerChat.SaveAttachmentAsync(host, "payload.exe", new MemoryStream(new byte[32]));

        Assert.Null(host.Pending);
        Assert.NotNull(host.AttachmentError);
        Assert.False(host.Uploading);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task SaveAttachmentAsync_ServiceThrows_ReleasesUploadingAndPropagates()
    {
        var host = new ThrowingSavePasteHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerChat.SaveAttachmentAsync(host, "picked.png", new MemoryStream(new byte[8])));

        Assert.False(host.Uploading); // finally released the claim.
    }
}

/// <summary>
/// In-memory <see cref="ComposerChat.IPasteHost"/> that records everything the production paste
/// flow does to component state, so the real handler can run without a Blazor circuit.
/// </summary>
internal sealed class RecordingPasteHost(ComposerAttachmentService service) : ComposerChat.IPasteHost
{
    private readonly List<string> _savedNames = [];
    private readonly List<bool> _syncedValues = [];

    /// <inheritdoc />
    public ComposerAttachment? Pending { get; set; }

    /// <inheritdoc />
    public bool Uploading { get; set; }

    /// <inheritdoc />
    public string? AttachmentError { get; set; }

    /// <summary>Backing value for <see cref="IsStreaming"/>, settable by tests.</summary>
    public bool IsStreamingValue { get; set; }

    /// <inheritdoc />
    public bool IsStreaming => IsStreamingValue;

    /// <summary>Names handed to <see cref="SaveAsync"/>, in order — empty means no save happened.</summary>
    public IReadOnlyList<string> SavedNames => _savedNames;

    /// <summary>Every value mirrored to JS, in order, one entry per sync.</summary>
    public IReadOnlyList<bool> SyncedValues => _syncedValues;

    /// <summary>How many times the JS allowed-state mirror was re-synced.</summary>
    public int SyncCount => _syncedValues.Count;

    /// <summary>The value that would have been mirrored to JS by the most recent sync.</summary>
    public bool LastSyncedAllowed => _syncedValues.Count > 0 && _syncedValues[^1];

    /// <summary>How many re-renders the flow requested.</summary>
    public int NotifyCount { get; private set; }

    /// <summary>When set, <see cref="SyncPasteAllowedAsync"/> throws it instead of recording.</summary>
    public Exception? SyncFailure { get; set; }

    /// <summary>When set, <see cref="SaveAsync"/> throws it instead of delegating to the service.</summary>
    public Exception? SaveFailure { get; set; }

    /// <inheritdoc />
    public Task<AttachmentSaveResult> SaveAsync(string originalName, Stream content)
    {
        _savedNames.Add(originalName);
        return SaveFailure is not null
            ? Task.FromException<AttachmentSaveResult>(SaveFailure)
            : service.SaveAsync(originalName, content);
    }

    /// <inheritdoc />
    public void Notify() => NotifyCount++;

    /// <inheritdoc />
    public Task SyncPasteAllowedAsync()
    {
        if (SyncFailure is not null)
            return Task.FromException(SyncFailure);

        // Mirrors the production sync: the component computes the same predicate.
        _syncedValues.Add(ComposerChat.ComputePasteAllowed(IsStreaming, Uploading, Pending));
        return Task.CompletedTask;
    }
}

/// <summary>Host whose save always throws, proving the shared funnel's finally releases the claim.</summary>
internal sealed class ThrowingSavePasteHost : ComposerChat.IPasteHost
{
    /// <inheritdoc />
    public ComposerAttachment? Pending { get; set; }

    /// <inheritdoc />
    public bool Uploading { get; set; }

    /// <inheritdoc />
    public string? AttachmentError { get; set; }

    /// <inheritdoc />
    public bool IsStreaming => false;

    /// <inheritdoc />
    public Task<AttachmentSaveResult> SaveAsync(string originalName, Stream content)
        => throw new InvalidOperationException("save exploded");

    /// <inheritdoc />
    public void Notify()
    {
    }

    /// <inheritdoc />
    public Task SyncPasteAllowedAsync() => Task.CompletedTask;
}

/// <summary>
/// Fake <see cref="IJSStreamReference"/> standing in for a pasted clipboard blob. Records whether
/// the stream was opened (and with which bound), captures the host's uploading state AT THE MOMENT
/// the stream is opened (proving the claim precedes the first await) and tracks its own disposal.
/// </summary>
internal sealed class FakeJSStreamReference(Stream content, RecordingPasteHost? host = null) : IJSStreamReference
{
    /// <inheritdoc />
    public long Length => content.CanSeek ? content.Length : 0;

    /// <summary>Whether <see cref="OpenReadStreamAsync"/> was called.</summary>
    public bool Opened { get; private set; }

    /// <summary>The bound the handler asked for; <c>null</c> when the stream was never opened.</summary>
    public long? MaxAllowedSize { get; private set; }

    /// <summary>The host's uploading state observed when the stream was opened.</summary>
    public bool UploadingWhenOpened { get; private set; }

    /// <summary>
    /// The values mirrored to JS BEFORE the stream was opened. Proves that accepting a paste is
    /// itself synced (and with <c>false</c>) rather than only after the save completes.
    /// </summary>
    public IReadOnlyList<bool> SyncedValuesWhenOpened { get; private set; } = [];

    /// <summary>Whether <see cref="DisposeAsync"/> was called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>When set, <see cref="OpenReadStreamAsync"/> throws it instead of returning a stream.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>When set, <see cref="DisposeAsync"/> throws it after recording the call.</summary>
    public Exception? DisposeFailure { get; set; }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadStreamAsync(
        long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
    {
        Opened = true;
        MaxAllowedSize = maxAllowedSize;
        UploadingWhenOpened = host?.Uploading ?? false;
        SyncedValuesWhenOpened = host is null ? [] : [.. host.SyncedValues];

        if (OpenFailure is not null)
            return ValueTask.FromException<Stream>(OpenFailure);

        return ValueTask.FromResult(content);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return DisposeFailure is not null
            ? ValueTask.FromException(DisposeFailure)
            : ValueTask.CompletedTask;
    }
}
