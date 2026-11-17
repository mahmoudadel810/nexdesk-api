using NexDesk.Api.Auth;
using NexDesk.Api.Middleware;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Tickets;

namespace NexDesk.Api.Endpoints;

public sealed record PublicListTicketsRequest(
    TicketStatus[]? Status,
    TicketPriority[]? Priority,
    string? Search,
    string? Sort,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

/// <summary>A ticket opened by an integration. The requester is matched by email or created.</summary>
public sealed record PublicCreateTicketRequest(
    string Subject,
    string Description,
    string RequesterEmail,
    string? RequesterName = null,
    TicketPriority Priority = TicketPriority.Normal,
    TicketType Type = TicketType.Question,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

/// <summary>Only the fields that are present are changed.</summary>
public sealed record PublicUpdateTicketRequest(
    string? Subject = null,
    TicketStatus? Status = null,
    TicketPriority? Priority = null,
    TicketType? Type = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyDictionary<string, string?>? CustomFields = null);

/// <summary>A reply the customer sees, or with <c>isInternal</c> a note only the team sees.</summary>
public sealed record PublicAddMessageRequest(string Body, bool IsInternal = false, TicketStatus? SetStatus = null);

/// <summary>
/// The API integrations call with an API key in the <c>X-Api-Key</c> header. Each endpoint needs a scope, and
/// the workspace's plan must include API access.
/// </summary>
public static class PublicApiEndpoints
{
    public static IEndpointRouteBuilder MapPublicApiEndpoints(this IEndpointRouteBuilder app)
    {
        var tickets = app.MapGroup(TenantResolutionMiddleware.PublicApiPath + "/tickets")
            .WithTags("Public API")
            .AddEndpointFilter(async (context, next) =>
                await context.HttpContext.RequestServices.GetRequiredService<IEntitlementService>()
                    .RequireApiAccess(context.HttpContext.RequestAborted) is { } limit
                    ? limit.ToProblem()
                    : await next(context));

        tickets.MapGet("/", async (
            [AsParameters] PublicListTicketsRequest request,
            IQueryHandler<ListTicketsQuery, PagedResult<TicketListItem>> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new ListTicketsQuery(
                    request.Status, request.Priority, null, null, null, null, null, request.Search, request.Sort,
                    request.Page, request.PageSize),
                ct)).ToOk())
            .RequireApiScope(ApiScopes.TicketsRead)
            .WithName("PublicListTickets")
            .WithSummary("Lists tickets, newest activity first. Needs the tickets:read scope.")
            .Produces<PagedResult<TicketListItem>>()
            .ProducesValidationProblem();

        tickets.MapGet("/{id:guid}", async (Guid id, IQueryHandler<GetTicketQuery, TicketDetailDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetTicketQuery(id), ct)).ToOk())
            .RequireApiScope(ApiScopes.TicketsRead)
            .WithName("PublicGetTicket")
            .WithSummary("Returns one ticket. Needs the tickets:read scope.")
            .Produces<TicketDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapPost("/", async (
            PublicCreateTicketRequest request,
            ICommandHandler<CreateTicketCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new CreateTicketCommand(
                    request.Subject,
                    request.Description,
                    null,
                    request.RequesterEmail,
                    request.RequesterName,
                    request.Priority,
                    request.Type,
                    null,
                    null,
                    request.Tags,
                    null,
                    request.CustomFields),
                ct)).ToHttp(ticket => TypedResults.Created($"{TenantResolutionMiddleware.PublicApiPath}/tickets/{ticket.Id}", ticket)))
            .RequireApiScope(ApiScopes.TicketsWrite)
            .WithName("PublicCreateTicket")
            .WithSummary("Opens a ticket for a customer, on the API channel. Needs the tickets:write scope.")
            .Produces<TicketDetailDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status402PaymentRequired);

        tickets.MapPatch("/{id:guid}", async (
            Guid id,
            PublicUpdateTicketRequest request,
            ICommandHandler<UpdateTicketCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new UpdateTicketCommand(
                    id, request.Subject, request.Status, request.Priority, request.Type, request.Tags, request.CustomFields),
                ct)).ToOk())
            .RequireApiScope(ApiScopes.TicketsWrite)
            .WithName("PublicUpdateTicket")
            .WithSummary("Changes a ticket's subject, status, priority, type, tags or custom fields. Needs the tickets:write scope.")
            .Produces<TicketDetailDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapPost("/{id:guid}/messages", async (
            Guid id,
            PublicAddMessageRequest request,
            ICommandHandler<AddTicketMessageCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new AddTicketMessageCommand(id, request.Body, request.IsInternal, null, request.SetStatus), ct))
            .ToOk())
            .RequireApiScope(ApiScopes.TicketsWrite)
            .WithName("PublicAddTicketMessage")
            .WithSummary("Adds a reply or an internal note to a ticket. Needs the tickets:write scope.")
            .Produces<TicketDetailDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
