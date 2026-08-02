using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Reports;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/incidents/{incidentId:guid}/reports")]
public class ReportsController(SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<ReportResponse>>> List(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var reports = await db.Reports
            .Where(r => r.OrganizationId == orgId && r.IncidentId == incidentId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new ReportResponse(
                r.Id, r.IncidentId, r.Summary, r.CustomerImpact, r.RootCause, r.DetectionDetails, r.ResolutionDetails,
                r.PreventionActions, r.FollowUpTasks, r.ReviewStatus, r.CreatedByUserId, r.CreatedAtUtc, r.UpdatedAtUtc))
            .ToListAsync(ct);

        return Ok(reports);
    }

    [HttpGet("{reportId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<ReportResponse>> Get(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        return Ok(ToResponse(report));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<ReportResponse>> Create(
        Guid orgId, Guid incidentId, CreateReportRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var report = new Report
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            Summary = request.Summary,
            CustomerImpact = request.CustomerImpact,
            RootCause = request.RootCause,
            DetectionDetails = request.DetectionDetails,
            ResolutionDetails = request.ResolutionDetails,
            PreventionActions = request.PreventionActions,
            FollowUpTasks = request.FollowUpTasks,
            ReviewStatus = ReportReviewStatus.Draft,
            CreatedByUserId = actor.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.created", nameof(Report), report.Id, null, ct);

        return Ok(ToResponse(report));
    }

    [HttpPut("{reportId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<ReportResponse>> Update(
        Guid orgId, Guid incidentId, Guid reportId, UpdateReportRequest request, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        report.Summary = request.Summary;
        report.CustomerImpact = request.CustomerImpact;
        report.RootCause = request.RootCause;
        report.DetectionDetails = request.DetectionDetails;
        report.ResolutionDetails = request.ResolutionDetails;
        report.PreventionActions = request.PreventionActions;
        report.FollowUpTasks = request.FollowUpTasks;
        report.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.updated", nameof(Report), report.Id, null, ct);

        return Ok(ToResponse(report));
    }

    [HttpPut("{reportId:guid}/review-status")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<ReportResponse>> UpdateReviewStatus(
        Guid orgId, Guid incidentId, Guid reportId, UpdateReportReviewStatusRequest request, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        report.ReviewStatus = request.ReviewStatus;
        report.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "report.review_status_changed", nameof(Report), report.Id, new { report.ReviewStatus }, ct);

        return Ok(ToResponse(report));
    }

    [HttpDelete("{reportId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Delete(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        db.Reports.Remove(report);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.deleted", nameof(Report), reportId, null, ct);

        return NoContent();
    }

    private Task<Report?> Find(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct) =>
        db.Reports.FirstOrDefaultAsync(r => r.OrganizationId == orgId && r.IncidentId == incidentId && r.Id == reportId, ct);

    private static ReportResponse ToResponse(Report r) => new(
        r.Id, r.IncidentId, r.Summary, r.CustomerImpact, r.RootCause, r.DetectionDetails, r.ResolutionDetails,
        r.PreventionActions, r.FollowUpTasks, r.ReviewStatus, r.CreatedByUserId, r.CreatedAtUtc, r.UpdatedAtUtc);
}
