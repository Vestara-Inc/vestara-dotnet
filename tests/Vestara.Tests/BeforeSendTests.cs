using Vestara.Models;
using Xunit;

namespace Vestara.Tests;

public class BeforeSendTests
{
    [Fact]
    public void BeforeSend_CanMutateEvent()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                evt.Payload["custom_tag"] = "injected";
                return evt;
            }
        };

        using var client = new VestaraClient(options);
        client.Log("info", "test message");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.Equal("injected", events[0].Event.Payload["custom_tag"]!.ToString());
    }

    [Fact]
    public void BeforeSend_CanDropEvent_ByReturningNull()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                if (evt.Payload.TryGetValue("message", out var msg) && msg?.ToString() == "drop_me")
                {
                    return null;
                }
                return evt;
            }
        };

        using var client = new VestaraClient(options);
        client.Log("info", "drop_me");
        client.Log("info", "keep_me");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.Equal("keep_me", events[0].Event.Payload["message"]!.ToString());
    }

    [Fact]
    public void BeforeSend_WhenThrows_PreservesOriginalUnmodifiedEvent()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false,
            BeforeSend = evt =>
            {
                evt.Payload["message"] = "corrupted_before_throw";
                throw new InvalidOperationException("User code explosion!");
            }
        };

        using var client = new VestaraClient(options);

        // Should not crash the host and must preserve original event payload
        client.Log("info", "original_message");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.Equal("original_message", events[0].Event.Payload["message"]!.ToString());
    }
}
