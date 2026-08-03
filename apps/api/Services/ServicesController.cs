using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Services;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/services")]
public class ServicesController(SentinelOpsDbContext db, IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<ServiceResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, [FromQuery] ServiceFilters filters, CancellationToken ct)
    {
        var query = db.Services.Where(s => s.OrganizationId == orgId).AsQueryable();

        if (filters.Environment is not null) query = query.Where(s => s.Environment == filters.Environment);
        if (filters.Status is not null) query = query.Where(s => s.Status == filters.Status);

        var result = await query
            .OrderBy(s => s.Name)
            .Select(s => new ServiceResponse(
                s.Id, s.Name, s.Description, s.OwnerUserId, s.Environment, s.Status, s.CreatedAtUtc, s.UpdatedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }

    [HttpGet("{serviceId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<ServiceResponse>> Get(Guid orgId, Guid serviceId, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (service is null) return NotFound();

        return Ok(ToResponse(service));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<ServiceResponse>> Create(Guid orgId, CreateServiceRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var service = new Service
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = request.Name.Trim(),
            Description = request.Description,
            OwnerUserId = request.OwnerUserId,
            Environment = request.Environment,
            Status = ServiceStatus.Operational,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Services.Add(service);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("service.created", nameof(Service), service.Id, new { service.Name }, ct);

        return Ok(ToResponse(service));
    }

    [HttpPut("{serviceId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<ServiceResponse>> Update(
        Guid orgId, Guid serviceId, UpdateServiceRequest request, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (service is null) return NotFound();

        service.Name = request.Name.Trim();
        service.Description = request.Description;
        service.OwnerUserId = request.OwnerUserId;
        service.Environment = request.Environment;
        service.Status = request.Status;
        service.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("service.updated", nameof(Service), service.Id, new { service.Name, service.Status }, ct);

        return Ok(ToResponse(service));
    }

    [HttpDelete("{serviceId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Delete(Guid orgId, Guid serviceId, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (service is null) return NotFound();

        db.Services.Remove(service);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("service.deleted", nameof(Service), serviceId, null, ct);

        return NoContent();
    }

    [HttpGet("{serviceId:guid}/dependencies")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<ServiceDependencyResponse>>> ListDependencies(
        Guid orgId, Guid serviceId, CancellationToken ct)
    {
        var dependencies = await db.ServiceDependencies
            .Where(d => d.OrganizationId == orgId && d.ServiceId == serviceId)
            .Select(d => new ServiceDependencyResponse(d.Id, d.ServiceId, d.DependsOnServiceId))
            .ToListAsync(ct);

        return Ok(dependencies);
    }

    [HttpPost("{serviceId:guid}/dependencies/{dependsOnServiceId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> AddDependency(
        Guid orgId, Guid serviceId, Guid dependsOnServiceId, CancellationToken ct)
    {
        if (serviceId == dependsOnServiceId)
        {
            return Problem(title: "Invalid request", detail: "A service cannot depend on itself.", statusCode: 400);
        }

        var servicesExist = await db.Services
            .CountAsync(s => s.OrganizationId == orgId && (s.Id == serviceId || s.Id == dependsOnServiceId), ct);
        if (servicesExist != 2) return NotFound();

        // Shallow cycle check: reject if the target already (directly or transitively)
        // depends back on this service. Walks the existing dependency edges rather than
        // pulling in a generic graph library, which is enough depth for this scaffolding.
        var visited = new HashSet<Guid> { dependsOnServiceId };
        var frontier = new Queue<Guid>();
        frontier.Enqueue(dependsOnServiceId);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (current == serviceId)
            {
                return Problem(title: "Invalid request", detail: "This dependency would create a cycle.", statusCode: 400);
            }

            var next = await db.ServiceDependencies
                .Where(d => d.OrganizationId == orgId && d.ServiceId == current)
                .Select(d => d.DependsOnServiceId)
                .ToListAsync(ct);

            foreach (var n in next)
            {
                if (visited.Add(n)) frontier.Enqueue(n);
            }
        }

        var alreadyExists = await db.ServiceDependencies
            .AnyAsync(d => d.OrganizationId == orgId && d.ServiceId == serviceId && d.DependsOnServiceId == dependsOnServiceId, ct);
        if (alreadyExists) return NoContent();

        db.ServiceDependencies.Add(new ServiceDependency
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ServiceId = serviceId,
            DependsOnServiceId = dependsOnServiceId,
        });
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpDelete("{serviceId:guid}/dependencies/{dependsOnServiceId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> RemoveDependency(
        Guid orgId, Guid serviceId, Guid dependsOnServiceId, CancellationToken ct)
    {
        var dependency = await db.ServiceDependencies.FirstOrDefaultAsync(
            d => d.OrganizationId == orgId && d.ServiceId == serviceId && d.DependsOnServiceId == dependsOnServiceId, ct);
        if (dependency is null) return NotFound();

        db.ServiceDependencies.Remove(dependency);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpGet("{serviceId:guid}/incidents")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<IncidentResponse>>> ListOpenIncidents(Guid orgId, Guid serviceId, CancellationToken ct)
    {
        var serviceExists = await db.Services.AnyAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (!serviceExists) return NotFound();

        var incidents = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.ServiceId == serviceId && i.Status != IncidentStatus.Resolved)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new IncidentResponse(
                i.Id, i.Title, i.Description, i.Severity, i.ServiceId, i.AssignedResponderUserId, i.Status,
                i.AlertCount, i.CreatedAtUtc, i.AcknowledgedAtUtc, i.ResolvedAtUtc))
            .ToListAsync(ct);

        return Ok(incidents);
    }

    [HttpGet("{serviceId:guid}/health")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<ServiceHealthResponse>> GetHealth(Guid orgId, Guid serviceId, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (service is null) return NotFound();

        var openIncidents = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.ServiceId == serviceId && i.Status != IncidentStatus.Resolved)
            .Select(i => new { i.Severity, i.CreatedAtUtc })
            .ToListAsync(ct);

        return Ok(new ServiceHealthResponse(
            service.Id,
            service.Status,
            openIncidents.Count,
            openIncidents.Count(i => i.Severity == IncidentSeverity.Critical),
            openIncidents.Count > 0 ? openIncidents.Max(i => i.CreatedAtUtc) : null));
    }

    [HttpGet("{serviceId:guid}/alert-rules")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<AlertRuleResponse>>> ListAlertRules(Guid orgId, Guid serviceId, CancellationToken ct)
    {
        var rules = await db.ServiceAlertRules
            .Where(r => r.OrganizationId == orgId && r.ServiceId == serviceId)
            .OrderBy(r => r.Name)
            .Select(r => new AlertRuleResponse(
                r.Id, r.ServiceId, r.Name, r.Description, r.Condition, r.Severity, r.IsEnabled, r.CreatedAtUtc, r.UpdatedAtUtc))
            .ToListAsync(ct);

        return Ok(rules);
    }

    [HttpPost("{serviceId:guid}/alert-rules")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<AlertRuleResponse>> CreateAlertRule(
        Guid orgId, Guid serviceId, CreateAlertRuleRequest request, CancellationToken ct)
    {
        var serviceExists = await db.Services.AnyAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (!serviceExists) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var rule = new ServiceAlertRule
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ServiceId = serviceId,
            Name = request.Name.Trim(),
            Description = request.Description,
            Condition = request.Condition.Trim(),
            Severity = request.Severity,
            IsEnabled = request.IsEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.ServiceAlertRules.Add(rule);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("service.alert_rule.created", nameof(ServiceAlertRule), rule.Id, new { rule.Name, rule.ServiceId }, ct);

        return Ok(ToAlertRuleResponse(rule));
    }

    [HttpPut("{serviceId:guid}/alert-rules/{ruleId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<AlertRuleResponse>> UpdateAlertRule(
        Guid orgId, Guid serviceId, Guid ruleId, UpdateAlertRuleRequest request, CancellationToken ct)
    {
        var rule = await db.ServiceAlertRules
            .FirstOrDefaultAsync(r => r.OrganizationId == orgId && r.ServiceId == serviceId && r.Id == ruleId, ct);
        if (rule is null) return NotFound();

        rule.Name = request.Name.Trim();
        rule.Description = request.Description;
        rule.Condition = request.Condition.Trim();
        rule.Severity = request.Severity;
        rule.IsEnabled = request.IsEnabled;
        rule.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("service.alert_rule.updated", nameof(ServiceAlertRule), rule.Id, new { rule.Name, rule.IsEnabled }, ct);

        return Ok(ToAlertRuleResponse(rule));
    }

    [HttpDelete("{serviceId:guid}/alert-rules/{ruleId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> DeleteAlertRule(Guid orgId, Guid serviceId, Guid ruleId, CancellationToken ct)
    {
        var rule = await db.ServiceAlertRules
            .FirstOrDefaultAsync(r => r.OrganizationId == orgId && r.ServiceId == serviceId && r.Id == ruleId, ct);
        if (rule is null) return NotFound();

        db.ServiceAlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("service.alert_rule.deleted", nameof(ServiceAlertRule), ruleId, null, ct);

        return NoContent();
    }

    private static ServiceResponse ToResponse(Service s) => new(
        s.Id, s.Name, s.Description, s.OwnerUserId, s.Environment, s.Status, s.CreatedAtUtc, s.UpdatedAtUtc);

    private static AlertRuleResponse ToAlertRuleResponse(ServiceAlertRule r) => new(
        r.Id, r.ServiceId, r.Name, r.Description, r.Condition, r.Severity, r.IsEnabled, r.CreatedAtUtc, r.UpdatedAtUtc);
}
