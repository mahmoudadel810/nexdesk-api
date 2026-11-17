using NexDesk.Api.Auth;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

/// <summary>The card fields belong to a simulated payment form: nothing is charged and the number is not stored.</summary>
public sealed record CheckoutRequest(
    string PlanCode,
    string? CardholderName = null,
    string? CardNumber = null,
    int? ExpiryMonth = null,
    int? ExpiryYear = null,
    string? Cvc = null);

public sealed record ListInvoicesRequest(int Page = 1, int PageSize = Paging.DefaultPageSize);

public static class BillingEndpoints
{
    public static RouteGroupBuilder MapBillingEndpoints(this RouteGroupBuilder api)
    {
        var billing = api.MapGroup("/billing").WithTags("Billing");

        billing.MapGet("/plans", async (IQueryHandler<ListPlansQuery, IReadOnlyList<PlanDto>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListPlansQuery(), ct)).ToOk())
            .AllowAnonymous()
            .WithName("ListPlans")
            .WithSummary("Lists the plans with their prices and limits. A limit of null means unlimited.")
            .Produces<IReadOnlyList<PlanDto>>();

        billing.MapGet("/subscription", async (
            IQueryHandler<GetSubscriptionQuery, SubscriptionDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetSubscriptionQuery(), ct)).ToOk())
            .RequirePermission(Permissions.SettingsManage)
            .WithName("GetSubscription")
            .WithSummary("Returns the workspace's plan and the period it is paid for.")
            .Produces<SubscriptionDto>();

        billing.MapGet("/usage", async (IQueryHandler<GetUsageQuery, UsageDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetUsageQuery(), ct)).ToOk())
            .RequirePermission(Permissions.SettingsManage)
            .WithName("GetUsage")
            .WithSummary("Returns how much of each plan allowance the workspace has used.")
            .Produces<UsageDto>();

        billing.MapPost("/checkout", async (
            CheckoutRequest request,
            ICommandHandler<CheckoutCommand, SubscriptionDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new CheckoutCommand(
                    request.PlanCode,
                    request.CardholderName,
                    request.CardNumber,
                    request.ExpiryMonth,
                    request.ExpiryYear,
                    request.Cvc),
                ct))
            .ToOk())
            .RequirePermission(Permissions.BillingManage)
            .WithName("Checkout")
            .WithSummary("Moves the workspace to another plan. Paid plans take a simulated card payment and produce "
                + "a paid invoice; card 4000 0000 0000 0002 is always declined.")
            .Produces<SubscriptionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        billing.MapGet("/invoices", async (
            [AsParameters] ListInvoicesRequest request,
            IQueryHandler<ListInvoicesQuery, PagedResult<InvoiceDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListInvoicesQuery(request.Page, request.PageSize), ct)).ToOk())
            .RequirePermission(Permissions.BillingManage)
            .WithName("ListInvoices")
            .WithSummary("Lists the workspace's invoices, newest first.")
            .Produces<PagedResult<InvoiceDto>>()
            .ProducesValidationProblem();

        billing.MapGet("/invoices/{id:guid}/pdf", async (
            Guid id,
            IQueryHandler<GetInvoicePdfQuery, InvoiceDocument> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetInvoicePdfQuery(id), ct))
            .ToHttp(document => TypedResults.File(document.Content, "application/pdf", document.FileName)))
            .RequirePermission(Permissions.BillingManage)
            .WithName("DownloadInvoice")
            .WithSummary("Downloads an invoice as a PDF.")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }
}
