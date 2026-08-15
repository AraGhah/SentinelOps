using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace SentinelOps.Workers.Shared;

// Narrow wrapper around the one SQS operation the deduplication worker needs
// (direct-send to the incident-creation queue) — IAmazonSQS itself is a huge
// interface, so this is what test doubles implement instead.
public interface IQueueSender
{
    Task SendAsync(string queueUrl, string body, Guid correlationId, CancellationToken ct);
}

public class SqsQueueSender : IQueueSender
{
    private readonly AmazonSQSClient _client;

    public SqsQueueSender(string region) => _client = new AmazonSQSClient(RegionEndpoint.GetBySystemName(region));

    // Correlation id also rides inside the JSON message body (every message
    // record already carries it as a field) — this attribute is what makes it
    // filterable/visible without deserializing the body, e.g. from the SQS
    // console or a CloudWatch Logs Insights query against the queue's own
    // access logging.
    public Task SendAsync(string queueUrl, string body, Guid correlationId, CancellationToken ct) =>
        _client.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CorrelationId"] = new MessageAttributeValue { DataType = "String", StringValue = correlationId.ToString() },
            },
        }, ct);
}
