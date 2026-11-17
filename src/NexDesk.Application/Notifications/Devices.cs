using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Notifications;

namespace NexDesk.Application.Notifications;

public sealed record DeviceDto(
    string InstallationId,
    DevicePlatform Platform,
    string? AppVersion,
    string? Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt)
{
    public static DeviceDto From(Device d) => new(d.InstallationId, d.Platform, d.AppVersion, d.Locale, d.CreatedAt, d.LastSeenAt);
}

public static class DeviceErrors
{
    public static readonly Error WorkspaceOnly =
        Error.Forbidden("insufficient-role", "Only workspace members can register a phone for notifications.");

    public static readonly Error SignInRequired =
        Error.Forbidden("session-required", "Sign in again on this phone before registering it for notifications.");
}

public sealed record RegisterDeviceCommand(
    string InstallationId,
    DevicePlatform Platform,
    string PushToken,
    string? AppVersion,
    string? Locale) : ICommand<DeviceDto>;

internal sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceValidator()
    {
        RuleFor(c => c.InstallationId).NotEmpty().MaximumLength(Device.InstallationIdMaxLength)
            .Matches("^[A-Za-z0-9._-]+$").WithMessage("Use letters, digits, dots, dashes and underscores only.");
        RuleFor(c => c.Platform).IsInEnum();
        RuleFor(c => c.PushToken).NotEmpty().MaximumLength(Device.PushTokenMaxLength);
        RuleFor(c => c.AppVersion).MaximumLength(Device.AppVersionMaxLength);
        RuleFor(c => c.Locale).MaximumLength(Device.LocaleMaxLength);
    }
}

/// <summary>
/// Registers this install of the app, or refreshes its push token and last-seen time. A push token belongs to one
/// phone, so if someone else had it (the phone was handed over), their registration is removed first.
/// </summary>
internal sealed class RegisterDeviceHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    ITenantContext tenant,
    IDeviceTakeover takeover,
    TimeProvider clock) : ICommandHandler<RegisterDeviceCommand, DeviceDto>
{
    public async Task<Result<DeviceDto>> Handle(RegisterDeviceCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        if (currentUser.IsPlatformAdmin || tenant.TenantId is null)
        {
            return DeviceErrors.WorkspaceOnly;
        }

        if (currentUser.SessionId is not { } sessionId)
        {
            return DeviceErrors.SignInRequired;
        }

        await takeover.ReleaseAsync(command.PushToken, userId, command.InstallationId, cancellationToken);

        var now = clock.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.UserId == userId && d.InstallationId == command.InstallationId, cancellationToken);
        if (device is null)
        {
            device = Device.Register(
                userId, command.InstallationId, command.Platform, command.PushToken, sessionId, command.AppVersion, command.Locale, now);
            db.Devices.Add(device);
        }
        else
        {
            device.Refresh(command.Platform, command.PushToken, sessionId, command.AppVersion, command.Locale, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return DeviceDto.From(device);
    }
}

public sealed record RemoveDeviceCommand(string InstallationId) : ICommand<Success>;

internal sealed class RemoveDeviceHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<RemoveDeviceCommand, Success>
{
    public async Task<Result<Success>> Handle(RemoveDeviceCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        await db.Devices
            .Where(d => d.UserId == userId && d.InstallationId == command.InstallationId)
            .ExecuteDeleteAsync(cancellationToken);
        return default(Success);
    }
}

/// <summary>
/// Removes other registrations of a push token, whichever workspace they are in. The one place devices cross
/// tenants: it deletes by token and returns nothing, so it never reveals what it found.
/// </summary>
public interface IDeviceTakeover
{
    Task ReleaseAsync(string pushToken, Guid keepUserId, string keepInstallationId, CancellationToken cancellationToken);
}
