using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Audit;
using NexDesk.Application.Common;
using NexDesk.Domain.Audit;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Audit;

[Collection(ApiCollection.Name)]
public sealed class AuditLogTests(ApiFactory factory)
{
    [Fact]
    public async Task A_role_change_is_logged_with_who_did_it_the_values_before_and_after_and_the_address()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");

        (await workspace.Client.PutAsJsonAsync($"/api/v1/users/{agent.UserId}/role", new ChangeRoleRequest(Role.Admin)))
            .EnsureSuccessStatusCode();

        var entry = (await Logs(workspace.Client, $"?entityType=user&operation=Updated&actorId={workspace.OwnerId}"))
            .Items.Where(e => e.EntityId == agent.UserId).ShouldHaveSingleItem();
        entry.EntityId.ShouldBe(agent.UserId);
        entry.EntityLabel.ShouldBe("Aaron Agent");
        entry.Actor.ShouldBe(new NexDesk.Application.Tickets.ActorDto(ActorType.Staff, workspace.OwnerId, "Olivia Owner"));
        entry.Changes.ShouldContain(new AuditChange("role", "Agent", "Admin"));
        entry.IpAddress.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Ticket_edits_and_deletes_are_logged_but_system_bookkeeping_is_not()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Printer on fire"));

        (await workspace.Client.PatchAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}",
            new UpdateTicketRequest(null, null, TicketPriority.Urgent, null, null))).EnsureSuccessStatusCode();
        (await workspace.Client.DeleteAsync($"/api/v1/tickets/{ticket.Id}")).EnsureSuccessStatusCode();

        var entries = (await Logs(workspace.Client, $"?search={ticket.Id}")).Items;
        entries.Select(e => e.Operation).ShouldBe([AuditOperation.Deleted, AuditOperation.Updated, AuditOperation.Created]);
        entries.ShouldAllBe(e => e.EntityType == "ticket" && e.EntityLabel == $"#{ticket.Number} Printer on fire");

        var update = entries[1];
        update.Changes.ShouldBe([new AuditChange("priority", "Normal", "Urgent")]);
        entries.SelectMany(e => e.Changes).ShouldNotContain(c => c.Field == "lastActivityAt" || c.Field.EndsWith("DueAt"));
        entries[0].Changes.ShouldContain(new AuditChange("subject", "Printer on fire", null));
    }

    [Fact]
    public async Task Secrets_are_never_copied_into_the_log()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await factory.AddMemberAsync(workspace, Role.Agent);

        var fields = (await Logs(workspace.Client, "?pageSize=100")).Items.SelectMany(e => e.Changes).Select(c => c.Field).ToList();

        fields.ShouldContain("email");
        fields.ShouldNotContain("passwordHash");
        fields.ShouldNotContain("tokenHash");
        fields.ShouldNotContain("securityVersion");
    }

    [Fact]
    public async Task The_log_can_be_filtered_by_record_name_and_period()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Refund for order 1001"));
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Shipping question"));

        (await Logs(workspace.Client, "?search=REFUND")).Items.ShouldHaveSingleItem()
            .EntityLabel.ShouldBe("#1 Refund for order 1001");
        (await Logs(workspace.Client, $"?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}"))
            .TotalCount.ShouldBe(0);

        var invalid = await workspace.Client.GetAsync(
            $"/api/v1/audit-logs?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}"
            + $"&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"))}");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_admins_and_owners_can_read_the_log()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var viewer = await factory.AddMemberAsync(workspace, Role.Viewer);
        var admin = await factory.AddMemberAsync(workspace, Role.Admin);

        (await agent.Client.GetAsync("/api/v1/audit-logs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.Client.GetAsync("/api/v1/audit-logs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Client.GetAsync("/api/v1/audit-logs")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_workspace_never_sees_another_workspaces_log()
    {
        var a = await factory.CreateWorkspaceAsync();
        var b = await factory.CreateWorkspaceAsync();
        var ticketOfA = await a.Client.CreateTicketAsync(TestTickets.NewTicket("Private to A"));

        var logOfB = await Logs(b.Client, "?pageSize=100");
        logOfB.Items.ShouldNotContain(e => e.EntityId == ticketOfA.Id || e.EntityId == a.OwnerId || e.EntityId == a.TenantId);
        (await Logs(b.Client, $"?search={ticketOfA.Id}")).TotalCount.ShouldBe(0);
        (await Logs(a.Client, $"?search={ticketOfA.Id}")).TotalCount.ShouldBe(1);
    }

    private static Task<PagedResult<AuditLogDto>> Logs(HttpClient client, string query) =>
        client.GetAsync<PagedResult<AuditLogDto>>($"/api/v1/audit-logs{query}");
}
