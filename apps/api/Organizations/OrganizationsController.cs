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
[Route("api/v1/organizations")]
public class OrganizationsController(SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger)
    : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrganizationResponse>> Create(CreateOrganizationRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return Problem(title: "Invalid request", detail: "Organization name is required.", statusCode: 400);
        }

        var user = await currentUserService.GetOrProvisionAsync(ct);
        var slug = await GenerateUniqueSlugAsync(name, ct);

        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            CreatedByUserId = user.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var membership = new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = OrganizationRole.Owner,
            IsActive = true,
            InvitedByUserId = user.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var settings = new OrganizationSettings
        {
            OrganizationId = organization.Id,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Organizations.Add(organization);
        // Tenant query filters would hide these newly-created rows from a re-read in
        // the same request (currentOrganization isn't set yet), but Add + SaveChanges
        // doesn't need the filter, so this is safe.
        db.OrganizationMemberships.Add(membership);
        db.OrganizationSettings.Add(settings);

        user.LastActiveOrganizationId = organization.Id;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "organization.created", nameof(Organization), organization.Id, new { organization.Name }, ct,
            organizationId: organization.Id);

        return Ok(new OrganizationResponse(organization.Id, organization.Name, organization.Slug, organization.CreatedAtUtc));
    }

    [HttpGet]
    public async Task<ActionResult<List<MyOrganizationResponse>>> ListMine(CancellationToken ct)
    {
        var user = await currentUserService.GetOrProvisionAsync(ct);

        // Lists every org the caller belongs to; explicit UserId predicate keeps this
        // scoped to the caller, since there's no single "current org" to filter by.
        var memberships = await db.OrganizationMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id && m.IsActive)
            .Include(m => m.Organization)
            .ToListAsync(ct);

        var result = memberships
            .Select(m => new MyOrganizationResponse(
                m.OrganizationId, m.Organization!.Name, m.Organization.Slug, m.Role,
                m.OrganizationId == user.LastActiveOrganizationId))
            .ToList();

        return Ok(result);
    }

    [HttpGet("current")]
    public async Task<ActionResult<MyOrganizationResponse?>> GetCurrent(CancellationToken ct)
    {
        var user = await currentUserService.GetOrProvisionAsync(ct);
        if (user.LastActiveOrganizationId is null) return Ok(null);

        // Same reasoning as ListMine: resolves which org is "current," so an explicit
        // OrganizationId + UserId predicate takes the filter's place.
        var membership = await db.OrganizationMemberships
            .IgnoreQueryFilters()
            .Include(m => m.Organization)
            .FirstOrDefaultAsync(m =>
                m.OrganizationId == user.LastActiveOrganizationId && m.UserId == user.Id && m.IsActive, ct);

        if (membership is null) return Ok(null);

        return Ok(new MyOrganizationResponse(
            membership.OrganizationId, membership.Organization!.Name, membership.Organization.Slug,
            membership.Role, true));
    }

    [HttpPost("{orgId:guid}/switch")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<IActionResult> Switch(Guid orgId, CancellationToken ct)
    {
        var user = await currentUserService.GetOrProvisionAsync(ct);
        user.LastActiveOrganizationId = orgId;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{orgId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<OrganizationResponse>> Get(Guid orgId, CancellationToken ct)
    {
        var organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (organization is null) return NotFound();

        return Ok(new OrganizationResponse(organization.Id, organization.Name, organization.Slug, organization.CreatedAtUtc));
    }

    [HttpGet("{orgId:guid}/settings")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<OrganizationSettingsResponse>> GetSettings(Guid orgId, CancellationToken ct)
    {
        var settings = await db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (settings is null) return NotFound();

        return Ok(new OrganizationSettingsResponse(
            settings.TimeZone, settings.AlertNotificationEmail, settings.RequireMfaForMembers, settings.UpdatedAtUtc));
    }

    [HttpPut("{orgId:guid}/settings")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<OrganizationSettingsResponse>> UpdateSettings(
        Guid orgId, UpdateOrganizationSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (settings is null) return NotFound();

        if (!TimeZoneValidation.IsValid(request.TimeZone))
        {
            return Problem(title: "Invalid request", detail: "Unknown time zone id.", statusCode: 400);
        }

        settings.TimeZone = request.TimeZone;
        settings.AlertNotificationEmail = request.AlertNotificationEmail;
        settings.RequireMfaForMembers = request.RequireMfaForMembers;
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("organization.settings_updated", nameof(OrganizationSettings), orgId, null, ct);

        return Ok(new OrganizationSettingsResponse(
            settings.TimeZone, settings.AlertNotificationEmail, settings.RequireMfaForMembers, settings.UpdatedAtUtc));
    }

    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = string.Join('-', name
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Aggregate(new System.Text.StringBuilder(), (sb, c) => sb.Append(c))
            .ToString();

        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "organization";

        // Organization is the tenant root itself (no OrganizationId, no query filter);
        // slugs must be unique across every org, so this checks the whole table.
        var slug = baseSlug;
        var suffix = 1;
        while (await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == slug, ct))
        {
            suffix++;
            slug = $"{baseSlug}-{suffix}";
        }

        return slug;
    }
}
