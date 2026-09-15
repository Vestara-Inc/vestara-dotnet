using Xunit;

namespace Vestara.Tests;

public class EnvironmentNormalizationTests
{
    [Theory]
    [InlineData("production", "production")]
    [InlineData("prod", "production")]
    [InlineData("live", "production")]
    [InlineData("PROD", "production")]
    [InlineData("  live  ", "production")]
    [InlineData("staging", "staging")]
    [InlineData("stage", "staging")]
    [InlineData("STAGE", "staging")]
    [InlineData("development", "development")]
    [InlineData("dev", "development")]
    [InlineData("DEV", "development")]
    [InlineData(null, "production")]
    [InlineData("", "production")]
    [InlineData("   ", "production")]
    [InlineData("qa", "production")]
    [InlineData("local_test", "production")]
    public void Environment_NormalizesToValidBackendEnum(string? input, string expected)
    {
        var options = new VestaraOptions
        {
            Environment = input!
        };

        Assert.Equal(expected, options.Environment);
    }

    [Fact]
    public void EmittedEvent_AlwaysContainsNormalizedEnvironment()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            Environment = "dev",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        using var client = new VestaraClient(options);
        client.Log("info", "env test");

        var events = client.Queue.GetAllForPersistence();
        Assert.Single(events);
        Assert.Equal("development", events[0].Event.Environment);
    }
}
