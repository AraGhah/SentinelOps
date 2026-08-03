namespace SentinelOps.Api.Domain;

public enum OrganizationStatus { Active = 0, Suspended = 1 }

public class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public Guid CreatedByUserId { get; set; }
    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public OrganizationSettings? Settings { get; set; }
    public List<OrganizationMembership> Memberships { get; set; } = [];
    public List<OrganizationInvitation> Invitations { get; set; } = [];
}
