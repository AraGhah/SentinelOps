using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Schedules;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class SchedulesTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task OnCall_PrefersActiveOverride_OverBaseRotation()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "OnCall Override Org");
        var schedule = await CreateScheduleAsync(client, org.Id);

        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Utc);
        var baseResponder = await InviteMemberAsync(client, org.Id);
        var overrideResponder = await InviteMemberAsync(client, org.Id);

        // A rotation covering the whole current day, plus an override covering right now;
        // the override must win.
        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/rotations",
            new CreateRotationRequest(
                baseResponder, (int)localNow.DayOfWeek, new TimeOnly(0, 0), new TimeOnly(23, 59), null, null, false));

        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/overrides",
            new CreateOverrideRequest(overrideResponder, baseResponder, nowUtc.AddMinutes(-5), nowUtc.AddMinutes(5), "covering"));

        var onCall = await client.GetFromJsonAsync<OnCallResponse>(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/on-call", Json.Options);

        Assert.Equal(overrideResponder, onCall!.ResponderUserId);
        Assert.Equal("override", onCall.Source);
    }

    [Fact]
    public async Task OnCall_MatchesOvernightRotation()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Overnight Org");
        var schedule = await CreateScheduleAsync(client, org.Id);

        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Utc);
        var responder = await InviteMemberAsync(client, org.Id);

        // End < Start makes this an overnight-shaped window (per the checked-in
        // IsActiveAt logic); placing both a few minutes in the recent past keeps
        // "now" inside it without depending on wall-clock time of day.
        var nowTime = TimeOnly.FromDateTime(localNow.DateTime);
        var start = nowTime.AddMinutes(-5);
        var end = nowTime.AddMinutes(-10);

        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/rotations",
            new CreateRotationRequest(responder, (int)localNow.DayOfWeek, start, end, null, null, false));

        var onCall = await client.GetFromJsonAsync<OnCallResponse>(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/on-call", Json.Options);

        Assert.Equal(responder, onCall!.ResponderUserId);
        Assert.Equal("rotation", onCall.Source);
    }

    [Fact]
    public async Task OnCall_BackupRotationCovers_WhenNoPrimaryIsActive()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Backup Org");
        var schedule = await CreateScheduleAsync(client, org.Id);

        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Utc);
        var backupResponder = await InviteMemberAsync(client, org.Id);

        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/rotations",
            new CreateRotationRequest(
                backupResponder, (int)localNow.DayOfWeek, new TimeOnly(0, 0), new TimeOnly(23, 59), null, null, true));

        var onCall = await client.GetFromJsonAsync<OnCallResponse>(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/on-call", Json.Options);

        Assert.Equal(backupResponder, onCall!.ResponderUserId);
        Assert.Equal("rotation", onCall.Source);
    }

    [Fact]
    public async Task OnCall_OverlappingRotations_ReturnsADeterministicResponder()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Overlap Org");
        var schedule = await CreateScheduleAsync(client, org.Id);

        var nowUtc = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Utc);
        var responderA = await InviteMemberAsync(client, org.Id);
        var responderB = await InviteMemberAsync(client, org.Id);

        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/rotations",
            new CreateRotationRequest(responderA, (int)localNow.DayOfWeek, new TimeOnly(0, 0), new TimeOnly(23, 59), null, null, false));
        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/rotations",
            new CreateRotationRequest(responderB, (int)localNow.DayOfWeek, new TimeOnly(0, 0), new TimeOnly(23, 59), null, null, false));

        var onCall = await client.GetFromJsonAsync<OnCallResponse>(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/on-call", Json.Options);

        Assert.True(onCall!.ResponderUserId == responderA || onCall.ResponderUserId == responderB);
        Assert.Equal("rotation", onCall.Source);
    }

    [Fact]
    public async Task Create_RejectsUnknownTimeZoneId()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Bad TimeZone Org");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules", new CreateScheduleRequest("Primary On-Call", null, "Not/AZone"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unknown time zone id.", body);
    }

    [Fact]
    public async Task AddRotation_RejectsResponderWhoIsNotAnOrgMember()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Non Member Rotation Org");
        var schedule = await CreateScheduleAsync(client, org.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/rotations",
            new CreateRotationRequest(Guid.NewGuid(), 1, new TimeOnly(0, 0), new TimeOnly(23, 59), null, null, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddOverride_RejectsResponderWhoIsNotAnOrgMember()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Non Member Override Org");
        var schedule = await CreateScheduleAsync(client, org.Id);
        var nowUtc = DateTimeOffset.UtcNow;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/schedules/{schedule.Id}/overrides",
            new CreateOverrideRequest(Guid.NewGuid(), null, nowUtc.AddMinutes(-5), nowUtc.AddMinutes(5), "covering"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<ScheduleResponse> CreateScheduleAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/schedules", new CreateScheduleRequest("Primary On-Call", null, "UTC"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScheduleResponse>(Json.Options))!;
    }

    private async Task<Guid> InviteMemberAsync(HttpClient ownerClient, Guid orgId)
    {
        var sub = TestClientFactory.NewSub();
        return await InviteAndAcceptUserIdAsync(fixture, ownerClient, orgId, sub, $"{sub}@test.local", OrganizationRole.Responder);
    }
}
