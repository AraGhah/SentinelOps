namespace SentinelOps.Api.Domain;

public class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public OrganizationSettings? Settings { get; set; }
    public List<OrganizationMembership> Memberships { get; set; } = [];
    public List<OrganizationInvitation> Invitations { get; set; } = [];
}
