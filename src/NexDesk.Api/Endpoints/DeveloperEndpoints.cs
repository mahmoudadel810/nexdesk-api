using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

/// <param name="Name">What the key is for, such as the integration that uses it.</param>
/// <param name="Scopes">Any of <c>tickets:read</c> and <c>tickets:write</c>.</param>
/// <param name="ExpiresInDays">How long the key works, from 1 to 730 days. Leave empty for a key that does not expire.</param>
public sealed record IssueApiKeyRequest(string Name, IReadOnlyCollection<string> Scopes, int? ExpiresInDays = null);

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

        return api;
    }
}
