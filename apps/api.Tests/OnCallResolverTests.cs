using SentinelOps.Api.Domain;
using SentinelOps.Api.Schedules;

namespace SentinelOps.Api.Tests;

public class OnCallResolverTests
{
    private static readonly Guid PrimaryUserId = Guid.NewGuid();
    private static readonly Guid BackupUserId = Guid.NewGuid();
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
    public void Resolve_OvernightShiftWrapsPastMidnight_SameCalendarDay()
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
    public void Resolve_OvernightShiftWrapsPastMidnight_NextCalendarDay()
    {
        // A Monday 22:00 -> 06:00 rotation must still cover Tuesday 03:00 —
        // the boundary the naive "DayOfWeek == today" check gets wrong,
        // since the rotation is stored under Monday but "today" here is
        // Tuesday.
        var now = new DateTimeOffset(2024, 1, 2, 3, 0, 0, TimeSpan.Zero);
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
    public void Resolve_OvernightShiftEndsAtBoundary_NoLongerActive()
    {
        // One minute past the 06:00 end of a Monday overnight rotation, on
        // the Tuesday side of the boundary, must return null rather than
        // still matching.
        var now = new DateTimeOffset(2024, 1, 2, 6, 1, 0, TimeSpan.Zero);
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

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NoPrimaryButBackupCoversWindow_ReturnsBackupResponder()
    {
        var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = BackupUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0), IsBackup = true,
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.Equal(BackupUserId, result);
    }

    [Fact]
    public void Resolve_PrimaryAndBackupBothCoverWindow_PrefersPrimary()
    {
        var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = BackupUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0), IsBackup = true,
            },
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0), IsBackup = false,
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.Equal(PrimaryUserId, result);
    }

    [Fact]
    public void Resolve_OverlappingPrimaryRotations_ReturnsOneOfThemNotNull()
    {
        // Two primary rotations covering the same window is a data-entry
        // mistake the API doesn't reject outright, but resolution must still
        // be deterministic — pick one of the overlapping responders rather
        // than returning null or throwing.
        var now = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var otherUserId = Guid.NewGuid();
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Primary", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = otherUserId,
                DayOfWeek = (int)DayOfWeek.Monday, StartTimeLocal = new TimeOnly(8, 0), EndTimeLocal = new TimeOnly(12, 0),
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.True(result == PrimaryUserId || result == otherUserId);
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
        // Represents a vacation/absence: an override with a null responder
        // wins over the base rotation, leaving the schedule intentionally
        // uncovered rather than falling back to the rotation.
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

    [Fact]
    public void Resolve_WeekendRotation_MatchesSaturday()
    {
        var now = new DateTimeOffset(2024, 1, 6, 10, 0, 0, TimeSpan.Zero); // Saturday.
        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "Weekend", TimeZoneId = "UTC" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)DayOfWeek.Saturday, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], now);

        Assert.Equal(PrimaryUserId, result);
    }

    [Theory]
    [InlineData(2024, 1, 8)] // Standard time (EST, UTC-5).
    [InlineData(2024, 7, 8)] // Daylight time (EDT, UTC-4).
    public void Resolve_NonUtcTimeZone_UsesCorrectOffsetAcrossDst(int year, int month, int day)
    {
        // Same rotation (09:00-17:00 America/New_York, whatever weekday this
        // date falls on), evaluated once in EST and once in EDT. If the
        // resolver used a fixed UTC offset instead of a real time zone
        // conversion, one of these two would resolve to the wrong side of
        // the window.
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var localNoon = new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified);
        var nowUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localNoon, timeZone));

        var schedule = new Schedule { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Name = "NY", TimeZoneId = "America/New_York" };
        var rotations = new List<ScheduleRotation>
        {
            new()
            {
                Id = Guid.NewGuid(), ScheduleId = schedule.Id, ResponderUserId = PrimaryUserId,
                DayOfWeek = (int)localNoon.DayOfWeek, StartTimeLocal = new TimeOnly(9, 0), EndTimeLocal = new TimeOnly(17, 0),
            },
        };

        var result = OnCallResolver.Resolve(schedule, rotations, [], nowUtc);

        Assert.Equal(PrimaryUserId, result);
    }
}
