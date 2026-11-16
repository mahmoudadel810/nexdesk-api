using NexDesk.Domain.Common;

namespace NexDesk.Domain.Identity;

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}

public sealed class Invitation : AuditableEntity, ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private Invitation()
    {
    }

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = null!;

    public Role Role { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid InvitedById { get; private set; }

    public static Invitation Create(string email, Role role, string tokenHash, Guid invitedById, DateTimeOffset now) =>
        new()
        {
            Email = User.NormalizeEmail(email),
            Role = role,
            TokenHash = tokenHash,
            InvitedById = invitedById,
            ExpiresAt = now + Lifetime,
        };

    public InvitationStatus StatusAt(DateTimeOffset now) => this switch
    {
        { AcceptedAt: not null } => InvitationStatus.Accepted,
        { RevokedAt: not null } => InvitationStatus.Revoked,
        _ when ExpiresAt <= now => InvitationStatus.Expired,
        _ => InvitationStatus.Pending,
    };

    public void Renew(string tokenHash, DateTimeOffset now)
    {
        TokenHash = tokenHash;
        ExpiresAt = now + Lifetime;
    }

    public void Accept(DateTimeOffset now) => AcceptedAt = now;

    public void Revoke(DateTimeOffset now) => RevokedAt = now;
}
