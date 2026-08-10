using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Services;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Dashboard;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/dashboard")]
public class DashboardController(SentinelOpsDbContext db) : ControllerBase
{
    private const int RecentlyResolvedLimit = 10;
    // Bounds the mean-ack/mean-resolution window — an org running for years
    // shouldn't have "today's average response time" dragged down by
    // incidents from three years ago, and it keeps the in-memory average
    // (see below) over a bounded row count.
    private static readonly TimeSpan MetricsWindow = TimeSpan.FromDays(30);

    [HttpGet("summary")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<DashboardSummaryResponse>> GetSummary(Guid orgId, CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var windowStart = nowUtc - MetricsWindow;

        var activeIncidents = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.Status != IncidentStatus.Resolved)
            .Select(i => i.Severity)
            .ToListAsync(ct);

        var activeBySeverity = Enum.GetValues<IncidentSeverity>()
            .ToDictionary(s => s.ToString(), s => activeIncidents.Count(sev => sev == s));

        var recentlyResolved = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.Status == IncidentStatus.Resolved)
            .OrderByDescending(i => i.ResolvedAtUtc)
            .Take(RecentlyResolvedLimit)
            .Select(i => new IncidentSummary(
                i.Id, i.Title, i.Severity, i.Status, i.ServiceId, i.CreatedAtUtc, i.AcknowledgedAtUtc, i.ResolvedAtUtc))
            .ToListAsync(ct);

        // EF/Npgsql doesn't reliably translate AVG() over a timestamp
        // subtraction, so the (bounded, windowed) rows are pulled into
        // memory and averaged in C# rather than in SQL.
        var ackTimes = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.CreatedAtUtc >= windowStart && i.AcknowledgedAtUtc != null)
            .Select(i => new { i.CreatedAtUtc, i.AcknowledgedAtUtc })
            .ToListAsync(ct);
        var meanAckSeconds = ackTimes.Count > 0
            ? ackTimes.Average(i => (i.AcknowledgedAtUtc!.Value - i.CreatedAtUtc).TotalSeconds)
            : (double?)null;

        var resolutionTimes = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.CreatedAtUtc >= windowStart && i.ResolvedAtUtc != null)
            .Select(i => new { i.CreatedAtUtc, i.ResolvedAtUtc })
            .ToListAsync(ct);
        var meanResolutionSeconds = resolutionTimes.Count > 0
            ? resolutionTimes.Average(i => (i.ResolvedAtUtc!.Value - i.CreatedAtUtc).TotalSeconds)
            : (double?)null;

        var alertVolume = await db.Alerts
            .Where(a => a.OrganizationId == orgId && a.CreatedAtUtc >= nowUtc.AddHours(-24))
            .CountAsync(ct);

        var services = await db.Services.Where(s => s.OrganizationId == orgId).ToListAsync(ct);
        var openIncidentsByService = await db.Incidents
            .Where(i => i.OrganizationId == orgId && i.ServiceId != null && i.Status != IncidentStatus.Resolved)
            .Select(i => new { i.ServiceId, i.Severity, i.CreatedAtUtc })
            .ToListAsync(ct);

        var serviceHealth = services.Select(s =>
        {
            var open = openIncidentsByService.Where(i => i.ServiceId == s.Id).ToList();
            return new ServiceHealthResponse(
                s.Id, s.Status, open.Count, open.Count(i => i.Severity == IncidentSeverity.Critical),
                open.Count > 0 ? open.Max(i => i.CreatedAtUtc) : null);
        }).ToList();

        return Ok(new DashboardSummaryResponse(
            activeIncidents.Count, activeBySeverity, recentlyResolved,
            meanAckSeconds, meanResolutionSeconds, alertVolume, serviceHealth));
    }
}
