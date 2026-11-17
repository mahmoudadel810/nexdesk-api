using NexDesk.Domain.Common;

namespace NexDesk.Domain.Developer;

/// <summary>What an API key may do. A key holds one or more of these.</summary>
public static class ApiScopes
{
    public const string TicketsRead = "tickets:read";
    public const string TicketsWrite = "tickets:write";

    public static IReadOnlyList<string> All { get; } = [TicketsRead, TicketsWrite];
}

/// <summary>
/// A credential an integration uses to call the public API on the workspace's behalf. The key itself is
/// shown once when it is issued; only its hash is kept.
/// </summary>
public sealed class ApiKey : AuditableEntity, ITenantOwned
{
    public const int NameMaxLength = 100;
    public const string KeyPrefix = "ndk_";

    /// <summary>Recording every use would mean a write per request; once a minute is precise enough.</summary>
    public static readonly TimeSpan UsageResolution = TimeSpan.FromMinutes(1);

    private ApiKey()
    {
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>The public start of the key, unique across all workspaces, used to find it.</summary>
    public string Prefix { get; private set; } = null!;

    public string KeyHash { get; private set; } = null!;

    public List<string> Scopes { get; private set; } = [];

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static ApiKey Issue(
        string name,
        string prefix,
        string keyHash,
        IEnumerable<string> scopes,
        DateTimeOffset? expiresAt)
    {
        var granted = scopes.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (granted.Count == 0 || granted.Any(scope => !ApiScopes.All.Contains(scope)))
        {
            throw new ArgumentException("Choose at least one known scope.", nameof(scopes));
        }

        return new ApiKey
        {
            Name = name.Trim(),
            Prefix = prefix,
            KeyHash = keyHash,
            Scopes = granted,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>The full key as handed to the caller: the prefix tells which key it is, the secret proves it.</summary>
    public static string Compose(string prefix, string secret) => $"{KeyPrefix}{prefix}_{secret}";

    /// <summary>Reads the prefix out of a presented key, or null when it is not shaped like one of ours.</summary>
    public static string? PrefixOf(string presented)
    {
        if (!presented.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = presented[KeyPrefix.Length..];
        var separator = rest.IndexOf('_', StringComparison.Ordinal);
        return separator > 0 && separator < rest.Length - 1 ? rest[..separator] : null;
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    /// <summary>Whether a use at <paramref name="now"/> is worth recording.</summary>
    public bool ShouldRecordUse(DateTimeOffset now) => LastUsedAt is null || now - LastUsedAt >= UsageResolution;
}
