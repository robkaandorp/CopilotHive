using System.Runtime.ExceptionServices;

namespace CopilotHive.Tests.Services;

/// <summary>Owns the gates and operations started by a circuit observation test.</summary>
internal sealed class CircuitObservationTestWork(TimeSpan timeout)
{
    private readonly object _gate = new();
    private readonly List<Task> _tasks = [];
    private readonly List<Action> _releases = [];
    private bool _closed;

    internal T Track<T>(T task) where T : Task
    {
        lock (_gate)
        {
            ThrowIfClosed();
            _tasks.Add(task);
        }
        return task;
    }

    internal T Start<T>(Func<T> start, Func<T, Task>? enclosing = null) where T : Task
    {
        lock (_gate)
        {
            ThrowIfClosed();
            var started = start();
            _tasks.Add(enclosing is null ? started : enclosing(started));
            return started;
        }
    }

    internal void ReleaseBeforeJoin(Action release)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            _releases.Add(release);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new InvalidOperationException("Circuit test work admission is closed.");
    }

    internal async Task RunAsync(Func<Task> scenario, params Func<Task>[] dispose)
    {
        Exception? primary = null;
        try { await scenario(); }
        catch (Exception ex) { primary = ex; }
        finally { await CompleteAsync(primary, dispose); }
    }

    internal async Task CompleteAsync(Exception? primary, params Func<Task>[] dispose)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }

        var failures = new List<Exception>();
        void Record(Exception ex)
        {
            if (!ReferenceEquals(ex, primary) && !failures.Contains(ex)) failures.Add(ex);
        }

        async Task JoinAsync(Func<Task> start)
        {
            try { await start().WaitAsync(timeout, CancellationToken.None); }
            catch (Exception ex) { Record(ex); }
        }

        foreach (var release in _releases)
        {
            try { release(); }
            catch (Exception ex) { Record(ex); }
        }
        foreach (var task in _tasks) await JoinAsync(() => task);
        foreach (var step in dispose) await JoinAsync(step);

        if (failures.Count != 0)
        {
            if (primary is not null) failures.Insert(0, primary);
            throw new AggregateException("Circuit observation scenario or cleanup failed.", failures);
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
