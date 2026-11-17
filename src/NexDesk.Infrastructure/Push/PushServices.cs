using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NexDesk.Application.Common;
using NexDesk.Application.Notifications;
using NexDesk.Domain.Notifications;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Push;

public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary><c>Log</c> (the default) writes a line per push; <c>Fcm</c> sends through Firebase Cloud Messaging.</summary>
    public string Provider { get; set; } = "Log";

    public FcmOptions Fcm { get; set; } = new();
}

public sealed class FcmOptions
{
    public string? ProjectId { get; set; }

    /// <summary>The service account key file's JSON. A secret: set it in the environment, never in a file in Git.</summary>
    public string? ServiceAccountJson { get; set; }
}

public sealed record PushTarget(DevicePlatform Platform, string Token);

/// <summary>What a phone shows: a title, the ticket subject, and data the app needs to open the ticket.</summary>
public sealed record PushPayload(
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    string AndroidChannel,
    string CollapseKey,
    int Badge,
    bool TimeSensitive);

public enum PushOutcome
{
    Sent,

    /// <summary>The push service no longer knows the token: the app was removed or the token replaced.</summary>
    Unregistered,

    /// <summary>A temporary failure, such as throttling or an outage.</summary>
    RetryLater,

    /// <summary>Anything else, such as a configuration problem. Logged, not retried.</summary>
    Failed,
}

public interface IPushSender
{
    Task<PushOutcome> SendAsync(PushTarget target, PushPayload payload, CancellationToken cancellationToken);
}

/// <summary>Builds the lock-screen text in the recipient's language. Message bodies never go into a push.</summary>
public static class PushPayloads
{
    public static PushPayload Build(PushMessage message, int unread)
    {
        var arabic = message.Language == "ar";
        var number = message.TicketNumber.ToString(CultureInfo.InvariantCulture);
        var actor = string.IsNullOrWhiteSpace(message.ActorName) ? null : message.ActorName;
        var title = (message.Type, arabic, actor is null) switch
        {
            (NotificationType.TicketAssigned, false, false) => $"{actor} assigned #{number} to you",
            (NotificationType.TicketAssigned, false, true) => $"#{number} assigned to you",
            (NotificationType.TicketAssigned, true, false) => $"أسند إليك {actor} التذكرة رقم {number}",
            (NotificationType.TicketAssigned, true, true) => $"أُسندت إليك التذكرة رقم {number}",
            (NotificationType.Mention, false, _) => $"{actor ?? "Someone"} mentioned you on #{number}",
            (NotificationType.Mention, true, _) => $"أشار إليك {actor ?? "أحد الزملاء"} في التذكرة رقم {number}",
            (NotificationType.TicketReply, false, false) => $"{actor} replied on #{number}",
            (NotificationType.TicketReply, false, true) => $"New reply on #{number}",
            (NotificationType.TicketReply, true, false) => $"ردّ {actor} على التذكرة رقم {number}",
            (NotificationType.TicketReply, true, true) => $"رد جديد على التذكرة رقم {number}",
            (NotificationType.TicketNote, false, false) => $"{actor} added a note on #{number}",
            (NotificationType.TicketNote, false, true) => $"Internal note on #{number}",
            (NotificationType.TicketNote, true, false) => $"أضاف {actor} ملاحظة على التذكرة رقم {number}",
            (NotificationType.TicketNote, true, true) => $"ملاحظة داخلية على التذكرة رقم {number}",
            (NotificationType.SlaWarning, false, _) => $"SLA at risk on #{number}",
            (NotificationType.SlaWarning, true, _) => $"التذكرة رقم {number} تقترب من موعدها",
            (NotificationType.SlaBreach, false, _) => $"SLA breached on #{number}",
            (NotificationType.SlaBreach, true, _) => $"تجاوزت التذكرة رقم {number} موعدها",
            (NotificationType.Automation, false, _) => actor is null ? $"Update on #{number}" : $"{actor}: #{number}",
            (NotificationType.Automation, true, _) => actor is null ? $"تحديث على التذكرة رقم {number}" : $"{actor}: التذكرة رقم {number}",
            _ => arabic ? $"التذكرة رقم {number}" : $"Ticket #{number}",
        };

        var sla = message.Type is NotificationType.SlaWarning or NotificationType.SlaBreach;
        return new PushPayload(
            title,
            message.TicketSubject,
            new Dictionary<string, string>
            {
                ["type"] = message.Type.ToString(),
                ["ticketId"] = message.TicketId.ToString(),
                ["ticketNumber"] = number,
                ["link"] = $"/app/tickets/{message.TicketId}",
            },
            sla ? "sla" : "tickets",
            message.TicketId.ToString(),
            unread,
            message.Type == NotificationType.SlaBreach);
    }
}

internal sealed class HangfirePushDispatcher(IBackgroundJobClient jobs) : IPushDispatcher
{
    public void Enqueue(PushMessage message) =>
        jobs.Enqueue<PushDeliveryJob>(job => job.RunAsync(message, CancellationToken.None));
}

/// <summary>
/// Sends one push to each of a person's phones. Only phones whose sign-in is still valid get it, so signing out,
/// a password reset, deactivation or a detected token theft all stop pushes at once.
/// </summary>
public sealed class PushDeliveryJob(IServiceScopeFactory scopes, IPushSender sender, TimeProvider clock, ILogger<PushDeliveryJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(PushMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Begin(message.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.GetUtcNow();

        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.UserId == message.UserId
                && db.Users.Any(u => u.Id == d.UserId && u.IsActive)
                && db.RefreshTokens.Any(t => t.FamilyId == d.RefreshFamilyId && t.RevokedAt == null && t.ExpiresAt > now))
            .Select(d => new { d.Id, d.Platform, d.PushToken })
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return;
        }

        var unread = await db.Notifications.CountAsync(n => n.UserId == message.UserId && n.ReadAt == null, cancellationToken);
        var payload = PushPayloads.Build(message, unread);
        var sent = 0;
        var retry = false;
        foreach (var device in devices)
        {
            switch (await sender.SendAsync(new PushTarget(device.Platform, device.PushToken), payload, cancellationToken))
            {
                case PushOutcome.Sent:
                    sent++;
                    break;
                case PushOutcome.Unregistered:
                    await db.Devices.Where(d => d.Id == device.Id).ExecuteDeleteAsync(cancellationToken);
                    break;
                case PushOutcome.RetryLater:
                    retry = true;
                    break;
                default:
                    break;
            }
        }

        // Retrying would send again to the phones that already got it, so only a push that reached nobody is retried.
        if (retry && sent == 0)
        {
            throw new InvalidOperationException("The push service asked to retry later.");
        }

        logger.LogDebug("Sent push {Type} to {Count} of {Total} devices", message.Type, sent, devices.Count);
    }
}

/// <summary>The default: logs that a push would go out, so the stack runs without a Firebase account.</summary>
internal sealed class LogPushSender(ILogger<LogPushSender> logger) : IPushSender
{
    public Task<PushOutcome> SendAsync(PushTarget target, PushPayload payload, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Push {Type} to an {Platform} device was not sent: the push provider is Log",
            payload.Data["type"],
            target.Platform);
        return Task.FromResult(PushOutcome.Sent);
    }
}

/// <summary>
/// Sends through the FCM HTTP v1 API, which also reaches iPhones through APNs. The OAuth token is signed with the
/// service account's key and reused until shortly before it expires.
/// </summary>
internal sealed class FcmPushSender(
    IHttpClientFactory httpClients,
    IOptions<PushOptions> options,
    TimeProvider clock,
    ILogger<FcmPushSender> logger) : IPushSender, IDisposable
{
    public const string HttpClientName = "fcm";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public async Task<PushOutcome> SendAsync(PushTarget target, PushPayload payload, CancellationToken cancellationToken)
    {
        var settings = options.Value.Fcm;
        var client = httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{settings.ProjectId}/messages:send")
        {
            Content = JsonContent.Create(new { message = Message(target, payload) }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await AccessTokenAsync(client, cancellationToken));

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return PushOutcome.Sent;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound
            || body.Contains("UNREGISTERED", StringComparison.Ordinal)
            || (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("INVALID_ARGUMENT", StringComparison.Ordinal)))
        {
            return PushOutcome.Unregistered;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            return PushOutcome.RetryLater;
        }

        logger.LogError("FCM refused a push with {Status}", (int)response.StatusCode);
        return PushOutcome.Failed;
    }

    public void Dispose() => _tokenLock.Dispose();

    private static object Message(PushTarget target, PushPayload payload)
    {
        var aps = new Dictionary<string, object>
        {
            ["thread-id"] = payload.CollapseKey,
            ["badge"] = payload.Badge,
            ["sound"] = "default",
        };
        if (payload.TimeSensitive)
        {
            aps["interruption-level"] = "time-sensitive";
        }

        return new
        {
            token = target.Token,
            notification = new { title = payload.Title, body = payload.Body },
            data = payload.Data,
            android = new
            {
                priority = "high",
                notification = new { channel_id = payload.AndroidChannel, tag = payload.CollapseKey },
            },
            apns = new { payload = new { aps } },
        };
    }

    private async Task<string> AccessTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (_accessToken is not null && _accessTokenExpiresAt > now.AddMinutes(5))
            {
                return _accessToken;
            }

            var account = JsonSerializer.Deserialize<ServiceAccount>(options.Value.Fcm.ServiceAccountJson!)!;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(account.PrivateKey);
            var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = account.ClientEmail,
                Audience = account.TokenUri,
                IssuedAt = now.UtcDateTime,
                Expires = now.AddHours(1).UtcDateTime,
                Claims = new Dictionary<string, object> { ["scope"] = Scope },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256),
            });

            using var response = await client.PostAsync(
                account.TokenUri,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
            _accessToken = token.AccessToken;
            _accessTokenExpiresAt = now.AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed record ServiceAccount(
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string TokenUri);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Deletes other registrations of a push token, in any workspace, without reading them.</summary>
internal sealed class DeviceTakeover(AppDbContext db) : IDeviceTakeover, ICrossTenantQuery
{
    public Task ReleaseAsync(string pushToken, Guid keepUserId, string keepInstallationId, CancellationToken cancellationToken) =>
        db.Devices
            .IgnoreQueryFilters()
            .Where(d => d.PushToken == pushToken && !(d.UserId == keepUserId && d.InstallationId == keepInstallationId))
            .ExecuteDeleteAsync(cancellationToken);
}

public static class PushRegistration
{
    public static IServiceCollection AddPushNotifications(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddOptions<PushOptions>().BindConfiguration(PushOptions.SectionName);
        services.AddScoped<IPushDispatcher, HangfirePushDispatcher>();
        services.AddScoped<PushDeliveryJob>();
        services.AddScoped<IDeviceTakeover, DeviceTakeover>();

        if (string.Equals(configuration["Push:Provider"], "Fcm", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuration["Push:Fcm:ProjectId"])
                || string.IsNullOrWhiteSpace(configuration["Push:Fcm:ServiceAccountJson"]))
            {
                throw new InvalidOperationException("Push:Provider is Fcm, so Push:Fcm:ProjectId and Push:Fcm:ServiceAccountJson are required.");
            }

            services.AddHttpClient(FcmPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
            services.AddSingleton<IPushSender, FcmPushSender>();
        }
        else
        {
            services.AddSingleton<IPushSender, LogPushSender>();
        }

        return services;
    }
}
