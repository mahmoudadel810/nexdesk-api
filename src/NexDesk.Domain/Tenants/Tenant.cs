using System.Text;
using System.Text.RegularExpressions;
using NexDesk.Domain.Common;

namespace NexDesk.Domain.Tenants;

public enum TenantStatus
{
    Active,
    Suspended,
}

public sealed partial class Tenant : AuditableEntity
{
    public const int NameMaxLength = 100;
    public const int SlugMinLength = 3;
    public const int SlugMaxLength = 40;

    private static readonly HashSet<string> ReservedSlugs =
    [
        "admin", "api", "app", "auth", "docs", "health", "hubs", "portal", "www", "nexdesk", "support", "static",
    ];

    private Tenant()
    {
    }

    public string Name { get; private set; } = null!;

    public string Slug { get; private set; } = null!;

    public TenantStatus Status { get; private set; }

    public string Timezone { get; private set; } = "UTC";

    public string DefaultLanguage { get; private set; } = "en";

    public static Tenant Create(string name, string slug, string timezone, string defaultLanguage) => new()
    {
        Name = name.Trim(),
        Slug = slug,
        Status = TenantStatus.Active,
        Timezone = timezone,
        DefaultLanguage = defaultLanguage,
    };

    public static bool IsValidSlug(string slug) =>
        slug.Length is >= SlugMinLength and <= SlugMaxLength
        && SlugPattern().IsMatch(slug)
        && !ReservedSlugs.Contains(slug);

    public static string SuggestSlug(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > SlugMaxLength)
        {
            slug = slug[..SlugMaxLength].Trim('-');
        }

        return IsValidSlug(slug) ? slug : string.Empty;
    }

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Reactivate() => Status = TenantStatus.Active;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
