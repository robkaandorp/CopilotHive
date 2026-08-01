using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Lifecycle state of a Composer chat attachment.
/// </summary>
public enum AttachmentState
{
    /// <summary>The attachment is staged locally and has not been sent to the model yet.</summary>
    Pending,

    /// <summary>The attachment has been sent to the model and is immutable from now on.</summary>
    Sent,
}

/// <summary>
/// A single attachment retained by <see cref="ComposerAttachmentService"/>.
/// </summary>
/// <param name="Id">Opaque GUID string identifying the attachment.</param>
/// <param name="DisplayName">The original file name exactly as supplied by the user.</param>
/// <param name="SavedRelativePath">
/// Path of the stored file RELATIVE to <see cref="ComposerAttachmentService.AttachmentsRootPath"/>
/// in the flat generated form <c>&lt;guid&gt;.&lt;ext&gt;</c> (for example <c>"a1b2.png"</c>).
/// </param>
/// <param name="State">Whether the attachment is still pending or already sent.</param>
public record ComposerAttachment(
    string Id,
    string DisplayName,
    string SavedRelativePath,
    AttachmentState State);

/// <summary>
/// Outcome of an operation that produces (or replaces) an attachment.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Attachment">The resulting attachment when <paramref name="Success"/> is <c>true</c>; otherwise <c>null</c>.</param>
/// <param name="Error">Human-readable error when <paramref name="Success"/> is <c>false</c>; otherwise <c>null</c>.</param>
public record AttachmentSaveResult(bool Success, ComposerAttachment? Attachment, string? Error);

/// <summary>
/// Outcome of an attachment operation that produces no attachment.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Error">Human-readable error when <paramref name="Success"/> is <c>false</c>; otherwise <c>null</c>.</param>
public record AttachmentResult(bool Success, string? Error);

/// <summary>
/// Narrow internal abstraction over the file operations used by
/// <see cref="ComposerAttachmentService"/>. Hand-rolled on purpose (no
/// <c>System.IO.Abstractions</c> dependency) so tests can portably simulate
/// IO failures, oversize sources, cancellation and cleanup faults.
/// </summary>
internal interface IAttachmentFileSystem
{
    /// <summary>Creates a directory (and any missing parents); a no-op when it already exists.</summary>
    /// <param name="path">Absolute directory path.</param>
    void CreateDirectory(string path);

    /// <summary>Returns whether the given file exists.</summary>
    /// <param name="path">Absolute file path.</param>
    bool FileExists(string path);

    /// <summary>Deletes the given file.</summary>
    /// <param name="path">Absolute file path.</param>
    void DeleteFile(string path);

    /// <summary>Opens (creating or truncating) a writable stream for the given file.</summary>
    /// <param name="path">Absolute file path.</param>
    Stream OpenWrite(string path);

    /// <summary>
    /// Flushes any buffered bytes of a stream returned by <see cref="OpenWrite"/> to durable storage.
    /// Surfaces late write failures (disk-full, quota, I/O) that would otherwise only appear during
    /// disposal, so the caller can reject the save before committing a record.
    /// </summary>
    /// <param name="destination">Destination stream returned by <see cref="OpenWrite"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task FlushAsync(Stream destination, CancellationToken ct);

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> in bounded chunks,
    /// never writing more than <paramref name="maxBytes"/> bytes and never reading meaningfully
    /// past the bound. Never buffers the whole source in memory.
    /// </summary>
    /// <param name="source">Source stream supplied by the caller.</param>
    /// <param name="destination">Destination stream returned by <see cref="OpenWrite"/>.</param>
    /// <param name="maxBytes">Inclusive upper bound on the number of bytes that may be written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the copy completed within the bound; <c>false</c> when the source exceeded it.</returns>
    Task<bool> CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct);
}

/// <summary>
/// Real <see cref="IAttachmentFileSystem"/> used by the production constructor.
/// </summary>
internal sealed class AttachmentFileSystem : IAttachmentFileSystem
{
    private const int BufferSize = 81920;

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

    /// <inheritdoc />
    public Task FlushAsync(Stream destination, CancellationToken ct) => destination.FlushAsync(ct);

    /// <inheritdoc />
    public Task<bool> CopyBoundedAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct) =>
        CopyBoundedCoreAsync(source, destination, maxBytes, ct);

    /// <summary>
    /// The single bounded-copy algorithm, shared so tests exercise the production implementation
    /// rather than a re-implementation.
    /// </summary>
    /// <remarks>
    /// Invariant: the number of bytes WRITTEN never exceeds <paramref name="maxBytes"/>. Each
    /// iteration reads at most the remaining allowance, so an oversize source is never consumed a
    /// whole chunk past the cap. Once exactly <paramref name="maxBytes"/> bytes are written, a
    /// single 1-byte probe distinguishes "exactly at the cap" (success) from "over the cap"
    /// (rejection).
    /// </remarks>
    /// <param name="source">Source stream supplied by the caller.</param>
    /// <param name="destination">Destination stream to write into.</param>
    /// <param name="maxBytes">Inclusive upper bound on the number of bytes that may be written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the copy completed within the bound; <c>false</c> when the source exceeded it.</returns>
    internal static async Task<bool> CopyBoundedCoreAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long written = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = maxBytes - written;
            if (remaining <= 0)
            {
                // At the cap: a single 1-byte probe tells us whether the source has more data.
                var probe = await source.ReadAsync(buffer.AsMemory(0, 1), ct);
                return probe == 0; // 0 → exactly at the cap (success); >0 → oversize.
            }

            var request = (int)Math.Min(BufferSize, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, request), ct);
            if (read == 0)
                return true; // Stream ended within the bound.

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
        }
    }
}

/// <summary>
/// Singleton store for Composer chat attachments.
/// </summary>
/// <remarks>
/// <para>
/// EVERY operation acquires one <see cref="SemaphoreSlim"/> gate for its ENTIRE duration —
/// including the streamed <see cref="SaveAsync"/> copy. This is correct-by-construction at
/// single-actor scale: the Composer chat has one user and an upload is a sub-second file copy.
/// There is deliberately no staging, no commit-during-clear, no snapshots, no generations and
/// no signal-waiter protocol.
/// </para>
/// <para>
/// Caller input is never trusted: operations take a <see cref="ComposerAttachment"/> but look it
/// up by <see cref="ComposerAttachment.Id"/>. The STORED record is authoritative for
/// <see cref="ComposerAttachment.SavedRelativePath"/> and <see cref="ComposerAttachment.State"/>,
/// so a fabricated path carrying a valid id cannot redirect a delete.
/// </para>
/// </remarks>
public sealed class ComposerAttachmentService
{
    /// <summary>Mirrors SharpCoder 0.16.0 internal ImageLoader.MaxTotalBytes.</summary>
    internal const long MaxAttachmentBytes = 20_971_520;

    private const string OversizeError = "attachment exceeds 20 MiB";
    private const string UnknownError = "unknown attachment";
    private const string InvalidPathError = "invalid stored path";

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ComposerAttachment> _records = new(StringComparer.Ordinal);
    private readonly IAttachmentFileSystem _fs;
    private readonly ILogger<ComposerAttachmentService> _logger;

    /// <summary>
    /// Initialises the service against the real file system, creating the attachments root.
    /// </summary>
    /// <param name="stateDir">Runtime state directory; the root is <c>{stateDir}/composer-attachments</c>.</param>
    /// <param name="logger">Logger used for stray-file and cleanup-failure diagnostics.</param>
    public ComposerAttachmentService(string stateDir, ILogger<ComposerAttachmentService> logger)
        : this(stateDir, logger, new AttachmentFileSystem(), seed: null)
    {
    }

    /// <summary>
    /// Test-only constructor: a narrow internal seam for portable IO-failure, oversize,
    /// cancellation and fault simulation plus an optional seed of retained records
    /// (used by the malicious-stored-path containment tests).
    /// </summary>
    /// <param name="stateDir">Runtime state directory; the root is <c>{stateDir}/composer-attachments</c>.</param>
    /// <param name="logger">Logger used for stray-file and cleanup-failure diagnostics.</param>
    /// <param name="fs">File-system seam implementation.</param>
    /// <param name="seed">Optional pre-existing records to retain.</param>
    internal ComposerAttachmentService(
        string stateDir,
        ILogger<ComposerAttachmentService> logger,
        IAttachmentFileSystem fs,
        IReadOnlyDictionary<string, ComposerAttachment>? seed = null)
    {
        ArgumentNullException.ThrowIfNull(stateDir);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fs);

        _logger = logger;
        _fs = fs;
        AttachmentsRootPath = Path.GetFullPath(Path.Combine(stateDir, "composer-attachments"));
        _fs.CreateDirectory(AttachmentsRootPath);

        if (seed is null)
            return;

        foreach (var (id, attachment) in seed)
            _records[id] = attachment;
    }

    /// <summary>
    /// Canonical absolute path of the directory holding every saved attachment.
    /// This is the single source of truth for consumers resolving
    /// <see cref="ComposerAttachment.SavedRelativePath"/>.
    /// </summary>
    public string AttachmentsRootPath { get; }

    /// <summary>
    /// Validates and streams <paramref name="content"/> into a newly generated file under
    /// <see cref="AttachmentsRootPath"/>, retaining a <see cref="AttachmentState.Pending"/> record.
    /// </summary>
    /// <param name="originalName">Original file name supplied by the user; retained unchanged as the display name.</param>
    /// <param name="content">Stream to copy; never fully buffered.</param>
    /// <param name="ct">Cancellation token; cancelling deletes the partial file and throws.</param>
    /// <returns>The saved attachment, or a failure result with an error message.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<AttachmentSaveResult> SaveAsync(
        string originalName, Stream content, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await SaveCoreAsync(originalName, content, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes a <see cref="AttachmentState.Pending"/> attachment and deletes its file.
    /// Sent attachments are refused and unknown ids report an error.
    /// </summary>
    /// <param name="attachment">Attachment to remove; only its <see cref="ComposerAttachment.Id"/> is trusted.</param>
    /// <returns>Success, or a failure result describing why the attachment was kept.</returns>
    public AttachmentResult Remove(ComposerAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        _gate.Wait();
        try
        {
            if (!_records.TryGetValue(attachment.Id, out var stored))
                return new AttachmentResult(false, UnknownError);

            if (stored.State == AttachmentState.Sent)
                return new AttachmentResult(false, "cannot remove a sent attachment");

            if (!TryResolveStoredPath(stored.SavedRelativePath, out var fullPath))
                return new AttachmentResult(false, InvalidPathError);

            try
            {
                DeleteIfExists(fullPath);
            }
            catch (Exception ex)
            {
                // Not swallowed: the record is retained so state still matches the file system.
                return new AttachmentResult(false, ex.Message);
            }

            _records.Remove(stored.Id);
            return new AttachmentResult(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically replaces a <see cref="AttachmentState.Pending"/> attachment. The old record is
    /// validated COMPLETELY (existence, pending state, generated flat form and canonical
    /// containment) before any new file is created, so a rejected old record can never leave a
    /// half-committed replacement behind. A failure at any step leaves exactly one attachment
    /// retained.
    /// </summary>
    /// <param name="old">Attachment being replaced; only its <see cref="ComposerAttachment.Id"/> is trusted.</param>
    /// <param name="originalName">Original file name of the new content.</param>
    /// <param name="content">Stream to copy; never fully buffered.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new attachment, or a failure result with an error message.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<AttachmentSaveResult> ReplaceAsync(
        ComposerAttachment old, string originalName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(old);

        await _gate.WaitAsync(ct);
        try
        {
            // ALL old-record validation happens first — nothing is saved when the target is invalid.
            if (!_records.TryGetValue(old.Id, out var stored))
                return new AttachmentSaveResult(false, null, UnknownError);

            if (stored.State == AttachmentState.Sent)
                return new AttachmentSaveResult(false, null, "cannot replace a sent attachment");

            // Resolved BEFORE the save so a bad stored path cannot strand a committed new file.
            if (!TryResolveStoredPath(stored.SavedRelativePath, out var oldPath))
                return new AttachmentSaveResult(false, null, InvalidPathError);

            var saved = await SaveCoreAsync(originalName, content, ct);
            if (!saved.Success || saved.Attachment is null)
                return saved; // Old attachment is untouched.

            var newAttachment = saved.Attachment;

            try
            {
                DeleteIfExists(oldPath);
            }
            catch (Exception ex)
            {
                return RollbackNew(newAttachment, ex.Message);
            }

            _records.Remove(stored.Id);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks an attachment as <see cref="AttachmentState.Sent"/>. Idempotent for attachments that
    /// are already sent.
    /// </summary>
    /// <param name="attachment">Attachment to mark; only its <see cref="ComposerAttachment.Id"/> is trusted.</param>
    /// <returns>Success, or a failure result when the id is unknown.</returns>
    public AttachmentResult MarkAsSent(ComposerAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        _gate.Wait();
        try
        {
            if (!_records.TryGetValue(attachment.Id, out var stored))
                return new AttachmentResult(false, UnknownError);

            if (stored.State == AttachmentState.Sent)
                return new AttachmentResult(true, null); // Idempotent.

            _records[stored.Id] = stored with { State = AttachmentState.Sent };
            return new AttachmentResult(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes every retained attachment file. The gate is held for the whole operation, so this
    /// can never interleave with another operation. Individual failures are counted and their
    /// records are retained so the in-memory state keeps matching the file system.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token. Cancelling while awaiting the gate deletes nothing; once the gate is
    /// held, cancellation is checked between files and un-deleted records are retained.
    /// </param>
    /// <returns>Success when everything was deleted, otherwise a failure result with the failure count.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public async Task<AttachmentResult> ClearAllAsync(CancellationToken ct = default)
    {
        // Cancellation while awaiting the gate stops here and deletes nothing.
        await _gate.WaitAsync(ct);
        try
        {
            var failures = 0;
            foreach (var stored in _records.Values.ToList())
            {
                // Checked BETWEEN files: un-deleted records are retained.
                ct.ThrowIfCancellationRequested();

                if (!TryResolveStoredPath(stored.SavedRelativePath, out var fullPath))
                {
                    // Counted as a failure, record retained, and we continue to the next file.
                    failures++;
                    _logger.LogError(
                        "Attachment {Id} has a stored path that is not in the generated contained form and was not deleted: {Path}",
                        stored.Id, stored.SavedRelativePath);
                    continue;
                }

                try
                {
                    DeleteIfExists(fullPath);
                }
                catch (Exception ex)
                {
                    // Continue past individual failures; retain the record.
                    failures++;
                    _logger.LogError(ex, "Failed to delete attachment file {Path}", fullPath);
                    continue;
                }

                _records.Remove(stored.Id);
            }

            return failures > 0
                ? new AttachmentResult(false, $"{failures} file(s) could not be deleted")
                : new AttachmentResult(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Internals (all callers already hold the gate) ────────────────────────

    /// <summary>
    /// Validates the name, streams the content into a generated flat file and retains a pending record.
    /// The caller must hold the gate.
    /// </summary>
    private async Task<AttachmentSaveResult> SaveCoreAsync(
        string originalName, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validationError = ValidateName(originalName, out var extension);
        if (validationError is not null)
            return new AttachmentSaveResult(false, null, validationError);

        var id = Guid.NewGuid().ToString("N");
        var fileName = id + extension;
        var fullPath = Path.Combine(AttachmentsRootPath, fileName);

        try
        {
            _fs.CreateDirectory(AttachmentsRootPath);

            bool withinLimit;
            var copyAndFlushSucceeded = false;
            var destination = _fs.OpenWrite(fullPath);            try
            {
                withinLimit = await _fs.CopyBoundedAsync(content, destination, MaxAttachmentBytes, ct);

                // Flush BEFORE the record is committed: disk-full/quota/I/O failures surface while
                // draining buffered bytes, and must fail the save rather than record a Pending
                // attachment pointing at a truncated file.
                if (withinLimit)
                {
                    await _fs.FlushAsync(destination, ct);
                    copyAndFlushSucceeded = true;
                }
            }
            catch
            {
                // An earlier copy/flush/cancel failure is the primary outcome; the close is then
                // only best-effort cleanup so it cannot mask the real error.
                await SafeDisposeAsync(destination, fullPath);
                throw;
            }

            if (!copyAndFlushSucceeded)
            {
                // Oversize is an earlier (copy-stage) outcome and wins over any close fault.
                await SafeDisposeAsync(destination, fullPath);
                TryDeletePartial(fullPath);
                return new AttachmentSaveResult(false, null, OversizeError);
            }

            // Copy and flush both succeeded, so the close is authoritative: a stream that fails
            // while closing may still have failed to persist its bytes. The fault is NOT
            // swallowed — it propagates to the handler below, which deletes the partial file and
            // returns a failure, so no Pending record is ever committed for it.
            await destination.DisposeAsync();

            var attachment = new ComposerAttachment(id, originalName, fileName, AttachmentState.Pending);
            _records[id] = attachment;
            return new AttachmentSaveResult(true, attachment, null);
        }
        catch (OperationCanceledException)
        {
            // Primary outcome preserved: a failing partial-delete only logs the stray file.
            TryDeletePartial(fullPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeletePartial(fullPath);
            return new AttachmentSaveResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Undoes a freshly saved attachment after the old file could not be dropped: the new file and
    /// record are removed and the old record is kept. A rollback that itself fails leaves a logged
    /// stray file rather than a silently ignored one.
    /// </summary>
    private AttachmentSaveResult RollbackNew(ComposerAttachment newAttachment, string error)
    {
        var newPath = Path.Combine(AttachmentsRootPath, newAttachment.SavedRelativePath);
        try
        {
            DeleteIfExists(newPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Rollback failed: stray attachment file left on disk at {Path}. Original error: {Error}",
                newPath, error);
        }

        _records.Remove(newAttachment.Id);
        return new AttachmentSaveResult(false, null, error);
    }

    /// <summary>Deletes a partially written file, logging (but never surfacing) a secondary failure.</summary>
    private void TryDeletePartial(string fullPath)
    {
        try
        {
            DeleteIfExists(fullPath);
        }
        catch (Exception ex)
        {
            // Secondary cleanup failure: preserve the primary outcome, log the stray file.
            _logger.LogError(ex, "Stray partial attachment file left on disk at {Path}", fullPath);
        }
    }

    private void DeleteIfExists(string fullPath)
    {
        if (_fs.FileExists(fullPath))
            _fs.DeleteFile(fullPath);
    }

    /// <summary>
    /// Closes a stream on an ALREADY-FAILED path, where the primary outcome (an oversize copy, an
    /// I/O fault or a cancellation) is determined and must not be masked. Only ever used for
    /// cleanup — the success path awaits <see cref="Stream.DisposeAsync"/> directly so a close
    /// failure surfaces as a save failure.
    /// </summary>
    private async Task SafeDisposeAsync(Stream stream, string fullPath)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close the attachment stream for {Path}", fullPath);
        }
    }

    /// <summary>
    /// Validates a caller-supplied file name and yields its lower-cased allowlisted extension.
    /// </summary>
    /// <returns><c>null</c> when the name is acceptable; otherwise the rejection reason.</returns>
    private static string? ValidateName(string originalName, out string extension)
    {
        extension = string.Empty;

        if (string.IsNullOrWhiteSpace(originalName))
            return "attachment name is required";

        if (originalName.Contains("..", StringComparison.Ordinal)
            || originalName.Contains('/')
            || originalName.Contains('\\'))
        {
            return "attachment name contains an invalid path segment";
        }

        if (originalName.Any(char.IsControl))
            return "attachment name contains control characters";

        var ext = Path.GetExtension(originalName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return $"attachment type '{ext}' is not allowed";

        extension = ext.ToLowerInvariant();
        return null;
    }

    /// <summary>
    /// Validates that a STORED relative path is exactly in the generated form
    /// <c>&lt;guid&gt;.&lt;lowercase-allowed-ext&gt;</c>: no directory separators, no <c>..</c>, no
    /// control characters, exactly one dot, a GUID-parsable stem and a lower-case allowlisted
    /// extension. Anything else — including otherwise-innocent names such as <c>real.png</c> or an
    /// upper-cased extension — is rejected, so a stored value can only ever name a file this
    /// service itself generated.
    /// </summary>
    /// <param name="savedRelativePath">The stored relative path to validate.</param>
    /// <returns><c>true</c> when the value is in the generated form.</returns>
    private static bool ValidateStoredPathForm(string savedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(savedRelativePath))
            return false;

        if (savedRelativePath.Contains("..", StringComparison.Ordinal)
            || savedRelativePath.Contains('/')
            || savedRelativePath.Contains('\\')
            || savedRelativePath.Any(char.IsControl))
        {
            return false;
        }

        // Exactly one dot separating the stem from the extension.
        var dot = savedRelativePath.IndexOf('.');
        if (dot <= 0 || dot != savedRelativePath.LastIndexOf('.') || dot == savedRelativePath.Length - 1)
            return false;

        var stem = savedRelativePath[..dot];
        var extension = savedRelativePath[dot..];

        // Extension must be allowlisted AND already lower-cased (the generated form).
        if (!AllowedExtensions.Contains(extension)
            || !string.Equals(extension, extension.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParse(stem, out _);
    }

    /// <summary>
    /// Resolves a STORED relative path to an absolute path. The value is accepted only when it is
    /// in the generated flat form AND its canonical path resolves inside the root. Lexical
    /// containment only — symlink resolution is out of scope.
    /// </summary>
    /// <remarks>
    /// Never throws for a malformed or platform-invalid value: canonicalization failures are
    /// reported as <c>false</c> so callers can return a result / retain a record / continue past
    /// the entry rather than propagating an exception mid-operation.
    /// </remarks>
    private bool TryResolveStoredPath(string savedRelativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (!ValidateStoredPathForm(savedRelativePath))
            return false;

        try
        {
            // Canonical containment: the resolved path must stay inside the root.
            var candidate = Path.GetFullPath(Path.Combine(AttachmentsRootPath, savedRelativePath));
            var rootPrefix = AttachmentsRootPath.EndsWith(Path.DirectorySeparatorChar)
                ? AttachmentsRootPath
                : AttachmentsRootPath + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
                return false;

            fullPath = candidate;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A platform-invalid stored value is rejected, never propagated.
            _logger.LogError(ex, "Failed to canonicalize stored attachment path {Path}", savedRelativePath);
            fullPath = string.Empty;
            return false;
        }
    }
}
