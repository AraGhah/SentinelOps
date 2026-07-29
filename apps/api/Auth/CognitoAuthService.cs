using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;
using CognitoModel = Amazon.CognitoIdentityProvider.Model;

namespace SentinelOps.Api.Auth;

public class CognitoAuthService
{
    private const string MfaChallengeName = "SOFTWARE_TOKEN_MFA";

    private readonly AmazonCognitoIdentityProviderClient _client;
    private readonly CognitoOptions _options;

    public CognitoAuthService(IOptions<CognitoOptions> options)
    {
        _options = options.Value;
        // Register/login/reset/MFA operations are authenticated via the app client ID
        // and bearer tokens, not SigV4 — anonymous credentials are sufficient and mean
        // the API never needs its own AWS access keys for auth flows.
        _client = new AmazonCognitoIdentityProviderClient(
            new AnonymousAWSCredentials(),
            RegionEndpoint.GetBySystemName(_options.Region));
    }

    public async Task RegisterAsync(string email, string password)
    {
        await _client.SignUpAsync(new SignUpRequest
        {
            ClientId = _options.ClientId,
            Username = email,
            Password = password,
            UserAttributes = [new AttributeType { Name = "email", Value = email }],
        });
    }

    public async Task ConfirmEmailAsync(string email, string code)
    {
        await _client.ConfirmSignUpAsync(new ConfirmSignUpRequest
        {
            ClientId = _options.ClientId,
            Username = email,
            ConfirmationCode = code,
        });
    }

    public async Task ResendConfirmationCodeAsync(string email)
    {
        await _client.ResendConfirmationCodeAsync(new ResendConfirmationCodeRequest
        {
            ClientId = _options.ClientId,
            Username = email,
        });
    }

    public async Task<LoginResponse> LoginAsync(string email, string password)
    {
        var result = await _client.InitiateAuthAsync(new InitiateAuthRequest
        {
            ClientId = _options.ClientId,
            AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
            AuthParameters = new Dictionary<string, string>
            {
                ["USERNAME"] = email,
                ["PASSWORD"] = password,
            },
        });

        if (result.ChallengeName == MfaChallengeName)
        {
            return new LoginResponse(null, new MfaChallenge(result.Session, email));
        }

        return new LoginResponse(ToTokenSet(result.AuthenticationResult), null);
    }

    public async Task<TokenSet> VerifyMfaAsync(string email, string code, string session)
    {
        var result = await _client.RespondToAuthChallengeAsync(new RespondToAuthChallengeRequest
        {
            ClientId = _options.ClientId,
            ChallengeName = MfaChallengeName,
            Session = session,
            ChallengeResponses = new Dictionary<string, string>
            {
                ["USERNAME"] = email,
                ["SOFTWARE_TOKEN_MFA_CODE"] = code,
            },
        });

        return ToTokenSet(result.AuthenticationResult)!;
    }

    public async Task<TokenSet> RefreshAsync(string refreshToken)
    {
        var result = await _client.InitiateAuthAsync(new InitiateAuthRequest
        {
            ClientId = _options.ClientId,
            AuthFlow = AuthFlowType.REFRESH_TOKEN_AUTH,
            AuthParameters = new Dictionary<string, string>
            {
                ["REFRESH_TOKEN"] = refreshToken,
            },
        });

        // Cognito doesn't reissue a refresh token on refresh; keep using the caller's.
        var tokens = ToTokenSet(result.AuthenticationResult)!;
        return tokens with { RefreshToken = refreshToken };
    }

    public async Task ForgotPasswordAsync(string email)
    {
        await _client.ForgotPasswordAsync(new CognitoModel.ForgotPasswordRequest
        {
            ClientId = _options.ClientId,
            Username = email,
        });
    }

    public async Task ResetPasswordAsync(string email, string code, string newPassword)
    {
        await _client.ConfirmForgotPasswordAsync(new ConfirmForgotPasswordRequest
        {
            ClientId = _options.ClientId,
            Username = email,
            ConfirmationCode = code,
            Password = newPassword,
        });
    }

    public async Task LogoutAsync(string accessToken)
    {
        await _client.GlobalSignOutAsync(new GlobalSignOutRequest
        {
            AccessToken = accessToken,
        });
    }

    public async Task<MfaSetupResponse> StartMfaSetupAsync(string accessToken, string email)
    {
        var result = await _client.AssociateSoftwareTokenAsync(new AssociateSoftwareTokenRequest
        {
            AccessToken = accessToken,
        });

        var label = Uri.EscapeDataString($"SentinelOps:{email}");
        var otpAuthUri = $"otpauth://totp/{label}?secret={result.SecretCode}&issuer=SentinelOps";

        return new MfaSetupResponse(result.SecretCode, otpAuthUri);
    }

    public async Task ConfirmMfaSetupAsync(string accessToken, string code)
    {
        await _client.VerifySoftwareTokenAsync(new VerifySoftwareTokenRequest
        {
            AccessToken = accessToken,
            UserCode = code,
        });

        await _client.SetUserMFAPreferenceAsync(new SetUserMFAPreferenceRequest
        {
            AccessToken = accessToken,
            SoftwareTokenMfaSettings = new SoftwareTokenMfaSettingsType
            {
                Enabled = true,
                PreferredMfa = true,
            },
        });
    }

    private static TokenSet? ToTokenSet(AuthenticationResultType? result)
    {
        if (result is null) return null;
        return new TokenSet(result.AccessToken, result.IdToken, result.RefreshToken, result.ExpiresIn ?? 0);
    }
}
