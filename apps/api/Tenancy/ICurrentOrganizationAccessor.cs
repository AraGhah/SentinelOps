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

    // For callers with no user membership at all — e.g. an integration API key,
    // which is scoped to the organization that owns it but has no role/membership.
    void Set(Guid organizationId);
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

    public void Set(Guid organizationId)
    {
        OrganizationId = organizationId;
        Role = null;
        MembershipId = null;
    }
}
