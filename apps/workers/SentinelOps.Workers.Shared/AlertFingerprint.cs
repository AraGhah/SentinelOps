using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelOps.Workers.Shared;

// Computes the identity a duplicate-detection decision is made on: two alerts
// with the same fingerprint are considered the same underlying problem, even
// if they arrived from different sources or with slightly different wording.
public static partial class AlertFingerprint
{
    // Checked in this order since different integrations spell it differently;
    // first match wins.
    private static readonly string[] ErrorCodeKeys = ["errorCode", "error_code", "code", "errorcode"];

    public static string Normalize(string value) =>
        CollapseWhitespace().Replace(value.Trim().ToLowerInvariant(), " ");

    // Best-effort: Alert.Metadata is arbitrary source-defined JSON (see
    // Alert.cs), so this returns null rather than throwing when it isn't an
    // object, isn't valid JSON, or has none of the known error-code keys.
    public static string? ExtractErrorCode(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var key in ErrorCodeKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var prop)) continue;

                var raw = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
                if (!string.IsNullOrWhiteSpace(raw)) return Normalize(raw).ToUpperInvariant();
            }
        }
        catch (JsonException)
        {
            // Not JSON, or malformed — treated as "no error code" rather than
            // failing the alert.
        }

        return null;
    }

    // Order matches the checklist: title, error code, service, environment.
    // OrganizationId is folded in too so the hash alone is enough to isolate
    // tenants even if a caller forgets to scope the DynamoDB key by org.
    public static string Compute(Guid organizationId, string title, string? errorCode, Guid? serviceId, string environment)
    {
        var joined = string.Join('|',
            organizationId.ToString("N"),
            Normalize(title),
            errorCode ?? string.Empty,
            serviceId?.ToString("N") ?? string.Empty,
            Normalize(environment));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(hash);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
