using System.Text.Json.Serialization;

namespace Vestara.Models;

public sealed class Breadcrumb
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("data")]
    public Dictionary<string, object?>? Data { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
}
