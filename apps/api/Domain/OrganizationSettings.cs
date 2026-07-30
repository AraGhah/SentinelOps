namespace SentinelOps.Api.Domain;

// 1:1 with Organization (OrganizationId is both PK and FK). Kept as its own
// table rather than columns on Organization so it can grow without churning
// the tenant root entity, and so future tenant-owned tables can follow the
// same "one row per org" shape.
public class OrganizationSettings : ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string? AlertNotificationEmail { get; set; }
    public bool RequireMfaForMembers { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
