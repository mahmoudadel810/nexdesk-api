using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Teams;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Tenancy;

/// <summary>Tenant B must never read or change tenant A's tickets, customers or attachments.</summary>
[Collection(ApiCollection.Name)]
public sealed class TicketIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Tickets_of_another_tenant_are_invisible_and_untouchable()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var ticket = await a.Client.CreateTicketAsync(TestTickets.NewTicket("Private to A"));
        var path = $"/api/v1/tickets/{ticket.Id}";

        (await b.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets")).TotalCount.ShouldBe(0);
        (await b.Client.GetAsync<PagedResult<CustomerDto>>("/api/v1/customers")).TotalCount.ShouldBe(0);

        var attempts = new[]
        {
            await b.Client.GetAsync(path),
            await b.Client.GetAsync($"{path}/timeline"),
            await b.Client.PatchAsJsonAsync(path, new UpdateTicketRequest("Hijacked", TicketStatus.Closed, null, null, null), TestJson.Options),
            await b.Client.PutAsJsonAsync($"{path}/assignment", new AssignTicketRequest(b.OwnerId, null)),
            await b.Client.PostAsJsonAsync($"{path}/messages", new AddMessageRequest("Hello from B"), TestJson.Options),
            await b.Client.PutAsync($"{path}/watchers/me", null),
            await b.Client.DeleteAsync($"{path}/watchers/me"),
            await b.Client.DeleteAsync(path),
        };
        attempts.ShouldAllBe(response => response.StatusCode == HttpStatusCode.NotFound);

        var unchanged = await a.Client.GetAsync<TicketDetailDto>(path);
        unchanged.Subject.ShouldBe("Private to A");
        unchanged.Status.ShouldBe(TicketStatus.New);
        unchanged.Assignee.ShouldBeNull();
        unchanged.Watchers.Select(w => w.Id).ShouldBe([a.OwnerId]);
        (await a.Client.GetAsync<List<TimelineItem>>($"{path}/timeline")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_ticket_cannot_reference_people_teams_or_files_of_another_tenant()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var ticketOfA = await a.Client.CreateTicketAsync();
        var teamOfA = await (await a.Client.PostAsJsonAsync("/api/v1/teams", new SaveTeamRequest("A team", null, null)))
            .ReadAsync<TeamDto>();
        var fileOfA = await a.Client.UploadAsync("secret.txt", [1, 2, 3]);

        async Task<string> Rejected(CreateTicketRequest request)
        {
            var response = await b.Client.PostAsJsonAsync("/api/v1/tickets", request, TestJson.Options);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return await response.ProblemCodeAsync();
        }

        (await Rejected(TestTickets.NewTicket() with { RequesterId = ticketOfA.Requester.Id, RequesterEmail = null }))
            .ShouldBe("unknown-requester");
        (await Rejected(TestTickets.NewTicket(assigneeId: a.OwnerId))).ShouldBe("invalid-assignee");
        (await Rejected(TestTickets.NewTicket(teamId: teamOfA.Id))).ShouldBe("unknown-team");
        (await Rejected(TestTickets.NewTicket(attachmentIds: [fileOfA.Id]))).ShouldBe("invalid-attachment");

        (await b.Client.GetAsync($"/api/v1/attachments/{fileOfA.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await a.Client.GetAsync($"/api/v1/attachments/{fileOfA.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ticket_numbers_and_customers_are_kept_per_tenant()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var sharedEmail = TestWorkspaces.UniqueEmail("shared");

        var first = await a.Client.CreateTicketAsync(TestTickets.NewTicket(requesterEmail: sharedEmail));
        await a.Client.CreateTicketAsync();
        var other = await b.Client.CreateTicketAsync(TestTickets.NewTicket(requesterEmail: sharedEmail));

        other.Number.ShouldBe(1);
        other.Requester.Email.ShouldBe(sharedEmail);
        other.Requester.Id.ShouldNotBe(first.Requester.Id);
        (await b.Client.GetAsync<PagedResult<TicketListItem>>($"/api/v1/tickets?requesterId={first.Requester.Id}"))
            .TotalCount.ShouldBe(0);
    }
}

/// <summary>Search, custom fields, saved views, bulk updates and merges must stay inside one tenant.</summary>
[Collection(ApiCollection.Name)]
public sealed class TicketProductivityIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task Search_bulk_and_merge_cannot_reach_another_tenants_tickets()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var ticketOfA = await a.Client.CreateTicketAsync(TestTickets.NewTicket("Zebra crossing outage"));
        var ticketOfB = await b.Client.CreateTicketAsync(TestTickets.NewTicket("Unrelated"));

        (await b.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?search=zebra")).TotalCount.ShouldBe(0);
        (await b.Client.GetAsync<PagedResult<TicketListItem>>($"/api/v1/tickets?search=%23{ticketOfA.Number}"))
            .Items.Select(t => t.Id).ShouldBe([ticketOfB.Id]);

        var bulk = await (await b.Client.PostAsJsonAsync(
            "/api/v1/tickets/bulk",
            new BulkUpdateRequest([ticketOfA.Id], TicketStatus.Closed),
            TestJson.Options)).ReadAsync<BulkResult>();
        bulk.Updated.ShouldBe(0);
        bulk.Failed.ShouldBe([new BulkFailure(ticketOfA.Id, "ticket-not-found")]);

        (await b.Client.PostAsJsonAsync($"/api/v1/tickets/{ticketOfB.Id}/merge", new MergeTicketRequest(ticketOfA.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsJsonAsync($"/api/v1/tickets/{ticketOfA.Id}/merge", new MergeTicketRequest(ticketOfB.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var unchanged = await a.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticketOfA.Id}");
        unchanged.Status.ShouldBe(TicketStatus.New);
        unchanged.MergedInto.ShouldBeNull();
        (await b.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticketOfB.Id}")).Status.ShouldBe(TicketStatus.New);
    }

    [Fact]
    public async Task Custom_fields_and_saved_views_are_kept_per_tenant()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var field = await (await a.Client.PostAsJsonAsync(
            "/api/v1/custom-fields",
            new CreateCustomFieldRequest("order_number", "Order number", NexDesk.Domain.Tickets.CustomFieldType.Text),
            TestJson.Options)).ReadAsync<CustomFieldDto>();
        var view = await (await a.Client.PostAsJsonAsync(
            "/api/v1/views",
            new SaveViewRequest("Shared in A", true, new TicketViewFilter(null, null, null, null, null, null, null)),
            TestJson.Options)).ReadAsync<SavedViewDto>();

        (await b.Client.GetAsync<List<CustomFieldDto>>("/api/v1/custom-fields")).ShouldBeEmpty();
        (await b.Client.GetAsync<List<SavedViewDto>>("/api/v1/views")).ShouldBeEmpty();
        (await b.Client.PutAsJsonAsync(
            $"/api/v1/custom-fields/{field.Id}",
            new UpdateCustomFieldRequest("Hijacked"),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.DeleteAsync($"/api/v1/custom-fields/{field.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.DeleteAsync($"/api/v1/views/{view.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PutAsJsonAsync(
            $"/api/v1/views/{view.Id}",
            new SaveViewRequest("Hijacked", true, view.Filter),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var usesForeignField = await b.Client.PostAsJsonAsync(
            "/api/v1/tickets",
            TestTickets.NewTicket() with { CustomFields = new Dictionary<string, string?> { ["order_number"] = "1" } },
            TestJson.Options);
        (await usesForeignField.ProblemCodeAsync()).ShouldBe("unknown-custom-field");

        // The same key is free in every tenant.
        (await b.Client.PostAsJsonAsync(
            "/api/v1/custom-fields",
            new CreateCustomFieldRequest("order_number", "Order", NexDesk.Domain.Tickets.CustomFieldType.Number),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await a.Client.GetAsync<List<CustomFieldDto>>("/api/v1/custom-fields")).ShouldHaveSingleItem().Label.ShouldBe("Order number");
        (await a.Client.GetAsync<List<SavedViewDto>>("/api/v1/views")).ShouldHaveSingleItem().Name.ShouldBe("Shared in A");
    }
}
