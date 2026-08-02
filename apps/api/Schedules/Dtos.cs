using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Schedules;

public record CreateScheduleRequest([Required, MaxLength(200)] string Name, Guid? ServiceId, [Required] string TimeZoneId);

public record UpdateScheduleRequest([Required, MaxLength(200)] string Name, Guid? ServiceId, [Required] string TimeZoneId);

public record ScheduleResponse(
    Guid Id, string Name, Guid? ServiceId, string TimeZoneId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public record CreateRotationRequest(
    Guid ResponderUserId, [Range(0, 6)] int DayOfWeek, TimeOnly StartTimeLocal, TimeOnly EndTimeLocal,
    DateTimeOffset? EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, bool IsBackup);

public record RotationResponse(
    Guid Id, Guid ResponderUserId, int DayOfWeek, TimeOnly StartTimeLocal, TimeOnly EndTimeLocal,
    DateTimeOffset? EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, bool IsBackup);

public record CreateOverrideRequest(
    Guid? ResponderUserId, Guid? OriginalResponderUserId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, string? Reason);

public record OverrideResponse(
    Guid Id, Guid? ResponderUserId, Guid? OriginalResponderUserId, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc,
    string? Reason, Guid CreatedByUserId, DateTimeOffset CreatedAtUtc);

public record OnCallResponse(Guid? ResponderUserId, string Source);
