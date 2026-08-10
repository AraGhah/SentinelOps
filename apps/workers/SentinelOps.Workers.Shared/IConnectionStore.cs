using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace SentinelOps.Workers.Shared;

// Tracks live WebSocket connections for the real-time dashboard (section 19).
// DynamoDB rather than Postgres for the same reason as the alert-fingerprint
// store: high-churn, ephemeral, keyed by a single id, with a TTL safety net —
// not the kind of data the relational store needs to own.
public interface IConnectionStore
{
    Task AddAsync(string connectionId, Guid organizationId, TimeSpan ttl, CancellationToken ct);

    Task RemoveAsync(string connectionId, CancellationToken ct);

    Task<IReadOnlyList<string>> GetConnectionIdsAsync(Guid organizationId, CancellationToken ct);
}

public class DynamoDbConnectionStore : IConnectionStore
{
    private const string PartitionKey = "ConnectionId";
    private const string OrganizationIndexName = "OrganizationId-index";

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;

    public DynamoDbConnectionStore(IAmazonDynamoDB client, string tableName)
    {
        _client = client;
        _tableName = tableName;
    }

    public static DynamoDbConnectionStore FromEnvironment()
    {
        var tableName = Environment.GetEnvironmentVariable("CONNECTIONS_TABLE_NAME")
            ?? throw new InvalidOperationException("CONNECTIONS_TABLE_NAME environment variable is not set.");
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.");

        return new DynamoDbConnectionStore(new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(region)), tableName);
    }

    public Task AddAsync(string connectionId, Guid organizationId, TimeSpan ttl, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return _client.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                [PartitionKey] = new(connectionId),
                ["OrganizationId"] = new(organizationId.ToString()),
                ["ConnectedAtUtc"] = new() { N = now.ToUnixTimeSeconds().ToString() },
                ["ExpiresAt"] = new() { N = now.Add(ttl).ToUnixTimeSeconds().ToString() },
            },
        }, ct);
    }

    public Task RemoveAsync(string connectionId, CancellationToken ct) =>
        _client.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { [PartitionKey] = new(connectionId) },
        }, ct);

    public async Task<IReadOnlyList<string>> GetConnectionIdsAsync(Guid organizationId, CancellationToken ct)
    {
        var connectionIds = new List<string>();
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await _client.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = OrganizationIndexName,
                KeyConditionExpression = "OrganizationId = :orgId",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":orgId"] = new(organizationId.ToString()),
                },
                ExclusiveStartKey = lastKey,
            }, ct);

            connectionIds.AddRange(response.Items.Select(item => item[PartitionKey].S));
            lastKey = response.LastEvaluatedKey?.Count > 0 ? response.LastEvaluatedKey : null;
        }
        while (lastKey is not null);

        return connectionIds;
    }
}
