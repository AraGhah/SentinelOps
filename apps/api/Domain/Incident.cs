namespace SentinelOps.Api.Domain;

// Ordinal, most-severe-first, so a plain OrderBy(Severity) sorts critical first.
public enum IncidentSeverity { Critical = 0, High = 1, Medium = 2, Low = 3 }

// Exact order from the checklist's incident-management section.
public enum IncidentStatus { Triggered = 0, Assigned = 1, Acknowledged = 2, Investigating = 3, Monitoring = 4, Resolved = 5, Reopened = 6 }

public class Incident : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public IncidentSeverity Severity { get; set; }
    public Guid? ServiceId { get; set; }
    public Guid? AssignedResponderUserId { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Triggered;
    public int AlertCount { get; set; }
    // Highest EscalationLevel.Order notified so far by the escalation state
    // machine (see SentinelOps.Workers.Escalation). Null until an escalation
    // policy has actually been engaged for this incident — a schedule-only
    // assignment with no applicable policy never sets this.
    public int? CurrentEscalationLevel { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public Service? Service { get; set; }
}

public class IncidentComment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public Guid AuthorUserId { get; set; }
    public required string Body { get; set; }
    public bool IsInternal { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}

// Append-only: no controller ever exposes an update/delete path for this entity.
public class IncidentStatusHistory : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public IncidentStatus? FromStatus { get; set; }
    public IncidentStatus ToStatus { get; set; }
    public Guid ChangedByUserId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}

// Normalized child table rather than an array/jsonb column, so tag filtering is a
// plain join, consistent with the rest of the fluent-API-only mapping style.
public class IncidentTag : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public required string Tag { get; set; }

    public Incident? Incident { get; set; }
}

// Stored once per direction (both rows inserted on link) so reads never need an
// OR across two columns.
public class RelatedIncidentLink : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public Guid RelatedIncidentId { get; set; }
}
