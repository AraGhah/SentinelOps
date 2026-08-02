using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Audit;

// Read-only by design: audit entries are written exclusively via IAuditLogger
// from other controllers, never through an endpoint here.
[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/audit-logs")]
public class AuditLogsController(SentinelOpsDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<PagedResult<AuditLogResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, [FromQuery] AuditLogFilters filters, CancellationToken ct)
    {
        var query = db.AuditLogs.Where(a => a.OrganizationId == orgId).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filters.Action)) query = query.Where(a => a.Action == filters.Action);
        if (!string.IsNullOrWhiteSpace(filters.EntityType)) query = query.Where(a => a.EntityType == filters.EntityType);
        if (filters.ActorUserId is not null) query = query.Where(a => a.ActorUserId == filters.ActorUserId);
        if (filters.From is not null) query = query.Where(a => a.CreatedAtUtc >= filters.From);
        if (filters.To is not null) query = query.Where(a => a.CreatedAtUtc < filters.To);

        var result = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => new AuditLogResponse(
                a.Id, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.IpAddress, a.UserAgent, a.Details, a.CreatedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }
}
