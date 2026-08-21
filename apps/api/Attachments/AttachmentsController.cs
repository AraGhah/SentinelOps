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
public class AttachmentsController(
    SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger, IAttachmentStorageService storage)
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
                a.Id, a.IncidentId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.UploadedByUserId, a.ScanStatus, a.CreatedAtUtc))
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

    // Step 1: mint a presigned PUT URL. No row is created until Create confirms the upload.
    [HttpPost("upload-url")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<UploadUrlResponse>> CreateUploadUrl(
        Guid orgId, Guid incidentId, CreateUploadUrlRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        if (!AttachmentPolicy.IsAllowed(request.ContentType, request.SizeBytes))
        {
            return Problem(
                title: "Attachment rejected",
                detail: $"Content type '{request.ContentType}' or size {request.SizeBytes} bytes is not allowed.",
                statusCode: 400);
        }

        var upload = storage.CreateUploadUrl(orgId, incidentId, request.FileName, request.ContentType);
        return Ok(new UploadUrlResponse(upload.StorageKey, upload.UploadUrl, upload.ExpiresAtUtc));
    }

    // Step 2: records the metadata row after the client PUTs the bytes. ScanStatus starts
    // Pending; SentinelOps.Workers.AttachmentScan updates it once GuardDuty has a verdict.
    [HttpPost]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<AttachmentResponse>> Create(
        Guid orgId, Guid incidentId, CreateAttachmentRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        if (!AttachmentPolicy.IsAllowed(request.ContentType, request.SizeBytes))
        {
            return Problem(
                title: "Attachment rejected",
                detail: $"Content type '{request.ContentType}' or size {request.SizeBytes} bytes is not allowed.",
                statusCode: 400);
        }

        // Reject keys borrowed from another tenant/incident.
        if (!request.StorageKey.StartsWith(AttachmentPolicy.StorageKeyPrefix(orgId, incidentId), StringComparison.Ordinal))
        {
            return Problem(title: "Attachment rejected", detail: "Storage key does not belong to this incident.", statusCode: 400);
        }

        // SizeBytes is just the client's claim; use the actual S3 object size as source of truth.
        var actualSizeBytes = await storage.GetObjectSizeAsync(request.StorageKey, ct);
        if (actualSizeBytes is null)
        {
            return Problem(
                title: "Attachment rejected", detail: "No object was found at the given storage key.", statusCode: 400);
        }
        if (actualSizeBytes.Value > AttachmentPolicy.MaxSizeBytes)
        {
            return Problem(
                title: "Attachment rejected",
                detail: $"Uploaded object size {actualSizeBytes.Value} bytes exceeds the allowed maximum.",
                statusCode: 400);
        }

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            FileName = request.FileName,
            ContentType = request.ContentType,
            SizeBytes = actualSizeBytes.Value,
            StorageKey = request.StorageKey,
            UploadedByUserId = actor.Id,
            ScanStatus = AttachmentScanStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Attachments.Add(attachment);
        IncidentTimeline.Record(db, orgId, incidentId, IncidentEventType.AttachmentAdded, actor.Id, attachment.FileName);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("attachment.created", nameof(Attachment), attachment.Id, new { attachment.FileName }, ct);

        return Ok(ToResponse(attachment));
    }

    [HttpGet("{attachmentId:guid}/download-url")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<DownloadUrlResponse>> CreateDownloadUrl(
        Guid orgId, Guid incidentId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await Find(orgId, incidentId, attachmentId, ct);
        if (attachment is null) return NotFound();

        if (attachment.ScanStatus != AttachmentScanStatus.Clean)
        {
            return Problem(
                title: "Attachment not available",
                detail: $"This attachment's malware scan status is '{attachment.ScanStatus}'.",
                statusCode: 409);
        }

        var (url, expiresAtUtc) = storage.CreateDownloadUrl(attachment.StorageKey, attachment.FileName);
        await auditLogger.LogAsync("attachment.downloaded", nameof(Attachment), attachment.Id, null, ct);

        return Ok(new DownloadUrlResponse(url, expiresAtUtc));
    }

    [HttpDelete("{attachmentId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<IActionResult> Delete(Guid orgId, Guid incidentId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await Find(orgId, incidentId, attachmentId, ct);
        if (attachment is null) return NotFound();

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(attachment.StorageKey, ct);
        await auditLogger.LogAsync("attachment.deleted", nameof(Attachment), attachmentId, null, ct);

        return NoContent();
    }

    private Task<Attachment?> Find(Guid orgId, Guid incidentId, Guid attachmentId, CancellationToken ct) =>
        db.Attachments.FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.IncidentId == incidentId && a.Id == attachmentId, ct);

    private static AttachmentResponse ToResponse(Attachment a) => new(
        a.Id, a.IncidentId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.UploadedByUserId, a.ScanStatus, a.CreatedAtUtc);
}
