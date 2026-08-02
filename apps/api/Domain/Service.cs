namespace SentinelOps.Api.Domain;

public enum ServiceEnvironment { Production = 0, Staging = 1, Development = 2 }

public enum ServiceStatus { Operational = 0, Degraded = 1, PartialOutage = 2, MajorOutage = 3, Maintenance = 4 }

public class Service : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid OwnerUserId { get; set; }
    public ServiceEnvironment Environment { get; set; }
    public ServiceStatus Status { get; set; } = ServiceStatus.Operational;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    // OwnerUserId intentionally has no FK/nav to User: like the other "some user
    // in the org" references on later entities (AssignedResponderUserId,
    // AuthorUserId, etc.), it isn't constrained to an existing row — there is no
    // membership-validation layer in this CRUD scaffolding.
    public Organization? Organization { get; set; }
}

// Explicit join entity for service-to-service dependencies rather than an EF
// skip-navigation, since a self-referencing many-to-many needs at least one FK
// configured with DeleteBehavior.Restrict to avoid multiple cascade paths.
public class ServiceDependency : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid DependsOnServiceId { get; set; }

    public Service? Service { get; set; }
    public Service? DependsOnService { get; set; }
}
