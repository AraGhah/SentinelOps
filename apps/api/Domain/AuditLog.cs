namespace SentinelOps.Api.Domain;

// Immutable: no controller exposes an update/delete path. OrganizationId is nullable
// (some audited actions, e.g. login, happen before org context is resolved), so this
// doesn't implement ITenantOwned; its query filter is configured directly in SentinelOpsDbContext.
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
