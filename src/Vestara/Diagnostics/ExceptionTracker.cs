using System.Runtime.CompilerServices;

namespace Vestara.Diagnostics;

public enum ExceptionCaptureState
{
    NonFatal,
    Fatal
}

public sealed class ExceptionTracker
{
    private readonly ConditionalWeakTable<Exception, StrongBox<ExceptionCaptureState>> _trackedExceptions = new();
    private readonly object _lock = new();

    public bool ShouldCapture(Exception exception, bool isFatal)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_lock)
        {
            if (_trackedExceptions.TryGetValue(exception, out var box))
            {
                if (box.Value == ExceptionCaptureState.NonFatal && isFatal)
                {
                    // Escalate from NonFatal to Fatal
                    box.Value = ExceptionCaptureState.Fatal;
                    return true;
                }

                // Already captured at same or higher severity -> suppress duplicate
                return false;
            }

            var initialState = isFatal ? ExceptionCaptureState.Fatal : ExceptionCaptureState.NonFatal;
            _trackedExceptions.Add(exception, new StrongBox<ExceptionCaptureState>(initialState));
            return true;
        }
    }
}
