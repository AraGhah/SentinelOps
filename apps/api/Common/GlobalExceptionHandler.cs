using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SentinelOps.Api.Auth;

namespace SentinelOps.Api.Common;

// Catches anything that escapes a controller action unhandled. Known Cognito
// exceptions still get CognitoExceptionMapper's tailored ProblemDetails (the
// same mapping AuthController's per-action try/catch already uses); everything
// else becomes a generic 500 with no exception detail leaked to the client.
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception for {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        var problem = exception.ToProblemDetails();

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}
