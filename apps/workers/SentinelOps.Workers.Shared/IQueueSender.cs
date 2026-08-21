using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace SentinelOps.Workers.Shared;

// Narrow wrapper around the one SQS operation the deduplication worker needs,
// so test doubles don't have to implement all of IAmazonSQS.
public interface IQueueSender
{
    Task SendAsync(string queueUrl, string body, Guid correlationId, CancellationToken ct);
}

public class SqsQueueSender : IQueueSender
{
    private readonly AmazonSQSClient _client;

    public SqsQueueSender(string region) => _client = new AmazonSQSClient(RegionEndpoint.GetBySystemName(region));

    // Also set as a message attribute (in addition to the JSON body) so it's
    // filterable without deserializing, e.g. from the SQS console.
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
