using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.Inbound;
using NexDesk.Application.Notifications;
using NexDesk.Application.Sla;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;
using NexDesk.IntegrationTests.Portal;

namespace NexDesk.IntegrationTests.Notifications;

[Collection(ApiCollection.Name)]
public sealed class NotificationsTests(ApiFactory factory)
{
    private static Task<PagedResult<NotificationDto>> Inbox(HttpClient client, string query = "") =>
        client.GetAsync<PagedResult<NotificationDto>>($"/api/v1/notifications{query}");

    [Fact]
    public async Task Assignment_and_replies_notify_the_people_involved_but_not_the_author()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");
        var requester = TestWorkspaces.UniqueEmail("casey");
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Printer jam", requester));

        await workspace.Client.PutAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/assignment",
            new AssignTicketRequest(agent.UserId, null));

        var assigned = (await Inbox(agent.Client)).Items.ShouldHaveSingleItem();
        assigned.Type.ShouldBe(NotificationType.TicketAssigned);
        assigned.TicketNumber.ShouldBe(ticket.Number);
        assigned.TicketSubject.ShouldBe("Printer jam");
        assigned.ActorName.ShouldBe("Olivia Owner");
        assigned.IsRead.ShouldBeFalse();
        (await Inbox(workspace.Client)).TotalCount.ShouldBe(0);
        factory.Emails.Last(agent.Email, "notify-assigned").Rendered.Subject
            .ShouldBe($"Ticket #{ticket.Number} was assigned to you");

        await agent.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Have you tried\nturning it off and on again?"),
            TestJson.Options);

        var reply = (await Inbox(workspace.Client)).Items.ShouldHaveSingleItem();
        reply.Type.ShouldBe(NotificationType.TicketReply);
        reply.ActorName.ShouldBe("Aaron Agent");
        reply.Preview.ShouldBe("Have you tried turning it off and on again?");
        (await Inbox(agent.Client)).TotalCount.ShouldBe(1);

        var customerEmail = factory.Emails.Last(requester, "customer-reply");
        customerEmail.Rendered.Subject.ShouldBe($"Re: Printer jam [#{ticket.Number}]");
        customerEmail.Rendered.Html.ShouldContain("Have you tried\nturning it off and on again?");
        customerEmail.Rendered.Html.ShouldContain($"/tickets/{ticket.Id}");

        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Customer is a VIP.", IsInternal: true),
            TestJson.Options);

        (await Inbox(agent.Client)).Items.First().Type.ShouldBe(NotificationType.TicketNote);
        factory.Emails.Count(requester, "customer-reply").ShouldBe(1);
        factory.Emails.Count(agent.Email, "notify-note").ShouldBe(1);
    }

    [Fact]
    public async Task A_mention_makes_a_teammate_watch_the_ticket_and_tells_them_once()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");
        var bystander = await factory.AddMemberAsync(workspace, Role.Agent, "Bea Bystander");
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Refund request"));

        var detail = await (await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("@aaron agent, can you check the invoice? @Olivia Owner @Nobody Here", IsInternal: true),
            TestJson.Options)).ReadAsync<TicketDetailDto>();

        detail.Watchers.Select(w => w.Id).ShouldContain(agent.UserId);
        detail.Watchers.Select(w => w.Id).ShouldNotContain(bystander.UserId);
        var mention = (await Inbox(agent.Client)).Items.ShouldHaveSingleItem();
        mention.Type.ShouldBe(NotificationType.Mention);
        mention.ActorName.ShouldBe("Olivia Owner");
        factory.Emails.Last(agent.Email, "notify-mention").Rendered.Subject
            .ShouldBe($"Olivia Owner mentioned you on ticket #{ticket.Number}: Refund request");
        (await Inbox(workspace.Client)).TotalCount.ShouldBe(0);
        (await Inbox(bystander.Client)).TotalCount.ShouldBe(0);

        // As a watcher now, the next note is an ordinary one.
        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Thanks!", IsInternal: true),
            TestJson.Options);
        (await Inbox(agent.Client)).Items.First().Type.ShouldBe(NotificationType.TicketNote);
    }

    [Fact]
    public async Task Customers_cannot_mention_the_team()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");

        var created = await (await factory.SendInboundAsync(
            workspace.Slug,
            new InboundEmailRequest(TestWorkspaces.UniqueEmail("mailer"), null, "Help", "@Aaron Agent please call me"))).ReadAsync<InboundEmailResult>();
        var ticket = await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{created.TicketId}");

        ticket.Watchers.ShouldNotContain(w => w.Id == agent.UserId);
        (await Inbox(agent.Client)).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Notifications_can_be_read_one_by_one_or_all_at_once()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        foreach (var subject in new[] { "One", "Two", "Three" })
        {
            await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(subject, assigneeId: agent.UserId));
        }

        (await agent.Client.GetAsync<UnreadCountDto>("/api/v1/notifications/unread-count")).Count.ShouldBe(3);
        var inbox = await Inbox(agent.Client, "?pageSize=2");
        inbox.TotalCount.ShouldBe(3);
        inbox.Items.Select(n => n.TicketSubject).ShouldBe(["Three", "Two"]);

        var afterOne = await (await agent.Client.PostAsync($"/api/v1/notifications/{inbox.Items[0].Id}/read", null))
            .ReadAsync<UnreadCountDto>();
        afterOne.Count.ShouldBe(2);
        (await Inbox(agent.Client, "?unreadOnly=true")).Items.Select(n => n.TicketSubject).ShouldBe(["Two", "One"]);

        (await (await agent.Client.PostAsync("/api/v1/notifications/read-all", null)).ReadAsync<UnreadCountDto>())
            .Count.ShouldBe(0);
        (await Inbox(agent.Client)).Items.ShouldAllBe(n => n.IsRead);
        (await agent.Client.PostAsync($"/api/v1/notifications/{Guid.NewGuid()}/read", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Preferences_switch_each_channel_off_separately()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);

        var defaults = await agent.Client.GetAsync<List<NotificationPreferenceDto>>("/api/v1/notifications/preferences");
        defaults.Select(p => p.Type).ShouldBe(Enum.GetValues<NotificationType>());
        defaults.ShouldAllBe(p => p.InApp && p.Email && p.Push);

        var saved = await (await agent.Client.PutAsJsonAsync(
            "/api/v1/notifications/preferences",
            new UpdateNotificationPreferencesRequest(
            [
                new NotificationPreferenceInput(NotificationType.TicketAssigned, InApp: false, Email: true),
                new NotificationPreferenceInput(NotificationType.TicketReply, InApp: true, Email: false),
            ]),
            TestJson.Options)).ReadAsync<List<NotificationPreferenceDto>>();
        saved.Single(p => p.Type == NotificationType.TicketAssigned).InApp.ShouldBeFalse();
        saved.Single(p => p.Type == NotificationType.SlaBreach).ShouldBe(
            new NotificationPreferenceDto(NotificationType.SlaBreach, true, true, true));

        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: agent.UserId));
        (await Inbox(agent.Client)).TotalCount.ShouldBe(0);
        factory.Emails.Count(agent.Email, "notify-assigned").ShouldBe(1);

        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Any update?"),
            TestJson.Options);
        (await Inbox(agent.Client)).Items.ShouldHaveSingleItem().Type.ShouldBe(NotificationType.TicketReply);
        factory.Emails.Count(agent.Email, "notify-reply").ShouldBe(0);

        (await agent.Client.PutAsJsonAsync(
            "/api/v1/notifications/preferences",
            new UpdateNotificationPreferencesRequest(
            [
                new NotificationPreferenceInput(NotificationType.TicketReply, true, true),
                new NotificationPreferenceInput(NotificationType.TicketReply, false, false),
            ]),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sla_alerts_reach_the_assignee_and_a_breach_is_escalated_to_admins()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.OpenAroundTheClockAsync();
        await workspace.Client.PutAsJsonAsync(
            "/api/v1/sla/policies",
            new UpdateSlaPoliciesRequest([new SlaPolicyDto(TicketPriority.Normal, 60, 600)]),
            TestJson.Options);
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: agent.UserId));
        await agent.Client.PostAsync("/api/v1/notifications/read-all", null);

        await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(52));

        var warning = (await Inbox(agent.Client, "?unreadOnly=true")).Items.ShouldHaveSingleItem();
        warning.Type.ShouldBe(NotificationType.SlaWarning);
        warning.Preview.ShouldBe("FirstResponse");
        warning.ActorName.ShouldBeNull();
        (await Inbox(workspace.Client)).TotalCount.ShouldBe(0);

        await factory.SweepSlaAsync(workspace.TenantId, ticket.CreatedAt.AddMinutes(63));

        (await Inbox(agent.Client, "?unreadOnly=true")).Items.Select(n => n.Type)
            .ShouldBe([NotificationType.SlaBreach, NotificationType.SlaWarning]);
        (await Inbox(workspace.Client)).Items.ShouldHaveSingleItem().Type.ShouldBe(NotificationType.SlaBreach);
        factory.Emails.Last(workspace.Email, "notify-sla-breach").Rendered.Subject
            .ShouldBe($"Ticket #{ticket.Number} missed its deadline");
    }

    [Fact]
    public async Task Notifications_are_private_to_their_recipient_and_their_tenant()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(a, Role.Agent);
        var colleague = await factory.AddMemberAsync(a, Role.Agent, "Carl Colleague");
        await a.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: agent.UserId));
        var notification = (await Inbox(agent.Client)).Items.ShouldHaveSingleItem();

        (await Inbox(colleague.Client)).TotalCount.ShouldBe(0);
        (await Inbox(b.Client)).TotalCount.ShouldBe(0);
        (await colleague.Client.PostAsync($"/api/v1/notifications/{notification.Id}/read", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"/api/v1/notifications/{notification.Id}/read", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await colleague.Client.PostAsync("/api/v1/notifications/read-all", null);
        await b.Client.PostAsync("/api/v1/notifications/read-all", null);

        (await agent.Client.GetAsync<UnreadCountDto>("/api/v1/notifications/unread-count")).Count.ShouldBe(1);
        (await factory.CreateBareClient().GetAsync("/api/v1/notifications")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Live_updates_reach_only_the_right_tenant_and_user()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(a, Role.Agent);
        await using var owner = await Listener.ConnectAsync(factory, a.AccessToken);
        await using var agentConnection = await Listener.ConnectAsync(factory, AccessTokenOf(agent));
        await using var outsider = await Listener.ConnectAsync(factory, b.AccessToken);

        var ticket = await a.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: agent.UserId));

        (await owner.NextTicketChange()).ShouldBe((ticket.Id, "created"));
        (await agentConnection.NextTicketChange()).ShouldBe((ticket.Id, "created"));
        var pushed = await agentConnection.NextNotification();
        pushed.Type.ShouldBe(NotificationType.TicketAssigned);
        pushed.TicketNumber.ShouldBe(ticket.Number);

        await Task.Delay(300);
        outsider.Received.ShouldBe(0);
        owner.Notifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_live_channel_rejects_connections_without_a_valid_token()
    {
        var exception = await Should.ThrowAsync<HttpRequestException>(() => Listener.ConnectAsync(factory, "not-a-token"));

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string AccessTokenOf(TestMember member) =>
        member.Client.DefaultRequestHeaders.Authorization!.Parameter!;

    /// <summary>A SignalR client connected to the in-memory test server.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private readonly HubConnection _connection;
        private readonly ConcurrentQueue<(Guid TicketId, string Change)> _ticketChanges = new();
        private readonly SemaphoreSlim _ticketSignal = new(0);
        private readonly SemaphoreSlim _notificationSignal = new(0);
        private int _received;

        private Listener(HubConnection connection) => _connection = connection;

        public ConcurrentQueue<NotificationDto> Notifications { get; } = new();

        public int Received => _received;

        public static async Task<Listener> ConnectAsync(ApiFactory factory, string accessToken)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/notifications"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                })
                .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .Build();
            var listener = new Listener(connection);
            connection.On<JsonElement>("ticketChanged", message =>
            {
                Interlocked.Increment(ref listener._received);
                listener._ticketChanges.Enqueue((message.GetProperty("ticketId").GetGuid(), message.GetProperty("change").GetString()!));
                listener._ticketSignal.Release();
            });
            connection.On<NotificationDto>("notification", notification =>
            {
                Interlocked.Increment(ref listener._received);
                listener.Notifications.Enqueue(notification);
                listener._notificationSignal.Release();
            });
            await connection.StartAsync();
            return listener;
        }

        public async Task<(Guid TicketId, string Change)> NextTicketChange()
        {
            (await _ticketSignal.WaitAsync(Timeout)).ShouldBeTrue("no ticketChanged message arrived in time");
            _ticketChanges.TryDequeue(out var change).ShouldBeTrue();
            return change;
        }

        public async Task<NotificationDto> NextNotification()
        {
            (await _notificationSignal.WaitAsync(Timeout)).ShouldBeTrue("no notification message arrived in time");
            Notifications.TryPeek(out var notification).ShouldBeTrue();
            return notification!;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
