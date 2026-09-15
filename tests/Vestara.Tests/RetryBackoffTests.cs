using System.Net;
using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;
using Vestara.Transport;
using Xunit;

namespace Vestara.Tests;

public class RetryBackoffTests
{
    [Fact]
    public async Task RetryBackoff_FollowsProgression_AndBlocksRoutineFlush()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "backoff_key");
        var handler = new MockHttpMessageHandler();
        var clock = new TestClock();
        var httpClient = new HttpClient(handler);
        var uploader = new HttpUploader(httpClient, queue, storage, "token", new Uri("https://api.vestara.dev"), clock);

        queue.Enqueue(new VestaraEvent { EventType = "log" });

        // Failure 1: 10s delay
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        var res1 = await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.False(res1);
        Assert.Equal(1, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(10), uploader.NextRetryAt);

        // Routine flush is blocked during backoff (force = false)
        var blockedRes = await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.False(blockedRes);
        // Handler should not have received a new request
        Assert.Single(handler.SentRequests);

        // Advance clock by 10s -> now eligible for retry
        clock.Advance(TimeSpan.FromSeconds(10));

        // Failure 2: 20s delay
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        var res2 = await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.False(res2);
        Assert.Equal(2, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(20), uploader.NextRetryAt);

        // Failure 3: 40s delay
        clock.Advance(TimeSpan.FromSeconds(20));
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.Equal(3, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(40), uploader.NextRetryAt);

        // Failure 4: 80s delay
        clock.Advance(TimeSpan.FromSeconds(40));
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.Equal(4, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(80), uploader.NextRetryAt);

        // Failure 5: 160s delay
        clock.Advance(TimeSpan.FromSeconds(80));
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.Equal(5, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(160), uploader.NextRetryAt);

        // Failure 6: clamped at 300s max
        clock.Advance(TimeSpan.FromSeconds(160));
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.Equal(6, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(300), uploader.NextRetryAt);

        // Failure 7: still clamped at 300s max
        clock.Advance(TimeSpan.FromSeconds(300));
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.Equal(7, uploader.ConsecutiveFailures);
        Assert.Equal(clock.UtcNow.AddSeconds(300), uploader.NextRetryAt);
    }

    [Fact]
    public async Task Success_ResetsBackoff()
    {
        using var temp = new TempDirectory();
        var queue = new EventQueue();
        var storage = new FileEventStorage(temp.Path, "key_reset");
        var handler = new MockHttpMessageHandler();
        var clock = new TestClock();
        var httpClient = new HttpClient(handler);
        var uploader = new HttpUploader(httpClient, queue, storage, "token", new Uri("https://api.vestara.dev"), clock);

        queue.Enqueue(new VestaraEvent { EventType = "log" });

        // 2 failures
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        clock.Advance(TimeSpan.FromSeconds(10));
        handler.EnqueueResponse(HttpStatusCode.InternalServerError);
        await uploader.FlushBatchAsync(ignoreBackoff: false);
        Assert.Equal(2, uploader.ConsecutiveFailures);

        // Success
        clock.Advance(TimeSpan.FromSeconds(20));
        handler.EnqueueResponse(HttpStatusCode.OK, "{\"accepted\":1,\"rejected\":0}");
        var success = await uploader.FlushBatchAsync(ignoreBackoff: false);

        Assert.True(success);
        Assert.Equal(0, uploader.ConsecutiveFailures);
        Assert.Equal(DateTimeOffset.MinValue, uploader.NextRetryAt);
    }
}
