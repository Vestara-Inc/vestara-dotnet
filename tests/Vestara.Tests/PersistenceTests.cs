using Vestara.Models;
using Vestara.Persistence;
using Xunit;

namespace Vestara.Tests;

public class PersistenceTests
{
    [Fact]
    public void StorageKey_IsDeterministic_AndTokenAbsentFromFilename()
    {
        var token = "vestara_sec_1234567890abcdef";
        var appIdentifier = "my-order-service";

        var key1 = StorageKeyResolver.ComputeKey(token, appIdentifier);
        var key2 = StorageKeyResolver.ComputeKey(token, appIdentifier);

        Assert.Equal(key1, key2);
        Assert.Equal(16, key1.Length);
        Assert.DoesNotContain(token, key1);

        using var temp = new TempDirectory();
        var storage = new FileEventStorage(temp.Path, key1);

        var events = new List<QueuedEvent>
        {
            new() { QueueId = "q1", Event = new VestaraEvent { EventType = "log" } }
        };
        storage.SaveEvents(events, revision: 0);

        var files = Directory.GetFiles(temp.Path);
        Assert.Single(files);
        var fileName = Path.GetFileName(files[0]);
        Assert.Equal($"vestara-queue-{key1}.json", fileName);
        Assert.DoesNotContain(token, fileName);
    }

    [Fact]
    public void SaveAndLoad_PreservesQueueId_AndResetsIsInFlightToFalse()
    {
        using var temp = new TempDirectory();
        var key = "testkey001";
        var storage = new FileEventStorage(temp.Path, key);

        var original = new List<QueuedEvent>
        {
            new() { QueueId = "q_abc", Event = new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["msg"] = "hello" } }, IsInFlight = true },
            new() { QueueId = "q_def", Event = new VestaraEvent { EventType = "error", Payload = new Dictionary<string, object?> { ["err"] = "fail" } }, IsInFlight = true }
        };

        storage.SaveEvents(original, revision: 0);

        var loaded = storage.LoadEvents();
        Assert.Equal(2, loaded.Count);

        Assert.Equal("q_abc", loaded[0].QueueId);
        Assert.False(loaded[0].IsInFlight);
        Assert.Equal("hello", loaded[0].Event.Payload["msg"]!.ToString());

        Assert.Equal("q_def", loaded[1].QueueId);
        Assert.False(loaded[1].IsInFlight);
        Assert.Equal("fail", loaded[1].Event.Payload["err"]!.ToString());
    }

    [Fact]
    public void FatalSave_ExecutesFlushToDiskPath()
    {
        using var temp = new TempDirectory();
        var key = "testkey_fatal";
        var storage = new FileEventStorage(temp.Path, key);

        var fatalEvents = new List<QueuedEvent>
        {
            new() { QueueId = "q_crash", Event = new VestaraEvent { EventType = "crash", Origin = EmissionOrigin.Crash } }
        };

        // isFatal: true triggers FileStream.Flush(flushToDisk: true)
        storage.SaveEvents(fatalEvents, revision: 0, isFatal: true);

        var loaded = storage.LoadEvents();
        Assert.Single(loaded);
        Assert.Equal("q_crash", loaded[0].QueueId);
    }

    [Fact]
    public void CorruptStorage_QuarantinesFile_AndReturnsEmptyListWithoutCrashing()
    {
        using var temp = new TempDirectory();
        var key = "testkey_corrupt";
        var storage = new FileEventStorage(temp.Path, key);

        var targetFile = Path.Combine(temp.Path, $"vestara-queue-{key}.json");
        File.WriteAllText(targetFile, "{ invalid json corrupt content !! [}");

        var loaded = storage.LoadEvents();
        Assert.Empty(loaded);

        // Target file should have been quarantined or cleared
        Assert.False(File.Exists(targetFile));
        var files = Directory.GetFiles(temp.Path);
        Assert.Contains(files, f => f.Contains(".corrupt."));
    }

    [Fact]
    public void MemoryOnlyFallback_WhenDirectoryUnwritable_OperatesCleanlyWithoutCrashing()
    {
        // Pass an invalid / inaccessible directory
        var storage = new FileEventStorage("/invalid/directory/that/cannot/exist/null", "key123");
        Assert.False(storage.IsPersistent);

        // Saving should be a no-op and never throw
        storage.SaveEvents(new List<QueuedEvent>
        {
            new() { QueueId = "q1", Event = new VestaraEvent { EventType = "log" } }
        }, revision: 0);

        var loaded = storage.LoadEvents();
        Assert.Empty(loaded);
    }
}
