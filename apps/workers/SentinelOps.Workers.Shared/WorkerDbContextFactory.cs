using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Workers.Shared;

// Workers run outside any HTTP request, so there's no
// OrganizationRoleAuthorizationHandler to populate ICurrentOrganizationAccessor.
// Builds a DbContext with the OrganizationId pre-set from the event instead —
// one instance per message, since the accessor is scoped per-message, not shared.
public static class WorkerDbContextFactory
{
    public static SentinelOpsDbContext Create(string connectionString, Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<SentinelOpsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var currentOrganization = new CurrentOrganizationAccessor();
        currentOrganization.Set(organizationId);

        return new SentinelOpsDbContext(options, currentOrganization);
    }

    // For workers (analytics, audit-log) that write rows not scoped to a
    // single organization's query filter.
    public static SentinelOpsDbContext CreateUnscoped(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SentinelOpsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new SentinelOpsDbContext(options, new CurrentOrganizationAccessor());
    }
}
