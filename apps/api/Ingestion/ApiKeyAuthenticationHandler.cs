using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Ingestion;

// Authenticates the alert-ingestion endpoint against an Integration's API key
// instead of a Cognito-issued user token. There is no OrganizationMembership
// behind an integration, so — unlike OrganizationRoleAuthorizationHandler —
// this resolves and validates tenancy directly against the {integrationId}
// route value and populates ICurrentOrganizationAccessor itself.
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SentinelOpsDbContext db,
    ICurrentOrganizationAccessor currentOrganization)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationApiKey";
    public const string ApiKeyHeader = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out var apiKeyValues) ||
            string.IsNullOrWhiteSpace(apiKeyValues.ToString()))
        {
            return AuthenticateResult.Fail("Missing API key.");
        }

        if (!Guid.TryParse(Request.RouteValues["integrationId"]?.ToString(), out var integrationId))
        {
            return AuthenticateResult.Fail("Request is missing a valid integration id.");
        }

        var apiKey = apiKeyValues.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();

        var integration = await db.Integrations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == integrationId && i.ApiKeyHash == hash, Context.RequestAborted);
        if (integration is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (integration.Status != IntegrationStatus.Active)
        {
            return AuthenticateResult.Fail("Integration is not active.");
        }

        var organization = await db.Organizations
            .FirstOrDefaultAsync(o => o.Id == integration.OrganizationId, Context.RequestAborted);
        if (organization is null || organization.Status != OrganizationStatus.Active)
        {
            return AuthenticateResult.Fail("Organization is not active.");
        }

        integration.LastUsedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Context.RequestAborted);

        currentOrganization.Set(organization.Id);
        Context.Items[IntegrationContextKey] = integration;

        var claims = new[]
        {
            new Claim("integration_id", integration.Id.ToString()),
            new Claim("organization_id", organization.Id.ToString()),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public const string IntegrationContextKey = "SentinelOps.Integration";
}
