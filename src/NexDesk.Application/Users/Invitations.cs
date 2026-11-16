using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Users;

public sealed record InvitationDto(
    Guid Id,
    string Email,
    Role Role,
    InvitationStatus Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record InvitationPreview(string WorkspaceName, string Email, Role Role);

public static class InvitationErrors
{
    public static readonly Error NotFound = Error.NotFound("invitation-not-found", "The invitation was not found.");

    public static readonly Error AlreadyPending =
        Error.Conflict("invitation-pending", "This person already has a pending invitation.");

    public static readonly Error NoLongerPending =
        Error.Conflict("invitation-closed", "This invitation was already accepted or revoked.");
}

/// <summary>Builds and queues the invitation email. The raw token exists only inside that email.</summary>
public sealed class InvitationMailer(
    IAppDbContext db,
    ITenantContext tenantContext,
    ITokenService tokens,
    IEmailDispatcher emails,
    IOptions<AppOptions> appOptions)
{
    public string NewToken(out string hash)
    {
        var secret = tokens.CreateSecret();
        hash = tokens.HashSecret(secret);
        return secret;
    }

    public async Task SendAsync(Invitation invitation, string secret, Guid inviterId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .FirstAsync(t => t.Id == tenantContext.TenantId, cancellationToken);
        var inviter = await db.Users.AsNoTracking()
            .Where(u => u.Id == inviterId)
            .Select(u => u.FullName)
            .FirstAsync(cancellationToken);

        emails.Enqueue(new EmailMessage(
            invitation.Email,
            invitation.Email,
            "invitation",
            tenant.DefaultLanguage,
            new Dictionary<string, string>
            {
                ["inviter"] = inviter,
                ["workspace"] = tenant.Name,
                ["link"] = $"{appOptions.Value.WebBaseUrl.TrimEnd('/')}/auth/accept-invite?token={Uri.EscapeDataString(secret)}",
                ["days"] = Invitation.Lifetime.Days.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }
}

public sealed record ListInvitationsQuery : IQuery<IReadOnlyList<InvitationDto>>;

internal sealed class ListInvitationsHandler(IAppDbContext db, TimeProvider clock)
    : IQueryHandler<ListInvitationsQuery, IReadOnlyList<InvitationDto>>
{
    public async Task<Result<IReadOnlyList<InvitationDto>>> Handle(
        ListInvitationsQuery query,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var open = await db.Invitations.AsNoTracking()
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
        return open
            .Select(i => new InvitationDto(i.Id, i.Email, i.Role, i.StatusAt(now), i.ExpiresAt, i.CreatedAt))
            .ToList();
    }
}

public sealed record CreateInvitationCommand(string Email, Role Role) : ICommand<InvitationDto>;

internal sealed class CreateInvitationValidator : AbstractValidator<CreateInvitationCommand>
{
    public CreateInvitationValidator()
    {
        RuleFor(c => c.Email).ValidEmail();
        RuleFor(c => c.Role).IsInEnum();
    }
}

internal sealed class CreateInvitationHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ICurrentUser currentUser,
    InvitationMailer mailer,
    TimeProvider clock) : ICommandHandler<CreateInvitationCommand, InvitationDto>
{
    public async Task<Result<InvitationDto>> Handle(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        var inviterId = currentUser.RequireUserId();
        var email = User.NormalizeEmail(command.Email);
        if (command.Role == Role.Owner
            && !await db.Users.AnyAsync(u => u.Id == inviterId && u.Role == Role.Owner, cancellationToken))
        {
            return UserErrors.InsufficientRole;
        }

        if (await directory.EmailExistsAsync(email, cancellationToken))
        {
            return AuthErrors.EmailTaken;
        }

        var now = clock.GetUtcNow();
        if (await db.Invitations.AnyAsync(
            i => i.Email == email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now,
            cancellationToken))
        {
            return InvitationErrors.AlreadyPending;
        }

        var secret = mailer.NewToken(out var hash);
        var invitation = Invitation.Create(email, command.Role, hash, inviterId, now);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        await mailer.SendAsync(invitation, secret, inviterId, cancellationToken);

        return new InvitationDto(
            invitation.Id, invitation.Email, invitation.Role, InvitationStatus.Pending, invitation.ExpiresAt, invitation.CreatedAt);
    }
}

public sealed record ResendInvitationCommand(Guid InvitationId) : ICommand<InvitationDto>;

internal sealed class ResendInvitationHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    InvitationMailer mailer,
    TimeProvider clock) : ICommandHandler<ResendInvitationCommand, InvitationDto>
{
    public async Task<Result<InvitationDto>> Handle(ResendInvitationCommand command, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == command.InvitationId, cancellationToken);
        if (invitation is null)
        {
            return InvitationErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        if (invitation.StatusAt(now) is InvitationStatus.Accepted or InvitationStatus.Revoked)
        {
            return InvitationErrors.NoLongerPending;
        }

        var secret = mailer.NewToken(out var hash);
        invitation.Renew(hash, now);
        await db.SaveChangesAsync(cancellationToken);
        await mailer.SendAsync(invitation, secret, currentUser.RequireUserId(), cancellationToken);

        return new InvitationDto(
            invitation.Id, invitation.Email, invitation.Role, InvitationStatus.Pending, invitation.ExpiresAt, invitation.CreatedAt);
    }
}

public sealed record RevokeInvitationCommand(Guid InvitationId) : ICommand<Success>;

internal sealed class RevokeInvitationHandler(IAppDbContext db, TimeProvider clock)
    : ICommandHandler<RevokeInvitationCommand, Success>
{
    public async Task<Result<Success>> Handle(RevokeInvitationCommand command, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == command.InvitationId, cancellationToken);
        if (invitation is null)
        {
            return InvitationErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        if (invitation.StatusAt(now) == InvitationStatus.Accepted)
        {
            return InvitationErrors.NoLongerPending;
        }

        invitation.Revoke(now);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record LookupInvitationQuery(string Token) : IQuery<InvitationPreview>;

internal sealed class LookupInvitationHandler(IIdentityDirectory directory, ITokenService tokens, TimeProvider clock)
    : IQueryHandler<LookupInvitationQuery, InvitationPreview>
{
    public async Task<Result<InvitationPreview>> Handle(LookupInvitationQuery query, CancellationToken cancellationToken)
    {
        var found = await directory.FindInvitationAsync(tokens.HashSecret(query.Token ?? string.Empty), cancellationToken);
        if (found is null
            || found.Invitation.StatusAt(clock.GetUtcNow()) != InvitationStatus.Pending
            || found.Tenant.Status != TenantStatus.Active)
        {
            return AuthErrors.InvalidToken;
        }

        return new InvitationPreview(found.Tenant.Name, found.Invitation.Email, found.Invitation.Role);
    }
}

public sealed record AcceptInvitationCommand(string Token, string FullName, string Password, string? UserAgent)
    : ICommand<AuthTokens>;

internal sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationValidator()
    {
        RuleFor(c => c.Token).NotEmpty().MaximumLength(200);
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(User.FullNameMaxLength);
        RuleFor(c => c.Password).StrongPassword();
    }
}

internal sealed class AcceptInvitationHandler(
    IAppDbContext db,
    IIdentityDirectory directory,
    ITenantScopeFactory tenantScopes,
    ITokenService tokens,
    IPasswordHasher passwordHasher,
    SessionIssuer sessions,
    TimeProvider clock) : ICommandHandler<AcceptInvitationCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var found = await directory.FindInvitationAsync(tokens.HashSecret(command.Token), cancellationToken);
        if (found is null
            || found.Invitation.StatusAt(now) != InvitationStatus.Pending
            || found.Tenant.Status != TenantStatus.Active)
        {
            return AuthErrors.InvalidToken;
        }

        var (invitation, tenant) = found;

        if (await directory.EmailExistsAsync(invitation.Email, cancellationToken))
        {
            return AuthErrors.EmailTaken;
        }

        using (tenantScopes.Begin(tenant.Id))
        {
            var user = User.CreateStaff(tenant.Id, invitation.Email, command.FullName, invitation.Role, tenant.DefaultLanguage);
            user.SetPasswordHash(passwordHasher.Hash(command.Password));

            // The invitation link was delivered to this address, so it counts as verified.
            user.MarkEmailVerified(now);
            user.RecordSuccessfulLogin(now);
            invitation.Accept(now);
            db.Users.Add(user);

            var (issued, _) = sessions.Issue(user, tenant, command.UserAgent);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return AuthErrors.EmailTaken;
            }

            return issued;
        }
    }
}
