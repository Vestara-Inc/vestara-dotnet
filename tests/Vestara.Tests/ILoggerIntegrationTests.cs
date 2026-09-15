using Microsoft.Extensions.Logging;
using Vestara.Context;
using Vestara.Logging;
using Xunit;

namespace Vestara.Tests;

public class ILoggerIntegrationTests
{
    [Theory]
    [InlineData(LogLevel.Trace, "debug")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "fatal")]
    public void LogLevel_MapsCorrectly(LogLevel inputLevel, string expectedLevel)
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var provider = new VestaraLoggerProvider(client);
        var logger = provider.CreateLogger("CategoryA");

        logger.Log(inputLevel, "test level mapping");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.Equal(expectedLevel, events[0].Event.Payload["level"]!.ToString());
    }

    [Fact]
    public void StructuredState_CapturesSafeScalars_AndRedactsSensitiveKeys()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var provider = new VestaraLoggerProvider(client);
        var logger = provider.CreateLogger("OrderService");

        logger.LogInformation(
            "Processed order {OrderId} for customer {CustomerId} with token {SecretToken} and password {UserPassword}",
            12345,
            "cust_999",
            "super_secret_jwt",
            "p@ssword!");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;

        Assert.Equal(12345, Convert.ToInt32(payload["OrderId"]));
        Assert.Equal("cust_999", payload["CustomerId"]!.ToString());
        Assert.Equal("[REDACTED]", payload["SecretToken"]!.ToString());
        Assert.Equal("[REDACTED]", payload["UserPassword"]!.ToString());
    }

    [Fact]
    public void RequestId_AttachesWhenCorrelationAccessorActive()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var provider = new VestaraLoggerProvider(client);
        var logger = provider.CreateLogger("HttpHandler");

        // Outside request scope: no requestId
        logger.LogInformation("Outside request");
        var event1 = client.Queue.GetAllForPersistence()[0];
        Assert.False(event1.Event.Payload.ContainsKey("requestId"));

        // Inside request scope: requestId attaches
        using (client.CorrelationAccessor.BeginRequestScope("req_test_12345"))
        {
            logger.LogInformation("Inside request");
        }

        var event2 = client.Queue.GetAllForPersistence()[1];
        Assert.True(event2.Event.Payload.ContainsKey("requestId"));
        Assert.Equal("req_test_12345", event2.Event.Payload["requestId"]!.ToString());
    }

    [Fact]
    public void HostileStructuredState_ILoggerDoesNotThrowToCaller_AndEmitsEvent()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var provider = new VestaraLoggerProvider(client);
        var logger = provider.CreateLogger("HostileLogger");

        var hostileState = new HostileStateEnumerable();

        // Must not throw out to caller
        var ex = Record.Exception(() =>
        {
            logger.Log(LogLevel.Information, new EventId(42, "TestEvent"), hostileState, null, (state, ex) => "Safe message");
        });
        Assert.Null(ex);

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        var payload = events[0].Event.Payload;
        Assert.Equal("Safe message", payload["message"]?.ToString());
        Assert.Equal("HostileLogger", payload["logger"]?.ToString());
        Assert.Equal(42, Convert.ToInt32(payload["eventId"]));

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    private sealed class HostileStateEnumerable : IEnumerable<KeyValuePair<string, object?>>
    {
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            throw new InvalidOperationException("Hostile state enumerator failure");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
