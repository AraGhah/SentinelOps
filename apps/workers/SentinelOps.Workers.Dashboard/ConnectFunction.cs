using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Workers.Shared;

namespace SentinelOps.Workers.Dashboard;

// Handles the WebSocket API's $connect route. Browsers can't set custom
// headers on a WebSocket handshake, so the client passes its Cognito access
// token and the organization it wants dashboard events for as query string
// parameters: wss://.../prod?orgId={orgId}&token={accessToken}. Rejecting
// here (a non-200 response) refuses the connection outright — this is the
// only enforcement point for "prevent users from receiving another
// organization's events," so it has to check real org membership, not just
// that the token is valid.
public class ConnectFunction
{
    private static readonly TimeSpan ConnectionTtl = TimeSpan.FromHours(2);

    private readonly string _connectionString;
    private readonly ITokenValidator _tokenValidator;
    private readonly IConnectionStore _connectionStore;

    public ConnectFunction() : this(
        DbConnectionStringResolver.FromEnvironment(),
        CognitoTokenValidator.FromEnvironment(),
        DynamoDbConnectionStore.FromEnvironment())
    {
    }

    public ConnectFunction(string connectionString, ITokenValidator tokenValidator, IConnectionStore connectionStore)
    {
        _connectionString = connectionString;
        _tokenValidator = tokenValidator;
        _connectionStore = connectionStore;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters;
        var orgIdRaw = queryParams is not null && queryParams.TryGetValue("orgId", out var orgIdValue) ? orgIdValue : null;
        var token = queryParams is not null && queryParams.TryGetValue("token", out var tokenValue) ? tokenValue : null;

        if (!Guid.TryParse(orgIdRaw, out var organizationId))
        {
            return Deny("Missing or invalid orgId query parameter.");
        }

        var sub = await _tokenValidator.ValidateAsync(token, CancellationToken.None);
        if (sub is null)
        {
            return Deny("Invalid or expired token.");
        }

        await using var db = WorkerDbContextFactory.Create(_connectionString, organizationId);

        var user = await db.Users.FirstOrDefaultAsync(u => u.CognitoSub == sub);
        if (user is null)
        {
            return Deny("Unknown user.");
        }

        // Query filter already scopes this to `organizationId` — an
        // inactive or nonexistent membership means no row comes back.
        var isMember = await db.OrganizationMemberships.AnyAsync(m => m.UserId == user.Id && m.IsActive);
        if (!isMember)
        {
            return Deny("Not a member of this organization.");
        }

        await _connectionStore.AddAsync(request.RequestContext.ConnectionId, organizationId, ConnectionTtl, CancellationToken.None);

        return new APIGatewayProxyResponse { StatusCode = 200 };
    }

    private static APIGatewayProxyResponse Deny(string reason) =>
        new() { StatusCode = 401, Body = reason };
}
