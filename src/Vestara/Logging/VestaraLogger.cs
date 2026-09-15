using System.Collections;
using Microsoft.Extensions.Logging;
using Vestara.Models;

namespace Vestara.Logging;

public sealed class VestaraLogger : ILogger
{
    private static readonly AsyncLocal<bool> IsLogging = new();
    private readonly string _categoryName;
    private readonly VestaraClient _client;

    public VestaraLogger(string categoryName, VestaraClient client)
    {
        _categoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        return _client.DeviceSettings.ShouldEmit(EmissionOrigin.OrdinaryLog);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        // Reentrancy guard: prevent infinite recursion if Vestara internals log
        if (IsLogging.Value)
        {
            return;
        }

        IsLogging.Value = true;
        try
        {
            string message;
            try
            {
                message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            }
            catch
            {
                message = "[FormatterFailed]";
            }

            var mappedLevel = MapLogLevel(logLevel);

            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["logger"] = _categoryName
            };

            if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
            {
                data["eventId"] = eventId.Id;
                if (!string.IsNullOrWhiteSpace(eventId.Name))
                {
                    data["eventName"] = eventId.Name;
                }
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> keyValues)
            {
                try
                {
                    var sanitized = LogStateSanitizer.SanitizeState(keyValues);
                    foreach (var kvp in sanitized)
                    {
                        data[kvp.Key] = kvp.Value;
                    }
                }
                catch
                {
                    data["state"] = "[UnrepresentableObject]";
                }
            }

            if (exception != null)
            {
                try
                {
                    data["exceptionType"] = exception.GetType().FullName;
                    data["exceptionMessage"] = exception.Message;
                    data["exceptionStack"] = exception.StackTrace;
                }
                catch
                {
                    data["exception"] = "[UnrepresentableException]";
                }
            }

            _client.Log(mappedLevel, message, data, EmissionOrigin.OrdinaryLog);
        }
        catch
        {
            // Fail-safe: ILogger.Log must never throw out to caller
        }
        finally
        {
            IsLogging.Value = false;
        }
    }

    private static string MapLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "fatal",
        _ => "info"
    };
}
