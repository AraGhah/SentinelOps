namespace SentinelOps.Api.Domain;

// Marker for any entity scoped to a single organization. SentinelOpsDbContext
// applies a global query filter to every ITenantOwned entity keyed off
// ICurrentOrganizationAccessor, so new tenant-owned tables get row isolation
// for free just by implementing this interface.
public interface ITenantOwned
{
    Guid OrganizationId { get; set; }
}
