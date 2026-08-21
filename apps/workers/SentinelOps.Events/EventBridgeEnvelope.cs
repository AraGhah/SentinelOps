using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelOps.Events;

// SQS message body when the queue is an EventBridge rule target: the full
// EventBridge event, not just our `detail` payload.
public record EventBridgeEnvelope(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("detail-type")] string DetailType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("detail")] JsonElement Detail)
{
    public static EventBridgeEnvelope Parse(string sqsMessageBody) =>
        JsonSerializer.Deserialize<EventBridgeEnvelope>(sqsMessageBody)
        ?? throw new InvalidOperationException("SQS message body is not a valid EventBridge event envelope.");

    public T DeserializeDetail<T>() =>
        Detail.Deserialize<T>(EventJson.Options)
            ?? throw new InvalidOperationException($"Event detail could not be deserialized as {typeof(T).Name}.");
}
