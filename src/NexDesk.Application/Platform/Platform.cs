using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Platform;

public sealed record AdminTenantListItem(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    string PlanCode,
    string PlanName,
    int MonthlyPriceCents,
    int Agents,
    int TicketsThisMonth,
    string? OwnerEmail,
    DateTimeOffset CreatedAt);

public sealed record AdminTenantUsage(
    int Agents,
    int? MaxAgents,
    int TicketsThisMonth,
    int? MaxTicketsPerMonth,
    long StorageBytes,
    long MaxStorageBytes,
    int AutomationRules,
    int? MaxAutomationRules,
    int ActiveApiKeys,
    int Webhooks);

public sealed record AdminTenantTotals(int Tickets, int OpenTickets, int Customers, int KbArticles, DateTimeOffset? LastTicketActivityAt);

public sealed record AdminTenantPerson(string FullName, string Email, string Role, bool IsActive, DateTimeOffset? LastLoginAt);

public sealed record AdminInvoice(string Number, string PlanName, int AmountCents, string Currency, DateTimeOffset IssuedAt);

/// <summary>A read-only look at one workspace. Nothing in it lets the platform act as the workspace.</summary>
public sealed record AdminTenantDetail(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    DateTimeOffset? SuspendedAt,
    string? SuspensionReason,
    string Timezone,
    string DefaultLanguage,
    bool OnboardingCompleted,
    DateTimeOffset CreatedAt,
    string PlanCode,
    string PlanName,
    int MonthlyPriceCents,
    DateTimeOffset? CurrentPeriodEnd,
    AdminTenantUsage Usage,
    AdminTenantTotals Totals,
    IReadOnlyList<AdminTenantPerson> Admins,
    IReadOnlyList<AdminInvoice> RecentInvoices);

public sealed record PlanShare(string PlanCode, string PlanName, int Workspaces, int MrrCents);

public sealed record PlatformOverview(
    int Workspaces,
    int ActiveWorkspaces,
    int SuspendedWorkspaces,
    int PayingWorkspaces,
    int MrrCents,
    string Currency,
    int SignupsLast30Days,
    IReadOnlyList<PlanShare> Plans);

/// <summary>A month's value: revenue under contract at its end, or workspaces created in it.</summary>
public sealed record MonthlyPoint(DateOnly Month, int Value);

public enum AdminTenantSort
{
    Newest,
    Oldest,
    Name,
    Revenue,
}

public sealed record AdminTenantFilter(
    string? Search,
    TenantStatus? Status,
    string? PlanCode,
    AdminTenantSort Sort,
    int Page,
    int PageSize) : IPagedRequest;

/// <summary>Reads across all workspaces for the platform console. Nothing else may.</summary>
public interface IPlatformQueries
{
    Task<PagedResult<AdminTenantListItem>> Tenants(AdminTenantFilter filter, CancellationToken cancellationToken);

    Task<AdminTenantDetail?> Tenant(Guid tenantId, CancellationToken cancellationToken);

    Task<PlatformOverview> Overview(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>For each of the last <paramref name="months"/> months, revenue under contract at its end, in cents.</summary>
    Task<IReadOnlyList<MonthlyPoint>> Mrr(int months, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>For each of the last <paramref name="months"/> months, how many workspaces signed up.</summary>
    Task<IReadOnlyList<MonthlyPoint>> Signups(int months, DateTimeOffset now, CancellationToken cancellationToken);
}

public static class PlatformErrors
{
    public static readonly Error TenantNotFound = Error.NotFound("tenant-not-found", "The workspace was not found.");
}

public sealed record ListAdminTenantsQuery(AdminTenantFilter Filter) : IQuery<PagedResult<AdminTenantListItem>>;

internal sealed class ListAdminTenantsValidator : AbstractValidator<ListAdminTenantsQuery>
{
    public ListAdminTenantsValidator()
    {
        RuleFor(q => q.Filter.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.Filter.PageSize).InclusiveBetween(1, Paging.MaxPageSize);
        RuleFor(q => q.Filter.Search).MaximumLength(100);
        RuleFor(q => q.Filter.Status).IsInEnum();
        RuleFor(q => q.Filter.Sort).IsInEnum();
        RuleFor(q => q.Filter.PlanCode).MaximumLength(32);
    }
}

internal sealed class ListAdminTenantsHandler(IPlatformQueries queries)
    : IQueryHandler<ListAdminTenantsQuery, PagedResult<AdminTenantListItem>>
{
    public async Task<Result<PagedResult<AdminTenantListItem>>> Handle(ListAdminTenantsQuery query, CancellationToken cancellationToken) =>
        await queries.Tenants(query.Filter, cancellationToken);
}

public sealed record GetAdminTenantQuery(Guid TenantId) : IQuery<AdminTenantDetail>;

internal sealed class GetAdminTenantHandler(IPlatformQueries queries) : IQueryHandler<GetAdminTenantQuery, AdminTenantDetail>
{
    public async Task<Result<AdminTenantDetail>> Handle(GetAdminTenantQuery query, CancellationToken cancellationToken) =>
        await queries.Tenant(query.TenantId, cancellationToken) is { } detail ? detail : PlatformErrors.TenantNotFound;
}

public sealed record PlatformMetrics(PlatformOverview Overview, IReadOnlyList<MonthlyPoint> Mrr, IReadOnlyList<MonthlyPoint> Signups);

public sealed record GetPlatformMetricsQuery(int Months) : IQuery<PlatformMetrics>;

internal sealed class GetPlatformMetricsValidator : AbstractValidator<GetPlatformMetricsQuery>
{
    public GetPlatformMetricsValidator() => RuleFor(q => q.Months).InclusiveBetween(1, 36);
}

internal sealed class GetPlatformMetricsHandler(IPlatformQueries queries, TimeProvider clock)
    : IQueryHandler<GetPlatformMetricsQuery, PlatformMetrics>
{
    public async Task<Result<PlatformMetrics>> Handle(GetPlatformMetricsQuery query, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return new PlatformMetrics(
            await queries.Overview(now, cancellationToken),
            await queries.Mrr(query.Months, now, cancellationToken),
            await queries.Signups(query.Months, now, cancellationToken));
    }
}

/// <summary>Suspends or reopens a workspace. The workspace's own audit log records it as done by NexDesk.</summary>
public sealed record SetTenantStatusCommand(Guid TenantId, bool Suspend, string? Reason) : ICommand<AdminTenantDetail>;

internal sealed class SetTenantStatusValidator : AbstractValidator<SetTenantStatusCommand>
{
    public SetTenantStatusValidator() => RuleFor(c => c.Reason).MaximumLength(Tenant.SuspensionReasonMaxLength);
}

internal sealed class SetTenantStatusHandler(
    IAppDbContext db,
    ITenantScopeFactory scopes,
    ISessionStateCache sessions,
    IPlatformQueries queries,
    TimeProvider clock) : ICommandHandler<SetTenantStatusCommand, AdminTenantDetail>
{
    public async Task<Result<AdminTenantDetail>> Handle(SetTenantStatusCommand command, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == command.TenantId, cancellationToken))
        {
            return PlatformErrors.TenantNotFound;
        }

        using (scopes.Begin(command.TenantId))
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == command.TenantId, cancellationToken);
            if (command.Suspend)
            {
                tenant.Suspend(command.Reason, clock.GetUtcNow());
            }
            else
            {
                tenant.Reactivate();
            }

            await db.SaveChangesAsync(cancellationToken);

            // Signed-in people find out on their next request rather than when their cached check expires.
            foreach (var userId in await db.Users.Select(u => u.Id).ToListAsync(cancellationToken))
            {
                sessions.Invalidate(userId);
            }

            foreach (var customerId in await db.Customers.Select(c => c.Id).ToListAsync(cancellationToken))
            {
                sessions.InvalidateCustomer(customerId);
            }
        }

        return (await queries.Tenant(command.TenantId, cancellationToken))!;
    }
}
