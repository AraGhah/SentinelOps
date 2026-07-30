using Microsoft.AspNetCore.Mvc.Testing;

namespace SentinelOps.Api.Tests;

public static class TestClientFactory
{
    public static HttpClient CreateClientFor(this WebApplicationFactory<Program> factory, string sub, string? email = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, sub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email ?? $"{sub}@test.local");
        return client;
    }

    public static string NewSub() => $"sub-{Guid.NewGuid():N}";
}
