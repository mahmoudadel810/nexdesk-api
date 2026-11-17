namespace NexDesk.Domain.Common;

/// <summary>Something that happened to an entity, handed to in-process handlers once the change is saved.</summary>
public interface IDomainEvent;

public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}

public interface ITenantOwned
{
    Guid TenantId { get; }
}

public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }

    Guid? CreatedBy { get; }

    DateTimeOffset? UpdatedAt { get; }

    Guid? UpdatedBy { get; }
}

/// <summary>Deleting the entity hides it from queries instead of removing the row.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; }

    Guid? DeletedBy { get; }
}

public abstract class AuditableEntity : Entity, IAuditable
{
    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }
}

/// <summary>Who performed an action on a ticket.</summary>
public enum ActorType
{
    Staff,
    Customer,
    System,

    /// <summary>An integration calling the public API with one of the workspace's API keys.</summary>
    ApiKey,
}

public readonly record struct Actor(ActorType Type, Guid? Id)
{
    public static readonly Actor System = new(ActorType.System, null);

    public static Actor Staff(Guid userId) => new(ActorType.Staff, userId);

    public static Actor Customer(Guid customerId) => new(ActorType.Customer, customerId);

    public static Actor ApiKey(Guid apiKeyId) => new(ActorType.ApiKey, apiKeyId);
}
