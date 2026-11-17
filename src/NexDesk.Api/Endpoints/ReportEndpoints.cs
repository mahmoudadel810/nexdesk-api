using NexDesk.Api.Auth;
using NexDesk.Application.Reports;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

/// <summary>The days a report covers, both inclusive, in the workspace's time zone. Defaults to the last 30 days.</summary>
public sealed record ReportRangeRequest(DateOnly? From = null, DateOnly? To = null);

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports").WithTags("Reports").RequirePermission(Permissions.ReportsView);

        reports.MapGet("/overview", async ([AsParameters] ReportRangeRequest range, ReportService service, CancellationToken ct) =>
            (await service.Run(range.From, range.To, (queries, period, token) => queries.Overview(period, token), ct)).ToOk())
            .WithName("GetReportOverview")
            .WithSummary("Returns the headline figures of a period: volume, backlog, response times, SLA compliance and CSAT.")
            .Produces<OverviewDto>()
            .ProducesValidationProblem();

        reports.MapGet("/volume", async ([AsParameters] ReportRangeRequest range, ReportService service, CancellationToken ct) =>
            (await service.Run(range.From, range.To, (queries, period, token) => queries.Volume(period, token), ct)).ToOk())
            .WithName("GetTicketVolume")
            .WithSummary("Returns tickets created and solved per day, or per week for periods longer than two months.")
            .Produces<IReadOnlyList<VolumePoint>>()
            .ProducesValidationProblem();

        reports.MapGet("/backlog", async (ReportService service, CancellationToken ct) =>
            (await service.Backlog(ct)).ToOk())
            .WithName("GetBacklog")
            .WithSummary("Returns the tickets that still need work, by status and by priority, as they stand now.")
            .Produces<BacklogDto>();

        reports.MapGet("/response-times", async ([AsParameters] ReportRangeRequest range, ReportService service, CancellationToken ct) =>
            (await service.Run(range.From, range.To, (queries, period, token) => queries.ResponseTimes(period, token), ct)).ToOk())
            .WithName("GetResponseTimes")
            .WithSummary("Returns the average first-response and resolution time, in minutes, per day or week.")
            .Produces<IReadOnlyList<ResponseTimePoint>>()
            .ProducesValidationProblem();

        reports.MapGet("/sla-compliance", async ([AsParameters] ReportRangeRequest range, ReportService service, CancellationToken ct) =>
            (await service.Run(range.From, range.To, (queries, period, token) => queries.SlaCompliance(period, token), ct)).ToOk())
            .WithName("GetSlaCompliance")
            .WithSummary("Returns how many tickets met or missed each SLA target, overall and by priority.")
            .Produces<SlaComplianceDto>()
            .ProducesValidationProblem();

        reports.MapGet("/csat", async ([AsParameters] ReportRangeRequest range, ReportService service, CancellationToken ct) =>
            (await service.Run(range.From, range.To, (queries, period, token) => queries.Csat(period, token), ct)).ToOk())
            .WithName("GetCsatReport")
            .WithSummary("Returns the satisfaction score, the spread of ratings and how the score moved.")
            .Produces<CsatReportDto>()
            .ProducesValidationProblem();

        reports.MapGet("/agents", async ([AsParameters] ReportRangeRequest range, ReportService service, CancellationToken ct) =>
            (await service.Run(range.From, range.To, (queries, period, token) => queries.Agents(period, token), ct)).ToOk())
            .WithName("GetAgentLeaderboard")
            .WithSummary("Returns each agent's solved tickets, response times and satisfaction score, most solved first.")
            .Produces<IReadOnlyList<AgentRow>>()
            .ProducesValidationProblem();

        reports.MapGet("/{name}/export.csv", async (
            string name,
            [AsParameters] ReportRangeRequest range,
            ReportService service,
            CancellationToken ct) =>
            (await service.Export(name, range.From, range.To, ct))
            .ToHttp(file => TypedResults.File(file.Content, "text/csv; charset=utf-8", file.FileName)))
            .WithName("ExportReport")
            .WithSummary("Downloads a report as CSV. Names: volume, backlog, response-times, sla-compliance, csat, agents, tickets.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }
}
