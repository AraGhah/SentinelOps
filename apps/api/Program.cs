using System.Threading.RateLimiting;
using Amazon.XRay.Recorder.Handlers.AspNetCore;
using Amazon.XRay.Recorder.Handlers.AwsSdk;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Amazon.S3;
using Npgsql;
using SentinelOps.Api.Attachments;
using SentinelOps.Api.Auth;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Ingestion;
using SentinelOps.Api.Reports;
using SentinelOps.Api.Tenancy;
using SentinelOps.Events;

// Community license (free for small teams/companies) — required at startup by
// QuestPDF or every document-generation call throws.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Must run before any AmazonServiceClient is constructed (every registration
// below that builds one) — patches the SDK's request pipeline so each AWS
// call becomes an X-Ray subsegment of whatever segment UseXRay opened for
// the current request. Harmless with no X-Ray daemon listening (e.g. local
// `dotnet run`): segments are sent over connectionless UDP and just drop.
AWSSDKHandler.RegisterXRayForAllServices();

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs to stdout — ECS ships them to ApiLogGroup via the
// awslogs driver (see ApiStack). Replaces the default plain-text console
// formatter; Debug/EventSource/EventLog providers from CreateBuilder's
// defaults are dropped along with it; that's fine because this always runs
// as a container, and MetricsEmitter's EMF lines below deliberately bypass
// this pipeline entirely so they stay unwrapped JSON.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});

// Overrides ConnectionStrings:SentinelOpsDb from the ECS-injected Secrets
// Manager JSON when running in AWS; no-op locally (appsettings.json already
// has a connection string there).
ApiDbConnectionStringResolver.ApplyToConfiguration(builder.Configuration);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentOrganizationAccessor, CurrentOrganizationAccessor>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<IAuthorizationHandler, OrganizationRoleAuthorizationHandler>();

builder.Services.AddHealthChecks();

// Graceful shutdown: on SIGTERM the host stops accepting new requests
// immediately but waits up to this long for in-flight requests to finish
// before forcing them closed. Matches ECS's container `stopTimeout` (see
// ApiStack) — kept in sync deliberately, since a shorter value here than
// ECS's SIGKILL deadline would just mean ECS does the killing instead of a
// clean exit, and a longer one would get cut off by ECS regardless.
builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = TimeSpan.FromSeconds(28));

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

// apps/web is the only browser-based caller today; additional origins are
// added here (or via CORS__ALLOWEDORIGINS__n env vars), never a wildcard,
// since AllowCredentials() is required for the JWT bearer token to reach the
// API from the browser.
builder.Services.AddCors(options => options.AddPolicy("Default", policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    .AllowCredentials()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict, per-IP: applied to auth endpoints (login/register/forgot-password/etc.)
    // to slow down credential-stuffing and account-enumeration attempts.
    options.AddFixedWindowLimiter("auth", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });

    // Looser default for the rest of the authenticated API, partitioned per caller
    // (JWT `sub`) so one busy user/org can't starve another.
    options.AddPolicy("api", httpContext =>
    {
        var partitionKey = httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromSeconds(10),
            SegmentsPerWindow = 2,
            QueueLimit = 0,
        });
    });

    // Alert ingestion has no JWT `sub` to partition on — callers authenticate with
    // a per-integration API key instead, so that's the partition key. Tighter than
    // "api" since a single misbehaving source integration shouldn't need 100/10s.
    options.AddPolicy("ingestion", httpContext =>
    {
        var partitionKey = httpContext.Request.RouteValues["integrationId"]?.ToString()
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 50,
            Window = TimeSpan.FromSeconds(10),
            SegmentsPerWindow = 2,
            QueueLimit = 0,
        });
    });
});

// A shared NpgsqlDataSource (rather than handing UseNpgsql a bare connection
// string) is what DatabaseConnectionsMetricService opens its monitoring
// connection through. Built lazily inside this factory delegate, not as a
// top-level statement — ApiTestFixture overrides ConnectionStrings:SentinelOpsDb
// via WebApplicationFactory's ConfigureAppConfiguration, which only takes
// effect by the time DI resolves services, not at the point Program.cs's own
// top-level code runs; reading builder.Configuration here immediately would
// have captured the appsettings.json value instead of the test container's.
builder.Services.AddSingleton(sp =>
    new NpgsqlDataSourceBuilder(sp.GetRequiredService<IConfiguration>().GetConnectionString("SentinelOpsDb")).Build());
builder.Services.AddDbContext<SentinelOpsDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
builder.Services.AddHostedService<DatabaseConnectionsMetricService>();

builder.Services
    .AddOptions<CognitoOptions>()
    .Bind(builder.Configuration.GetSection(CognitoOptions.SectionName))
    .ValidateDataAnnotations();
builder.Services.AddSingleton<CognitoAuthService>();

builder.Services
    .AddOptions<EventBridgeOptions>()
    .Bind(builder.Configuration.GetSection(EventBridgeOptions.SectionName))
    .ValidateDataAnnotations();
builder.Services.AddSingleton<IEventPublisher, EventBridgeEventPublisher>();
builder.Services.AddSingleton<IAlertQueuePublisher, EventBridgeAlertPublisher>();
builder.Services.AddScoped<IAlertIngestionService, AlertIngestionService>();

builder.Services
    .AddOptions<AttachmentStorageOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentStorageOptions.SectionName))
    .ValidateDataAnnotations();
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var region = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AttachmentStorageOptions>>().Value.Region;
    return new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));
});
builder.Services.AddSingleton<IAttachmentStorageService, S3AttachmentStorageService>();

builder.Services
    .AddOptions<ReportStorageOptions>()
    .Bind(builder.Configuration.GetSection(ReportStorageOptions.SectionName))
    .ValidateDataAnnotations();
// Reuses the IAmazonS3 singleton registered above for attachments — same
// account/region, no need for a second S3 client just for a different bucket.
builder.Services.AddSingleton<IReportStorageService, S3ReportStorageService>();

var cognitoOptions = builder.Configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>()!;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cognitoOptions.Issuer;
        // Cognito access tokens carry `client_id`, not `aud` — audience is
        // validated manually below instead of via TokenValidationParameters.
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cognitoOptions.Issuer,
            ValidateAudience = false,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var tokenUse = context.Principal?.FindFirst("token_use")?.Value;
                var clientId = context.Principal?.FindFirst("client_id")?.Value;

                if (tokenUse != "access" || clientId != cognitoOptions.ClientId)
                {
                    context.Fail("Token is not a valid Cognito access token for this client.");
                }

                return Task.CompletedTask;
            },
        };
    })
    // Second scheme, opted into explicitly via [Authorize(AuthenticationSchemes = ...)]
    // on AlertIngestionController — never the default, so it can't accidentally
    // authenticate a request meant for the JWT-protected org endpoints.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    foreach (var role in Enum.GetValues<OrganizationRole>())
    {
        options.AddPolicy(OrgPolicies.ForRole(role), policy =>
            policy.Requirements.Add(new OrganizationRoleRequirement(role)));
    }
});

var app = builder.Build();

// Opens one X-Ray segment per request, wrapping every AWS SDK subsegment
// AWSSDKHandler.RegisterXRayForAllServices() (above) records during it —
// as early in the pipeline as possible so the segment covers the whole
// request, exception handling included.
app.UseXRay("SentinelOpsApi");

app.UseExceptionHandler();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "SentinelOps API v1"));
}

// HSTS on localhost during Development breaks plain-HTTP local dev, so it's
// only enforced once the app is actually reachable over TLS (behind the ALB).
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseCors("Default");

app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

app.MapHealthChecks("/healthz").AllowAnonymous();

// Default rate-limit policy for the whole API; AuthController overrides it
// with the stricter "auth" policy via [EnableRateLimiting("auth")].
app.MapControllers().RequireRateLimiting("api");

app.Run();
