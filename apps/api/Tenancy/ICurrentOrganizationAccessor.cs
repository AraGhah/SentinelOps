using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Tenancy;

// Scoped per-request. Left unset (null) by default so the DbContext's global
// query filter is default-closed: ITenantOwned rows are invisible until an
// authorization handler resolves and validates the caller's membership for a
// specific organization and populates this accessor.
public interface ICurrentOrganizationAccessor
{
    Guid? OrganizationId { get; }
    OrganizationRole? Role { get; }
    Guid? MembershipId { get; }

    void Set(Guid organizationId, OrganizationRole role, Guid membershipId);
}

public class CurrentOrganizationAccessor : ICurrentOrganizationAccessor
{
    public Guid? OrganizationId { get; private set; }
    public OrganizationRole? Role { get; private set; }
    public Guid? MembershipId { get; private set; }

    public void Set(Guid organizationId, OrganizationRole role, Guid membershipId)
    {
        OrganizationId = organizationId;
        Role = role;
        MembershipId = membershipId;
    }
}
