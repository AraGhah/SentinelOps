using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace SentinelOps.Workers.Shared;

// Narrow wrapper around the one SQS operation the deduplication worker needs
// (direct-send to the incident-creation queue) — IAmazonSQS itself is a huge
// interface, so this is what test doubles implement instead.
public interface IQueueSender
{
    Task SendAsync(string queueUrl, string body, CancellationToken ct);
}

public class SqsQueueSender : IQueueSender
{
    private readonly AmazonSQSClient _client;

    public SqsQueueSender(string region) => _client = new AmazonSQSClient(RegionEndpoint.GetBySystemName(region));

    public Task SendAsync(string queueUrl, string body, CancellationToken ct) =>
        _client.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body }, ct);
}
