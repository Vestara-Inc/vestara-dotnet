using System.Net;
using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;
using Vestara.Transport;
using Xunit;

namespace Vestara.Tests;

public class UploaderInFlightSafetyTests
{
    private class FaultyReadHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new IOException("Simulated network read explosion!");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private class ThrowingOnSerialize
    {
        public string Value => throw new InvalidOperationException("Serialization explosion!");
    }

    private class BlockingFakeHandler : HttpMessageHandler
    {
        public bool ReceivedCancellation { get; private set; }
        private readonly TaskCompletionSource<HttpResponseMessage> _tcs = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() =>
            {
                ReceivedCancellation = true;
                _tcs.TrySetCanceled(cancellationToken);
            }))
            {
                return await _tcs.Task.ConfigureAwait(false);
            }
        }
    }

    [Fact]
    public async Task UnserializableInput_CannotStrandInFlightBatch_AndAllowsRetry()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");

        var badEvent = new VestaraEvent { EventType = "log" };
        badEvent.Payload["bad"] = new ThrowingOnSerialize();
        queue.Enqueue(badEvent);

        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var uploader = new HttpUploader(client, queue, storage, "token123", new Uri("https://api.vestara.dev"));

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.False(success);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount); // Must NOT remain stranded in flight

        // Remove bad object and retry
        badEvent.Payload["bad"] = "fixed";
        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":1,\"rejected\":0}");

        var retrySuccess = await uploader.FlushBatchAsync(ignoreBackoff: true);
        Assert.True(retrySuccess);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ResponseContentReadException_ReleasesInFlightBatch_AndAllowsRetry()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");

        queue.Enqueue(new VestaraEvent { EventType = "log" });
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount);

        var handler = new MockHttpMessageHandler();
        handler.EnqueueCallback(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new FaultyReadHttpContent()
        });

        var client = new HttpClient(handler);
        var uploader = new HttpUploader(client, queue, storage, "token123", new Uri("https://api.vestara.dev"));

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.False(success);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount); // In-flight state released

        // Now enqueue valid response and retry
        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":1,\"rejected\":0}");
        var retrySuccess = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.True(retrySuccess);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task NormalUpload_EnforcesBoundedRequestBudget_UsingLinkedCancellation()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");

        queue.Enqueue(new VestaraEvent { EventType = "log" });

        var blockingHandler = new BlockingFakeHandler();
        var httpClient = new HttpClient(blockingHandler);

        var uploader = new HttpUploader(httpClient, queue, storage, "token123", new Uri("https://api.vestara.dev"))
        {
            RequestTimeout = TimeSpan.FromMilliseconds(50) // test seam: 50ms budget
        };

        var success = await uploader.FlushBatchAsync(ignoreBackoff: false, isCrashBudget: false);

        Assert.False(success);
        Assert.True(blockingHandler.ReceivedCancellation);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount); // In-flight released
    }

    [Fact]
    public async Task CrashBestEffortUpload_PropagatesCrashBudgetCancellation()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");

        queue.Enqueue(new VestaraEvent { EventType = "crash", Origin = EmissionOrigin.Crash });

        var blockingHandler = new BlockingFakeHandler();
        var httpClient = new HttpClient(blockingHandler);

        var uploader = new HttpUploader(httpClient, queue, storage, "token123", new Uri("https://api.vestara.dev"))
        {
            CrashBudgetTimeout = TimeSpan.FromMilliseconds(50) // test seam: 50ms crash budget
        };

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true, isCrashBudget: true);

        Assert.False(success);
        Assert.True(blockingHandler.ReceivedCancellation);
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount); // In-flight released
    }

    [Fact]
    public async Task FlushAllAsync_WithPoisonBatchFollowedBySuccessBatch_FlushesEntireQueue()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");

        // Enqueue 101 events (batch 1 has 100 events, batch 2 has 1 event)
        for (int i = 0; i < 101; i++)
        {
            queue.Enqueue(new VestaraEvent { EventType = "log" });
        }
        Assert.Equal(101, queue.Count);

        var handler = new MockHttpMessageHandler();
        // First batch of 100 gets 400 Bad Request (poison) -> removed from queue
        handler.EnqueueResponse(HttpStatusCode.BadRequest, "{\"error\":\"poison_batch\"}");
        // Second batch of 1 gets 200 OK -> full ACK
        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":1,\"rejected\":0}");

        var client = new HttpClient(handler);
        var uploader = new HttpUploader(client, queue, storage, "token123", new Uri("https://api.vestara.dev"));

        await uploader.FlushAllAsync(CancellationToken.None);

        // Entire queue must be cleared (poison removed, valid event sent)
        Assert.Equal(0, queue.Count);
        Assert.Equal(2, handler.SentRequests.Count);
    }

    [Fact]
    public async Task DeviceSettingsSync_EnforcesBoundedPerRequestTimeout()
    {
        var blockingHandler = new BlockingFakeHandler();
        var httpClient = new HttpClient(blockingHandler);

        var settings = new Vestara.Settings.DeviceSettingsSync(
            httpClient,
            "test_token",
            new Uri("https://api.vestara.dev"),
            "device_123")
        {
            RequestTimeout = TimeSpan.FromMilliseconds(50)
        };

        var success = await settings.PollAsync(CancellationToken.None);

        Assert.False(success);
        Assert.True(blockingHandler.ReceivedCancellation);
    }

    [Fact]
    public async Task FlushAllAsync_UsesNormalRequestBudget_NotCrashBudget()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");

        queue.Enqueue(new VestaraEvent { EventType = "log" });

        var handler = new MockHttpMessageHandler();
        // Handler completes with short 20ms delay
        handler.EnqueueCallback(_ =>
        {
            Thread.Sleep(20);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accepted\":1,\"rejected\":0}")
            };
        });

        var httpClient = new HttpClient(handler);
        var uploader = new HttpUploader(httpClient, queue, storage, "token123", new Uri("https://api.vestara.dev"))
        {
            // Set crash budget to very tiny 5ms (which would fail if used)
            CrashBudgetTimeout = TimeSpan.FromMilliseconds(5),
            RequestTimeout = TimeSpan.FromSeconds(5)
        };

        // FlushAllAsync must use normal request budget, so it will succeed without hitting the 5ms crash budget
        await uploader.FlushAllAsync(CancellationToken.None);

        Assert.Equal(0, queue.Count);
        Assert.Single(handler.SentRequests);
    }

    [Fact]
    public async Task CrashBudget_IncludesGateAcquisitionWait_TimesOutCleanlyWithoutLeakingInFlight()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "crash_gate_testkey");

        // Event 1 (for normal upload)
        queue.Enqueue(new VestaraEvent { EventType = "log", Payload = new Dictionary<string, object?> { ["msg"] = "normal" } });

        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount);

        var handler = new ControllableBlockingHandler();
        var httpClient = new HttpClient(handler);
        var uploader = new HttpUploader(httpClient, queue, storage, "token123", new Uri("https://api.vestara.dev"))
        {
            CrashBudgetTimeout = TimeSpan.FromMilliseconds(50) // 50ms test crash budget
        };

        // 1. First uploader call obtains _concurrencyGate and blocks inside fake HTTP handler
        var firstUploadTask = uploader.FlushBatchAsync(ignoreBackoff: true, isCrashBudget: false);
        await handler.WaitForRequestStartedAsync();

        // Gate is held by first call; event 1 is in-flight
        Assert.Equal(1, handler.TotalRequestsSent);
        Assert.Equal(1, queue.InFlightCount);

        // Enqueue Event 2 (fatal crash) while first upload is still in flight
        queue.Enqueue(new VestaraEvent { EventType = "crash", Origin = EmissionOrigin.Crash, Payload = new Dictionary<string, object?> { ["msg"] = "fatal" } });
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.InFlightCount);

        // 2. Start second call with isCrashBudget: true
        // It must wait for the gate, exceed its 50ms crash budget, and return false cleanly
        var secondUploadSuccess = await uploader.FlushBatchAsync(ignoreBackoff: true, isCrashBudget: true);

        Assert.False(secondUploadSuccess);

        // 3. Second call must NEVER reach SendAsync
        Assert.Equal(1, handler.TotalRequestsSent);

        // 4. Second call must NOT mark any additional event IsInFlight (remains 1 from first call)
        Assert.Equal(1, queue.InFlightCount);

        // 5. Release first request cleanly
        handler.CompleteRequest(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"accepted\":1,\"rejected\":0}", System.Text.Encoding.UTF8, "application/json")
        });
        var firstUploadSuccess = await firstUploadTask;
        Assert.True(firstUploadSuccess);

        // Queue now has only event 2 remaining, and 0 items in-flight
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, queue.InFlightCount);
    }

    private class ControllableBlockingHandler : HttpMessageHandler
    {
        public int TotalRequestsSent => _totalRequestsSent;
        private int _totalRequestsSent;
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _responseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForRequestStartedAsync() => _requestStarted.Task;

        public void CompleteRequest(HttpResponseMessage response) => _responseTcs.TrySetResult(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalRequestsSent);
            _requestStarted.TrySetResult();
            return await _responseTcs.Task.ConfigureAwait(false);
        }
    }
}
