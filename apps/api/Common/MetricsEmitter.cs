using System.Text.Json;

namespace SentinelOps.Api.Common;

// Emits CloudWatch Embedded Metric Format (EMF) lines to stdout; CloudWatch parses
// EMF-shaped lines in ApiLogGroup into real metrics automatically, no PutMetricData
// call needed. Bypasses ILogger, whose JSON formatter would re-escape the line and
// break EMF's requirement that the raw log line be the metric JSON object.
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
