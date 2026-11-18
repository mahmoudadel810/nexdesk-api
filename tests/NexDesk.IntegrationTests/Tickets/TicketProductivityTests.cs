using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Tickets;

[Collection(ApiCollection.Name)]
public sealed class TicketProductivityTests(ApiFactory factory)
{
    [Fact]
    public async Task Search_finds_tickets_by_subject_message_requester_and_number()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var invoice = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("Invoice download fails", "pat.payer@example.test"));
        var login = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Cannot sign in"));
        var arabic = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("مشكلة في الفاتورة الشهرية"));
        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{login.Id}/messages",
            new AddMessageRequest("The customer mentioned a kangaroo-shaped error icon.", IsInternal: true),
            TestJson.Options);

        async Task<Guid[]> Search(string term) =>
            [.. (await workspace.Client.GetAsync<PagedResult<TicketListItem>>(
                $"/api/v1/tickets?sort=number&search={Uri.EscapeDataString(term)}")).Items.Select(t => t.Id)];

        (await Search("invoice")).ShouldBe([invoice.Id]);
        (await Search("INVO")).ShouldBe([invoice.Id]);
        (await Search("download invoice")).ShouldBe([invoice.Id]);
        (await Search("kangaroo")).ShouldBe([login.Id]);
        (await Search("pat.payer")).ShouldBe([invoice.Id]);
        (await Search("الفاتورة")).ShouldBe([arabic.Id]);
        (await Search($"#{login.Number}")).ShouldBe([login.Id]);
        (await Search("sign & | ! ( cannot")).ShouldBe([login.Id]);
        (await Search("nothing-matches-this")).ShouldBeEmpty();
        (await Search("'; DROP TABLE tickets; --")).ShouldBeEmpty();

        var combined = await workspace.Client.GetAsync<PagedResult<TicketListItem>>(
            "/api/v1/tickets?search=sign&status=Solved");
        combined.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Searching_a_number_puts_that_ticket_first_ahead_of_text_that_mentions_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var target = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Printer jam"));
        var mentions = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket($"Follow-up to {target.Number} please"));

        var found = await workspace.Client.GetAsync<PagedResult<TicketListItem>>(
            $"/api/v1/tickets?search={target.Number}&pageSize=1");

        found.TotalCount.ShouldBe(2);
        found.Items.ShouldHaveSingleItem().Id.ShouldBe(target.Id);
        mentions.Number.ShouldBeGreaterThan(target.Number);
    }

    [Fact]
    public async Task A_stale_edit_is_rejected_and_the_first_writer_wins()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var ticket = await workspace.Client.CreateTicketAsync();
        var path = $"/api/v1/tickets/{ticket.Id}";

        var first = await (await workspace.Client.PatchAsJsonAsync(
            path,
            new UpdateTicketRequest(null, null, TicketPriority.High, null, null, Version: ticket.Version),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        first.Version.ShouldNotBe(ticket.Version);

        var stale = await agent.Client.PatchAsJsonAsync(
            path,
            new UpdateTicketRequest(null, null, TicketPriority.Low, null, null, Version: ticket.Version),
            TestJson.Options);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await stale.ProblemCodeAsync()).ShouldBe("stale-ticket");

        var staleAssign = await agent.Client.PutAsJsonAsync(
            $"{path}/assignment",
            new AssignTicketRequest(agent.UserId, null, ticket.Version));
        (await staleAssign.ProblemCodeAsync()).ShouldBe("stale-ticket");

        var current = await agent.Client.GetAsync<TicketDetailDto>(path);
        current.Priority.ShouldBe(TicketPriority.High);
        current.Assignee.ShouldBeNull();

        var retried = await (await agent.Client.PutAsJsonAsync(
            $"{path}/assignment",
            new AssignTicketRequest(agent.UserId, null, current.Version))).ReadAsync<TicketDetailDto>();
        retried.Assignee!.Id.ShouldBe(agent.UserId);
    }

    [Fact]
    public async Task Simultaneous_replies_are_all_kept()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var ticket = await workspace.Client.CreateTicketAsync();

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest($"Reply {i}"),
            TestJson.Options)));

        replies.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var timeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline");
        timeline.Count(item => item.Kind == TimelineKind.Message).ShouldBe(9);
        timeline.Count(item => item.EventType == TicketEventType.StatusChanged).ShouldBe(1);
    }

    [Fact]
    public async Task Custom_fields_are_defined_validated_and_recorded_in_the_timeline()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);

        var area = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/custom-fields",
            new CreateCustomFieldRequest("product_area", "Product area", CustomFieldType.Dropdown, ["Billing", "Mobile app"], IsRequired: true),
            TestJson.Options)).ReadAsync<CustomFieldDto>();
        foreach (var (key, type) in new[]
        {
            ("order_number", CustomFieldType.Text),
            ("seats", CustomFieldType.Number),
            ("vip", CustomFieldType.Checkbox),
            ("renewal", CustomFieldType.Date),
        })
        {
            (await workspace.Client.PostAsJsonAsync(
                "/api/v1/custom-fields",
                new CreateCustomFieldRequest(key, key, type),
                TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await agent.Client.GetAsync<List<CustomFieldDto>>("/api/v1/custom-fields")).Count.ShouldBe(5);
        (await agent.Client.PostAsJsonAsync(
            "/api/v1/custom-fields",
            new CreateCustomFieldRequest("nope", "Nope", CustomFieldType.Text),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var duplicate = await workspace.Client.PostAsJsonAsync(
            "/api/v1/custom-fields",
            new CreateCustomFieldRequest("seats", "Seats again", CustomFieldType.Number),
            TestJson.Options);
        (await duplicate.ProblemCodeAsync()).ShouldBe("custom-field-key-taken");
        (await workspace.Client.PostAsJsonAsync(
            "/api/v1/custom-fields",
            new CreateCustomFieldRequest("Bad Key", "Bad", CustomFieldType.Dropdown),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        async Task<string> Rejected(Dictionary<string, string?> fields)
        {
            var response = await agent.Client.PostAsJsonAsync(
                "/api/v1/tickets",
                TestTickets.NewTicket() with { CustomFields = fields },
                TestJson.Options);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return await response.ProblemCodeAsync();
        }

        (await Rejected([])).ShouldBe("required-custom-field-missing");
        (await Rejected(new() { ["product_area"] = "Desktop" })).ShouldBe("invalid-custom-field-value");
        (await Rejected(new() { ["product_area"] = "Billing", ["seats"] = "many" })).ShouldBe("invalid-custom-field-value");
        (await Rejected(new() { ["product_area"] = "Billing", ["renewal"] = "17/11/2026" })).ShouldBe("invalid-custom-field-value");
        (await Rejected(new() { ["product_area"] = "Billing", ["ghost"] = "x" })).ShouldBe("unknown-custom-field");

        var ticket = await agent.Client.CreateTicketAsync(TestTickets.NewTicket() with
        {
            CustomFields = new Dictionary<string, string?>
            {
                ["product_area"] = " Billing ",
                ["order_number"] = "SO-1",
                ["seats"] = "12.50",
                ["vip"] = "True",
                ["renewal"] = "2026-12-31",
            },
        });
        ticket.CustomFields.ShouldBe(new Dictionary<string, string>
        {
            ["product_area"] = "Billing",
            ["order_number"] = "SO-1",
            ["seats"] = "12.50",
            ["vip"] = "true",
            ["renewal"] = "2026-12-31",
        }, ignoreOrder: true);

        var updated = await (await agent.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, null, null, null, null, new Dictionary<string, string?>
            {
                ["product_area"] = "Mobile app",
                ["order_number"] = null,
                ["vip"] = "true",
            }),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        updated.CustomFields["product_area"].ShouldBe("Mobile app");
        updated.CustomFields.ShouldNotContainKey("order_number");

        var clearRequired = await agent.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, null, null, null, null, new Dictionary<string, string?> { ["product_area"] = "" }),
            TestJson.Options);
        (await clearRequired.ProblemCodeAsync()).ShouldBe("required-custom-field-missing");

        var changes = (await agent.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline"))
            .Where(item => item.EventType == TicketEventType.CustomFieldChanged)
            .Select(item => (item.Field, item.OldValue, item.NewValue))
            .ToList();
        changes.ShouldBe(
            [("Product area", "Billing", "Mobile app"), ("order_number", "SO-1", null)],
            ignoreOrder: true);

        var relaxed = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/custom-fields/{area.Id}",
            new UpdateCustomFieldRequest("Area", ["Billing", "Mobile app", "Web"], IsRequired: false, IsActive: false, Position: 3),
            TestJson.Options)).ReadAsync<CustomFieldDto>();
        relaxed.IsActive.ShouldBeFalse();
        (await agent.Client.CreateTicketAsync()).CustomFields.ShouldBeEmpty();

        (await workspace.Client.DeleteAsync($"/api/v1/custom-fields/{area.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync<List<CustomFieldDto>>("/api/v1/custom-fields")).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Saved_views_are_private_unless_an_admin_shares_them()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var other = await factory.AddMemberAsync(workspace, Role.Agent, "Other Agent");
        var filter = new TicketViewFilter([TicketStatus.New, TicketStatus.Open], [TicketPriority.Urgent], "me", null, "vip", "refund", "-priority");

        var mine = await (await agent.Client.PostAsJsonAsync(
            "/api/v1/views",
            new SaveViewRequest("  My urgent  ", false, filter),
            TestJson.Options)).ReadAsync<SavedViewDto>();
        mine.Name.ShouldBe("My urgent");
        mine.IsMine.ShouldBeTrue();
        mine.Filter.Status.ShouldBe(filter.Status);
        mine.Filter.Priority.ShouldBe(filter.Priority);
        (mine.Filter with { Status = null, Priority = null }).ShouldBe(filter with { Status = null, Priority = null });

        var agentShares = await agent.Client.PostAsJsonAsync(
            "/api/v1/views",
            new SaveViewRequest("For everyone", true, filter),
            TestJson.Options);
        agentShares.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agentShares.ProblemCodeAsync()).ShouldBe("shared-views-restricted");

        var shared = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/views",
            new SaveViewRequest("Unassigned", true, filter with { Assignee = "unassigned" }),
            TestJson.Options)).ReadAsync<SavedViewDto>();

        var seenByOther = await other.Client.GetAsync<List<SavedViewDto>>("/api/v1/views");
        seenByOther.Select(v => (v.Name, v.IsMine)).ShouldBe([("Unassigned", false)]);
        (await agent.Client.GetAsync<List<SavedViewDto>>("/api/v1/views")).Select(v => v.Name)
            .ShouldBe(["Unassigned", "My urgent"]);

        (await other.Client.DeleteAsync($"/api/v1/views/{mine.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Client.DeleteAsync($"/api/v1/views/{shared.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PutAsJsonAsync(
            $"/api/v1/views/{mine.Id}",
            new SaveViewRequest("Renamed", false, filter with { Assignee = "nobody" }),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var renamed = await (await agent.Client.PutAsJsonAsync(
            $"/api/v1/views/{mine.Id}",
            new SaveViewRequest("Renamed", false, filter with { Search = null }),
            TestJson.Options)).ReadAsync<SavedViewDto>();
        renamed.Name.ShouldBe("Renamed");
        renamed.Filter.Search.ShouldBeNull();

        (await agent.Client.DeleteAsync($"/api/v1/views/{mine.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.DeleteAsync($"/api/v1/views/{shared.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await agent.Client.GetAsync<List<SavedViewDto>>("/api/v1/views")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Bulk_update_changes_what_it_can_and_reports_the_rest()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");
        var first = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(tags: ["old", "keep"]));
        var second = await workspace.Client.CreateTicketAsync();
        var closed = await workspace.Client.CreateTicketAsync();
        await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{closed.Id}",
            new UpdateTicketRequest(null, TicketStatus.Closed, null, null, null),
            TestJson.Options);
        var missing = Guid.NewGuid();

        var result = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets/bulk",
            new BulkUpdateRequest(
                [first.Id, second.Id, closed.Id, missing, first.Id],
                TicketStatus.Pending,
                TicketPriority.High,
                SetAssignee: true,
                AssigneeId: agent.UserId,
                AddTags: ["Outage"],
                RemoveTags: ["OLD"]),
            TestJson.Options)).ReadAsync<BulkResult>();

        result.Updated.ShouldBe(2);
        result.Failed.ShouldBe(
            [new BulkFailure(closed.Id, "ticket-closed"), new BulkFailure(missing, "ticket-not-found")],
            ignoreOrder: true);

        var updated = await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{first.Id}");
        updated.Status.ShouldBe(TicketStatus.Pending);
        updated.Priority.ShouldBe(TicketPriority.High);
        updated.Assignee!.Name.ShouldBe("Aaron Agent");
        updated.Tags.ShouldBe(["keep", "outage"]);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{second.Id}")).Tags.ShouldBe(["outage"]);

        var unassigned = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets/bulk",
            new BulkUpdateRequest([first.Id], SetAssignee: true, AssigneeId: null),
            TestJson.Options)).ReadAsync<BulkResult>();
        unassigned.Updated.ShouldBe(1);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{first.Id}")).Assignee.ShouldBeNull();

        (await workspace.Client.PostAsJsonAsync("/api/v1/tickets/bulk", new BulkUpdateRequest([first.Id]), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets/bulk",
            new BulkUpdateRequest([.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())], TicketStatus.Open),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var viewer = await factory.AddMemberAsync(workspace, Role.Viewer);
        (await viewer.Client.PostAsJsonAsync(
            "/api/v1/tickets/bulk",
            new BulkUpdateRequest([first.Id], TicketStatus.Open),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Merging_moves_the_conversation_and_closes_the_duplicate()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var target = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Original report"));
        var file = await agent.Client.UploadAsync("dup.txt", [1, 2, 3]);
        var duplicate = await agent.Client.CreateTicketAsync(
            TestTickets.NewTicket("Same problem again", attachmentIds: [file.Id]));
        await agent.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{duplicate.Id}/messages",
            new AddMessageRequest("Looks like a duplicate.", IsInternal: true),
            TestJson.Options);

        var self = await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{target.Id}/merge",
            new MergeTicketRequest(target.Id));
        (await self.ProblemCodeAsync()).ShouldBe("merge-into-self");

        var merged = await (await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{duplicate.Id}/merge",
            new MergeTicketRequest(target.Id))).ReadAsync<TicketDetailDto>();
        merged.Id.ShouldBe(target.Id);
        merged.Watchers.Select(w => w.Id).ShouldBe([agent.UserId, workspace.OwnerId], ignoreOrder: true);

        var timeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{target.Id}/timeline");
        timeline.Count(item => item.Kind == TimelineKind.Message).ShouldBe(3);
        timeline.SelectMany(item => item.Attachments).ShouldHaveSingleItem().FileName.ShouldBe("dup.txt");
        timeline.Single(item => item.EventType == TicketEventType.MergedFrom).NewValue.ShouldBe($"#{duplicate.Number}");

        var source = await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{duplicate.Id}");
        source.Status.ShouldBe(TicketStatus.Closed);
        source.MergedInto.ShouldBe(new TicketLink(target.Id, target.Number, "Original report"));
        var sourceTimeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{duplicate.Id}/timeline");
        sourceTimeline.ShouldAllBe(item => item.Kind == TimelineKind.Event);
        sourceTimeline.Single(item => item.EventType == TicketEventType.MergedInto).NewValue.ShouldBe($"#{target.Number}");

        var again = await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{duplicate.Id}/merge",
            new MergeTicketRequest(target.Id));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.ProblemCodeAsync()).ShouldBe("ticket-closed");
    }
}
