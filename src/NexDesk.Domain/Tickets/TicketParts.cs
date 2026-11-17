using NexDesk.Domain.Common;

namespace NexDesk.Domain.Tickets;

public sealed class TicketMessage : Entity, ITenantOwned
{
    public const int BodyMaxLength = 20_000;

    private TicketMessage()
    {
    }

    internal TicketMessage(Guid ticketId, Actor author, string body, bool isInternal, DateTimeOffset now)
    {
        TicketId = ticketId;
        AuthorType = author.Type;
        AuthorId = author.Id;
        Body = body.Trim();
        IsInternal = isInternal;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public ActorType AuthorType { get; private set; }

    public Guid? AuthorId { get; private set; }

    public string Body { get; private set; } = null!;

    public bool IsInternal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal void MoveTo(Guid ticketId) => TicketId = ticketId;
}

public enum TicketEventType
{
    Created,
    SubjectChanged,
    StatusChanged,
    PriorityChanged,
    TypeChanged,
    AssigneeChanged,
    TeamChanged,
    TagsChanged,
    CustomFieldChanged,
    MergedInto,
    MergedFrom,
    SlaBreached,
    CsatSubmitted,
}

/// <summary>One entry of the activity timeline. Values are stored as display text so history survives renames.</summary>
public sealed class TicketEvent : Entity, ITenantOwned
{
    private TicketEvent()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public TicketEventType Type { get; private set; }

    public ActorType ActorType { get; private set; }

    public Guid? ActorId { get; private set; }

    /// <summary>Label of the custom field a change refers to.</summary>
    public string? Field { get; private set; }

    public string? OldValue { get; private set; }

    public string? NewValue { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static TicketEvent Create(
        Guid ticketId,
        TicketEventType type,
        Actor actor,
        string? oldValue,
        string? newValue,
        DateTimeOffset now,
        string? field = null) => new()
        {
            Field = field,
            TicketId = ticketId,
            Type = type,
            ActorType = actor.Type,
            ActorId = actor.Id,
            OldValue = oldValue,
            NewValue = newValue,
            CreatedAt = now,
        };
}

public sealed class TicketWatcher : ITenantOwned
{
    internal TicketWatcher(Guid ticketId, Guid userId)
    {
        TicketId = ticketId;
        UserId = userId;
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public Guid UserId { get; private set; }
}

/// <summary>How satisfied the customer was with the way a ticket was handled, from 1 (bad) to 5 (great).</summary>
public sealed class CsatResponse : Entity, ITenantOwned
{
    public const int MinRating = 1;
    public const int MaxRating = 5;
    public const int CommentMaxLength = 1000;

    private CsatResponse()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid TicketId { get; private set; }

    public Guid CustomerId { get; private set; }

    public int Rating { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static CsatResponse Create(Guid ticketId, Guid customerId, int rating, string? comment, DateTimeOffset now)
    {
        var response = new CsatResponse { TicketId = ticketId, CustomerId = customerId };
        response.Update(rating, comment, now);
        return response;
    }

    internal void Update(int rating, string? comment, DateTimeOffset now)
    {
        if (rating is < MinRating or > MaxRating)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "A rating is a whole number from 1 to 5.");
        }

        Rating = rating;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        CreatedAt = now;
    }
}

/// <summary>An uploaded file. It starts unattached and is linked to a message when that message is posted.</summary>
public sealed class Attachment : Entity, ITenantOwned
{
    public const long MaxSizeBytes = 10 * 1024 * 1024;
    public const int FileNameMaxLength = 200;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".scr", ".ps1", ".vbs", ".jar", ".sh", ".app",
    };

    private Attachment()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid? TicketId { get; private set; }

    public Guid? MessageId { get; private set; }

    public string FileName { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    public long SizeBytes { get; private set; }

    public string StorageKey { get; private set; } = null!;

    public ActorType UploaderType { get; private set; }

    public Guid? UploaderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static bool IsAllowedFileName(string fileName) =>
        !BlockedExtensions.Contains(Path.GetExtension(fileName));

    public static Attachment Create(
        Guid tenantId,
        string fileName,
        string contentType,
        long sizeBytes,
        Actor uploader,
        DateTimeOffset now)
    {
        var attachment = new Attachment
        {
            TenantId = tenantId,
            FileName = Path.GetFileName(fileName).Trim(),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = sizeBytes,
            UploaderType = uploader.Type,
            UploaderId = uploader.Id,
            CreatedAt = now,
        };
        attachment.StorageKey = $"{tenantId:N}/{now:yyyy}/{now:MM}/{attachment.Id:N}";
        return attachment;
    }

    public bool IsUnattachedAndOwnedBy(Actor actor) =>
        MessageId is null && UploaderType == actor.Type && UploaderId == actor.Id;

    public void AttachTo(TicketMessage message)
    {
        TicketId = message.TicketId;
        MessageId = message.Id;
    }

    internal void MoveTo(Guid ticketId) => TicketId = ticketId;
}
