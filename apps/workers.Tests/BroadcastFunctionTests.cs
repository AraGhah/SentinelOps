using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using SentinelOps.Events;
using SentinelOps.Workers.Dashboard;

namespace SentinelOps.Workers.Tests;

public class BroadcastFunctionTests
{
    private static SQSEvent.SQSMessage IncidentCreatedMessage(Guid orgId, Guid incidentId) =>
        SqsEventFactory.Wrap(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated,
            new IncidentCreatedDetail(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incidentId, Guid.NewGuid(), null, "Title", Severity.High));

    [Fact]
    public async Task Handle_PostsToEveryConnectionForTheEventsOrganization_ButNotOtherOrgs()
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var connectionStore = new FakeConnectionStore();
        await connectionStore.AddAsync("conn-a", orgId, TimeSpan.FromHours(1), CancellationToken.None);
        await connectionStore.AddAsync("conn-b", orgId, TimeSpan.FromHours(1), CancellationToken.None);
        await connectionStore.AddAsync("conn-other-org", otherOrgId, TimeSpan.FromHours(1), CancellationToken.None);

        var broadcaster = new FakeConnectionBroadcaster();
        var function = new BroadcastFunction(connectionStore, broadcaster);

        var message = IncidentCreatedMessage(orgId, Guid.NewGuid());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Equal(2, broadcaster.Posted.Count);
        Assert.Contains(broadcaster.Posted, p => p.ConnectionId == "conn-a");
        Assert.Contains(broadcaster.Posted, p => p.ConnectionId == "conn-b");
        Assert.DoesNotContain(broadcaster.Posted, p => p.ConnectionId == "conn-other-org");

        var payload = JsonSerializer.Deserialize<JsonElement>(broadcaster.Posted.First().Payload);
        Assert.Equal(EventTypes.IncidentCreated, payload.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Handle_GoneConnection_IsRemovedFromStore()
    {
        var orgId = Guid.NewGuid();
        var connectionStore = new FakeConnectionStore();
        await connectionStore.AddAsync("conn-live", orgId, TimeSpan.FromHours(1), CancellationToken.None);
        await connectionStore.AddAsync("conn-gone", orgId, TimeSpan.FromHours(1), CancellationToken.None);

        var broadcaster = new FakeConnectionBroadcaster("conn-gone");
        var function = new BroadcastFunction(connectionStore, broadcaster);

        var message = IncidentCreatedMessage(orgId, Guid.NewGuid());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Single(connectionStore.Connections, c => c.Key == "conn-live");
    }

    [Fact]
    public async Task Handle_NoConnectionsForOrganization_DoesNothing()
    {
        var connectionStore = new FakeConnectionStore();
        var broadcaster = new FakeConnectionBroadcaster();
        var function = new BroadcastFunction(connectionStore, broadcaster);

        var message = IncidentCreatedMessage(Guid.NewGuid(), Guid.NewGuid());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(broadcaster.Posted);
    }
}
