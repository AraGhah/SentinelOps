using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentinelOps.Workers.Shared;

// Computes the identity duplicate-detection is based on: two alerts with the
// same fingerprint are treated as the same underlying problem.
public static partial class AlertFingerprint
{
    // Checked in this order since different integrations spell it differently;
    // first match wins.
    private static readonly string[] ErrorCodeKeys = ["errorCode", "error_code", "code", "errorcode"];

    public static string Normalize(string value) =>
        CollapseWhitespace().Replace(value.Trim().ToLowerInvariant(), " ");

    // Best-effort: Alert.Metadata is arbitrary source-defined JSON, so this
    // returns null rather than throwing on non-object/invalid/missing keys.
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
            // Malformed JSON treated as "no error code" rather than failing the alert.
        }

        return null;
    }

    // OrganizationId is folded into the hash so it alone isolates tenants
    // even if a caller forgets to scope the DynamoDB key by org.
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
