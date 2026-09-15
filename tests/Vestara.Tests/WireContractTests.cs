using Vestara.Models;
using Xunit;

namespace Vestara.Tests;

public class WireContractTests
{
    private static readonly HashSet<string> BackendSupportedEventTypes = new(StringComparer.Ordinal)
    {
        "log",
        "crash"
    };

    [Fact]
    public void NonFatal_CaptureException_EmitsLog_WithLevelError()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.CaptureException(new InvalidOperationException("handled test error"), isFatal: false);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var evt = events[0].Event;

        Assert.Equal("log", evt.EventType);
        Assert.Equal(EmissionOrigin.Exception, evt.Origin);
        Assert.Equal("error", evt.Payload["level"]?.ToString());
        Assert.Contains(evt.EventType, BackendSupportedEventTypes);
    }

    [Fact]
    public void Fatal_CaptureException_EmitsCrash()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.CaptureException(new AccessViolationException("fatal test crash"), isFatal: true);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var evt = events[0].Event;

        Assert.Equal("crash", evt.EventType);
        Assert.Equal(EmissionOrigin.Crash, evt.Origin);
        Assert.Contains(evt.EventType, BackendSupportedEventTypes);
    }

    [Fact]
    public void AllEmittedEventTypes_AreBackendSupported()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.Log("info", "test info");
        client.Log("warn", "test warn");
        client.CaptureException(new Exception("test nonfatal"), isFatal: false);
        client.CaptureException(new Exception("test fatal"), isFatal: true);

        var events = client.Queue.GetAllForPersistence();
        Assert.Equal(4, events.Count);

        foreach (var q in events)
        {
            Assert.Contains(q.Event.EventType, BackendSupportedEventTypes);
        }
    }
}
