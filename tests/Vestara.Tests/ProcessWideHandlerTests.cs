using Vestara.Diagnostics;
using Xunit;

namespace Vestara.Tests;

[Collection("ProcessWideDiagnostics")]
public class ProcessWideHandlerTests
{
    [Fact]
    public void MultipleClients_RegisteringUnhandledHandlers_ProducesSingleFatalCallbackPath()
    {
        int client1CallbackCount = 0;
        int client2CallbackCount = 0;

        using var handler1 = new UnhandledExceptionHandler((ex, isFatal) => client1CallbackCount++);
        using var handler2 = new UnhandledExceptionHandler((ex, isFatal) => client2CallbackCount++);

        handler1.Register();
        handler2.Register();

        // Dispatch simulated unhandled terminating exception through static entrypoint
        UnhandledExceptionHandler.DispatchUnhandledException(
            this,
            new UnhandledExceptionEventArgs(new InvalidOperationException("simulated crash"), isTerminating: true)
        );

        // Exactly one handler (the active registered one) must process the callback
        Assert.Equal(0, client1CallbackCount);
        Assert.Equal(1, client2CallbackCount);
    }

    [Fact]
    public void DisposingActiveClient_ClearsVestaraSdk_DisposingOldClient_DoesNotClearNewerClient()
    {
        using var temp = new TempDirectory();
        var options = new VestaraOptions
        {
            Token = "test_token_123",
            StorageDirectory = temp.Path,
            CaptureUnhandledExceptions = false
        };

        var client1 = new VestaraClient(options);
        VestaraSdk.SetClient(client1);
        Assert.Same(client1, VestaraSdk.CurrentClient);

        // Replace with newer client2
        var client2 = new VestaraClient(options);
        VestaraSdk.SetClient(client2);
        Assert.Same(client2, VestaraSdk.CurrentClient);

        // Disposing old client1 must NOT clear client2 from static facade
        client1.Dispose();
        Assert.Same(client2, VestaraSdk.CurrentClient);

        // Disposing active client2 MUST clear the static facade
        client2.Dispose();
        Assert.Null(VestaraSdk.CurrentClient);
    }
}
