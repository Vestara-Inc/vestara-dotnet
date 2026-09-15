using Vestara.Models;
using Vestara.Transport;

namespace Vestara;

public sealed class VestaraOptions
{
    private string _apiUrl = "https://api.vestara.dev";

    /// <summary>
    /// Vestara project token (required).
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Vestara API URL. Defaults to https://api.vestara.dev.
    /// Loopback HTTP (localhost, 127.0.0.1) is permitted for local development; remote endpoints must use HTTPS.
    /// </summary>
    public string ApiUrl
    {
        get => _apiUrl;
        set
        {
            var validated = EndpointValidator.ValidateAndNormalize(value);
            _apiUrl = validated.ToString();
        }
    }

    private string _environment = "production";

    /// <summary>
    /// Deployment environment name (e.g. "production", "staging", "development"). Defaults to "production".
    /// Normalizes aliases (prod/live -> production, stage -> staging, dev -> development) and defaults unsupported to production.
    /// </summary>
    public string Environment
    {
        get => _environment;
        set => _environment = NormalizeEnvironment(value);
    }

    /// <summary>
    /// Normalizes environment string to one of: production, staging, development.
    /// </summary>
    public static string NormalizeEnvironment(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return "production";
        }

        var trimmed = environment.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "production" or "prod" or "live" => "production",
            "staging" or "stage" => "staging",
            "development" or "dev" => "development",
            _ => "production"
        };
    }

    /// <summary>
    /// Application version string.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Logical service name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Application identifier.
    /// </summary>
    public string? AppIdentifier { get; set; }

    /// <summary>
    /// Optional callback invoked before enqueuing an event.
    /// Returning null drops the event. Any uncaught exceptions inside the callback will not crash the host and the original event will be retained.
    /// </summary>
    public Func<VestaraEvent, VestaraEvent?>? BeforeSend { get; set; }

    /// <summary>
    /// Optional directory for persistent storage.
    /// Defaults to Environment.SpecialFolder.LocalApplicationData / Vestara.
    /// </summary>
    public string? StorageDirectory { get; set; }

    /// <summary>
    /// Whether to automatically capture unhandled AppDomain and unobserved Task exceptions. Defaults to true.
    /// </summary>
    public bool CaptureUnhandledExceptions { get; set; } = true;

    /// <summary>
    /// Validates required options.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            throw new ArgumentException("Vestara:Token must be provided and not empty.", nameof(Token));
        }

        EndpointValidator.ValidateAndNormalize(ApiUrl);
    }
}
