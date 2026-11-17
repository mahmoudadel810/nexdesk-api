using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Persistence;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NexDesk.Infrastructure.Billing;

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");
        builder.HasKey(p => p.Code);
        builder.Property(p => p.Code).HasMaxLength(40);
        builder.Property(p => p.Name).HasMaxLength(60);
        builder.Ignore(p => p.IsFree);
        builder.HasData(Plan.Catalog.Select(p => new
        {
            p.Code,
            p.Name,
            p.PriceCentsMonthly,
            p.MaxAgents,
            p.MaxTicketsPerMonth,
            p.MaxStorageBytes,
            p.MaxAutomationRules,
            p.ApiAccess,
            p.RequestsPerMinute,
            p.Position,
        }));
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PlanCode).HasMaxLength(40);
        builder.Property(s => s.CardBrand).HasMaxLength(30);
        builder.Property(s => s.CardLast4).HasMaxLength(4);
        builder.HasIndex(s => s.TenantId).IsUnique();
        builder.HasIndex(s => s.CurrentPeriodEnd);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanCode).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.PlanCode).HasMaxLength(40);
        builder.Property(i => i.PlanName).HasMaxLength(60);
        builder.Property(i => i.Currency).HasMaxLength(3);
        builder.Property(i => i.BilledToName).HasMaxLength(Invoice.BilledToMaxLength);
        builder.Property(i => i.BilledToEmail).HasMaxLength(Invoice.BilledToMaxLength);
        builder.Property(i => i.CardBrand).HasMaxLength(30);
        builder.Property(i => i.CardLast4).HasMaxLength(4);
        builder.Ignore(i => i.DisplayNumber);
        builder.HasIndex(i => new { i.TenantId, i.Number }).IsUnique();
        builder.HasIndex(i => i.IssuedAt);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UsageCounterConfiguration : IEntityTypeConfiguration<UsageCounter>
{
    public void Configure(EntityTypeBuilder<UsageCounter> builder)
    {
        builder.ToTable("usage_counters");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => new { c.TenantId, c.Period }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UsageMeter(AppDbContext db, ITenantContext tenant) : IUsageMeter
{
    public async Task TicketCreatedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return;
        }

        // One statement, so tickets created at the same moment are all counted.
        var id = Guid.CreateVersion7();
        var period = UsageCounter.PeriodOf(now);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO usage_counters (id, tenant_id, period, tickets_created) VALUES ({id}, {tenantId}, {period}, 1)
            ON CONFLICT (tenant_id, period) DO UPDATE SET tickets_created = usage_counters.tickets_created + 1
            """,
            cancellationToken);
    }
}

/// <summary>Draws an invoice as a one-page PDF.</summary>
public sealed class InvoicePdfRenderer : IInvoiceRenderer
{
    /// <summary>Ships with QuestPDF.</summary>
    private const string LatinFont = "Lato";

    /// <summary>The family name stored in the embedded font file.</summary>
    private const string ArabicFont = "Noto Sans Arabic";
    private const string Accent = "#3F51B5";
    private const string Muted = "#5F6368";

    static InvoicePdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        using var font = typeof(InvoicePdfRenderer).Assembly
            .GetManifestResourceStream("NexDesk.Infrastructure.Billing.Fonts.NotoSansArabic-Regular.ttf")
            ?? throw new InvalidOperationException("The invoice font is missing from the assembly.");
        FontManager.RegisterFontFromStream(font);
    }

    public byte[] RenderPdf(InvoicePrintModel invoice)
    {
        var amount = Money(invoice.AmountCents, invoice.Currency);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(48);
            page.DefaultTextStyle(style => style.FontSize(10).FontFamily(LatinFont, ArabicFont));

            page.Header().Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text("NexDesk").FontSize(22).Bold().FontColor(Accent);
                    column.Item().Text("Help desk and SLA management").FontColor(Muted);
                });
                row.ConstantItem(200).AlignRight().Column(column =>
                {
                    column.Item().AlignRight().Text("INVOICE").FontSize(18).SemiBold();
                    column.Item().AlignRight().Text(invoice.Number).FontColor(Muted);
                    column.Item().PaddingTop(6).AlignRight().Text("PAID").Bold().FontColor("#1B7F3B");
                });
            });

            page.Content().PaddingVertical(28).Column(column =>
            {
                column.Spacing(20);
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(billed =>
                    {
                        billed.Item().Text("Billed to").FontColor(Muted);
                        billed.Item().Text(invoice.BilledToName).SemiBold();
                        billed.Item().Text(invoice.BilledToEmail);
                    });
                    row.RelativeItem().Column(dates =>
                    {
                        dates.Item().AlignRight().Text($"Issued: {Date(invoice.IssuedAt)}");
                        dates.Item().AlignRight().Text($"Paid: {Date(invoice.PaidAt)}");
                        dates.Item().AlignRight().Text(invoice.CardLast4 is null
                            ? "Payment: card"
                            : $"Payment: {invoice.CardBrand} ending {invoice.CardLast4}");
                    });
                });

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(4);
                        columns.RelativeColumn(3);
                        columns.RelativeColumn(2);
                    });
                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Description");
                        header.Cell().Element(HeaderCell).Text("Period");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Amount");
                    });
                    table.Cell().Element(BodyCell).Text($"NexDesk {invoice.PlanName} plan, one month, for {invoice.WorkspaceName}");
                    table.Cell().Element(BodyCell).Text($"{Date(invoice.PeriodStart)} to {Date(invoice.PeriodEnd)}");
                    table.Cell().Element(BodyCell).AlignRight().Text(amount);
                });

                column.Item().AlignRight().Width(220).Column(totals =>
                {
                    totals.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Total").SemiBold();
                        row.RelativeItem().AlignRight().Text(amount).SemiBold();
                    });
                    totals.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Amount paid");
                        row.RelativeItem().AlignRight().Text(amount);
                    });
                    totals.Item().PaddingTop(4).BorderTop(1).BorderColor(Colors.Grey.Lighten1).PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem().Text("Balance due").Bold();
                        row.RelativeItem().AlignRight().Text(Money(0, invoice.Currency)).Bold();
                    });
                });
            });

            page.Footer().AlignCenter().Text(
                "NexDesk is a demonstration product. Payments are simulated and no money was charged.")
                .FontSize(8)
                .FontColor(Muted);
        })).GeneratePdf();

        static IContainer HeaderCell(IContainer cell) =>
            cell.BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingVertical(6).DefaultTextStyle(style => style.SemiBold());

        static IContainer BodyCell(IContainer cell) =>
            cell.BorderBottom(1).BorderColor(Colors.Grey.Lighten3).PaddingVertical(8);
    }

    private static string Date(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Money(int cents, string currency) =>
        $"{(cents / 100m).ToString("N2", CultureInfo.InvariantCulture)} {currency}";
}

/// <summary>Bills the next month of every paid subscription whose period has ended. Runs hourly.</summary>
public sealed class SubscriptionRenewalJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SubscriptionRenewalJob> logger)
    : ICrossTenantQuery
{
    public const string JobId = "subscription-renewal";

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<Guid> dueTenantIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            dueTenantIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions
                .IgnoreQueryFilters()
                .Where(s => s.CurrentPeriodEnd != null && s.CurrentPeriodEnd <= now)
                .Select(s => s.TenantId)
                .ToListAsync(cancellationToken);
        }

        foreach (var tenantId in dueTenantIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Begin(tenantId);
            try
            {
                if (await scope.ServiceProvider.GetRequiredService<BillingService>().RenewIfDueAsync(now, cancellationToken))
                {
                    logger.LogInformation("Renewed the subscription of tenant {TenantId}", tenantId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Renewal failed for tenant {TenantId}", tenantId);
            }
        }
    }
}

/// <summary>Deletes rows nobody needs any more: dead tokens and old automation log entries. Runs daily.</summary>
public sealed class CleanupJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CleanupJob> logger) : ICrossTenantQuery
{
    public const string JobId = "cleanup";

    /// <summary>Expired tokens are kept this long, so a reused one can still be recognised as stolen.</summary>
    public static readonly TimeSpan TokenGrace = TimeSpan.FromDays(7);

    public static readonly TimeSpan ExecutionRetention = TimeSpan.FromDays(90);

    public static readonly TimeSpan WebhookDeliveryRetention = TimeSpan.FromDays(30);

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Phones whose sign-in has ended get no pushes anyway; their rows go before the tokens that prove it.
        var devices = await db.Devices
            .IgnoreQueryFilters()
            .Where(d => !db.RefreshTokens.Any(t => t.FamilyId == d.RefreshFamilyId && t.RevokedAt == null && t.ExpiresAt > now))
            .ExecuteDeleteAsync(cancellationToken);

        var tokenCutoff = now - TokenGrace;
        var refreshTokens = await db.RefreshTokens
            .Where(t => t.ExpiresAt < tokenCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var oneTimeTokens = await db.OneTimeTokens
            .Where(t => t.ExpiresAt < tokenCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.Invitations
            .IgnoreQueryFilters()
            .Where(i => i.ExpiresAt < tokenCutoff && i.AcceptedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        // The newest entry per rule and ticket stays: a time-based rule reads it to know the ticket is done.
        var executionCutoff = now - ExecutionRetention;
        var executions = await db.AutomationExecutions
            .IgnoreQueryFilters()
            .Where(e => e.CreatedAt < executionCutoff
                && db.AutomationExecutions.IgnoreQueryFilters().Any(newer =>
                    newer.RuleId == e.RuleId && newer.TicketId == e.TicketId && newer.CreatedAt > e.CreatedAt))
            .ExecuteDeleteAsync(cancellationToken);

        var deliveryCutoff = now - WebhookDeliveryRetention;
        var deliveries = await db.WebhookDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.CreatedAt < deliveryCutoff && d.Status != WebhookDeliveryStatus.Pending)
            .ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation(
            "Cleanup removed {Devices} signed-out devices, {RefreshTokens} refresh tokens, {OneTimeTokens} one-time tokens, "
            + "{Invitations} invitations, {Executions} automation log entries and {Deliveries} webhook deliveries",
            devices,
            refreshTokens,
            oneTimeTokens,
            invitations,
            executions,
            deliveries);
    }
}

internal sealed class PlanRateLimits(IServiceScopeFactory scopes, TimeProvider clock) : IPlanRateLimits, ICrossTenantQuery
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (int Limit, DateTimeOffset LoadedAt)> _limits = new();

    public async ValueTask<int> RequestsPerMinuteAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_limits.TryGetValue(tenantId, out var cached) && now - cached.LoadedAt < Lifetime)
        {
            return cached.Limit;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var limit = await db.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Join(db.Plans, s => s.PlanCode, p => p.Code, (_, p) => (int?)p.RequestsPerMinute)
            .FirstOrDefaultAsync(cancellationToken)
            ?? Plan.Catalog[0].RequestsPerMinute;
        _limits[tenantId] = (limit, now);
        return limit;
    }

    public void Forget(Guid tenantId) => _limits.TryRemove(tenantId, out _);
}
