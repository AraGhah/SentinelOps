using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;

namespace SentinelOps.Workers.Notification;

public class SesNotificationChannel : INotificationChannel
{
    private readonly IAmazonSimpleEmailServiceV2 _client;
    private readonly string _senderEmail;

    public SesNotificationChannel(IAmazonSimpleEmailServiceV2 client, string senderEmail)
    {
        _client = client;
        _senderEmail = senderEmail;
    }

    // Workers run as bare Lambdas with no DI container — same pattern as
    // EventBridgeEventPublisher.FromEnvironment().
    public static SesNotificationChannel FromEnvironment()
    {
        var senderEmail = Environment.GetEnvironmentVariable("SES_SENDER_EMAIL")
            ?? throw new InvalidOperationException("SES_SENDER_EMAIL environment variable is not set.");
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.");

        return new SesNotificationChannel(
            new AmazonSimpleEmailServiceV2Client(RegionEndpoint.GetBySystemName(region)), senderEmail);
    }

    public async Task<NotificationSendResult> SendAsync(
        string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken ct)
    {
        try
        {
            await _client.SendEmailAsync(new SendEmailRequest
            {
                FromEmailAddress = _senderEmail,
                Destination = new Destination { ToAddresses = [recipientEmail] },
                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content { Data = subject, Charset = "UTF-8" },
                        Body = new Body
                        {
                            Html = new Content { Data = htmlBody, Charset = "UTF-8" },
                            Text = new Content { Data = textBody, Charset = "UTF-8" },
                        },
                    },
                },
            }, ct);

            return new NotificationSendResult(Success: true, IsTransient: false, FailureReason: null);
        }
        // The recipient/content itself is the problem — retrying the exact
        // same send will never succeed.
        catch (MessageRejectedException ex)
        {
            return new NotificationSendResult(false, IsTransient: false, ex.Message);
        }
        catch (MailFromDomainNotVerifiedException ex)
        {
            return new NotificationSendResult(false, IsTransient: false, ex.Message);
        }
        catch (AccountSuspendedException ex)
        {
            return new NotificationSendResult(false, IsTransient: false, ex.Message);
        }
        // Throttling / momentary service issues — safe, and worth, retrying.
        catch (TooManyRequestsException ex)
        {
            return new NotificationSendResult(false, IsTransient: true, ex.Message);
        }
        catch (SendingPausedException ex)
        {
            return new NotificationSendResult(false, IsTransient: true, ex.Message);
        }
        // Anything else (network blip, an AWS-side 5xx) — default to
        // retryable rather than silently dropping a page.
        catch (AmazonSimpleEmailServiceV2Exception ex)
        {
            return new NotificationSendResult(false, IsTransient: true, ex.Message);
        }
    }
}
