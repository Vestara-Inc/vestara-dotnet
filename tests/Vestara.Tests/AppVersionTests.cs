using System.Text.Json;
using Vestara.Models;
using Xunit;

namespace Vestara.Tests;

public class AppVersionTests
{
    [Fact]
    public void ExplicitAppVersion_Wins()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            AppVersion = "2.4.1",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.Log("info", "test");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.Equal("2.4.1", events[0].Event.AppVersion);
    }

    [Fact]
    public void OmittedAppVersion_DerivesNonEmptyVersion()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            AppVersion = null,
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.Log("info", "test");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.False(string.IsNullOrWhiteSpace(events[0].Event.AppVersion));
    }

    [Fact]
    public void EmittedJson_NeverContainsNullAppVersion()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            AppVersion = null,
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.Log("info", "test");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var json = JsonSerializer.Serialize(events[0].Event);

        Assert.DoesNotContain("\"app_version\":null", json);
        Assert.Contains("\"app_version\":", json);
    }
}
