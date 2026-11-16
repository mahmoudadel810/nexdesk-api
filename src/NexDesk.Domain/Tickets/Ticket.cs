using NexDesk.Domain.Common;

namespace NexDesk.Domain.Tickets;

public enum TicketStatus
{
    New,
    Open,
    Pending,
    OnHold,
    Solved,
    Closed,
}

public enum TicketPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum TicketType
{
    Question,
    Incident,
    Problem,
    Task,
}

public enum TicketChannel
{
    Agent,
    Portal,
    Email,
    Api,
}

public sealed class Ticket : AuditableEntity, ITenantOwned, ISoftDeletable
{
    public const int SubjectMaxLength = 200;
    public const int MaxTags = 20;
    public const int TagMaxLength = 30;

    private readonly List<TicketMessage> _messages = [];
    private readonly List<TicketEvent> _events = [];
    private readonly List<TicketWatcher> _watchers = [];

    private Ticket()
    {
    }

    public Guid TenantId { get; private set; }

    public int Number { get; private set; }

    public string Subject { get; private set; } = null!;

    public TicketStatus Status { get; private set; }

    public TicketPriority Priority { get; private set; }

    public TicketType Type { get; private set; }

    public TicketChannel Channel { get; private set; }

    public Guid RequesterId { get; private set; }

    public Guid? AssigneeId { get; private set; }

    public Guid? TeamId { get; private set; }

    public List<string> Tags { get; private set; } = [];

    public DateTimeOffset LastActivityAt { get; private set; }

    public DateTimeOffset? FirstRespondedAt { get; private set; }

    public DateTimeOffset? SolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public IReadOnlyCollection<TicketMessage> Messages => _messages;

    public IReadOnlyCollection<TicketEvent> Events => _events;

    public IReadOnlyCollection<TicketWatcher> Watchers => _watchers;

    public bool IsClosed => Status == TicketStatus.Closed;

    public static Ticket Open(
        int number,
        string subject,
        string description,
        Guid requesterId,
        TicketPriority priority,
        TicketType type,
        TicketChannel channel,
        IEnumerable<string>? tags,
        Actor actor,
        DateTimeOffset now)
    {
        var ticket = new Ticket
        {
            Number = number,
            Subject = subject.Trim(),
            Status = TicketStatus.New,
            Priority = priority,
            Type = type,
            Channel = channel,
            RequesterId = requesterId,
            Tags = NormalizeTags(tags),
            LastActivityAt = now,
        };

        // The description is the first message; a customer wrote it even when an agent typed it in.
        var author = actor.Type == ActorType.Customer ? actor : Actor.Customer(requesterId);
        ticket._messages.Add(new TicketMessage(ticket.Id, author, description, isInternal: false, now));
        ticket._events.Add(TicketEvent.Create(ticket.Id, TicketEventType.Created, actor, null, null, now));
        ticket.Raise(new TicketCreated(ticket.Id));
        return ticket;
    }

    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
    [
        .. (tags ?? [])
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Distinct()
            .Order(StringComparer.Ordinal),
    ];

    public void Rename(string subject, Actor actor, DateTimeOffset now)
    {
        var trimmed = subject.Trim();
        if (trimmed == Subject)
        {
            return;
        }

        Record(TicketEventType.SubjectChanged, actor, Subject, trimmed, now);
        Subject = trimmed;
    }

    public void ChangeStatus(TicketStatus status, Actor actor, DateTimeOffset now)
    {
        if (status == Status)
        {
            return;
        }

        EnsureNotClosed();
        Record(TicketEventType.StatusChanged, actor, Status.ToString(), status.ToString(), now);
        Status = status;
        SolvedAt = status is TicketStatus.Solved or TicketStatus.Closed ? SolvedAt ?? now : null;
        ClosedAt = status == TicketStatus.Closed ? now : null;
    }

    public void ChangePriority(TicketPriority priority, Actor actor, DateTimeOffset now)
    {
        if (priority == Priority)
        {
            return;
        }

        Record(TicketEventType.PriorityChanged, actor, Priority.ToString(), priority.ToString(), now);
        Priority = priority;
    }

    public void ChangeType(TicketType type, Actor actor, DateTimeOffset now)
    {
        if (type == Type)
        {
            return;
        }

        Record(TicketEventType.TypeChanged, actor, Type.ToString(), type.ToString(), now);
        Type = type;
    }

    public void SetTags(IEnumerable<string>? tags, Actor actor, DateTimeOffset now)
    {
        var normalized = NormalizeTags(tags);
        if (normalized.SequenceEqual(Tags))
        {
            return;
        }

        Record(TicketEventType.TagsChanged, actor, string.Join(", ", Tags), string.Join(", ", normalized), now);
        Tags = normalized;
    }

    /// <summary>The names are stored in the timeline as text so history survives later renames.</summary>
    public void AssignTo(Guid? assigneeId, string? assigneeName, string? previousName, Actor actor, DateTimeOffset now)
    {
        if (assigneeId == AssigneeId)
        {
            return;
        }

        Record(TicketEventType.AssigneeChanged, actor, previousName, assigneeName, now);
        AssigneeId = assigneeId;
        if (assigneeId is { } id)
        {
            Watch(id);
        }
    }

    public void RouteTo(Guid? teamId, string? teamName, string? previousName, Actor actor, DateTimeOffset now)
    {
        if (teamId == TeamId)
        {
            return;
        }

        Record(TicketEventType.TeamChanged, actor, previousName, teamName, now);
        TeamId = teamId;
    }

    public TicketMessage AddMessage(Actor author, string body, bool isInternal, DateTimeOffset now)
    {
        EnsureNotClosed();
        var message = new TicketMessage(Id, author, body, isInternal, now);
        _messages.Add(message);
        LastActivityAt = now;

        if (!isInternal && author.Type == ActorType.Staff)
        {
            FirstRespondedAt ??= now;
            if (Status == TicketStatus.New)
            {
                ChangeStatus(TicketStatus.Open, Actor.System, now);
            }
        }
        else if (author.Type == ActorType.Customer
            && Status is TicketStatus.Pending or TicketStatus.Solved or TicketStatus.OnHold)
        {
            // The customer wrote back, so the ticket needs an agent again.
            ChangeStatus(TicketStatus.Open, Actor.System, now);
        }

        if (author is { Type: ActorType.Staff, Id: { } staffId })
        {
            Watch(staffId);
        }

        Raise(new TicketMessageAdded(Id, message.Id, author, isInternal));
        return message;
    }

    /// <summary>Adds a watcher. The watchers collection must be loaded to avoid duplicates.</summary>
    public bool Watch(Guid userId)
    {
        if (_watchers.Any(watcher => watcher.UserId == userId))
        {
            return false;
        }

        _watchers.Add(new TicketWatcher(Id, userId));
        return true;
    }

    public bool Unwatch(Guid userId) => _watchers.RemoveAll(watcher => watcher.UserId == userId) > 0;

    private void EnsureNotClosed()
    {
        if (IsClosed)
        {
            throw new TicketClosedException();
        }
    }

    private void Record(TicketEventType type, Actor actor, string? oldValue, string? newValue, DateTimeOffset now)
    {
        _events.Add(TicketEvent.Create(Id, type, actor, oldValue, newValue, now));
        LastActivityAt = now;
        if (!DomainEvents.OfType<TicketChanged>().Any())
        {
            Raise(new TicketChanged(Id));
        }
    }
}

public sealed class TicketClosedException() : InvalidOperationException("A closed ticket cannot be changed.");

public sealed record TicketCreated(Guid TicketId) : IDomainEvent;

public sealed record TicketChanged(Guid TicketId) : IDomainEvent;

public sealed record TicketMessageAdded(Guid TicketId, Guid MessageId, Actor Author, bool IsInternal) : IDomainEvent;
