namespace SentinelOps.Workers.Notification;

// SES is the only implementation today (see SesNotificationChannel).
public interface INotificationChannel
{
    Task<NotificationSendResult> SendAsync(string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken ct);
}

// IsTransient: true for retryable failures (throttling, AWS blips), false for
// permanent ones (rejected content, unverified sender). Meaningless when Success is true.
public record NotificationSendResult(bool Success, bool IsTransient, string? FailureReason);

public class LoggingNotificationChannel : INotificationChannel
{
    public Task<NotificationSendResult> SendAsync(string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken ct) =>
        Task.FromResult(new NotificationSendResult(Success: true, IsTransient: false, FailureReason: null));
}
