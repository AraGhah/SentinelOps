using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using SentinelOps.Workers.Shared;

namespace SentinelOps.Workers.Dashboard;

// Handles the WebSocket API's $disconnect route — removes the connection row
// so BroadcastFunction stops pushing to it. Always returns 200: failing
// $disconnect doesn't stop the connection from closing anyway, and the
// connection row's TTL is the backstop for unclean disconnects.
public class DisconnectFunction
{
    private readonly IConnectionStore _connectionStore;

    public DisconnectFunction() : this(DynamoDbConnectionStore.FromEnvironment())
    {
    }

    public DisconnectFunction(IConnectionStore connectionStore)
    {
        _connectionStore = connectionStore;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        await _connectionStore.RemoveAsync(request.RequestContext.ConnectionId, CancellationToken.None);
        return new APIGatewayProxyResponse { StatusCode = 200 };
    }
}
