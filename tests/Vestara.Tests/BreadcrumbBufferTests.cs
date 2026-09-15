using Vestara.Models;
using Vestara.Queue;
using Xunit;

namespace Vestara.Tests;

public class BreadcrumbBufferTests
{
    [Fact]
    public void BreadcrumbBuffer_Enforces100Cap_AndEvictsOldest()
    {
        var buffer = new BreadcrumbBuffer();

        for (int i = 0; i < 150; i++)
        {
            buffer.Add(new Breadcrumb
            {
                Message = $"breadcrumb_{i}",
                Category = "test"
            });
        }

        Assert.Equal(100, buffer.Count);

        var snapshot = buffer.Snapshot();
        Assert.Equal(100, snapshot.Count);

        // Oldest 50 evicted: first item in snapshot must be index 50, last index 149
        Assert.Equal("breadcrumb_50", snapshot[0].Message);
        Assert.Equal("breadcrumb_149", snapshot[^1].Message);
    }

    [Fact]
    public void CrashSnapshot_ReceivesLatest100Breadcrumbs()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);

        for (int i = 0; i < 120; i++)
        {
            client.AddBreadcrumb($"step_{i}");
        }

        client.CaptureException(new Exception("fatal_event"), isFatal: true);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var crash = events[0].Event;

        var breadcrumbs = Assert.IsAssignableFrom<List<Breadcrumb>>(crash.Payload["breadcrumbs"]);
        Assert.Equal(100, breadcrumbs.Count);
        Assert.Equal("step_20", breadcrumbs[0].Message);
        Assert.Equal("step_119", breadcrumbs[^1].Message);
    }
}
