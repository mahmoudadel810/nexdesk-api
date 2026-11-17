using NexDesk.Domain.Common;

namespace NexDesk.Domain.Audit;

public enum AuditOperation
{
    Created,
    Updated,
    Deleted,
}

/// <summary>One field of a record before and after a change, written as display text.</summary>
public sealed record AuditChange(string Field, string? Before, string? After);

/// <summary>A record of who changed what in a workspace, when, and from which address.</summary>
public sealed class AuditLog : ITenantOwned
{
    public const int EntityTypeMaxLength = 64;
    public const int LabelMaxLength = 300;
    public const int IpAddressMaxLength = 64;

    private AuditLog()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public ActorType ActorType { get; private set; }

    public Guid? ActorId { get; private set; }

    /// <summary>The kind of record, such as <c>ticket</c> or <c>sla-policy</c>.</summary>
    public string EntityType { get; private set; } = null!;

    public Guid EntityId { get; private set; }

    /// <summary>How the record was named at the time, so the entry stays readable after it is deleted.</summary>
    public string? EntityLabel { get; private set; }

    public AuditOperation Operation { get; private set; }

    public IReadOnlyList<AuditChange> Changes { get; private set; } = [];

    public string? IpAddress { get; private set; }

    public static AuditLog Record(
        Guid tenantId,
        DateTimeOffset occurredAt,
        Actor actor,
        string entityType,
        Guid entityId,
        string? entityLabel,
        AuditOperation operation,
        IReadOnlyList<AuditChange> changes,
        string? ipAddress) => new()
        {
            TenantId = tenantId,
            OccurredAt = occurredAt,
            ActorType = actor.Type,
            ActorId = actor.Id,
            EntityType = entityType,
            EntityId = entityId,
            EntityLabel = Truncate(entityLabel, LabelMaxLength),
            Operation = operation,
            Changes = changes,
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
        };

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
