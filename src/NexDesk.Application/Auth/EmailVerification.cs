using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Auth;

public sealed record VerifyEmailCommand(string Token, string? UserAgent) : ICommand<AuthTokens>;

internal sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailValidator() => RuleFor(c => c.Token).NotEmpty().MaximumLength(200);
}

internal sealed class VerifyEmailHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ITokenService tokens,
    SessionIssuer sessions,
    TimeProvider clock) : ICommandHandler<VerifyEmailCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashSecret(command.Token);
        var token = await db.OneTimeTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash
                && t.Purpose == OneTimeTokenPurpose.EmailVerification
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

        var tenant = await SessionGuards.LoadTenant(db, user, cancellationToken);
        if (tenant is { Status: TenantStatus.Suspended })
        {
            return AuthErrors.TenantSuspended;
        }

        token.Consume(now);
        user.MarkEmailVerified(now);
        user.RecordSuccessfulLogin(now);
        var (issued, _) = sessions.Issue(user, tenant, command.UserAgent);
        await db.SaveChangesAsync(cancellationToken);
        return issued;
    }
}

public sealed record ResendVerificationCommand(string Email) : ICommand<Success>;

internal sealed class ResendVerificationValidator : AbstractValidator<ResendVerificationCommand>
{
    public ResendVerificationValidator() => RuleFor(c => c.Email).ValidEmail();
}

internal sealed class ResendVerificationHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    AuthEmails authEmails) : ICommandHandler<ResendVerificationCommand, Success>
{
    public async Task<Result<Success>> Handle(ResendVerificationCommand command, CancellationToken cancellationToken)
    {
        var user = await directory.FindUserByEmailAsync(command.Email, cancellationToken);
        if (user is { IsActive: true, IsEmailVerified: false })
        {
            var message = authEmails.PrepareVerification(user);
            await db.SaveChangesAsync(cancellationToken);
            authEmails.Send(message);
        }

        return default(Success);
    }
}
