using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.EscalationPolicies;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/escalation-policies")]
public class EscalationPoliciesController(SentinelOpsDbContext db, IAuditLogger auditLogger) : ControllerBase
{
    private const int MaxLevels = 10;

    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<EscalationPolicyResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, CancellationToken ct)
    {
        var query = db.EscalationPolicies
            .Include(p => p.Levels).ThenInclude(l => l.Targets)
            .Where(p => p.OrganizationId == orgId)
            .OrderBy(p => p.Name);

        var page = await query.ToPagedResultAsync(paging, ct);
        var items = page.Items.Select(ToResponse).ToList();

        return Ok(new PagedResult<EscalationPolicyResponse>(items, page.Page, page.PageSize, page.TotalCount));
    }

    [HttpGet("{policyId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<EscalationPolicyResponse>> Get(Guid orgId, Guid policyId, CancellationToken ct)
    {
        var policy = await Find(orgId, policyId, ct);
        if (policy is null) return NotFound();

        return Ok(ToResponse(policy));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<EscalationPolicyResponse>> Create(
        Guid orgId, CreateEscalationPolicyRequest request, CancellationToken ct)
    {
        var validationError = ValidateLevels(request.Levels);
        if (validationError is not null) return Problem(title: "Invalid request", detail: validationError, statusCode: 400);

        var memberError = await ValidateMembersAsync(
            orgId, request.FallbackAdministratorUserId, request.Levels.SelectMany(l => l.TargetUserIds), ct);
        if (memberError is not null) return Problem(title: "Invalid request", detail: memberError, statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var policy = new EscalationPolicy
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = request.Name.Trim(),
            Description = request.Description,
            ServiceId = request.ServiceId,
            FallbackAdministratorUserId = request.FallbackAdministratorUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        foreach (var levelRequest in request.Levels)
        {
            var level = new EscalationLevel
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                EscalationPolicyId = policy.Id,
                Order = levelRequest.Order,
                AckTimeoutMinutes = levelRequest.AckTimeoutMinutes,
            };
            level.Targets = levelRequest.TargetUserIds
                .Select(userId => new EscalationLevelTarget
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = orgId,
                    EscalationLevelId = level.Id,
                    UserId = userId,
                })
                .ToList();
            policy.Levels.Add(level);
        }

        db.EscalationPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("escalation_policy.created", nameof(EscalationPolicy), policy.Id, new { policy.Name }, ct);

        return Ok(ToResponse(policy));
    }

    [HttpPut("{policyId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<EscalationPolicyResponse>> Update(
        Guid orgId, Guid policyId, UpdateEscalationPolicyRequest request, CancellationToken ct)
    {
        var policy = await Find(orgId, policyId, ct);
        if (policy is null) return NotFound();

        var memberError = await ValidateMembersAsync(orgId, request.FallbackAdministratorUserId, [], ct);
        if (memberError is not null) return Problem(title: "Invalid request", detail: memberError, statusCode: 400);

        policy.Name = request.Name.Trim();
        policy.Description = request.Description;
        policy.ServiceId = request.ServiceId;
        policy.FallbackAdministratorUserId = request.FallbackAdministratorUserId;
        policy.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("escalation_policy.updated", nameof(EscalationPolicy), policy.Id, null, ct);

        return Ok(ToResponse(policy));
    }

    [HttpDelete("{policyId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Delete(Guid orgId, Guid policyId, CancellationToken ct)
    {
        var policy = await db.EscalationPolicies.FirstOrDefaultAsync(p => p.OrganizationId == orgId && p.Id == policyId, ct);
        if (policy is null) return NotFound();

        db.EscalationPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("escalation_policy.deleted", nameof(EscalationPolicy), policyId, null, ct);

        return NoContent();
    }

    [HttpPost("{policyId:guid}/levels")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<EscalationPolicyResponse>> AddLevel(
        Guid orgId, Guid policyId, EscalationLevelRequest request, CancellationToken ct)
    {
        var policy = await Find(orgId, policyId, ct);
        if (policy is null) return NotFound();

        if (policy.Levels.Count >= MaxLevels)
        {
            return Problem(title: "Invalid request", detail: $"A policy may have at most {MaxLevels} levels.", statusCode: 400);
        }
        if (policy.Levels.Any(l => l.Order == request.Order))
        {
            return Problem(title: "Invalid request", detail: "A level with this order already exists.", statusCode: 400);
        }
        if (request.TargetUserIds.Count == 0)
        {
            return Problem(title: "Invalid request", detail: "A level must have at least one target.", statusCode: 400);
        }

        var memberError = await ValidateMembersAsync(orgId, null, request.TargetUserIds, ct);
        if (memberError is not null) return Problem(title: "Invalid request", detail: memberError, statusCode: 400);

        var level = new EscalationLevel
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            EscalationPolicyId = policyId,
            Order = request.Order,
            AckTimeoutMinutes = request.AckTimeoutMinutes,
            Targets = request.TargetUserIds
                .Select(userId => new EscalationLevelTarget { Id = Guid.NewGuid(), OrganizationId = orgId, UserId = userId })
                .ToList(),
        };
        foreach (var target in level.Targets) target.EscalationLevelId = level.Id;

        db.EscalationLevels.Add(level);
        await db.SaveChangesAsync(ct);

        var refreshed = await Find(orgId, policyId, ct);
        return Ok(ToResponse(refreshed!));
    }

    [HttpDelete("{policyId:guid}/levels/{levelId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> RemoveLevel(Guid orgId, Guid policyId, Guid levelId, CancellationToken ct)
    {
        var policy = await Find(orgId, policyId, ct);
        if (policy is null) return NotFound();

        var level = policy.Levels.FirstOrDefault(l => l.Id == levelId);
        if (level is null) return NotFound();

        if (policy.Levels.Count <= 1)
        {
            return Problem(title: "Invalid request", detail: "A policy must have at least one level.", statusCode: 400);
        }

        db.EscalationLevels.Remove(level);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // "Validate escalation loops": levels only target individual users, no policy-to-policy
    // reference, so there's no cycle possible at CRUD time. The runtime equivalent (never
    // re-notifying a level, stopping after the fallback admin) is enforced by
    // SentinelOps.Workers.Escalation's AdvanceLevel action.
    private static string? ValidateLevels(List<EscalationLevelRequest> levels)
    {
        if (levels.Count == 0) return "A policy must have at least one escalation level.";
        if (levels.Count > MaxLevels) return $"A policy may have at most {MaxLevels} levels.";
        if (levels.Select(l => l.Order).Distinct().Count() != levels.Count) return "Escalation level order values must be unique.";
        if (levels.Any(l => l.TargetUserIds.Count == 0)) return "Every escalation level must have at least one target.";
        return null;
    }

    // Every target/fallback user must be an active member of this org, or escalation
    // would silently (fail to) notify someone outside the team.
    private async Task<string?> ValidateMembersAsync(
        Guid orgId, Guid? fallbackAdministratorUserId, IEnumerable<Guid> targetUserIds, CancellationToken ct)
    {
        var candidateIds = targetUserIds.ToHashSet();
        if (fallbackAdministratorUserId is not null) candidateIds.Add(fallbackAdministratorUserId.Value);

        foreach (var userId in candidateIds)
        {
            if (!await db.IsActiveMemberAsync(orgId, userId, ct))
            {
                return $"User {userId} is not an active member of this organization.";
            }
        }

        return null;
    }

    private Task<EscalationPolicy?> Find(Guid orgId, Guid policyId, CancellationToken ct) =>
        db.EscalationPolicies
            .Include(p => p.Levels).ThenInclude(l => l.Targets)
            .FirstOrDefaultAsync(p => p.OrganizationId == orgId && p.Id == policyId, ct);

    private static EscalationPolicyResponse ToResponse(EscalationPolicy p) => new(
        p.Id, p.Name, p.Description, p.ServiceId, p.FallbackAdministratorUserId,
        p.Levels.OrderBy(l => l.Order)
            .Select(l => new EscalationLevelResponse(l.Id, l.Order, l.AckTimeoutMinutes, l.Targets.Select(t => t.UserId).ToList()))
            .ToList(),
        p.CreatedAtUtc, p.UpdatedAtUtc);
}
