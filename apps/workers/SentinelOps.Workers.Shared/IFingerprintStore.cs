using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace SentinelOps.Workers.Shared;

// Snapshot of a fingerprint's DynamoDB item after a Touch. AlertCount == 1
// means first alert for this fingerprint; IncidentId == Pending means the
// winner hasn't finished creating the real incident yet.
public record FingerprintRecord(string Fingerprint, string IncidentId, long AlertCount)
{
    public const string Pending = "PENDING";

    public bool IsPending => IncidentId == Pending;
}

public interface IFingerprintStore
{
    // Atomic create-or-update: new item gets AlertCount = 1, IncidentId =
    // Pending; existing item gets AlertCount incremented and TTL extended.
    // DynamoDB's UpdateItem ADD is a per-item atomic increment, so exactly
    // one concurrent caller ever observes AlertCount == 1.
    Task<FingerprintRecord> TouchAsync(string fingerprint, TimeSpan ttl, CancellationToken ct);

    // Called by the winner (AlertCount == 1) once the real incident exists,
    // releasing anyone still seeing Pending.
    Task SetIncidentIdAsync(string fingerprint, Guid incidentId, TimeSpan ttl, CancellationToken ct);

    // Best-effort cleanup if the winner aborts before creating an incident.
    // Only removes the item if still Pending, so it can't clobber a real
    // IncidentId a concurrent SetIncidentIdAsync already wrote.
    Task ReleaseAsync(string fingerprint, CancellationToken ct);
}

public class DynamoDbFingerprintStore : IFingerprintStore
{
    private const string PartitionKey = "Fingerprint";

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;

    public DynamoDbFingerprintStore(IAmazonDynamoDB client, string tableName)
    {
        _client = client;
        _tableName = tableName;
    }

    // Bare Lambda, no DI container.
    public static DynamoDbFingerprintStore FromEnvironment()
    {
        var tableName = Environment.GetEnvironmentVariable("FINGERPRINT_TABLE_NAME")
            ?? throw new InvalidOperationException("FINGERPRINT_TABLE_NAME environment variable is not set.");
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.");

        return new DynamoDbFingerprintStore(new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region)), tableName);
    }

    public async Task<FingerprintRecord> TouchAsync(string fingerprint, TimeSpan ttl, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var response = await _client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { [PartitionKey] = new(fingerprint) },
            UpdateExpression = "SET LastSeenAtUtc = :now, ExpiresAt = :expiresAt, IncidentId = if_not_exists(IncidentId, :pending) ADD AlertCount :incr",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":now"] = new() { N = now.ToUnixTimeSeconds().ToString() },
                [":expiresAt"] = new() { N = now.Add(ttl).ToUnixTimeSeconds().ToString() },
                [":pending"] = new(FingerprintRecord.Pending),
                [":incr"] = new() { N = "1" },
            },
            ReturnValues = ReturnValue.ALL_NEW,
        }, ct);

        var item = response.Attributes;
        return new FingerprintRecord(fingerprint, item["IncidentId"].S, long.Parse(item["AlertCount"].N));
    }

    public async Task SetIncidentIdAsync(string fingerprint, Guid incidentId, TimeSpan ttl, CancellationToken ct)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        try
        {
            await _client.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue> { [PartitionKey] = new(fingerprint) },
                UpdateExpression = "SET IncidentId = :incidentId, ExpiresAt = :expiresAt",
                ConditionExpression = "IncidentId = :pending",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":incidentId"] = new(incidentId.ToString()),
                    [":expiresAt"] = new() { N = expiresAt.ToString() },
                    [":pending"] = new(FingerprintRecord.Pending),
                },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Already resolved by a prior attempt (e.g. redelivery) — nothing to do.
        }
    }

    public async Task ReleaseAsync(string fingerprint, CancellationToken ct)
    {
        try
        {
            await _client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue> { [PartitionKey] = new(fingerprint) },
                ConditionExpression = "IncidentId = :pending",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pending"] = new(FingerprintRecord.Pending),
                },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Someone already resolved it (or it's already gone) — leave it alone.
        }
    }
}

// Thrown when a duplicate alert arrives before the winning alert's incident
// finishes being created. Left uncaught so SQS redelivers after the
// visibility timeout, by when the winner should have called
// SetIncidentIdAsync — avoids busy-polling within one invocation.
public class FingerprintPendingException(string fingerprint)
    : Exception($"Fingerprint '{fingerprint}' is still pending incident creation; will retry on redelivery.");
