using System.Text.Json;

namespace SentinelOps.Api.Common;

// Emits CloudWatch Embedded Metric Format (EMF) log lines directly to
// stdout. ECS ships container stdout to ApiLogGroup via the awslogs log
// driver (see ApiStack), and CloudWatch parses any EMF-shaped log line in
// that group into a real custom metric automatically — no CloudWatch agent,
// no extra IAM permission, and no PutMetricData call blocking the request
// path. Deliberately bypasses ILogger (whose JSON console formatter would
// re-escape this into a "message" string field, breaking EMF's requirement
// that the raw log line itself be the metric JSON object).
public static class MetricsEmitter
{
    public const string Namespace = "SentinelOps/Api";

    public static void Emit(
        string metricName, double value, string unit = "Count",
        IReadOnlyDictionary<string, string>? dimensions = null)
    {
        var dims = dimensions ?? new Dictionary<string, string>();
        var payload = new Dictionary<string, object?>
        {
            ["_aws"] = new
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = new[]
                {
                    new
                    {
                        Namespace,
                        Dimensions = new[] { dims.Keys.ToArray() },
                        Metrics = new[] { new { Name = metricName, Unit = unit } },
                    },
                },
            },
            [metricName] = value,
        };
        foreach (var (key, val) in dims)
        {
            payload[key] = val;
        }

        Console.WriteLine(JsonSerializer.Serialize(payload));
    }
}
