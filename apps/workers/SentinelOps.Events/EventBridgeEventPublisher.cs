using System.Text.Json;
using Amazon;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Microsoft.Extensions.Options;

namespace SentinelOps.Events;

public record EventBridgeOptions
{
    public const string SectionName = "Aws:EventBridge";

    public required string Region { get; set; }
    public required string EventBusName { get; set; }
}

public class EventBridgeEventPublisher : IEventPublisher
{
    private readonly AmazonEventBridgeClient _client;
    private readonly string _eventBusName;

    public EventBridgeEventPublisher(IOptions<EventBridgeOptions> options)
    {
        _eventBusName = options.Value.EventBusName;
        _client = new AmazonEventBridgeClient(RegionEndpoint.GetBySystemName(options.Value.Region));
    }

    // Workers run as bare Lambda functions with no DI container, so they read
    // EventBridgeOptions from env vars the CDK stack sets on the function
    // (AWS_REGION is also Lambda-managed and set automatically).
    public static EventBridgeEventPublisher FromEnvironment()
    {
        var eventBusName = Environment.GetEnvironmentVariable("EVENT_BUS_NAME")
            ?? throw new InvalidOperationException("EVENT_BUS_NAME environment variable is not set.");
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.");

        return new EventBridgeEventPublisher(Microsoft.Extensions.Options.Options.Create(
            new EventBridgeOptions { Region = region, EventBusName = eventBusName }));
    }

    public async Task PublishAsync(string source, string detailType, IEventDetail detail, CancellationToken ct)
    {
        var response = await _client.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = _eventBusName,
                    Source = source,
                    DetailType = detailType,
                    Time = detail.OccurredAtUtc.UtcDateTime,
                    Detail = JsonSerializer.Serialize(detail, detail.GetType(), EventJson.Options),
                },
            ],
        }, ct);

        if (response.FailedEntryCount > 0)
        {
            var reasons = string.Join("; ", response.Entries.Where(e => e.ErrorMessage is not null).Select(e => e.ErrorMessage));
            throw new InvalidOperationException($"Failed to publish {detailType} to EventBridge: {reasons}");
        }
    }
}
