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
