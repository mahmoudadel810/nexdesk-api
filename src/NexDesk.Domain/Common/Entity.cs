namespace NexDesk.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
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

public abstract class AuditableEntity : Entity, IAuditable
{
    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }
}
