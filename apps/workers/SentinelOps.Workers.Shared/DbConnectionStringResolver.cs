using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace SentinelOps.Workers.Shared;

// Workers used to read a plaintext CONNECTION_STRING env var populated from a
// CFN parameter. That's gone — CDK now grants each function read access to
// the RDS-generated Secrets Manager secret (DB_SECRET_ARN) instead, which is
// what makes credential rotation possible without redeploying every Lambda.
// Resolved once per cold start (constructors call this synchronously, same
// as EventBridgeEventPublisher.FromEnvironment), so a rotated secret only
// takes effect on the function's next cold start — see
// docs/security/security-assumptions.md for that tradeoff.
public static class DbConnectionStringResolver
{
    public static string FromEnvironment()
    {
        var secretArn = Environment.GetEnvironmentVariable("DB_SECRET_ARN")
            ?? throw new InvalidOperationException("DB_SECRET_ARN environment variable is not set.");
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.");

        using var client = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
        var response = client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn })
            .GetAwaiter().GetResult();

        return BuildConnectionString(response.SecretString);
    }

    // rds.Credentials.fromGeneratedSecret produces this exact JSON shape:
    // {"username":"...","password":"...","host":"...","port":5432,"dbname":"..."}
    internal static string BuildConnectionString(string secretJson)
    {
        using var document = JsonDocument.Parse(secretJson);
        var root = document.RootElement;

        var host = root.GetProperty("host").GetString();
        var port = root.GetProperty("port").GetInt32();
        var dbname = root.GetProperty("dbname").GetString();
        var username = root.GetProperty("username").GetString();
        var password = root.GetProperty("password").GetString();

        return $"Host={host};Port={port};Database={dbname};Username={username};Password={password};" +
            "SSL Mode=Require;Trust Server Certificate=true";
    }
}
