using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Billing;
using NexDesk.Application.Common;
using NexDesk.Domain.Developer;

namespace NexDesk.Application.Developer;

/// <summary>An issued key. <c>Hint</c> is its start, enough to recognise it, such as <c>ndk_3f9a0c1b7e42_…</c>.</summary>
public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string Hint,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt)
{
    public static ApiKeyDto From(ApiKey key) => new(
        key.Id,
        key.Name,
        $"{ApiKey.KeyPrefix}{key.Prefix}_…",
        key.Scopes,
        key.ExpiresAt,
        key.LastUsedAt,
        key.RevokedAt,
        key.CreatedAt);
}

/// <summary>A newly issued key. <see cref="Key"/> is shown this once and cannot be read again.</summary>
public sealed record IssuedApiKeyDto(ApiKeyDto ApiKey, string Key);

/// <summary>The workspace and permissions behind a key presented to the public API.</summary>
public sealed record ApiKeyIdentity(Guid ApiKeyId, Guid TenantId, IReadOnlyList<string> Scopes, bool TenantSuspended);

/// <summary>Finds the key behind a request before any workspace is known.</summary>
public interface IApiKeyDirectory
{
    /// <summary>Returns the identity of an active key, or null when the key is unknown, revoked or expired.</summary>
    Task<ApiKeyIdentity?> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken);
}

public static class ApiKeyErrors
{
    public static readonly Error NotFound = Error.NotFound("api-key-not-found", "The API key was not found.");
}

public sealed record ListApiKeysQuery : IQuery<IReadOnlyList<ApiKeyDto>>;

internal sealed class ListApiKeysHandler(IAppDbContext db) : IQueryHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>
{
    public async Task<Result<IReadOnlyList<ApiKeyDto>>> Handle(ListApiKeysQuery query, CancellationToken cancellationToken)
    {
        var keys = await db.ApiKeys.AsNoTracking()
            .OrderBy(k => k.RevokedAt != null)
            .ThenByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
        return keys.Select(ApiKeyDto.From).ToList();
    }
}

public sealed record IssueApiKeyCommand(string Name, IReadOnlyCollection<string> Scopes, int? ExpiresInDays)
    : ICommand<IssuedApiKeyDto>;

internal sealed class IssueApiKeyValidator : AbstractValidator<IssueApiKeyCommand>
{
    public const int MaxLifetimeDays = 730;

    public IssueApiKeyValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(ApiKey.NameMaxLength);
        RuleFor(c => c.Scopes).NotEmpty().WithMessage("Choose at least one scope.");
        RuleForEach(c => c.Scopes).Must(scope => ApiScopes.All.Contains(scope))
            .WithMessage($"Scopes must be among: {string.Join(", ", ApiScopes.All)}.");
        RuleFor(c => c.ExpiresInDays).InclusiveBetween(1, MaxLifetimeDays).When(c => c.ExpiresInDays is not null);
    }
}

internal sealed class IssueApiKeyHandler(
    IAppDbContext db,
    ITokenService tokens,
    IEntitlementService entitlements,
    TimeProvider clock) : ICommandHandler<IssueApiKeyCommand, IssuedApiKeyDto>
{
    public async Task<Result<IssuedApiKeyDto>> Handle(IssueApiKeyCommand command, CancellationToken cancellationToken)
    {
        if (await entitlements.RequireApiAccess(cancellationToken) is { } limit)
        {
            return limit;
        }

        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var key = ApiKey.Compose(prefix, tokens.CreateSecret());
        var expiresAt = command.ExpiresInDays is { } days ? clock.GetUtcNow().AddDays(days) : (DateTimeOffset?)null;
        var apiKey = ApiKey.Issue(command.Name, prefix, tokens.HashSecret(key), command.Scopes, expiresAt);

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedApiKeyDto(ApiKeyDto.From(apiKey), key);
    }
}

public sealed record RevokeApiKeyCommand(Guid ApiKeyId) : ICommand<ApiKeyDto>;

internal sealed class RevokeApiKeyHandler(IAppDbContext db, TimeProvider clock) : ICommandHandler<RevokeApiKeyCommand, ApiKeyDto>
{
    public async Task<Result<ApiKeyDto>> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken)
    {
        var apiKey = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == command.ApiKeyId, cancellationToken);
        if (apiKey is null)
        {
            return ApiKeyErrors.NotFound;
        }

        apiKey.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ApiKeyDto.From(apiKey);
    }
}
