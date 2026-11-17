using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Developer;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Developer;

public sealed record WebhookEndpointDto(
    Guid Id,
    string Url,
    string? Description,
    IReadOnlyList<string> Events,
    bool IsEnabled,
    DateTimeOffset CreatedAt)
{
    public static WebhookEndpointDto From(WebhookEndpoint endpoint) => new(
        endpoint.Id, endpoint.Url, endpoint.Description, endpoint.Events, endpoint.IsEnabled, endpoint.CreatedAt);
}

/// <summary>An endpoint with its signing secret, returned only when the secret is created or rotated.</summary>
public sealed record WebhookSecretDto(WebhookEndpointDto Endpoint, string Secret);

public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid EndpointId,
    string Event,
    WebhookDeliveryStatus Status,
    int Attempts,
    int? ResponseStatus,
    string? ResponseBody,
    string? Error,
    int? DurationMs,
    string Payload,
    Guid? ResentFromId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt)
{
    public static WebhookDeliveryDto From(WebhookDelivery d) => new(
        d.Id, d.EndpointId, d.Event, d.Status, d.Attempts, d.ResponseStatus, d.ResponseBody, d.Error, d.DurationMs,
        d.Payload, d.ResentFromId, d.CreatedAt, d.LastAttemptAt, d.NextAttemptAt);
}

/// <summary>Hands deliveries to the background worker. The tenant travels with the job.</summary>
public interface IWebhookDispatcher
{
    void Enqueue(Guid tenantId, Guid deliveryId);

    void Schedule(Guid tenantId, Guid deliveryId, DateTimeOffset at);
}

/// <summary>
/// How requests are signed. Receivers recompute the HMAC-SHA256 of <c>{timestamp}.{body}</c> with the
/// endpoint's secret and compare it with the <c>v1</c> value of the <c>X-NexDesk-Signature</c> header.
/// </summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "X-NexDesk-Signature";
    public const string EventHeader = "X-NexDesk-Event";
    public const string DeliveryHeader = "X-NexDesk-Delivery";
    public const string TimestampHeader = "X-NexDesk-Timestamp";

    public static string Sign(string secret, long timestamp, string payload)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }
}

public static class WebhookErrors
{
    public static readonly Error EndpointNotFound = Error.NotFound("webhook-not-found", "The webhook was not found.");

    public static readonly Error DeliveryNotFound = Error.NotFound("webhook-delivery-not-found", "The delivery was not found.");
}

internal static class WebhookPayloads
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(string eventName, DateTimeOffset occurredAt, object data) =>
        JsonSerializer.Serialize(new { @event = eventName, occurredAt, data }, Json);
}

/// <summary>Turns ticket events into deliveries for every endpoint that subscribed to them.</summary>
internal sealed class WebhookPublisher(
    IAppDbContext db,
    ITenantContext tenant,
    IEntitlementService entitlements,
    IWebhookDispatcher dispatcher,
    TimeProvider clock)
    : IDomainEventHandler<TicketCreated>,
        IDomainEventHandler<TicketChanged>,
        IDomainEventHandler<TicketAssigned>,
        IDomainEventHandler<TicketMessageAdded>,
        IDomainEventHandler<TicketSolved>
{
    public Task Handle(TicketCreated domainEvent, CancellationToken cancellationToken) =>
        Publish(WebhookEvents.TicketCreated, domainEvent.TicketId, null, cancellationToken);

    public Task Handle(TicketChanged domainEvent, CancellationToken cancellationToken) =>
        Publish(WebhookEvents.TicketUpdated, domainEvent.TicketId, new { changes = domainEvent.Changes }, cancellationToken);

    public Task Handle(TicketAssigned domainEvent, CancellationToken cancellationToken) =>
        Publish(WebhookEvents.TicketAssigned, domainEvent.TicketId, null, cancellationToken);

    public Task Handle(TicketSolved domainEvent, CancellationToken cancellationToken) =>
        Publish(WebhookEvents.TicketSolved, domainEvent.TicketId, null, cancellationToken);

    public async Task Handle(TicketMessageAdded domainEvent, CancellationToken cancellationToken)
    {
        // Internal notes stay inside the team.
        if (domainEvent.IsInternal)
        {
            return;
        }

        var message = await db.TicketMessages.AsNoTracking()
            .Where(m => m.Id == domainEvent.MessageId)
            .Select(m => new { m.Id, m.Body, author = new { type = m.AuthorType, id = m.AuthorId }, m.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        await Publish(WebhookEvents.TicketReplied, domainEvent.TicketId, new { message }, cancellationToken);
    }

    private async Task Publish(string eventName, Guid ticketId, object? extra, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return;
        }

        var endpoints = await db.WebhookEndpoints.AsNoTracking()
            .Where(e => e.IsEnabled && e.Events.Contains(eventName))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        if (endpoints.Count == 0 || await entitlements.RequireApiAccess(cancellationToken) is not null)
        {
            return;
        }

        var ticket = await TicketQueries.LoadDetail(db, ticketId, null, cancellationToken);
        if (ticket is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var payload = WebhookPayloads.Write(eventName, now, new { ticket, details = extra });
        var deliveries = endpoints.Select(endpointId => WebhookDelivery.Queue(endpointId, eventName, payload, now)).ToList();
        db.WebhookDeliveries.AddRange(deliveries);
        await db.SaveChangesAsync(cancellationToken);
        deliveries.ForEach(delivery => dispatcher.Enqueue(tenantId, delivery.Id));
    }
}

public sealed record ListWebhooksQuery : IQuery<IReadOnlyList<WebhookEndpointDto>>;

internal sealed class ListWebhooksHandler(IAppDbContext db) : IQueryHandler<ListWebhooksQuery, IReadOnlyList<WebhookEndpointDto>>
{
    public async Task<Result<IReadOnlyList<WebhookEndpointDto>>> Handle(ListWebhooksQuery query, CancellationToken cancellationToken)
    {
        var endpoints = await db.WebhookEndpoints.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken);
        return endpoints.Select(WebhookEndpointDto.From).ToList();
    }
}

/// <summary>The fields an endpoint is made of, checked the same way when it is added or changed.</summary>
public interface IWebhookFields
{
    string Url { get; }

    string? Description { get; }

    IReadOnlyCollection<string> Events { get; }
}

internal static class WebhookFieldRules
{
    public static void AddWebhookRules<T>(this AbstractValidator<T> validator)
        where T : IWebhookFields
    {
        validator.RuleFor(c => c.Url).NotEmpty().MaximumLength(WebhookEndpoint.UrlMaxLength)
            .Must(url => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && string.IsNullOrEmpty(uri.UserInfo))
            .WithMessage("Enter a full http or https address, without a user name or password.");
        validator.RuleFor(c => c.Description).MaximumLength(WebhookEndpoint.DescriptionMaxLength);
        validator.RuleFor(c => c.Events).NotEmpty().WithMessage("Choose at least one event.");
        validator.RuleForEach(c => c.Events).Must(e => WebhookEvents.All.Contains(e))
            .WithMessage($"Events must be among: {string.Join(", ", WebhookEvents.All)}.");
    }
}

public sealed record CreateWebhookCommand(string Url, string? Description, IReadOnlyCollection<string> Events)
    : ICommand<WebhookSecretDto>, IWebhookFields;

internal sealed class CreateWebhookValidator : AbstractValidator<CreateWebhookCommand>
{
    public CreateWebhookValidator() => this.AddWebhookRules();
}

/// <summary>Adds an enabled endpoint. Its signing secret is returned this once.</summary>
internal sealed class CreateWebhookHandler(IAppDbContext db, ITokenService tokens, IEntitlementService entitlements)
    : ICommandHandler<CreateWebhookCommand, WebhookSecretDto>
{
    public async Task<Result<WebhookSecretDto>> Handle(CreateWebhookCommand command, CancellationToken cancellationToken)
    {
        if (await entitlements.RequireApiAccess(cancellationToken) is { } limit)
        {
            return limit;
        }

        var secret = $"whsec_{tokens.CreateSecret()}";
        var endpoint = WebhookEndpoint.Create(command.Url, command.Description, command.Events, secret);
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        return new WebhookSecretDto(WebhookEndpointDto.From(endpoint), secret);
    }
}

public sealed record UpdateWebhookCommand(Guid EndpointId, string Url, string? Description, IReadOnlyCollection<string> Events, bool IsEnabled)
    : ICommand<WebhookEndpointDto>, IWebhookFields;

internal sealed class UpdateWebhookValidator : AbstractValidator<UpdateWebhookCommand>
{
    public UpdateWebhookValidator() => this.AddWebhookRules();
}

internal sealed class UpdateWebhookHandler(IAppDbContext db) : ICommandHandler<UpdateWebhookCommand, WebhookEndpointDto>
{
    public async Task<Result<WebhookEndpointDto>> Handle(UpdateWebhookCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.EndpointId, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.EndpointNotFound;
        }

        endpoint.Update(command.Url, command.Description, command.Events, command.IsEnabled);
        await db.SaveChangesAsync(cancellationToken);
        return WebhookEndpointDto.From(endpoint);
    }
}

public sealed record RotateWebhookSecretCommand(Guid EndpointId) : ICommand<WebhookSecretDto>;

internal sealed class RotateWebhookSecretHandler(IAppDbContext db, ITokenService tokens)
    : ICommandHandler<RotateWebhookSecretCommand, WebhookSecretDto>
{
    public async Task<Result<WebhookSecretDto>> Handle(RotateWebhookSecretCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.EndpointId, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.EndpointNotFound;
        }

        var secret = $"whsec_{tokens.CreateSecret()}";
        endpoint.RotateSecret(secret);
        await db.SaveChangesAsync(cancellationToken);
        return new WebhookSecretDto(WebhookEndpointDto.From(endpoint), secret);
    }
}

public sealed record DeleteWebhookCommand(Guid EndpointId) : ICommand<Success>;

internal sealed class DeleteWebhookHandler(IAppDbContext db) : ICommandHandler<DeleteWebhookCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteWebhookCommand command, CancellationToken cancellationToken)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == command.EndpointId, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.EndpointNotFound;
        }

        db.WebhookEndpoints.Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record ListWebhookDeliveriesQuery(Guid EndpointId, int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<WebhookDeliveryDto>>, IPagedRequest;

internal sealed class ListWebhookDeliveriesValidator : AbstractValidator<ListWebhookDeliveriesQuery>
{
    public ListWebhookDeliveriesValidator() => this.AddPagingRules();
}

internal sealed class ListWebhookDeliveriesHandler(IAppDbContext db)
    : IQueryHandler<ListWebhookDeliveriesQuery, PagedResult<WebhookDeliveryDto>>
{
    public async Task<Result<PagedResult<WebhookDeliveryDto>>> Handle(ListWebhookDeliveriesQuery query, CancellationToken cancellationToken)
    {
        if (!await db.WebhookEndpoints.AnyAsync(e => e.Id == query.EndpointId, cancellationToken))
        {
            return WebhookErrors.EndpointNotFound;
        }

        var page = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.EndpointId == query.EndpointId)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .ToPagedAsync(query, cancellationToken);
        return new PagedResult<WebhookDeliveryDto>(
            page.Items.Select(WebhookDeliveryDto.From).ToList(), page.Page, page.PageSize, page.TotalCount);
    }
}

/// <summary>Sends a delivery's exact payload again, as a new delivery, so the original's record stays intact.</summary>
public sealed record ResendWebhookDeliveryCommand(Guid DeliveryId) : ICommand<WebhookDeliveryDto>;

internal sealed class ResendWebhookDeliveryHandler(
    IAppDbContext db,
    ITenantContext tenant,
    IEntitlementService entitlements,
    IWebhookDispatcher dispatcher,
    TimeProvider clock) : ICommandHandler<ResendWebhookDeliveryCommand, WebhookDeliveryDto>
{
    public async Task<Result<WebhookDeliveryDto>> Handle(ResendWebhookDeliveryCommand command, CancellationToken cancellationToken)
    {
        if (await entitlements.RequireApiAccess(cancellationToken) is { } limit)
        {
            return limit;
        }

        var original = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == command.DeliveryId, cancellationToken);
        if (original is null)
        {
            return WebhookErrors.DeliveryNotFound;
        }

        var copy = WebhookDelivery.Queue(original.EndpointId, original.Event, original.Payload, clock.GetUtcNow(), original.Id);
        db.WebhookDeliveries.Add(copy);
        await db.SaveChangesAsync(cancellationToken);
        dispatcher.Enqueue(tenant.TenantId!.Value, copy.Id);
        return WebhookDeliveryDto.From(copy);
    }
}

/// <summary>Sends a small <c>ping</c> event, to check that an endpoint is reachable and verifies signatures.</summary>
public sealed record PingWebhookCommand(Guid EndpointId) : ICommand<WebhookDeliveryDto>;

internal sealed class PingWebhookHandler(
    IAppDbContext db,
    ITenantContext tenant,
    IEntitlementService entitlements,
    IWebhookDispatcher dispatcher,
    TimeProvider clock) : ICommandHandler<PingWebhookCommand, WebhookDeliveryDto>
{
    public async Task<Result<WebhookDeliveryDto>> Handle(PingWebhookCommand command, CancellationToken cancellationToken)
    {
        if (await entitlements.RequireApiAccess(cancellationToken) is { } limit)
        {
            return limit;
        }

        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == command.EndpointId, cancellationToken);
        if (endpoint is null)
        {
            return WebhookErrors.EndpointNotFound;
        }

        var now = clock.GetUtcNow();
        var payload = WebhookPayloads.Write(WebhookEvents.Ping, now, new { webhookId = endpoint.Id });
        var delivery = WebhookDelivery.Queue(endpoint.Id, WebhookEvents.Ping, payload, now);
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken);
        dispatcher.Enqueue(tenant.TenantId!.Value, delivery.Id);
        return WebhookDeliveryDto.From(delivery);
    }
}
