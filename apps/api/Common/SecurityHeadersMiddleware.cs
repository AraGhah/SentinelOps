namespace SentinelOps.Api.Common;

// Pure JSON API, never renders HTML, so CSP is maximally strict. Revisit if that changes
// (e.g. serving Swagger UI outside Development).
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.Append("X-Content-Type-Options", "nosniff");
        headers.Append("X-Frame-Options", "DENY");
        headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.Append("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
        headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");

        await next(context);
    }
}
