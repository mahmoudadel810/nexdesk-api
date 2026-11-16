using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Domain.Sla;

/// <summary>Response and resolution targets for one priority, counted in working minutes.</summary>
public sealed class SlaPolicy : AuditableEntity, ITenantOwned
{
    public const int MaxMinutes = 60 * 24 * 90;

    private SlaPolicy()
    {
    }

    public Guid TenantId { get; private set; }

    public TicketPriority Priority { get; private set; }

    public int FirstResponseMinutes { get; private set; }

    public int ResolutionMinutes { get; private set; }

    /// <summary>One policy per priority, with targets a small team can realistically keep.</summary>
    public static IReadOnlyList<SlaPolicy> Defaults() =>
    [
        Create(TicketPriority.Urgent, 30, 4 * 60),
        Create(TicketPriority.High, 60, 8 * 60),
        Create(TicketPriority.Normal, 4 * 60, 24 * 60),
        Create(TicketPriority.Low, 8 * 60, 48 * 60),
    ];

    public static SlaPolicy Create(TicketPriority priority, int firstResponseMinutes, int resolutionMinutes)
    {
        var policy = new SlaPolicy { Priority = priority };
        policy.SetTargets(firstResponseMinutes, resolutionMinutes);
        return policy;
    }

    public void SetTargets(int firstResponseMinutes, int resolutionMinutes)
    {
        if (firstResponseMinutes is < 1 or > MaxMinutes || resolutionMinutes is < 1 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(firstResponseMinutes), "SLA targets must be between 1 minute and 90 days.");
        }

        if (resolutionMinutes < firstResponseMinutes)
        {
            throw new ArgumentException("The resolution target cannot be shorter than the first-response target.", nameof(resolutionMinutes));
        }

        FirstResponseMinutes = firstResponseMinutes;
        ResolutionMinutes = resolutionMinutes;
    }
}

/// <summary>A day the workspace is closed, in its own time zone.</summary>
public sealed class Holiday : Entity, ITenantOwned
{
    public const int NameMaxLength = 80;

    private Holiday()
    {
    }

    public Guid TenantId { get; private set; }

    public DateOnly Date { get; private set; }

    public string Name { get; private set; } = null!;

    public static Holiday Create(DateOnly date, string name) => new() { Date = date, Name = name.Trim() };
}

public enum SlaMetric
{
    FirstResponse,
    Resolution,
}

/// <summary>A deadline that was missed. Kept for reporting even after the ticket is solved.</summary>
public sealed class SlaBreach : Entity, ITenantOwned
{
    private SlaBreach()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public SlaMetric Metric { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public DateTimeOffset BreachedAt { get; private set; }

    public static SlaBreach Create(Guid ticketId, SlaMetric metric, DateTimeOffset dueAt, DateTimeOffset now) => new()
    {
        TicketId = ticketId,
        Metric = metric,
        DueAt = dueAt,
        BreachedAt = now,
    };
}
