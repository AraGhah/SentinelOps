using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SentinelOps.Api.Auth;
using SentinelOps.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<SentinelOpsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("SentinelOpsDb")));

builder.Services
    .AddOptions<CognitoOptions>()
    .Bind(builder.Configuration.GetSection(CognitoOptions.SectionName))
    .ValidateDataAnnotations();
builder.Services.AddSingleton<CognitoAuthService>();

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
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
