using NexDesk.Domain.Common;

namespace NexDesk.Domain.Sla;

/// <summary>Opening hours of one weekday, in the tenant's time zone.</summary>
public sealed class BusinessHoursDay : Entity, ITenantOwned
{
    private BusinessHoursDay()
    {
    }

    public Guid TenantId { get; private set; }

    public DayOfWeek Day { get; private set; }

    public bool IsWorkingDay { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    /// <summary>Monday to Friday, 09:00 to 17:00.</summary>
    public static IReadOnlyList<BusinessHoursDay> DefaultWeek() =>
    [
        .. Enum.GetValues<DayOfWeek>().Select(day => new BusinessHoursDay
        {
            Day = day,
            IsWorkingDay = day is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
        }),
    ];

    public void Set(bool isWorkingDay, TimeOnly startTime, TimeOnly endTime)
    {
        if (isWorkingDay && endTime <= startTime)
        {
            throw new ArgumentException("A working day must end after it starts.", nameof(endTime));
        }

        IsWorkingDay = isWorkingDay;
        StartTime = startTime;
        EndTime = endTime;
    }
}
