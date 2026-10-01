using FlashSale.Infrastructure.Queueing;

namespace FlashSale.Tests;

/// <summary>
/// Unit tests for the in-memory waiting room. No database required.
/// </summary>
public sealed class InMemoryQueueStoreTests
{
    /// <summary>Test fixture: a store, one sale id, and a connection-id factory.</summary>
    private sealed class Harness(InMemoryQueueStore store, Guid eventId)
    {
        public InMemoryQueueStore Store { get; } = store;
        public Guid EventId { get; } = eventId;
        public string Connection(string user) => $"conn-{user}";
    }

    private static Harness Build()
    {
        return new Harness(new InMemoryQueueStore(), Guid.NewGuid());
    }

    [Fact]
    public void Enqueue_AssignsSequentialPositionsStartingAtOne()
    {
        var h = Build();

        var users = Enumerable.Range(1, 5).Select(i => Guid.NewGuid()).ToArray();
        foreach (var user in users)
        {
            h.Store.Enqueue(h.EventId, user, h.Connection(user.ToString()), DateTimeOffset.UtcNow);
        }

        Assert.Equal(5, h.Store.Count(h.EventId));
        Assert.Equal(1, h.Store.PositionOf(h.EventId, users[0]));
        Assert.Equal(5, h.Store.PositionOf(h.EventId, users[4]));
    }

    [Fact]
    public void Enqueue_SameUserTwice_KeepsOriginalPosition()
    {
        // Guards against a buyer skipping the queue by reconnecting.
        var h = Build();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        h.Store.Enqueue(h.EventId, first, h.Connection("a"), DateTimeOffset.UtcNow);
        h.Store.Enqueue(h.EventId, second, h.Connection("b"), DateTimeOffset.UtcNow);

        // Same user rejoins on a new socket.
        h.Store.Enqueue(h.EventId, first, h.Connection("a2"), DateTimeOffset.UtcNow);

        Assert.Equal(1, h.Store.PositionOf(h.EventId, first));
        Assert.Equal(2, h.Store.PositionOf(h.EventId, second));
        Assert.Equal(2, h.Store.Count(h.EventId));
    }

    [Fact]
    public void DequeueBatch_ReturnsFrontOfLineInOrder_AndRemovesThem()
    {
        var h = Build();
        var users = Enumerable.Range(1, 6).Select(_ => Guid.NewGuid()).ToArray();

        foreach (var user in users)
        {
            h.Store.Enqueue(h.EventId, user, h.Connection(user.ToString()), DateTimeOffset.UtcNow);
        }

        var admitted = h.Store.DequeueBatch(h.EventId, 3);

        Assert.Equal(3, admitted.Count);
        Assert.Equal(users[0], admitted[0].UserId);
        Assert.Equal(users[2], admitted[2].UserId);

        // Everyone left keeps a contiguous position renumbered from 1.
        Assert.Equal(3, h.Store.Count(h.EventId));
        Assert.Equal(1, h.Store.PositionOf(h.EventId, users[3]));
        Assert.Null(h.Store.PositionOf(h.EventId, users[0]));
    }

    [Fact]
    public void DequeueBatch_RespectsRequestedSize_WhenQueueIsShorter()
    {
        var h = Build();
        h.Store.Enqueue(h.EventId, Guid.NewGuid(), h.Connection("a"), DateTimeOffset.UtcNow);

        var admitted = h.Store.DequeueBatch(h.EventId, 50);

        Assert.Single(admitted);
        Assert.Equal(0, h.Store.Count(h.EventId));
    }

    [Fact]
    public void Queues_AreIsolatedPerEvent()
    {
        var h = Build();
        var eventA = Guid.NewGuid();
        var eventB = Guid.NewGuid();
        var user = Guid.NewGuid();

        h.Store.Enqueue(eventA, user, h.Connection("a"), DateTimeOffset.UtcNow);

        Assert.Equal(1, h.Store.Count(eventA));
        Assert.Equal(0, h.Store.Count(eventB));
        Assert.Null(h.Store.PositionOf(eventB, user));
    }

    [Fact]
    public async Task ConcurrentEnqueues_ProduceUniqueOrderedPositions()
    {
        // The store is shared by the hub and the worker across many threads.
        var h = Build();
        var users = Enumerable.Range(1, 200).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(users.Select(user => Task.Run(() =>
            h.Store.Enqueue(h.EventId, user, h.Connection(user.ToString()), DateTimeOffset.UtcNow))));

        Assert.Equal(200, h.Store.Count(h.EventId));

        // Every position must be distinct and cover 1..200 exactly.
        var positions = users.Select(u => h.Store.PositionOf(h.EventId, u)!.Value).OrderBy(p => p).ToArray();
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (int?)i).ToArray(),
                     positions.Select(p => (int?)p).ToArray());
    }
}