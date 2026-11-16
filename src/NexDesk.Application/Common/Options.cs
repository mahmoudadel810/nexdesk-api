using System.ComponentModel.DataAnnotations;

namespace NexDesk.Application.Common;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public address of the web client, used to build links in emails.</summary>
    [Required]
    [Url]
    public string WebBaseUrl { get; set; } = "http://localhost:4200";
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 14;

    [Range(1, 168)]
    public int EmailVerificationHours { get; set; } = 48;

    [Range(5, 1440)]
    public int PasswordResetMinutes { get; set; } = 60;
}

public static class SupportedLanguages
{
    public const string Default = "en";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { "en", "ar" };

    public static string Normalize(string? language) =>
        language is not null && All.Contains(language) ? language : Default;
}
