using Microsoft.EntityFrameworkCore;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Teams;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Common;

public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<OneTimeToken> OneTimeTokens { get; }

    DbSet<Invitation> Invitations { get; }

    DbSet<Team> Teams { get; }

    DbSet<TeamMember> TeamMembers { get; }

    DbSet<BusinessHoursDay> BusinessHours { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The tenant every query and write in the current scope is confined to.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

public interface ITenantScopeFactory
{
    /// <summary>Confines the current scope to one tenant until the returned handle is disposed.</summary>
    IDisposable Begin(Guid tenantId);
}

public interface ICurrentUser
{
    Guid? UserId { get; }
}

/// <summary>Marks a class that is allowed to read across tenants by ignoring the tenant query filter.</summary>
public interface ICrossTenantQuery;

/// <summary>Staff lookups that run before a tenant is known, such as sign-up and login.</summary>
public interface IIdentityDirectory
{
    Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken);

    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    Task<InvitationLookup?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken);
}

public sealed record InvitationLookup(Invitation Invitation, Tenant Tenant);

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string passwordHash, string password);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateStaffAccessToken(User user);

    /// <summary>Creates a random URL-safe secret. Only its hash is ever stored.</summary>
    string CreateSecret();

    string HashSecret(string secret);
}

public interface ISessionStateCache
{
    void Invalidate(Guid userId);
}

public sealed record EmailMessage(
    string ToAddress,
    string ToName,
    string Template,
    string Language,
    IReadOnlyDictionary<string, string> Model);

public interface IEmailDispatcher
{
    void Enqueue(EmailMessage message);
}
