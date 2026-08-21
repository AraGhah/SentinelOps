using System.Text.Json;

namespace SentinelOps.Events;

// Lets a caller round-trip an IEventDetail through JSON when it only has the
// runtime type's simple name to go on (e.g. a worker's persisted outbox —
// see SentinelOps.Workers.Shared.OutboxItem — which can't store a .NET Type
// directly). Deliberately keyed by short type name rather than
// AssemblyQualifiedName so a redeployment/assembly-version bump can't break
// deserialization of an outbox row written by a previous deployment.
public static class EventDetailTypeRegistry
{
    private static readonly Dictionary<string, Type> Types = new()
    {
        [nameof(AlertReceivedDetail)] = typeof(AlertReceivedDetail),
        [nameof(AlertValidatedDetail)] = typeof(AlertValidatedDetail),
        [nameof(AlertRejectedDetail)] = typeof(AlertRejectedDetail),
        [nameof(IncidentCreatedDetail)] = typeof(IncidentCreatedDetail),
        [nameof(IncidentUpdatedDetail)] = typeof(IncidentUpdatedDetail),
        [nameof(IncidentAcknowledgedDetail)] = typeof(IncidentAcknowledgedDetail),
        [nameof(IncidentEscalatedDetail)] = typeof(IncidentEscalatedDetail),
        [nameof(IncidentResolvedDetail)] = typeof(IncidentResolvedDetail),
        [nameof(NotificationRequestedDetail)] = typeof(NotificationRequestedDetail),
        [nameof(NotificationDeliveredDetail)] = typeof(NotificationDeliveredDetail),
        [nameof(NotificationFailedDetail)] = typeof(NotificationFailedDetail),
    };

    public static IEventDetail Deserialize(string typeName, string json)
    {
        if (!Types.TryGetValue(typeName, out var type))
        {
            throw new InvalidOperationException($"Unknown event detail type '{typeName}'.");
        }

        return (IEventDetail)(JsonSerializer.Deserialize(json, type, EventJson.Options)
            ?? throw new InvalidOperationException($"Failed to deserialize a '{typeName}' payload."));
    }
}
