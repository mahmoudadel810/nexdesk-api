using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexDesk.Application.Common;
using NexDesk.Infrastructure.Identity;

namespace NexDesk.Api.Realtime;

/// <summary>
/// Live channel for staff. Each connection joins the group of its tenant and of its user, both taken from
/// the access token, so a client can never subscribe to another workspace.
/// </summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string TicketChanged = "ticketChanged";
    public const string NotificationReceived = "notification";

    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId:N}";

    public static string UserGroup(Guid userId) => $"user:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (Guid.TryParse(user?.FindFirst(NexDeskClaims.TenantId)?.Value, out var tenantId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        }

        if (Guid.TryParse(user?.FindFirst(NexDeskClaims.Subject)?.Value, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        await base.OnConnectedAsync();
    }
}

public sealed record TicketChangedMessage(Guid TicketId, string Change);

internal sealed class SignalRRealtimeNotifier(IHubContext<NotificationsHub> hub) : IRealtimeNotifier
{
    public Task TicketChangedAsync(Guid tenantId, Guid ticketId, string change, CancellationToken cancellationToken) =>
        hub.Clients.Group(NotificationsHub.TenantGroup(tenantId))
            .SendAsync(NotificationsHub.TicketChanged, new TicketChangedMessage(ticketId, change), cancellationToken);

    public Task NotificationAsync(Guid userId, object notification, CancellationToken cancellationToken) =>
        hub.Clients.Group(NotificationsHub.UserGroup(userId))
            .SendAsync(NotificationsHub.NotificationReceived, notification, cancellationToken);
}
