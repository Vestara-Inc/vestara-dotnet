using System.Text.Json.Serialization;

namespace Vestara.Models;

public sealed class QueuedEvent
{
    [JsonPropertyName("queue_id")]
    public string QueueId { get; set; } = Guid.NewGuid().ToString("n");

    [JsonPropertyName("event")]
    public VestaraEvent Event { get; set; } = null!;

    [JsonIgnore]
    public bool IsInFlight { get; set; }
}
