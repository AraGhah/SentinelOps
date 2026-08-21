using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;

namespace SentinelOps.Workers.Shared;

// Narrow wrapper around the one API Gateway Management API operation
// BroadcastFunction needs, so test doubles don't need all of
// IAmazonApiGatewayManagementApi.
public interface IConnectionBroadcaster
{
    // Returns true if delivered, false if the connection is gone and should
    // be removed from the connection store.
    Task<bool> TryPostAsync(string connectionId, byte[] payload, CancellationToken ct);
}

public class ApiGatewayConnectionBroadcaster : IConnectionBroadcaster
{
    private readonly IAmazonApiGatewayManagementApi _client;

    public ApiGatewayConnectionBroadcaster(IAmazonApiGatewayManagementApi client) => _client = client;

    public static ApiGatewayConnectionBroadcaster FromEnvironment()
    {
        var managementEndpoint = Environment.GetEnvironmentVariable("WEBSOCKET_MANAGEMENT_ENDPOINT")
            ?? throw new InvalidOperationException("WEBSOCKET_MANAGEMENT_ENDPOINT environment variable is not set.");

        return new ApiGatewayConnectionBroadcaster(
            new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = managementEndpoint }));
    }

    public async Task<bool> TryPostAsync(string connectionId, byte[] payload, CancellationToken ct)
    {
        try
        {
            using var stream = new MemoryStream(payload);
            await _client.PostToConnectionAsync(new PostToConnectionRequest { ConnectionId = connectionId, Data = stream }, ct);
            return true;
        }
        catch (GoneException)
        {
            return false;
        }
    }
}
