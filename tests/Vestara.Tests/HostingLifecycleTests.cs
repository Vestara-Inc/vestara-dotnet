using Vestara.Diagnostics;
using Vestara.Hosting;
using Vestara.Models;
using Vestara.Persistence;
using Xunit;

namespace Vestara.Tests;

[Collection("ProcessWideDiagnostics")]
public class HostingLifecycleTests
{
    [Fact]
    public async Task HostedService_RecoversQueueOnStart_AndPersistsOnStop()
    {
        using var temp = new TempDirectory();
        var storageKey = StorageKeyResolver.ComputeKey("test_token_123", null);
        var storage = new FileEventStorage(temp.Path, storageKey);

        // Pre-populate storage with an un-flushed event from a prior run
        storage.SaveEvents(new List<QueuedEvent>
        {
            new() { QueueId = "recovered_1", Event = new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["msg"] = "old" } } }
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
        var hostedService = new VestaraHostedService(client);

        // Start hosted service -> recovers queue
        await hostedService.StartAsync(CancellationToken.None);

        Assert.Equal(1, client.Queue.Count);
        Assert.Equal("recovered_1", client.Queue.GetAllForPersistence()[0].QueueId);

        // Enqueue another item
        client.Log("info", "new_event");
        Assert.Equal(2, client.Queue.Count);

        // Stop hosted service -> persists remaining events
        await hostedService.StopAsync(CancellationToken.None);

        var loadedAfterStop = storage.LoadEvents();
        Assert.Equal(2, loadedAfterStop.Count);
    }

    [Fact]
    public void UnhandledExceptionHandler_RegistrationIsIdempotent_AndDisposesCleanly()
    {
        int captureCount = 0;
        var handler = new UnhandledExceptionHandler((ex, isFatal) => captureCount++);

        // Multiple calls to Register are idempotent
        handler.Register();
        handler.Register();

        // Unregister is idempotent
        handler.Unregister();
        handler.Unregister();

        handler.Dispose();
        handler.Dispose();
    }
}
