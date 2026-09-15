using Vestara.Transport;
using Xunit;

namespace Vestara.Tests;

public class TransportSecurityTests
{
    [Theory]
    [InlineData("https://api.vestara.dev")]
    [InlineData("https://staging-api.vestara.dev")]
    [InlineData("https://custom.domain.com:8443")]
    public void HttpsEndpoints_Accepted(string url)
    {
        var uri = EndpointValidator.ValidateAndNormalize(url);
        Assert.Equal("https", uri.Scheme);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://LOCALHOST:8080")]
    public void LocalhostHttp_Accepted(string url)
    {
        var uri = EndpointValidator.ValidateAndNormalize(url);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal("localhost", uri.Host, ignoreCase: true);
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8080")]
    public void LoopbackIpv4Http_Accepted(string url)
    {
        var uri = EndpointValidator.ValidateAndNormalize(url);
        Assert.Equal("http", uri.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
    }

    [Theory]
    [InlineData("http://api.vestara.dev")]
    [InlineData("http://production.vestara.internal")]
    [InlineData("http://192.168.1.100")]
    public void RemoteHttp_Rejected(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => EndpointValidator.ValidateAndNormalize(url));
        Assert.Contains("HTTPS", ex.Message);
    }

    [Theory]
    [InlineData("http://[::1]")]
    [InlineData("http://[::1]:5000")]
    public void Ipv6LoopbackHttp_Rejected(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => EndpointValidator.ValidateAndNormalize(url));
        Assert.Contains("IPv6 loopback is not permitted", ex.Message);
    }

    [Theory]
    [InlineData("not-a-valid-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://api.vestara.dev")]
    [InlineData("file:///etc/hosts")]
    [InlineData("")]
    [InlineData("   ")]
    public void MalformedOrUnsupportedSchemes_Rejected(string url)
    {
        Assert.Throws<ArgumentException>(() => EndpointValidator.ValidateAndNormalize(url));
    }
}
