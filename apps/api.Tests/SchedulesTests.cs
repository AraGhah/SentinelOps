using System.Net.Http.Json;
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
        var baseResponder = Guid.NewGuid();
        var overrideResponder = Guid.NewGuid();

        // A rotation covering the whole current day, plus an override covering
        // right now — the override must win.
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
        var responder = Guid.NewGuid();

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

    private static async Task<ScheduleResponse> CreateScheduleAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/schedules", new CreateScheduleRequest("Primary On-Call", null, "UTC"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScheduleResponse>(Json.Options))!;
    }
}
