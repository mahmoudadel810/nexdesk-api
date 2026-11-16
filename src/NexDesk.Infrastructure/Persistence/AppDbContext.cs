using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock) : DbContext(options), IAppDbContext
{
    public const string TenantFilter = "Tenant";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<OneTimeToken> OneTimeTokens => Set<OneTimeToken>();

    private Guid? CurrentTenantId => tenant.TenantId;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTenantAndAudit();
        return base.SaveChangesAsync(cancellationToken);
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
