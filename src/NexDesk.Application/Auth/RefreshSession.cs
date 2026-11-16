using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Auth;

public sealed record RefreshSessionCommand(string? RefreshToken, string? UserAgent) : ICommand<AuthTokens>;

internal sealed class RefreshSessionHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ITokenService tokens,
    SessionIssuer sessions,
    TimeProvider clock) : ICommandHandler<RefreshSessionCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.RefreshToken))
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var now = clock.GetUtcNow();
        var hash = tokens.HashSecret(command.RefreshToken);
        var current = await db.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.SubjectType == SubjectType.Staff, cancellationToken);
        if (current is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        if (current.RevokedAt is not null)
        {
            // A rotated token came back: someone else holds a copy, so the whole family is cut off.
            await RevokeFamily(current.FamilyId, now, cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        if (current.ExpiresAt <= now)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var user = await directory.FindUserByIdAsync(current.SubjectId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var tenant = await SessionGuards.LoadTenant(db, user, cancellationToken);
        if (tenant is { Status: TenantStatus.Suspended })
        {
            return AuthErrors.TenantSuspended;
        }

        var (issued, successor) = sessions.Issue(user, tenant, command.UserAgent, current.FamilyId);

        // Claiming the old token is atomic, so two concurrent refreshes cannot both rotate it.
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.ReplacedById, successor.Id),
                cancellationToken);
        if (claimed == 0)
        {
            await RevokeFamily(current.FamilyId, now, cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        await db.SaveChangesAsync(cancellationToken);
        return issued;
    }

    private Task<int> RevokeFamily(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);
}

public sealed record LogoutCommand(string? RefreshToken) : ICommand<Success>;

internal sealed class LogoutHandler(IAppDbContext db, ITokenService tokens, TimeProvider clock)
    : ICommandHandler<LogoutCommand, Success>
{
    public async Task<Result<Success>> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.RefreshToken))
        {
            return default(Success);
        }

        var hash = tokens.HashSecret(command.RefreshToken);
        var familyId = await db.RefreshTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamilyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (familyId is not null)
        {
            var now = clock.GetUtcNow();
            await db.RefreshTokens
                .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);
        }

        return default(Success);
    }
}
