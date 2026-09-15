using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vestara.Logging;

public sealed class VestaraLoggerProvider : ILoggerProvider
{
    private readonly VestaraClient _client;
    private readonly ConcurrentDictionary<string, VestaraLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public VestaraLoggerProvider(VestaraClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new VestaraLogger(name, _client));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _loggers.Clear();
            _disposed = true;
        }
    }
}
