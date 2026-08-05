namespace AgentBell.Desktop.Tests;

public sealed class LruEventIdSetTests
{
    [Fact]
    public void TryAdd_At1000ItemBoundary_EvictsLeastRecentlyUsedIdentifier()
    {
        var set = new LruEventIdSet(EventPipeline.DeduplicationCapacity);
        for (var index = 0; index < EventPipeline.DeduplicationCapacity; index++)
        {
            Assert.True(set.TryAdd($"event-{index}"));
        }

        Assert.False(set.TryAdd("event-999"));
        Assert.True(set.TryAdd("event-1000"));
        Assert.True(set.TryAdd("event-0"));
        Assert.Equal(EventPipeline.DeduplicationCapacity, set.Count);
    }

    [Fact]
    public async Task TryAdd_ConcurrentSameIdentifier_AllowsExactlyOneNewItem()
    {
        var set = new LruEventIdSet(EventPipeline.DeduplicationCapacity);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => set.TryAdd("same-event"))));

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, set.Count);
    }
}
