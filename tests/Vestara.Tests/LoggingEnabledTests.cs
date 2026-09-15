using Microsoft.Extensions.Logging;
using Vestara.Logging;
using Vestara.Models;
using Xunit;

namespace Vestara.Tests;

public class LoggingEnabledTests
{
    [Fact]
    public void LoggingEnabled_False_SuppressesOrdinaryLogs_AndRetainsCriticalTelemetry()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        var loggerProvider = new VestaraLoggerProvider(client);
        var logger = loggerProvider.CreateLogger("TestCategory");

        // Explicitly disable remote logging via device settings
        client.DeviceSettings.UpdateSettingsDirectly(loggingEnabled: false);

        // 1. Ordinary manual log must be suppressed
        client.Log("info", "suppressed_manual_log");
        Assert.Equal(0, client.Queue.Count);

        // 2. ILogger ordinary log must be suppressed
        logger.LogInformation("suppressed_ilogger_info");
        Assert.Equal(0, client.Queue.Count);

        // 3. Breadcrumbs must still be captured
        client.AddBreadcrumb("navigation_step", "ui");
        Assert.Equal(1, client.Breadcrumbs.Count);

        // 4. CaptureException must STILL be captured
        client.CaptureException(new InvalidOperationException("handled_error"));
        Assert.Equal(1, client.Queue.Count);
        var exEvent = client.Queue.GetAllForPersistence()[0];
        Assert.Equal("log", exEvent.Event.EventType);
        Assert.Equal(EmissionOrigin.Exception, exEvent.Event.Origin);
        Assert.Equal("error", exEvent.Event.Payload["level"]?.ToString());

        // 5. Fatal crash must STILL be captured
        client.CaptureException(new AccessViolationException("fatal_crash"), isFatal: true);
        Assert.Equal(2, client.Queue.Count);
        var crashEvent = client.Queue.GetAllForPersistence()[1];
        Assert.Equal("crash", crashEvent.Event.EventType);
        Assert.Equal(EmissionOrigin.Crash, crashEvent.Event.Origin);
    }
}
