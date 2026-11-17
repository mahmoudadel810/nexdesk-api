using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Identity;

namespace NexDesk.Application.Billing;

public sealed record PlanDto(
    string Code,
    string Name,
    int PriceCentsMonthly,
    string Currency,
    int? MaxAgents,
    int? MaxTicketsPerMonth,
    long MaxStorageBytes,
    int? MaxAutomationRules,
    bool ApiAccess,
    int RequestsPerMinute)
{
    public static PlanDto From(Plan plan) => new(
        plan.Code,
        plan.Name,
        plan.PriceCentsMonthly,
        Plan.Currency,
        plan.MaxAgents,
        plan.MaxTicketsPerMonth,
        plan.MaxStorageBytes,
        plan.MaxAutomationRules,
        plan.ApiAccess,
        plan.RequestsPerMinute);
}

/// <summary>How much of each allowance a workspace has used. A limit of null means unlimited.</summary>
public sealed record UsageDto(
    int Agents,
    int? MaxAgents,
    int TicketsThisMonth,
    int? MaxTicketsPerMonth,
    long StorageBytes,
    long MaxStorageBytes,
    int AutomationRules,
    int? MaxAutomationRules,
    bool ApiAccess);

public static class PlanErrors
{
    public static readonly Error AgentLimit = Error.LimitReached(
        "plan-limit-agents", "Your plan's team member limit is reached. Upgrade to add more people.");

    public static readonly Error TicketLimit = Error.LimitReached(
        "plan-limit-tickets", "Your plan's monthly ticket limit is reached. Upgrade to keep creating tickets.");

    public static readonly Error StorageLimit = Error.LimitReached(
        "plan-limit-storage", "Your plan's attachment storage is full. Upgrade to upload more files.");

    public static readonly Error AutomationLimit = Error.LimitReached(
        "plan-limit-automation", "Your plan's automation rule limit is reached. Upgrade to add more rules.");

    public static readonly Error ApiAccess = Error.LimitReached(
        "plan-feature-api", "API keys and webhooks are not part of your plan. Upgrade to use them.");
}

/// <summary>The single place that answers "may this workspace do that?" from its plan and usage.</summary>
public interface IEntitlementService
{
    Task<Plan> CurrentPlan(CancellationToken cancellationToken);

    Task<UsageDto> Usage(CancellationToken cancellationToken);

    /// <summary>Whether one more person who works on tickets fits. Viewers are free.</summary>
    Task<Error?> CanAddAgent(CancellationToken cancellationToken);

    Task<Error?> CanCreateTicket(CancellationToken cancellationToken);

    Task<Error?> CanStore(long bytes, CancellationToken cancellationToken);

    Task<Error?> CanAddAutomationRule(CancellationToken cancellationToken);

    Task<Error?> RequireApiAccess(CancellationToken cancellationToken);
}

internal sealed class EntitlementService(IAppDbContext db, ITenantContext tenant, TimeProvider clock) : IEntitlementService
{
    private Plan? _plan;

    public async Task<Plan> CurrentPlan(CancellationToken cancellationToken)
    {
        if (_plan is not null)
        {
            return _plan;
        }

        var code = await db.Subscriptions.AsNoTracking()
            .Select(s => s.PlanCode)
            .FirstOrDefaultAsync(cancellationToken) ?? Plan.Free;
        _plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Code == code, cancellationToken);
        return _plan;
    }

    public async Task<UsageDto> Usage(CancellationToken cancellationToken)
    {
        var plan = await CurrentPlan(cancellationToken);
        return new UsageDto(
            await Agents(cancellationToken),
            plan.MaxAgents,
            await TicketsThisMonth(cancellationToken),
            plan.MaxTicketsPerMonth,
            await StorageBytes(cancellationToken),
            plan.MaxStorageBytes,
            await db.AutomationRules.CountAsync(cancellationToken),
            plan.MaxAutomationRules,
            plan.ApiAccess);
    }

    public async Task<Error?> CanAddAgent(CancellationToken cancellationToken)
    {
        var plan = await CurrentPlan(cancellationToken);
        return plan.MaxAgents is { } limit && await Agents(cancellationToken) >= limit ? PlanErrors.AgentLimit : null;
    }

    public async Task<Error?> CanCreateTicket(CancellationToken cancellationToken)
    {
        var plan = await CurrentPlan(cancellationToken);
        return plan.MaxTicketsPerMonth is { } limit && await TicketsThisMonth(cancellationToken) >= limit
            ? PlanErrors.TicketLimit
            : null;
    }

    public async Task<Error?> CanStore(long bytes, CancellationToken cancellationToken)
    {
        var plan = await CurrentPlan(cancellationToken);
        return await StorageBytes(cancellationToken) + bytes > plan.MaxStorageBytes ? PlanErrors.StorageLimit : null;
    }

    public async Task<Error?> CanAddAutomationRule(CancellationToken cancellationToken)
    {
        var plan = await CurrentPlan(cancellationToken);
        return plan.MaxAutomationRules is { } limit && await db.AutomationRules.CountAsync(cancellationToken) >= limit
            ? PlanErrors.AutomationLimit
            : null;
    }

    public async Task<Error?> RequireApiAccess(CancellationToken cancellationToken) =>
        (await CurrentPlan(cancellationToken)).ApiAccess ? null : PlanErrors.ApiAccess;

    /// <summary>Active people who work on tickets, plus invitations that would add one.</summary>
    private async Task<int> Agents(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var members = await db.Users.CountAsync(u => u.IsActive && u.Role != Role.Viewer, cancellationToken);
        var invited = await db.Invitations.CountAsync(
            i => i.Role != Role.Viewer && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now,
            cancellationToken);
        return members + invited;
    }

    private async Task<int> TicketsThisMonth(CancellationToken cancellationToken)
    {
        var period = UsageCounter.PeriodOf(clock.GetUtcNow());
        return await db.UsageCounters.AsNoTracking()
            .Where(c => c.Period == period)
            .Select(c => c.TicketsCreated)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Attachments plus the workspace logo.</summary>
    private async Task<long> StorageBytes(CancellationToken cancellationToken) =>
        (await db.Attachments.SumAsync(a => (long?)a.SizeBytes, cancellationToken) ?? 0)
        + await db.Tenants.Where(t => t.Id == tenant.TenantId).Select(t => t.LogoSizeBytes).FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Counts what a workspace consumes against its monthly allowances.</summary>
public interface IUsageMeter
{
    Task TicketCreatedAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class UsageEventHandlers(IUsageMeter meter, TimeProvider clock)
    : IDomainEventHandler<NexDesk.Domain.Tickets.TicketCreated>
{
    public Task Handle(NexDesk.Domain.Tickets.TicketCreated domainEvent, CancellationToken cancellationToken) =>
        meter.TicketCreatedAsync(clock.GetUtcNow(), cancellationToken);
}

/// <summary>Remembers how many requests per minute each workspace's plan allows.</summary>
public interface IPlanRateLimits
{
    ValueTask<int> RequestsPerMinuteAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Forgets a workspace's limit, so a plan change applies to its next request.</summary>
    void Forget(Guid tenantId);
}
