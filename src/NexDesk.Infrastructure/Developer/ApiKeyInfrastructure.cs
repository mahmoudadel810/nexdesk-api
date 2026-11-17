using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Developer;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Name).HasMaxLength(ApiKey.NameMaxLength);
        builder.Property(k => k.Prefix).HasMaxLength(32);
        builder.Property(k => k.KeyHash).HasMaxLength(128);
        builder.HasIndex(k => k.Prefix).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Resolves a presented key to its workspace. Keys arrive before any workspace is in scope.</summary>
internal sealed class ApiKeyDirectory(AppDbContext db, ITokenService tokens, TimeProvider clock)
    : IApiKeyDirectory, ICrossTenantQuery
{
    public async Task<ApiKeyIdentity?> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken)
    {
        if (ApiKey.PrefixOf(presentedKey) is not { } prefix)
        {
            return null;
        }

        var match = await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.Prefix == prefix)
            .Join(db.Tenants, k => k.TenantId, t => t.Id, (key, tenant) => new { Key = key, tenant.Status })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        var now = clock.GetUtcNow();
        if (match is null || !SameHash(match.Key.KeyHash, tokens.HashSecret(presentedKey)) || !match.Key.IsActive(now))
        {
            return null;
        }

        if (match.Key.ShouldRecordUse(now))
        {
            await db.ApiKeys.IgnoreQueryFilters()
                .Where(k => k.Id == match.Key.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return new ApiKeyIdentity(match.Key.Id, match.Key.TenantId, match.Key.Scopes, match.Status == TenantStatus.Suspended);
    }

    private static bool SameHash(string stored, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(presented));
}
