using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;

namespace SentinelOps.Api.Common;

// Guard for Guid fields that must reference a user within the current org
// (assigned responder, escalation target, etc.); prevents pointing them at
// an arbitrary or another org's user id.
public static class OrgMembershipValidation
{
    public static Task<bool> IsActiveMemberAsync(this SentinelOpsDbContext db, Guid orgId, Guid userId, CancellationToken ct) =>
        db.OrganizationMemberships.AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId && m.IsActive, ct);
}
