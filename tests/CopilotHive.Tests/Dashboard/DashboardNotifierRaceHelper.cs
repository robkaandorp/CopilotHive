using CopilotHive.Dashboard;

namespace CopilotHive.Tests.Dashboard;

/// <summary>
/// Shared helper that genuinely races <see cref="DashboardNotifier.NotifyStateChanged"/>
/// against concurrent unsubscribe/resubscribe of a handler.
/// </summary>
internal static class DashboardNotifierRaceHelper
{
    /// <summary>
    /// Runs a real two-thread race using a <see cref="Barrier"/>.
    /// Thread 1 hammers <see cref="DashboardNotifier.NotifyStateChanged"/>; the calling thread
    /// concurrently unsubscribes and resubscribes a handler.
    /// <para>
    /// A second, always-throwing subscriber is registered so that removing the
    /// <c>GetInvocationList()</c> snapshot + per-handler try/catch (replacing it with a plain
    /// <c>OnStateChanged?.Invoke()</c>) makes the exception escape the notifier — this method
    /// then rethrows it and the test fails.
    /// </para>
    /// Returns <c>true</c> when at least one captured handler still executed after it had been
    /// logically unsubscribed, proving snapshot semantics.
    /// </summary>
    public static bool RaceUnsubscribe()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            var notifier = new DashboardNotifier();
            var subscribed = 1;
            var ranWhileUnsubscribed = 0;

            void Handler()
            {
                if (Volatile.Read(ref subscribed) == 0)
                    Interlocked.Increment(ref ranWhileUnsubscribed);
            }

            // Always-throwing subscriber: guarantees the test fails if per-handler
            // exception isolation (the snapshot loop) is removed.
            notifier.OnStateChanged += () => throw new InvalidOperationException("boom");
            notifier.OnStateChanged += Handler;

            using var barrier = new Barrier(2);
            Exception? captured = null;

            var notifyThread = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        for (var i = 0; i < 1000; i++)
                            notifier.NotifyStateChanged();
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                },
                TaskCreationOptions.LongRunning);

            barrier.SignalAndWait();
            for (var i = 0; i < 1000; i++)
            {
                Volatile.Write(ref subscribed, 0);
                notifier.OnStateChanged -= Handler;
                notifier.OnStateChanged += Handler;
                Volatile.Write(ref subscribed, 1);
            }

            notifyThread.GetAwaiter().GetResult();

            if (captured is not null)
                throw captured;

            if (Volatile.Read(ref ranWhileUnsubscribed) > 0)
                return true;
        }

        return false;
    }
}
