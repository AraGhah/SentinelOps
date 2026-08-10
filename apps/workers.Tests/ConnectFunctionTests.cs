using Amazon.Lambda.APIGatewayEvents;
using SentinelOps.Api.Domain;
using SentinelOps.Workers.Dashboard;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class ConnectFunctionTests(WorkerTestFixture fixture)
{
    private static APIGatewayProxyRequest Request(string connectionId, string? orgId, string? token) => new()
    {
        QueryStringParameters = new Dictionary<string, string>()
            .Concat(orgId is not null ? new[] { new KeyValuePair<string, string>("orgId", orgId) } : [])
            .Concat(token is not null ? new[] { new KeyValuePair<string, string>("token", token) } : [])
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { ConnectionId = connectionId },
    };

    [Fact]
    public async Task Connect_ValidTokenAndActiveMember_AcceptsAndStoresConnection()
    {
        var orgId = Guid.NewGuid();
        var cognitoSub = Guid.NewGuid().ToString();
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var user = TestData.NewUser(db);
            user.CognitoSub = cognitoSub;
            db.OrganizationMemberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid(), OrganizationId = orgId, UserId = user.Id, Role = OrganizationRole.Responder,
                IsActive = true, InvitedByUserId = user.Id, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var connectionStore = new FakeConnectionStore();
        var function = new ConnectFunction(fixture.ConnectionString, new FakeTokenValidator(cognitoSub), connectionStore);

        var response = await function.FunctionHandler(Request("conn-1", orgId.ToString(), "valid-token"), SqsEventFactory.Context());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(orgId, Assert.Single(connectionStore.Connections).Value);
    }

    [Fact]
    public async Task Connect_InvalidToken_IsDenied()
    {
        var orgId = Guid.NewGuid();
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            await db.SaveChangesAsync();
        }

        var connectionStore = new FakeConnectionStore();
        var function = new ConnectFunction(fixture.ConnectionString, new FakeTokenValidator(validTokenSub: null), connectionStore);

        var response = await function.FunctionHandler(Request("conn-2", orgId.ToString(), "garbage-token"), SqsEventFactory.Context());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(connectionStore.Connections);
    }

    [Fact]
    public async Task Connect_ValidTokenButNotAMember_IsDenied()
    {
        var orgId = Guid.NewGuid();
        var cognitoSub = Guid.NewGuid().ToString();
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            TestData.NewUser(db).CognitoSub = cognitoSub; // exists, but no OrganizationMembership row
            await db.SaveChangesAsync();
        }

        var connectionStore = new FakeConnectionStore();
        var function = new ConnectFunction(fixture.ConnectionString, new FakeTokenValidator(cognitoSub), connectionStore);

        var response = await function.FunctionHandler(Request("conn-3", orgId.ToString(), "valid-token"), SqsEventFactory.Context());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(connectionStore.Connections);
    }

    [Fact]
    public async Task Connect_MissingOrgId_IsDenied()
    {
        var connectionStore = new FakeConnectionStore();
        var function = new ConnectFunction(fixture.ConnectionString, new FakeTokenValidator("some-sub"), connectionStore);

        var response = await function.FunctionHandler(Request("conn-4", orgId: null, "valid-token"), SqsEventFactory.Context());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(connectionStore.Connections);
    }

    [Fact]
    public async Task Connect_InactiveMembership_IsDenied()
    {
        var orgId = Guid.NewGuid();
        var cognitoSub = Guid.NewGuid().ToString();
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var user = TestData.NewUser(db);
            user.CognitoSub = cognitoSub;
            db.OrganizationMemberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid(), OrganizationId = orgId, UserId = user.Id, Role = OrganizationRole.Responder,
                IsActive = false, InvitedByUserId = user.Id, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var connectionStore = new FakeConnectionStore();
        var function = new ConnectFunction(fixture.ConnectionString, new FakeTokenValidator(cognitoSub), connectionStore);

        var response = await function.FunctionHandler(Request("conn-5", orgId.ToString(), "valid-token"), SqsEventFactory.Context());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(connectionStore.Connections);
    }
}
