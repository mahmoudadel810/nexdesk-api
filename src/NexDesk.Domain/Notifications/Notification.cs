using NexDesk.Domain.Common;

namespace NexDesk.Domain.Notifications;

public enum NotificationType
{
    TicketAssigned,
    TicketReply,
    TicketNote,
    SlaWarning,
    SlaBreach,

    /// <summary>Sent by an automation rule. The actor is the rule's name.</summary>
    Automation,

    /// <summary>A teammate wrote @Your Name in a reply or note.</summary>
    Mention,
}

/// <summary>Something a staff member should know about, shown in their notification centre.</summary>
public sealed class Notification : Entity, ITenantOwned
{
    public const int PreviewMaxLength = 160;

    private Notification()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    public Guid TicketId { get; private set; }

    public int TicketNumber { get; private set; }

    public string TicketSubject { get; private set; } = null!;

    /// <summary>Who caused it. Null when the system did, as for SLA alerts.</summary>
    public string? ActorName { get; private set; }

    /// <summary>The start of the message, or the name of the SLA clock for SLA alerts.</summary>
    public string? Preview { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification Create(
        Guid userId,
        NotificationType type,
        Guid ticketId,
        int ticketNumber,
        string ticketSubject,
        string? actorName,
        string? preview,
        DateTimeOffset now) => new()
        {
            UserId = userId,
            Type = type,
            TicketId = ticketId,
            TicketNumber = ticketNumber,
            TicketSubject = ticketSubject,
            ActorName = actorName,
            Preview = Shorten(preview),
            CreatedAt = now,
        };

    public static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= PreviewMaxLength ? flat : flat[..(PreviewMaxLength - 1)] + "…";
    }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}

/// <summary>
/// Whether a user wants one kind of notification in the app, by email and as a push to their phone. Missing rows
/// mean yes to all three.
/// </summary>
public sealed class NotificationPreference : ITenantOwned
{
    private NotificationPreference()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    public bool InApp { get; private set; }

    public bool Email { get; private set; }

    public bool Push { get; private set; } = true;

    public static NotificationPreference Create(Guid userId, NotificationType type, bool inApp, bool email, bool? push = null) => new()
    {
        UserId = userId,
        Type = type,
        InApp = inApp,
        Email = email,
        Push = push ?? true,
    };

    /// <summary>Changes the channels. A null <paramref name="push"/> keeps the stored choice, for clients that do not know it.</summary>
    public void Set(bool inApp, bool email, bool? push = null)
    {
        InApp = inApp;
        Email = email;
        Push = push ?? Push;
    }
}
