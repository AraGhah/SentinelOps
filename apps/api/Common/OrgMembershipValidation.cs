using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;

namespace SentinelOps.Api.Common;

// Shared guard for every Guid field that's supposed to reference a user
// within the current organization (assigned responder, schedule rotation
// responder, escalation target, fallback administrator, ...). Without this,
// nothing stops a caller from pointing one of these fields at an arbitrary
// (or another org's) user id.
public static class OrgMembershipValidation
{
    public static Task<bool> IsActiveMemberAsync(this SentinelOpsDbContext db, Guid orgId, Guid userId, CancellationToken ct) =>
        db.OrganizationMemberships.AnyAsync(m => m.OrganizationId == orgId && m.UserId == userId && m.IsActive, ct);
}
