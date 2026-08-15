using System.Collections.Concurrent;

using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for <see cref="EventBus"/> (publish, exception isolation, concurrent safety,
/// timestamp handling) and <see cref="ComposerEventSubscriber"/> (buffer/drain/clear,
/// peek, restore-to-front, MaxBufferSize overflow on enqueue and restore, dispose).
/// </summary>
public sealed class EventBusTests
{
    // ── EventBus ──

    [Fact]
    public void Publish_WithSingleSubscriber_InvokesHandlerWithExactPayload()
    {
        var bus = new EventBus();
        SystemEvent? received = null;
        bus.OnEvent += e => received = e;

        var evt = new SystemEvent(
            Type: EventType.GoalCompleted,
            Message: "Goal merged successfully",
            GoalId: "my-goal",
            IssueId: null,
            ReleaseId: null,
            Repository: "CopilotHive",
            Timestamp: new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        bus.Publish(evt);

        Assert.NotNull(received);
        Assert.Equal(EventType.GoalCompleted, received!.Type);
        Assert.Equal("Goal merged successfully", received.Message);
        Assert.Equal("my-goal", received.GoalId);
        Assert.Null(received.IssueId);
        Assert.Null(received.ReleaseId);
        Assert.Equal("CopilotHive", received.Repository);
        Assert.Equal(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc), received.Timestamp);
    }

    [Fact]
    public void Publish_WithMultipleSubscribers_AllReceiveSameEvent()
    {
        var bus = new EventBus();
        var received1 = new List<SystemEvent>();
        var received2 = new List<SystemEvent>();
        bus.OnEvent += e => received1.Add(e);
        bus.OnEvent += e => received2.Add(e);

        var evt = new SystemEvent(EventType.GoalDispatched, "dispatched", GoalId: "g-1");
        bus.Publish(evt);

        Assert.Single(received1);
        Assert.Single(received2);
        Assert.Equal(received1[0], received2[0]);
        Assert.Equal("g-1", received1[0].GoalId);
    }

    [Fact]
    public void Publish_WhenHandlerThrows_DoesNotPreventLaterHandlerAndDoesNotThrow()
    {
        var bus = new EventBus();
        var receivedByThrowing = false;
        var receivedByLater = false;

        bus.OnEvent += e =>
        {
            receivedByThrowing = true;
            throw new InvalidOperationException("handler failure");
        };
        bus.OnEvent += e =>
        {
            receivedByLater = true;
        };

        var evt = new SystemEvent(EventType.GoalCompleted, "done", GoalId: "g-1");

        // Publish must not throw despite the first handler throwing.
        var ex = Record.Exception(() => bus.Publish(evt));

        Assert.Null(ex);
        Assert.True(receivedByThrowing);
        Assert.True(receivedByLater, "A later-registered handler must receive the event even when an earlier one throws.");
    }

    [Fact]
    public void Publish_ConcurrentFromMultipleThreads_AllDeliveredExactlyOnce()
    {
        const int subscriberCount = 3;
        const int publisherCount = 4;
        const int publishesPerThread = 25;
        var bus = new EventBus();
        var delivered = new ConcurrentBag<SystemEvent>();

        for (var i = 0; i < subscriberCount; i++)
            bus.OnEvent += e => delivered.Add(e);

        var barrier = new Barrier(publisherCount);
        var threads = new Thread[publisherCount];
        for (var t = 0; t < publisherCount; t++)
        {
            var tid = t;
            threads[tid] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var p = 0; p < publishesPerThread; p++)
                    bus.Publish(new SystemEvent(
                        EventType.GoalCompleted,
                        $"msg-{tid}-{p}",
                        GoalId: $"goal-{tid}-{p}"));
            })
            { IsBackground = true };
            threads[tid].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        var expected = subscriberCount * publisherCount * publishesPerThread;
        Assert.Equal(expected, delivered.Count);

        // Verify each subscriber received every event (no lost events): the bag contains
        // subscriberCount copies of each unique GoalId. Group by GoalId and assert each
        // appears exactly subscriberCount times.
        var byGoalId = delivered.GroupBy(e => e.GoalId!).ToDictionary(g => g.Key, g => g.Count());
        var expectedUniqueGoals = publisherCount * publishesPerThread;
        Assert.Equal(expectedUniqueGoals, byGoalId.Count);
        foreach (var kvp in byGoalId)
            Assert.Equal(subscriberCount, kvp.Value);
    }

    [Fact]
    public void Publish_WithDefaultTimestamp_AutoPopulatesToRecentUtc()
    {
        var bus = new EventBus();
        SystemEvent? received = null;
        bus.OnEvent += e => received = e;

        var before = DateTime.UtcNow;
        bus.Publish(new SystemEvent(EventType.GoalCompleted, "done", GoalId: "g-1"));
        var after = DateTime.UtcNow;

        Assert.NotNull(received);
        Assert.NotEqual(default, received!.Timestamp);
        Assert.InRange(received.Timestamp, before, after);
    }

    [Fact]
    public void Publish_WithExplicitTimestamp_PreservesExactValue()
    {
        var bus = new EventBus();
        SystemEvent? received = null;
        bus.OnEvent += e => received = e;

        var fixedTs = new DateTime(2020, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        bus.Publish(new SystemEvent(EventType.GoalFailed, "old", GoalId: "g-1", Timestamp: fixedTs));

        Assert.NotNull(received);
        Assert.Equal(fixedTs, received!.Timestamp);
    }

    // ── ComposerEventSubscriber ──

    [Fact]
    public void DrainPendingEvents_AfterPublish_ReturnsEventsInFifoOrderAndClearsBuffer()
    {
        var bus = new EventBus();
        using var subscriber = new ComposerEventSubscriber(bus);

        bus.Publish(new SystemEvent(EventType.GoalCompleted, "msg-1", GoalId: "g-1"));
        bus.Publish(new SystemEvent(EventType.GoalFailed, "msg-2", GoalId: "g-2"));
        bus.Publish(new SystemEvent(EventType.GoalDispatched, "msg-3", GoalId: "g-3"));

        var drained = subscriber.DrainPendingEvents();

        Assert.Equal(3, drained.Count);
        Assert.Equal("g-1", drained[0].GoalId);
        Assert.Equal("g-2", drained[1].GoalId);
        Assert.Equal("g-3", drained[2].GoalId);

        // A second drain returns empty (buffer cleared).
        var secondDrain = subscriber.DrainPendingEvents();
        Assert.Empty(secondDrain);
    }

    [Fact]
    public void PeekPendingEvents_ReturnsCopyWithoutClearing()
    {
        var bus = new EventBus();
        using var subscriber = new ComposerEventSubscriber(bus);

        bus.Publish(new SystemEvent(EventType.GoalCompleted, "msg-1", GoalId: "g-1"));
        bus.Publish(new SystemEvent(EventType.GoalFailed, "msg-2", GoalId: "g-2"));

        var peeked = subscriber.PeekPendingEvents();

        Assert.Equal(2, peeked.Count);
        Assert.Equal("g-1", peeked[0].GoalId);
        Assert.Equal("g-2", peeked[1].GoalId);

        // Drain still returns the events (peek did not clear).
        var drained = subscriber.DrainPendingEvents();
        Assert.Equal(2, drained.Count);
        Assert.Equal("g-1", drained[0].GoalId);
    }

    [Fact]
    public void RestoreEvents_PlacesRestoredEventsAheadOfLaterArrivals()
    {
        var bus = new EventBus();
        using var subscriber = new ComposerEventSubscriber(bus);

        bus.Publish(new SystemEvent(EventType.GoalCompleted, "original-1", GoalId: "g-1"));
        bus.Publish(new SystemEvent(EventType.GoalCompleted, "original-2", GoalId: "g-2"));

        var drained = subscriber.DrainPendingEvents();
        Assert.Equal(2, drained.Count);

        // While drained, new events arrive.
        bus.Publish(new SystemEvent(EventType.GoalFailed, "later-1", GoalId: "g-3"));
        bus.Publish(new SystemEvent(EventType.GoalFailed, "later-2", GoalId: "g-4"));

        // Restore the previously-drained events; they should come BEFORE the later arrivals.
        subscriber.RestoreEvents(drained);

        var all = subscriber.DrainPendingEvents();

        Assert.Equal(4, all.Count);
        Assert.Equal("g-1", all[0].GoalId);   // restored first
        Assert.Equal("g-2", all[1].GoalId);   // restored second
        Assert.Equal("g-3", all[2].GoalId);   // later arrival
        Assert.Equal("g-4", all[3].GoalId);   // later arrival
    }

    [Fact]
    public void RestoreEvents_EventsArrivingBetweenDrainAndRestore_PreservedInOrder()
    {
        var bus = new EventBus();
        using var subscriber = new ComposerEventSubscriber(bus);

        // Publish N events, then drain them.
        for (var i = 0; i < 3; i++)
            bus.Publish(new SystemEvent(EventType.GoalCompleted, $"early-{i}", GoalId: $"early-{i}"));
        var drained = subscriber.DrainPendingEvents();
        Assert.Equal(3, drained.Count);

        // N new events arrive between drain and restore.
        for (var i = 0; i < 4; i++)
            bus.Publish(new SystemEvent(EventType.GoalFailed, $"mid-{i}", GoalId: $"mid-{i}"));

        // Restore the originally-drained events.
        subscriber.RestoreEvents(drained);

        var all = subscriber.DrainPendingEvents();

        // All 7 events should survive in order: restored first, then later arrivals.
        Assert.Equal(7, all.Count);
        Assert.Equal("early-0", all[0].GoalId);
        Assert.Equal("early-1", all[1].GoalId);
        Assert.Equal("early-2", all[2].GoalId);
        Assert.Equal("mid-0", all[3].GoalId);
        Assert.Equal("mid-1", all[4].GoalId);
        Assert.Equal("mid-2", all[5].GoalId);
        Assert.Equal("mid-3", all[6].GoalId);
    }

    [Fact]
    public void MaxBufferSize_OverflowOnEnqueue_KeepsMostRecent50()
    {
        var bus = new EventBus();
        using var subscriber = new ComposerEventSubscriber(bus);

        // Publish 60 events; only the most recent 50 should remain.
        for (var i = 0; i < 60; i++)
            bus.Publish(new SystemEvent(EventType.GoalCompleted, $"msg-{i}", GoalId: $"g-{i}"));

        var drained = subscriber.DrainPendingEvents();

        Assert.Equal(50, drained.Count);
        // The most recent 50 are events 10..59.
        Assert.Equal("g-10", drained[0].GoalId);
        Assert.Equal("g-59", drained[^1].GoalId);
    }

    [Fact]
    public void MaxBufferSize_OverflowOnRestore_CapsAt50RestoredEventsTakePriority()
    {
        var bus = new EventBus();
        using var subscriber = new ComposerEventSubscriber(bus);

        // Enqueue 30 events first (these become "existing" after drain).
        for (var i = 0; i < 30; i++)
            bus.Publish(new SystemEvent(EventType.GoalCompleted, $"early-{i}", GoalId: $"e-{i}"));
        var drained = subscriber.DrainPendingEvents();
        Assert.Equal(30, drained.Count);

        // Now enqueue 30 new events (these are the "later arrivals" in the buffer).
        for (var i = 0; i < 30; i++)
            bus.Publish(new SystemEvent(EventType.GoalFailed, $"late-{i}", GoalId: $"l-{i}"));

        // Restore the 30 drained events. Total would be 60, but cap is 50.
        // The implementation enqueues restored first, then existing, and evicts
        // from the front (oldest). So the 10 oldest restored events (e-0..e-9) are evicted.
        subscriber.RestoreEvents(drained);

        var all = subscriber.DrainPendingEvents();

        Assert.Equal(50, all.Count);
        // First 20 are the surviving restored events (e-10..e-29) in order.
        Assert.Equal("e-10", all[0].GoalId);
        Assert.Equal("e-29", all[19].GoalId);
        // Remaining 30 are all the later arrivals (l-0..l-29), which were enqueued after.
        Assert.Equal("l-0", all[20].GoalId);
        Assert.Equal("l-29", all[^1].GoalId);
    }

    [Fact]
    public void Dispose_UnsubscribesFromBus_FuturePublishesDeliverNothing()
    {
        var bus = new EventBus();
        var subscriber = new ComposerEventSubscriber(bus);

        // Publish before dispose — should be buffered.
        bus.Publish(new SystemEvent(EventType.GoalCompleted, "before", GoalId: "g-1"));
        Assert.Single(subscriber.PeekPendingEvents());

        // Drain to clear, then dispose.
        subscriber.DrainPendingEvents();
        subscriber.Dispose();

        // Publish after dispose — nothing should be buffered.
        bus.Publish(new SystemEvent(EventType.GoalCompleted, "after", GoalId: "g-2"));

        Assert.Empty(subscriber.PeekPendingEvents());
    }

    // ── Concurrency ──
    //
    // Deterministic lock-safety coverage for ComposerEventSubscriber lives in
    // ComposerEventSubscriberConcurrencyTests. The forced-overlap gate tests there park a
    // publishing thread inside the delivery pipeline while a second thread performs the
    // conflicting drain/peek/restore, then assert exact buffer contents and order.
    //
    // The previous Barrier(2) + Thread.Yield() tests that lived here were removed: they only
    // rendezvoused the threads at startup, so a legal schedule could complete every publish
    // and every drain without a single conflicting Queue<T> operation (staying green even with
    // the production lock removed), and their event counts could exceed the 50-entry
    // MaxBufferSize cap and fail CORRECT code via overflow. Cap behaviour is asserted by
    // MaxBufferSize_OverflowOnEnqueue_KeepsMostRecent50 and
    // MaxBufferSize_OverflowOnRestore_CapsAt50RestoredEventsTakePriority above.
}
