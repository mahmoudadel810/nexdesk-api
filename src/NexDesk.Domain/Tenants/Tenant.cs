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
    public const int WelcomeTextMaxLength = 1000;
    public const long LogoMaxSizeBytes = 512 * 1024;

    public static IReadOnlyList<string> Languages { get; } = ["en", "ar"];

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

    public DateTimeOffset? OnboardingCompletedAt { get; private set; }

    /// <summary>The portal's accent colour as <c>#rrggbb</c>, or null for NexDesk's own.</summary>
    public string? BrandColor { get; private set; }

    /// <summary>Text the help centre greets customers with. Plain text; line breaks are kept.</summary>
    public string? PortalWelcomeText { get; private set; }

    public string? LogoFileKey { get; private set; }

    public string? LogoContentType { get; private set; }

    public long LogoSizeBytes { get; private set; }

    /// <summary>When the logo last changed, so its address can change with it and caches never show an old one.</summary>
    public DateTimeOffset? LogoUpdatedAt { get; private set; }

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

    public void SetTimezone(string timezone) => Timezone = timezone;

    public static bool IsValidBrandColor(string? color) => color is null || BrandColorPattern().IsMatch(color);

    public void UpdateSettings(string name, string timezone, string defaultLanguage, string? brandColor, string? welcomeText)
    {
        if (!IsValidBrandColor(brandColor) || !Languages.Contains(defaultLanguage))
        {
            throw new ArgumentException("The colour or language is not valid.");
        }

        Name = name.Trim();
        Timezone = timezone;
        DefaultLanguage = defaultLanguage;
        BrandColor = brandColor?.ToLowerInvariant();
        PortalWelcomeText = string.IsNullOrWhiteSpace(welcomeText) ? null : welcomeText.Trim();
    }

    /// <summary>Points the workspace at a newly stored logo and returns the key of the one it replaces, if any.</summary>
    public string? ReplaceLogo(string fileKey, string contentType, long sizeBytes, DateTimeOffset now)
    {
        var previous = LogoFileKey;
        LogoFileKey = fileKey;
        LogoContentType = contentType;
        LogoSizeBytes = sizeBytes;
        LogoUpdatedAt = now;
        return previous;
    }

    /// <summary>Removes the logo and returns the key of the stored file, if there was one.</summary>
    public string? RemoveLogo(DateTimeOffset now)
    {
        var previous = LogoFileKey;
        LogoFileKey = null;
        LogoContentType = null;
        LogoSizeBytes = 0;
        LogoUpdatedAt = previous is null ? LogoUpdatedAt : now;
        return previous;
    }

    public void CompleteOnboarding(DateTimeOffset now) => OnboardingCompletedAt ??= now;

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Reactivate() => Status = TenantStatus.Active;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex BrandColorPattern();
}
