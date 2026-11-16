using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;

namespace NexDesk.Application.Auth;

public sealed record ForgotPasswordCommand(string Email) : ICommand<Success>;

internal sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() => RuleFor(c => c.Email).ValidEmail();
}

internal sealed class ForgotPasswordHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    AuthEmails authEmails) : ICommandHandler<ForgotPasswordCommand, Success>
{
    public async Task<Result<Success>> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await directory.FindUserByEmailAsync(command.Email, cancellationToken);
        if (user is { IsActive: true })
        {
            var message = authEmails.PreparePasswordReset(user);
            await db.SaveChangesAsync(cancellationToken);
            authEmails.Send(message);
        }

        return default(Success);
    }
}

public sealed record ResetPasswordCommand(string Token, string NewPassword) : ICommand<Success>;

internal sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(c => c.Token).NotEmpty().MaximumLength(200);
        RuleFor(c => c.NewPassword).StrongPassword();
    }
}

internal sealed class ResetPasswordHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ITokenService tokens,
    IPasswordHasher passwordHasher,
    ISessionStateCache sessionState,
    TimeProvider clock) : ICommandHandler<ResetPasswordCommand, Success>
{
    public async Task<Result<Success>> Handle(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashSecret(command.Token);
        var token = await db.OneTimeTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash
                && t.Purpose == OneTimeTokenPurpose.PasswordReset
                && t.SubjectType == SubjectType.Staff,
            cancellationToken);
        if (token is null || !token.IsUsable(now))
        {
            return AuthErrors.InvalidToken;
        }

        var user = await directory.FindUserByIdAsync(token.SubjectId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return AuthErrors.InvalidToken;
        }

        token.Consume(now);
        user.ChangePassword(passwordHasher.Hash(command.NewPassword));

        // Receiving the reset link proves control of the mailbox.
        user.MarkEmailVerified(now);
        await db.SaveChangesAsync(cancellationToken);

        await db.RefreshTokens
            .Where(t => t.SubjectId == user.Id && t.SubjectType == SubjectType.Staff && t.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);
        sessionState.Invalidate(user.Id);
        return default(Success);
    }
}
