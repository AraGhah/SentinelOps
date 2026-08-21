using System.Text.Json;
using SentinelOps.Events;

namespace SentinelOps.Workers.Shared;

// A pending outbound side-effect (EventBridge event, queue message, or Step
// Functions execution) owed once the business-state write commits. Persisted
// before SaveChangesAsync so a crash between commit and publish can replay
// the same serialized payloads on redelivery instead of re-running business
// logic and producing duplicates. See IdempotencyGuard.
public record OutboxItem(
    string Kind,
    string? Source,
    string? DetailType,
    string? DetailTypeName,
    string? QueueUrl,
    string? StateMachineArn,
    string? ExecutionName,
    Guid CorrelationId,
    string PayloadJson)
{
    private const string EventBridgeKind = "eventbridge";
    private const string QueueKind = "queue";
    private const string StepFunctionsKind = "stepfunctions";

    public static OutboxItem EventBridge(string source, string detailType, IEventDetail detail) => new(
        EventBridgeKind, source, detailType, detail.GetType().Name, null, null, null,
        detail.CorrelationId, JsonSerializer.Serialize(detail, detail.GetType(), EventJson.Options));

    public static OutboxItem Queue(string queueUrl, string body, Guid correlationId) => new(
        QueueKind, null, null, null, queueUrl, null, null, correlationId, body);

    public static OutboxItem StepFunctions(string stateMachineArn, string executionName, string inputJson, Guid correlationId) => new(
        StepFunctionsKind, null, null, null, null, stateMachineArn, executionName, correlationId, inputJson);

    public static string SerializeList(IReadOnlyList<OutboxItem> items) => JsonSerializer.Serialize(items, EventJson.Options);

    public static IReadOnlyList<OutboxItem> DeserializeList(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<OutboxItem>>(json, EventJson.Options) ?? [];
}

// Publishes/sends every item in a worker's pending outbox — right after
// SaveChangesAsync, or on redelivery after deserializing from
// ProcessedWorkerEvent.PendingOutboxJson.
public static class OutboxPublisher
{
    public static async Task PublishAllAsync(
        IEventPublisher? eventPublisher, IQueueSender? queueSender, IEscalationStarter? escalationStarter,
        IReadOnlyList<OutboxItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case "eventbridge":
                    var detail = EventDetailTypeRegistry.Deserialize(item.DetailTypeName!, item.PayloadJson);
                    await (eventPublisher ?? throw new InvalidOperationException("No IEventPublisher supplied for an EventBridge outbox item."))
                        .PublishAsync(item.Source!, item.DetailType!, detail, ct);
                    break;
                case "queue":
                    await (queueSender ?? throw new InvalidOperationException("No IQueueSender supplied for a queue outbox item."))
                        .SendAsync(item.QueueUrl!, item.PayloadJson, item.CorrelationId, ct);
                    break;
                case "stepfunctions":
                    await (escalationStarter ?? throw new InvalidOperationException("No IEscalationStarter supplied for a Step Functions outbox item."))
                        .StartExecutionAsync(item.StateMachineArn!, item.ExecutionName!, item.PayloadJson, ct);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown outbox item kind '{item.Kind}'.");
            }
        }
    }
}
