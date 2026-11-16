using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;

namespace NexDesk.Application.Users;

public sealed record UserDto(
    Guid Id,
    string Email,
    string FullName,
    Role Role,
    bool IsActive,
    Availability Availability,
    bool EmailVerified,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt)
{
    public static UserDto From(User user) => new(
        user.Id,
        user.Email,
        user.FullName,
        user.Role,
        user.IsActive,
        user.Availability,
        user.IsEmailVerified,
        user.LastLoginAt,
        user.CreatedAt);
}

public static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound("user-not-found", "The user was not found.");

    public static readonly Error CannotModifySelf =
        Error.Conflict("cannot-modify-self", "You cannot change your own role or deactivate your own account.");

    public static readonly Error InsufficientRole =
        Error.Forbidden("insufficient-role", "Only an owner can manage owners.");

    public static readonly Error WrongPassword =
        Error.Validation("wrong-password", "The current password is incorrect.");
}

public sealed record ListUsersQuery(
    string? Search,
    Role? Role,
    bool? IsActive,
    string? Sort,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize) : IQuery<PagedResult<UserDto>>, IPagedRequest;

internal sealed class ListUsersValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersValidator()
    {
        this.AddPagingRules();
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

internal sealed class ListUsersHandler(IAppDbContext db) : IQueryHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<Result<PagedResult<UserDto>>> Handle(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            users = users.Where(u => u.FullName.ToLower().Contains(term) || u.Email.Contains(term));
        }

        if (query.Role is { } role)
        {
            users = users.Where(u => u.Role == role);
        }

        if (query.IsActive is { } isActive)
        {
            users = users.Where(u => u.IsActive == isActive);
        }

        users = Paging.ParseSort(query.Sort, "name") switch
        {
            ("email", false) => users.OrderBy(u => u.Email),
            ("email", true) => users.OrderByDescending(u => u.Email),
            ("createdat", false) => users.OrderBy(u => u.CreatedAt),
            ("createdat", true) => users.OrderByDescending(u => u.CreatedAt),
            ("lastloginat", false) => users.OrderBy(u => u.LastLoginAt),
            ("lastloginat", true) => users.OrderByDescending(u => u.LastLoginAt),
            (_, true) => users.OrderByDescending(u => u.FullName).ThenBy(u => u.Id),
            _ => users.OrderBy(u => u.FullName).ThenBy(u => u.Id),
        };

        var page = await users.ToPagedAsync(query, cancellationToken);
        return new PagedResult<UserDto>([.. page.Items.Select(UserDto.From)], page.Page, page.PageSize, page.TotalCount);
    }
}

public sealed record GetUserQuery(Guid UserId) : IQuery<UserDto>;

internal sealed class GetUserHandler(IAppDbContext db) : IQueryHandler<GetUserQuery, UserDto>
{
    public async Task<Result<UserDto>> Handle(GetUserQuery query, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == query.UserId, cancellationToken);
        return user is null ? UserErrors.NotFound : UserDto.From(user);
    }
}

public sealed record ChangeUserRoleCommand(Guid UserId, Role Role) : ICommand<UserDto>;

internal sealed class ChangeUserRoleValidator : AbstractValidator<ChangeUserRoleCommand>
{
    public ChangeUserRoleValidator() => RuleFor(c => c.Role).IsInEnum();
}

internal sealed class ChangeUserRoleHandler(IAppDbContext db, ICurrentUser currentUser, ISessionStateCache sessions)
    : ICommandHandler<ChangeUserRoleCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(ChangeUserRoleCommand command, CancellationToken cancellationToken)
    {
        var (actor, target, error) = await UserAdministration.Load(db, currentUser, command.UserId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (command.Role == Role.Owner && actor!.Role != Role.Owner)
        {
            return UserErrors.InsufficientRole;
        }

        target!.ChangeRole(command.Role);
        await db.SaveChangesAsync(cancellationToken);
        sessions.Invalidate(target.Id);
        return UserDto.From(target);
    }
}

public sealed record DeactivateUserCommand(Guid UserId) : ICommand<UserDto>;

internal sealed class DeactivateUserHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    ISessionStateCache sessions,
    TimeProvider clock) : ICommandHandler<DeactivateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(DeactivateUserCommand command, CancellationToken cancellationToken)
    {
        var (_, target, error) = await UserAdministration.Load(db, currentUser, command.UserId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        target!.Deactivate();
        await db.SaveChangesAsync(cancellationToken);

        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.SubjectId == target.Id && t.SubjectType == SubjectType.Staff && t.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);
        sessions.Invalidate(target.Id);
        return UserDto.From(target);
    }
}

public sealed record ReactivateUserCommand(Guid UserId) : ICommand<UserDto>;

internal sealed class ReactivateUserHandler(IAppDbContext db, ICurrentUser currentUser, ISessionStateCache sessions)
    : ICommandHandler<ReactivateUserCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(ReactivateUserCommand command, CancellationToken cancellationToken)
    {
        var (_, target, error) = await UserAdministration.Load(db, currentUser, command.UserId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        target!.Reactivate();
        await db.SaveChangesAsync(cancellationToken);
        sessions.Invalidate(target.Id);
        return UserDto.From(target);
    }
}

internal static class UserAdministration
{
    /// <summary>
    /// Loads the acting and the target user and applies the rules common to every admin action.
    /// Nobody can change their own role or deactivate themselves, so the acting owner always remains.
    /// </summary>
    public static async Task<(User? Actor, User? Target, Error? Error)> Load(
        IAppDbContext db,
        ICurrentUser currentUser,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var actorId = currentUser.RequireUserId();
        if (actorId == targetId)
        {
            return (null, null, UserErrors.CannotModifySelf);
        }

        var users = await db.Users
            .Where(u => u.Id == actorId || u.Id == targetId)
            .ToListAsync(cancellationToken);
        var actor = users.FirstOrDefault(u => u.Id == actorId);
        var target = users.FirstOrDefault(u => u.Id == targetId);
        if (actor is null || target is null)
        {
            return (actor, target, UserErrors.NotFound);
        }

        return target.Role == Role.Owner && actor.Role != Role.Owner
            ? (actor, target, UserErrors.InsufficientRole)
            : (actor, target, null);
    }
}

public sealed record UpdateMyProfileCommand(string FullName, string Language) : ICommand<SessionDto>;

internal sealed class UpdateMyProfileValidator : AbstractValidator<UpdateMyProfileCommand>
{
    public UpdateMyProfileValidator()
    {
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(User.FullNameMaxLength);
        RuleFor(c => c.Language).Must(SupportedLanguages.All.Contains).WithMessage("The language is not supported.");
    }
}

internal sealed class UpdateMyProfileHandler(IAppDbContext db, IIdentityDirectory directory, ICurrentUser currentUser)
    : ICommandHandler<UpdateMyProfileCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(UpdateMyProfileCommand command, CancellationToken cancellationToken)
    {
        var user = await directory.FindUserByIdAsync(currentUser.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        user.UpdateProfile(command.FullName, command.Language);
        await db.SaveChangesAsync(cancellationToken);
        return SessionDto.From(user, await SessionGuards.LoadTenant(db, user, cancellationToken));
    }
}

public sealed record SetMyAvailabilityCommand(Availability Availability) : ICommand<UserDto>;

internal sealed class SetMyAvailabilityValidator : AbstractValidator<SetMyAvailabilityCommand>
{
    public SetMyAvailabilityValidator() => RuleFor(c => c.Availability).IsInEnum();
}

internal sealed class SetMyAvailabilityHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<SetMyAvailabilityCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(SetMyAvailabilityCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        user.SetAvailability(command.Availability);
        await db.SaveChangesAsync(cancellationToken);
        return UserDto.From(user);
    }
}

public sealed record ChangeMyPasswordCommand(string CurrentPassword, string NewPassword, string? UserAgent)
    : ICommand<AuthTokens>;

internal sealed class ChangeMyPasswordValidator : AbstractValidator<ChangeMyPasswordCommand>
{
    public ChangeMyPasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty().MaximumLength(AuthRules.PasswordMaxLength);
        RuleFor(c => c.NewPassword).StrongPassword();
    }
}

internal sealed class ChangeMyPasswordHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ICurrentUser currentUser,
    IPasswordHasher passwordHasher,
    ISessionStateCache sessionState,
    SessionIssuer sessions,
    TimeProvider clock) : ICommandHandler<ChangeMyPasswordCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(ChangeMyPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await directory.FindUserByIdAsync(currentUser.RequireUserId(), cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        if (!passwordHasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            return UserErrors.WrongPassword;
        }

        var now = clock.GetUtcNow();
        user.ChangePassword(passwordHasher.Hash(command.NewPassword));
        await db.RefreshTokens
            .Where(t => t.SubjectId == user.Id && t.SubjectType == SubjectType.Staff && t.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);

        // Every other device is signed out; this one continues with a fresh token pair.
        var tenant = await SessionGuards.LoadTenant(db, user, cancellationToken);
        var (issued, _) = sessions.Issue(user, tenant, command.UserAgent);
        await db.SaveChangesAsync(cancellationToken);
        sessionState.Invalidate(user.Id);
        return issued;
    }
}
