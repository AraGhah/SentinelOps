using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.Auth;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(8), MaxLength(256)] string Password);

public record ConfirmEmailRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MaxLength(20)] string Code);

public record ResendConfirmationRequest([Required, EmailAddress, MaxLength(320)] string Email);

public record LoginRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MaxLength(256)] string Password);

// One of Tokens or MfaChallenge is populated, never both.
public record LoginResponse(TokenSet? Tokens, MfaChallenge? MfaChallenge);

public record MfaChallenge(string Session, string Email);

public record MfaVerifyRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MaxLength(20)] string Code,
    [Required, MaxLength(2048)] string Session);

public record RefreshRequest(
    [Required, MaxLength(2048)] string RefreshToken,
    [Required, EmailAddress, MaxLength(320)] string Email);

public record TokenSet(string AccessToken, string IdToken, string RefreshToken, int ExpiresIn);

public record ForgotPasswordRequest([Required, EmailAddress, MaxLength(320)] string Email);

public record ResetPasswordRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MaxLength(20)] string Code,
    [Required, MinLength(8), MaxLength(256)] string NewPassword);

public record LogoutRequest([Required, MaxLength(4096)] string AccessToken);

public record MfaSetupResponse(string SecretCode, string OtpAuthUri);

public record MfaEnableRequest([Required, MaxLength(20)] string Code);
