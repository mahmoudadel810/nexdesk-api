using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Application.Platform;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Platform;

/// <summary>
/// The only reads that span every workspace, for the platform console. Each query states which workspace
/// it looks at, because the tenant filter is switched off here.
/// </summary>
internal sealed class PlatformQueries(AppDbContext db) : IPlatformQueries, ICrossTenantQuery
{
    private static readonly TicketStatus[] Unresolved =
        [TicketStatus.New, TicketStatus.Open, TicketStatus.Pending, TicketStatus.OnHold];

    public async Task<PagedResult<AdminTenantListItem>> Tenants(AdminTenantFilter filter, CancellationToken cancellationToken)
    {
        var period = UsageCounter.PeriodOf(DateTimeOffset.UtcNow);
        var rows =
            from t in db.Tenants.AsNoTracking()
            join s in db.Subscriptions.IgnoreQueryFilters() on t.Id equals s.TenantId into subscriptions
            from s in subscriptions.DefaultIfEmpty()
            join p in db.Plans on s == null ? Plan.Free : s.PlanCode equals p.Code
            select new
            {
                t.Id,
                t.Name,
                t.Slug,
                t.Status,
                t.CreatedAt,
                PlanCode = p.Code,
                PlanName = p.Name,
                p.PriceCentsMonthly,
                OwnerEmail = db.Users.IgnoreQueryFilters()
                    .Where(u => u.TenantId == t.Id && u.Role == Role.Owner)
                    .OrderBy(u => u.CreatedAt)
                    .Select(u => u.Email)
                    .FirstOrDefault(),
                Agents = db.Users.IgnoreQueryFilters()
                    .Count(u => u.TenantId == t.Id && u.IsActive && u.Role != Role.Viewer),
                TicketsThisMonth = db.UsageCounters.IgnoreQueryFilters()
                    .Where(c => c.TenantId == t.Id && c.Period == period)
                    .Select(c => c.TicketsCreated)
                    .FirstOrDefault(),
            };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            rows = rows.Where(r => r.Name.ToLower().Contains(term)
                || r.Slug.Contains(term)
                || (r.OwnerEmail != null && r.OwnerEmail.Contains(term)));
        }

        if (filter.Status is { } status)
        {
            rows = rows.Where(r => r.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.PlanCode))
        {
            rows = rows.Where(r => r.PlanCode == filter.PlanCode);
        }

        rows = filter.Sort switch
        {
            AdminTenantSort.Oldest => rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
            AdminTenantSort.Name => rows.OrderBy(r => r.Name).ThenBy(r => r.Id),
            AdminTenantSort.Revenue => rows.OrderByDescending(r => r.PriceCentsMonthly).ThenByDescending(r => r.CreatedAt),
            _ => rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id),
        };

        var page = await rows.ToPagedAsync(filter, cancellationToken);
        return new PagedResult<AdminTenantListItem>(
            page.Items.Select(r => new AdminTenantListItem(
                r.Id, r.Name, r.Slug, r.Status, r.PlanCode, r.PlanName, r.PriceCentsMonthly, r.Agents,
                r.TicketsThisMonth, r.OwnerEmail, r.CreatedAt)).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }

    public async Task<AdminTenantDetail?> Tenant(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var subscription = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        var plan = await db.Plans.AsNoTracking()
            .FirstAsync(p => p.Code == (subscription == null ? Plan.Free : subscription.PlanCode), cancellationToken);
        var period = UsageCounter.PeriodOf(DateTimeOffset.UtcNow);

        var users = db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.TenantId == tenantId);
        var tickets = db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(t => t.TenantId == tenantId && t.DeletedAt == null);
        var usage = new AdminTenantUsage(
            await users.CountAsync(u => u.IsActive && u.Role != Role.Viewer, cancellationToken),
            plan.MaxAgents,
            await db.UsageCounters.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.Period == period)
                .Select(c => c.TicketsCreated)
                .FirstOrDefaultAsync(cancellationToken),
            plan.MaxTicketsPerMonth,
            (await db.Attachments.IgnoreQueryFilters().Where(a => a.TenantId == tenantId)
                .SumAsync(a => (long?)a.SizeBytes, cancellationToken) ?? 0) + tenant.LogoSizeBytes,
            plan.MaxStorageBytes,
            await db.AutomationRules.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId, cancellationToken),
            plan.MaxAutomationRules,
            await db.ApiKeys.IgnoreQueryFilters()
                .CountAsync(k => k.TenantId == tenantId && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > DateTimeOffset.UtcNow), cancellationToken),
            await db.WebhookEndpoints.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantId, cancellationToken));

        var totals = new AdminTenantTotals(
            await tickets.CountAsync(cancellationToken),
            await tickets.CountAsync(t => Unresolved.Contains(t.Status), cancellationToken),
            await db.Customers.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId, cancellationToken),
            await db.KbArticles.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId, cancellationToken),
            await tickets.MaxAsync(t => (DateTimeOffset?)t.LastActivityAt, cancellationToken));

        var admins = await users
            .Where(u => u.Role == Role.Owner || u.Role == Role.Admin)
            .OrderBy(u => u.Role)
            .ThenBy(u => u.FullName)
            .Select(u => new AdminTenantPerson(u.FullName, u.Email, u.Role.ToString(), u.IsActive, u.LastLoginAt))
            .ToListAsync(cancellationToken);

        var invoices = (await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.TenantId == tenantId)
                .OrderByDescending(i => i.IssuedAt)
                .Take(5)
                .ToListAsync(cancellationToken))
            .Select(i => new AdminInvoice(Invoice.FormatNumber(i.Number), i.PlanName, i.AmountCents, i.Currency, i.IssuedAt))
            .ToList();

        return new AdminTenantDetail(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Status,
            tenant.SuspendedAt,
            tenant.SuspensionReason,
            tenant.Timezone,
            tenant.DefaultLanguage,
            tenant.OnboardingCompletedAt is not null,
            tenant.CreatedAt,
            plan.Code,
            plan.Name,
            plan.PriceCentsMonthly,
            subscription?.CurrentPeriodEnd,
            usage,
            totals,
            admins,
            invoices);
    }

    public async Task<PlatformOverview> Overview(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Select(t => new
            {
                t.Status,
                t.CreatedAt,
                PlanCode = db.Subscriptions.IgnoreQueryFilters()
                    .Where(s => s.TenantId == t.Id)
                    .Select(s => s.PlanCode)
                    .FirstOrDefault() ?? Plan.Free,
            })
            .ToListAsync(cancellationToken);
        var plans = await db.Plans.AsNoTracking().OrderBy(p => p.Position).ToListAsync(cancellationToken);

        // Revenue counts workspaces that can use what they pay for; a suspended one cannot.
        var active = tenants.Where(t => t.Status == TenantStatus.Active).ToList();
        var shares = plans
            .Select(plan =>
            {
                var onPlan = active.Count(t => t.PlanCode == plan.Code);
                return new PlanShare(plan.Code, plan.Name, onPlan, onPlan * plan.PriceCentsMonthly);
            })
            .ToList();

        return new PlatformOverview(
            tenants.Count,
            active.Count,
            tenants.Count - active.Count,
            shares.Where(s => s.MrrCents > 0).Sum(s => s.Workspaces),
            shares.Sum(s => s.MrrCents),
            Plan.Currency,
            tenants.Count(t => t.CreatedAt >= now.AddDays(-30)),
            shares);
    }

    public async Task<IReadOnlyList<MonthlyPoint>> Mrr(int months, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // At each month's end (or now, for the current month), every workspace's newest invoice that was
        // already issued counts while the month it paid for is still running.
        var rows = await db.Database.SqlQuery<MonthlyRow>(
            $"""
            WITH months AS (
                SELECT month_start,
                       LEAST(month_start + interval '1 month', {now}) AS measured_at
                FROM generate_series(
                    date_trunc('month', {now}, 'UTC') - make_interval(months => {months - 1}),
                    date_trunc('month', {now}, 'UTC'),
                    interval '1 month') AS month_start
            )
            SELECT (m.month_start AT TIME ZONE 'UTC')::date AS month,
                   COALESCE((
                       SELECT SUM(latest.amount_cents)
                       FROM (
                           SELECT DISTINCT ON (i.tenant_id) i.amount_cents, i.period_end
                           FROM invoices i
                           WHERE i.issued_at <= m.measured_at
                           ORDER BY i.tenant_id, i.issued_at DESC
                       ) AS latest
                       WHERE latest.period_end > m.measured_at
                   ), 0)::int AS value
            FROM months m
            ORDER BY m.month_start
            """).ToListAsync(cancellationToken);
        return rows.Select(r => new MonthlyPoint(r.Month, r.Value)).ToList();
    }

    public async Task<IReadOnlyList<MonthlyPoint>> Signups(int months, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<MonthlyRow>(
            $"""
            SELECT (m.month_start AT TIME ZONE 'UTC')::date AS month,
                   (SELECT count(*) FROM tenants t
                    WHERE t.created_at >= m.month_start AND t.created_at < m.month_start + interval '1 month')::int AS value
            FROM generate_series(
                date_trunc('month', {now}, 'UTC') - make_interval(months => {months - 1}),
                date_trunc('month', {now}, 'UTC'),
                interval '1 month') AS m(month_start)
            ORDER BY m.month_start
            """).ToListAsync(cancellationToken);
        return rows.Select(r => new MonthlyPoint(r.Month, r.Value)).ToList();
    }

    private sealed record MonthlyRow(DateOnly Month, int Value);
}
