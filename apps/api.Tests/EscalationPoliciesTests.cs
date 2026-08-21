using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.EscalationPolicies;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class EscalationPoliciesTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Create_RejectsEmptyLevels()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Escalation Org");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies",
            new CreateEscalationPolicyRequest("Empty Policy", null, null, null, []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsDuplicateLevelOrder()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Duplicate Order Org");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies",
            new CreateEscalationPolicyRequest("Dup Policy", null, null, null,
                [new EscalationLevelRequest(1, 5, [Guid.NewGuid()]), new EscalationLevelRequest(1, 10, [Guid.NewGuid()])]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_HappyPath_ReturnsLevelsWithTargets()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Valid Escalation Org");
        var targetUser = await InviteMemberAsync(client, org.Id);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies",
            new CreateEscalationPolicyRequest("Primary Policy", "desc", null, null,
                [new EscalationLevelRequest(1, 15, [targetUser])]));
        createResponse.EnsureSuccessStatusCode();
        var policy = await createResponse.Content.ReadFromJsonAsync<EscalationPolicyResponse>(Json.Options);

        Assert.Single(policy!.Levels);
        Assert.Contains(targetUser, policy.Levels[0].TargetUserIds);
    }

    [Fact]
    public async Task RemoveLastLevel_IsRejected()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Last Level Org");
        var targetUser = await InviteMemberAsync(client, org.Id);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies",
            new CreateEscalationPolicyRequest("Sole Level Policy", null, null, null,
                [new EscalationLevelRequest(1, 15, [targetUser])]));
        createResponse.EnsureSuccessStatusCode();
        var policy = await createResponse.Content.ReadFromJsonAsync<EscalationPolicyResponse>(Json.Options);

        var deleteResponse = await client.DeleteAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies/{policy!.Id}/levels/{policy.Levels[0].Id}");

        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsTargetUserWhoIsNotAnOrgMember()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Non Member Target Org");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies",
            new CreateEscalationPolicyRequest("Policy", null, null, null,
                [new EscalationLevelRequest(1, 15, [Guid.NewGuid()])]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsFallbackAdministratorWhoIsNotAnOrgMember()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Non Member Fallback Org");
        var targetUser = await InviteMemberAsync(client, org.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/escalation-policies",
            new CreateEscalationPolicyRequest("Policy", null, null, Guid.NewGuid(),
                [new EscalationLevelRequest(1, 15, [targetUser])]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Guid> InviteMemberAsync(HttpClient ownerClient, Guid orgId)
    {
        var sub = TestClientFactory.NewSub();
        return await InviteAndAcceptUserIdAsync(fixture, ownerClient, orgId, sub, $"{sub}@test.local", OrganizationRole.Responder);
    }
}
