namespace SentinelOps.Api.Domain;

// Immutable by construction: no controller ever exposes an update/delete path
// for this entity. Unlike other business entities, OrganizationId is nullable
// (some audited actions, e.g. login, happen before any organization context
// has been resolved) so this does not implement ITenantOwned; its query
// filter is configured directly in SentinelOpsDbContext instead.
public class AuditLog
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
