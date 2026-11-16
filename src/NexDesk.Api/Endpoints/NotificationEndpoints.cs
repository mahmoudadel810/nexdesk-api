using NexDesk.Application.Common;
using NexDesk.Application.Notifications;

namespace NexDesk.Api.Endpoints;

public sealed record ListNotificationsRequest(bool UnreadOnly = false, int Page = 1, int PageSize = Paging.DefaultPageSize);

public sealed record UpdateNotificationPreferencesRequest(IReadOnlyList<NotificationPreferenceDto> Preferences);

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization();

        notifications.MapGet("/", async (
            [AsParameters] ListNotificationsRequest request,
            IQueryHandler<ListNotificationsQuery, PagedResult<NotificationDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListNotificationsQuery(request.UnreadOnly, request.Page, request.PageSize), ct)).ToOk())
            .WithName("ListNotifications")
            .WithSummary("Lists the signed-in user's notifications, newest first.")
            .Produces<PagedResult<NotificationDto>>();

        notifications.MapGet("/unread-count", async (
            IQueryHandler<GetUnreadCountQuery, UnreadCountDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetUnreadCountQuery(), ct)).ToOk())
            .WithName("GetUnreadNotificationCount")
            .WithSummary("Returns how many notifications the signed-in user has not read yet.")
            .Produces<UnreadCountDto>();

        notifications.MapPost("/{id:guid}/read", async (
            Guid id,
            ICommandHandler<MarkNotificationsReadCommand, UnreadCountDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new MarkNotificationsReadCommand(id), ct)).ToOk())
            .WithName("MarkNotificationRead")
            .WithSummary("Marks one notification as read and returns the remaining unread count.")
            .Produces<UnreadCountDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        notifications.MapPost("/read-all", async (
            ICommandHandler<MarkNotificationsReadCommand, UnreadCountDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new MarkNotificationsReadCommand(null), ct)).ToOk())
            .WithName("MarkAllNotificationsRead")
            .WithSummary("Marks every notification of the signed-in user as read.")
            .Produces<UnreadCountDto>();

        notifications.MapGet("/preferences", async (
            IQueryHandler<GetNotificationPreferencesQuery, IReadOnlyList<NotificationPreferenceDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetNotificationPreferencesQuery(), ct)).ToOk())
            .WithName("GetNotificationPreferences")
            .WithSummary("Returns, per notification type, whether the user wants it in the app and by email.")
            .Produces<IReadOnlyList<NotificationPreferenceDto>>();

        notifications.MapPut("/preferences", async (
            UpdateNotificationPreferencesRequest request,
            ICommandHandler<UpdateNotificationPreferencesCommand, IReadOnlyList<NotificationPreferenceDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new UpdateNotificationPreferencesCommand(request.Preferences), ct)).ToOk())
            .WithName("UpdateNotificationPreferences")
            .WithSummary("Switches notification types on or off for the in-app and email channels.")
            .Produces<IReadOnlyList<NotificationPreferenceDto>>()
            .ProducesValidationProblem();

        return api;
    }
}
