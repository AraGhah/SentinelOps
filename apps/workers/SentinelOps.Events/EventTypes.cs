namespace SentinelOps.Events;

// EventBridge "detail-type" values. Plain strings since these are also used
// verbatim as CDK rule patterns and in infrastructure/event-schemas/.
public static class EventTypes
{
    public const string AlertReceived = "alert.received";
    public const string AlertValidated = "alert.validated";
    public const string AlertRejected = "alert.rejected";
    public const string IncidentCreated = "incident.created";
    public const string IncidentUpdated = "incident.updated";
    public const string IncidentAcknowledged = "incident.acknowledged";
    public const string IncidentEscalated = "incident.escalated";
    public const string IncidentResolved = "incident.resolved";
    public const string NotificationRequested = "notification.requested";
    public const string NotificationDelivered = "notification.delivered";
    public const string NotificationFailed = "notification.failed";
}

// EventBridge "source" values, one per publisher.
public static class EventSources
{
    public const string Api = "sentinelops.api";
    public const string AlertValidationWorker = "sentinelops.workers.alert-validation";
    public const string DeduplicationWorker = "sentinelops.workers.deduplication";
    public const string IncidentCreationWorker = "sentinelops.workers.incident-creation";
    public const string ResponderAssignmentWorker = "sentinelops.workers.responder-assignment";
    public const string NotificationWorker = "sentinelops.workers.notification";
    public const string EscalationWorker = "sentinelops.workers.escalation";
}
