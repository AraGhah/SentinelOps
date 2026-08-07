using System.Text.Json;

namespace SentinelOps.Events;

// Shared serializer options so the wire shape published by EventBridgeEventPublisher
// matches what EventBridgeEnvelope.DeserializeDetail expects on the way back in —
// camelCase to match the field names used in infrastructure/event-schemas/*.schema.json.
public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
