using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;

namespace NexDesk.Infrastructure.Persistence.Configurations;

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("teams");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Team.NameMaxLength);
        builder.Property(t => t.Description).HasMaxLength(Team.DescriptionMaxLength);
        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.Members).WithOne().HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("team_members");
        builder.HasKey(m => new { m.TeamId, m.UserId });
        builder.HasIndex(m => m.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BusinessHoursDayConfiguration : IEntityTypeConfiguration<BusinessHoursDay>
{
    public void Configure(EntityTypeBuilder<BusinessHoursDay> builder)
    {
        builder.ToTable("business_hours");
        builder.Property(d => d.Id).ValueGeneratedNever();

        // Stored as a number so the week sorts Sunday to Saturday rather than alphabetically.
        builder.Property(d => d.Day).HasConversion<int>();
        builder.HasIndex(d => new { d.TenantId, d.Day }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
