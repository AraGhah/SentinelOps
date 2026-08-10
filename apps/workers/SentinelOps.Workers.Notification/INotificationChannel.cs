namespace SentinelOps.Workers.Notification;

// SES is the only real channel today (see SesNotificationChannel) — SMS/Slack/
// Teams are a documented future seam (section 18's "Advanced" tier), swappable
// without touching Function.cs since they'd all implement this same interface.
public interface INotificationChannel
{
    Task<NotificationSendResult> SendAsync(string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken ct);
}

// IsTransient distinguishes "worth retrying" (throttling, a momentary AWS
// error) from "will never succeed as-is" (rejected content, unverified
// sender) — Function.cs uses it to decide whether to let SQS redeliver or
// record a permanent failure. Meaningless when Success is true.
public record NotificationSendResult(bool Success, bool IsTransient, string? FailureReason);

public class LoggingNotificationChannel : INotificationChannel
{
    public Task<NotificationSendResult> SendAsync(string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken ct) =>
        Task.FromResult(new NotificationSendResult(Success: true, IsTransient: false, FailureReason: null));
}
