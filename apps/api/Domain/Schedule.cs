namespace SentinelOps.Api.Domain;

public class Schedule : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public Guid? ServiceId { get; set; }
    public required string TimeZoneId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public Service? Service { get; set; }
}

// A recurring weekly slot. EndTimeLocal < StartTimeLocal represents an overnight
// shift (e.g. 22:00 -> 06:00); this is a simple recurring-slot model, not a
// general RRULE engine.
public class ScheduleRotation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ScheduleId { get; set; }
    public Guid ResponderUserId { get; set; }
    public int DayOfWeek { get; set; }
    public TimeOnly StartTimeLocal { get; set; }
    public TimeOnly EndTimeLocal { get; set; }
    public DateTimeOffset? EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveToUtc { get; set; }
    public bool IsBackup { get; set; }

    public Schedule? Schedule { get; set; }
}

// A one-off override (temporary cover, vacation, etc.) that takes precedence
// over the base rotation for the given window. ResponderUserId is null to
// represent an intentional gap (nobody covering).
public class ScheduleOverride : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ScheduleId { get; set; }
    public Guid? ResponderUserId { get; set; }
    public Guid? OriginalResponderUserId { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public string? Reason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Schedule? Schedule { get; set; }
}
