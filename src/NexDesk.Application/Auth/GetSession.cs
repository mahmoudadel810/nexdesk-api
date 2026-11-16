using NexDesk.Application.Common;

namespace NexDesk.Application.Auth;

public sealed record GetSessionQuery : IQuery<SessionDto>;

internal sealed class GetSessionHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ICurrentUser currentUser) : IQueryHandler<GetSessionQuery, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(GetSessionQuery query, CancellationToken cancellationToken)
    {
        var user = currentUser.UserId is { } userId
            ? await directory.FindUserByIdAsync(userId, cancellationToken)
            : null;
        if (user is null)
        {
            return AuthErrors.InvalidCredentials;
        }

        var tenant = await SessionGuards.LoadTenant(db, user, cancellationToken);
        return SessionDto.From(user, tenant);
    }
}
