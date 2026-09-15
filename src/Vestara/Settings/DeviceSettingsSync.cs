using System.Text.Json;
using Vestara.Models;

namespace Vestara.Settings;

public sealed class DeviceSettingsSync
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly Uri _deviceSettingsUri;
    private volatile bool _loggingEnabled = true;
    private string? _updatedAt;

    public bool LoggingEnabled => _loggingEnabled;
    public string? UpdatedAt => _updatedAt;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public DeviceSettingsSync(HttpClient httpClient, string token, Uri baseUri, string deviceId)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        var normalizedBase = baseUri.ToString().TrimEnd('/');
        _deviceSettingsUri = new Uri($"{normalizedBase}/v1/sdk/device-settings?device_id={Uri.EscapeDataString(deviceId)}");
    }

    public bool ShouldEmit(EmissionOrigin origin)
    {
        // When logging_enabled is false, suppress ONLY OrdinaryLog.
        // StructuralEvidence, Breadcrumb, Exception, and Crash are ALWAYS preserved.
        if (origin == EmissionOrigin.OrdinaryLog)
        {
            return _loggingEnabled;
        }

        return true;
    }

    public void UpdateSettingsDirectly(bool loggingEnabled, string? updatedAt = null)
    {
        _loggingEnabled = loggingEnabled;
        _updatedAt = updatedAt;
    }

    public async Task<bool> PollAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _deviceSettingsUri);
        request.Headers.TryAddWithoutValidation("X-SDK-Token", _token);

        try
        {
            var timeout = RequestTimeout > TimeSpan.Zero ? RequestTimeout : TimeSpan.FromSeconds(15);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var effectiveToken = linkedCts.Token;

            using var response = await _httpClient.SendAsync(request, effectiveToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(effectiveToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object)
            {
                root = dataElem;
            }

            if (root.TryGetProperty("logging_enabled", out var enabledElem) &&
                (enabledElem.ValueKind == JsonValueKind.True || enabledElem.ValueKind == JsonValueKind.False))
            {
                _loggingEnabled = enabledElem.GetBoolean();
            }

            if (root.TryGetProperty("updated_at", out var updatedElem) &&
                updatedElem.ValueKind == JsonValueKind.String)
            {
                _updatedAt = updatedElem.GetString();
            }

            return true;
        }
        catch
        {
            // Transient polling failure: retain current state
            return false;
        }
    }
}
