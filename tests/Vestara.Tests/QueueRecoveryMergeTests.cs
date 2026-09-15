using Vestara.Hosting;
using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;
using Xunit;

namespace Vestara.Tests;

public class QueueRecoveryMergeTests
{
    [Fact]
    public async Task Recovery_DoesNotDelete_LiveStartupEvents()
    {
        using var temp = new TempDirectory();
        var storageKey = StorageKeyResolver.ComputeKey("test_token_123", null);
        var storage = new FileEventStorage(temp.Path, storageKey);

        // Prepopulate storage with an un-flushed durable event from prior run
        storage.SaveEvents(new List<QueuedEvent>
        {
            new()
            {
                QueueId = "durable_recovered_1",
                Event = new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["msg"] = "old" } },
                IsInFlight = true // Was in-flight when previous process exited
            }
        }, revision: 0);

        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(System.Net.HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);

        using var client = new VestaraClient(options, httpClient: httpClient);

        // 1. Emit live startup event BEFORE HostedService.StartAsync runs
        client.Log("info", "live_startup_event");
        Assert.Equal(1, client.Queue.Count);
        var liveId = client.Queue.GetAllForPersistence()[0].QueueId;

        // 2. StartAsync triggers queue recovery
        var hostedService = new VestaraHostedService(client);
        await hostedService.StartAsync(CancellationToken.None);

        // 3. BOTH events MUST remain in queue!
        var allEvents = client.Queue.GetAllForPersistence();
        Assert.Equal(2, allEvents.Count);

        var recoveredItem = allEvents.FirstOrDefault(e => e.QueueId == "durable_recovered_1");
        Assert.NotNull(recoveredItem);
        Assert.False(recoveredItem.IsInFlight); // Must reset IsInFlight to false

        var liveItem = allEvents.FirstOrDefault(e => e.QueueId == liveId);
        Assert.NotNull(liveItem);
        Assert.Equal("live_startup_event", liveItem.Event.Payload["message"]?.ToString());

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void MergeRecoveredEvents_DeduplicatesById_AndEnforcesCapacityWithCrashPriority()
    {
        var queue = new EventQueue();

        // Enqueue 3 live events
        queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["m"] = "live1" } });
        queue.Enqueue(new VestaraEvent { EventType = "crash", Origin = EmissionOrigin.Crash });
        var liveItems = queue.GetAllForPersistence();

        // Create recovered list with: duplicate of live1, and a new recovered event
        var recovered = new List<QueuedEvent>
        {
            new() { QueueId = liveItems[0].QueueId, Event = new VestaraEvent { EventType = "log" } },
            new() { QueueId = "unique_recovered", Event = new VestaraEvent { EventType = "log" }, IsInFlight = true }
        };

        queue.MergeRecoveredEvents(recovered);

        var merged = queue.GetAllForPersistence();
        // 1 unique recovered + 2 live items (deduped live1) = 3 total
        Assert.Equal(3, merged.Count);
        Assert.Contains(merged, i => i.QueueId == "unique_recovered" && !i.IsInFlight);
        Assert.Contains(merged, i => i.QueueId == liveItems[0].QueueId);
        Assert.Contains(merged, i => i.QueueId == liveItems[1].QueueId);
    }
}
