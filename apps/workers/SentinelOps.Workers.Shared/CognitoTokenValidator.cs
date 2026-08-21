using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SentinelOps.Workers.Shared;

public interface ITokenValidator
{
    // Returns the token's `sub` claim on success, or null if invalid.
    Task<string?> ValidateAsync(string? accessToken, CancellationToken ct);
}

// Validates a Cognito access token outside ASP.NET, for the WebSocket
// $connect route which has no HTTP middleware pipeline (see
// ConnectFunction). Mirrors Program.cs's AddJwtBearer checks: JWKS
// signature, issuer, and client_id/token_use checked manually since Cognito
// access tokens don't carry a standard `aud`.
public class CognitoTokenValidator : ITokenValidator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;
    private readonly string _issuer;
    private readonly string _clientId;

    public CognitoTokenValidator(string region, string userPoolId, string clientId)
    {
        _issuer = $"https://cognito-idp.{region}.amazonaws.com/{userPoolId}";
        _clientId = clientId;
        // Caches the JWKS document; a warm Lambda instance reuses this across invocations.
        _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{_issuer}/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
    }

    public static CognitoTokenValidator FromEnvironment()
    {
        var region = Environment.GetEnvironmentVariable("COGNITO_REGION")
            ?? throw new InvalidOperationException("COGNITO_REGION environment variable is not set.");
        var userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")
            ?? throw new InvalidOperationException("COGNITO_USER_POOL_ID environment variable is not set.");
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
            ?? throw new InvalidOperationException("COGNITO_CLIENT_ID environment variable is not set.");

        return new CognitoTokenValidator(region, userPoolId, clientId);
    }

    // Returns the token's `sub` claim (matches User.CognitoSub), or null if
    // missing, expired, mis-signed, or not issued to this app client.
    public async Task<string?> ValidateAsync(string? accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        try
        {
            var config = await _configManager.GetConfigurationAsync(ct);
            var handler = new JwtSecurityTokenHandler();

            var principal = handler.ValidateToken(accessToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = false,
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
            }, out _);

            var tokenUse = principal.FindFirst("token_use")?.Value;
            var clientId = principal.FindFirst("client_id")?.Value;
            if (tokenUse != "access" || clientId != _clientId) return null;

            return principal.FindFirst("sub")?.Value;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }
}
