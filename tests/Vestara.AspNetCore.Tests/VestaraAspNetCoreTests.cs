using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vestara.Context;
using Vestara.Models;
using Xunit;

namespace Vestara.AspNetCore.Tests;

public sealed class VestaraAspNetCoreTests : IDisposable
{
    private readonly string _tempDir;

    public VestaraAspNetCoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VestaraAspNetTests_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private IHostBuilder CreateHostBuilder(
        Action<IApplicationBuilder>? configureApp = null,
        Action<VestaraOptions>? configureOptions = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureLogging(logging =>
                {
                    logging.AddFilter("Microsoft", LogLevel.Warning);
                });
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddVestara(options =>
                    {
                        options.Token = "test_token_aspnet";
                        options.StorageDirectory = _tempDir;
                        options.CaptureUnhandledExceptions = false;
                        configureOptions?.Invoke(options);
                    });
                    services.AddVestaraAspNetCore();
                });
                webHost.Configure(app =>
                {
                    if (configureApp != null)
                    {
                        configureApp(app);
                    }
                    else
                    {
                        app.UseVestara();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/api/items/{id}", async context =>
                            {
                                await context.Response.WriteAsync("item-ok");
                            });
                        });
                    }
                });
            });
    }

    [Fact]
    public async Task SuccessfulRequest_EmitsStartAndCompletion_WithSameRequestId_AndNumericStatusCode()
    {
        using var host = await CreateHostBuilder().StartAsync();
        var client = host.GetTestClient();
        var vestaraClient = host.Services.GetRequiredService<VestaraClient>();

        var response = await client.GetAsync("/api/items/item_42");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queued = vestaraClient.Queue.GetAllForPersistence();
        Assert.Equal(2, queued.Count);

        var startEvt = queued[0].Event;
        Assert.Equal("log", startEvt.EventType);
        Assert.Equal(EmissionOrigin.StructuralEvidence, startEvt.Origin);
        Assert.Equal("info", startEvt.Payload["level"]?.ToString());
        Assert.Equal("HTTP request started", startEvt.Payload["message"]?.ToString());
        Assert.Equal("GET", startEvt.Payload["method"]?.ToString());
        Assert.Equal("backend_request", startEvt.Payload["operationName"]?.ToString());
        Assert.Equal("start", startEvt.Payload["stage"]?.ToString());

        var startRequestId = startEvt.Payload["requestId"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(startRequestId));

        var completeEvt = queued[1].Event;
        Assert.Equal("log", completeEvt.EventType);
        Assert.Equal(EmissionOrigin.StructuralEvidence, completeEvt.Origin);
        Assert.Equal("info", completeEvt.Payload["level"]?.ToString());
        Assert.Equal("HTTP request completed", completeEvt.Payload["message"]?.ToString());
        Assert.Equal("backend_request", completeEvt.Payload["operationName"]?.ToString());
        Assert.Equal("complete", completeEvt.Payload["stage"]?.ToString());
        Assert.Equal(startRequestId, completeEvt.Payload["requestId"]?.ToString());

        // Numeric status code check (strictly int)
        Assert.IsType<int>(completeEvt.Payload["statusCode"]);
        Assert.Equal(200, (int)completeEvt.Payload["statusCode"]!);

        // Safe route pattern check
        Assert.Equal("/api/items/{id}", completeEvt.Payload["url"]?.ToString());

        // Numeric duration check
        Assert.True(completeEvt.Payload.ContainsKey("durationMs"));
        Assert.IsType<long>(completeEvt.Payload["durationMs"]);
    }

    [Fact]
    public async Task ExceptionHandlerReExecution_PreservesSingleStart_SingleCompletion_OriginalRoute_AndSameRequestId()
    {
        using var host = await CreateHostBuilder(app =>
        {
            app.UseExceptionHandler("/error");
            app.UseVestara();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/api/items/{id}", context =>
                {
                    throw new InvalidOperationException("Endpoint failure for item");
                });
                endpoints.Map("/error", async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync("Handled error response");
                });
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var vestaraClient = host.Services.GetRequiredService<VestaraClient>();

        var response = await client.GetAsync("/api/items/fail_99");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var queued = vestaraClient.Queue.GetAllForPersistence();

        // Exactly one structural start
        var startItem = Assert.Single(queued, q => q.Event.Origin == EmissionOrigin.StructuralEvidence && Equals(q.Event.Payload["stage"], "start"));
        var startEvt = startItem.Event;
        Assert.Equal("HTTP request started", startEvt.Payload["message"]?.ToString());
        var startRequestId = startEvt.Payload["requestId"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(startRequestId));

        // Exactly one original exception capture
        var exItem = Assert.Single(queued, q => q.Event.Origin == EmissionOrigin.Exception);
        var exEvt = exItem.Event;
        Assert.Equal("log", exEvt.EventType);
        Assert.Equal("error", exEvt.Payload["level"]?.ToString());
        Assert.Equal(startRequestId, exEvt.Payload["requestId"]?.ToString());
        Assert.Contains("Endpoint failure for item", exEvt.Payload["message"]?.ToString());

        // Exactly one structural completion
        var completeItem = Assert.Single(queued, q => q.Event.Origin == EmissionOrigin.StructuralEvidence && Equals(q.Event.Payload["stage"], "complete"));
        var completeEvt = completeItem.Event;
        Assert.Equal("HTTP request completed", completeEvt.Payload["message"]?.ToString());
        Assert.Equal(startRequestId, completeEvt.Payload["requestId"]?.ToString());

        // Final status code must be numeric 500
        Assert.IsType<int>(completeEvt.Payload["statusCode"]);
        Assert.Equal(500, (int)completeEvt.Payload["statusCode"]!);

        // Route template must remain original route template, NOT /error
        Assert.Equal("/api/items/{id}", completeEvt.Payload["url"]?.ToString());
        Assert.NotEqual("/error", completeEvt.Payload["url"]?.ToString());
    }

    [Fact]
    public async Task DirectThrownException_WithoutHandler_CapturesExceptionWithRequestId_AndRethrows_WithoutCompletion()
    {
        using var host = await CreateHostBuilder(app =>
        {
            app.UseVestara();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/api/crash", context =>
                {
                    throw new InvalidOperationException("Direct unhandled crash");
                });
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var vestaraClient = host.Services.GetRequiredService<VestaraClient>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/crash"));
        Assert.Equal("Direct unhandled crash", ex.Message);

        var queued = vestaraClient.Queue.GetAllForPersistence();

        // Exactly 1 start and 1 exception capture, NO completion event manufactured
        Assert.Equal(2, queued.Count);

        var startEvt = queued[0].Event;
        Assert.Equal("HTTP request started", startEvt.Payload["message"]?.ToString());
        var startRequestId = startEvt.Payload["requestId"]?.ToString();

        var exEvt = queued[1].Event;
        Assert.Equal(EmissionOrigin.Exception, exEvt.Origin);
        Assert.Equal(startRequestId, exEvt.Payload["requestId"]?.ToString());
        Assert.Equal("Direct unhandled crash", exEvt.Payload["message"]?.ToString());

        // Ensure no completion event exists
        Assert.DoesNotContain(queued, q => q.Event.Payload.TryGetValue("stage", out var s) && Equals(s, "complete"));
    }

    [Fact]
    public async Task LoggingDisabled_PreservesStructuralEvidence()
    {
        using var host = await CreateHostBuilder().StartAsync();
        var client = host.GetTestClient();
        var vestaraClient = host.Services.GetRequiredService<VestaraClient>();

        // Disable remote ordinary logging
        vestaraClient.DeviceSettings.UpdateSettingsDirectly(false);
        Assert.False(vestaraClient.DeviceSettings.LoggingEnabled);

        var response = await client.GetAsync("/api/items/item_disabled");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queued = vestaraClient.Queue.GetAllForPersistence();
        Assert.Equal(2, queued.Count);

        Assert.Equal("HTTP request started", queued[0].Event.Payload["message"]?.ToString());
        Assert.Equal(EmissionOrigin.StructuralEvidence, queued[0].Event.Origin);

        Assert.Equal("HTTP request completed", queued[1].Event.Payload["message"]?.ToString());
        Assert.Equal(EmissionOrigin.StructuralEvidence, queued[1].Event.Origin);
    }

    [Fact]
    public async Task Privacy_NeverCapturesRawPath_QueryValues_Headers_Cookies_OrBody()
    {
        using var host = await CreateHostBuilder(app =>
        {
            app.UseVestara();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/api/users/{userId}", async context =>
                {
                    // Read request body to emulate real endpoint
                    using var reader = new StreamReader(context.Request.Body);
                    await reader.ReadToEndAsync();
                    await context.Response.WriteAsync("user-updated");
                });
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var vestaraClient = host.Services.GetRequiredService<VestaraClient>();

        var rawPathWithId = "/api/users/user_top_secret_999";
        var querySecret = "secret_token=super_secret_query_val_12345&key=secret_key_67890";
        var uri = $"{rawPathWithId}?{querySecret}";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Authorization", "Bearer top_secret_bearer_token_xyz");
        request.Headers.Add("Cookie", "session_id=top_secret_cookie_abc; tracker=123");
        request.Headers.Add("X-Sensitive-Header", "ultra_sensitive_header_val");
        request.Content = new StringContent(
            "{\"password\":\"super_secret_password_p@ss\",\"credit_card\":\"4111-2222-3333-4444\"}",
            Encoding.UTF8,
            "application/json"
        );

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var queued = vestaraClient.Queue.GetAllForPersistence();
        Assert.Equal(2, queued.Count);

        var forbiddenSubstrings = new[]
        {
            "super_secret_query_val_12345",
            "secret_key_67890",
            "top_secret_bearer_token_xyz",
            "top_secret_cookie_abc",
            "ultra_sensitive_header_val",
            "super_secret_password_p@ss",
            "4111-2222-3333-4444",
            rawPathWithId
        };

        foreach (var q in queued)
        {
            foreach (var kvp in q.Event.Payload)
            {
                var valStr = kvp.Value?.ToString() ?? string.Empty;
                foreach (var forbidden in forbiddenSubstrings)
                {
                    Assert.DoesNotContain(forbidden, kvp.Key, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(forbidden, valStr, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // Must strictly use safe route template
        var completeEvt = queued[1].Event;
        Assert.Equal("/api/users/{userId}", completeEvt.Payload["url"]?.ToString());
    }

    [Fact]
    public async Task ConcurrentRequests_MaintainIsolatedRequestIds()
    {
        using var host = await CreateHostBuilder(app =>
        {
            app.UseVestara();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/api/concurrent/{id}", async context =>
                {
                    var correlation = context.RequestServices.GetRequiredService<IVestaraCorrelationAccessor>();
                    var currentId = correlation.CurrentRequestId;
                    await Task.Delay(15);
                    await context.Response.WriteAsync(currentId ?? "missing");
                });
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var vestaraClient = host.Services.GetRequiredService<VestaraClient>();

        const int requestCount = 15;
        var tasks = Enumerable.Range(0, requestCount)
            .Select(i => client.GetStringAsync($"/api/concurrent/{i}"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Every request must see a non-empty, unique requestId inside the handler
        Assert.Equal(requestCount, results.Distinct().Count());
        Assert.DoesNotContain("missing", results);

        var queued = vestaraClient.Queue.GetAllForPersistence();
        Assert.Equal(requestCount * 2, queued.Count);

        var startIds = queued
            .Where(q => Equals(q.Event.Payload["stage"], "start"))
            .Select(q => q.Event.Payload["requestId"]?.ToString())
            .ToList();

        var completeIds = queued
            .Where(q => Equals(q.Event.Payload["stage"], "complete"))
            .Select(q => q.Event.Payload["requestId"]?.ToString())
            .ToList();

        Assert.Equal(requestCount, startIds.Distinct().Count());
        Assert.Equal(requestCount, completeIds.Distinct().Count());
        Assert.Equal(startIds.OrderBy(x => x), completeIds.OrderBy(x => x));
        Assert.Equal(startIds.OrderBy(x => x), results.OrderBy(x => x));
    }

    [Fact]
    public async Task TraceIdentifier_IsPreservedAndNotMutated()
    {
        string? observedTraceId = null;
        const string customTraceId = "custom-trace-id-12345-abcde";

        using var host = await CreateHostBuilder(app =>
        {
            // Middleware prior to Vestara setting custom TraceIdentifier
            app.Use((context, next) =>
            {
                context.TraceIdentifier = customTraceId;
                return next();
            });
            app.UseVestara();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/api/trace-test", context =>
                {
                    observedTraceId = context.TraceIdentifier;
                    return context.Response.WriteAsync("trace-ok");
                });
            });
        }).StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetAsync("/api/trace-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(customTraceId, observedTraceId);
    }
}
