using NexDesk.Domain.Common;

namespace NexDesk.Domain.Identity;

public enum SubjectType
{
    Staff,
    Customer,
}

public sealed class RefreshToken : Entity
{
    private RefreshToken()
    {
    }

    public SubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? ReplacedById { get; private set; }

    public string? UserAgent { get; private set; }

    public static RefreshToken Issue(
        SubjectType subjectType,
        Guid subjectId,
        string tokenHash,
        DateTimeOffset now,
        TimeSpan lifetime,
        string? userAgent,
        Guid? familyId = null) => new()
        {
            SubjectType = subjectType,
            SubjectId = subjectId,
            FamilyId = familyId ?? Guid.CreateVersion7(),
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
        };

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void ReplaceWith(RefreshToken successor, DateTimeOffset now)
    {
        Revoke(now);
        ReplacedById = successor.Id;
    }
}

public enum OneTimeTokenPurpose
{
    EmailVerification,
    PasswordReset,
}

public sealed class OneTimeToken : Entity
{
    private OneTimeToken()
    {
    }

    public SubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public OneTimeTokenPurpose Purpose { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public static OneTimeToken Issue(
        SubjectType subjectType,
        Guid subjectId,
        OneTimeTokenPurpose purpose,
        string tokenHash,
        DateTimeOffset now,
        TimeSpan lifetime) => new()
        {
            SubjectType = subjectType,
            SubjectId = subjectId,
            Purpose = purpose,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;

    public void Consume(DateTimeOffset now) => ConsumedAt = now;
}
