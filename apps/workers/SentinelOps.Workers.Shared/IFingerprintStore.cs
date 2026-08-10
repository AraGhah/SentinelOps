using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace SentinelOps.Workers.Shared;

// Snapshot of a fingerprint's DynamoDB item immediately after a Touch. Callers
// distinguish "I'm the first alert for this fingerprint" from "a duplicate"
// purely from AlertCount, and check IncidentId against Pending to know whether
// the winner has finished creating the real incident yet.
public record FingerprintRecord(string Fingerprint, string IncidentId, long AlertCount)
{
    public const string Pending = "PENDING";

    public bool IsPending => IncidentId == Pending;
}

public interface IFingerprintStore
{
    // Atomically creates-or-updates the fingerprint item: if it doesn't exist,
    // creates it with AlertCount = 1 and IncidentId = Pending; if it exists,
    // increments AlertCount and slides the TTL forward without touching
    // IncidentId. Returning the post-update state from the same atomic
    // operation (rather than a separate read) is what makes this safe under
    // concurrent invocations — DynamoDB's UpdateItem ADD is a per-item atomic
    // increment, so exactly one concurrent caller ever observes AlertCount == 1.
    Task<FingerprintRecord> TouchAsync(string fingerprint, TimeSpan ttl, CancellationToken ct);

    // Called by the winner (the caller that saw AlertCount == 1) once it has
    // created the real incident, releasing anyone still seeing Pending.
    Task SetIncidentIdAsync(string fingerprint, Guid incidentId, TimeSpan ttl, CancellationToken ct);

    // Best-effort cleanup for when the winner aborts before creating an
    // incident (e.g. the triggering alert was deleted). Only removes the item
    // if it's still Pending, so it never clobbers a real IncidentId a
    // concurrent SetIncidentIdAsync already wrote.
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

    // Workers run as bare Lambdas with no DI container — same pattern as
    // EventBridgeEventPublisher.FromEnvironment().
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
            // Already resolved (e.g. a redelivered SQS message re-ran this
            // after the first attempt already succeeded) — nothing to do.
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
// has finished being created. Left uncaught so the Lambda invocation fails and
// SQS redelivers the message after its visibility timeout — by then the
// winner should have called SetIncidentIdAsync, so the retry attaches
// normally. This deliberately avoids busy-polling within a single invocation.
public class FingerprintPendingException(string fingerprint)
    : Exception($"Fingerprint '{fingerprint}' is still pending incident creation; will retry on redelivery.");
