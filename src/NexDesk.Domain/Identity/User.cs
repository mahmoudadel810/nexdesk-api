using NexDesk.Domain.Common;

namespace NexDesk.Domain.Identity;

public enum Availability
{
    Online,
    Away,
    Offline,
}

public sealed class User : AuditableEntity
{
    public const int FullNameMaxLength = 100;
    public const int EmailMaxLength = 254;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private User()
    {
    }

    public Guid? TenantId { get; private set; }

    public string Email { get; private set; } = null!;

    public string PasswordHash { get; private set; } = null!;

    public string FullName { get; private set; } = null!;

    public Role Role { get; private set; }

    public bool IsPlatformAdmin { get; private set; }

    public bool IsActive { get; private set; }

    public string Language { get; private set; } = "en";

    public Availability Availability { get; private set; } = Availability.Offline;

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    public int SecurityVersion { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAt is not null;

    public static User CreateStaff(Guid tenantId, string email, string fullName, Role role, string language) => new()
    {
        TenantId = tenantId,
        Email = NormalizeEmail(email),
        FullName = fullName.Trim(),
        Role = role,
        IsActive = true,
        Language = language,
        SecurityVersion = 1,
    };

    public static User CreatePlatformAdmin(string email, string fullName, DateTimeOffset now) => new()
    {
        Email = NormalizeEmail(email),
        FullName = fullName.Trim(),
        Role = Role.Owner,
        IsPlatformAdmin = true,
        IsActive = true,
        EmailVerifiedAt = now,
        SecurityVersion = 1,
    };

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

    public void ChangePassword(string passwordHash)
    {
        PasswordHash = passwordHash;
        SecurityVersion++;
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public void UpdateProfile(string fullName, string language)
    {
        FullName = fullName.Trim();
        Language = language;
    }

    public void SetAvailability(Availability availability) => Availability = availability;

    /// <summary>Changes the role and invalidates access tokens that still carry the old permissions.</summary>
    public void ChangeRole(Role role)
    {
        if (Role != role)
        {
            Role = role;
            SecurityVersion++;
        }
    }

    public void Deactivate()
    {
        if (IsActive)
        {
            IsActive = false;
            Availability = Availability.Offline;
            SecurityVersion++;
        }
    }

    public void Reactivate() => IsActive = true;

    public void MarkEmailVerified(DateTimeOffset now) => EmailVerifiedAt ??= now;

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockedUntil = now + LockoutDuration;
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
