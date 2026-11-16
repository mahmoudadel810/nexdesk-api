using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Sla;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

public sealed record UpdateSlaPoliciesRequest(IReadOnlyList<SlaPolicyDto> Policies);

public sealed record AddHolidayRequest(DateOnly Date, string Name);

public sealed record ListBreachesRequest(int Page = 1, int PageSize = Paging.DefaultPageSize);

public static class SlaEndpoints
{
    public static RouteGroupBuilder MapSlaEndpoints(this RouteGroupBuilder api)
    {
        var sla = api.MapGroup("/sla").WithTags("SLA");

        sla.MapGet("/policies", async (
            IQueryHandler<ListSlaPoliciesQuery, IReadOnlyList<SlaPolicyDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListSlaPoliciesQuery(), ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("ListSlaPolicies")
            .WithSummary("Returns the first-response and resolution targets of each priority, in working minutes.")
            .Produces<IReadOnlyList<SlaPolicyDto>>();

        sla.MapPut("/policies", async (
            UpdateSlaPoliciesRequest request,
            ICommandHandler<UpdateSlaPoliciesCommand, IReadOnlyList<SlaPolicyDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new UpdateSlaPoliciesCommand(request.Policies), ct)).ToOk())
            .RequirePermission(Permissions.SlaManage)
            .WithName("UpdateSlaPolicies")
            .WithSummary("Sets the targets of one or more priorities. Open tickets pick them up on their next change.")
            .Produces<IReadOnlyList<SlaPolicyDto>>()
            .ProducesValidationProblem();

        sla.MapGet("/holidays", async (
            IQueryHandler<ListHolidaysQuery, IReadOnlyList<HolidayDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListHolidaysQuery(), ct)).ToOk())
            .RequirePermission(Permissions.TicketsView)
            .WithName("ListHolidays")
            .WithSummary("Lists the days the workspace is closed.")
            .Produces<IReadOnlyList<HolidayDto>>();

        sla.MapPost("/holidays", async (
            AddHolidayRequest request,
            ICommandHandler<AddHolidayCommand, HolidayDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new AddHolidayCommand(request.Date, request.Name), ct))
            .ToHttp(holiday => TypedResults.Created((string?)null, holiday)))
            .RequirePermission(Permissions.SlaManage)
            .WithName("AddHoliday")
            .WithSummary("Adds a day on which SLA clocks do not run.")
            .Produces<HolidayDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        sla.MapDelete("/holidays/{id:guid}", async (
            Guid id,
            ICommandHandler<DeleteHolidayCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeleteHolidayCommand(id), ct)).ToNoContent())
            .RequirePermission(Permissions.SlaManage)
            .WithName("DeleteHoliday")
            .WithSummary("Removes a holiday.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        sla.MapGet("/breaches", async (
            [AsParameters] ListBreachesRequest request,
            IQueryHandler<ListSlaBreachesQuery, PagedResult<SlaBreachDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListSlaBreachesQuery(request.Page, request.PageSize), ct)).ToOk())
            .RequirePermission(Permissions.ReportsView)
            .WithName("ListSlaBreaches")
            .WithSummary("Lists missed deadlines, newest first.")
            .Produces<PagedResult<SlaBreachDto>>();

        return api;
    }
}
