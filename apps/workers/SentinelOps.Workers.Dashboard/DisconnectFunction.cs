using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using SentinelOps.Workers.Shared;

namespace SentinelOps.Workers.Dashboard;

// Handles the WebSocket API's $disconnect route — removes the connection row
// so BroadcastFunction stops trying to push events to it. Always returns 200:
// per AWS's guidance, failing $disconnect doesn't stop the connection from
// closing, it just leaves you without a clean signal, and the connection row
// carries a TTL as a backstop for whatever cases get here anyway (e.g. a
// client that disappears mid-network-partition without a clean close frame).
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
