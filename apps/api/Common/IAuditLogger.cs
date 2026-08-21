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

// Calls its own SaveChangesAsync so a failure to persist the audit row doesn't
// get folded into the caller's business transaction.
public class AuditLogger(
    SentinelOpsDbContext db,
    ICurrentUserService currentUserService,
    ICurrentOrganizationAccessor currentOrganization,
    IHttpContextAccessor httpContextAccessor)
    : IAuditLogger
{
    // organizationId overrides currentOrganization.OrganizationId for actions outside an
    // {orgId}-scoped route (e.g. org creation, invitation acceptance before membership exists).
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
            // No authenticated principal (e.g. anonymous auth failure); still record without an actor.
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
