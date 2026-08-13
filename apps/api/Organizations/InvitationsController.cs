using System.Security.Cryptography;
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
[Route("api/v1/organizations/{orgId:guid}/invitations")]
public class InvitationsController(SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger)
    : ControllerBase
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    [HttpGet]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<List<InvitationResponse>>> List(Guid orgId, CancellationToken ct)
    {
        var invitations = await db.OrganizationInvitations
            .Where(i => i.OrganizationId == orgId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new InvitationResponse(i.Id, i.Email, i.Role, i.Status, i.ExpiresAtUtc, i.Token))
            .ToListAsync(ct);

        return Ok(invitations);
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<InvitationResponse>> Create(Guid orgId, CreateInvitationRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
        {
            return Problem(title: "Invalid request", detail: "Email is required.", statusCode: 400);
        }

        // Only an Owner can invite another Owner, same rationale as role changes.
        if (request.Role == OrganizationRole.Owner && CurrentRole(HttpContext) != OrganizationRole.Owner)
        {
            return Problem(title: "Forbidden", detail: "Only an Owner can invite another Owner.", statusCode: 403);
        }

        var alreadyMember = await db.OrganizationMemberships
            .AnyAsync(m => m.OrganizationId == orgId && m.User!.Email == email, ct);
        if (alreadyMember)
        {
            return Problem(title: "Invalid request", detail: "This user is already a member of the organization.", statusCode: 409);
        }

        var existingPending = await db.OrganizationInvitations
            .FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Email == email && i.Status == InvitationStatus.Pending, ct);
        if (existingPending is not null)
        {
            existingPending.Status = InvitationStatus.Revoked;
        }

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var invitation = new OrganizationInvitation
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Email = email,
            Role = request.Role,
            Token = GenerateToken(),
            Status = InvitationStatus.Pending,
            InvitedByUserId = actor.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(InvitationLifetime),
        };

        db.OrganizationInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "invitation.created", nameof(OrganizationInvitation), invitation.Id, new { invitation.Email, invitation.Role }, ct);

        return Ok(new InvitationResponse(
            invitation.Id, invitation.Email, invitation.Role, invitation.Status, invitation.ExpiresAtUtc, invitation.Token));
    }

    [HttpDelete("{invitationId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Revoke(Guid orgId, Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.OrganizationInvitations
            .FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == invitationId, ct);
        if (invitation is null) return NotFound();

        if (invitation.Status == InvitationStatus.Pending)
        {
            invitation.Status = InvitationStatus.Revoked;
            await db.SaveChangesAsync(ct);
            await auditLogger.LogAsync("invitation.revoked", nameof(OrganizationInvitation), invitation.Id, null, ct);
        }

        return NoContent();
    }

    private static OrganizationRole? CurrentRole(HttpContext context) =>
        context.RequestServices.GetRequiredService<ICurrentOrganizationAccessor>().Role;

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

// Accepting an invitation happens before the caller has any organization
// membership, so it lives outside the {orgId} route and its authorization
// requirement — any authenticated user may attempt it, and the handler itself
// validates the token/email match.
[ApiController]
[Authorize]
[Route("api/v1/invitations")]
public class InvitationAcceptanceController(SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger)
    : ControllerBase
{
    [HttpPost("accept")]
    public async Task<ActionResult<MyOrganizationResponse>> Accept(AcceptInvitationRequest request, CancellationToken ct)
    {
        // The caller isn't a member of the target org yet — that's the whole
        // point of accepting an invitation — so there's no org to filter by.
        // Token is a random 64-char secret (OrganizationInvitation.Token),
        // not enumerable, so this is safe: it's effectively a lookup key, not
        // a listing.
        var invitation = await db.OrganizationInvitations
            .IgnoreQueryFilters()
            .Include(i => i.Organization)
            .FirstOrDefaultAsync(i => i.Token == request.Token, ct);

        if (invitation is null || invitation.Status != InvitationStatus.Pending || invitation.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return Problem(title: "Invalid invitation", detail: "This invitation is invalid or has expired.", statusCode: 400);
        }

        var user = await currentUserService.GetOrProvisionAsync(ct);
        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(title: "Invalid invitation", detail: "This invitation was issued to a different email address.", statusCode: 403);
        }

        // Same reason as above: checking for an existing membership in
        // invitation.OrganizationId is what determines whether the caller is
        // already a member of that org — can't apply a filter keyed on an org
        // membership this query exists to establish/confirm.
        var existingMembership = await db.OrganizationMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == user.Id, ct);

        if (existingMembership is null)
        {
            db.OrganizationMemberships.Add(new Domain.OrganizationMembership
            {
                Id = Guid.NewGuid(),
                OrganizationId = invitation.OrganizationId,
                UserId = user.Id,
                Role = invitation.Role,
                IsActive = true,
                InvitedByUserId = invitation.InvitedByUserId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existingMembership.IsActive = true;
            existingMembership.Role = invitation.Role;
        }

        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedByUserId = user.Id;
        invitation.AcceptedAtUtc = DateTimeOffset.UtcNow;
        user.LastActiveOrganizationId = invitation.OrganizationId;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "invitation.accepted", nameof(OrganizationInvitation), invitation.Id, null, ct,
            organizationId: invitation.OrganizationId);

        return Ok(new MyOrganizationResponse(
            invitation.OrganizationId, invitation.Organization!.Name, invitation.Organization.Slug, invitation.Role, true));
    }
}
