using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Notifications;

public record NotificationPreferenceResponse(
    bool EmailEnabled, TimeOnly? QuietHoursStartLocal, TimeOnly? QuietHoursEndLocal, string? TimeZoneId, DateTimeOffset UpdatedAtUtc);

public record UpdateNotificationPreferenceRequest(
    bool EmailEnabled, TimeOnly? QuietHoursStartLocal, TimeOnly? QuietHoursEndLocal,
    [MaxLength(100)] string? TimeZoneId);

public record NotificationResponse(
    Guid Id, Guid IncidentId, Guid RecipientUserId, string Channel, string Kind, string Status,
    string? FailureReason, DateTimeOffset RequestedAtUtc, DateTimeOffset? DeliveredAtUtc, DateTimeOffset? FailedAtUtc);

public record NotificationFilters(Guid? IncidentId, Guid? RecipientUserId, string? Status);
