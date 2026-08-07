using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Workers.Shared;

// Workers run outside any HTTP request, so there's no OrganizationRoleAuthorizationHandler
// to populate ICurrentOrganizationAccessor the way controllers rely on. Every worker
// instead knows the OrganizationId straight from the event it's handling, so this
// builds a DbContext with that value pre-set — one instance per message, since
// ICurrentOrganizationAccessor is meant to be scoped-per-request/message, not shared.
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

    // A handful of workers (analytics, audit-log) intentionally write rows that
    // aren't scoped to a single organization's query filter — they use this
    // instead, with an accessor that never resolves an OrganizationId.
    public static SentinelOpsDbContext CreateUnscoped(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SentinelOpsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new SentinelOpsDbContext(options, new CurrentOrganizationAccessor());
    }
}
