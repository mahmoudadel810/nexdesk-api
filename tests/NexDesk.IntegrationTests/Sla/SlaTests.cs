using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Sla;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Sla;

[Collection(ApiCollection.Name)]
public sealed class SlaTests(ApiFactory factory)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task New_workspaces_get_a_policy_for_every_priority_and_admins_can_change_them()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);

        var defaults = await agent.Client.GetAsync<List<SlaPolicyDto>>("/api/v1/sla/policies");
        defaults.Select(p => p.Priority).ShouldBe(
            [TicketPriority.Urgent, TicketPriority.High, TicketPriority.Normal, TicketPriority.Low]);
        defaults.ShouldAllBe(p => p.ResolutionMinutes >= p.FirstResponseMinutes);

        var updated = await (await workspace.Client.PutAsJsonAsync(
            "/api/v1/sla/policies",
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Urgent, 15, 120)]),
            TestJson.Options)).ReadAsync<List<SlaPolicyDto>>();
        updated.Single(p => p.Priority == TicketPriority.Urgent).ShouldBe(new SlaPolicyDto(TicketPriority.Urgent, 15, 120));
        updated.Count.ShouldBe(4);

        foreach (var invalid in new[]
        {
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Low, 120, 60)]),
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Low, 0, 60)]),
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Low, 10, 60), new SlaPolicyDto(TicketPriority.Low, 20, 60)]),
            new UpdateSlaPoliciesRequest([]),
        })
        {
            (await workspace.Client.PutAsJsonAsync("/api/v1/sla/policies", invalid, TestJson.Options))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await agent.Client.PutAsJsonAsync(
            "/api/v1/sla/policies",
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Low, 10, 60)]),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Deadlines_follow_the_priority_and_pause_while_waiting_on_the_customer()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.OpenAroundTheClockAsync();
        await workspace.Client.PutAsJsonAsync(
            "/api/v1/sla/policies",
            new UpdateSlaPoliciesRequest(
            [
                new SlaPolicyDto(TicketPriority.Normal, 60, 600),
                new SlaPolicyDto(TicketPriority.Urgent, 10, 100),
            ]),
            TestJson.Options);

        var ticket = await workspace.Client.CreateTicketAsync();
        ticket.Sla.FirstResponseDueAt!.Value.ShouldBe(ticket.CreatedAt.AddMinutes(60), Tolerance);
        ticket.Sla.ResolutionDueAt!.Value.ShouldBe(ticket.CreatedAt.AddMinutes(600), Tolerance);
        ticket.Sla.IsPaused.ShouldBeFalse();
        ticket.Sla.FirstResponseMet.ShouldBeFalse();

        var urgent = await (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, null, TicketPriority.Urgent, null, null),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        urgent.Sla.FirstResponseDueAt!.Value.ShouldBe(ticket.CreatedAt.AddMinutes(10), Tolerance);
        urgent.Sla.ResolutionDueAt!.Value.ShouldBe(ticket.CreatedAt.AddMinutes(100), Tolerance);

        var replied = await (await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("What is your order number?", SetStatus: TicketStatus.Pending),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        replied.Sla.FirstResponseMet.ShouldBeTrue();
        replied.Sla.IsPaused.ShouldBeTrue();
        replied.Sla.ResolutionDueAt.ShouldBeNull();

        var reopened = await (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, TicketStatus.Open, null, null, null),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        reopened.Sla.IsPaused.ShouldBeFalse();
        reopened.Sla.ResolutionDueAt!.Value.ShouldBe(ticket.CreatedAt.AddMinutes(100), Tolerance);

        var list = await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?sort=due");
        list.Items.ShouldHaveSingleItem().Sla.ResolutionDueAt.ShouldBe(reopened.Sla.ResolutionDueAt);
    }

    [Fact]
    public async Task The_sweep_warns_before_a_deadline_and_records_a_breach_after_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.OpenAroundTheClockAsync();
        await workspace.Client.PutAsJsonAsync(
            "/api/v1/sla/policies",
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Normal, 60, 600)]),
            TestJson.Options);
        var ticket = await workspace.Client.CreateTicketAsync();
        var answered = await workspace.Client.CreateTicketAsync();
        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{answered.Id}/messages",
            new AddMessageRequest("Hello"),
            TestJson.Options);
        var path = $"/api/v1/tickets/{ticket.Id}";

        (await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(20))).ShouldBe(0);

        // Eight minutes before the first-response deadline falls inside the twelve-minute warning window.
        (await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(52))).ShouldBe(1);
        (await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?sla=AtRisk"))
            .Items.Select(t => t.Id).ShouldBe([ticket.Id]);
        (await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(53))).ShouldBe(0);

        (await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(63))).ShouldBe(1);
        var breached = await workspace.Client.GetAsync<TicketDetailDto>(path);
        breached.Sla.FirstResponseBreached.ShouldBeTrue();
        breached.Sla.ResolutionBreached.ShouldBeFalse();
        breached.Sla.IsEscalated.ShouldBeTrue();
        (await workspace.Client.GetAsync<List<TimelineItem>>($"{path}/timeline"))
            .Count(item => item.EventType == TicketEventType.SlaBreached && item.NewValue == "FirstResponse").ShouldBe(1);

        (await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(700))).ShouldBe(2);
        (await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(800))).ShouldBe(0);

        var breaches = await workspace.Client.GetAsync<PagedResult<SlaBreachDto>>("/api/v1/sla/breaches");
        breaches.Items.Select(b => (b.TicketNumber, b.Metric)).ShouldBe(
        [
            (ticket.Number, SlaMetric.FirstResponse),
            (ticket.Number, SlaMetric.Resolution),
            (answered.Number, SlaMetric.Resolution),
        ], ignoreOrder: true);
        (await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?sla=Breached&sort=number"))
            .Items.Select(t => t.Id).ShouldBe([ticket.Id, answered.Id]);
        (await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?sla=AtRisk")).TotalCount.ShouldBe(0);

        // A reply after the breach does not un-breach the ticket.
        var late = await (await workspace.Client.PostAsJsonAsync(
            $"{path}/messages",
            new AddMessageRequest("Sorry for the delay."),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        late.Sla.FirstResponseBreached.ShouldBeTrue();
        late.Sla.FirstResponseMet.ShouldBeFalse();
    }

    [Fact]
    public async Task Paused_and_closed_tickets_are_not_breached()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.OpenAroundTheClockAsync();
        var pending = await workspace.Client.CreateTicketAsync();
        var closed = await workspace.Client.CreateTicketAsync();
        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{pending.Id}/messages",
            new AddMessageRequest("Waiting for you.", SetStatus: TicketStatus.Pending),
            TestJson.Options);
        await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{closed.Id}",
            new UpdateTicketRequest(null, TicketStatus.Closed, null, null, null),
            TestJson.Options);

        (await factory.SweepSlaAsync(workspace.TenantId, DateTimeOffset.UtcNow.AddDays(60))).ShouldBe(0);

        (await workspace.Client.GetAsync<PagedResult<SlaBreachDto>>("/api/v1/sla/breaches")).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Holidays_can_be_added_once_per_date_and_removed()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);

        var added = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/sla/holidays",
            new AddHolidayRequest(new DateOnly(2027, 1, 1), "  New Year  "))).ReadAsync<HolidayDto>();
        added.Name.ShouldBe("New Year");

        var duplicate = await workspace.Client.PostAsJsonAsync(
            "/api/v1/sla/holidays",
            new AddHolidayRequest(new DateOnly(2027, 1, 1), "Again"));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ProblemCodeAsync()).ShouldBe("holiday-exists");
        (await workspace.Client.PostAsJsonAsync("/api/v1/sla/holidays", new AddHolidayRequest(new DateOnly(2027, 5, 1), " ")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await agent.Client.PostAsJsonAsync("/api/v1/sla/holidays", new AddHolidayRequest(new DateOnly(2027, 5, 1), "Labour Day")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await agent.Client.GetAsync<List<HolidayDto>>("/api/v1/sla/holidays")).ShouldHaveSingleItem().Id.ShouldBe(added.Id);
        (await workspace.Client.DeleteAsync($"/api/v1/sla/holidays/{added.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.DeleteAsync($"/api/v1/sla/holidays/{added.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sla_settings_and_sweeps_stay_inside_one_tenant()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        await a.Client.OpenAroundTheClockAsync();
        await b.Client.OpenAroundTheClockAsync();
        await a.Client.PutAsJsonAsync(
            "/api/v1/sla/policies",
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Normal, 5, 50)]),
            TestJson.Options);
        var holiday = await (await a.Client.PostAsJsonAsync(
            "/api/v1/sla/holidays",
            new AddHolidayRequest(new DateOnly(2027, 3, 3), "Founders' Day"))).ReadAsync<HolidayDto>();
        var ticketOfA = await a.Client.CreateTicketAsync();
        var ticketOfB = await b.Client.CreateTicketAsync();

        (await b.Client.GetAsync<List<SlaPolicyDto>>("/api/v1/sla/policies"))
            .Single(p => p.Priority == TicketPriority.Normal).FirstResponseMinutes.ShouldNotBe(5);
        (await b.Client.GetAsync<List<HolidayDto>>("/api/v1/sla/holidays")).ShouldBeEmpty();
        (await b.Client.DeleteAsync($"/api/v1/sla/holidays/{holiday.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await factory.SweepSlaAsync(a.TenantId, DateTimeOffset.UtcNow.AddDays(30))).ShouldBe(1);

        (await a.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticketOfA.Id}")).Sla.ResolutionBreached.ShouldBeTrue();
        var untouched = await b.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticketOfB.Id}");
        untouched.Sla.ResolutionBreached.ShouldBeFalse();
        untouched.Sla.IsEscalated.ShouldBeFalse();
        (await b.Client.GetAsync<PagedResult<SlaBreachDto>>("/api/v1/sla/breaches")).TotalCount.ShouldBe(0);
        (await a.Client.GetAsync<List<HolidayDto>>("/api/v1/sla/holidays")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_background_job_sweeps_every_active_workspace_without_failing()
    {
        await factory.CreateWorkspaceAsync();
        await using var scope = factory.Services.CreateAsyncScope();

        var job = scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Sla.SlaSweepJob>();

        await Should.NotThrowAsync(() => job.RunAsync(CancellationToken.None));
    }
}
