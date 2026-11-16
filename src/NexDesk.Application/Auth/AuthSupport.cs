using FluentValidation;
using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;

namespace NexDesk.Application.Auth;

public static class AuthErrors
{
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("invalid-credentials", "The email or password is incorrect.");

    public static readonly Error InvalidRefreshToken =
        Error.Unauthorized("invalid-refresh-token", "The session has expired. Sign in again.");

    public static readonly Error AccountLocked =
        Error.Forbidden("account-locked", "Too many failed attempts. Try again in a few minutes.");

    public static readonly Error AccountDisabled =
        Error.Forbidden("account-disabled", "This account has been deactivated.");

    public static readonly Error EmailNotVerified =
        Error.Forbidden("email-not-verified", "Verify your email address before signing in.");

    public static readonly Error TenantSuspended =
        Error.Forbidden("tenant-suspended", "This workspace has been suspended.");

    public static readonly Error InvalidToken =
        Error.Validation("invalid-token", "This link is invalid or has expired.");

    public static readonly Error EmailTaken =
        Error.Conflict("email-taken", "An account with this email address already exists.");

    public static readonly Error SlugTaken =
        Error.Conflict("slug-taken", "This workspace address is already in use.");
}

public static class AuthRules
{
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(User.EmailMaxLength).EmailAddress();

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(PasswordMinLength)
            .MaximumLength(PasswordMaxLength)
            .Must(password => password is not null && password.Any(char.IsLetter) && password.Any(char.IsDigit))
            .WithMessage("The password must contain at least one letter and one digit.");
}

/// <summary>Issues one-time tokens and queues the emails that carry them.</summary>
public sealed class AuthEmails(
    IAppDbContext db,
    ITokenService tokens,
    IEmailDispatcher emails,
    TimeProvider clock,
    IOptions<AuthOptions> authOptions,
    IOptions<AppOptions> appOptions)
{
    public EmailMessage PrepareVerification(User user)
    {
        var hours = authOptions.Value.EmailVerificationHours;
        var secret = IssueToken(user, OneTimeTokenPurpose.EmailVerification, TimeSpan.FromHours(hours));
        return new EmailMessage(user.Email, user.FullName, "verify-email", user.Language, new Dictionary<string, string>
        {
            ["name"] = user.FullName,
            ["link"] = Link("auth/verify-email", secret),
            ["hours"] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    public EmailMessage PreparePasswordReset(User user)
    {
        var minutes = authOptions.Value.PasswordResetMinutes;
        var secret = IssueToken(user, OneTimeTokenPurpose.PasswordReset, TimeSpan.FromMinutes(minutes));
        return new EmailMessage(user.Email, user.FullName, "reset-password", user.Language, new Dictionary<string, string>
        {
            ["name"] = user.FullName,
            ["link"] = Link("auth/reset-password", secret),
            ["minutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    public void Send(EmailMessage message) => emails.Enqueue(message);

    private string IssueToken(User user, OneTimeTokenPurpose purpose, TimeSpan lifetime)
    {
        var secret = tokens.CreateSecret();
        db.OneTimeTokens.Add(OneTimeToken.Issue(
            SubjectType.Staff, user.Id, purpose, tokens.HashSecret(secret), clock.GetUtcNow(), lifetime));
        return secret;
    }

    private string Link(string path, string secret) =>
        $"{appOptions.Value.WebBaseUrl.TrimEnd('/')}/{path}?token={Uri.EscapeDataString(secret)}";
}
