using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Organizations;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class IncidentTimelineTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Timeline_RecordsCreationStatusChangesAndComments_InChronologicalOrder()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Timeline Org");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Timeline incident", null, IncidentSeverity.High, null, null));
        createResponse.EnsureSuccessStatusCode();
        var incident = (await createResponse.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;

        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/comments",
            new CreateCommentRequest("Investigating now.", true));

        await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Acknowledged, null));

        await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Resolved, "Fixed."));

        var timeline = await client.GetFromJsonAsync<List<IncidentEventResponse>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/timeline", Json.Options);

        Assert.NotNull(timeline);
        var types = timeline!.Select(e => e.EventType).ToList();
        Assert.Equal(
            [
                IncidentEventType.Created,
                IncidentEventType.CommentAdded,
                IncidentEventType.Acknowledged,
                IncidentEventType.Resolved,
            ],
            types);

        // Chronological, not insertion order.
        Assert.True(timeline!.SequenceEqual(timeline.OrderBy(e => e.OccurredAtUtc)));

        // Every entry here was a direct API call by `owner`, never an automated action.
        Assert.All(timeline, e => Assert.NotNull(e.ActorUserId));
    }

    [Fact]
    public async Task Timeline_RecordsAssignmentOnUpdate()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Timeline Assign Org");

        var responderSub = TestClientFactory.NewSub();
        var responderMembershipId = await InviteAndAcceptAsync(
            fixture, client, org.Id, responderSub, "responder@timeline.test", OrganizationRole.Responder);
        var members = await client.GetFromJsonAsync<List<MemberResponse>>($"/api/v1/organizations/{org.Id}/members", Json.Options);
        var responderId = members!.Single(m => m.MembershipId == responderMembershipId).UserId;

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Needs an owner", null, IncidentSeverity.Low, null, null));
        var incident = (await createResponse.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;

        await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}",
            new UpdateIncidentRequest("Needs an owner", null, IncidentSeverity.Low, null, responderId));

        var timeline = await client.GetFromJsonAsync<List<IncidentEventResponse>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/timeline", Json.Options);

        Assert.Contains(timeline!, e => e.EventType == IncidentEventType.Assigned);
    }
}
