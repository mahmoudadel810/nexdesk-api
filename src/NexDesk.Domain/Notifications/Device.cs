using NexDesk.Domain.Common;

namespace NexDesk.Domain.Notifications;

public enum DevicePlatform
{
    Android,
    Ios,
}

/// <summary>
/// A phone with the NexDesk app, registered for push notifications. It belongs to one sign-in (the refresh-token
/// family behind the access token that registered it), so it stops getting pushes once that sign-in ends.
/// </summary>
public sealed class Device : Entity, ITenantOwned
{
    public const int InstallationIdMaxLength = 64;
    public const int PushTokenMaxLength = 512;
    public const int AppVersionMaxLength = 32;
    public const int LocaleMaxLength = 16;

    private Device()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>A random id the app creates once per install.</summary>
    public string InstallationId { get; private set; } = null!;

    public DevicePlatform Platform { get; private set; }

    /// <summary>The push service's registration token for this install.</summary>
    public string PushToken { get; private set; } = null!;

    public Guid RefreshFamilyId { get; private set; }

    public string? AppVersion { get; private set; }

    public string? Locale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public static Device Register(
        Guid userId,
        string installationId,
        DevicePlatform platform,
        string pushToken,
        Guid refreshFamilyId,
        string? appVersion,
        string? locale,
        DateTimeOffset now)
    {
        var device = new Device { UserId = userId, InstallationId = installationId, CreatedAt = now };
        device.Refresh(platform, pushToken, refreshFamilyId, appVersion, locale, now);
        return device;
    }

    public void Refresh(
        DevicePlatform platform,
        string pushToken,
        Guid refreshFamilyId,
        string? appVersion,
        string? locale,
        DateTimeOffset now)
    {
        Platform = platform;
        PushToken = pushToken;
        RefreshFamilyId = refreshFamilyId;
        AppVersion = appVersion;
        Locale = locale;
        LastSeenAt = now;
    }
}
