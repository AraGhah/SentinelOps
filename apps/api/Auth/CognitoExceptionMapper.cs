using Amazon.CognitoIdentityProvider.Model;
using Microsoft.AspNetCore.Mvc;

namespace SentinelOps.Api.Auth;

public static class CognitoExceptionMapper
{
    // Maps AWSSDK Cognito exceptions to RFC 7807 ProblemDetails that
    // apps/web's apiClient already knows how to parse (title/detail).
    public static ProblemDetails ToProblemDetails(this Exception ex) => ex switch
    {
        UsernameExistsException => Problem(409, "Account already exists", "An account with this email already exists."),
        UserNotFoundException => Problem(404, "Account not found", "No account was found for this email."),
        UserNotConfirmedException => Problem(403, "Email not verified", "Please verify your email before signing in."),
        NotAuthorizedException => Problem(401, "Invalid credentials", "Incorrect email or password."),
        CodeMismatchException => Problem(400, "Invalid code", "The verification code is incorrect."),
        ExpiredCodeException => Problem(400, "Code expired", "The verification code has expired. Request a new one."),
        InvalidPasswordException ipe => Problem(400, "Weak password", ipe.Message),
        LimitExceededException => Problem(429, "Too many attempts", "Please wait a moment and try again."),
        TooManyRequestsException => Problem(429, "Too many attempts", "Please wait a moment and try again."),
        TooManyFailedAttemptsException => Problem(429, "Too many attempts", "Please wait a moment and try again."),
        AliasExistsException => Problem(409, "Account already exists", "An account with this email already exists."),
        EnableSoftwareTokenMFAException => Problem(400, "MFA setup failed", "The authenticator code could not be verified."),
        InvalidParameterException ipe => Problem(400, "Invalid request", ipe.Message),
        _ => Problem(500, "Something went wrong", "Please try again."),
    };

    private static ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };
}
