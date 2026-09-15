using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vestara.Context;
using Vestara.Diagnostics;
using Vestara.Hosting;
using Vestara.Logging;
using Vestara.Models;
using Vestara.Persistence;
using Vestara.Queue;
using Vestara.Settings;
using Vestara.Transport;

namespace Vestara;

public sealed class VestaraClient : IDisposable
{
    private readonly VestaraOptions _options;
    private readonly EventQueue _queue;
    private readonly BreadcrumbBuffer _breadcrumbs;
    private readonly FileEventStorage _storage;
    private readonly DeviceIdStore _deviceIdStore;
    private readonly HttpUploader _uploader;
    private readonly DeviceSettingsSync _deviceSettingsSync;
    private readonly ExceptionTracker _exceptionTracker;
    private readonly UnhandledExceptionHandler? _unhandledExceptionHandler;
    private readonly IVestaraCorrelationAccessor _correlationAccessor;

    private readonly string _sessionId;
    private readonly string _deviceId;
    private readonly string _appVersion;
    private readonly string? _serviceName;
    private readonly string? _appIdentifier;
    private readonly string _runtime;
    private readonly string _osVersion;
    private readonly string _deviceModel;
    private bool _disposed;

    public VestaraOptions Options => _options;
    public EventQueue Queue => _queue;
    public BreadcrumbBuffer Breadcrumbs => _breadcrumbs;
    public FileEventStorage Storage => _storage;
    public HttpUploader Uploader => _uploader;
    public DeviceSettingsSync DeviceSettings => _deviceSettingsSync;
    public ExceptionTracker ExceptionTracker => _exceptionTracker;
    public IVestaraCorrelationAccessor CorrelationAccessor => _correlationAccessor;

    public string SessionId => _sessionId;
    public string DeviceId => _deviceId;
    public string AppVersion => _appVersion;
    public string? ServiceName => _serviceName;
    public string? AppIdentifier => _appIdentifier;

    public VestaraClient(
        VestaraOptions options,
        HttpClient? httpClient = null,
        IVestaraCorrelationAccessor? correlationAccessor = null,
        ISystemClock? clock = null,
        string? hostApplicationName = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _correlationAccessor = correlationAccessor ?? new VestaraCorrelationAccessor();
        _queue = new EventQueue();
        _breadcrumbs = new BreadcrumbBuffer();
        _exceptionTracker = new ExceptionTracker();

        var (resolvedService, resolvedIdentifier) = ApplicationIdentityResolver.ResolveIdentity(
            _options.ServiceName,
            _options.AppIdentifier,
            hostApplicationName);

        _serviceName = resolvedService;
        _appIdentifier = resolvedIdentifier;

        if (!string.IsNullOrWhiteSpace(_options.AppVersion))
        {
            _appVersion = _options.AppVersion.Trim();
        }
        else
        {
            var entryVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            _appVersion = !string.IsNullOrWhiteSpace(entryVersion) ? entryVersion.Trim() : "unknown";
        }

        var storageKey = StorageKeyResolver.ComputeKey(_options.Token, _appIdentifier, "dotnet_service");
        _deviceIdStore = new DeviceIdStore(_options.StorageDirectory, storageKey);
        _deviceId = _deviceIdStore.GetOrCreateDeviceId();
        _sessionId = Guid.NewGuid().ToString("D");

        _storage = new FileEventStorage(_options.StorageDirectory, storageKey);

        _runtime = RuntimeInformation.FrameworkDescription;
        _osVersion = RuntimeInformation.OSDescription;
        _deviceModel = RuntimeInformation.ProcessArchitecture.ToString();

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var baseUri = new Uri(_options.ApiUrl);

        _uploader = new HttpUploader(client, _queue, _storage, _options.Token, baseUri, clock);
        _deviceSettingsSync = new DeviceSettingsSync(client, _options.Token, baseUri, _deviceId);

        if (_options.CaptureUnhandledExceptions)
        {
            _unhandledExceptionHandler = new UnhandledExceptionHandler(
                onException: (ex, isFatal) => CaptureException(ex, isFatal: isFatal),
                bestEffortFlush: async () => await _uploader.FlushBatchAsync(ignoreBackoff: true, isCrashBudget: true).ConfigureAwait(false)
            );
            _unhandledExceptionHandler.Register();
        }
    }

    public void RecoverQueue()
    {
        var recovered = _storage.LoadEvents();
        if (recovered.Count > 0)
        {
            _queue.MergeRecoveredEvents(recovered);
        }
    }

    public void AddBreadcrumb(string message, string? category = null, IDictionary<string, object?>? data = null, string? level = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var breadcrumb = new Breadcrumb
        {
            Message = LogStateSanitizer.TruncateUtf8(message, 1024),
            Category = category,
            Level = level ?? "info",
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            RequestId = _correlationAccessor.CurrentRequestId,
            Data = data != null ? LogStateSanitizer.SanitizeDictionary(data) : null
        };

        _breadcrumbs.Add(breadcrumb);
    }

    public void Log(
        string level,
        string message,
        IDictionary<string, object?>? data = null,
        EmissionOrigin origin = EmissionOrigin.OrdinaryLog)
    {
        if (!_deviceSettingsSync.ShouldEmit(origin))
        {
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["level"] = level,
            ["message"] = LogStateSanitizer.TruncateUtf8(message, LogStateSanitizer.MaxMessageBytes)
        };

        var requestId = _correlationAccessor.CurrentRequestId;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            payload["requestId"] = requestId;
        }

        if (data != null)
        {
            var sanitizedData = LogStateSanitizer.SanitizeDictionary(data);
            foreach (var kvp in sanitizedData)
            {
                if (!payload.ContainsKey(kvp.Key))
                {
                    payload[kvp.Key] = kvp.Value;
                }
            }
        }

        LogStateSanitizer.NormalizeAndBoundPayload(payload, isFatal: false);

        var @event = new VestaraEvent
        {
            EventType = "log",
            Origin = origin,
            Payload = payload
        };

        EnqueueEvent(@event);
    }

    public void CaptureException(Exception exception, IDictionary<string, object?>? data = null, bool isFatal = false)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!_exceptionTracker.ShouldCapture(exception, isFatal))
        {
            return;
        }

        var origin = isFatal ? EmissionOrigin.Crash : EmissionOrigin.Exception;
        if (!_deviceSettingsSync.ShouldEmit(origin))
        {
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = LogStateSanitizer.TruncateUtf8(exception.Message, LogStateSanitizer.MaxMessageBytes),
            ["type"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["stack"] = FormatBoundedStackTrace(exception),
            ["stack_trace"] = ExtractStructuredFrames(exception),
            ["breadcrumbs"] = _breadcrumbs.Snapshot()
        };

        if (!isFatal)
        {
            payload["level"] = "error";
        }

        var requestId = _correlationAccessor.CurrentRequestId;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            payload["requestId"] = requestId;
        }

        if (data != null)
        {
            var sanitizedData = LogStateSanitizer.SanitizeDictionary(data);
            foreach (var kvp in sanitizedData)
            {
                if (!payload.ContainsKey(kvp.Key))
                {
                    payload[kvp.Key] = kvp.Value;
                }
            }
        }

        LogStateSanitizer.NormalizeAndBoundPayload(payload, isFatal: isFatal);

        var @event = new VestaraEvent
        {
            EventType = isFatal ? "crash" : "log",
            Origin = origin,
            Payload = payload
        };

        EnqueueEvent(@event, isFatal: isFatal);
    }

    private void EnqueueEvent(VestaraEvent @event, bool isFatal = false)
    {
        // Populate standard metadata
        @event.SessionId = _sessionId;
        @event.DeviceId = _deviceId;
        @event.Timestamp = DateTimeOffset.UtcNow.ToString("o");
        @event.SdkName = "sdk-dotnet";
        @event.SdkVersion = "0.1.0";
        @event.AppVersion = _appVersion;
        @event.OsName = "dotnet";
        @event.OsVersion = _osVersion;
        @event.DeviceModel = _deviceModel;
        @event.Environment = _options.Environment;
        @event.TargetCategory = "dotnet_service";
        @event.Runtime = _runtime;
        @event.ServiceName = _serviceName;
        @event.AppIdentifier = _appIdentifier;

        var finalEvent = @event;

        if (_options.BeforeSend != null)
        {
            var original = @event.DeepClone();
            var mutableCopy = @event.DeepClone();
            try
            {
                var modified = _options.BeforeSend(mutableCopy);
                if (modified == null)
                {
                    // Dropped by BeforeSend
                    return;
                }

                // Reassert canonical fields
                modified.EventType = original.EventType;
                modified.Origin = original.Origin;
                modified.SdkName = "sdk-dotnet";
                modified.SdkVersion = "0.1.0";
                modified.OsName = "dotnet";
                modified.OsVersion = _osVersion;
                modified.DeviceModel = _deviceModel;
                modified.TargetCategory = "dotnet_service";
                modified.Runtime = _runtime;
                modified.ServiceName = _serviceName;
                modified.AppIdentifier = _appIdentifier;
                modified.DeviceId = _deviceId;
                modified.SessionId = _sessionId;
                modified.Timestamp = original.Timestamp;
                modified.Environment = _options.Environment;
                modified.AppVersion = _appVersion;

                // Re-sanitize and bound payload
                modified.Payload = LogStateSanitizer.SanitizeDictionary(modified.Payload);
                LogStateSanitizer.NormalizeAndBoundPayload(modified.Payload, isFatal: modified.Origin == EmissionOrigin.Crash);

                finalEvent = modified;
            }
            catch
            {
                // On user callback exception, retain original unmodified event
                finalEvent = original;
            }
        }

        if (isFatal)
        {
            _queue.EnqueueFatalAndPersist(finalEvent, _storage);
        }
        else
        {
            _queue.Enqueue(finalEvent);
        }
    }

    private static List<Dictionary<string, object?>> ExtractStructuredFrames(Exception ex)
    {
        var frames = new List<Dictionary<string, object?>>();
        try
        {
            var trace = new StackTrace(ex, true);
            var stackFrames = trace.GetFrames();
            if (stackFrames != null)
            {
                int count = 0;
                foreach (var frame in stackFrames)
                {
                    if (count++ >= 100)
                    {
                        break;
                    }

                    var method = frame.GetMethod();
                    var functionName = method != null
                        ? $"{method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? ""}.{method.Name}".TrimStart('.')
                        : "(unknown)";
                    if (string.IsNullOrWhiteSpace(functionName))
                    {
                        functionName = "(unknown)";
                    }

                    var fileName = frame.GetFileName();
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["function"] = functionName,
                        ["file"] = !string.IsNullOrWhiteSpace(fileName) ? fileName : "(unknown)",
                        ["line"] = frame.GetFileLineNumber() != 0 ? frame.GetFileLineNumber() : null,
                        ["column"] = frame.GetFileColumnNumber() != 0 ? frame.GetFileColumnNumber() : null
                    };
                    frames.Add(dict);
                }
            }
        }
        catch
        {
            // Fail-safe: do not let stack extraction failure prevent capture
        }
        return frames;
    }

    private static string FormatBoundedStackTrace(Exception ex)
    {
        var stack = ex.StackTrace ?? string.Empty;
        return LogStateSanitizer.TruncateUtf8(stack, LogStateSanitizer.MaxStackBytes);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _uploader.FlushAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            VestaraSdk.ClearClient(this);
            _unhandledExceptionHandler?.Dispose();
            _disposed = true;
        }
    }
}
