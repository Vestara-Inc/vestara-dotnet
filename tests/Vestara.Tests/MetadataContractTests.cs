using Vestara.Models;
using Xunit;

namespace Vestara.Tests;

public class MetadataContractTests
{
    [Fact]
    public void EmittedEvent_MatchesExactFrozenMetadataContract()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            Environment = "staging",
            AppVersion = "1.2.3",
            ServiceName = "payments-worker",
            AppIdentifier = "app-payments",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.Log("info", "metadata validation");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var evt = events[0].Event;

        // Exact frozen wire values
        Assert.Equal("dotnet", evt.OsName);
        Assert.Equal("sdk-dotnet", evt.SdkName);
        Assert.Equal("0.1.0", evt.SdkVersion);
        Assert.Equal("dotnet_service", evt.TargetCategory);

        // Runtime and environment metadata
        Assert.Equal("staging", evt.Environment);
        Assert.Equal("1.2.3", evt.AppVersion);
        Assert.Equal("payments-worker", evt.ServiceName);
        Assert.Equal("app-payments", evt.AppIdentifier);
        Assert.False(string.IsNullOrWhiteSpace(evt.Runtime));
        Assert.Contains(".NET", evt.Runtime);
        Assert.False(string.IsNullOrWhiteSpace(evt.SessionId));
        Assert.False(string.IsNullOrWhiteSpace(evt.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(evt.Timestamp));
    }
}
