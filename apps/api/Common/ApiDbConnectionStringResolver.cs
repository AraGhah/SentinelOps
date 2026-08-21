using System.Text.Json;

namespace SentinelOps.Api.Common;

// ECS injects the RDS secret as a JSON blob (DB_SECRET_JSON) rather than a finished
// connection string. Builds it once at startup under the same config key
// (ConnectionStrings:SentinelOpsDb) local dev uses, so AddDbContext stays env-agnostic.
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
