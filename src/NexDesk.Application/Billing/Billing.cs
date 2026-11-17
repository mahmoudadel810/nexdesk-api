using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Identity;

namespace NexDesk.Application.Billing;

public sealed record SubscriptionDto(
    PlanDto Plan,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    string? CardBrand,
    string? CardLast4);

public sealed record InvoiceDto(
    Guid Id,
    string Number,
    string PlanCode,
    string PlanName,
    InvoiceReason Reason,
    int AmountCents,
    string Currency,
    DateTimeOffset IssuedAt,
    DateTimeOffset PaidAt,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string? CardBrand,
    string? CardLast4)
{
    public static InvoiceDto From(Invoice invoice) => new(
        invoice.Id,
        invoice.DisplayNumber,
        invoice.PlanCode,
        invoice.PlanName,
        invoice.Reason,
        invoice.AmountCents,
        invoice.Currency,
        invoice.IssuedAt,
        invoice.PaidAt,
        invoice.PeriodStart,
        invoice.PeriodEnd,
        invoice.CardBrand,
        invoice.CardLast4);
}

public sealed record InvoiceDocument(string FileName, byte[] Content);

/// <summary>Everything printed on an invoice.</summary>
public sealed record InvoicePrintModel(
    string Number,
    string WorkspaceName,
    string BilledToName,
    string BilledToEmail,
    string PlanName,
    int AmountCents,
    string Currency,
    DateTimeOffset IssuedAt,
    DateTimeOffset PaidAt,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string? CardBrand,
    string? CardLast4);

public interface IInvoiceRenderer
{
    byte[] RenderPdf(InvoicePrintModel invoice);
}

public static class BillingErrors
{
    public static readonly Error PlanNotFound = Error.NotFound("plan-not-found", "This plan does not exist.");

    public static readonly Error InvoiceNotFound = Error.NotFound("invoice-not-found", "The invoice was not found.");

    public static readonly Error AlreadyOnPlan = Error.Conflict("already-on-plan", "The workspace is already on this plan.");

    public static readonly Error InvalidCard = Error.Validation("invalid-card", "Check the card number and expiry date.");

    public static readonly Error CardDeclined = Error.Conflict("card-declined", "The card was declined. Try another card.");

    public static readonly Error TooManyAgents = Error.Conflict(
        "downgrade-agents", "This plan allows fewer team members than you have. Deactivate people or revoke invitations first.");

    public static readonly Error TooMuchStorage = Error.Conflict(
        "downgrade-storage", "Your attachments need more storage than this plan includes.");

    public static readonly Error TooManyRules = Error.Conflict(
        "downgrade-automation", "This plan allows fewer automation rules than you have. Delete some rules first.");
}

/// <summary>Issues invoices and moves subscriptions between periods.</summary>
public sealed class BillingService(IAppDbContext db, ITenantContext tenant)
{
    public async Task<Subscription> SubscriptionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            subscription = Subscription.Start(now);
            db.Subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Adds a paid invoice for the subscription's current period. The caller saves.</summary>
    public async Task<Invoice> IssueInvoiceAsync(
        Plan plan,
        Subscription subscription,
        InvoiceReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => t.Name)
            .FirstAsync(cancellationToken);
        var ownerEmail = await db.Users.AsNoTracking()
            .Where(u => u.Role == Role.Owner && u.IsActive)
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var last = await db.Invoices.MaxAsync(i => (int?)i.Number, cancellationToken) ?? 0;
        var invoice = Invoice.Issue(last + 1, plan, subscription, reason, workspace, ownerEmail, now);
        db.Invoices.Add(invoice);
        return invoice;
    }

    /// <summary>Bills the next month of a paid plan whose period has ended. Returns whether it did.</summary>
    public async Task<bool> RenewIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(cancellationToken);
        if (subscription is null || !subscription.IsDueForRenewal(now))
        {
            return false;
        }

        var plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Code == subscription.PlanCode, cancellationToken);
        subscription.Renew();
        await IssueInvoiceAsync(plan, subscription, InvoiceReason.Renewal, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record ListPlansQuery : IQuery<IReadOnlyList<PlanDto>>;

internal sealed class ListPlansHandler(IAppDbContext db) : IQueryHandler<ListPlansQuery, IReadOnlyList<PlanDto>>
{
    public async Task<Result<IReadOnlyList<PlanDto>>> Handle(ListPlansQuery query, CancellationToken cancellationToken)
    {
        var plans = await db.Plans.AsNoTracking().OrderBy(p => p.Position).ToListAsync(cancellationToken);
        return plans.Select(PlanDto.From).ToList();
    }
}

public sealed record GetSubscriptionQuery : IQuery<SubscriptionDto>;

internal sealed class GetSubscriptionHandler(IAppDbContext db, IEntitlementService entitlements, TimeProvider clock)
    : IQueryHandler<GetSubscriptionQuery, SubscriptionDto>
{
    public async Task<Result<SubscriptionDto>> Handle(GetSubscriptionQuery query, CancellationToken cancellationToken)
    {
        var plan = await entitlements.CurrentPlan(cancellationToken);
        var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return new SubscriptionDto(
            PlanDto.From(plan),
            subscription?.CurrentPeriodStart ?? clock.GetUtcNow(),
            subscription?.CurrentPeriodEnd,
            subscription?.CardBrand,
            subscription?.CardLast4);
    }
}

public sealed record GetUsageQuery : IQuery<UsageDto>;

internal sealed class GetUsageHandler(IEntitlementService entitlements) : IQueryHandler<GetUsageQuery, UsageDto>
{
    public async Task<Result<UsageDto>> Handle(GetUsageQuery query, CancellationToken cancellationToken) =>
        await entitlements.Usage(cancellationToken);
}

/// <summary>
/// Moves the workspace to another plan. A paid plan is "bought" with a simulated card payment that
/// produces a paid invoice; no payment provider is involved and the card number is not stored.
/// </summary>
public sealed record CheckoutCommand(
    string PlanCode,
    string? CardholderName,
    string? CardNumber,
    int? ExpiryMonth,
    int? ExpiryYear,
    string? Cvc) : ICommand<SubscriptionDto>;

internal sealed class CheckoutValidator : AbstractValidator<CheckoutCommand>
{
    public CheckoutValidator()
    {
        RuleFor(c => c.PlanCode).NotEmpty().MaximumLength(40);
        RuleFor(c => c.CardholderName).MaximumLength(100);
        RuleFor(c => c.CardNumber).MaximumLength(30);
        RuleFor(c => c.Cvc).Matches("^[0-9]{3,4}$").When(c => !string.IsNullOrEmpty(c.Cvc));
    }
}

internal sealed class CheckoutHandler(
    IAppDbContext db,
    BillingService billing,
    IEntitlementService entitlements,
    IPlanRateLimits rateLimits,
    ITenantContext tenant,
    TimeProvider clock) : ICommandHandler<CheckoutCommand, SubscriptionDto>
{
    public async Task<Result<SubscriptionDto>> Handle(CheckoutCommand command, CancellationToken cancellationToken)
    {
        var code = command.PlanCode.Trim().ToLowerInvariant();
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, cancellationToken);
        if (plan is null)
        {
            return BillingErrors.PlanNotFound;
        }

        var now = clock.GetUtcNow();
        var subscription = await billing.SubscriptionAsync(now, cancellationToken);
        if (subscription.PlanCode == plan.Code)
        {
            return BillingErrors.AlreadyOnPlan;
        }

        if (Fits(plan, await entitlements.Usage(cancellationToken)) is { } tooSmall)
        {
            return tooSmall;
        }

        PaymentCard? card = null;
        if (!plan.IsFree)
        {
            card = PaymentCard.Parse(command.CardNumber);
            if (card is null
                || string.IsNullOrWhiteSpace(command.CardholderName)
                || string.IsNullOrWhiteSpace(command.Cvc)
                || PaymentCard.IsExpired(command.ExpiryMonth ?? 0, command.ExpiryYear ?? 0, now))
            {
                return BillingErrors.InvalidCard;
            }

            if (PaymentCard.IsDeclined(command.CardNumber))
            {
                return BillingErrors.CardDeclined;
            }
        }

        subscription.ChangeTo(plan, card, now);
        if (!plan.IsFree)
        {
            await billing.IssueInvoiceAsync(plan, subscription, InvoiceReason.PlanChange, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (tenant.TenantId is { } tenantId)
        {
            rateLimits.Forget(tenantId);
        }

        return new SubscriptionDto(
            PlanDto.From(plan),
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.CardBrand,
            subscription.CardLast4);
    }

    /// <summary>A plan can only be chosen when what the workspace already uses fits into it.</summary>
    private static Error? Fits(Plan plan, UsageDto usage)
    {
        if (plan.MaxAgents is { } agents && usage.Agents > agents)
        {
            return BillingErrors.TooManyAgents;
        }

        if (usage.StorageBytes > plan.MaxStorageBytes)
        {
            return BillingErrors.TooMuchStorage;
        }

        return plan.MaxAutomationRules is { } rules && usage.AutomationRules > rules ? BillingErrors.TooManyRules : null;
    }
}

public sealed record ListInvoicesQuery(int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<InvoiceDto>>, IPagedRequest;

internal sealed class ListInvoicesValidator : AbstractValidator<ListInvoicesQuery>
{
    public ListInvoicesValidator() => this.AddPagingRules();
}

internal sealed class ListInvoicesHandler(IAppDbContext db) : IQueryHandler<ListInvoicesQuery, PagedResult<InvoiceDto>>
{
    public async Task<Result<PagedResult<InvoiceDto>>> Handle(ListInvoicesQuery query, CancellationToken cancellationToken)
    {
        var page = await db.Invoices.AsNoTracking()
            .OrderByDescending(i => i.Number)
            .ToPagedAsync(query, cancellationToken);
        return new PagedResult<InvoiceDto>(page.Items.Select(InvoiceDto.From).ToList(), page.Page, page.PageSize, page.TotalCount);
    }
}

public sealed record GetInvoicePdfQuery(Guid InvoiceId) : IQuery<InvoiceDocument>;

internal sealed class GetInvoicePdfHandler(IAppDbContext db, ITenantContext tenant, IInvoiceRenderer renderer)
    : IQueryHandler<GetInvoicePdfQuery, InvoiceDocument>
{
    public async Task<Result<InvoiceDocument>> Handle(GetInvoicePdfQuery query, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == query.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var workspace = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => t.Name)
            .FirstAsync(cancellationToken);
        var pdf = renderer.RenderPdf(new InvoicePrintModel(
            invoice.DisplayNumber,
            workspace,
            invoice.BilledToName,
            invoice.BilledToEmail,
            invoice.PlanName,
            invoice.AmountCents,
            invoice.Currency,
            invoice.IssuedAt,
            invoice.PaidAt,
            invoice.PeriodStart,
            invoice.PeriodEnd,
            invoice.CardBrand,
            invoice.CardLast4));
        return new InvoiceDocument($"{invoice.DisplayNumber}.pdf", pdf);
    }
}
