using System.Net;
using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;
using Vestara.Transport;
using Xunit;

namespace Vestara.Tests;

public class IngestionAckTests
{
    private static (HttpUploader Uploader, EventQueue Queue, MockHttpMessageHandler Handler) CreateUploader(
        TempDirectory temp,
        string token = "token123")
    {
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "testkey");
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var uploader = new HttpUploader(httpClient, queue, storage, token, new Uri("https://api.vestara.dev"));
        return (uploader, queue, handler);
    }

    [Fact]
    public async Task StrictFullAck_RemovesEventsFromQueue()
    {
        using var temp = new TempDirectory();
        var (uploader, queue, handler) = CreateUploader(temp);

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(new VestaraEvent { EventType = "log" });
        }

        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":5,\"rejected\":0}");

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.True(success);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Fact]
    public async Task PartialAck_RejectedGreaterThanZero_RetainsBatchAndClearsInFlight()
    {
        using var temp = new TempDirectory();
        var (uploader, queue, handler) = CreateUploader(temp);

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(new VestaraEvent { EventType = "log" });
        }

        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":3,\"rejected\":2}");

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.False(success);
        Assert.Equal(5, queue.Count);
        Assert.Equal(0, queue.InFlightCount); // Cleared in flight state
    }

    [Fact]
    public async Task PartialAck_AcceptedLessThanCount_RetainsBatch()
    {
        using var temp = new TempDirectory();
        var (uploader, queue, handler) = CreateUploader(temp);

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(new VestaraEvent { EventType = "log" });
        }

        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":4,\"rejected\":0}");

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.False(success);
        Assert.Equal(5, queue.Count);
        Assert.Equal(0, queue.InFlightCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]           // 400
    [InlineData(HttpStatusCode.Unauthorized)]         // 401
    [InlineData(HttpStatusCode.Forbidden)]            // 403
    [InlineData(HttpStatusCode.NotFound)]             // 404
    [InlineData(HttpStatusCode.RequestEntityTooLarge)] // 413
    [InlineData(HttpStatusCode.UnprocessableEntity)]   // 422
    public async Task PoisonResponses_RemoveExactPoisonBatchAndResetBackoff(HttpStatusCode poisonCode)
    {
        using var temp = new TempDirectory();
        var (uploader, queue, handler) = CreateUploader(temp);

        for (int i = 0; i < 3; i++)
        {
            queue.Enqueue(new VestaraEvent { EventType = "log" });
        }

        handler.EnqueueResponse(poisonCode, "{\"error\":\"poison_batch\"}");

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.False(success);
        Assert.Equal(0, queue.Count); // Poison batch removed so subsequent events are not blocked
        Assert.Equal(0, uploader.ConsecutiveFailures); // Backoff reset
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)] // 500
    [InlineData(HttpStatusCode.BadGateway)]          // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]  // 503
    public async Task ServerErrors_RetainBatchForRetry(HttpStatusCode serverErrorCode)
    {
        using var temp = new TempDirectory();
        var (uploader, queue, handler) = CreateUploader(temp);

        for (int i = 0; i < 4; i++)
        {
            queue.Enqueue(new VestaraEvent { EventType = "log" });
        }

        handler.EnqueueResponse(serverErrorCode, "{\"error\":\"server_error\"}");

        var success = await uploader.FlushBatchAsync(ignoreBackoff: true);

        Assert.False(success);
        Assert.Equal(4, queue.Count);
        Assert.Equal(0, queue.InFlightCount);
        Assert.Equal(1, uploader.ConsecutiveFailures);
    }
}
