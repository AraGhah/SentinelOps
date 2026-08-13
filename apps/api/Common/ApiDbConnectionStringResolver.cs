using System.Text.Json;

namespace SentinelOps.Api.Common;

// ECS injects the RDS-generated Secrets Manager secret as a single JSON blob
// (DB_SECRET_JSON, via ecs.Secret.fromSecretsManager in the CDK stack) rather
// than a ready-made connection string — Secrets Manager has no way to store
// the finished Npgsql string itself without a second, redundant secret. This
// builds it once at startup and feeds it into configuration under the same
// key (ConnectionStrings:SentinelOpsDb) local dev already uses via
// appsettings.json, so AddDbContext doesn't need to know which environment
// it's running in.
public static class ApiDbConnectionStringResolver
{
    public static void ApplyToConfiguration(IConfigurationBuilder configuration)
    {
        var secretJson = Environment.GetEnvironmentVariable("DB_SECRET_JSON");
        if (string.IsNullOrEmpty(secretJson))
        {
            return; // local dev / docker-compose — appsettings.json already has a connection string.
        }

        using var document = JsonDocument.Parse(secretJson);
        var root = document.RootElement;

        var host = root.GetProperty("host").GetString();
        var port = root.GetProperty("port").GetInt32();
        var dbname = root.GetProperty("dbname").GetString();
        var username = root.GetProperty("username").GetString();
        var password = root.GetProperty("password").GetString();

        var connectionString = $"Host={host};Port={port};Database={dbname};Username={username};Password={password};" +
            "SSL Mode=Require;Trust Server Certificate=true";

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SentinelOpsDb"] = connectionString,
        });
    }
}
