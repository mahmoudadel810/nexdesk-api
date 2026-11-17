using Microsoft.AspNetCore.Http.HttpResults;
using NexDesk.Api.Admin;
using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Platform;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Endpoints;

public sealed record ListAdminTenantsRequest(
    string? Search = null,
    TenantStatus? Status = null,
    string? Plan = null,
    AdminTenantSort Sort = AdminTenantSort.Newest,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

/// <param name="Reason">Why the workspace is closed, kept for the platform team. Up to 500 characters.</param>
public sealed record SuspendTenantRequest(string? Reason = null);

public sealed record JobsDashboardLink(string Url);

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Platform admin").RequirePlatformAdmin();

        admin.MapGet("/tenants", async (
            [AsParameters] ListAdminTenantsRequest request,
            IQueryHandler<ListAdminTenantsQuery, PagedResult<AdminTenantListItem>> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new ListAdminTenantsQuery(new AdminTenantFilter(
                    request.Search, request.Status, request.Plan, request.Sort, request.Page, request.PageSize)),
                ct)).ToOk())
            .WithName("ListAdminTenants")
            .WithSummary(
                "Lists every workspace with its plan, team size and this month's tickets. Search matches the name, "
                + "address or owner email.")
            .Produces<PagedResult<AdminTenantListItem>>()
            .ProducesValidationProblem();

        admin.MapGet("/tenants/{id:guid}", async (
            Guid id,
            IQueryHandler<GetAdminTenantQuery, AdminTenantDetail> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetAdminTenantQuery(id), ct)).ToOk())
            .WithName("GetAdminTenant")
            .WithSummary(
                "Returns a read-only overview of a workspace: plan, usage against its limits, totals, admins and "
                + "recent invoices. It never shows ticket or customer content.")
            .Produces<AdminTenantDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/tenants/{id:guid}/suspend", async (
            Guid id,
            SuspendTenantRequest request,
            ICommandHandler<SetTenantStatusCommand, AdminTenantDetail> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetTenantStatusCommand(id, Suspend: true, request.Reason), ct)).ToOk())
            .WithName("SuspendTenant")
            .WithSummary(
                "Closes a workspace: its team, customers, API keys and webhooks stop working at once. No data is "
                + "deleted, and the workspace's audit log records the change as made by NexDesk.")
            .Produces<AdminTenantDetail>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/tenants/{id:guid}/reactivate", async (
            Guid id,
            ICommandHandler<SetTenantStatusCommand, AdminTenantDetail> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetTenantStatusCommand(id, Suspend: false, null), ct)).ToOk())
            .WithName("ReactivateTenant")
            .WithSummary("Reopens a suspended workspace.")
            .Produces<AdminTenantDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapGet("/metrics", async (
            IQueryHandler<GetPlatformMetricsQuery, PlatformMetrics> handler,
            CancellationToken ct,
            int months = 12) =>
            (await handler.Handle(new GetPlatformMetricsQuery(months), ct)).ToOk())
            .WithName("GetPlatformMetrics")
            .WithSummary(
                "Returns workspace counts, current monthly recurring revenue by plan, and per month for the last "
                + "1 to 36 months: revenue under contract at the month's end and new sign-ups.")
            .Produces<PlatformMetrics>()
            .ProducesValidationProblem();

        admin.MapPost("/jobs-dashboard", Ok<JobsDashboardLink> (HttpContext http, JobsDashboardAccess access) =>
            TypedResults.Ok(new JobsDashboardLink(
                access.IssueLink(Guid.Parse(http.User.FindFirst(NexDeskClaims.Subject)!.Value)))))
            .WithName("OpenJobsDashboard")
            .WithSummary("Returns a single-use address, valid for one minute, that opens the background job dashboard.")
            .Produces<JobsDashboardLink>();

        return api;
    }
}
