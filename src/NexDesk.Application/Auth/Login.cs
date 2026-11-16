using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Auth;

public sealed record LoginCommand(string Email, string Password, string? UserAgent) : ICommand<AuthTokens>;

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().MaximumLength(User.EmailMaxLength);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(AuthRules.PasswordMaxLength);
    }
}

internal sealed class LoginHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    IPasswordHasher passwordHasher,
    SessionIssuer sessions,
    TimeProvider clock) : ICommandHandler<LoginCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var user = await directory.FindUserByEmailAsync(command.Email, cancellationToken);
        if (user is null)
        {
            // Spend the same hashing time as a real attempt so response timing does not reveal the account.
            passwordHasher.Hash(command.Password);
            return AuthErrors.InvalidCredentials;
        }

        if (user.IsLockedOut(now))
        {
            return AuthErrors.AccountLocked;
        }

        if (!passwordHasher.Verify(user.PasswordHash, command.Password))
        {
            user.RecordFailedLogin(now);
            await db.SaveChangesAsync(cancellationToken);
            return user.IsLockedOut(now) ? AuthErrors.AccountLocked : AuthErrors.InvalidCredentials;
        }

        if (!user.IsActive)
        {
            return AuthErrors.AccountDisabled;
        }

        if (!user.IsEmailVerified)
        {
            return AuthErrors.EmailNotVerified;
        }

        var tenant = await SessionGuards.LoadTenant(db, user, cancellationToken);
        if (tenant is { Status: TenantStatus.Suspended })
        {
            return AuthErrors.TenantSuspended;
        }

        user.RecordSuccessfulLogin(now);
        var (tokens, _) = sessions.Issue(user, tenant, command.UserAgent);
        await db.SaveChangesAsync(cancellationToken);
        return tokens;
    }
}

internal static class SessionGuards
{
    public static async Task<Tenant?> LoadTenant(IAppDbContext db, User user, CancellationToken cancellationToken) =>
        user.TenantId is { } tenantId
            ? await db.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken)
            : null;
}
