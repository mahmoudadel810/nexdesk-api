using System.Net;
using System.Net.Http.Json;
using System.Text;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Teams;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Tickets;

[Collection(ApiCollection.Name)]
public sealed class TicketsTests(ApiFactory factory)
{
    [Fact]
    public async Task Creating_tickets_numbers_them_per_workspace_and_reuses_the_customer()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var email = TestWorkspaces.UniqueEmail("casey");

        var first = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("Printer on fire", email, tags: [" Hardware ", "URGENT", "hardware"]));
        var second = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("Second question", email.ToUpperInvariant()));

        first.Number.ShouldBe(1);
        second.Number.ShouldBe(2);
        first.Status.ShouldBe(TicketStatus.New);
        first.Channel.ShouldBe(TicketChannel.Agent);
        first.Tags.ShouldBe(["hardware", "urgent"]);
        first.Requester.Email.ShouldBe(email);
        second.Requester.Id.ShouldBe(first.Requester.Id);
        first.IsWatching.ShouldBeTrue();

        var timeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{first.Id}/timeline");
        timeline.Count.ShouldBe(2);
        var description = timeline.Single(item => item.Kind == TimelineKind.Message);
        description.Body.ShouldBe("I get an error every time I try to sign in.");
        description.Actor.ShouldBe(new ActorDto(ActorType.Customer, first.Requester.Id, "Casey Customer"));
        var created = timeline.Single(item => item.Kind == TimelineKind.Event);
        created.EventType.ShouldBe(TicketEventType.Created);
        created.Actor.Name.ShouldBe("Olivia Owner");

        var customers = await workspace.Client.GetAsync<PagedResult<CustomerDto>>("/api/v1/customers?search=CASEY");
        customers.Items.ShouldHaveSingleItem().Email.ShouldBe(email);
    }

    [Fact]
    public async Task Concurrent_ticket_creation_never_duplicates_a_number()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var tickets = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => workspace.Client.CreateTicketAsync(TestTickets.NewTicket($"Parallel {i}"))));

        tickets.Select(t => t.Number).Order().ShouldBe(Enumerable.Range(1, 12));
    }

    [Fact]
    public async Task Creation_is_rejected_without_a_requester_or_with_an_invalid_assignment()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var viewer = await factory.AddMemberAsync(workspace, Role.Viewer);

        var noRequester = await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets",
            TestTickets.NewTicket() with { RequesterEmail = null },
            TestJson.Options);
        noRequester.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noRequester.ProblemCodeAsync()).ShouldBe("requester-required");

        var viewerAssignee = await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets",
            TestTickets.NewTicket(assigneeId: viewer.UserId),
            TestJson.Options);
        (await viewerAssignee.ProblemCodeAsync()).ShouldBe("invalid-assignee");

        var unknownTeam = await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets",
            TestTickets.NewTicket(teamId: Guid.NewGuid()),
            TestJson.Options);
        (await unknownTeam.ProblemCodeAsync()).ShouldBe("unknown-team");

        var invalid = await workspace.Client.PostAsJsonAsync(
            "/api/v1/tickets",
            TestTickets.NewTicket(subject: " ", tags: [new string('x', 31)]) with { RequesterEmail = "nope" },
            TestJson.Options);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.ProblemCodeAsync()).ShouldBe("validation");

        (await viewer.Client.PostAsJsonAsync("/api/v1/tickets", TestTickets.NewTicket(), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets")).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_public_reply_opens_the_ticket_and_records_the_first_response()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var ticket = await workspace.Client.CreateTicketAsync();

        var noted = await (await agent.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Looks like a known issue.", IsInternal: true),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        noted.Status.ShouldBe(TicketStatus.New);
        noted.FirstRespondedAt.ShouldBeNull();
        noted.Watchers.Select(w => w.Id).ShouldContain(agent.UserId);

        var replied = await (await agent.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("We are on it."),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        replied.Status.ShouldBe(TicketStatus.Open);
        replied.FirstRespondedAt.ShouldNotBeNull();

        var pending = await (await agent.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Can you send a screenshot?", SetStatus: TicketStatus.Pending),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        pending.Status.ShouldBe(TicketStatus.Pending);
        pending.FirstRespondedAt.ShouldBe(replied.FirstRespondedAt);

        var timeline = await agent.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline");
        timeline.Where(i => i.Kind == TimelineKind.Message).Select(i => i.IsInternal).ShouldBe([false, true, false, false]);
        timeline.Where(i => i.EventType == TicketEventType.StatusChanged).Select(i => (i.OldValue, i.NewValue, i.Actor.Type))
            .ShouldBe([("New", "Open", ActorType.System), ("Open", "Pending", ActorType.Staff)]);

        var viewer = await factory.AddMemberAsync(workspace, Role.Viewer);
        (await viewer.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("I should not be able to write."),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.Client.GetAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Updating_a_ticket_records_each_change_and_a_closed_ticket_is_read_only()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(tags: ["login"]));

        var updated = await (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest("Cannot sign in with SSO", TicketStatus.Solved, TicketPriority.Urgent, TicketType.Problem, ["login", "sso"]),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        updated.Subject.ShouldBe("Cannot sign in with SSO");
        updated.Status.ShouldBe(TicketStatus.Solved);
        updated.Priority.ShouldBe(TicketPriority.Urgent);
        updated.Type.ShouldBe(TicketType.Problem);
        updated.Tags.ShouldBe(["login", "sso"]);
        updated.SolvedAt.ShouldNotBeNull();

        var unchanged = await (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, null, TicketPriority.Urgent, null, null),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        unchanged.LastActivityAt.ShouldBe(updated.LastActivityAt);

        var events = (await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline"))
            .Where(i => i.Kind == TimelineKind.Event)
            .ToDictionary(i => i.EventType!.Value, i => (i.OldValue, i.NewValue));
        events[TicketEventType.SubjectChanged].ShouldBe(("Cannot sign in", "Cannot sign in with SSO"));
        events[TicketEventType.PriorityChanged].ShouldBe(("Normal", "Urgent"));
        events[TicketEventType.TypeChanged].ShouldBe(("Incident", "Problem"));
        events[TicketEventType.TagsChanged].ShouldBe(("login", "login, sso"));
        events[TicketEventType.StatusChanged].ShouldBe(("New", "Solved"));

        var closed = await (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, TicketStatus.Closed, null, null, null),
            TestJson.Options)).ReadAsync<TicketDetailDto>();
        closed.ClosedAt.ShouldNotBeNull();

        var edit = await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, TicketStatus.Open, null, null, null),
            TestJson.Options);
        edit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await edit.ProblemCodeAsync()).ShouldBe("ticket-closed");
        var reply = await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Too late."),
            TestJson.Options);
        (await reply.ProblemCodeAsync()).ShouldBe("ticket-closed");
        var assign = await workspace.Client.PutAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assignment",
            new AssignTicketRequest(workspace.OwnerId, null));
        (await assign.ProblemCodeAsync()).ShouldBe("ticket-closed");
    }

    [Fact]
    public async Task Assignment_sets_and_clears_the_assignee_and_team_and_the_assignee_becomes_a_watcher()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");
        var team = await (await workspace.Client.PostAsJsonAsync(
            "/api/v1/teams",
            new SaveTeamRequest("Tier 2", null, [agent.UserId]))).ReadAsync<TeamDto>();
        var ticket = await workspace.Client.CreateTicketAsync();

        var assigned = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assignment",
            new AssignTicketRequest(agent.UserId, team.Id))).ReadAsync<TicketDetailDto>();
        assigned.Assignee!.Name.ShouldBe("Aaron Agent");
        assigned.Team.ShouldBe(new TeamRef(team.Id, "Tier 2"));
        assigned.Watchers.Select(w => w.Id).ShouldBe([agent.UserId, workspace.OwnerId], ignoreOrder: true);

        var cleared = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assignment",
            new AssignTicketRequest(null, null))).ReadAsync<TicketDetailDto>();
        cleared.Assignee.ShouldBeNull();
        cleared.Team.ShouldBeNull();

        var timeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline");
        timeline.Where(i => i.EventType == TicketEventType.AssigneeChanged).Select(i => (i.OldValue, i.NewValue))
            .ShouldBe([(null, "Aaron Agent"), ("Aaron Agent", null)]);
        timeline.Where(i => i.EventType == TicketEventType.TeamChanged).Select(i => (i.OldValue, i.NewValue))
            .ShouldBe([(null, "Tier 2"), ("Tier 2", null)]);

        // Deleting a team leaves its tickets without a team rather than deleting them.
        await workspace.Client.PutAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assignment",
            new AssignTicketRequest(null, team.Id));
        (await workspace.Client.DeleteAsync($"/api/v1/teams/{team.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).Team.ShouldBeNull();
    }

    [Fact]
    public async Task Tickets_can_be_filtered_sorted_and_paged()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var low = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("Low one", priority: TicketPriority.Low, tags: ["billing"]));
        var urgent = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("Urgent one", priority: TicketPriority.Urgent, assigneeId: agent.UserId));
        var high = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("High one", priority: TicketPriority.High, tags: ["billing", "vip"]));
        await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{high.Id}",
            new UpdateTicketRequest(null, TicketStatus.Pending, null, null, null),
            TestJson.Options);

        async Task<string[]> Subjects(string query) =>
            [.. (await workspace.Client.GetAsync<PagedResult<TicketListItem>>($"/api/v1/tickets?{query}")).Items.Select(t => t.Subject)];

        (await Subjects("sort=-priority")).ShouldBe(["Urgent one", "High one", "Low one"]);
        (await Subjects("sort=number")).ShouldBe(["Low one", "Urgent one", "High one"]);
        (await Subjects("")).First().ShouldBe("High one");
        (await Subjects("status=Pending")).ShouldBe(["High one"]);
        (await Subjects("status=New&status=Pending&sort=number")).ShouldBe(["Low one", "Urgent one", "High one"]);
        (await Subjects("priority=Low&priority=High&sort=number")).ShouldBe(["Low one", "High one"]);
        (await Subjects($"assigneeId={agent.UserId}")).ShouldBe(["Urgent one"]);
        (await Subjects("unassigned=true&sort=number")).ShouldBe(["Low one", "High one"]);
        (await Subjects("tag=BILLING&sort=number")).ShouldBe(["Low one", "High one"]);
        (await Subjects("tag=vip")).ShouldBe(["High one"]);
        (await Subjects($"requesterId={low.Requester.Id}")).ShouldBe(["Low one"]);

        var paged = await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets?sort=number&page=2&pageSize=2");
        paged.TotalCount.ShouldBe(3);
        paged.Items.ShouldHaveSingleItem().Id.ShouldBe(high.Id);
        urgent.Assignee!.Id.ShouldBe(agent.UserId);

        (await workspace.Client.GetAsync("/api/v1/tickets?status=Nope")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await workspace.Client.GetAsync("/api/v1/tickets?pageSize=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Watching_can_be_turned_on_and_off()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var ticket = await workspace.Client.CreateTicketAsync();

        (await agent.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).IsWatching.ShouldBeFalse();

        var watching = await (await agent.Client.PutAsync($"/api/v1/tickets/{ticket.Id}/watchers/me", null))
            .ReadAsync<TicketDetailDto>();
        watching.IsWatching.ShouldBeTrue();
        watching.Watchers.Count.ShouldBe(2);
        (await (await agent.Client.PutAsync($"/api/v1/tickets/{ticket.Id}/watchers/me", null))
            .ReadAsync<TicketDetailDto>()).Watchers.Count.ShouldBe(2);

        var stopped = await (await agent.Client.DeleteAsync($"/api/v1/tickets/{ticket.Id}/watchers/me"))
            .ReadAsync<TicketDetailDto>();
        stopped.IsWatching.ShouldBeFalse();
        stopped.Watchers.Select(w => w.Id).ShouldBe([workspace.OwnerId]);
    }

    [Fact]
    public async Task Deleting_a_ticket_hides_it_everywhere_and_its_number_is_not_reused()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var ticket = await workspace.Client.CreateTicketAsync();

        (await agent.Client.DeleteAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await workspace.Client.DeleteAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await workspace.Client.GetAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await workspace.Client.GetAsync($"/api/v1/tickets/{ticket.Id}/timeline")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await workspace.Client.DeleteAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await workspace.Client.GetAsync<PagedResult<TicketListItem>>("/api/v1/tickets")).TotalCount.ShouldBe(0);
        (await workspace.Client.CreateTicketAsync()).Number.ShouldBe(2);
    }

    [Fact]
    public async Task Attachments_are_uploaded_linked_to_a_message_and_downloaded_safely()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var bytes = Encoding.UTF8.GetBytes("<script>alert(1)</script>");
        var onTicket = await workspace.Client.UploadAsync("../../screen shot.html", bytes, "text/html");
        onTicket.FileName.ShouldBe("screen shot.html");
        onTicket.SizeBytes.ShouldBe(bytes.Length);

        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(attachmentIds: [onTicket.Id]));
        var log = await workspace.Client.UploadAsync("error.log", Encoding.UTF8.GetBytes("stack trace"));
        (await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Log attached.", AttachmentIds: [log.Id]),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var timeline = await workspace.Client.GetAsync<List<TimelineItem>>($"/api/v1/tickets/{ticket.Id}/timeline");
        timeline.Where(i => i.Kind == TimelineKind.Message).Select(i => i.Attachments.Single().FileName)
            .ShouldBe(["screen shot.html", "error.log"]);

        var download = await agent.Client.GetAsync($"/api/v1/attachments/{onTicket.Id}");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        download.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);

        var reused = await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Again.", AttachmentIds: [log.Id]),
            TestJson.Options);
        reused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reused.ProblemCodeAsync()).ShouldBe("invalid-attachment");

        var someoneElses = await agent.Client.UploadAsync("mine.txt", [1, 2, 3]);
        var stolen = await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Not my file.", AttachmentIds: [someoneElses.Id]),
            TestJson.Options);
        (await stolen.ProblemCodeAsync()).ShouldBe("invalid-attachment");
    }

    [Fact]
    public async Task Uploads_reject_blocked_types_and_oversized_files()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var executable = await workspace.Client.PostAsync(
            "/api/v1/attachments",
            TestTickets.FileForm("setup.EXE", [1, 2, 3], "application/octet-stream"));
        executable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await executable.ProblemCodeAsync()).ShouldBe("file-type-blocked");

        var tooLarge = await workspace.Client.PostAsync(
            "/api/v1/attachments",
            TestTickets.FileForm("big.bin", new byte[Attachment.MaxSizeBytes + 1], "application/octet-stream"));
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLarge.ProblemCodeAsync()).ShouldBe("file-too-large");

        var viewer = await factory.AddMemberAsync(workspace, Role.Viewer);
        (await viewer.Client.PostAsync("/api/v1/attachments", TestTickets.FileForm("a.txt", [1])))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Domain_events_reach_their_handlers_even_when_another_handler_fails()
    {
        var workspace = await factory.CreateWorkspaceAsync();

        var ticket = await workspace.Client.CreateTicketAsync();

        factory.CreatedTicketEvents.ShouldContain(ticket.Id);
    }
}
