using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexDesk.Application.KnowledgeBase;
using NexDesk.Domain.KnowledgeBase;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Persistence;
using NpgsqlTypes;

namespace NexDesk.Infrastructure.KnowledgeBase;

internal sealed class KbCategoryConfiguration : IEntityTypeConfiguration<KbCategory>
{
    public void Configure(EntityTypeBuilder<KbCategory> builder)
    {
        builder.ToTable("kb_categories");
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(KbCategory.NameMaxLength);
        builder.Property(c => c.Description).HasMaxLength(KbCategory.DescriptionMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.Position });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KbArticleConfiguration : IEntityTypeConfiguration<KbArticle>
{
    public void Configure(EntityTypeBuilder<KbArticle> builder)
    {
        builder.ToTable("kb_articles");
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Title).HasMaxLength(KbArticle.TitleMaxLength);
        builder.Property(a => a.Slug).HasMaxLength(KbArticle.SlugMaxLength);
        builder.Property(a => a.BodyMarkdown).HasMaxLength(KbArticle.BodyMaxLength);

        builder.HasIndex(a => new { a.TenantId, a.Slug }).IsUnique();
        builder.HasIndex(a => new { a.TenantId, a.CategoryId, a.Status });

        // The title outweighs the text, which the search uses to put the best match first.
        builder.Property<NpgsqlTsVector>(FullText.SearchVector)
            .HasComputedColumnSql(
                $"setweight(to_tsvector('{FullText.Config}', title), 'A') || "
                + $"setweight(to_tsvector('{FullText.Config}', body_markdown), 'B')",
                stored: true);
        builder.HasIndex(FullText.SearchVector).HasMethod("gin");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KbCategory>().WithMany().HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Full-text search over article titles and text.</summary>
internal sealed class KbSearch : IKbSearch
{
    public IQueryable<KbArticle> Matching(IQueryable<KbArticle> articles, string term)
    {
        var query = FullText.PrefixQuery(term);
        return query is null
            ? articles.Where(_ => false)
            : articles.Where(a => EF.Property<NpgsqlTsVector>(a, FullText.SearchVector)
                .Matches(EF.Functions.ToTsQuery(FullText.Config, query)));
    }
}

/// <summary>
/// Turns Markdown into HTML. Raw HTML in the source is shown as text rather than passed through, and
/// links may only point at web pages, email addresses or places on the same site.
/// </summary>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private const string BlockedUrl = "#";

    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    public string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var inline in document.Descendants<Inline>())
        {
            switch (inline)
            {
                case LinkInline link:
                    link.Url = IsSafe(link.Url) ? link.Url : BlockedUrl;
                    if (!link.IsImage)
                    {
                        link.GetAttributes().AddPropertyIfNotExist("rel", "noopener noreferrer nofollow");
                    }

                    break;
                case AutolinkInline autolink when !IsSafe(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url):
                    autolink.Url = BlockedUrl;
                    autolink.IsEmail = false;
                    break;
            }
        }

        return document.ToHtml(Pipeline);
    }

    private static bool IsSafe(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        // Browsers ignore whitespace and control characters inside a scheme, so they are ignored here too.
        var cleaned = new string(url.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        var colon = cleaned.IndexOf(':', StringComparison.Ordinal);
        var firstDelimiter = cleaned.IndexOfAny(['/', '?', '#']);
        var hasScheme = colon >= 0 && (firstDelimiter < 0 || colon < firstDelimiter);
        return !hasScheme || AllowedSchemes.Contains(cleaned[..colon], StringComparer.OrdinalIgnoreCase);
    }
}
