using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
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

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.TicketSubject).HasMaxLength(NexDesk.Domain.Tickets.Ticket.SubjectMaxLength);
        builder.Property(n => n.ActorName).HasMaxLength(User.FullNameMaxLength);
        builder.Property(n => n.Preview).HasMaxLength(Notification.PreviewMaxLength);
        builder.HasIndex(n => new { n.UserId, n.ReadAt, n.CreatedAt });
        builder.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexDesk.Domain.Tickets.Ticket>().WithMany().HasForeignKey(n => n.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences");
        builder.HasKey(p => new { p.UserId, p.Type });
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.ToTable("sla_policies");
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Priority).HasConversion<int>();
        builder.HasIndex(p => new { p.TenantId, p.Priority }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.ToTable("holidays");
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.Name).HasMaxLength(Holiday.NameMaxLength);
        builder.HasIndex(h => new { h.TenantId, h.Date }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(h => h.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SlaBreachConfiguration : IEntityTypeConfiguration<SlaBreach>
{
    public void Configure(EntityTypeBuilder<SlaBreach> builder)
    {
        builder.ToTable("sla_breaches");
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.HasIndex(b => new { b.TenantId, b.BreachedAt });
        builder.HasIndex(b => new { b.TicketId, b.Metric }).IsUnique();
        builder.HasOne<NexDesk.Domain.Tickets.Ticket>().WithMany().HasForeignKey(b => b.TicketId).OnDelete(DeleteBehavior.Cascade);
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
