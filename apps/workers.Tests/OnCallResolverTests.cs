using SentinelOps.Api.Domain;
using SentinelOps.Workers.ResponderAssignment;

namespace SentinelOps.Workers.Tests;

public class OnCallResolverTests
{
    private static readonly Guid PrimaryUserId = Guid.NewGuid();
    private static readonly Guid OverrideUserId = Guid.NewGuid();

    [Fact]
    public void Resolve_MatchingRotationWindow_ReturnsResponder()
    {
        // 2024-01-01 is a Monday, 10:00 UTC — inside a Monday 09:00-17:00 rotation.
        var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.Equal(PrimaryUserId, result);
    }

    [Fact]
    public void Resolve_OutsideRotationWindow_ReturnsNull()
    {
        var now = new DateTimeOffset(2024, 1, 1, 20, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_OvernightShiftWrapsPastMidnight()
    {
        // Monday 23:00 falls inside a 22:00 -> 06:00 overnight rotation.
        var now = new DateTimeOffset(2024, 1, 1, 23, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Overnight", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(22, 0), EndTimeLocal = new TimeOnly(6, 0),
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.Equal(PrimaryUserId, result);
    }

    [Fact]
    public void Resolve_ActiveOverride_TakesPrecedenceOverRotation()
    {
        var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
        };
        var overrides = new List<ScheduleOverride>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = OverrideUserId,
                StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(1), CreatedByUserId = Guid.NewGuid(), CreatedAtUtc = now,
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, overrides, now);

        Assert.Equal(OverrideUserId, result);
    }

    [Fact]
    public void Resolve_ActiveGapOverride_ReturnsNullEvenWithMatchingRotation()
    {
        var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
        };
        var overrides = new List<ScheduleOverride>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = null,
                StartsAtUtc = now.AddHours(-1), EndsAtUtc = now.AddHours(1), CreatedByUserId = Guid.NewGuid(), CreatedAtUtc = now,
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, overrides, now);

        Assert.Null(result);
    }
}
