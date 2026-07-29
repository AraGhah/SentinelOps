namespace SentinelOps.Api.Auth;

public record RegisterRequest(string Email, string Password);

public record ConfirmEmailRequest(string Email, string Code);

public record ResendConfirmationRequest(string Email);

public record LoginRequest(string Email, string Password);

// One of Tokens or MfaChallenge is populated, never both.
public record LoginResponse(TokenSet? Tokens, MfaChallenge? MfaChallenge);

public record MfaChallenge(string Session, string Email);

public record MfaVerifyRequest(string Email, string Code, string Session);

public record RefreshRequest(string RefreshToken, string Email);

public record TokenSet(string AccessToken, string IdToken, string RefreshToken, int ExpiresIn);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Code, string NewPassword);

public record LogoutRequest(string AccessToken);

public record MfaSetupResponse(string SecretCode, string OtpAuthUri);

public record MfaEnableRequest(string Code);
