using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentinelOps.Api.Data;
using SentinelOps.Api.Ingestion;
using SentinelOps.Events;
using Testcontainers.PostgreSql;

namespace SentinelOps.Api.Tests;

// One Postgres container + one WebApplicationFactory shared across every test
// in the "Api" collection (see ApiCollection). Real Npgsql/EF behavior (unique
// indexes, query filter SQL translation, cascade deletes) matters for
// tenant-isolation guarantees, so this intentionally isn't the InMemory provider.
public class ApiTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("sentinelops_test")
        .WithUsername("sentinelops")
        .WithPassword("sentinelops")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SentinelOpsDb"] = _container.GetConnectionString(),
                    ["Cognito:Region"] = "us-east-1",
                    ["Cognito:UserPoolId"] = "test-pool",
                    ["Cognito:ClientId"] = "test-client",
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

                // No real SQS queue in tests — swap in an in-memory recorder that
                // tests can resolve and assert against.
                services.AddSingleton<FakeAlertQueuePublisher>();
                services.AddSingleton<IAlertQueuePublisher>(sp => sp.GetRequiredService<FakeAlertQueuePublisher>());

                // No real EventBridge bus in tests either — same recorder pattern.
                services.AddSingleton<FakeEventPublisher>();
                services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<FakeEventPublisher>());
            });
        });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelOpsDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiTestFixture>;
