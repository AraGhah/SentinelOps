using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Attachments;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/incidents/{incidentId:guid}/attachments")]
public class AttachmentsController(SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<AttachmentResponse>>> List(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var attachments = await db.Attachments
            .Where(a => a.OrganizationId == orgId && a.IncidentId == incidentId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(a => new AttachmentResponse(
                a.Id, a.IncidentId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.UploadedByUserId, a.CreatedAtUtc))
            .ToListAsync(ct);

        return Ok(attachments);
    }

    [HttpGet("{attachmentId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<AttachmentResponse>> Get(Guid orgId, Guid incidentId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await Find(orgId, incidentId, attachmentId, ct);
        if (attachment is null) return NotFound();

        return Ok(ToResponse(attachment));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<AttachmentResponse>> Create(
        Guid orgId, Guid incidentId, CreateAttachmentRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = request.SizeBytes,
            StorageKey = request.StorageKey,
            UploadedByUserId = actor.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("attachment.created", nameof(Attachment), attachment.Id, new { attachment.FileName }, ct);

        return Ok(ToResponse(attachment));
    }

    [HttpDelete("{attachmentId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> Delete(Guid orgId, Guid incidentId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await Find(orgId, incidentId, attachmentId, ct);
        if (attachment is null) return NotFound();

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("attachment.deleted", nameof(Attachment), attachmentId, null, ct);

        return NoContent();
    }

    private Task<Attachment?> Find(Guid orgId, Guid incidentId, Guid attachmentId, CancellationToken ct) =>
        db.Attachments.FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.IncidentId == incidentId && a.Id == attachmentId, ct);

    private static AttachmentResponse ToResponse(Attachment a) => new(
        a.Id, a.IncidentId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.UploadedByUserId, a.CreatedAtUtc);
}
