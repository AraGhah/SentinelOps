namespace SentinelOps.Api.Domain;

// Local shadow of the Cognito principal. Provisioned lazily on first
// authenticated request (see ICurrentUserService) so we have a stable Guid
// to hang organization memberships, invitations, and audit fields off of
// without depending on Cognito's sub format anywhere else in the schema.
public class User
{
    public Guid Id { get; set; }
    public required string CognitoSub { get; set; }
    public required string Email { get; set; }
    public Guid? LastActiveOrganizationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public List<OrganizationMembership> Memberships { get; set; } = [];
}
