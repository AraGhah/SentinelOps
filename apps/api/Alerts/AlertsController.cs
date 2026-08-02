using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Alerts;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/alerts")]
public class AlertsController(SentinelOpsDbContext db, IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<AlertResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, [FromQuery] AlertFilters filters, CancellationToken ct)
    {
        var query = db.Alerts.Where(a => a.OrganizationId == orgId).AsQueryable();

        if (filters.IntegrationId is not null) query = query.Where(a => a.IntegrationId == filters.IntegrationId);
        if (filters.Severity is not null) query = query.Where(a => a.Severity == filters.Severity);
        if (filters.IncidentId is not null) query = query.Where(a => a.IncidentId == filters.IncidentId);

        var result = await query
            .OrderByDescending(a => a.TimestampUtc)
            .Select(a => new AlertResponse(
                a.Id, a.IntegrationId, a.ExternalId, a.Source, a.Title, a.Description, a.Severity, a.TimestampUtc,
                a.Environment, a.Region, a.Metadata, a.IncidentId, a.CreatedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }

    [HttpGet("{alertId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<AlertResponse>> Get(Guid orgId, Guid alertId, CancellationToken ct)
    {
        var alert = await Find(orgId, alertId, ct);
        if (alert is null) return NotFound();

        return Ok(ToResponse(alert));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<AlertResponse>> Create(Guid orgId, CreateAlertRequest request, CancellationToken ct)
    {
        var integrationExists = await db.Integrations.AnyAsync(i => i.OrganizationId == orgId && i.Id == request.IntegrationId, ct);
        if (!integrationExists) return NotFound();

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IntegrationId = request.IntegrationId,
            ExternalId = request.ExternalId,
            Source = request.Source,
            Title = request.Title,
            Description = request.Description,
            Severity = request.Severity,
            TimestampUtc = request.TimestampUtc,
            Environment = request.Environment,
            Region = request.Region,
            Metadata = request.Metadata,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);

        return Ok(ToResponse(alert));
    }

    [HttpPut("{alertId:guid}/link-incident")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<AlertResponse>> LinkIncident(
        Guid orgId, Guid alertId, LinkAlertIncidentRequest request, CancellationToken ct)
    {
        var alert = await Find(orgId, alertId, ct);
        if (alert is null) return NotFound();

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == request.IncidentId, ct);
        if (incident is null) return NotFound();

        if (alert.IncidentId == incident.Id) return Ok(ToResponse(alert));

        if (alert.IncidentId is Guid previousIncidentId)
        {
            var previousIncident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == previousIncidentId, ct);
            if (previousIncident is not null) previousIncident.AlertCount = Math.Max(0, previousIncident.AlertCount - 1);
        }

        alert.IncidentId = incident.Id;
        incident.AlertCount += 1;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("alert.linked_to_incident", nameof(Alert), alert.Id, new { IncidentId = incident.Id }, ct);

        return Ok(ToResponse(alert));
    }

    [HttpPut("{alertId:guid}/unlink-incident")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<AlertResponse>> UnlinkIncident(Guid orgId, Guid alertId, CancellationToken ct)
    {
        var alert = await Find(orgId, alertId, ct);
        if (alert is null) return NotFound();

        if (alert.IncidentId is Guid incidentId)
        {
            var incident = await db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
            if (incident is not null) incident.AlertCount = Math.Max(0, incident.AlertCount - 1);
        }

        alert.IncidentId = null;
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("alert.unlinked_from_incident", nameof(Alert), alert.Id, null, ct);

        return Ok(ToResponse(alert));
    }

    private Task<Alert?> Find(Guid orgId, Guid alertId, CancellationToken ct) =>
        db.Alerts.FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.Id == alertId, ct);

    private static AlertResponse ToResponse(Alert a) => new(
        a.Id, a.IntegrationId, a.ExternalId, a.Source, a.Title, a.Description, a.Severity, a.TimestampUtc,
        a.Environment, a.Region, a.Metadata, a.IncidentId, a.CreatedAtUtc);
}
