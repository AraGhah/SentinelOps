using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.Tests;

// Workers query straight against SentinelOpsDbContext (no HTTP layer), so seed directly through EF.
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

    public static User NewUser(SentinelOpsDbContext db, string? email = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            CognitoSub = Guid.NewGuid().ToString(),
            Email = email ?? $"user-{Guid.NewGuid():N}@example.com",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        return user;
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

    public static Schedule NewSchedule(SentinelOpsDbContext db, Guid organizationId, Guid? serviceId = null, string timeZoneId = "UTC")
    {
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = "Primary On-Call",
            ServiceId = serviceId,
            TimeZoneId = timeZoneId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Schedules.Add(schedule);
        return schedule;
    }

    public static ScheduleRotation NewRotation(
        SentinelOpsDbContext db, Guid organizationId, Guid scheduleId, Guid responderUserId, int dayOfWeek,
        TimeOnly startTimeLocal, TimeOnly endTimeLocal, bool isBackup = false)
    {
        var rotation = new ScheduleRotation
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ScheduleId = scheduleId,
            ResponderUserId = responderUserId,
            DayOfWeek = dayOfWeek,
            StartTimeLocal = startTimeLocal,
            EndTimeLocal = endTimeLocal,
            IsBackup = isBackup,
        };
        db.ScheduleRotations.Add(rotation);
        return rotation;
    }

    public static EscalationPolicy NewEscalationPolicy(
        SentinelOpsDbContext db, Guid organizationId, Guid? serviceId = null, Guid? fallbackAdministratorUserId = null)
    {
        var policy = new EscalationPolicy
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = "Default",
            ServiceId = serviceId,
            FallbackAdministratorUserId = fallbackAdministratorUserId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.EscalationPolicies.Add(policy);
        return policy;
    }

    public static EscalationLevel NewEscalationLevel(
        SentinelOpsDbContext db, Guid organizationId, Guid policyId, int order, int ackTimeoutMinutes, params Guid[] targetUserIds)
    {
        var level = new EscalationLevel
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            EscalationPolicyId = policyId,
            Order = order,
            AckTimeoutMinutes = ackTimeoutMinutes,
        };
        db.EscalationLevels.Add(level);

        foreach (var userId in targetUserIds)
        {
            db.EscalationLevelTargets.Add(new EscalationLevelTarget
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                EscalationLevelId = level.Id,
                UserId = userId,
            });
        }

        return level;
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
