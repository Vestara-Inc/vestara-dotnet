using Vestara.Hosting;
using Xunit;

namespace Vestara.Tests;

public class ApplicationIdentityTests
{
    [Fact]
    public void ExplicitConfiguration_TakesPrecedence()
    {
        var (service, app) = ApplicationIdentityResolver.ResolveIdentity(
            configuredServiceName: "billing-api",
            configuredAppIdentifier: "app-billing-prod",
            hostApplicationName: "GenericHostName");

        Assert.Equal("billing-api", service);
        Assert.Equal("app-billing-prod", app);
    }

    [Fact]
    public void HostEnvironmentApplicationName_UsedWhenNotExplicitlyConfigured()
    {
        var (service, app) = ApplicationIdentityResolver.ResolveIdentity(
            configuredServiceName: null,
            configuredAppIdentifier: null,
            hostApplicationName: "MyWorkerService");

        Assert.Equal("MyWorkerService", service);
        Assert.Equal("MyWorkerService", app);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData("testhost")]
    [InlineData("testhost.x86")]
    [InlineData("testhost.arm64")]
    [InlineData(".NET Service")]
    [InlineData("dotnet-service")]
    [InlineData("dotnet-app")]
    public void GenericProcessNames_AreRejected(string genericName)
    {
        var (service, app) = ApplicationIdentityResolver.ResolveIdentity(
            configuredServiceName: genericName,
            configuredAppIdentifier: genericName,
            hostApplicationName: genericName);

        Assert.Null(service);
        Assert.Null(app);
    }

    [Fact]
    public void UnresolvedIdentity_RemainsNull_NoFakeFallbacks()
    {
        var (service, app) = ApplicationIdentityResolver.ResolveIdentity(
            configuredServiceName: null,
            configuredAppIdentifier: null,
            hostApplicationName: "dotnet");

        Assert.Null(service);
        Assert.Null(app);
    }
}
