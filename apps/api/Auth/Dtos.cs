using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Auth;

public record RegisterRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MinLength(8), MaxLength(256)] string Password);

public record ConfirmEmailRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MaxLength(20)] string Code);

public record ResendConfirmationRequest([property: Required, EmailAddress, MaxLength(320)] string Email);

public record LoginRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MaxLength(256)] string Password);

// One of Tokens or MfaChallenge is populated, never both.
public record LoginResponse(TokenSet? Tokens, MfaChallenge? MfaChallenge);

public record MfaChallenge(string Session, string Email);

public record MfaVerifyRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MaxLength(20)] string Code,
    [property: Required, MaxLength(2048)] string Session);

public record RefreshRequest(
    [property: Required, MaxLength(2048)] string RefreshToken,
    [property: Required, EmailAddress, MaxLength(320)] string Email);

public record TokenSet(string AccessToken, string IdToken, string RefreshToken, int ExpiresIn);

public record ForgotPasswordRequest([property: Required, EmailAddress, MaxLength(320)] string Email);

public record ResetPasswordRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MaxLength(20)] string Code,
    [property: Required, MinLength(8), MaxLength(256)] string NewPassword);

public record LogoutRequest([property: Required, MaxLength(4096)] string AccessToken);

public record MfaSetupResponse(string SecretCode, string OtpAuthUri);

public record MfaEnableRequest([property: Required, MaxLength(20)] string Code);
