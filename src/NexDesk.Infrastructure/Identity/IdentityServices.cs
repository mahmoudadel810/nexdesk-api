using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string StaffAudience = "nexdesk-staff";
    public const string PortalAudience = "nexdesk-portal";

    [Required]
    public string Issuer { get; set; } = "nexdesk";

    [Required]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    public SymmetricSecurityKey CreateKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public static class NexDeskClaims
{
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string Role = "role";
    public const string Permission = "perm";
    public const string SecurityVersion = "sv";
    public const string PlatformAdmin = "platform_admin";
}

internal sealed class IdentityDirectory(AppDbContext db) : IIdentityDirectory, ICrossTenantQuery
{
    public Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email);
        return db.Users
            .IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users
            .IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email);
        return db.Users
            .IgnoreQueryFilters([AppDbContext.TenantFilter])
            .AnyAsync(u => u.Email == normalized, cancellationToken);
    }

    public async Task<InvitationLookup?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations
            .IgnoreQueryFilters([AppDbContext.TenantFilter])
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);
        if (invitation is null)
        {
            return null;
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == invitation.TenantId, cancellationToken);
        return new InvitationLookup(invitation, tenant);
    }
}

internal sealed class PasswordHasherAdapter : IPasswordHasher
{
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(Subject, password);

    public bool Verify(string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(Subject, passwordHash, password) != PasswordVerificationResult.Failed;
}

internal sealed class JwtTokenService(
    IOptions<JwtOptions> jwtOptions,
    IOptions<AuthOptions> authOptions,
    TimeProvider clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateStaffAccessToken(User user)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(authOptions.Value.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(NexDeskClaims.Subject, user.Id.ToString()),
            new(NexDeskClaims.SecurityVersion, user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        if (user.IsPlatformAdmin)
        {
            claims.Add(new Claim(NexDeskClaims.PlatformAdmin, "true"));
        }
        else
        {
            claims.Add(new Claim(NexDeskClaims.TenantId, user.TenantId!.Value.ToString()));
            claims.Add(new Claim(NexDeskClaims.Role, user.Role.ToString()));
            claims.AddRange(Permissions.For(user.Role).Select(p => new Claim(NexDeskClaims.Permission, p)));
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwtOptions.Value.Issuer,
            Audience = JwtOptions.StaffAudience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwtOptions.Value.CreateKey(), SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }

    public AccessToken CreateCustomerAccessToken(NexDesk.Domain.Customers.Customer customer)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(authOptions.Value.AccessTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwtOptions.Value.Issuer,
            Audience = JwtOptions.PortalAudience,
            Subject = new ClaimsIdentity(
            [
                new Claim(NexDeskClaims.Subject, customer.Id.ToString()),
                new Claim(NexDeskClaims.TenantId, customer.TenantId.ToString()),
                new Claim(
                    NexDeskClaims.SecurityVersion,
                    customer.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwtOptions.Value.CreateKey(), SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }

    public string CreateSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string HashSecret(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

public enum SessionState
{
    Valid,
    Invalid,
    TenantSuspended,
}

/// <summary>Checks on every request that a token still matches the account it was issued for.</summary>
public sealed class SessionStateCache(IMemoryCache cache, IServiceScopeFactory scopes)
    : ISessionStateCache, ICrossTenantQuery
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));

    public void InvalidateCustomer(Guid customerId) => cache.Remove(CustomerKey(customerId));

    /// <summary>Whether a portal token still matches its customer and the workspace is open.</summary>
    public async Task<SessionState> CheckCustomerAsync(Guid customerId, int securityVersion, CancellationToken cancellationToken)
    {
        var snapshot = await cache.GetOrCreateAsync(CustomerKey(customerId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Lifetime;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Customers
                .IgnoreQueryFilters([AppDbContext.TenantFilter])
                .Where(c => c.Id == customerId)
                .Select(c => new Snapshot(
                    true,
                    c.SecurityVersion,
                    db.Tenants.Any(t => t.Id == c.TenantId && t.Status == TenantStatus.Suspended)))
                .FirstOrDefaultAsync(cancellationToken);
        });

        if (snapshot is null || snapshot.SecurityVersion != securityVersion)
        {
            return SessionState.Invalid;
        }

        return snapshot.TenantSuspended ? SessionState.TenantSuspended : SessionState.Valid;
    }

    public async Task<SessionState> CheckAsync(Guid userId, int securityVersion, CancellationToken cancellationToken)
    {
        var snapshot = await cache.GetOrCreateAsync(Key(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Lifetime;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Users
                .IgnoreQueryFilters([AppDbContext.TenantFilter])
                .Where(u => u.Id == userId)
                .Select(u => new Snapshot(
                    u.IsActive,
                    u.SecurityVersion,
                    db.Tenants.Any(t => t.Id == u.TenantId && t.Status == TenantStatus.Suspended)))
                .FirstOrDefaultAsync(cancellationToken);
        });

        if (snapshot is null || !snapshot.IsActive || snapshot.SecurityVersion != securityVersion)
        {
            return SessionState.Invalid;
        }

        return snapshot.TenantSuspended ? SessionState.TenantSuspended : SessionState.Valid;
    }

    private static string Key(Guid userId) => $"session-state:{userId}";

    private static string CustomerKey(Guid customerId) => $"portal-session-state:{customerId}";

    private sealed record Snapshot(bool IsActive, int SecurityVersion, bool TenantSuspended);
}
