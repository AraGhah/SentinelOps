using System.Text.Json;
using SentinelOps.Events;

namespace SentinelOps.Workers.Shared;

// A single outbound side-effect (publish an EventBridge event, send a queue
// message, or start a Step Functions execution) that a worker still owes the
// rest of the system once its business-state write has committed. See
// IdempotencyGuard and ProcessedWorkerEvent.PendingOutboxJson for why this
// gets captured and persisted *before* the business SaveChangesAsync, rather
// than built fresh each attempt: on redelivery after a crash between "business
// state committed" and "publish succeeded," the worker must retry exactly
// these already-serialized payloads (same EventIds and all) instead of
// re-running business logic that would produce new ones or, worse, duplicate
// records.
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

// Publishes/sends every item captured in a worker's pending outbox. Used both
// on the "happy path" right after the business SaveChangesAsync (with the
// items still fresh in memory) and on redelivery, after deserializing them
// back out of ProcessedWorkerEvent.PendingOutboxJson.
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
