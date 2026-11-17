using System.Text;
using NexDesk.Domain.Common;

namespace NexDesk.Domain.KnowledgeBase;

public sealed class KbCategory : AuditableEntity, ITenantOwned
{
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 300;

    private KbCategory()
    {
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public int Position { get; private set; }

    public static KbCategory Create(string name, string? description, int position) => new()
    {
        Name = name.Trim(),
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        Position = position,
    };

    public void Update(string name, string? description, int position)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Position = position;
    }
}

public enum KbArticleStatus
{
    Draft,
    Published,
}

/// <summary>A help article written in Markdown. Only published articles are visible in the help centre.</summary>
public sealed class KbArticle : AuditableEntity, ITenantOwned
{
    public const int TitleMaxLength = 160;
    public const int SlugMaxLength = 180;
    public const int BodyMaxLength = 100_000;

    private KbArticle()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Title { get; private set; } = null!;

    /// <summary>The address of the article in the help centre. Unique per workspace.</summary>
    public string Slug { get; private set; } = null!;

    public string BodyMarkdown { get; private set; } = null!;

    public KbArticleStatus Status { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int ViewCount { get; private set; }

    public int HelpfulCount { get; private set; }

    public int NotHelpfulCount { get; private set; }

    public static KbArticle Create(Guid categoryId, string title, string slug, string bodyMarkdown) => new()
    {
        CategoryId = categoryId,
        Title = title.Trim(),
        Slug = slug,
        BodyMarkdown = bodyMarkdown,
        Status = KbArticleStatus.Draft,
    };

    /// <summary>
    /// Builds a URL-friendly slug from a title. Letters of any script are kept, so an Arabic title gets
    /// an Arabic slug instead of an empty one.
    /// </summary>
    public static string Slugify(string title)
    {
        var builder = new StringBuilder();
        foreach (var character in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        slug = slug.Length > SlugMaxLength - 8 ? slug[..(SlugMaxLength - 8)].Trim('-') : slug;
        return slug.Length == 0 ? "article" : slug;
    }

    public void Update(Guid categoryId, string title, string bodyMarkdown)
    {
        CategoryId = categoryId;
        Title = title.Trim();
        BodyMarkdown = bodyMarkdown;
    }

    public void Publish(DateTimeOffset now)
    {
        Status = KbArticleStatus.Published;
        PublishedAt ??= now;
    }

    public void Unpublish() => Status = KbArticleStatus.Draft;
}
