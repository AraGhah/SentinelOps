using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelOps.Api.Attachments;
using SentinelOps.Api.Ingestion;
using SentinelOps.Events;

namespace SentinelOps.Api.Tests;

// A dedicated WebApplicationFactory pointed at a connection string that refuses to
// connect, the closest a test gets to "database is unavailable." Confirms
// GlobalExceptionHandler turns that into a clean 500 ProblemDetails response instead
// of an unhandled exception taking the host down.
public class DatabaseUnavailableTests
{
    [Fact]
    public async Task Request_WhenDatabaseIsUnreachable_Returns500ProblemDetailsWithoutCrashingHost()
    {
        await using var factory = BuildFactory();
        var client = factory.CreateClientFor(TestClientFactory.NewSub());

        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(body);
        // GlobalExceptionHandler must not leak the Npgsql connection failure
        // (host/port/credentials) into the response body.
        Assert.DoesNotContain(body!, kv => kv.Value?.ToString()?.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) == true);

        // /healthz has no DB dependency, so this proves the host itself is still up.
        var health = await factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Closed local port: fails fast (connection refused)
                    // rather than hanging for a long OS-level TCP timeout.
                    ["ConnectionStrings:SentinelOpsDb"] =
                        "Host=127.0.0.1;Port=1;Database=unreachable;Username=nope;Password=nope;Timeout=2",
                    ["Cognito:Region"] = "us-east-1",
                    ["Cognito:UserPoolId"] = "test-pool",
                    ["Cognito:ClientId"] = "test-client",
                    ["Aws:Attachments:Region"] = "us-east-1",
                    ["Aws:Attachments:BucketName"] = "test-attachments-bucket",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                });

                services.AddSingleton<FakeAlertQueuePublisher>();
                services.AddSingleton<IAlertQueuePublisher>(sp => sp.GetRequiredService<FakeAlertQueuePublisher>());
                services.AddSingleton<FakeEventPublisher>();
                services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<FakeEventPublisher>());
                services.AddSingleton<FakeAttachmentStorageService>();
                services.AddSingleton<IAttachmentStorageService>(sp => sp.GetRequiredService<FakeAttachmentStorageService>());
            });
        });
}
