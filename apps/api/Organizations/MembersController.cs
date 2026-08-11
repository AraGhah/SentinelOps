using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Organizations;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/members")]
public class MembersController(
    SentinelOpsDbContext db,
    ICurrentUserService currentUserService,
    ICurrentOrganizationAccessor currentOrganization,
    IAuditLogger auditLogger)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<MemberResponse>>> List(Guid orgId, CancellationToken ct)
    {
        var members = await db.OrganizationMemberships
            .Include(m => m.User)
            .Where(m => m.OrganizationId == orgId)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => new MemberResponse(m.Id, m.UserId, m.User!.Email, m.Role, m.IsActive, m.CreatedAtUtc))
            .ToListAsync(ct);

        return Ok(members);
    }

    [HttpPut("{membershipId:guid}/role")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> UpdateRole(Guid orgId, Guid membershipId, UpdateMemberRoleRequest request, CancellationToken ct)
    {
        // Administrators may promote/demote anyone except Owners; only an Owner can
        // change another Owner's role or hand out the Owner role, preventing an
        // Administrator from silently displacing the org's Owner.
        if ((request.Role == OrganizationRole.Owner || await IsOwnerAsync(orgId, membershipId, ct))
            && currentOrganization.Role != OrganizationRole.Owner)
        {
            return Problem(title: "Forbidden", detail: "Only an Owner can grant or change the Owner role.", statusCode: 403);
        }

        var membership = await db.OrganizationMemberships
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.Id == membershipId, ct);
        if (membership is null) return NotFound();

        if (membership.Role == OrganizationRole.Owner && request.Role != OrganizationRole.Owner)
        {
            var remainingOwners = await db.OrganizationMemberships
                .CountAsync(m => m.OrganizationId == orgId && m.Role == OrganizationRole.Owner
                    && m.IsActive && m.Id != membershipId, ct);
            if (remainingOwners == 0)
            {
                return Problem(title: "Invalid request", detail: "An organization must have at least one Owner.", statusCode: 400);
            }
        }

        var fromRole = membership.Role;
        membership.Role = request.Role;
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "member.role_changed", nameof(OrganizationMembership), membership.Id, new { From = fromRole, To = request.Role }, ct);

        return NoContent();
    }

    [HttpPost("{membershipId:guid}/deactivate")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Deactivate(Guid orgId, Guid membershipId, CancellationToken ct)
    {
        var membership = await db.OrganizationMemberships
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.Id == membershipId, ct);
        if (membership is null) return NotFound();

        if (membership.Role == OrganizationRole.Owner)
        {
            var remainingOwners = await db.OrganizationMemberships
                .CountAsync(m => m.OrganizationId == orgId && m.Role == OrganizationRole.Owner
                    && m.IsActive && m.Id != membershipId, ct);
            if (remainingOwners == 0)
            {
                return Problem(title: "Invalid request", detail: "An organization must have at least one active Owner.", statusCode: 400);
            }
        }

        if (!membership.IsActive) return NoContent();

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        membership.IsActive = false;
        membership.DeactivatedAtUtc = DateTimeOffset.UtcNow;
        membership.DeactivatedByUserId = actor.Id;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("member.deactivated", nameof(OrganizationMembership), membership.Id, null, ct);
        return NoContent();
    }

    [HttpPost("{membershipId:guid}/reactivate")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Reactivate(Guid orgId, Guid membershipId, CancellationToken ct)
    {
        var membership = await db.OrganizationMemberships
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.Id == membershipId, ct);
        if (membership is null) return NotFound();

        membership.IsActive = true;
        membership.DeactivatedAtUtc = null;
        membership.DeactivatedByUserId = null;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("member.reactivated", nameof(OrganizationMembership), membership.Id, null, ct);
        return NoContent();
    }

    private async Task<bool> IsOwnerAsync(Guid orgId, Guid membershipId, CancellationToken ct) =>
        await db.OrganizationMemberships
            .AnyAsync(m => m.OrganizationId == orgId && m.Id == membershipId && m.Role == OrganizationRole.Owner, ct);
}
