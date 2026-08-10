namespace SentinelOps.Api.Domain;

// One entry per notable thing that happened to an incident, in the exact
// order the checklist's timeline section lists them. StatusChanged covers
// every transition other than the two called out separately (Acknowledged,
// Resolved) so those don't show up twice.
public enum IncidentEventType
{
    Created,
    Assigned,
    NotificationSent,
    Acknowledged,
    Escalated,
    StatusChanged,
    CommentAdded,
    AttachmentAdded,
    Resolved,
}

// Immutable by construction: no controller ever exposes an update/delete path
// for this entity, same convention as AuditLog/IncidentStatusHistory.
// ActorUserId is null for automated system actions (escalation, auto-assignment,
// notification delivery) so the timeline can render "System" instead of a user.
public class IncidentEvent : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public IncidentEventType EventType { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Summary { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }

    public Incident? Incident { get; set; }
}
