using Vestara.Models;
using Vestara.Queue;
using Xunit;

namespace Vestara.Tests;

public class EventQueueTests
{
    [Fact]
    public void Queue_Enforces500Capacity()
    {
        var queue = new EventQueue();

        for (int i = 0; i < 550; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Origin = EmissionOrigin.OrdinaryLog,
                Payload = new Dictionary<string, object?> { ["i"] = i }
            });
        }

        Assert.Equal(500, queue.Count);
    }

    [Fact]
    public void CrashPriority_EvictsOldestNonCrashFirst()
    {
        var queue = new EventQueue();

        // Enqueue 500 ordinary events
        for (int i = 0; i < 500; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Origin = EmissionOrigin.OrdinaryLog,
                Payload = new Dictionary<string, object?> { ["seq"] = i }
            });
        }

        // Enqueue 1 crash event
        var crashResult = queue.Enqueue(new VestaraEvent
        {
            EventType = "crash",
            Origin = EmissionOrigin.Crash,
            Payload = new Dictionary<string, object?> { ["crash"] = true }
        });

        Assert.NotNull(crashResult);
        Assert.Equal(500, queue.Count);

        // First item seq=0 should have been evicted; seq=1 should be oldest remaining
        var all = queue.GetAllForPersistence();
        Assert.Equal(1, (int)all[0].Event.Payload["seq"]!);
        Assert.True((bool)all[^1].Event.Payload["crash"]!);
    }

    [Fact]
    public void OrdinaryLog_NeverEvictsCrash()
    {
        var queue = new EventQueue();

        // Fill queue with 500 crash events
        for (int i = 0; i < 500; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "crash",
                Origin = EmissionOrigin.Crash,
                Payload = new Dictionary<string, object?> { ["crash_id"] = i }
            });
        }

        // Try to enqueue an ordinary log event
        var logResult = queue.Enqueue(new VestaraEvent
        {
            EventType = "log",
            Origin = EmissionOrigin.OrdinaryLog,
            Payload = new Dictionary<string, object?> { ["msg"] = "dropped" }
        });

        // The ordinary log must be dropped because all 500 items are crashes
        Assert.Null(logResult);
        Assert.Equal(500, queue.Count);

        var all = queue.GetAllForPersistence();
        Assert.All(all, item => Assert.Equal("crash", item.Event.EventType));
    }

    [Fact]
    public void InFlightItems_AreProtectedFromEviction()
    {
        var queue = new EventQueue();

        // Enqueue 100 items and mark them in flight
        for (int i = 0; i < 100; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Origin = EmissionOrigin.OrdinaryLog,
                Payload = new Dictionary<string, object?> { ["id"] = i }
            });
        }

        var inFlightBatch = queue.GetBatch(maxBatchSize: 100);
        Assert.Equal(100, inFlightBatch.Count);
        Assert.All(inFlightBatch, item => Assert.True(item.IsInFlight));

        // Enqueue 400 more items (total 500)
        for (int i = 100; i < 500; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Origin = EmissionOrigin.OrdinaryLog,
                Payload = new Dictionary<string, object?> { ["id"] = i }
            });
        }

        Assert.Equal(500, queue.Count);

        // Enqueue 50 more items, which causes 50 evictions
        for (int i = 500; i < 550; i++)
        {
            queue.Enqueue(new VestaraEvent
            {
                EventType = "log",
                Origin = EmissionOrigin.OrdinaryLog,
                Payload = new Dictionary<string, object?> { ["id"] = i }
            });
        }

        Assert.Equal(500, queue.Count);

        // The 100 in-flight items must still be present and in-flight!
        var all = queue.GetAllForPersistence();
        var inFlightRemaining = all.Where(i => i.IsInFlight).ToList();
        Assert.Equal(100, inFlightRemaining.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Contains(inFlightRemaining, item => (int)item.Event.Payload["id"]! == i);
        }
    }
}
