using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Organizations;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class TenantIsolationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task CreateOrganization_MakesCreatorOwner()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);

        var createResponse = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest("Acme Security"));
        createResponse.EnsureSuccessStatusCode();
        var org = await createResponse.Content.ReadFromJsonAsync<OrganizationResponse>(Json.Options);

        var mine = await client.GetFromJsonAsync<List<MyOrganizationResponse>>("/api/v1/organizations", Json.Options);

        Assert.Contains(mine!, o => o.Id == org!.Id && o.Role == OrganizationRole.Owner);
    }

    [Fact]
    public async Task Invite_Then_Accept_CreatesActiveMembershipWithInvitedRole()
    {
        var owner = TestClientFactory.NewSub();
        var invitee = TestClientFactory.NewSub();
        var inviteeEmail = $"{invitee}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Invite Test Org");

        var inviteResponse = await ownerClient.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/invitations",
            new CreateInvitationRequest(inviteeEmail, OrganizationRole.Responder));
        inviteResponse.EnsureSuccessStatusCode();
        var invitation = await inviteResponse.Content.ReadFromJsonAsync<InvitationResponse>(Json.Options);

        var inviteeClient = fixture.Factory.CreateClientFor(invitee, inviteeEmail);
        var acceptResponse = await inviteeClient.PostAsJsonAsync(
            "/api/v1/invitations/accept", new AcceptInvitationRequest(invitation!.Token));
        acceptResponse.EnsureSuccessStatusCode();

        var members = await ownerClient.GetFromJsonAsync<List<MemberResponse>>(
            $"/api/v1/organizations/{org.Id}/members", Json.Options);

        Assert.Contains(members!, m => m.Email == inviteeEmail && m.Role == OrganizationRole.Responder && m.IsActive);
    }

    [Fact]
    public async Task Viewer_CannotChangeMemberRoles()
    {
        var owner = TestClientFactory.NewSub();
        var viewer = TestClientFactory.NewSub();
        var viewerEmail = $"{viewer}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Viewer Perms Org");
        var viewerMembershipId = await InviteAndAcceptAsync(fixture, ownerClient, org.Id, viewer, viewerEmail, OrganizationRole.Viewer);

        var viewerClient = fixture.Factory.CreateClientFor(viewer, viewerEmail);
        var response = await viewerClient.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/members/{viewerMembershipId}/role",
            new UpdateMemberRoleRequest(OrganizationRole.Administrator));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_CannotAccessAnotherOrganizationsData()
    {
        var ownerA = TestClientFactory.NewSub();
        var ownerB = TestClientFactory.NewSub();

        var clientA = fixture.Factory.CreateClientFor(ownerA);
        var clientB = fixture.Factory.CreateClientFor(ownerB);

        var orgA = await CreateOrganizationAsync(clientA, "Org A");
        var orgB = await CreateOrganizationAsync(clientB, "Org B");

        var membersResponse = await clientA.GetAsync($"/api/v1/organizations/{orgB.Id}/members");
        Assert.Equal(HttpStatusCode.Forbidden, membersResponse.StatusCode);

        var settingsResponse = await clientA.GetAsync($"/api/v1/organizations/{orgB.Id}/settings");
        Assert.Equal(HttpStatusCode.Forbidden, settingsResponse.StatusCode);

        var orgResponse = await clientA.GetAsync($"/api/v1/organizations/{orgB.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, orgResponse.StatusCode);
    }

    [Fact]
    public async Task DeactivatedMember_LosesAccessImmediately()
    {
        var owner = TestClientFactory.NewSub();
        var member = TestClientFactory.NewSub();
        var memberEmail = $"{member}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Deactivation Org");
        var membershipId = await InviteAndAcceptAsync(fixture, ownerClient, org.Id, member, memberEmail, OrganizationRole.Administrator);

        var memberClient = fixture.Factory.CreateClientFor(member, memberEmail);
        var beforeDeactivate = await memberClient.GetAsync($"/api/v1/organizations/{org.Id}/members");
        Assert.Equal(HttpStatusCode.OK, beforeDeactivate.StatusCode);

        var deactivateResponse = await ownerClient.PostAsync($"/api/v1/organizations/{org.Id}/members/{membershipId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var afterDeactivate = await memberClient.GetAsync($"/api/v1/organizations/{org.Id}/members");
        Assert.Equal(HttpStatusCode.Forbidden, afterDeactivate.StatusCode);
    }

    [Fact]
    public async Task CannotDeactivateTheLastRemainingOwner()
    {
        var owner = TestClientFactory.NewSub();
        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Sole Owner Org");

        var members = await ownerClient.GetFromJsonAsync<List<MemberResponse>>(
            $"/api/v1/organizations/{org.Id}/members", Json.Options);
        var ownerMembershipId = members!.Single().MembershipId;

        var response = await ownerClient.PostAsync($"/api/v1/organizations/{org.Id}/members/{ownerMembershipId}/deactivate", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new CreateOrganizationRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>(Json.Options))!;
    }

    private static async Task<Guid> InviteAndAcceptAsync(
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
