using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;
using Xunit;

namespace Vestara.Tests;

public class PersistenceConcurrencyOrderingTests
{
    [Fact]
    public void StaleOrdinarySnapshot_CannotOverwriteNewerFatalEvent()
    {
        using var temp = new TempDirectory();
        var storageKey = "concurrency_test_key";
        var storage = new FileEventStorage(temp.Path, storageKey);
        var queue = new EventQueue();

        // 1. Enqueue ordinary log event -> revision = 1
        queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["msg"] = "old_state" } });
        var (oldSnapshot, oldRevision) = queue.GetPersistenceSnapshot();
        Assert.Equal(1, oldRevision);
        Assert.Single(oldSnapshot);

        // 2. Newer fatal crash event is enqueued -> revision = 2
        var fatalEvent = new VestaraEvent
        {
            EventType = "crash",
            Origin = EmissionOrigin.Crash,
            Payload = new Dictionary<string, object?> { ["msg"] = "fatal_crash_state" }
        };
        queue.Enqueue(fatalEvent);
        Assert.Equal(2, queue.Revision);

        // 3. Fatal path persists immediately
        queue.SnapshotAndPersist(storage, isFatal: true);
        Assert.Equal(2, storage.LastWrittenRevision);

        // Verify disk currently has the fatal crash
        var loadedAfterCrash = storage.LoadEvents();
        Assert.Equal(2, loadedAfterCrash.Count);
        Assert.Contains(loadedAfterCrash, e => e.Event.EventType == "crash");

        // 4. Stale ordinary persistence attempt from step 1 now arrives and tries to commit revision 1
        storage.SaveEvents(oldSnapshot, revision: oldRevision, isFatal: false);

        // 5. Final disk state MUST still contain the fatal crash event, stale write was dropped
        var finalLoaded = storage.LoadEvents();
        Assert.Equal(2, finalLoaded.Count);
        Assert.Contains(finalLoaded, e => e.Event.EventType == "crash");
        Assert.Equal(2, storage.LastWrittenRevision);
    }

    [Fact]
    public void EqualRevisionOrStaleRevision_RejectedMonotonically()
    {
        using var temp = new TempDirectory();
        var storage = new FileEventStorage(temp.Path, "rev_test");
        var events = new List<QueuedEvent>
        {
            new() { QueueId = "e1", Event = new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["m"] = "v1" } } }
        };

        // Write revision 5
        storage.SaveEvents(events, revision: 5);
        Assert.Equal(5, storage.LastWrittenRevision);

        // Attempt write with revision 5 again (equal revision) -> must be rejected
        var eventsEqual = new List<QueuedEvent>
        {
            new() { QueueId = "e2", Event = new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["m"] = "stale_equal" } } }
        };
        storage.SaveEvents(eventsEqual, revision: 5);
        Assert.Equal(5, storage.LastWrittenRevision);

        // Attempt write with revision 4 (older revision) -> must be rejected
        storage.SaveEvents(eventsEqual, revision: 4);
        Assert.Equal(5, storage.LastWrittenRevision);

        var loaded = storage.LoadEvents();
        Assert.Single(loaded);
        Assert.Equal("e1", loaded[0].QueueId);
    }

    [Fact]
    public void ShutdownSnapshotVsFatalOrdering_RejectsStaleShutdownPersistence()
    {
        using var temp = new TempDirectory();
        var storage = new FileEventStorage(temp.Path, "shutdown_race_key");
        var queue = new EventQueue();

        // Step 1: Queue has normal events -> revision 1
        queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["m"] = "normal" } });
        Assert.Equal(1, queue.Revision);

        // Simulated shutdown snapshot captured
        var (shutdownSnapshot, shutdownRevision) = queue.GetPersistenceSnapshot();
        Assert.Equal(1, shutdownRevision);

        // Step 2: Fatal crash occurs concurrently and increments revision -> revision 2
        queue.Enqueue(new VestaraEvent { EventType = "crash", Origin = EmissionOrigin.Crash });
        Assert.Equal(2, queue.Revision);

        // Fatal crash persists synchronously to disk
        queue.SnapshotAndPersist(storage, isFatal: true);
        Assert.Equal(2, storage.LastWrittenRevision);

        // Step 3: Delayed shutdown persistence attempt arrives with stale revision 1
        storage.SaveEvents(shutdownSnapshot, revision: shutdownRevision, isFatal: false);

        // Disk must preserve fatal state and reject the stale shutdown write
        Assert.Equal(2, storage.LastWrittenRevision);
        var loaded = storage.LoadEvents();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, e => e.Event.EventType == "crash");
    }

    [Fact]
    public void FatalCaptureException_PersistsDurableFileBeforeReturning_AndCrashEligibleForGetBatchAndAck()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_fatal_atomicity",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        using var client = new VestaraClient(options, httpClient: httpClient);

        // Enqueue normal event first
        client.Log("info", "normal log event");
        Assert.Equal(1, client.Queue.Count);

        // Capture fatal exception synchronously
        client.CaptureException(new InvalidOperationException("Fatal crash"), isFatal: true);

        // 1. Verify queue contains both events, revision advanced
        Assert.Equal(2, client.Queue.Count);
        Assert.Equal(2, client.Queue.Revision);

        // 2. Verify disk storage was durably written BEFORE returning
        var storageKey = StorageKeyResolver.ComputeKey(options.Token, options.AppIdentifier, "dotnet_service");
        var directStorage = new FileEventStorage(temp.Path, storageKey);
        var loaded = directStorage.LoadEvents();
        Assert.Equal(2, loaded.Count);
        var fatalQueued = loaded.FirstOrDefault(e => e.Event.EventType == "crash");
        Assert.NotNull(fatalQueued);
        Assert.Equal("crash", fatalQueued.Event.EventType);
        Assert.Equal("Fatal crash", fatalQueued.Event.Payload["message"]!.ToString());

        // 3. Now verify GetBatch can retrieve the fatal crash
        var batch = client.Queue.GetBatch(10);
        Assert.Equal(2, batch.Count);
        Assert.All(batch, b => Assert.True(b.IsInFlight));

        // 4. Verify exact-ACK removes the crash
        client.Queue.AcknowledgeSuccessful(batch.Select(b => b.QueueId));
        Assert.Equal(0, client.Queue.Count);
        Assert.Equal(0, client.Queue.InFlightCount);
    }
}
