using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;

namespace NexDesk.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Email).HasColumnType("citext").HasMaxLength(User.EmailMaxLength);
        builder.Property(c => c.FullName).HasMaxLength(Customer.FullNameMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.Email }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Subject).HasMaxLength(Ticket.SubjectMaxLength);

        // Stored as a number so sorting by priority follows Low < Normal < High < Urgent.
        builder.Property(t => t.Priority).HasConversion<int>();
        builder.Property(t => t.Tags).HasColumnType("text[]");
        builder.Ignore(t => t.IsClosed);

        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.AssigneeId });
        builder.HasIndex(t => new { t.TenantId, t.RequesterId });
        builder.HasIndex(t => new { t.TenantId, t.LastActivityAt });
        builder.HasIndex(t => t.Tags).HasMethod("gin");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Customer>().WithMany().HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Team>().WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(t => t.Messages).WithOne().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.Events).WithOne().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.Watchers).WithOne().HasForeignKey(w => w.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(t => t.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(t => t.Watchers).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        builder.ToTable("ticket_messages");
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Body).HasMaxLength(TicketMessage.BodyMaxLength);
        builder.HasIndex(m => new { m.TicketId, m.CreatedAt });
    }
}

internal sealed class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
{
    public void Configure(EntityTypeBuilder<TicketEvent> builder)
    {
        builder.ToTable("ticket_events");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.OldValue).HasMaxLength(1000);
        builder.Property(e => e.NewValue).HasMaxLength(1000);
        builder.HasIndex(e => new { e.TicketId, e.CreatedAt });
    }
}

internal sealed class TicketWatcherConfiguration : IEntityTypeConfiguration<TicketWatcher>
{
    public void Configure(EntityTypeBuilder<TicketWatcher> builder)
    {
        builder.ToTable("ticket_watchers");
        builder.HasKey(w => new { w.TicketId, w.UserId });
        builder.HasIndex(w => w.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachments");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.FileName).HasMaxLength(Attachment.FileNameMaxLength);
        builder.Property(a => a.ContentType).HasMaxLength(200);
        builder.Property(a => a.StorageKey).HasMaxLength(200);
        builder.HasIndex(a => a.MessageId);
        builder.HasIndex(a => a.TicketId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TicketMessage>().WithMany().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Last ticket number handed out per tenant. Only touched through an atomic upsert.</summary>
internal sealed class TicketSequence
{
    public Guid TenantId { get; set; }

    public int LastNumber { get; set; }
}

internal sealed class TicketSequenceConfiguration : IEntityTypeConfiguration<TicketSequence>
{
    public void Configure(EntityTypeBuilder<TicketSequence> builder)
    {
        builder.ToTable("ticket_sequences");
        builder.HasKey(s => s.TenantId);
        builder.Property(s => s.TenantId).ValueGeneratedNever();
        builder.HasOne<Tenant>().WithOne().HasForeignKey<TicketSequence>(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
