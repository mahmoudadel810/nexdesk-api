using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.KnowledgeBase;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Tickets;

namespace NexDesk.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock,
    IDomainEventDispatcher domainEvents,
    IServiceProvider services) : DbContext(options), IAppDbContext
{
    public const string TenantFilter = "Tenant";
    public const string SoftDeleteFilter = "SoftDelete";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<OneTimeToken> OneTimeTokens => Set<OneTimeToken>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<BusinessHoursDay> BusinessHours => Set<BusinessHoursDay>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();

    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();

    public DbSet<TicketWatcher> TicketWatchers => Set<TicketWatcher>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<CustomField> CustomFields => Set<CustomField>();

    public DbSet<SavedView> SavedViews => Set<SavedView>();

    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<SlaBreach> SlaBreaches => Set<SlaBreach>();

    public DbSet<CsatResponse> CsatResponses => Set<CsatResponse>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<KbCategory> KbCategories => Set<KbCategory>();

    public DbSet<KbArticle> KbArticles => Set<KbArticle>();

    public void DiscardChanges() => ChangeTracker.Clear();

    private Guid? CurrentTenantId => tenant.TenantId;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var changedTickets = ChangeTracker.Entries<Ticket>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .Select(entry => entry.Entity)
            .ToList();
        if (changedTickets.Count > 0)
        {
            // Resolved here rather than injected: the hooks themselves depend on this context.
            foreach (var hook in services.GetServices<ITicketSaveHook>())
            {
                await hook.BeforeSaveAsync(changedTickets, cancellationToken);
            }
        }

        ApplyTenantAndAudit();
        var raised = TakeDomainEvents();
        var written = await base.SaveChangesAsync(cancellationToken);
        await domainEvents.DispatchAsync(raised, cancellationToken);
        return written;
    }

    public override int SaveChanges()
    {
        ApplyTenantAndAudit();
        return base.SaveChanges();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<User>().HasQueryFilter(
            TenantFilter,
            user => CurrentTenantId != null && user.TenantId == CurrentTenantId);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(type => typeof(ITenantOwned).IsAssignableFrom(type.ClrType)))
        {
            var entity = Expression.Parameter(entityType.ClrType, "entity");
            var filter = Expression.Lambda(
                Expression.Equal(
                    Expression.Convert(Expression.Property(entity, nameof(ITenantOwned.TenantId)), typeof(Guid?)),
                    Expression.Property(Expression.Constant(this), nameof(CurrentTenantId))),
                entity);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(TenantFilter, filter);
            modelBuilder.Entity(entityType.ClrType).HasIndex(nameof(ITenantOwned.TenantId));
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(type => typeof(ISoftDeletable).IsAssignableFrom(type.ClrType)))
        {
            var entity = Expression.Parameter(entityType.ClrType, "entity");
            var notDeleted = Expression.Lambda(
                Expression.Equal(
                    Expression.Property(entity, nameof(ISoftDeletable.DeletedAt)),
                    Expression.Constant(null, typeof(DateTimeOffset?))),
                entity);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(SoftDeleteFilter, notDeleted);
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(type => typeof(Entity).IsAssignableFrom(type.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType).Ignore(nameof(Entity.DomainEvents));
        }

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        base.ConfigureConventions(configurationBuilder);
    }

    private void ApplyTenantAndAudit()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is ITenantOwned owned && entry.State is not (EntityState.Detached or EntityState.Unchanged))
            {
                EnforceTenant(entry, owned);
            }

            if (entry is { Entity: ISoftDeletable, State: EntityState.Deleted })
            {
                // The row stays; the soft-delete query filter hides it from now on.
                entry.State = EntityState.Modified;
                entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = now;
                entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = currentUser.UserId;
            }

            if (entry.Entity is IAuditable)
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Property(nameof(IAuditable.CreatedAt)).CurrentValue = now;
                    entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = currentUser.UserId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
                    entry.Property(nameof(IAuditable.UpdatedBy)).CurrentValue = currentUser.UserId;
                }
            }
        }
    }

    private List<IDomainEvent> TakeDomainEvents()
    {
        var entities = ChangeTracker.Entries<Entity>().Select(entry => entry.Entity).ToList();
        var raised = entities.SelectMany(entity => entity.DomainEvents).ToList();
        entities.ForEach(entity => entity.ClearDomainEvents());
        return raised;
    }

    private void EnforceTenant(EntityEntry entry, ITenantOwned owned)
    {
        if (CurrentTenantId is not { } tenantId)
        {
            throw new InvalidOperationException(
                $"Cannot write {entry.Metadata.ClrType.Name} without a tenant in scope.");
        }

        if (entry.State == EntityState.Added && owned.TenantId == Guid.Empty)
        {
            entry.Property(nameof(ITenantOwned.TenantId)).CurrentValue = tenantId;
        }
        else if (owned.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"{entry.Metadata.ClrType.Name} belongs to another tenant than the one in scope.");
        }
    }
}
