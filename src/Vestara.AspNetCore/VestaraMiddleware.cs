using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vestara.Context;
using Vestara.Models;

namespace Vestara.AspNetCore;

/// <summary>
/// ASP.NET Core middleware providing structural request and failure evidence for Vestara.
/// </summary>
public sealed class VestaraMiddleware
{
    private static readonly object VestaraStateItemKey = new();

    private readonly RequestDelegate _next;
    private readonly VestaraClient _client;
    private readonly IVestaraCorrelationAccessor _correlationAccessor;

    public VestaraMiddleware(
        RequestDelegate next,
        VestaraClient client,
        IVestaraCorrelationAccessor correlationAccessor)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _correlationAccessor = correlationAccessor ?? throw new ArgumentNullException(nameof(correlationAccessor));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Re-execution handling (e.g. UseExceptionHandler("/error") re-executing pipeline on same HttpContext)
        if (context.Items.TryGetValue(VestaraStateItemKey, out var existing) && existing is VestaraRequestState existingState)
        {
            using var reentrantScope = _correlationAccessor.BeginRequestScope(existingState.RequestId);
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _client.CaptureException(ex);
                throw;
            }
            return;
        }

        // First invocation
        var requestId = $"req_{Guid.NewGuid():N}";
        var state = new VestaraRequestState(requestId);
        context.Items[VestaraStateItemKey] = state;

        context.Response.OnCompleted(() =>
        {
            state.Stopwatch.Stop();
            var statusCode = context.Response.StatusCode;

            var completionData = new Dictionary<string, object?>
            {
                ["statusCode"] = statusCode,
                ["operationName"] = "backend_request",
                ["stage"] = "complete",
                ["requestId"] = state.RequestId,
                ["durationMs"] = state.Stopwatch.ElapsedMilliseconds
            };

            if (!string.IsNullOrEmpty(state.RouteTemplate))
            {
                completionData["url"] = state.RouteTemplate;
            }

            _client.Log(
                "info",
                "HTTP request completed",
                completionData,
                EmissionOrigin.StructuralEvidence
            );

            return Task.CompletedTask;
        });

        using var scope = _correlationAccessor.BeginRequestScope(requestId);

        _client.Log(
            "info",
            "HTTP request started",
            new Dictionary<string, object?>
            {
                ["method"] = context.Request.Method,
                ["operationName"] = "backend_request",
                ["stage"] = "start",
                ["requestId"] = requestId
            },
            EmissionOrigin.StructuralEvidence
        );

        try
        {
            await _next(context).ConfigureAwait(false);
            state.CaptureInitialRouteTemplate(context);
        }
        catch (Exception ex)
        {
            state.CaptureInitialRouteTemplate(context);
            _client.CaptureException(ex);
            throw;
        }
    }

    private sealed class VestaraRequestState
    {
        public string RequestId { get; }
        public Stopwatch Stopwatch { get; }
        public string? RouteTemplate { get; private set; }
        private bool _routeCaptured;

        public VestaraRequestState(string requestId)
        {
            RequestId = requestId;
            Stopwatch = Stopwatch.StartNew();
        }

        public void CaptureInitialRouteTemplate(HttpContext context)
        {
            if (_routeCaptured)
            {
                return;
            }

            _routeCaptured = true;
            var rawText = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern?.RawText;
            if (!string.IsNullOrWhiteSpace(rawText))
            {
                RouteTemplate = rawText;
            }
        }
    }
}
