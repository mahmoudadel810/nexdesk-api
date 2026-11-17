using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Auth;

public sealed record SessionUser(
    Guid Id,
    string Email,
    string FullName,
    string? Role,
    string Language,
    bool IsPlatformAdmin);

public sealed record SessionTenant(
    Guid Id,
    string Name,
    string Slug,
    string DefaultLanguage,
    string Timezone,
    bool OnboardingCompleted);

public sealed record SessionDto(SessionUser User, SessionTenant? Tenant, IReadOnlyCollection<string> Permissions)
{
    public static SessionDto From(User user, Tenant? tenant) => new(
        new SessionUser(
            user.Id,
            user.Email,
            user.FullName,
            user.IsPlatformAdmin ? null : user.Role.ToString(),
            user.Language,
            user.IsPlatformAdmin),
        tenant is null
            ? null
            : new SessionTenant(
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.DefaultLanguage,
                tenant.Timezone,
                tenant.OnboardingCompletedAt is not null),
        user.IsPlatformAdmin ? [] : [.. Domain.Identity.Permissions.For(user.Role).Order(StringComparer.Ordinal)]);
}

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    SessionDto Session);

/// <summary>Creates the access and refresh token pair for a staff user. The caller saves the context.</summary>
public sealed class SessionIssuer(
    IAppDbContext db,
    ITokenService tokens,
    TimeProvider clock,
    IOptions<AuthOptions> options)
{
    public (AuthTokens Tokens, RefreshToken RefreshToken) Issue(
        User user,
        Tenant? tenant,
        string? userAgent,
        Guid? familyId = null)
    {
        var secret = tokens.CreateSecret();
        var refreshToken = RefreshToken.Issue(
            SubjectType.Staff,
            user.Id,
            tokens.HashSecret(secret),
            clock.GetUtcNow(),
            TimeSpan.FromDays(options.Value.RefreshTokenDays),
            userAgent,
            familyId);
        db.RefreshTokens.Add(refreshToken);

        var accessToken = tokens.CreateStaffAccessToken(user, refreshToken.FamilyId);
        var result = new AuthTokens(
            accessToken.Value,
            accessToken.ExpiresAt,
            secret,
            refreshToken.ExpiresAt,
            SessionDto.From(user, tenant));
        return (result, refreshToken);
    }
}
