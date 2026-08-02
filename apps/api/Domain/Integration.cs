namespace SentinelOps.Api.Domain;

public enum IntegrationStatus { Active = 0, Revoked = 1, Disabled = 2 }

public class Integration : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ServiceId { get; set; }
    public required string Name { get; set; }
    public required string Provider { get; set; }
    public required string ApiKeyHash { get; set; }
    public required string ApiKeyLastFour { get; set; }
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Active;
    public DateTimeOffset? LastUsedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public Service? Service { get; set; }
}
