using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Incidents;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/incidents")]
public class IncidentsController(SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<IncidentResponse>>> List(
        Guid orgId, [FromQuery] PagedQuery paging, [FromQuery] IncidentFilters filters, CancellationToken ct)
    {
        var query = db.Incidents.Where(i => i.OrganizationId == orgId).AsQueryable();

        if (filters.Status is not null) query = query.Where(i => i.Status == filters.Status);
        if (filters.Severity is not null) query = query.Where(i => i.Severity == filters.Severity);
        if (filters.ServiceId is not null) query = query.Where(i => i.ServiceId == filters.ServiceId);
        if (filters.AssignedResponderUserId is not null)
        {
            query = query.Where(i => i.AssignedResponderUserId == filters.AssignedResponderUserId);
        }

        var result = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new IncidentResponse(
                i.Id, i.Title, i.Description, i.Severity, i.ServiceId, i.AssignedResponderUserId, i.Status,
                i.AlertCount, i.CreatedAtUtc, i.AcknowledgedAtUtc, i.ResolvedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }

    [HttpGet("{incidentId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<IncidentResponse>> Get(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var incident = await Find(orgId, incidentId, ct);
        if (incident is null) return NotFound();

        return Ok(ToResponse(incident));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<IncidentResponse>> Create(Guid orgId, CreateIncidentRequest request, CancellationToken ct)
    {
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Title = request.Title.Trim(),
            Description = request.Description,
            Severity = request.Severity,
            ServiceId = request.ServiceId,
            AssignedResponderUserId = request.AssignedResponderUserId,
            Status = request.AssignedResponderUserId is null ? IncidentStatus.Triggered : IncidentStatus.Assigned,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("incident.created", nameof(Incident), incident.Id, new { incident.Title }, ct);

        return Ok(ToResponse(incident));
    }

    [HttpPut("{incidentId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<IncidentResponse>> Update(
        Guid orgId, Guid incidentId, UpdateIncidentRequest request, CancellationToken ct)
    {
        var incident = await Find(orgId, incidentId, ct);
        if (incident is null) return NotFound();

        incident.Title = request.Title.Trim();
        incident.Description = request.Description;
        incident.Severity = request.Severity;
        incident.ServiceId = request.ServiceId;
        incident.AssignedResponderUserId = request.AssignedResponderUserId;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("incident.updated", nameof(Incident), incident.Id, null, ct);

        return Ok(ToResponse(incident));
    }

    [HttpDelete("{incidentId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Delete(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var incident = await Find(orgId, incidentId, ct);
        if (incident is null) return NotFound();

        db.Incidents.Remove(incident);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("incident.deleted", nameof(Incident), incidentId, null, ct);

        return NoContent();
    }

    [HttpPut("{incidentId:guid}/status")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<IncidentResponse>> UpdateStatus(
        Guid orgId, Guid incidentId, UpdateIncidentStatusRequest request, CancellationToken ct)
    {
        var incident = await Find(orgId, incidentId, ct);
        if (incident is null) return NotFound();

        // Minimal legality rule: once Resolved, the only way out is Reopened —
        // guards against silently "un-resolving" an incident by skipping the
        // explicit reopen step. All other forward/backward transitions are allowed
        // (e.g. Investigating -> Resolved directly is fine).
        if (incident.Status == IncidentStatus.Resolved && request.Status != IncidentStatus.Resolved
            && request.Status != IncidentStatus.Reopened)
        {
            return Problem(
                title: "Invalid transition", detail: "A resolved incident must be reopened before changing to another status.",
                statusCode: 400);
        }

        if (incident.Status == request.Status) return Ok(ToResponse(incident));

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var fromStatus = incident.Status;

        incident.Status = request.Status;
        if (request.Status == IncidentStatus.Acknowledged && incident.AcknowledgedAtUtc is null)
        {
            incident.AcknowledgedAtUtc = DateTimeOffset.UtcNow;
        }
        if (request.Status == IncidentStatus.Resolved)
        {
            incident.ResolvedAtUtc = DateTimeOffset.UtcNow;
        }
        else if (request.Status == IncidentStatus.Reopened)
        {
            incident.ResolvedAtUtc = null;
        }

        db.IncidentStatusHistories.Add(new IncidentStatusHistory
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            FromStatus = fromStatus,
            ToStatus = request.Status,
            ChangedByUserId = actor.Id,
            Note = request.Note,
            ChangedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "incident.status_changed", nameof(Incident), incident.Id, new { From = fromStatus, To = request.Status }, ct);

        return Ok(ToResponse(incident));
    }

    [HttpGet("{incidentId:guid}/status-history")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<IncidentStatusHistoryResponse>>> GetStatusHistory(
        Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var history = await db.IncidentStatusHistories
            .Where(h => h.OrganizationId == orgId && h.IncidentId == incidentId)
            .OrderBy(h => h.ChangedAtUtc)
            .Select(h => new IncidentStatusHistoryResponse(h.FromStatus, h.ToStatus, h.ChangedByUserId, h.Note, h.ChangedAtUtc))
            .ToListAsync(ct);

        return Ok(history);
    }

    [HttpGet("{incidentId:guid}/comments")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<CommentResponse>>> ListComments(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var comments = await db.IncidentComments
            .Where(c => c.OrganizationId == orgId && c.IncidentId == incidentId)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new CommentResponse(c.Id, c.AuthorUserId, c.Body, c.IsInternal, c.CreatedAtUtc))
            .ToListAsync(ct);

        return Ok(comments);
    }

    [HttpPost("{incidentId:guid}/comments")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<CommentResponse>> AddComment(
        Guid orgId, Guid incidentId, CreateCommentRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var comment = new IncidentComment
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            AuthorUserId = actor.Id,
            Body = request.Body,
            IsInternal = request.IsInternal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.IncidentComments.Add(comment);
        await db.SaveChangesAsync(ct);

        return Ok(new CommentResponse(comment.Id, comment.AuthorUserId, comment.Body, comment.IsInternal, comment.CreatedAtUtc));
    }

    [HttpPost("{incidentId:guid}/related/{relatedIncidentId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> LinkRelated(Guid orgId, Guid incidentId, Guid relatedIncidentId, CancellationToken ct)
    {
        if (incidentId == relatedIncidentId)
        {
            return Problem(title: "Invalid request", detail: "An incident cannot relate to itself.", statusCode: 400);
        }

        var incidentsExist = await db.Incidents
            .CountAsync(i => i.OrganizationId == orgId && (i.Id == incidentId || i.Id == relatedIncidentId), ct);
        if (incidentsExist != 2) return NotFound();

        var alreadyLinked = await db.RelatedIncidentLinks
            .AnyAsync(l => l.OrganizationId == orgId && l.IncidentId == incidentId && l.RelatedIncidentId == relatedIncidentId, ct);
        if (alreadyLinked) return NoContent();

        db.RelatedIncidentLinks.AddRange(
            new RelatedIncidentLink { Id = Guid.NewGuid(), OrganizationId = orgId, IncidentId = incidentId, RelatedIncidentId = relatedIncidentId },
            new RelatedIncidentLink { Id = Guid.NewGuid(), OrganizationId = orgId, IncidentId = relatedIncidentId, RelatedIncidentId = incidentId });

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{incidentId:guid}/related/{relatedIncidentId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> UnlinkRelated(Guid orgId, Guid incidentId, Guid relatedIncidentId, CancellationToken ct)
    {
        var links = await db.RelatedIncidentLinks
            .Where(l => l.OrganizationId == orgId
                && ((l.IncidentId == incidentId && l.RelatedIncidentId == relatedIncidentId)
                    || (l.IncidentId == relatedIncidentId && l.RelatedIncidentId == incidentId)))
            .ToListAsync(ct);

        db.RelatedIncidentLinks.RemoveRange(links);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{incidentId:guid}/related")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<Guid>>> ListRelated(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var related = await db.RelatedIncidentLinks
            .Where(l => l.OrganizationId == orgId && l.IncidentId == incidentId)
            .Select(l => l.RelatedIncidentId)
            .ToListAsync(ct);

        return Ok(related);
    }

    [HttpPost("{incidentId:guid}/tags")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> AddTag(Guid orgId, Guid incidentId, AddTagRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        var tag = request.Tag.Trim();
        var alreadyExists = await db.IncidentTags
            .AnyAsync(t => t.OrganizationId == orgId && t.IncidentId == incidentId && t.Tag == tag, ct);
        if (alreadyExists) return NoContent();

        db.IncidentTags.Add(new IncidentTag { Id = Guid.NewGuid(), OrganizationId = orgId, IncidentId = incidentId, Tag = tag });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{incidentId:guid}/tags/{tag}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> RemoveTag(Guid orgId, Guid incidentId, string tag, CancellationToken ct)
    {
        var entry = await db.IncidentTags
            .FirstOrDefaultAsync(t => t.OrganizationId == orgId && t.IncidentId == incidentId && t.Tag == tag, ct);
        if (entry is null) return NotFound();

        db.IncidentTags.Remove(entry);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{incidentId:guid}/tags")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<string>>> ListTags(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var tags = await db.IncidentTags
            .Where(t => t.OrganizationId == orgId && t.IncidentId == incidentId)
            .Select(t => t.Tag)
            .ToListAsync(ct);

        return Ok(tags);
    }

    private Task<Incident?> Find(Guid orgId, Guid incidentId, CancellationToken ct) =>
        db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);

    private static IncidentResponse ToResponse(Incident i) => new(
        i.Id, i.Title, i.Description, i.Severity, i.ServiceId, i.AssignedResponderUserId, i.Status, i.AlertCount,
        i.CreatedAtUtc, i.AcknowledgedAtUtc, i.ResolvedAtUtc);
}
