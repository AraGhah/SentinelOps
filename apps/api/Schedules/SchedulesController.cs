using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Schedules;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/schedules")]
public class SchedulesController(SentinelOpsDbContext db, ICurrentUserService currentUserService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<ScheduleResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, CancellationToken ct)
    {
        var result = await db.Schedules
            .Where(s => s.OrganizationId == orgId)
            .OrderBy(s => s.Name)
            .Select(s => new ScheduleResponse(s.Id, s.Name, s.ServiceId, s.TimeZoneId, s.CreatedAtUtc, s.UpdatedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }

    [HttpGet("{scheduleId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<ScheduleResponse>> Get(Guid orgId, Guid scheduleId, CancellationToken ct)
    {
        var schedule = await Find(orgId, scheduleId, ct);
        if (schedule is null) return NotFound();

        return Ok(ToResponse(schedule));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<ScheduleResponse>> Create(Guid orgId, CreateScheduleRequest request, CancellationToken ct)
    {
        if (TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId) is null)
        {
            return Problem(title: "Invalid request", detail: "Unknown time zone id.", statusCode: 400);
        }

        var now = DateTimeOffset.UtcNow;
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = request.Name.Trim(),
            ServiceId = request.ServiceId,
            TimeZoneId = request.TimeZoneId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(schedule));
    }

    [HttpPut("{scheduleId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<ScheduleResponse>> Update(
        Guid orgId, Guid scheduleId, UpdateScheduleRequest request, CancellationToken ct)
    {
        var schedule = await Find(orgId, scheduleId, ct);
        if (schedule is null) return NotFound();

        schedule.Name = request.Name.Trim();
        schedule.ServiceId = request.ServiceId;
        schedule.TimeZoneId = request.TimeZoneId;
        schedule.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ToResponse(schedule));
    }

    [HttpDelete("{scheduleId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Delete(Guid orgId, Guid scheduleId, CancellationToken ct)
    {
        var schedule = await Find(orgId, scheduleId, ct);
        if (schedule is null) return NotFound();

        db.Schedules.Remove(schedule);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{scheduleId:guid}/rotations")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<RotationResponse>>> ListRotations(Guid orgId, Guid scheduleId, CancellationToken ct)
    {
        var rotations = await db.ScheduleRotations
            .Where(r => r.OrganizationId == orgId && r.ScheduleId == scheduleId)
            .OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartTimeLocal)
            .Select(r => new RotationResponse(
                r.Id, r.ResponderUserId, r.DayOfWeek, r.StartTimeLocal, r.EndTimeLocal, r.EffectiveFromUtc, r.EffectiveToUtc, r.IsBackup))
            .ToListAsync(ct);

        return Ok(rotations);
    }

    [HttpPost("{scheduleId:guid}/rotations")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<RotationResponse>> AddRotation(
        Guid orgId, Guid scheduleId, CreateRotationRequest request, CancellationToken ct)
    {
        var scheduleExists = await db.Schedules.AnyAsync(s => s.OrganizationId == orgId && s.Id == scheduleId, ct);
        if (!scheduleExists) return NotFound();

        var rotation = new ScheduleRotation
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ScheduleId = scheduleId,
            ResponderUserId = request.ResponderUserId,
            DayOfWeek = request.DayOfWeek,
            StartTimeLocal = request.StartTimeLocal,
            EndTimeLocal = request.EndTimeLocal,
            EffectiveFromUtc = request.EffectiveFromUtc,
            EffectiveToUtc = request.EffectiveToUtc,
            IsBackup = request.IsBackup,
        };

        db.ScheduleRotations.Add(rotation);
        await db.SaveChangesAsync(ct);

        return Ok(new RotationResponse(
            rotation.Id, rotation.ResponderUserId, rotation.DayOfWeek, rotation.StartTimeLocal, rotation.EndTimeLocal,
            rotation.EffectiveFromUtc, rotation.EffectiveToUtc, rotation.IsBackup));
    }

    [HttpDelete("{scheduleId:guid}/rotations/{rotationId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> RemoveRotation(Guid orgId, Guid scheduleId, Guid rotationId, CancellationToken ct)
    {
        var rotation = await db.ScheduleRotations
            .FirstOrDefaultAsync(r => r.OrganizationId == orgId && r.ScheduleId == scheduleId && r.Id == rotationId, ct);
        if (rotation is null) return NotFound();

        db.ScheduleRotations.Remove(rotation);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{scheduleId:guid}/overrides")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<OverrideResponse>>> ListOverrides(Guid orgId, Guid scheduleId, CancellationToken ct)
    {
        var overrides = await db.ScheduleOverrides
            .Where(o => o.OrganizationId == orgId && o.ScheduleId == scheduleId)
            .OrderByDescending(o => o.StartsAtUtc)
            .Select(o => new OverrideResponse(
                o.Id, o.ResponderUserId, o.OriginalResponderUserId, o.StartsAtUtc, o.EndsAtUtc, o.Reason, o.CreatedByUserId, o.CreatedAtUtc))
            .ToListAsync(ct);

        return Ok(overrides);
    }

    [HttpPost("{scheduleId:guid}/overrides")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<OverrideResponse>> AddOverride(
        Guid orgId, Guid scheduleId, CreateOverrideRequest request, CancellationToken ct)
    {
        var scheduleExists = await db.Schedules.AnyAsync(s => s.OrganizationId == orgId && s.Id == scheduleId, ct);
        if (!scheduleExists) return NotFound();

        if (request.EndsAtUtc <= request.StartsAtUtc)
        {
            return Problem(title: "Invalid request", detail: "EndsAtUtc must be after StartsAtUtc.", statusCode: 400);
        }

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var scheduleOverride = new ScheduleOverride
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ScheduleId = scheduleId,
            ResponderUserId = request.ResponderUserId,
            OriginalResponderUserId = request.OriginalResponderUserId,
            StartsAtUtc = request.StartsAtUtc,
            EndsAtUtc = request.EndsAtUtc,
            Reason = request.Reason,
            CreatedByUserId = actor.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.ScheduleOverrides.Add(scheduleOverride);
        await db.SaveChangesAsync(ct);

        return Ok(new OverrideResponse(
            scheduleOverride.Id, scheduleOverride.ResponderUserId, scheduleOverride.OriginalResponderUserId,
            scheduleOverride.StartsAtUtc, scheduleOverride.EndsAtUtc, scheduleOverride.Reason,
            scheduleOverride.CreatedByUserId, scheduleOverride.CreatedAtUtc));
    }

    [HttpDelete("{scheduleId:guid}/overrides/{overrideId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> RemoveOverride(Guid orgId, Guid scheduleId, Guid overrideId, CancellationToken ct)
    {
        var scheduleOverride = await db.ScheduleOverrides
            .FirstOrDefaultAsync(o => o.OrganizationId == orgId && o.ScheduleId == scheduleId && o.Id == overrideId, ct);
        if (scheduleOverride is null) return NotFound();

        db.ScheduleOverrides.Remove(scheduleOverride);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Delegates to OnCallResolver (also used by the ResponderAssignment
    // worker) so "who's on call" answers the same way here as it does when
    // the system actually assigns an incident.
    [HttpGet("{scheduleId:guid}/on-call")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<OnCallResponse>> GetOnCall(Guid orgId, Guid scheduleId, CancellationToken ct)
    {
        var schedule = await Find(orgId, scheduleId, ct);
        if (schedule is null) return NotFound();

        var nowUtc = DateTimeOffset.UtcNow;

        var activeOverride = await db.ScheduleOverrides
            .Where(o => o.OrganizationId == orgId && o.ScheduleId == scheduleId
                && o.StartsAtUtc <= nowUtc && nowUtc < o.EndsAtUtc)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (activeOverride is not null)
        {
            return Ok(new OnCallResponse(activeOverride.ResponderUserId, "override"));
        }

        var rotations = await db.ScheduleRotations
            .Where(r => r.OrganizationId == orgId && r.ScheduleId == scheduleId)
            .ToListAsync(ct);

        var responderId = OnCallResolver.Resolve(schedule, rotations, [], nowUtc);
        return Ok(new OnCallResponse(responderId, responderId is null ? "none" : "rotation"));
    }

    private Task<Schedule?> Find(Guid orgId, Guid scheduleId, CancellationToken ct) =>
        db.Schedules.FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == scheduleId, ct);

    private static ScheduleResponse ToResponse(Schedule s) => new(s.Id, s.Name, s.ServiceId, s.TimeZoneId, s.CreatedAtUtc, s.UpdatedAtUtc);
}
