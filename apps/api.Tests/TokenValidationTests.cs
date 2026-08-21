using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SentinelOps.Api.Tests;

// Exercises the actual lifetime-validation mechanism (ValidateLifetime = true) rather
// than hitting a live Cognito user pool for JWKS, which isn't available in CI/local runs.
// TestAuthHandler bypasses real token validation everywhere else, so this is the one
// place that failure mode gets coverage.
public class TokenValidationTests
{
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("test-signing-key-at-least-256-bits-long-for-hs256"));

    [Fact]
    public void ValidateToken_ExpiredToken_ThrowsSecurityTokenExpiredException()
    {
        var token = BuildToken(expiresAtUtc: DateTime.UtcNow.AddMinutes(-5));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        var ex = Assert.Throws<SecurityTokenExpiredException>(() =>
            handler.ValidateToken(token, ValidationParameters(), out _));

        Assert.True(ex.Expires < DateTime.UtcNow);
    }

    [Fact]
    public void ValidateToken_NotYetExpiredToken_Succeeds()
    {
        var token = BuildToken(expiresAtUtc: DateTime.UtcNow.AddMinutes(5));
        // Without MapInboundClaims = false, the default inbound claim map silently renames
        // "sub" to ClaimTypes.NameIdentifier and FindFirst("sub") below would wrongly return null.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        var principal = handler.ValidateToken(token, ValidationParameters(), out _);

        Assert.Equal("test-sub", principal.FindFirst("sub")?.Value);
    }

    private static TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "https://cognito-idp.us-east-1.amazonaws.com/test-pool",
        ValidateAudience = false,
        ValidateLifetime = true,
        IssuerSigningKey = SigningKey,
    };

    private static string BuildToken(DateTime expiresAtUtc)
    {
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "https://cognito-idp.us-east-1.amazonaws.com/test-pool",
            Subject = new ClaimsIdentity([
                new Claim("sub", "test-sub"),
                new Claim("token_use", "access"),
                new Claim("client_id", "test-client"),
            ]),
            NotBefore = expiresAtUtc.AddMinutes(-10),
            Expires = expiresAtUtc,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return handler.CreateEncodedJwt(descriptor);
    }
}
