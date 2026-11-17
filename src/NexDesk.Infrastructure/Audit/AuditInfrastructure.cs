using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Application.Common;
using NexDesk.Domain.Audit;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Identity;
using NexDesk.Domain.KnowledgeBase;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;

namespace NexDesk.Infrastructure.Audit;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.EntityType).HasMaxLength(AuditLog.EntityTypeMaxLength);
        builder.Property(a => a.EntityLabel).HasMaxLength(AuditLog.LabelMaxLength);
        builder.Property(a => a.IpAddress).HasMaxLength(AuditLog.IpAddressMaxLength);
        builder.Property(a => a.Changes)
            .HasColumnType("jsonb")
            .HasConversion(
                changes => JsonSerializer.Serialize(changes, AuditValues.Json),
                json => JsonSerializer.Deserialize<List<AuditChange>>(json, AuditValues.Json)!,
                new ValueComparer<IReadOnlyList<AuditChange>>(
                    (left, right) => ReferenceEquals(left, right),
                    changes => changes.Count,
                    changes => changes));
        builder.HasIndex(a => new { a.TenantId, a.OccurredAt });
        builder.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
        builder.HasIndex(a => new { a.TenantId, a.ActorId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Which records are audited, under what name, and which of their fields are left out.</summary>
internal static class AuditCatalog
{
    /// <summary>Bookkeeping that every record has, and secrets that must never be copied anywhere.</summary>
    private static readonly HashSet<string> AlwaysHidden =
    [
        "Id", "TenantId", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeletedAt", "DeletedBy", "Version",
        "PasswordHash", "TokenHash", "KeyHash", "Secret", "SecurityVersion", "FailedLoginCount", "LockedUntil",
        "LastLoginAt",
    ];

    private static readonly Dictionary<Type, AuditedType> Types = new()
    {
        [typeof(Tenant)] = new("workspace", "LogoFileKey", "LogoSizeBytes"),
        [typeof(User)] = new("user", "Availability"),
        [typeof(Invitation)] = new("invitation"),
        [typeof(Team)] = new("team"),
        [typeof(TeamMember)] = new("team-member"),
        [typeof(Customer)] = new("customer"),
        [typeof(BusinessHoursDay)] = new("business-hours"),
        [typeof(Holiday)] = new("holiday"),
        [typeof(SlaPolicy)] = new("sla-policy"),
        [typeof(CustomField)] = new("custom-field"),
        [typeof(KbCategory)] = new("kb-category"),
        [typeof(KbArticle)] = new("kb-article", "ViewCount", "HelpfulCount", "NotHelpfulCount"),
        [typeof(AutomationRule)] = new("automation-rule", "RunCount", "LastRunAt"),
        [typeof(Subscription)] = new("subscription"),
        [typeof(Invoice)] = new("invoice"),
        [typeof(ApiKey)] = new("api-key", "Prefix", "LastUsedAt"),
        [typeof(WebhookEndpoint)] = new("webhook"),

        // Deadlines, warnings and activity times are kept up to date by the system; the ticket's own
        // timeline already explains them.
        [typeof(Ticket)] = new(
            "ticket",
            "LastActivityAt", "FirstRespondedAt", "FirstResponseDueAt", "ResolutionDueAt", "FirstResponseBreachedAt",
            "ResolutionBreachedAt", "FirstResponseWarnedAt", "ResolutionWarnedAt", "ResolutionRemainingMinutes",
            "SlaFirstResponseMinutes", "SlaResolutionMinutes", "IsEscalated"),
    };

    public static AuditedType? Find(Type type) => Types.GetValueOrDefault(type);

    public static bool IsVisible(AuditedType type, string property) =>
        !AlwaysHidden.Contains(property) && !type.Hidden.Contains(property);
}

internal sealed class AuditedType(string name, params string[] hidden)
{
    public string Name { get; } = name;

    public HashSet<string> Hidden { get; } = [.. hidden];
}

/// <summary>Turns stored values into short, stable display text.</summary>
public static class AuditValues
{
    public const int MaxLength = 1000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? Format(object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            Enum e => e.ToString(),
            DateTimeOffset d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable<string> list => string.Join(", ", list),
            _ => JsonSerializer.Serialize(value, value.GetType(), Json),
        };
        return text is { Length: > MaxLength } ? text[..MaxLength] + "…" : text;
    }
}

internal sealed class NoRequestOrigin : IRequestOrigin
{
    public string? IpAddress => null;
}

/// <summary>
/// Writes an audit entry for every audited record a save creates, changes or deletes, inside that same save,
/// so the log can never disagree with the data.
/// </summary>
internal sealed class AuditRecorder(
    ITenantContext tenant,
    ICurrentUser currentUser,
    IRequestOrigin origin,
    TimeProvider clock,
    ICurrentCustomer? currentCustomer = null)
{
    public IReadOnlyList<AuditLog> Collect(ChangeTracker changeTracker)
    {
        // Work outside a workspace, such as sign-up before the workspace exists, has no log to write to.
        if (tenant.TenantId is not { } tenantId)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        var actor = CurrentActor();
        var entries = new List<AuditLog>();
        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged
                || AuditCatalog.Find(entry.Metadata.ClrType) is not { } type
                || OwningTenant(entry, tenantId) != tenantId)
            {
                continue;
            }

            var operation = OperationOf(entry);
            var changes = ChangesOf(entry, type, operation);
            if (operation == AuditOperation.Updated && changes.Count == 0)
            {
                continue;
            }

            entries.Add(AuditLog.Record(
                tenantId, now, actor, type.Name, EntityId(entry), Label(entry), operation, changes, origin.IpAddress));
        }

        return entries;
    }

    // A platform admin is not a member of the workspace, so the workspace sees their actions as NexDesk's.
    private Actor CurrentActor() =>
        currentUser.IsPlatformAdmin ? Actor.System
        : currentUser.ApiKeyId is { } apiKeyId ? Actor.ApiKey(apiKeyId)
        : currentUser.UserId is { } userId ? Actor.Staff(userId)
        : currentCustomer?.CustomerId is { } customerId ? Actor.Customer(customerId)
        : Actor.System;

    private static Guid? OwningTenant(EntityEntry entry, Guid scopeTenantId) => entry.Entity switch
    {
        Tenant workspace => workspace.Id,
        User user => user.TenantId,

        // A new record without a workspace gets the one in scope stamped later in the same save.
        ITenantOwned { TenantId: var id } => id == Guid.Empty && entry.State == EntityState.Added ? scopeTenantId : id,
        _ => null,
    };

    private static AuditOperation OperationOf(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            return AuditOperation.Created;
        }

        if (entry.State == EntityState.Deleted)
        {
            return AuditOperation.Deleted;
        }

        var deletedAt = entry.Metadata.FindProperty(nameof(ISoftDeletable.DeletedAt));
        return deletedAt is not null
            && entry.Property(deletedAt.Name) is { IsModified: true, OriginalValue: null, CurrentValue: not null }
            ? AuditOperation.Deleted
            : AuditOperation.Updated;
    }

    private static List<AuditChange> ChangesOf(EntityEntry entry, AuditedType type, AuditOperation operation)
    {
        var changes = new List<AuditChange>();
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (property.Metadata.IsShadowProperty() || !AuditCatalog.IsVisible(type, name))
            {
                continue;
            }

            switch (operation)
            {
                case AuditOperation.Created when AuditValues.Format(property.CurrentValue) is { } after:
                    changes.Add(new AuditChange(Field(name), null, after));
                    break;
                case AuditOperation.Deleted when AuditValues.Format(property.OriginalValue) is { } before:
                    changes.Add(new AuditChange(Field(name), before, null));
                    break;
                case AuditOperation.Updated when property.IsModified:
                    var was = AuditValues.Format(property.OriginalValue);
                    var now = AuditValues.Format(property.CurrentValue);
                    if (was != now)
                    {
                        changes.Add(new AuditChange(Field(name), was, now));
                    }

                    break;
            }
        }

        return changes;
    }

    private static Guid EntityId(EntityEntry entry) => entry.Entity switch
    {
        Entity entity => entity.Id,
        TeamMember member => member.TeamId,
        _ => entry.Metadata.FindPrimaryKey()?.Properties
            .Select(p => entry.Property(p.Name).CurrentValue)
            .OfType<Guid>()
            .FirstOrDefault() ?? Guid.Empty,
    };

    private static string? Label(EntityEntry entry) => entry.Entity switch
    {
        Ticket ticket => $"#{ticket.Number} {ticket.Subject}",
        SlaPolicy policy => policy.Priority.ToString(),
        BusinessHoursDay day => day.Day.ToString(),
        Invoice invoice => invoice.Number.ToString(CultureInfo.InvariantCulture),
        Subscription subscription => subscription.PlanCode,
        Holiday holiday => $"{holiday.Name} ({holiday.Date:yyyy-MM-dd})",
        TeamMember member => member.UserId.ToString(),
        _ => FirstText(entry, "Name", "Title", "FullName", "Label", "Email", "Url"),
    };

    private static string? FirstText(EntityEntry entry, params string[] names) =>
        names
            .Select(name => entry.Metadata.FindProperty(name))
            .Where(property => property is not null)
            .Select(property => entry.Property(property!.Name).CurrentValue as string)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>The field name as the API spells it, in camel case.</summary>
    private static string Field(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);
}
