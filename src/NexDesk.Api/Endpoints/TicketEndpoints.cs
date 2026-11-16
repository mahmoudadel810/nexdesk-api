using Microsoft.AspNetCore.Mvc;
using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Api.Endpoints;

public sealed record CreateTicketRequest(
    string Subject,
    string Description,
    Guid? RequesterId,
    string? RequesterEmail,
    string? RequesterName,
    TicketPriority Priority = TicketPriority.Normal,
    TicketType Type = TicketType.Question,
    Guid? AssigneeId = null,
    Guid? TeamId = null,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<Guid>? AttachmentIds = null);

/// <summary>Only the fields that are present are changed.</summary>
public sealed record UpdateTicketRequest(
    string? Subject,
    TicketStatus? Status,
    TicketPriority? Priority,
    TicketType? Type,
    IReadOnlyCollection<string>? Tags);

/// <summary>Both fields are applied as sent; null clears the assignee or the team.</summary>
public sealed record AssignTicketRequest(Guid? AssigneeId, Guid? TeamId);

public sealed record AddMessageRequest(
    string Body,
    bool IsInternal = false,
    IReadOnlyCollection<Guid>? AttachmentIds = null,
    TicketStatus? SetStatus = null);

public sealed record ListTicketsRequest(
    TicketStatus[]? Status,
    TicketPriority[]? Priority,
    Guid? AssigneeId,
    bool? Unassigned,
    Guid? TeamId,
    Guid? RequesterId,
    string? Tag,
    string? Sort,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

public sealed record ListCustomersRequest(string? Search, int Page = 1, int PageSize = Paging.DefaultPageSize);

public static class TicketEndpoints
{
    public static RouteGroupBuilder MapTicketEndpoints(this RouteGroupBuilder api)
    {
        MapTickets(api.MapGroup("/tickets").WithTags("Tickets"));
        MapAttachments(api.MapGroup("/attachments").WithTags("Attachments"));

        api.MapGet("/customers", async (
            [AsParameters] ListCustomersRequest request,
            IQueryHandler<ListCustomersQuery, PagedResult<CustomerDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListCustomersQuery(request.Search, request.Page, request.PageSize), ct)).ToOk())
            .RequirePermission(Permissions.CustomersView)
            .WithTags("Customers")
            .WithName("ListCustomers")
            .WithSummary("Lists the customers of the workspace, optionally filtered by name or email.")
            .Produces<PagedResult<CustomerDto>>();

        return api;
    }

    private static void MapTickets(RouteGroupBuilder tickets)
    {
        tickets.MapGet("/", async (
            [AsParameters] ListTicketsRequest request,
            IQueryHandler<ListTicketsQuery, PagedResult<TicketListItem>> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new ListTicketsQuery(
                    request.Status,
                    request.Priority,
                    request.AssigneeId,
                    request.Unassigned,
                    request.TeamId,
                    request.RequesterId,
                    request.Tag,
                    request.Sort,
                    request.Page,
                    request.PageSize),
                ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("ListTickets")
            .WithSummary("Lists tickets. Repeat status or priority to match several values. "
                + "Sort by lastActivityAt, createdAt, number or priority; prefix with - to reverse.")
            .Produces<PagedResult<TicketListItem>>()
            .ProducesValidationProblem();

        tickets.MapPost("/", async (
            CreateTicketRequest request,
            ICommandHandler<CreateTicketCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new CreateTicketCommand(
                    request.Subject,
                    request.Description,
                    request.RequesterId,
                    request.RequesterEmail,
                    request.RequesterName,
                    request.Priority,
                    request.Type,
                    request.AssigneeId,
                    request.TeamId,
                    request.Tags,
                    request.AttachmentIds),
                ct)).ToHttp(ticket => TypedResults.Created($"/api/v1/tickets/{ticket.Id}", ticket)))
            .RequirePermission(Permissions.TicketsCreate)
            .WithName("CreateTicket")
            .WithSummary("Creates a ticket on behalf of a customer. The requester is matched by email or created.")
            .Produces<TicketDetailDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        tickets.MapGet("/{id:guid}", async (Guid id, IQueryHandler<GetTicketQuery, TicketDetailDto> handler, CancellationToken ct) =>
            (await handler.Handle(new GetTicketQuery(id), ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("GetTicket")
            .WithSummary("Returns one ticket with its requester, assignee, team and watchers.")
            .Produces<TicketDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateTicketRequest request,
            ICommandHandler<UpdateTicketCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new UpdateTicketCommand(id, request.Subject, request.Status, request.Priority, request.Type, request.Tags),
                ct)).ToOk())
            .RequirePermission(Permissions.TicketsEdit)
            .WithName("UpdateTicket")
            .WithSummary("Changes the subject, status, priority, type or tags. Omitted fields stay as they are.")
            .Produces<TicketDetailDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tickets.MapPut("/{id:guid}/assignment", async (
            Guid id,
            AssignTicketRequest request,
            ICommandHandler<AssignTicketCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new AssignTicketCommand(id, request.AssigneeId, request.TeamId), ct)).ToOk())
            .RequirePermission(Permissions.TicketsEdit)
            .WithName("AssignTicket")
            .WithSummary("Sets the assignee and team of a ticket. Send null to clear either one.")
            .Produces<TicketDetailDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapPost("/{id:guid}/messages", async (
            Guid id,
            AddMessageRequest request,
            ICommandHandler<AddTicketMessageCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new AddTicketMessageCommand(id, request.Body, request.IsInternal, request.AttachmentIds, request.SetStatus),
                ct)).ToOk())
            .RequirePermission(Permissions.TicketsReply)
            .WithName("AddTicketMessage")
            .WithSummary("Adds a public reply or an internal note, optionally changing the status in the same step.")
            .Produces<TicketDetailDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tickets.MapGet("/{id:guid}/timeline", async (
            Guid id,
            IQueryHandler<GetTicketTimelineQuery, IReadOnlyList<TimelineItem>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetTicketTimelineQuery(id), ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("GetTicketTimeline")
            .WithSummary("Returns the conversation and every recorded change of a ticket, oldest first.")
            .Produces<IReadOnlyList<TimelineItem>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapPut("/{id:guid}/watchers/me", async (
            Guid id,
            ICommandHandler<SetWatchingCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetWatchingCommand(id, Watching: true), ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("WatchTicket")
            .WithSummary("Subscribes the signed-in user to updates of a ticket.")
            .Produces<TicketDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapDelete("/{id:guid}/watchers/me", async (
            Guid id,
            ICommandHandler<SetWatchingCommand, TicketDetailDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetWatchingCommand(id, Watching: false), ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("UnwatchTicket")
            .WithSummary("Unsubscribes the signed-in user from updates of a ticket.")
            .Produces<TicketDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        tickets.MapDelete("/{id:guid}", async (
            Guid id,
            ICommandHandler<DeleteTicketCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeleteTicketCommand(id), ct)).ToNoContent())
            .RequirePermission(Permissions.TicketsDelete)
            .WithName("DeleteTicket")
            .WithSummary("Deletes a ticket. It disappears from every list but its number is never reused.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapAttachments(RouteGroupBuilder attachments)
    {
        attachments.MapPost("/", async (
            IFormFile file,
            ICommandHandler<UploadAttachmentCommand, AttachmentDto> handler,
            CancellationToken ct) =>
        {
            await using var content = file.OpenReadStream();
            var result = await handler.Handle(
                new UploadAttachmentCommand(file.FileName, file.ContentType, file.Length, content),
                ct);
            return result.ToHttp(attachment => TypedResults.Created($"/api/v1/attachments/{attachment.Id}", attachment));
        })
            .RequirePermission(Permissions.TicketsReply)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(Attachment.MaxSizeBytes + (64 * 1024)))
            .WithName("UploadAttachment")
            .WithSummary("Uploads one file of up to 10 MB. Pass the returned id when creating a ticket or message.")
            .Produces<AttachmentDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        attachments.MapGet("/{id:guid}", async Task<IResult> (
            Guid id,
            HttpContext http,
            IQueryHandler<DownloadAttachmentQuery, AttachmentContent> handler,
            CancellationToken ct) =>
        {
            var result = await handler.Handle(new DownloadAttachmentQuery(id), ct);
            if (!result.IsSuccess)
            {
                return result.Error.ToProblem();
            }

            // Served as a download with sniffing off, so an uploaded HTML or SVG file can never run as a page.
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return TypedResults.File(result.Value.Content, result.Value.ContentType, result.Value.FileName);
        })
            .RequirePermission(Permissions.TicketsView)
            .WithName("DownloadAttachment")
            .WithSummary("Downloads an attachment.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
