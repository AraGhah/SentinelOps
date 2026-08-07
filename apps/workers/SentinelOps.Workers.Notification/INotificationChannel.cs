namespace SentinelOps.Workers.Notification;

// No email/SMS/Slack integration exists anywhere in this repo yet, so this is a
// documented seam rather than a real integration: swap LoggingNotificationChannel
// for a real SES/SNS/Twilio-backed implementation when one is built, without
// touching Function.cs.
public interface INotificationChannel
{
    Task<NotificationSendResult> SendAsync(Guid recipientUserId, string channel, string message, CancellationToken ct);
}

public record NotificationSendResult(bool Success, string? FailureReason);

public class LoggingNotificationChannel : INotificationChannel
{
    public Task<NotificationSendResult> SendAsync(Guid recipientUserId, string channel, string message, CancellationToken ct) =>
        Task.FromResult(new NotificationSendResult(Success: true, FailureReason: null));
}
