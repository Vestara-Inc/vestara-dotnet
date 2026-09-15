using Microsoft.Extensions.DependencyInjection;
using Vestara.Context;
using Xunit;

namespace Vestara.Tests;

public class CorrelationContextTests
{
    private static IVestaraCorrelationAccessor CreateAccessor()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddVestara(options =>
        {
            options.Token = "test_token";
            options.CaptureUnhandledExceptions = false;
        });
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IVestaraCorrelationAccessor>();
    }

    [Fact]
    public void Default_CurrentRequestId_IsNull()
    {
        var accessor = CreateAccessor();
        Assert.Null(accessor.CurrentRequestId);
    }

    [Fact]
    public void BeginRequestScope_SetsAndRestoresRequestId_OnDispose()
    {
        var accessor = CreateAccessor();

        using (accessor.BeginRequestScope("req_level1"))
        {
            Assert.Equal("req_level1", accessor.CurrentRequestId);

            using (accessor.BeginRequestScope("req_level2"))
            {
                Assert.Equal("req_level2", accessor.CurrentRequestId);
            }

            Assert.Equal("req_level1", accessor.CurrentRequestId);
        }

        Assert.Null(accessor.CurrentRequestId);
    }

    [Fact]
    public async Task AwaitedAsyncWork_PreservesRequestId()
    {
        var accessor = CreateAccessor();

        using (accessor.BeginRequestScope("req_async_test"))
        {
            Assert.Equal("req_async_test", accessor.CurrentRequestId);

            await Task.Yield();
            Assert.Equal("req_async_test", accessor.CurrentRequestId);

            await Task.Delay(10);
            Assert.Equal("req_async_test", accessor.CurrentRequestId);
        }

        Assert.Null(accessor.CurrentRequestId);
    }

    [Fact]
    public async Task ConcurrentFlows_RemainIsolated()
    {
        var accessor = CreateAccessor();

        var taskA = Task.Run(async () =>
        {
            using (accessor.BeginRequestScope("req_A"))
            {
                for (int i = 0; i < 5; i++)
                {
                    Assert.Equal("req_A", accessor.CurrentRequestId);
                    await Task.Delay(5);
                }
            }
            Assert.Null(accessor.CurrentRequestId);
        });

        var taskB = Task.Run(async () =>
        {
            using (accessor.BeginRequestScope("req_B"))
            {
                for (int i = 0; i < 5; i++)
                {
                    Assert.Equal("req_B", accessor.CurrentRequestId);
                    await Task.Delay(5);
                }
            }
            Assert.Null(accessor.CurrentRequestId);
        });

        await Task.WhenAll(taskA, taskB);
    }
}
