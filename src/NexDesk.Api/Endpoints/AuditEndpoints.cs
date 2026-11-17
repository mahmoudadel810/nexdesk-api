using NexDesk.Api.Auth;
using NexDesk.Application.Audit;
using NexDesk.Application.Common;
using NexDesk.Domain.Audit;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

/// <summary>Filters for the audit log. <c>from</c> and <c>to</c> are instants, both inclusive.</summary>
public sealed record ListAuditLogsRequest(
    string? EntityType = null,
    AuditOperation? Operation = null,
    Guid? ActorId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audit-logs", async (
            [AsParameters] ListAuditLogsRequest request,
            IQueryHandler<ListAuditLogsQuery, PagedResult<AuditLogDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new ListAuditLogsQuery(
                    request.EntityType,
                    request.Operation,
                    request.ActorId,
                    request.From,
                    request.To,
                    request.Search,
                    request.Page,
                    request.PageSize),
                ct)).ToOk())
            .WithTags("Audit")
            .RequirePermission(Permissions.AuditView)
            .WithName("ListAuditLogs")
            .WithSummary(
                "Lists who created, changed or deleted what in the workspace, newest first, with each field's "
                + "value before and after and the address the change came from. Search matches the record's name, "
                + "or a record or actor id.")
            .Produces<PagedResult<AuditLogDto>>()
            .ProducesValidationProblem();

        return api;
    }
}
