using System.Text.Json;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Common;

public interface IAuditLogger
{
    Task LogAsync(
        string action, string? entityType = null, Guid? entityId = null, object? details = null,
        CancellationToken ct = default, Guid? organizationId = null);
}

// Writes its own SaveChangesAsync, separate from whatever business-entity
// changes the caller is also saving, so a failure to persist the audit row
// never gets silently folded into an unrelated business transaction.
public class AuditLogger(
    SentinelOpsDbContext db,
    ICurrentUserService currentUserService,
    ICurrentOrganizationAccessor currentOrganization,
    IHttpContextAccessor httpContextAccessor)
    : IAuditLogger
{
    // organizationId overrides currentOrganization.OrganizationId for actions
    // taken outside an {orgId}-scoped route (e.g. organization creation, or
    // invitation acceptance before membership exists) where the accessor
    // hasn't been populated by OrganizationRoleAuthorizationHandler but the
    // organization the action concerns is still known to the caller.
    public async Task LogAsync(
        string action, string? entityType = null, Guid? entityId = null, object? details = null,
        CancellationToken ct = default, Guid? organizationId = null)
    {
        var httpContext = httpContextAccessor.HttpContext;

        Guid? actorUserId = null;
        try
        {
            actorUserId = (await currentUserService.GetOrProvisionAsync(ct)).Id;
        }
        catch (InvalidOperationException)
        {
            // No authenticated principal available (e.g. an anonymous auth failure) —
            // still record the event, just without an actor.
        }

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId ?? currentOrganization.OrganizationId,
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString(),
            Details = details is null ? null : JsonSerializer.Serialize(details),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
    }
}
