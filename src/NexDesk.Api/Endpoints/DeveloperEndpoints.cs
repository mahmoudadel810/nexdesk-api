using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

/// <param name="Name">What the key is for, such as the integration that uses it.</param>
/// <param name="Scopes">Any of <c>tickets:read</c> and <c>tickets:write</c>.</param>
/// <param name="ExpiresInDays">How long the key works, from 1 to 730 days. Leave empty for a key that does not expire.</param>
public sealed record IssueApiKeyRequest(string Name, IReadOnlyCollection<string> Scopes, int? ExpiresInDays = null);

/// <param name="Url">Where to post events: a full http or https address.</param>
/// <param name="Description">A note on what receives the events.</param>
/// <param name="Events">Any of ticket.created, ticket.updated, ticket.assigned, ticket.replied and ticket.solved.</param>
public sealed record CreateWebhookRequest(string Url, IReadOnlyCollection<string> Events, string? Description = null);

public sealed record UpdateWebhookRequest(string Url, IReadOnlyCollection<string> Events, bool IsEnabled, string? Description = null);

public sealed record ListWebhookDeliveriesRequest(int Page = 1, int PageSize = Paging.DefaultPageSize);

public static class DeveloperEndpoints
{
    public static RouteGroupBuilder MapDeveloperEndpoints(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/api-keys").WithTags("API keys").RequirePermission(Permissions.DeveloperManage);

        keys.MapGet("/", async (IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListApiKeysQuery(), ct)).ToOk())
            .WithName("ListApiKeys")
            .WithSummary("Lists the workspace's API keys, active ones first. The keys themselves are never shown again.")
            .Produces<IReadOnlyList<ApiKeyDto>>();

        keys.MapPost("/", async (
            IssueApiKeyRequest request,
            ICommandHandler<IssueApiKeyCommand, IssuedApiKeyDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new IssueApiKeyCommand(request.Name, request.Scopes ?? [], request.ExpiresInDays), ct))
            .ToHttp(issued => TypedResults.Created($"/api/v1/api-keys/{issued.ApiKey.Id}", issued)))
            .WithName("IssueApiKey")
            .WithSummary(
                "Issues a key for the public API. The response holds the full key, which is shown only this once; "
                + "send it in the X-Api-Key header. Requires a plan with API access.")
            .Produces<IssuedApiKeyDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status402PaymentRequired);

        keys.MapDelete("/{id:guid}", async (
            Guid id,
            ICommandHandler<RevokeApiKeyCommand, ApiKeyDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new RevokeApiKeyCommand(id), ct)).ToOk())
            .WithName("RevokeApiKey")
            .WithSummary("Revokes a key. Requests made with it are refused from now on; it stays listed for the record.")
            .Produces<ApiKeyDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapWebhooks(api.MapGroup("/webhooks").WithTags("Webhooks").RequirePermission(Permissions.DeveloperManage));
        return api;
    }

    private static void MapWebhooks(RouteGroupBuilder webhooks)
    {
        webhooks.MapGet("/", async (IQueryHandler<ListWebhooksQuery, IReadOnlyList<WebhookEndpointDto>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListWebhooksQuery(), ct)).ToOk())
            .WithName("ListWebhooks")
            .WithSummary("Lists the endpoints that receive the workspace's events.")
            .Produces<IReadOnlyList<WebhookEndpointDto>>();

        webhooks.MapPost("/", async (
            CreateWebhookRequest request,
            ICommandHandler<CreateWebhookCommand, WebhookSecretDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new CreateWebhookCommand(request.Url, request.Description, request.Events ?? []), ct))
            .ToHttp(created => TypedResults.Created($"/api/v1/webhooks/{created.Endpoint.Id}", created)))
            .WithName("CreateWebhook")
            .WithSummary(
                "Adds an endpoint. The response holds its signing secret, shown only this once. Each request carries "
                + "X-NexDesk-Signature: t={unix seconds},v1={hex HMAC-SHA256 of \"{t}.{body}\" with the secret}. "
                + "Failed deliveries are retried up to 6 times over about 9 hours. Requires a plan with API access.")
            .Produces<WebhookSecretDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status402PaymentRequired);

        webhooks.MapPut("/{id:guid}", async (
            Guid id,
            UpdateWebhookRequest request,
            ICommandHandler<UpdateWebhookCommand, WebhookEndpointDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new UpdateWebhookCommand(id, request.Url, request.Description, request.Events ?? [], request.IsEnabled), ct))
            .ToOk())
            .WithName("UpdateWebhook")
            .WithSummary("Changes an endpoint's address, events or description, or switches it off.")
            .Produces<WebhookEndpointDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        webhooks.MapDelete("/{id:guid}", async (Guid id, ICommandHandler<DeleteWebhookCommand, Success> handler, CancellationToken ct) =>
            (await handler.Handle(new DeleteWebhookCommand(id), ct)).ToNoContent())
            .WithName("DeleteWebhook")
            .WithSummary("Removes an endpoint and its delivery log.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        webhooks.MapPost("/{id:guid}/rotate-secret", async (
            Guid id,
            ICommandHandler<RotateWebhookSecretCommand, WebhookSecretDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new RotateWebhookSecretCommand(id), ct)).ToOk())
            .WithName("RotateWebhookSecret")
            .WithSummary("Replaces the signing secret. The new one is returned this once; the old one stops being used at once.")
            .Produces<WebhookSecretDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        webhooks.MapPost("/{id:guid}/ping", async (
            Guid id,
            ICommandHandler<PingWebhookCommand, WebhookDeliveryDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new PingWebhookCommand(id), ct))
            .ToHttp(delivery => TypedResults.Accepted($"/api/v1/webhooks/{id}/deliveries", delivery)))
            .WithName("PingWebhook")
            .WithSummary("Sends a signed ping event, to check that the endpoint answers and verifies signatures.")
            .Produces<WebhookDeliveryDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

        webhooks.MapGet("/{id:guid}/deliveries", async (
            Guid id,
            [AsParameters] ListWebhookDeliveriesRequest request,
            IQueryHandler<ListWebhookDeliveriesQuery, PagedResult<WebhookDeliveryDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListWebhookDeliveriesQuery(id, request.Page, request.PageSize), ct)).ToOk())
            .WithName("ListWebhookDeliveries")
            .WithSummary("Lists what was sent to an endpoint, newest first, with the response to the latest attempt.")
            .Produces<PagedResult<WebhookDeliveryDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        webhooks.MapPost("/deliveries/{deliveryId:guid}/resend", async (
            Guid deliveryId,
            ICommandHandler<ResendWebhookDeliveryCommand, WebhookDeliveryDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ResendWebhookDeliveryCommand(deliveryId), ct))
            .ToHttp(delivery => TypedResults.Accepted($"/api/v1/webhooks/{delivery.EndpointId}/deliveries", delivery)))
            .WithName("ResendWebhookDelivery")
            .WithSummary("Sends a delivery's payload again as a new delivery, keeping the original's record.")
            .Produces<WebhookDeliveryDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
