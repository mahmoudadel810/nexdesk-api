using NexDesk.Application.Common;
using NexDesk.Application.Notifications;
using NexDesk.Domain.Notifications;

namespace NexDesk.Api.Endpoints;

public sealed record RegisterDeviceRequest(DevicePlatform Platform, string PushToken, string? AppVersion, string? Locale);

public static class DeviceEndpoints
{
    public static RouteGroupBuilder MapDeviceEndpoints(this RouteGroupBuilder api)
    {
        var devices = api.MapGroup("/devices").WithTags("Devices").RequireAuthorization();

        devices.MapPut("/{installationId}", async (
            string installationId,
            RegisterDeviceRequest request,
            ICommandHandler<RegisterDeviceCommand, DeviceDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new RegisterDeviceCommand(installationId, request.Platform, request.PushToken, request.AppVersion, request.Locale),
                ct)).ToOk())
            .WithName("RegisterDevice")
            .WithSummary("Registers this install of the mobile app for push notifications, or refreshes its push token.")
            .Produces<DeviceDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        devices.MapDelete("/{installationId}", async (
            string installationId,
            ICommandHandler<RemoveDeviceCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new RemoveDeviceCommand(installationId), ct)).ToNoContent())
            .WithName("RemoveDevice")
            .WithSummary("Stops push notifications to this install. Removing one that is not registered is fine.")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }
}
