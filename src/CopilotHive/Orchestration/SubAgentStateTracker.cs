using SharpCoder.SubAgents;

using System.Threading.Channels;

namespace CopilotHive.Orchestration;

/// <summary>
/// Channel-fed consumer that maintains the Composer's view of sub-agent status.
/// <para>
/// Producers (the <c>CodingAgent.SubAgentChanged</c> event, which may fire concurrently from
/// several sub-agent tasks) call <see cref="Post"/>, which is a non-blocking
/// <see cref="ChannelWriter{T}.TryWrite"/>. A single background reader drains the channel and
/// applies each message sequentially, so no lock is required — the channel provides the ordering.
/// </para>
/// <para>
/// State is published as an immutable <see cref="SubAgentSnapshot"/> holder (a reference type, so
/// it can be swapped atomically via <see cref="Volatile.Write{T}"/>). Readers use
/// <see cref="Volatile.Read{T}"/> and therefore never observe a torn or partially-built list.
/// </para>
/// <para>
/// <b>Defensive-copy contract:</b> <see cref="SubAgentInfo"/> has public setters on every property,
/// so instances handed in by SharpCoder must never be stored directly, and stored instances must
/// never be handed out. Every incoming message is cloned before it enters the snapshot, the
/// forwarded <see cref="OnSubAgentChanged"/> event carries another fresh clone, and
/// <see cref="GetSubAgents"/> clones again on the way out — subscribers and readers can never
/// mutate tracked state, and the tracker can never be mutated by whoever produced the original
/// instance.
/// </para>
/// </summary>
internal sealed class SubAgentStateTracker : IAsyncDisposable
{
    /// <summary>
    /// Maximum number of retained <i>terminal</i> entries (Completed, Failed, TimedOut and
    /// Cancelled share this budget). Running entries are never dropped and never counted.
    /// </summary>
    internal const int MaxTerminalEntries = 50;

    /// <summary>
    /// Immutable holder for a published set of sub-agent entries. A reference type on purpose:
    /// it can be swapped atomically through <see cref="Volatile.Write{T}"/>, which an
    /// <c>ImmutableArray&lt;T&gt;</c> (a struct) could not be.
    /// </summary>
    internal sealed class SubAgentSnapshot(IReadOnlyList<SubAgentInfo> entries)
    {
        /// <summary>The ordered sub-agent entries. Already defensively cloned.</summary>
        public IReadOnlyList<SubAgentInfo> Entries { get; } = entries;
    }

    private static readonly SubAgentSnapshot EmptySnapshot = new([]);

    private readonly Channel<SubAgentInfo> _channel = Channel.CreateUnbounded<SubAgentInfo>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ILogger _logger;

    private SubAgentSnapshot _snapshot = EmptySnapshot;
    private Task? _readerTask;

    /// <summary>Creates a tracker that logs consumer failures to <paramref name="logger"/>.</summary>
    /// <param name="logger">Logger used for reader-loop and subscriber failures.</param>
    public SubAgentStateTracker(ILogger logger) => _logger = logger;

    /// <summary>
    /// Raised by the reader loop <i>after</i> the snapshot has been updated, carrying a fresh clone
    /// of the entry that was just applied — never the instance stored in the snapshot.
    /// </summary>
    public event Action<SubAgentInfo>? OnSubAgentChanged;

    /// <summary>
    /// Returns the currently published, ordered sub-agent entries. Lock-free.
    /// <para>
    /// Each entry is cloned on the way out: <see cref="SubAgentInfo"/> is fully mutable, so handing
    /// out the stored instances would let a caller corrupt tracker state simply by assigning to a
    /// property of a returned item. The returned instances are therefore never reference-equal to
    /// the ones held in the snapshot.
    /// </para>
    /// </summary>
    public IReadOnlyList<SubAgentInfo> GetSubAgents()
    {
        var entries = Volatile.Read(ref _snapshot).Entries;

        var copies = new List<SubAgentInfo>(entries.Count);
        foreach (var entry in entries)
            copies.Add(CloneInfo(entry));

        return copies.AsReadOnly();
    }

    /// <summary>
    /// Enqueues a status change. Non-blocking and safe to call from any number of concurrent
    /// producers. Messages posted after teardown are silently dropped.
    /// </summary>
    /// <param name="info">The status snapshot supplied by SharpCoder.</param>
    public void Post(SubAgentInfo info)
    {
        if (info is null)
            return;

        _channel.Writer.TryWrite(info);
    }

    /// <summary>Starts the background reader loop. Idempotent.</summary>
    public Task StartAsync()
    {
        _readerTask ??= Task.Run(RunAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes the writer, lets the reader drain every already-queued message (no cancellation),
    /// then publishes an empty snapshot. Idempotent.
    /// </summary>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();

        var reader = _readerTask;
        if (reader is not null)
        {
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sub-agent state tracker reader loop failed during shutdown");
            }
        }

        Volatile.Write(ref _snapshot, EmptySnapshot);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Single-reader drain loop. Applies each message to a private dictionary keyed by
    /// <see cref="SubAgentInfo.Id"/> (upsert), re-orders, caps terminal retention, publishes the new
    /// snapshot, and only then notifies subscribers.
    /// </summary>
    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var state = new Dictionary<string, SubAgentInfo>(StringComparer.Ordinal);

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var incoming))
            {
                if (incoming is null || string.IsNullOrEmpty(incoming.Id))
                    continue;

                // Defensive copy: SubAgentInfo is fully mutable, so never store the caller's instance.
                var stored = CloneInfo(incoming);
                state[stored.Id] = stored;

                ApplyTerminalCap(state);

                Volatile.Write(ref _snapshot, new SubAgentSnapshot(BuildOrdered(state)));

                RaiseChanged(stored);
            }
        }
    }

    /// <summary>
    /// Notifies subscribers with a fresh clone. Subscriber failures are logged and swallowed so a
    /// faulty handler can never tear down the reader loop.
    /// </summary>
    private void RaiseChanged(SubAgentInfo stored)
    {
        var handler = OnSubAgentChanged;
        if (handler is null)
            return;

        try
        {
            handler(CloneInfo(stored));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sub-agent status subscriber threw for sub-agent {SubAgentId}", stored.Id);
        }
    }

    /// <summary>
    /// Trims retained terminal entries to <see cref="MaxTerminalEntries"/>, keeping the most recent
    /// ones. Total order: <c>CompletedAt</c> descending, then <c>StartedAt</c> descending, then
    /// <c>Id</c> ordinal — fully deterministic. Running entries are never dropped.
    /// </summary>
    private static void ApplyTerminalCap(Dictionary<string, SubAgentInfo> state)
    {
        var terminalCount = 0;
        foreach (var entry in state.Values)
        {
            if (IsTerminal(entry.Status))
                terminalCount++;
        }

        if (terminalCount <= MaxTerminalEntries)
            return;

        var doomed = state.Values
            .Where(e => IsTerminal(e.Status))
            .OrderByDescending(e => e.CompletedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.StartedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Skip(MaxTerminalEntries)
            .Select(e => e.Id)
            .ToList();

        foreach (var id in doomed)
            state.Remove(id);
    }

    /// <summary>
    /// Builds the display order: Running entries first (oldest first — <c>StartedAt</c> ascending,
    /// then <c>Id</c> ordinal), followed by terminal entries newest-first (<c>StartedAt</c>
    /// descending, then <c>Id</c> ordinal). Both orders are total, so the UI never flickers between
    /// equally-timed entries.
    /// </summary>
    private static IReadOnlyList<SubAgentInfo> BuildOrdered(Dictionary<string, SubAgentInfo> state)
    {
        var running = state.Values
            .Where(e => !IsTerminal(e.Status))
            .OrderBy(e => e.StartedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal);

        var terminal = state.Values
            .Where(e => IsTerminal(e.Status))
            .OrderByDescending(e => e.StartedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal);

        return running.Concat(terminal).ToList().AsReadOnly();
    }

    /// <summary>Whether the status is one of the terminal (finished) states.</summary>
    private static bool IsTerminal(SubAgentStatus status) => status is SubAgentStatus.Completed
        or SubAgentStatus.Failed
        or SubAgentStatus.TimedOut
        or SubAgentStatus.Cancelled;

    /// <summary>
    /// Copies all ten <see cref="SubAgentInfo"/> properties into a fresh instance. Required because
    /// every property has a public setter — sharing instances would let producers or subscribers
    /// mutate published state.
    /// </summary>
    private static SubAgentInfo CloneInfo(SubAgentInfo src) => new()
    {
        Id = src.Id,
        Task = src.Task,
        Status = src.Status,
        StartedAt = src.StartedAt,
        CompletedAt = src.CompletedAt,
        Model = src.Model,
        Summary = src.Summary,
        Error = src.Error,
        InputTokens = src.InputTokens,
        OutputTokens = src.OutputTokens,
    };
}
