using System.Text.Json;
using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace SentinelOps.Api.Ingestion;

public class SqsAlertQueuePublisher : IAlertQueuePublisher
{
    private readonly AmazonSQSClient _client;
    private readonly AwsSqsOptions _options;

    public SqsAlertQueuePublisher(IOptions<AwsSqsOptions> options)
    {
        _options = options.Value;
        _client = new AmazonSQSClient(RegionEndpoint.GetBySystemName(_options.Region));
    }

    public async Task PublishAsync(AlertQueueMessage message, CancellationToken ct)
    {
        await _client.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _options.AlertsQueueUrl,
            MessageBody = JsonSerializer.Serialize(message),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CorrelationId"] = new() { DataType = "String", StringValue = message.CorrelationId.ToString() },
                ["IntegrationId"] = new() { DataType = "String", StringValue = message.IntegrationId.ToString() },
            },
        }, ct);
    }
}
