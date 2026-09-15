using Microsoft.Extensions.Hosting;

namespace Vestara.Hosting;

public sealed class VestaraHostedService : IHostedService, IDisposable
{
    private readonly VestaraClient _client;
    private readonly CancellationTokenSource _cts = new();
    private Task? _uploadLoopTask;
    private Task? _settingsLoopTask;
    private bool _disposed;

    public VestaraHostedService(VestaraClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        VestaraSdk.SetClient(_client);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. Recover persistent queue from disk
        _client.RecoverQueue();

        // 2. Start background upload loop
        _uploadLoopTask = Task.Run(() => RunUploadLoopAsync(_cts.Token), CancellationToken.None);

        // 3. Start background device settings sync loop
        _settingsLoopTask = Task.Run(() => RunSettingsLoopAsync(_cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task RunUploadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_client.Queue.Count > 0)
                {
                    await _client.Uploader.FlushBatchAsync(ignoreBackoff: false, isCrashBudget: false, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Prevent loop crash on unexpected background errors
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunSettingsLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _client.DeviceSettings.PollAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cooperatively cancel loops
        _cts.Cancel();

        var backgroundTasks = new List<Task>();
        if (_uploadLoopTask != null) backgroundTasks.Add(_uploadLoopTask);
        if (_settingsLoopTask != null) backgroundTasks.Add(_settingsLoopTask);

        try
        {
            await Task.WhenAll(backgroundTasks).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Timeout or cancellation; proceed to flush
        }

        // Bounded flush of remaining events
        try
        {
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, flushCts.Token);
            await _client.FlushAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignore timeout during shutdown flush
        }

        // Persist any un-uploaded events to disk through queue persistence coordinator
        _client.Queue.SnapshotAndPersist(_client.Storage);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Dispose();
            _client.Dispose();
            _disposed = true;
        }
    }
}
