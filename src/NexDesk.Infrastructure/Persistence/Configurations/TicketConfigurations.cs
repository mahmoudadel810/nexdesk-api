using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;
using NpgsqlTypes;

namespace NexDesk.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Email).HasColumnType("citext").HasMaxLength(User.EmailMaxLength);
        builder.Property(c => c.FullName).HasMaxLength(Customer.FullNameMaxLength);
        builder.Property(c => c.PasswordHash).HasMaxLength(256);
        builder.Ignore(c => c.HasPortalAccount);
        builder.Ignore(c => c.IsEmailVerified);
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
        builder.Ignore(t => t.IsSlaPaused);
        builder.HasIndex(t => new { t.TenantId, t.ResolutionDueAt });
        builder.HasIndex(t => new { t.TenantId, t.FirstResponseDueAt });

        builder.Property(t => t.CustomFields)
            .HasColumnType("jsonb")
            .HasConversion(
                fields => JsonSerializer.Serialize(fields, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonSerializerOptions.Default)!,
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) => ReferenceEquals(left, right),
                    fields => fields.Count,
                    fields => fields));

        // PostgreSQL's row version: it changes on every update, so no extra column or trigger is needed.
        builder.Property(t => t.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        builder.Property<NpgsqlTsVector>(FullText.SearchVector)
            .HasComputedColumnSql($"to_tsvector('{FullText.Config}', subject)", stored: true);
        builder.HasIndex(FullText.SearchVector).HasMethod("gin");
        builder.HasOne<Ticket>().WithMany().HasForeignKey(t => t.MergedIntoId).OnDelete(DeleteBehavior.SetNull);

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
        builder.Property<NpgsqlTsVector>(FullText.SearchVector)
            .HasComputedColumnSql($"to_tsvector('{FullText.Config}', body)", stored: true);
        builder.HasIndex(FullText.SearchVector).HasMethod("gin");
    }
}

internal sealed class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
{
    public void Configure(EntityTypeBuilder<TicketEvent> builder)
    {
        builder.ToTable("ticket_events");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Field).HasMaxLength(CustomField.LabelMaxLength);
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

internal sealed class CsatResponseConfiguration : IEntityTypeConfiguration<CsatResponse>
{
    public void Configure(EntityTypeBuilder<CsatResponse> builder)
    {
        builder.ToTable("csat_responses");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Comment).HasMaxLength(CsatResponse.CommentMaxLength);
        builder.HasIndex(c => c.TicketId).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.CreatedAt });
        builder.HasOne<Ticket>().WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Customer>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomFieldConfiguration : IEntityTypeConfiguration<CustomField>
{
    public void Configure(EntityTypeBuilder<CustomField> builder)
    {
        builder.ToTable("custom_fields");
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Key).HasMaxLength(CustomField.KeyMaxLength);
        builder.Property(f => f.Label).HasMaxLength(CustomField.LabelMaxLength);
        builder.Property(f => f.Options).HasColumnType("text[]");
        builder.HasIndex(f => new { f.TenantId, f.Key }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SavedViewConfiguration : IEntityTypeConfiguration<SavedView>
{
    public void Configure(EntityTypeBuilder<SavedView> builder)
    {
        builder.ToTable("saved_views");
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Name).HasMaxLength(SavedView.NameMaxLength);
        builder.Property(v => v.FilterJson).HasColumnName("filter").HasColumnType("jsonb");
        builder.HasIndex(v => new { v.TenantId, v.OwnerId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(v => v.OwnerId).OnDelete(DeleteBehavior.Cascade);
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
