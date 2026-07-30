namespace SentinelOps.Api.Domain;

public enum InvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Revoked = 2,
}

public class OrganizationInvitation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Email { get; set; }
    public OrganizationRole Role { get; set; }
    public required string Token { get; set; }
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;
    public Guid InvitedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }

    public Organization? Organization { get; set; }
}
