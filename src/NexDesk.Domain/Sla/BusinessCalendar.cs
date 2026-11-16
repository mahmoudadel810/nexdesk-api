namespace NexDesk.Domain.Sla;

/// <summary>
/// Counts time the way a support team works: only inside opening hours, in the workspace's own time zone,
/// skipping holidays. All SLA deadlines are measured with it.
/// </summary>
public sealed class BusinessCalendar
{
    /// <summary>Stops a calendar with no future working time from being searched forever.</summary>
    private const int MaxDaysAhead = 3660;

    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End)> _hours;
    private readonly HashSet<DateOnly> _holidays;

    public BusinessCalendar(
        TimeZoneInfo zone,
        IEnumerable<BusinessHoursDay> week,
        IEnumerable<DateOnly>? holidays = null)
    {
        _zone = zone;
        _hours = week
            .Where(day => day.IsWorkingDay && day.EndTime > day.StartTime)
            .ToDictionary(day => day.Day, day => (day.StartTime, day.EndTime));
        _holidays = [.. holidays ?? []];
    }

    public bool HasWorkingTime => _hours.Count > 0;

    /// <summary>Returns the moment that lies the given number of working minutes after <paramref name="start"/>.</summary>
    public DateTimeOffset AddBusinessMinutes(DateTimeOffset start, int minutes)
    {
        if (!HasWorkingTime)
        {
            return start.AddMinutes(Math.Max(0, minutes));
        }

        var remaining = TimeSpan.FromMinutes(Math.Max(0, minutes));
        var local = TimeZoneInfo.ConvertTime(start, _zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var cursor = local.DateTime;

        for (var day = 0; day < MaxDaysAhead; day++, date = date.AddDays(1))
        {
            if (Window(date) is not var (open, close))
            {
                continue;
            }

            var from = cursor > open ? cursor : open;
            if (from >= close)
            {
                continue;
            }

            var available = close - from;
            if (remaining <= available)
            {
                return ToInstant(from + remaining);
            }

            remaining -= available;
        }

        return ToInstant(cursor) + remaining;
    }

    /// <summary>Counts the working minutes between two moments. Zero when <paramref name="end"/> is not later.</summary>
    public int BusinessMinutesBetween(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return 0;
        }

        if (!HasWorkingTime)
        {
            return (int)(end - start).TotalMinutes;
        }

        var from = TimeZoneInfo.ConvertTime(start, _zone).DateTime;
        var to = TimeZoneInfo.ConvertTime(end, _zone).DateTime;
        var total = TimeSpan.Zero;
        for (var date = DateOnly.FromDateTime(from); date <= DateOnly.FromDateTime(to); date = date.AddDays(1))
        {
            if (Window(date) is not var (open, close))
            {
                continue;
            }

            var windowStart = from > open ? from : open;
            var windowEnd = to < close ? to : close;
            if (windowEnd > windowStart)
            {
                total += windowEnd - windowStart;
            }
        }

        return (int)total.TotalMinutes;
    }

    private (DateTime Open, DateTime Close)? Window(DateOnly date) =>
        !_holidays.Contains(date) && _hours.TryGetValue(date.DayOfWeek, out var hours)
            ? (date.ToDateTime(hours.Start), date.ToDateTime(hours.End))
            : null;

    private DateTimeOffset ToInstant(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A wall-clock time skipped by a daylight-saving jump does not exist; move past the gap.
        while (_zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }

        return new DateTimeOffset(unspecified, _zone.GetUtcOffset(unspecified));
    }
}
