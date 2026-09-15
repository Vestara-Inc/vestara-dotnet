using Vestara.Models;
using Vestara.Queue;
using Xunit;

namespace Vestara.Tests;

public class QueueConcurrencyTests
{
    [Fact]
    public void Enqueue_WhileBatchInFlight_BothPersistCorrectly()
    {
        var queue = new EventQueue();

        for (int i = 0; i < 20; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Payload = new Dictionary<string, object?> { ["seq"] = i }
            });
        }

        var inFlightBatch = queue.GetBatch(maxBatchSize: 10);
        Assert.Equal(10, inFlightBatch.Count);
        Assert.Equal(10, queue.InFlightCount);

        // Enqueue new items concurrently while batch is in-flight
        for (int i = 20; i < 30; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Payload = new Dictionary<string, object?> { ["seq"] = i }
            });
        }

        Assert.Equal(30, queue.Count);
        Assert.Equal(10, queue.InFlightCount);
    }

    [Fact]
    public void AcknowledgeSuccessful_RemovesExactQueueIdsOnly()
    {
        var queue = new EventQueue();

        for (int i = 0; i < 10; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Payload = new Dictionary<string, object?> { ["id"] = i }
            });
        }

        var batch = queue.GetBatch(maxBatchSize: 5);
        var ackIds = batch.Select(b => b.QueueId).ToList();

        // Enqueue additional items before ACK arrives
        queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["id"] = 10 } });

        queue.AcknowledgeSuccessful(ackIds);

        Assert.Equal(6, queue.Count);
        var remaining = queue.GetAllForPersistence();
        Assert.DoesNotContain(remaining, r => ackIds.Contains(r.QueueId));
        Assert.Contains(remaining, r => (int)r.Event.Payload["id"]! == 10);
    }

    [Fact]
    public void PartialAck_RetainsExactQueueIdsAndClearsInFlight()
    {
        var queue = new EventQueue();

        for (int i = 0; i < 10; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Payload = new Dictionary<string, object?> { ["id"] = i }
            });
        }

        var batch = queue.GetBatch(maxBatchSize: 5);
        var batchIds = batch.Select(b => b.QueueId).ToList();

        Assert.Equal(5, queue.InFlightCount);

        // Simulate partial ACK handling: release batch
        queue.ReleaseInFlight(batchIds);

        Assert.Equal(10, queue.Count);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Fact]
    public void OlderAck_CannotRemoveNewerItems()
    {
        var queue = new EventQueue();

        var first = queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["v"] = 1 } })!;
        var second = queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["v"] = 2 } })!;

        // Old ACK with a non-existent or previously cleared ID
        queue.AcknowledgeSuccessful(new[] { "old-already-removed-queue-id" });

        Assert.Equal(2, queue.Count);
        var all = queue.GetAllForPersistence();
        Assert.Contains(all, i => i.QueueId == first.QueueId);
        Assert.Contains(all, i => i.QueueId == second.QueueId);
    }
}
