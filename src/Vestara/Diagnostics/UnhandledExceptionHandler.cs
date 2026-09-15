namespace Vestara.Diagnostics;

public sealed class UnhandledExceptionHandler : IDisposable
{
    private static readonly object ProcessLock = new();
    private static readonly List<UnhandledExceptionHandler> ActiveHandlers = new();
    private static bool ProcessHandlersHooked = false;

    private readonly Action<Exception, bool> _onException;
    private readonly Func<Task>? _bestEffortFlush;
    private int _registered;
    private bool _disposed;

    public UnhandledExceptionHandler(Action<Exception, bool> onException, Func<Task>? bestEffortFlush = null)
    {
        _onException = onException ?? throw new ArgumentNullException(nameof(onException));
        _bestEffortFlush = bestEffortFlush;
    }

    public void Register()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) == 0)
        {
            lock (ProcessLock)
            {
                if (!ActiveHandlers.Contains(this))
                {
                    ActiveHandlers.Add(this);
                }

                if (!ProcessHandlersHooked)
                {
                    AppDomain.CurrentDomain.UnhandledException += ProcessOnUnhandledException;
                    TaskScheduler.UnobservedTaskException += ProcessOnUnobservedTaskException;
                    ProcessHandlersHooked = true;
                }
            }
        }
    }

    public void Unregister()
    {
        if (Interlocked.CompareExchange(ref _registered, 0, 1) == 1)
        {
            lock (ProcessLock)
            {
                ActiveHandlers.Remove(this);
                if (ActiveHandlers.Count == 0 && ProcessHandlersHooked)
                {
                    AppDomain.CurrentDomain.UnhandledException -= ProcessOnUnhandledException;
                    TaskScheduler.UnobservedTaskException -= ProcessOnUnobservedTaskException;
                    ProcessHandlersHooked = false;
                }
            }
        }
    }

    private static void ProcessOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        UnhandledExceptionHandler? handler;
        lock (ProcessLock)
        {
            handler = ActiveHandlers.LastOrDefault();
        }

        handler?.HandleUnhandledException(e);
    }

    private static void ProcessOnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        UnhandledExceptionHandler? handler;
        lock (ProcessLock)
        {
            handler = ActiveHandlers.LastOrDefault();
        }

        handler?.HandleUnobservedTaskException(e);
    }

    public static void DispatchUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ProcessOnUnhandledException(sender, e);
    }

    public static void DispatchUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ProcessOnUnobservedTaskException(sender, e);
    }

    private void HandleUnhandledException(UnhandledExceptionEventArgs e)
    {
        try
        {
            var exception = e.ExceptionObject as Exception ??
                            new Exception($"Non-exception object thrown: {e.ExceptionObject}");

            var isFatal = e.IsTerminating;
            _onException(exception, isFatal);

            if (isFatal && _bestEffortFlush != null)
            {
                // Bounded best-effort network upload attempt only after persistence has completed
                try
                {
                    Task.Run(async () => await _bestEffortFlush().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Ignore network failure on crash path
                }
            }
        }
        catch
        {
            // Swallow Vestara internal failures during crash path to preserve CLR termination
        }
    }

    private void HandleUnobservedTaskException(UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            // Not automatically fatal. Do NOT call SetObserved.
            _onException(e.Exception, false);
        }
        catch
        {
            // Swallow
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Unregister();
            _disposed = true;
        }
    }
}
