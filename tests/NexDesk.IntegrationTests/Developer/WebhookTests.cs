using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Audit;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Domain.Billing;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Developer;

[Collection(ApiCollection.Name)]
public sealed class WebhookTests(ApiFactory factory)
{
    private const string Webhooks = "/api/v1/webhooks";

    [Fact]
    public async Task Subscribed_events_are_posted_signed_with_the_endpoints_secret()
    {
        var workspace = await BusinessWorkspaceAsync();
        var url = WebhookReceiver.NewUrl();
        var created = await CreateAsync(workspace, url, WebhookEvents.TicketCreated);
        created.Secret.ShouldStartWith("whsec_");

        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Printer on fire"));

        var request = factory.Webhooks.To(url).ShouldHaveSingleItem();
        request.Headers["X-NexDesk-Event"].ShouldBe(WebhookEvents.TicketCreated);
        var timestamp = long.Parse(request.Headers["X-NexDesk-Timestamp"]);
        request.Headers["X-NexDesk-Signature"].ShouldBe(WebhookSignature.Sign(created.Secret, timestamp, request.Body));
        request.Headers["User-Agent"].ShouldContain("NexDesk-Webhooks");

        var body = JsonDocument.Parse(request.Body).RootElement;
        body.GetProperty("event").GetString().ShouldBe(WebhookEvents.TicketCreated);
        body.GetProperty("data").GetProperty("ticket").GetProperty("id").GetGuid().ShouldBe(ticket.Id);
        body.GetProperty("data").GetProperty("ticket").GetProperty("subject").GetString().ShouldBe("Printer on fire");

        var delivery = (await Deliveries(workspace, created.Endpoint.Id)).Items.ShouldHaveSingleItem();
        delivery.Id.ToString().ShouldBe(request.Headers["X-NexDesk-Delivery"]);
        (delivery.Status, delivery.Attempts, delivery.ResponseStatus).ShouldBe((WebhookDeliveryStatus.Succeeded, 1, 200));
        delivery.ResponseBody.ShouldBe("answered 200");
        delivery.Payload.ShouldBe(request.Body);
    }

    [Fact]
    public async Task Only_subscribed_events_are_sent_and_internal_notes_never_are()
    {
        var workspace = await BusinessWorkspaceAsync();
        var url = WebhookReceiver.NewUrl();
        await CreateAsync(workspace, url, WebhookEvents.TicketReplied, WebhookEvents.TicketSolved);
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());

        (await workspace.Client.PostAsJsonAsync($"/api/v1/tickets/{ticket.Id}/messages", new AddMessageRequest("Checked logs", IsInternal: true)))
            .EnsureSuccessStatusCode();
        factory.Webhooks.To(url).ShouldBeEmpty();

        (await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Fixed now", SetStatus: TicketStatus.Solved))).EnsureSuccessStatusCode();

        var events = factory.Webhooks.To(url).Select(r => r.Headers["X-NexDesk-Event"]).ToList();
        events.ShouldBe([WebhookEvents.TicketReplied, WebhookEvents.TicketSolved], ignoreOrder: true);
        var reply = factory.Webhooks.To(url).Single(r => r.Headers["X-NexDesk-Event"] == WebhookEvents.TicketReplied);
        JsonDocument.Parse(reply.Body).RootElement.GetProperty("data").GetProperty("details")
            .GetProperty("message").GetProperty("body").GetString().ShouldBe("Fixed now");
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_with_backoff_until_they_succeed_or_give_up()
    {
        var workspace = await BusinessWorkspaceAsync();
        var url = WebhookReceiver.NewUrl();
        var endpoint = (await CreateAsync(workspace, url, WebhookEvents.TicketCreated)).Endpoint;
        factory.Webhooks.Answer(url, HttpStatusCode.InternalServerError);

        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());

        var first = (await Deliveries(workspace, endpoint.Id)).Items.Single();
        (first.Status, first.Attempts, first.ResponseStatus).ShouldBe((WebhookDeliveryStatus.Pending, 1, 500));
        first.Error.ShouldBe("The endpoint answered 500.");
        (first.NextAttemptAt - first.LastAttemptAt).ShouldBe(TimeSpan.FromMinutes(1));

        (await factory.RunScheduledWebhooksAsync(workspace.TenantId)).ShouldBe(1);
        var second = (await Deliveries(workspace, endpoint.Id)).Items.Single();
        second.Attempts.ShouldBe(2);
        (second.NextAttemptAt - second.LastAttemptAt).ShouldBe(TimeSpan.FromMinutes(5));

        factory.Webhooks.Answer(url, HttpStatusCode.NoContent);
        await factory.RunScheduledWebhooksAsync(workspace.TenantId);
        var delivered = (await Deliveries(workspace, endpoint.Id)).Items.Single();
        (delivered.Status, delivered.Attempts, delivered.ResponseStatus).ShouldBe((WebhookDeliveryStatus.Succeeded, 3, 204));
        delivered.NextAttemptAt.ShouldBeNull();

        // A receiver that never recovers is given up on after the last attempt.
        factory.Webhooks.Answer(url, HttpStatusCode.BadGateway);
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());
        while (await factory.RunScheduledWebhooksAsync(workspace.TenantId) > 0)
        {
        }

        var abandoned = (await Deliveries(workspace, endpoint.Id)).Items.First();
        (abandoned.Status, abandoned.Attempts).ShouldBe((WebhookDeliveryStatus.Failed, WebhookDelivery.MaxAttempts));
        abandoned.NextAttemptAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_resend_posts_the_same_payload_again_as_a_new_delivery()
    {
        var workspace = await BusinessWorkspaceAsync();
        var url = WebhookReceiver.NewUrl();
        var endpoint = (await CreateAsync(workspace, url, WebhookEvents.TicketCreated)).Endpoint;
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());
        var original = (await Deliveries(workspace, endpoint.Id)).Items.Single();

        var resent = await workspace.Client.PostAsync($"{Webhooks}/deliveries/{original.Id}/resend", null);

        resent.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var log = (await Deliveries(workspace, endpoint.Id)).Items;
        log.Count.ShouldBe(2);
        var copy = log.Single(d => d.ResentFromId == original.Id);
        copy.Status.ShouldBe(WebhookDeliveryStatus.Succeeded);
        copy.Payload.ShouldBe(original.Payload);
        factory.Webhooks.To(url).Select(r => r.Body).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_ping_checks_the_endpoint_and_switched_off_endpoints_receive_nothing_else()
    {
        var workspace = await BusinessWorkspaceAsync();
        var url = WebhookReceiver.NewUrl();
        var endpoint = (await CreateAsync(workspace, url, WebhookEvents.TicketCreated)).Endpoint;
        (await workspace.Client.PutAsJsonAsync(
            $"{Webhooks}/{endpoint.Id}",
            new UpdateWebhookRequest(url, [WebhookEvents.TicketCreated], IsEnabled: false))).EnsureSuccessStatusCode();

        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());
        factory.Webhooks.To(url).ShouldBeEmpty();

        (await workspace.Client.PostAsync($"{Webhooks}/{endpoint.Id}/ping", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Webhooks.To(url).ShouldHaveSingleItem().Headers["X-NexDesk-Event"].ShouldBe(WebhookEvents.Ping);
    }

    [Fact]
    public async Task Rotating_the_secret_signs_from_then_on_with_the_new_one_and_secrets_never_show_again()
    {
        var workspace = await BusinessWorkspaceAsync();
        var url = WebhookReceiver.NewUrl();
        var created = await CreateAsync(workspace, url, WebhookEvents.TicketCreated);

        var rotated = await (await workspace.Client.PostAsync($"{Webhooks}/{created.Endpoint.Id}/rotate-secret", null))
            .ReadAsync<WebhookSecretDto>();
        rotated.Secret.ShouldNotBe(created.Secret);
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket());

        var request = factory.Webhooks.To(url).Single();
        request.Headers["X-NexDesk-Signature"]
            .ShouldBe(WebhookSignature.Sign(rotated.Secret, long.Parse(request.Headers["X-NexDesk-Timestamp"]), request.Body));

        var listed = await workspace.Client.GetStringAsync(Webhooks);
        listed.ShouldNotContain(created.Secret);
        listed.ShouldNotContain(rotated.Secret);
        var audit = await workspace.Client.GetAsync<PagedResult<AuditLogDto>>("/api/v1/audit-logs?entityType=webhook");
        audit.Items.Count.ShouldBe(1);
        audit.Items.SelectMany(e => e.Changes).ShouldNotContain(c => c.Field == "secret");
    }

    [Fact]
    public async Task Webhooks_need_a_plan_with_api_access_and_valid_settings()
    {
        var free = await factory.CreateWorkspaceAsync();
        var refused = await free.Client.PostAsJsonAsync(Webhooks, new CreateWebhookRequest(WebhookReceiver.NewUrl(), [WebhookEvents.TicketCreated]));
        refused.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        var workspace = await BusinessWorkspaceAsync();
        foreach (var request in new[]
        {
            new CreateWebhookRequest("not a url", [WebhookEvents.TicketCreated]),
            new CreateWebhookRequest("ftp://files.example.com/drop", [WebhookEvents.TicketCreated]),
            new CreateWebhookRequest("https://user:pass@hooks.example.com/x", [WebhookEvents.TicketCreated]),
            new CreateWebhookRequest(WebhookReceiver.NewUrl(), []),
            new CreateWebhookRequest(WebhookReceiver.NewUrl(), ["ticket.deleted"]),
        })
        {
            (await workspace.Client.PostAsJsonAsync(Webhooks, request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        (await agent.Client.GetAsync(Webhooks)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_workspace_never_sees_or_triggers_another_workspaces_webhooks()
    {
        var a = await BusinessWorkspaceAsync();
        var b = await BusinessWorkspaceAsync();
        var urlOfA = WebhookReceiver.NewUrl();
        var endpointOfA = (await CreateAsync(a, urlOfA, WebhookEvents.TicketCreated)).Endpoint;
        await a.Client.CreateTicketAsync(TestTickets.NewTicket());
        var deliveryOfA = (await Deliveries(a, endpointOfA.Id)).Items.Single();

        await b.Client.CreateTicketAsync(TestTickets.NewTicket());
        factory.Webhooks.To(urlOfA).Count.ShouldBe(1);

        (await b.Client.GetAsync<List<WebhookEndpointDto>>(Webhooks)).ShouldBeEmpty();
        var update = new UpdateWebhookRequest("https://evil.example.com", [WebhookEvents.TicketCreated], true);
        (await b.Client.PutAsJsonAsync($"{Webhooks}/{endpointOfA.Id}", update)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"{Webhooks}/{endpointOfA.Id}/rotate-secret", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"{Webhooks}/{endpointOfA.Id}/ping", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.GetAsync($"{Webhooks}/{endpointOfA.Id}/deliveries")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PostAsync($"{Webhooks}/deliveries/{deliveryOfA.Id}/resend", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.DeleteAsync($"{Webhooks}/{endpointOfA.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await a.Client.GetAsync<List<WebhookEndpointDto>>(Webhooks)).ShouldHaveSingleItem().Url.ShouldBe(urlOfA);
        (await Deliveries(a, endpointOfA.Id)).TotalCount.ShouldBe(1);
        factory.Webhooks.To(urlOfA).Count.ShouldBe(1);
    }

    private async Task<TestWorkspace> BusinessWorkspaceAsync()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.ChangePlanAsync(Plan.Business);
        return workspace;
    }

    private static async Task<WebhookSecretDto> CreateAsync(TestWorkspace workspace, string url, params string[] events) =>
        await (await workspace.Client.PostAsJsonAsync(Webhooks, new CreateWebhookRequest(url, events))).ReadAsync<WebhookSecretDto>();

    private static Task<PagedResult<WebhookDeliveryDto>> Deliveries(TestWorkspace workspace, Guid endpointId) =>
        workspace.Client.GetAsync<PagedResult<WebhookDeliveryDto>>($"{Webhooks}/{endpointId}/deliveries");
}
