using Amazon.Lambda.APIGatewayEvents;
using SentinelOps.Workers.Dashboard;

namespace SentinelOps.Workers.Tests;

public class DisconnectFunctionTests
{
    [Fact]
    public async Task Disconnect_RemovesConnectionAndReturns200()
    {
        var connectionStore = new FakeConnectionStore();
        await connectionStore.AddAsync("conn-1", Guid.NewGuid(), TimeSpan.FromHours(1), CancellationToken.None);
        var function = new DisconnectFunction(connectionStore);

        var request = new APIGatewayProxyRequest
        {
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { ConnectionId = "conn-1" },
        };
        var response = await function.FunctionHandler(request, SqsEventFactory.Context());

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(connectionStore.Connections);
    }

    [Fact]
    public async Task Disconnect_UnknownConnectionId_StillReturns200()
    {
        var connectionStore = new FakeConnectionStore();
        var function = new DisconnectFunction(connectionStore);

        var request = new APIGatewayProxyRequest
        {
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { ConnectionId = "never-connected" },
        };
        var response = await function.FunctionHandler(request, SqsEventFactory.Context());

        Assert.Equal(200, response.StatusCode);
    }
}
