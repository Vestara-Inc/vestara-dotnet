namespace Vestara.Context;

internal sealed class VestaraCorrelationAccessor : IVestaraCorrelationAccessor
{
    private static readonly AsyncLocal<string?> CurrentRequestIdLocal = new();

    public string? CurrentRequestId => CurrentRequestIdLocal.Value;

    public IDisposable BeginRequestScope(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var previous = CurrentRequestIdLocal.Value;
        CurrentRequestIdLocal.Value = requestId;
        return new RequestScope(previous);
    }

    private sealed class RequestScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public RequestScope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CurrentRequestIdLocal.Value = _previous;
                _disposed = true;
            }
        }
    }
}
