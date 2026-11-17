using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Notifications;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Billing;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Notifications;

[Collection(ApiCollection.Name)]
public sealed class PushTests(ApiFactory factory)
{
    /// <summary>Signs in on "the phone" and registers it; returns the phone's client and its tokens.</summary>
    private async Task<(HttpClient Phone, MobileAuthResponse Auth, string PushToken)> PhoneAsync(
        string email, string password, string? pushToken = null, string installationId = "install-1")
    {
        var phone = factory.CreateBareClient();
        var auth = await (await phone.PostAsJsonAsync(
            "/api/v1/auth/mobile/login", new MobileLoginRequest(email, password, "Pixel 8"))).ReadAsync<MobileAuthResponse>();
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        pushToken ??= $"fcm-{Guid.NewGuid():N}";
        var registered = await phone.PutAsJsonAsync(
            $"/api/v1/devices/{installationId}",
            new RegisterDeviceRequest(DevicePlatform.Android, pushToken, "1.0.0+1", "en"),
            TestJson.Options);
        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (phone, auth, pushToken);
    }

    [Fact]
    public async Task Each_ticket_event_pushes_to_the_people_involved_and_never_carries_the_message()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");
        var (_, _, agentPhone) = await PhoneAsync(agent.Email, agent.Password);
        var (_, _, ownerPhone) = await PhoneAsync(workspace.Email, workspace.Password);
        var ticket = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket("Printer jam"));

        await workspace.Client.PutAsJsonAsync($"/api/v1/tickets/{ticket.Id}/assignment", new AssignTicketRequest(agent.UserId, null));
        var assigned = factory.Pushes.SentTo(agentPhone).ShouldHaveSingleItem();
        assigned.Title.ShouldBe($"Olivia Owner assigned #{ticket.Number} to you");
        assigned.Body.ShouldBe("Printer jam");
        assigned.Data["type"].ShouldBe(nameof(NotificationType.TicketAssigned));
        assigned.Data["link"].ShouldBe($"/app/tickets/{ticket.Id}");
        assigned.AndroidChannel.ShouldBe("tickets");
        assigned.Badge.ShouldBe(1);
        factory.Pushes.SentTo(ownerPhone).ShouldBeEmpty();

        await workspace.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("Secret: the VIP's door code is 4521", IsInternal: true),
            TestJson.Options);
        var note = factory.Pushes.SentTo(agentPhone)[^1];
        note.Title.ShouldBe($"Olivia Owner added a note on #{ticket.Number}");
        string.Join(" ", note.Title, note.Body, string.Join(" ", note.Data.Values)).ShouldNotContain("4521");

        await agent.Client.PostAsJsonAsync(
            $"/api/v1/tickets/{ticket.Id}/messages",
            new AddMessageRequest("On it, @Olivia Owner"),
            TestJson.Options);
        factory.Pushes.SentTo(ownerPhone).ShouldHaveSingleItem().Title.ShouldBe($"Aaron Agent mentioned you on #{ticket.Number}");
        factory.Pushes.SentTo(agentPhone).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Arabic_speakers_get_arabic_text_and_sla_breaches_are_time_sensitive()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent, "Aaron Agent");
        (await agent.Client.PutAsJsonAsync("/api/v1/users/me/profile", new UpdateProfileRequest("Aaron Agent", "ar"))).EnsureSuccessStatusCode();
        var (_, _, agentPhone) = await PhoneAsync(agent.Email, agent.Password);
        await workspace.Client.OpenAroundTheClockAsync();
        var ticket = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("طابعة معطلة", priority: TicketPriority.Urgent, assigneeId: agent.UserId));

        factory.Pushes.SentTo(agentPhone)[^1].Title.ShouldBe($"أسند إليك Olivia Owner التذكرة رقم {ticket.Number}");

        await factory.SweepSlaAsync(workspace.TenantId, DateTimeOffset.UtcNow.AddDays(3));
        var breach = factory.Pushes.SentTo(agentPhone).Last(p => p.Data["type"] == nameof(NotificationType.SlaBreach));
        breach.Title.ShouldBe($"تجاوزت التذكرة رقم {ticket.Number} موعدها");
        breach.AndroidChannel.ShouldBe("sla");
        breach.TimeSensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task Push_has_its_own_switch_and_older_clients_cannot_flip_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var (_, _, phone) = await PhoneAsync(agent.Email, agent.Password);

        var saved = await (await agent.Client.PutAsJsonAsync(
            "/api/v1/notifications/preferences",
            new UpdateNotificationPreferencesRequest([new NotificationPreferenceInput(NotificationType.TicketAssigned, true, true, Push: false)]),
            TestJson.Options)).ReadAsync<List<NotificationPreferenceDto>>();
        saved.Single(p => p.Type == NotificationType.TicketAssigned).ShouldBe(
            new NotificationPreferenceDto(NotificationType.TicketAssigned, true, true, false));

        // The web app before 16.3 sends only inApp and email.
        var raw = await agent.Client.PutAsJsonAsync(
            "/api/v1/notifications/preferences",
            new { preferences = new[] { new { type = "TicketAssigned", inApp = true, email = false } } });
        (await raw.ReadAsync<List<NotificationPreferenceDto>>()).Single(p => p.Type == NotificationType.TicketAssigned)
            .ShouldBe(new NotificationPreferenceDto(NotificationType.TicketAssigned, true, false, false));

        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: agent.UserId));
        factory.Pushes.SentTo(phone).ShouldBeEmpty();
        factory.Pushes.QueuedFor(agent.UserId).ShouldBeEmpty();
        (await agent.Client.GetAsync<UnreadCountDto>("/api/v1/notifications/unread-count")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_phone_whose_sign_in_ended_gets_nothing()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var (phone, auth, token) = await PhoneAsync(agent.Email, agent.Password);
        var (_, _, otherToken) = await PhoneAsync(agent.Email, agent.Password, installationId: "install-2");

        (await phone.PostAsJsonAsync("/api/v1/auth/mobile/logout", new MobileRefreshRequest(auth.RefreshToken)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: agent.UserId));

        factory.Pushes.SentTo(token).ShouldBeEmpty();
        factory.Pushes.SentTo(otherToken).ShouldHaveSingleItem();

        (await workspace.Client.PostAsync($"/api/v1/users/{agent.UserId}/deactivate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = factory.Pushes.Sent.Count;
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: workspace.OwnerId));
        factory.Pushes.SentTo(otherToken).Count.ShouldBe(1);
        factory.Pushes.Sent.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_handed_over_phone_and_an_uninstalled_app_stop_getting_pushes()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var first = await factory.AddMemberAsync(workspace, Role.Agent);
        var second = await factory.AddMemberAsync(workspace, Role.Agent);
        var shared = $"fcm-{Guid.NewGuid():N}";
        await PhoneAsync(first.Email, first.Password, shared);
        await PhoneAsync(second.Email, second.Password, shared);

        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: first.UserId));
        factory.Pushes.SentTo(shared).ShouldBeEmpty();
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: second.UserId));
        factory.Pushes.SentTo(shared).ShouldHaveSingleItem();

        var gone = $"{CapturedPushes.UnregisteredPrefix}{Guid.NewGuid():N}";
        await PhoneAsync(first.Email, first.Password, gone, "install-gone");
        await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(assigneeId: first.UserId));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Persistence.AppDbContext>();
        db.Devices.IgnoreQueryFilters().Any(d => d.PushToken == gone).ShouldBeFalse();
    }

    [Fact]
    public async Task Devices_belong_to_one_workspace_and_need_a_mobile_sign_in()
    {
        var brightpath = await factory.CreateWorkspaceAsync();
        var souqna = await factory.CreateWorkspaceAsync();
        var token = $"fcm-{Guid.NewGuid():N}";
        await PhoneAsync(souqna.Email, souqna.Password, token, "souqna-phone");

        // Another workspace removing "its" install of the same id touches only its own rows.
        (await brightpath.Client.DeleteAsync("/api/v1/devices/souqna-phone")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Persistence.AppDbContext>();
            db.Devices.IgnoreQueryFilters().Single(d => d.PushToken == token).TenantId.ShouldBe(souqna.TenantId);
        }

        var invalid = await brightpath.Client.PutAsJsonAsync(
            "/api/v1/devices/bad id", new RegisterDeviceRequest(DevicePlatform.Ios, "", null, null), TestJson.Options);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var admin = factory.CreateBareClient();
        var adminAuth = await (await admin.PostAsJsonAsync(
            "/api/v1/auth/mobile/login", new MobileLoginRequest(ApiFactory.PlatformAdminEmail, ApiFactory.PlatformAdminPassword, null)))
            .ReadAsync<MobileAuthResponse>();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);
        var refused = await admin.PutAsJsonAsync(
            "/api/v1/devices/admin-phone", new RegisterDeviceRequest(DevicePlatform.Ios, "fcm-admin", null, null), TestJson.Options);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ProblemCodeAsync()).ShouldBe("insufficient-role");
    }

    [Fact]
    public async Task Cleanup_removes_phones_whose_sign_in_ended()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var (phone, auth, _) = await PhoneAsync(workspace.Email, workspace.Password, installationId: "cleanup-phone");
        await phone.PostAsJsonAsync("/api/v1/auth/mobile/logout", new MobileRefreshRequest(auth.RefreshToken));

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CleanupJob>().RunAsync(CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<NexDesk.Infrastructure.Persistence.AppDbContext>();
        db.Devices.IgnoreQueryFilters().Any(d => d.InstallationId == "cleanup-phone").ShouldBeFalse();
    }
}
