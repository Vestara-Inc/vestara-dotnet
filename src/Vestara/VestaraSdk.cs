using Microsoft.Extensions.Logging;
using Vestara.Models;

namespace Vestara;

public static class VestaraSdk
{
    private static VestaraClient? _currentClient;
    private static readonly object _clientLock = new();

    public static VestaraClient? CurrentClient
    {
        get
        {
            lock (_clientLock)
            {
                return _currentClient;
            }
        }
    }

    public static void SetClient(VestaraClient? client)
    {
        lock (_clientLock)
        {
            _currentClient = client;
        }
    }

    public static void ClearClient(VestaraClient? client)
    {
        if (client == null)
        {
            return;
        }

        lock (_clientLock)
        {
            if (ReferenceEquals(_currentClient, client))
            {
                _currentClient = null;
            }
        }
    }

    public static void Log(
        string level,
        string message,
        IDictionary<string, object?>? data = null)
    {
        CurrentClient?.Log(level, message, data, EmissionOrigin.OrdinaryLog);
    }

    public static void Log(
        LogLevel level,
        string message,
        IDictionary<string, object?>? data = null)
    {
        var mappedLevel = level switch
        {
            LogLevel.Trace or LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => "info"
        };

        CurrentClient?.Log(mappedLevel, message, data, EmissionOrigin.OrdinaryLog);
    }

    public static void AddBreadcrumb(
        string message,
        string? category = null,
        IDictionary<string, object?>? data = null,
        string? level = null)
    {
        CurrentClient?.AddBreadcrumb(message, category, data, level);
    }

    public static void CaptureException(
        Exception exception,
        IDictionary<string, object?>? data = null)
    {
        CurrentClient?.CaptureException(exception, data, isFatal: false);
    }

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var client = CurrentClient;
        if (client != null)
        {
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
