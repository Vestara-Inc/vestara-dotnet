namespace Vestara.Context;

/// <summary>
/// Provides access to the current Vestara request correlation identifier and manages request-scoped lifecycles.
/// </summary>
public interface IVestaraCorrelationAccessor
{
    /// <summary>
    /// Gets the active Vestara request correlation ID, or null if outside an active request scope.
    /// </summary>
    string? CurrentRequestId { get; }

    /// <summary>
    /// Begins a scoped request context and returns an <see cref="IDisposable"/> that restores the prior context on disposal.
    /// </summary>
    /// <param name="requestId">The Vestara request correlation ID.</param>
    IDisposable BeginRequestScope(string requestId);
}
