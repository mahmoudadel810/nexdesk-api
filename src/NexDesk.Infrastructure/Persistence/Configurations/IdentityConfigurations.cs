using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength);
        builder.Property(t => t.Slug).HasMaxLength(Tenant.SlugMaxLength);
        builder.Property(t => t.Timezone).HasMaxLength(64);
        builder.Property(t => t.DefaultLanguage).HasMaxLength(8);
        builder.Property(t => t.BrandColor).HasMaxLength(7);
        builder.Property(t => t.PortalWelcomeText).HasMaxLength(Tenant.WelcomeTextMaxLength);
        builder.Property(t => t.LogoFileKey).HasMaxLength(200);
        builder.Property(t => t.LogoContentType).HasMaxLength(64);
        builder.HasIndex(t => t.Slug).IsUnique();
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.Email).HasColumnType("citext").HasMaxLength(User.EmailMaxLength);
        builder.Property(u => u.FullName).HasMaxLength(User.FullNameMaxLength);
        builder.Property(u => u.PasswordHash).HasMaxLength(256);
        builder.Property(u => u.Language).HasMaxLength(8);
        builder.Ignore(u => u.IsEmailVerified);
        builder.HasIndex(u => u.Email).IsUnique();
        builder.HasIndex(u => u.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(64);
        builder.Property(t => t.UserAgent).HasMaxLength(256);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => new { t.SubjectType, t.SubjectId });
        builder.HasIndex(t => t.ExpiresAt);
    }
}

internal sealed class OneTimeTokenConfiguration : IEntityTypeConfiguration<OneTimeToken>
{
    public void Configure(EntityTypeBuilder<OneTimeToken> builder)
    {
        builder.ToTable("one_time_tokens");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(64);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.SubjectType, t.SubjectId, t.Purpose });
        builder.HasIndex(t => t.ExpiresAt);
    }
}

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Email).HasColumnType("citext").HasMaxLength(User.EmailMaxLength);
        builder.Property(i => i.TokenHash).HasMaxLength(64);
        builder.HasIndex(i => i.TokenHash).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.Email });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.InvitedById).OnDelete(DeleteBehavior.Restrict);
    }
}
