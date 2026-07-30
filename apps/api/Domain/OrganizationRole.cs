namespace SentinelOps.Api.Domain;

// Ordinal order matters: higher value == more privilege. Authorization checks
// compare Role >= requirement.MinimumRole, so never reorder these members.
public enum OrganizationRole
{
    Viewer = 0,
    Responder = 1,
    Administrator = 2,
    Owner = 3,
}
