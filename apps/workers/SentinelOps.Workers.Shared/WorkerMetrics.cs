using System.Text.Json;

namespace SentinelOps.Workers.Shared;

// CloudWatch EMF, same approach as apps/api's MetricsEmitter: writes a JSON
// line to stdout, which Lambda ships to CloudWatch Logs and parses into a
// custom metric with no agent/PutMetricData call.
public static class WorkerMetrics
{
    public const string Namespace = "SentinelOps/Workers";

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
