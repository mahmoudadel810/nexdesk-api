using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tenants;

namespace NexDesk.Application.Portal;

/// <summary>What the public needs to know about a workspace to show its help centre.</summary>
public sealed record PortalInfoDto(string Name, string Slug, string DefaultLanguage);

public sealed record PortalCustomerDto(Guid Id, string Email, string FullName);

public sealed record PortalTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    PortalCustomerDto Customer);

public static class PortalErrors
{
    public static readonly Error WorkspaceNotFound =
        Error.NotFound("workspace-not-found", "This help centre does not exist.");

    public static readonly Error NotSignedIn = Error.Unauthorized("unauthorized", "Sign in to continue.");
}

/// <summary>Issues portal token pairs and the emails that carry one-time links for customers.</summary>
public sealed class PortalSessions(
    IAppDbContext db,
    ITenantContext tenantContext,
    ITokenService tokens,
    IEmailDispatcher emails,
    TimeProvider clock,
    IOptions<AuthOptions> authOptions,
    IOptions<AppOptions> appOptions)
{
    public (PortalTokens Tokens, RefreshToken RefreshToken) Issue(Customer customer, string? userAgent, Guid? familyId = null)
    {
        var secret = tokens.CreateSecret();
        var refreshToken = RefreshToken.Issue(
            SubjectType.Customer,
            customer.Id,
            tokens.HashSecret(secret),
            clock.GetUtcNow(),
            TimeSpan.FromDays(authOptions.Value.RefreshTokenDays),
            userAgent,
            familyId);
        db.RefreshTokens.Add(refreshToken);
        var access = tokens.CreateCustomerAccessToken(customer);
        return (
            new PortalTokens(
                access.Value,
                access.ExpiresAt,
                secret,
                refreshToken.ExpiresAt,
                new PortalCustomerDto(customer.Id, customer.Email, customer.FullName)),
            refreshToken);
    }

    /// <summary>
    /// Creates a one-time token and returns the email that lets the customer choose a password. The caller
    /// saves and sends. A password is only ever set through this link, which proves control of the mailbox.
    /// </summary>
    public async Task<EmailMessage> PrepareSetPasswordAsync(
        Customer customer,
        bool isSignUp,
        CancellationToken cancellationToken)
    {
        var workspace = await WorkspaceAsync(cancellationToken);
        var lifetime = isSignUp
            ? TimeSpan.FromHours(authOptions.Value.EmailVerificationHours)
            : TimeSpan.FromMinutes(authOptions.Value.PasswordResetMinutes);
        var secret = tokens.CreateSecret();
        db.OneTimeTokens.Add(OneTimeToken.Issue(
            SubjectType.Customer,
            customer.Id,
            OneTimeTokenPurpose.PasswordReset,
            tokens.HashSecret(secret),
            clock.GetUtcNow(),
            lifetime));

        return new EmailMessage(
            customer.Email,
            customer.FullName,
            isSignUp ? "portal-welcome" : "portal-reset-password",
            workspace.DefaultLanguage,
            new Dictionary<string, string>
            {
                ["name"] = customer.FullName,
                ["workspace"] = workspace.Name,
                ["link"] = $"{PortalUrl(workspace.Slug)}/reset-password?token={Uri.EscapeDataString(secret)}",
                ["hours"] = authOptions.Value.EmailVerificationHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["minutes"] = authOptions.Value.PasswordResetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    public void Send(EmailMessage message) => emails.Enqueue(message);

    public string PortalUrl(string slug) => $"{appOptions.Value.WebBaseUrl.TrimEnd('/')}/portal/{slug}";

    public Task<Tenant> WorkspaceAsync(CancellationToken cancellationToken) =>
        db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantContext.TenantId, cancellationToken);

    /// <summary>Finds an unused, unexpired set-password token and the customer it belongs to.</summary>
    public async Task<(OneTimeToken Token, Customer Customer)?> FindSetPasswordTokenAsync(
        string secret,
        CancellationToken cancellationToken)
    {
        var hash = tokens.HashSecret(secret);
        var token = await db.OneTimeTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash
                && t.Purpose == OneTimeTokenPurpose.PasswordReset
                && t.SubjectType == SubjectType.Customer,
            cancellationToken);
        if (token is null || !token.IsUsable(clock.GetUtcNow()))
        {
            return null;
        }

        // The customer lookup is tenant-filtered, so a token from another workspace finds nobody here.
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == token.SubjectId, cancellationToken);
        return customer is null ? null : (token, customer);
    }
}

public sealed record GetPortalInfoQuery(string Slug) : IQuery<PortalInfoDto>;

internal sealed class GetPortalInfoHandler(IAppDbContext db) : IQueryHandler<GetPortalInfoQuery, PortalInfoDto>
{
    public async Task<Result<PortalInfoDto>> Handle(GetPortalInfoQuery query, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == query.Slug && t.Status == TenantStatus.Active, cancellationToken);
        return tenant is null
            ? PortalErrors.WorkspaceNotFound
            : new PortalInfoDto(tenant.Name, tenant.Slug, tenant.DefaultLanguage);
    }
}

public sealed record PortalRegisterCommand(string FullName, string Email) : ICommand<Success>;

internal sealed class PortalRegisterValidator : AbstractValidator<PortalRegisterCommand>
{
    public PortalRegisterValidator()
    {
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(Customer.FullNameMaxLength);
        RuleFor(c => c.Email).ValidEmail();
    }
}

/// <summary>
/// Sign-up never takes a password. Whoever submits the form only causes an email to the address; the
/// password is chosen by the person who opens that email. This way a stranger cannot attach credentials
/// to someone else's address, whether or not that address already has tickets.
/// </summary>
internal sealed class PortalRegisterHandler(IAppDbContext db, PortalSessions sessions)
    : ICommandHandler<PortalRegisterCommand, Success>
{
    public async Task<Result<Success>> Handle(PortalRegisterCommand command, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(command.Email);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        var isNew = customer is null;
        if (customer is null)
        {
            customer = Customer.Create(email, command.FullName);
            db.Customers.Add(customer);
        }

        // An existing record keeps its name: the form was not necessarily filled in by its owner.
        var message = await sessions.PrepareSetPasswordAsync(
            customer, isSignUp: isNew || !customer.HasPortalAccount, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        sessions.Send(message);
        return default(Success);
    }
}

public sealed record PortalLoginCommand(string Email, string Password, string? UserAgent) : ICommand<PortalTokens>;

internal sealed class PortalLoginValidator : AbstractValidator<PortalLoginCommand>
{
    public PortalLoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().MaximumLength(User.EmailMaxLength);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(AuthRules.PasswordMaxLength);
    }
}

internal sealed class PortalLoginHandler(
    IAppDbContext db,
    IPasswordHasher passwordHasher,
    PortalSessions sessions,
    TimeProvider clock) : ICommandHandler<PortalLoginCommand, PortalTokens>
{
    public async Task<Result<PortalTokens>> Handle(PortalLoginCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var email = User.NormalizeEmail(command.Email);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (customer?.PasswordHash is null)
        {
            passwordHasher.Hash(command.Password);
            return AuthErrors.InvalidCredentials;
        }

        if (customer.IsLockedOut(now))
        {
            return AuthErrors.AccountLocked;
        }

        if (!passwordHasher.Verify(customer.PasswordHash, command.Password))
        {
            customer.RecordFailedLogin(now);
            await db.SaveChangesAsync(cancellationToken);
            return customer.IsLockedOut(now) ? AuthErrors.AccountLocked : AuthErrors.InvalidCredentials;
        }

        customer.RecordSuccessfulLogin(now);
        var (issued, _) = sessions.Issue(customer, command.UserAgent);
        await db.SaveChangesAsync(cancellationToken);
        return issued;
    }
}

public sealed record PortalRefreshCommand(string? RefreshToken, string? UserAgent) : ICommand<PortalTokens>;

internal sealed class PortalRefreshHandler(
    IAppDbContext db,
    ITokenService tokens,
    PortalSessions sessions,
    TimeProvider clock) : ICommandHandler<PortalRefreshCommand, PortalTokens>
{
    public async Task<Result<PortalTokens>> Handle(PortalRefreshCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.RefreshToken))
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var now = clock.GetUtcNow();
        var hash = tokens.HashSecret(command.RefreshToken);
        var current = await db.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.SubjectType == SubjectType.Customer, cancellationToken);
        if (current is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        if (current.RevokedAt is not null)
        {
            await RevokeFamily(current.FamilyId, now, cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == current.SubjectId, cancellationToken);
        if (current.ExpiresAt <= now || customer is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var (issued, successor) = sessions.Issue(customer, command.UserAgent, current.FamilyId);
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedById, successor.Id),
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

public sealed record PortalForgotPasswordCommand(string Email) : ICommand<Success>;

internal sealed class PortalForgotPasswordValidator : AbstractValidator<PortalForgotPasswordCommand>
{
    public PortalForgotPasswordValidator() => RuleFor(c => c.Email).ValidEmail();
}

internal sealed class PortalForgotPasswordHandler(IAppDbContext db, PortalSessions sessions)
    : ICommandHandler<PortalForgotPasswordCommand, Success>
{
    public async Task<Result<Success>> Handle(PortalForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(command.Email);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (customer is not null)
        {
            // Also works for customers who only ever emailed in: the link lets them choose a first password.
            var message = await sessions.PrepareSetPasswordAsync(customer, isSignUp: false, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            sessions.Send(message);
        }

        return default(Success);
    }
}

public sealed record PortalResetPasswordCommand(string Token, string NewPassword) : ICommand<Success>;

internal sealed class PortalResetPasswordValidator : AbstractValidator<PortalResetPasswordCommand>
{
    public PortalResetPasswordValidator()
    {
        RuleFor(c => c.Token).NotEmpty().MaximumLength(200);
        RuleFor(c => c.NewPassword).StrongPassword();
    }
}

internal sealed class PortalResetPasswordHandler(
    IAppDbContext db,
    IPasswordHasher passwordHasher,
    PortalSessions sessions,
    ISessionStateCache sessionState,
    TimeProvider clock) : ICommandHandler<PortalResetPasswordCommand, Success>
{
    public async Task<Result<Success>> Handle(PortalResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var found = await sessions.FindSetPasswordTokenAsync(command.Token, cancellationToken);
        if (found is not { } match)
        {
            return AuthErrors.InvalidToken;
        }

        var now = clock.GetUtcNow();
        match.Token.Consume(now);
        match.Customer.SetPassword(passwordHasher.Hash(command.NewPassword));
        match.Customer.MarkEmailVerified(now);
        await db.SaveChangesAsync(cancellationToken);

        var customerId = match.Customer.Id;
        await db.RefreshTokens
            .Where(t => t.SubjectId == customerId && t.SubjectType == SubjectType.Customer && t.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);
        sessionState.InvalidateCustomer(customerId);
        return default(Success);
    }
}

public sealed record PortalLogoutCommand(string? RefreshToken) : ICommand<Success>;

internal sealed class PortalLogoutHandler(IAppDbContext db, ITokenService tokens, TimeProvider clock)
    : ICommandHandler<PortalLogoutCommand, Success>
{
    public async Task<Result<Success>> Handle(PortalLogoutCommand command, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(command.RefreshToken))
        {
            var hash = tokens.HashSecret(command.RefreshToken);
            var now = clock.GetUtcNow();
            var familyId = await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.SubjectType == SubjectType.Customer)
                .Select(t => (Guid?)t.FamilyId)
                .FirstOrDefaultAsync(cancellationToken);
            if (familyId is not null)
            {
                await db.RefreshTokens
                    .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(t => t.RevokedAt, now), cancellationToken);
            }
        }

        return default(Success);
    }
}

public sealed record GetPortalCustomerQuery : IQuery<PortalCustomerDto>;

internal sealed class GetPortalCustomerHandler(IAppDbContext db, ICurrentCustomer currentCustomer)
    : IQueryHandler<GetPortalCustomerQuery, PortalCustomerDto>
{
    public async Task<Result<PortalCustomerDto>> Handle(GetPortalCustomerQuery query, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == currentCustomer.CustomerId, cancellationToken);
        return customer is null
            ? PortalErrors.NotSignedIn
            : new PortalCustomerDto(customer.Id, customer.Email, customer.FullName);
    }
}
