using System.Text.Json;

namespace SentinelOps.Api.Tests;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
