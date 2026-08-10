using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Notifications;

// Read-only notification history — section 18's "Add notification history".
[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/notifications")]
public class NotificationsController(SentinelOpsDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<NotificationResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, [FromQuery] NotificationFilters filters, CancellationToken ct)
    {
        var query = db.Notifications.Where(n => n.OrganizationId == orgId).AsQueryable();

        if (filters.IncidentId is not null) query = query.Where(n => n.IncidentId == filters.IncidentId);
        if (filters.RecipientUserId is not null) query = query.Where(n => n.RecipientUserId == filters.RecipientUserId);
        if (filters.Status is not null)
        {
            if (!Enum.TryParse<NotificationStatus>(filters.Status, ignoreCase: true, out var status))
            {
                return Problem(title: "Invalid request", detail: $"Unknown notification status '{filters.Status}'.", statusCode: 400);
            }
            query = query.Where(n => n.Status == status);
        }

        var result = await query
            .OrderByDescending(n => n.RequestedAtUtc)
            .Select(n => new NotificationResponse(
                n.Id, n.IncidentId, n.RecipientUserId, n.Channel, n.Kind.ToString(), n.Status.ToString(),
                n.FailureReason, n.RequestedAtUtc, n.DeliveredAtUtc, n.FailedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }
}
