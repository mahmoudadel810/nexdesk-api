using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;

namespace NexDesk.Domain.Customers;

/// <summary>A person who asks for help. Unique per tenant by email address.</summary>
public sealed class Customer : AuditableEntity, ITenantOwned
{
    public const int FullNameMaxLength = 100;

    private Customer()
    {
    }

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = null!;

    public string FullName { get; private set; } = null!;

    /// <summary>Null until the customer signs up for the portal; agents and email can create customers without one.</summary>
    public string? PasswordHash { get; private set; }

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    public int SecurityVersion { get; private set; } = 1;

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public bool HasPortalAccount => PasswordHash is not null;

    public bool IsEmailVerified => EmailVerifiedAt is not null;

    public static Customer Create(string email, string? fullName)
    {
        var normalized = User.NormalizeEmail(email);
        return new Customer
        {
            Email = normalized,
            FullName = string.IsNullOrWhiteSpace(fullName) ? normalized[..normalized.IndexOf('@')] : fullName.Trim(),
        };
    }

    public void Rename(string fullName) => FullName = fullName.Trim();

    /// <summary>Sets or replaces the portal password and signs out every existing portal session.</summary>
    public void SetPassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        SecurityVersion++;
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public void MarkEmailVerified(DateTimeOffset now) => EmailVerifiedAt ??= now;

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= User.MaxFailedLogins)
        {
            LockedUntil = now + User.LockoutDuration;
            FailedLoginCount = 0;
        }
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }
}
