using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SentinelOps.Api.Auth;

namespace SentinelOps.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public class AuthController(CognitoAuthService authService, ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        try
        {
            await authService.RegisterAsync(request.Email, request.Password);
            return NoContent();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request)
    {
        try
        {
            await authService.ConfirmEmailAsync(request.Email, request.Code);
            return NoContent();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationRequest request)
    {
        try
        {
            await authService.ResendConfirmationCodeAsync(request.Email);
            return NoContent();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
    {
        try
        {
            var response = await authService.LoginAsync(request.Email, request.Password);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("mfa/verify")]
    public async Task<ActionResult<TokenSet>> VerifyMfa(MfaVerifyRequest request)
    {
        try
        {
            var tokens = await authService.VerifyMfaAsync(request.Email, request.Code, request.Session);
            return Ok(tokens);
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<TokenSet>> Refresh(RefreshRequest request)
    {
        try
        {
            var tokens = await authService.RefreshAsync(request.RefreshToken);
            return Ok(tokens);
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request)
    {
        try
        {
            await authService.ForgotPasswordAsync(request.Email);
            return NoContent();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request)
    {
        try
        {
            await authService.ResetPasswordAsync(request.Email, request.Code, request.NewPassword);
            return NoContent();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request)
    {
        try
        {
            await authService.LogoutAsync(request.AccessToken);
        }
        catch (Exception ex)
        {
            // Logout is best-effort from the client's perspective — the cookie is
            // cleared regardless — but log so we can spot real Cognito issues.
            logger.LogWarning(ex, "GlobalSignOut failed");
        }

        return NoContent();
    }

    [Authorize]
    [HttpGet("mfa/setup")]
    public async Task<ActionResult<MfaSetupResponse>> StartMfaSetup()
    {
        var accessToken = HttpContext.GetBearerToken();
        var email = User.FindFirst("username")?.Value ?? User.Identity?.Name ?? "user";

        try
        {
            var setup = await authService.StartMfaSetupAsync(accessToken, email);
            return Ok(setup);
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [Authorize]
    [HttpPost("mfa/enable")]
    public async Task<IActionResult> EnableMfa(MfaEnableRequest request)
    {
        var accessToken = HttpContext.GetBearerToken();

        try
        {
            await authService.ConfirmMfaSetupAsync(accessToken, request.Code);
            return NoContent();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    private ObjectResult FromException(Exception ex)
    {
        var problem = ex.ToProblemDetails();
        return StatusCode(problem.Status ?? 500, problem);
    }
}

internal static class HttpContextExtensions
{
    public static string GetBearerToken(this HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : string.Empty;
    }
}
