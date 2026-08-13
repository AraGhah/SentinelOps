namespace SentinelOps.Api.Common;

// SentinelOps.Api is a pure JSON API — it never renders HTML of its own — so
// the CSP is as strict as CSP gets. If that ever changes (e.g. serving
// Swagger UI outside Development), this policy needs revisiting.
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
