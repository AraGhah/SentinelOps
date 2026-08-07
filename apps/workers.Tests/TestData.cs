using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.Tests;

// Workers query straight against SentinelOpsDbContext (no HTTP layer, unlike
// apps/api.Tests), so test data is seeded the same way: directly through EF.
public static class TestData
{
    public static Organization NewOrganization(SentinelOpsDbContext db)
    {
        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Test Org",
            Slug = $"test-org-{Guid.NewGuid():N}",
            CreatedByUserId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Organizations.Add(org);
        return org;
    }

    public static Service NewService(SentinelOpsDbContext db, Guid organizationId)
    {
        var service = new Service
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = "Payments",
            OwnerUserId = Guid.NewGuid(),
            Environment = ServiceEnvironment.Production,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Services.Add(service);
        return service;
    }

    public static Integration NewIntegration(SentinelOpsDbContext db, Guid organizationId, Guid serviceId)
    {
        var integration = new Integration
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ServiceId = serviceId,
            Name = "Webhook",
            Provider = "Generic",
            ApiKeyHash = "hash",
            ApiKeyLastFour = "abcd",
            SigningSecret = "secret",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Integrations.Add(integration);
        return integration;
    }

    public static Alert NewAlert(
        SentinelOpsDbContext db, Guid organizationId, Guid integrationId, string externalId = "ext-1",
        string source = "datadog", string environment = "production", Guid? incidentId = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            IntegrationId = integrationId,
            ExternalId = externalId,
            Source = source,
            Title = "High latency detected",
            Severity = IncidentSeverity.High,
            TimestampUtc = DateTimeOffset.UtcNow,
            Environment = environment,
            IncidentId = incidentId,
            CorrelationId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Alerts.Add(alert);
        return alert;
    }

    public static Incident NewIncident(
        SentinelOpsDbContext db, Guid organizationId, Guid? serviceId = null, IncidentStatus status = IncidentStatus.Triggered)
    {
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Title = "High latency detected",
            Severity = IncidentSeverity.High,
            ServiceId = serviceId,
            Status = status,
            AlertCount = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Incidents.Add(incident);
        return incident;
    }
}
