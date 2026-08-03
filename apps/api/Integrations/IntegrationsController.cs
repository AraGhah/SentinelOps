using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Ingestion;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Integrations;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/services/{serviceId:guid}/integrations")]
public class IntegrationsController(
    SentinelOpsDbContext db, IAuditLogger auditLogger, IAlertIngestionService ingestionService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<PagedResult<IntegrationResponse>>> List(
        Guid orgId, Guid serviceId, [FromQuery] PagedQuery paging, [FromQuery] IntegrationFilters filters, CancellationToken ct)
    {
        var query = db.Integrations.Where(i => i.OrganizationId == orgId && i.ServiceId == serviceId).AsQueryable();
        if (filters.Status is not null) query = query.Where(i => i.Status == filters.Status);

        var result = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new IntegrationResponse(
                i.Id, i.ServiceId, i.Name, i.Provider, i.ApiKeyLastFour, i.Status, i.LastUsedAtUtc, i.CreatedAtUtc, i.RevokedAtUtc))
            .ToPagedResultAsync(paging, ct);

        return Ok(result);
    }

    [HttpGet("{integrationId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<IntegrationResponse>> Get(Guid orgId, Guid serviceId, Guid integrationId, CancellationToken ct)
    {
        var integration = await Find(orgId, serviceId, integrationId, ct);
        if (integration is null) return NotFound();

        return Ok(ToResponse(integration));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<IntegrationCreatedResponse>> Create(
        Guid orgId, Guid serviceId, CreateIntegrationRequest request, CancellationToken ct)
    {
        var serviceExists = await db.Services.AnyAsync(s => s.OrganizationId == orgId && s.Id == serviceId, ct);
        if (!serviceExists) return NotFound();

        var (apiKey, hash, lastFour) = GenerateApiKey();
        var signingSecret = GenerateSigningSecret();
        var integration = new Integration
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ServiceId = serviceId,
            Name = request.Name.Trim(),
            Provider = request.Provider.Trim(),
            ApiKeyHash = hash,
            ApiKeyLastFour = lastFour,
            SigningSecret = signingSecret,
            Status = IntegrationStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Integrations.Add(integration);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("integration.created", nameof(Integration), integration.Id, new { integration.Name }, ct);

        return Ok(new IntegrationCreatedResponse(ToResponse(integration), apiKey, signingSecret));
    }

    [HttpPost("{integrationId:guid}/rotate-key")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<IntegrationCreatedResponse>> RotateKey(
        Guid orgId, Guid serviceId, Guid integrationId, CancellationToken ct)
    {
        var integration = await Find(orgId, serviceId, integrationId, ct);
        if (integration is null) return NotFound();

        var (apiKey, hash, lastFour) = GenerateApiKey();
        var signingSecret = GenerateSigningSecret();
        integration.ApiKeyHash = hash;
        integration.ApiKeyLastFour = lastFour;
        integration.SigningSecret = signingSecret;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("integration.key_rotated", nameof(Integration), integration.Id, null, ct);

        return Ok(new IntegrationCreatedResponse(ToResponse(integration), apiKey, signingSecret));
    }

    [HttpPost("{integrationId:guid}/test-event")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<TestEventResponse>> SendTestEvent(
        Guid orgId, Guid serviceId, Guid integrationId, CancellationToken ct)
    {
        var integration = await Find(orgId, serviceId, integrationId, ct);
        if (integration is null) return NotFound();
        if (integration.Status != IntegrationStatus.Active)
        {
            return Problem(title: "Invalid request", detail: "Integration is not active.", statusCode: 400);
        }

        var request = new IngestAlertRequest(
            ExternalId: $"test-{Guid.NewGuid()}",
            Source: "test-event",
            Title: "Test alert",
            Description: "Synthetic alert generated to verify this integration is wired up correctly.",
            Severity: IncidentSeverity.Low,
            TimestampUtc: DateTimeOffset.UtcNow,
            Environment: "test",
            Region: null,
            Metadata: null);

        var result = await ingestionService.IngestAsync(
            orgId, integrationId, request, rawPayload: System.Text.Json.JsonSerializer.Serialize(request),
            idempotencyKey: $"test:{Guid.NewGuid()}", ct);

        await auditLogger.LogAsync("integration.test_event_sent", nameof(Integration), integration.Id, null, ct);

        return Ok(new TestEventResponse(result.AlertId, result.CorrelationId));
    }

    [HttpPost("{integrationId:guid}/revoke")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Revoke(Guid orgId, Guid serviceId, Guid integrationId, CancellationToken ct)
    {
        var integration = await Find(orgId, serviceId, integrationId, ct);
        if (integration is null) return NotFound();

        integration.Status = IntegrationStatus.Revoked;
        integration.RevokedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("integration.revoked", nameof(Integration), integration.Id, null, ct);

        return NoContent();
    }

    private Task<Integration?> Find(Guid orgId, Guid serviceId, Guid integrationId, CancellationToken ct) =>
        db.Integrations.FirstOrDefaultAsync(
            i => i.OrganizationId == orgId && i.ServiceId == serviceId && i.Id == integrationId, ct);

    private static (string ApiKey, string Hash, string LastFour) GenerateApiKey()
    {
        var apiKey = "sops_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        var lastFour = apiKey[^4..];
        return (apiKey, hash, lastFour);
    }

    private static string GenerateSigningSecret() =>
        "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static IntegrationResponse ToResponse(Integration i) => new(
        i.Id, i.ServiceId, i.Name, i.Provider, i.ApiKeyLastFour, i.Status, i.LastUsedAtUtc, i.CreatedAtUtc, i.RevokedAtUtc);
}
