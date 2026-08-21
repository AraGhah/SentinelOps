using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Schedules;

namespace SentinelOps.Workers.Shared;

// Shared between ResponderAssignment (on incident.created) and Escalation's
// Restart handler (on a reopened incident): resolve who should be notified
// right now — on-call schedule first, escalation policy's lowest-order level
// as fallback.
public static class ResponderResolver
{
    public static async Task<Guid?> ResolveAsync(SentinelOpsDbContext db, Guid? serviceId, DateTimeOffset nowUtc)
    {
        if (serviceId is not null)
        {
            var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.ServiceId == serviceId);
            if (schedule is not null)
            {
                var rotations = await db.ScheduleRotations.Where(r => r.ScheduleId == schedule.Id).ToListAsync();
                var overrides = await db.ScheduleOverrides.Where(o => o.ScheduleId == schedule.Id).ToListAsync();
                var onCall = OnCallResolver.Resolve(schedule, rotations, overrides, nowUtc);
                if (onCall is not null) return onCall;
            }
        }

        var policy = await ResolveEscalationPolicyAsync(db, serviceId);
        var firstLevel = policy?.Levels.OrderBy(l => l.Order).FirstOrDefault();
        var firstTarget = firstLevel?.Targets.FirstOrDefault();
        return firstTarget?.UserId;
    }

    // Service-scoped policy first, falling back to org-wide default
    // (ServiceId == null). An incident with no applicable policy is assigned
    // but never escalates.
    public static async Task<EscalationPolicy?> ResolveEscalationPolicyAsync(SentinelOpsDbContext db, Guid? serviceId) =>
        await db.EscalationPolicies
            .Include(p => p.Levels).ThenInclude(l => l.Targets)
            .Where(p => p.ServiceId == serviceId)
            .FirstOrDefaultAsync()
        ?? await db.EscalationPolicies
            .Include(p => p.Levels).ThenInclude(l => l.Targets)
            .Where(p => p.ServiceId == null)
            .FirstOrDefaultAsync();
}
