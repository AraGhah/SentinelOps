namespace SentinelOps.Api.Domain;

public class EscalationPolicy : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? ServiceId { get; set; }
    public Guid? FallbackAdministratorUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public Service? Service { get; set; }
    public List<EscalationLevel> Levels { get; set; } = [];
}

public class EscalationLevel : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EscalationPolicyId { get; set; }
    public int Order { get; set; }
    public int AckTimeoutMinutes { get; set; }

    public EscalationPolicy? EscalationPolicy { get; set; }
    public List<EscalationLevelTarget> Targets { get; set; } = [];
}

// Individual users only for now — there is no Team entity yet, so "assign users
// or teams to each level" (per the checklist) is scoped down to users here.
public class EscalationLevelTarget : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EscalationLevelId { get; set; }
    public Guid UserId { get; set; }

    public EscalationLevel? EscalationLevel { get; set; }
}
