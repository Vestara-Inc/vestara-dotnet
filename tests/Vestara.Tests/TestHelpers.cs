using System.Net;
using System.Text;
using Vestara.Transport;

namespace Vestara.Tests;

public sealed class TestClock : ISystemClock
{
    public DateTimeOffset CurrentTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => CurrentTime;

    public void Advance(TimeSpan delta)
    {
        CurrentTime = CurrentTime.Add(delta);
    }
}

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> SentRequests { get; } = new();

    public void EnqueueResponse(HttpStatusCode statusCode, string content = "{\"accepted\":100,\"rejected\":0}")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });
    }

    public void EnqueueCallback(Func<HttpRequestMessage, HttpResponseMessage> callback)
    {
        _responses.Enqueue(callback);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SentRequests.Add(request);
        if (_responses.Count > 0)
        {
            var handler = _responses.Dequeue();
            return Task.FromResult(handler(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"accepted\":100,\"rejected\":0}", Encoding.UTF8, "application/json")
        });
    }
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VestaraTests_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup errors
        }
    }
}
