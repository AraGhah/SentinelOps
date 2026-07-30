using Microsoft.AspNetCore.Authorization;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Tenancy;

// Registered once per OrganizationRole as policy "OrgRole:{role}" (see Program.cs).
// Viewer is the lowest role, so "OrgRole:Viewer" effectively means "any active member".
public class OrganizationRoleRequirement(OrganizationRole minimumRole) : IAuthorizationRequirement
{
    public OrganizationRole MinimumRole { get; } = minimumRole;
}
