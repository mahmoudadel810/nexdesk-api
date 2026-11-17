using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Reports;
using NexDesk.Application.Sla;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Reports;

[Collection(ApiCollection.Name)]
public sealed class ReportsTests(ApiFactory factory)
{
    private const string Reports = "/api/v1/reports";

    private async Task Sql(FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private Task Rate(TestWorkspace workspace, TicketDetailDto ticket, int rating, DateTimeOffset at) => Sql(
        $"""
        INSERT INTO csat_responses (id, tenant_id, ticket_id, customer_id, rating, created_at)
        VALUES (gen_random_uuid(), {workspace.TenantId}, {ticket.Id}, {ticket.Requester.Id}, {rating}, {at})
        """);

    private static async Task SetTimezone(HttpClient client, string timezone)
    {
        var days = Enum.GetValues<DayOfWeek>()
            .Select(day => new BusinessDayDto(day, true, new TimeOnly(0, 0), new TimeOnly(23, 59, 59)))
            .ToList();
        (await client.PutAsJsonAsync("/api/v1/sla/business-hours", new UpdateBusinessHoursRequest(timezone, days), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A workspace in UTC with a little history: one ticket answered and solved three days ago, two still
    /// open today, one from forty days ago and one deleted.
    /// </summary>
    private async Task<(TestWorkspace Workspace, TestMember Agent, TicketDetailDto Solved, DateOnly Today)> Seed()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await SetTimezone(workspace.Client, "UTC");
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var threeDaysAgo = new DateTimeOffset(today.AddDays(-3).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var fortyDaysAgo = new DateTimeOffset(today.AddDays(-40).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);

        var solved = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("=HYPERLINK(\"http://evil.test\")", priority: TicketPriority.Urgent, assigneeId: agent.UserId));
        var waiting = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Slow answer", assigneeId: agent.UserId));
        var pending = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Pending, \"quoted\"", priority: TicketPriority.Low));
        var old = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Old one"));
        var deleted = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Deleted"));
        (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{pending.Id}", new UpdateTicketRequest(null, TicketStatus.Pending, null, null, null), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await workspace.Client.DeleteAsync($"/api/v1/tickets/{deleted.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Sql($"""
            UPDATE tickets SET status = 'Solved', created_at = {threeDaysAgo},
                first_responded_at = {threeDaysAgo.AddMinutes(30)}, solved_at = {threeDaysAgo.AddMinutes(120)},
                first_response_breached_at = NULL, resolution_breached_at = NULL
            WHERE id = {solved.Id}
            """);
        await Sql($"UPDATE tickets SET first_response_breached_at = now() WHERE id = {waiting.Id}");
        await Sql($"""
            UPDATE tickets SET status = 'Closed', created_at = {fortyDaysAgo}, solved_at = {fortyDaysAgo.AddHours(5)},
                closed_at = {fortyDaysAgo.AddHours(5)}
            WHERE id = {old.Id}
            """);
        await Rate(workspace, solved, 5, threeDaysAgo.AddHours(3));
        await Rate(workspace, old, 2, fortyDaysAgo.AddHours(6));
        return (workspace, agent, solved, today);
    }

    [Fact]
    public async Task The_overview_summarises_the_period_and_compares_it_with_the_one_before()
    {
        var (workspace, _, _, today) = await Seed();

        var overview = await workspace.Client.GetAsync<OverviewDto>($"{Reports}/overview");

        overview.Range.ShouldBe(new ReportRangeDto(today.AddDays(-29), today, "UTC", ReportBucket.Day));
        (overview.Created, overview.Solved, overview.PreviousCreated, overview.PreviousSolved).ShouldBe((3, 1, 1, 1));
        overview.OpenBacklog.ShouldBe(2);
        (overview.AvgFirstResponseMinutes, overview.AvgResolutionMinutes).ShouldBe((30, 120));
        overview.SlaCompliancePercent.ShouldBe(66.7);
        (overview.CsatAverage, overview.CsatResponses).ShouldBe((5, 1));

        // A range that reaches back far enough takes in the older ticket and its rating.
        var wide = await workspace.Client.GetAsync<OverviewDto>(
            $"{Reports}/overview?from={today.AddDays(-45):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        (wide.Created, wide.Solved, wide.PreviousCreated, wide.CsatAverage, wide.CsatResponses).ShouldBe((4, 2, 0, 3.5, 2));
        wide.AvgResolutionMinutes.ShouldBe(210);
    }

    [Fact]
    public async Task Time_series_have_a_point_for_every_day_and_switch_to_weeks_for_long_periods()
    {
        var (workspace, _, _, today) = await Seed();

        var volume = await workspace.Client.GetAsync<List<VolumePoint>>($"{Reports}/volume");
        volume.Count.ShouldBe(30);
        volume.Select(p => p.Date).ShouldBe(Enumerable.Range(0, 30).Select(day => today.AddDays(day - 29)));
        volume.Single(p => p.Date == today.AddDays(-3)).ShouldBe(new VolumePoint(today.AddDays(-3), 1, 1));
        volume.Single(p => p.Date == today).ShouldBe(new VolumePoint(today, 2, 0));
        (volume.Sum(p => p.Created), volume.Sum(p => p.Solved)).ShouldBe((3, 1));

        var times = await workspace.Client.GetAsync<List<ResponseTimePoint>>($"{Reports}/response-times");
        times.Count.ShouldBe(30);
        times.Single(p => p.Date == today.AddDays(-3)).ShouldBe(new ResponseTimePoint(today.AddDays(-3), 30, 120));
        times.Single(p => p.Date == today).ShouldBe(new ResponseTimePoint(today, null, null));

        var weekly = await workspace.Client.GetAsync<List<VolumePoint>>(
            $"{Reports}/volume?from={today.AddDays(-119):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        weekly.Count.ShouldBeInRange(17, 19);
        weekly.ShouldAllBe(p => p.Date.DayOfWeek == DayOfWeek.Monday);
        (weekly.Sum(p => p.Created), weekly.Sum(p => p.Solved)).ShouldBe((4, 2));
    }

    [Fact]
    public async Task Days_are_the_workspaces_days_not_the_servers()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await SetTimezone(workspace.Client, "Asia/Tokyo");
        var ticket = await workspace.Client.CreateTicketAsync();

        // 20:00 UTC is already five in the morning of the next day in Tokyo.
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tokyo).DateTime);
        var createdUtc = new DateTimeOffset(localToday.AddDays(-2).ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero);
        await Sql($"UPDATE tickets SET created_at = {createdUtc} WHERE id = {ticket.Id}");

        var overview = await workspace.Client.GetAsync<OverviewDto>($"{Reports}/overview");
        (overview.Range.Timezone, overview.Range.To).ShouldBe(("Asia/Tokyo", localToday));
        var volume = await workspace.Client.GetAsync<List<VolumePoint>>($"{Reports}/volume");
        volume.Single(p => p.Created == 1).Date.ShouldBe(localToday.AddDays(-1));

        // Asking for exactly that local day finds the ticket; the day before does not.
        var day = localToday.AddDays(-1).ToString("yyyy-MM-dd");
        (await workspace.Client.GetAsync<OverviewDto>($"{Reports}/overview?from={day}&to={day}")).Created.ShouldBe(1);
        var before = localToday.AddDays(-2).ToString("yyyy-MM-dd");
        (await workspace.Client.GetAsync<OverviewDto>($"{Reports}/overview?from={before}&to={before}")).Created.ShouldBe(0);
    }

    [Fact]
    public async Task Backlog_sla_csat_and_the_leaderboard_break_the_numbers_down()
    {
        var (workspace, agent, _, today) = await Seed();

        var backlog = await workspace.Client.GetAsync<BacklogDto>($"{Reports}/backlog");
        backlog.ByStatus.ShouldBe(
        [
            new StatusCount(TicketStatus.New, 1),
            new StatusCount(TicketStatus.Open, 0),
            new StatusCount(TicketStatus.Pending, 1),
            new StatusCount(TicketStatus.OnHold, 0),
        ]);
        backlog.ByPriority.ShouldBe(
        [
            new PriorityCount(TicketPriority.Urgent, 0),
            new PriorityCount(TicketPriority.High, 0),
            new PriorityCount(TicketPriority.Normal, 1),
            new PriorityCount(TicketPriority.Low, 1),
        ]);
        backlog.Unassigned.ShouldBe(1);

        var sla = await workspace.Client.GetAsync<SlaComplianceDto>($"{Reports}/sla-compliance");
        (sla.FirstResponsePercent, sla.ResolutionPercent).ShouldBe((50, 100));
        sla.ByPriority.Select(r => r.Priority).ShouldBe(
            [TicketPriority.Urgent, TicketPriority.High, TicketPriority.Normal, TicketPriority.Low]);
        sla.ByPriority[0].ShouldBe(new SlaComplianceRow(TicketPriority.Urgent, 1, 0, 1, 0));
        sla.ByPriority[2].ShouldBe(new SlaComplianceRow(TicketPriority.Normal, 0, 1, 0, 0));
        (sla.ByPriority[1].FirstResponsePercent, sla.ByPriority[2].FirstResponsePercent).ShouldBe((null, 0));

        var csat = await workspace.Client.GetAsync<CsatReportDto>($"{Reports}/csat");
        (csat.Average, csat.Responses, csat.SatisfiedPercent).ShouldBe((5, 1, 100));
        csat.Distribution.ShouldBe(
            [new RatingCount(1, 0), new RatingCount(2, 0), new RatingCount(3, 0), new RatingCount(4, 0), new RatingCount(5, 1)]);
        csat.Trend.Count.ShouldBe(30);
        csat.Trend.Single(p => p.Responses > 0).ShouldBe(new CsatPoint(today.AddDays(-3), 5, 1));
        var wide = await workspace.Client.GetAsync<CsatReportDto>(
            $"{Reports}/csat?from={today.AddDays(-45):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        (wide.Average, wide.Responses, wide.SatisfiedPercent).ShouldBe((3.5, 2, 50));

        var agents = await workspace.Client.GetAsync<List<AgentRow>>($"{Reports}/agents");
        agents.Count.ShouldBe(2);
        agents[0].ShouldBe(new AgentRow(agent.UserId, "Aaron Agent", 1, 1, 30, 120, 5, 1));
        (agents[1].UserId, agents[1].Solved, agents[1].AssignedOpen, agents[1].CsatAverage).ShouldBe((workspace.OwnerId, 0, 0, null));
    }

    [Fact]
    public async Task Reports_export_as_csv_that_spreadsheets_open_safely()
    {
        var (workspace, _, _, today) = await Seed();

        var response = await workspace.Client.GetAsync($"{Reports}/volume/export.csv");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe(
            $"nexdesk-volume-{today.AddDays(-29):yyyy-MM-dd}-to-{today:yyyy-MM-dd}.csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(31);
        lines[0].ShouldBe("Date,Created,Solved");
        lines.ShouldContain($"{today.AddDays(-3):yyyy-MM-dd},1,1");

        var tickets = await workspace.Client.GetStringAsync($"{Reports}/tickets/export.csv");
        tickets.ShouldContain("'=HYPERLINK(");
        tickets.ShouldContain("\"Pending, \"\"quoted\"\"\"");
        tickets.ShouldContain(",Urgent,");
        tickets.ShouldContain("Aaron Agent");
        tickets.ShouldNotContain("Deleted");
        tickets.ShouldNotContain("Old one");

        foreach (var name in ReportService.ExportNames)
        {
            (await workspace.Client.GetAsync($"{Reports}/{name}/export.csv")).StatusCode.ShouldBe(HttpStatusCode.OK, name);
        }

        var agents = await workspace.Client.GetStringAsync($"{Reports}/agents/export.csv");
        agents.ShouldContain("Aaron Agent,1,1,30,120,5,1");
        (await workspace.Client.GetAsync($"{Reports}/secrets/export.csv")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Impossible_ranges_are_refused()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        foreach (var query in new[] { "from=2026-03-10&to=2026-03-01", "from=2024-01-01&to=2026-03-01", "from=2026-01-01&to=2027-01-02" })
        {
            foreach (var report in new[] { "overview", "volume", "response-times", "sla-compliance", "csat", "agents", "volume/export.csv" })
            {
                var response = await workspace.Client.GetAsync($"{Reports}/{report}?{query}");
                response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{report}?{query}");
                (await response.ProblemCodeAsync()).ShouldBe("invalid-report-range");
            }
        }

        (await workspace.Client.GetAsync($"{Reports}/overview?from=yesterday")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await workspace.Client.GetAsync<List<VolumePoint>>($"{Reports}/volume?from=2026-01-01&to=2027-01-01")).Count.ShouldBeInRange(52, 54);
    }

    [Fact]
    public async Task Everyone_on_the_team_reads_reports_and_no_workspace_sees_another_workspaces_numbers()
    {
        var (acme, _, _, today) = await Seed();
        var globex = await factory.CreateWorkspaceAsync();
        await SetTimezone(globex.Client, "UTC");
        var viewer = await factory.AddMemberAsync(acme, Role.Viewer, "Vera Viewer");

        (await viewer.Client.GetAsync<OverviewDto>($"{Reports}/overview")).Created.ShouldBe(3);
        (await factory.CreateBareClient().GetAsync($"{Reports}/overview")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.CreateBareClient().GetAsync($"{Reports}/tickets/export.csv")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var wide = $"from={today.AddDays(-60):yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var overview = await globex.Client.GetAsync<OverviewDto>($"{Reports}/overview?{wide}");
        (overview.Created, overview.Solved, overview.OpenBacklog, overview.CsatResponses).ShouldBe((0, 0, 0, 0));
        (overview.AvgFirstResponseMinutes, overview.SlaCompliancePercent, overview.CsatAverage).ShouldBe((null, null, null));
        (await globex.Client.GetAsync<List<VolumePoint>>($"{Reports}/volume?{wide}")).ShouldAllBe(p => p.Created == 0 && p.Solved == 0);
        (await globex.Client.GetAsync<BacklogDto>($"{Reports}/backlog")).ByStatus.ShouldAllBe(s => s.Count == 0);
        (await globex.Client.GetAsync<List<ResponseTimePoint>>($"{Reports}/response-times?{wide}"))
            .ShouldAllBe(p => p.AvgFirstResponseMinutes == null && p.AvgResolutionMinutes == null);
        (await globex.Client.GetAsync<SlaComplianceDto>($"{Reports}/sla-compliance?{wide}")).ByPriority
            .ShouldAllBe(r => r.FirstResponseMet + r.FirstResponseBreached + r.ResolutionMet + r.ResolutionBreached == 0);
        (await globex.Client.GetAsync<CsatReportDto>($"{Reports}/csat?{wide}")).Responses.ShouldBe(0);
        var agents = await globex.Client.GetAsync<List<AgentRow>>($"{Reports}/agents?{wide}");
        agents.ShouldHaveSingleItem().UserId.ShouldBe(globex.OwnerId);
        (await globex.Client.GetStringAsync($"{Reports}/tickets/export.csv?{wide}")).ShouldNotContain("Slow answer");
        (await globex.Client.GetStringAsync($"{Reports}/agents/export.csv?{wide}")).ShouldNotContain("Aaron Agent");
    }
}
