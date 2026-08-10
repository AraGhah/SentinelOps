using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Incidents;

public record CreateIncidentRequest(
    [Required, MaxLength(300)] string Title,
    string? Description,
    IncidentSeverity Severity,
    Guid? ServiceId,
    Guid? AssignedResponderUserId);

public record UpdateIncidentRequest(
    [Required, MaxLength(300)] string Title,
    string? Description,
    IncidentSeverity Severity,
    Guid? ServiceId,
    Guid? AssignedResponderUserId);

public record UpdateIncidentStatusRequest(IncidentStatus Status, string? Note);

public record IncidentResponse(
    Guid Id, string Title, string? Description, IncidentSeverity Severity, Guid? ServiceId,
    Guid? AssignedResponderUserId, IncidentStatus Status, int AlertCount, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc, DateTimeOffset? ResolvedAtUtc,
    // Highest EscalationLevel.Order the escalation state machine has notified
    // so far for this incident; null if no escalation policy has ever
    // applied to it. See SentinelOps.Workers.Escalation.
    int? CurrentEscalationLevel);

public record IncidentFilters(IncidentStatus? Status, IncidentSeverity? Severity, Guid? ServiceId, Guid? AssignedResponderUserId);

public record IncidentStatusHistoryResponse(
    IncidentStatus? FromStatus, IncidentStatus ToStatus, Guid ChangedByUserId, string? Note, DateTimeOffset ChangedAtUtc);

public record CreateCommentRequest([Required] string Body, bool IsInternal);

public record CommentResponse(Guid Id, Guid AuthorUserId, string Body, bool IsInternal, DateTimeOffset CreatedAtUtc);

public record AddTagRequest([Required, MaxLength(100)] string Tag);

// ActorUserId is null for automated system actions (assignment, escalation,
// notification delivery) — the timeline UI renders those as "System".
public record IncidentEventResponse(
    Guid Id, IncidentEventType EventType, Guid? ActorUserId, string? Summary, string? Details, DateTimeOffset OccurredAtUtc);
