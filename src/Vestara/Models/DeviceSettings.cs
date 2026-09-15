using System.Text.Json.Serialization;

namespace Vestara.Models;

public sealed class DeviceSettings
{
    [JsonPropertyName("logging_enabled")]
    public bool LoggingEnabled { get; set; } = true;

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}
