using NexDesk.Domain.Common;

namespace NexDesk.Domain.Developer;

/// <summary>The events an endpoint can subscribe to.</summary>
public static class WebhookEvents
{
    public const string TicketCreated = "ticket.created";
    public const string TicketUpdated = "ticket.updated";
    public const string TicketAssigned = "ticket.assigned";
    public const string TicketReplied = "ticket.replied";
    public const string TicketSolved = "ticket.solved";

    /// <summary>Sent on request from the settings screen to check that an endpoint answers.</summary>
    public const string Ping = "ping";

    public static IReadOnlyList<string> All { get; } = [TicketCreated, TicketUpdated, TicketAssigned, TicketReplied, TicketSolved];
}

/// <summary>A URL the workspace wants told about ticket events, with the secret its requests are signed with.</summary>
public sealed class WebhookEndpoint : AuditableEntity, ITenantOwned
{
    public const int UrlMaxLength = 2000;
    public const int DescriptionMaxLength = 200;

    private WebhookEndpoint()
    {
    }

    public Guid TenantId { get; private set; }

    public string Url { get; private set; } = null!;

    public string? Description { get; private set; }

    public List<string> Events { get; private set; } = [];

    public bool IsEnabled { get; private set; }

    /// <summary>Signs every request. Kept readable because signing needs it; never returned after creation.</summary>
    public string Secret { get; private set; } = null!;

    public static WebhookEndpoint Create(string url, string? description, IEnumerable<string> events, string secret)
    {
        var endpoint = new WebhookEndpoint { Secret = secret, IsEnabled = true };
        endpoint.Update(url, description, events, isEnabled: true);
        return endpoint;
    }

    public void Update(string url, string? description, IEnumerable<string> events, bool isEnabled)
    {
        var subscribed = events.Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
        if (subscribed.Count == 0 || subscribed.Any(e => !WebhookEvents.All.Contains(e)))
        {
            throw new ArgumentException("Choose at least one known event.", nameof(events));
        }

        Url = url.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Events = subscribed;
        IsEnabled = isEnabled;
    }

    public void RotateSecret(string secret) => Secret = secret;

    public bool Wants(string eventName) => IsEnabled && Events.Contains(eventName);
}

public enum WebhookDeliveryStatus
{
    /// <summary>Waiting for its first attempt or for a retry.</summary>
    Pending,
    Succeeded,

    /// <summary>Every attempt failed; it will not be tried again unless resent.</summary>
    Failed,
}

/// <summary>One event sent to one endpoint, with the outcome of its latest attempt.</summary>
public sealed class WebhookDelivery : Entity, ITenantOwned
{
    public const int MaxAttempts = 6;
    public const int ResponseBodyMaxLength = 2000;
    public const int ErrorMaxLength = 500;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    ];

    private WebhookDelivery()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid EndpointId { get; private set; }

    public string Event { get; private set; } = null!;

    /// <summary>The exact body sent, so a retry or a resend signs and sends the same bytes.</summary>
    public string Payload { get; private set; } = null!;

    public WebhookDeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public int? ResponseStatus { get; private set; }

    public string? ResponseBody { get; private set; }

    public string? Error { get; private set; }

    public int? DurationMs { get; private set; }

    /// <summary>The delivery this one repeats, when someone pressed resend.</summary>
    public Guid? ResentFromId { get; private set; }

    public static WebhookDelivery Queue(Guid endpointId, string eventName, string payload, DateTimeOffset now, Guid? resentFromId = null) =>
        new()
        {
            EndpointId = endpointId,
            Event = eventName,
            Payload = payload,
            Status = WebhookDeliveryStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now,
            ResentFromId = resentFromId,
        };

    /// <summary>How long to wait after failed attempt number <paramref name="attempt"/>, or null to give up.</summary>
    public static TimeSpan? RetryDelay(int attempt) =>
        attempt >= 1 && attempt < MaxAttempts ? Backoff[Math.Min(attempt, Backoff.Length) - 1] : null;

    public void RecordSuccess(DateTimeOffset now, int status, string? body, int durationMs)
    {
        Attempts++;
        Status = WebhookDeliveryStatus.Succeeded;
        LastAttemptAt = now;
        NextAttemptAt = null;
        ResponseStatus = status;
        ResponseBody = Cut(body, ResponseBodyMaxLength);
        Error = null;
        DurationMs = durationMs;
    }

    /// <summary>Records a failed attempt and returns when to try again, or null when the delivery has given up.</summary>
    public DateTimeOffset? RecordFailure(DateTimeOffset now, int? status, string? body, string error, int durationMs)
    {
        Attempts++;
        LastAttemptAt = now;
        ResponseStatus = status;
        ResponseBody = Cut(body, ResponseBodyMaxLength);
        Error = Cut(error, ErrorMaxLength);
        DurationMs = durationMs;
        NextAttemptAt = RetryDelay(Attempts) is { } delay ? now + delay : null;
        Status = NextAttemptAt is null ? WebhookDeliveryStatus.Failed : WebhookDeliveryStatus.Pending;
        return NextAttemptAt;
    }

    /// <summary>Gives up at once, for example because the endpoint was switched off or removed.</summary>
    public void Abandon(DateTimeOffset now, string reason)
    {
        Status = WebhookDeliveryStatus.Failed;
        LastAttemptAt = now;
        NextAttemptAt = null;
        Error = Cut(reason, ErrorMaxLength);
    }

    private static string? Cut(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
