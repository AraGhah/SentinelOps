using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Notifications;

// Self-service only — a user manages their own quiet hours/channel toggle,
// no endpoint for an administrator to manage someone else's.
[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/notification-preferences/me")]
public class NotificationPreferencesController(SentinelOpsDbContext db, ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<NotificationPreferenceResponse>> Get(Guid orgId, CancellationToken ct)
    {
        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var preference = await db.NotificationPreferences
            .FirstOrDefaultAsync(p => p.OrganizationId == orgId && p.UserId == actor.Id, ct);

        // No row yet means all defaults — return them rather than 404, since
        // "not configured" is a valid, common state.
        return Ok(preference is null
            ? new NotificationPreferenceResponse(true, null, null, null, default)
            : ToResponse(preference));
    }

    [HttpPut]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<NotificationPreferenceResponse>> Update(
        Guid orgId, UpdateNotificationPreferenceRequest request, CancellationToken ct)
    {
        if ((request.QuietHoursStartLocal is null) != (request.QuietHoursEndLocal is null))
        {
            return Problem(
                title: "Invalid request", detail: "Quiet hours start and end must both be set, or both left null.", statusCode: 400);
        }
        if (request.QuietHoursStartLocal is not null && string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            return Problem(title: "Invalid request", detail: "Quiet hours require a time zone.", statusCode: 400);
        }
        if (request.TimeZoneId is not null && !TimeZoneValidation.IsValid(request.TimeZoneId))
        {
            return Problem(title: "Invalid request", detail: "Unknown time zone id.", statusCode: 400);
        }

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var preference = await db.NotificationPreferences
            .FirstOrDefaultAsync(p => p.OrganizationId == orgId && p.UserId == actor.Id, ct);

        if (preference is null)
        {
            preference = new NotificationPreference { Id = Guid.NewGuid(), OrganizationId = orgId, UserId = actor.Id };
            db.NotificationPreferences.Add(preference);
        }

        preference.EmailEnabled = request.EmailEnabled;
        preference.QuietHoursStartLocal = request.QuietHoursStartLocal;
        preference.QuietHoursEndLocal = request.QuietHoursEndLocal;
        preference.TimeZoneId = request.TimeZoneId;
        preference.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(preference));
    }

    private static NotificationPreferenceResponse ToResponse(NotificationPreference p) => new(
        p.EmailEnabled, p.QuietHoursStartLocal, p.QuietHoursEndLocal, p.TimeZoneId, p.UpdatedAtUtc);
}
