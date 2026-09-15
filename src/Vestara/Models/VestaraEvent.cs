using System.Text.Json.Serialization;
using Vestara.Logging;

namespace Vestara.Models;

public sealed class VestaraEvent
{
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "log";

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("sdk_name")]
    public string SdkName { get; set; } = "sdk-dotnet";

    [JsonPropertyName("sdk_version")]
    public string SdkVersion { get; set; } = "0.1.0";

    [JsonPropertyName("app_version")]
    public string? AppVersion { get; set; }

    [JsonPropertyName("os_name")]
    public string OsName { get; set; } = "dotnet";

    [JsonPropertyName("os_version")]
    public string? OsVersion { get; set; }

    [JsonPropertyName("device_model")]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "production";

    [JsonPropertyName("target_category")]
    public string TargetCategory { get; set; } = "dotnet_service";

    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("service_name")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("app_identifier")]
    public string? AppIdentifier { get; set; }

    [JsonPropertyName("payload")]
    public Dictionary<string, object?> Payload { get; set; } = new();

    [JsonIgnore]
    public EmissionOrigin Origin { get; set; } = EmissionOrigin.OrdinaryLog;

    public VestaraEvent DeepClone()
    {
        return new VestaraEvent
        {
            EventType = EventType,
            SessionId = SessionId,
            DeviceId = DeviceId,
            Timestamp = Timestamp,
            SdkName = SdkName,
            SdkVersion = SdkVersion,
            AppVersion = AppVersion,
            OsName = OsName,
            OsVersion = OsVersion,
            DeviceModel = DeviceModel,
            Environment = Environment,
            TargetCategory = TargetCategory,
            Runtime = Runtime,
            ServiceName = ServiceName,
            AppIdentifier = AppIdentifier,
            Payload = LogStateSanitizer.DeepCloneDictionary(Payload),
            Origin = Origin
        };
    }

    public VestaraEvent Clone() => DeepClone();
}
