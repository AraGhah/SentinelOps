using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelOps.Events;

// Shared serializer options so publish and deserialize agree on wire shape.
// camelCase to match infrastructure/event-schemas/*.schema.json.
public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
