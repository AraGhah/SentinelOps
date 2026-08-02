using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Organizations;

namespace SentinelOps.Api.Tests;

// Shared setup used by every feature's test file: creating an organization and
// inviting/accepting a member at a given role, following the same two calls
// TenantIsolationTests already makes inline.
internal static class OrgTestHelpers
{
    public static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(Json.Options))!;
    }

    public static async Task<Guid> InviteAndAcceptAsync(
        ApiTestFixture fixture, HttpClient ownerClient, Guid orgId, string inviteeSub, string inviteeEmail, OrganizationRole role)
    {
        var inviteResponse = await ownerClient.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/invitations", new CreateInvitationRequest(inviteeEmail, role));
        inviteResponse.EnsureSuccessStatusCode();
        var invitation = await inviteResponse.Content.ReadFromJsonAsync<InvitationResponse>(Json.Options);

        var inviteeClient = fixture.Factory.CreateClientFor(inviteeSub, inviteeEmail);
        var acceptResponse = await inviteeClient.PostAsJsonAsync(
            "/api/v1/invitations/accept", new AcceptInvitationRequest(invitation!.Token));
        acceptResponse.EnsureSuccessStatusCode();

        var members = await ownerClient.GetFromJsonAsync<List<MemberResponse>>(
            $"/api/v1/organizations/{orgId}/members", Json.Options);
        return members!.Single(m => m.Email == inviteeEmail).MembershipId;
    }
}
