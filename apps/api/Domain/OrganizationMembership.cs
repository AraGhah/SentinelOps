namespace SentinelOps.Api.Domain;

public class OrganizationMembership : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public OrganizationRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid InvitedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? DeactivatedByUserId { get; set; }
    public DateTimeOffset? DeactivatedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public User? User { get; set; }
}
