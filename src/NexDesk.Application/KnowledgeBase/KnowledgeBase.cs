using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.KnowledgeBase;

namespace NexDesk.Application.KnowledgeBase;

public sealed record KbCategoryDto(Guid Id, string Name, string? Description, int Position, int ArticleCount);

public sealed record KbArticleListItem(
    Guid Id,
    string Title,
    string Slug,
    Guid CategoryId,
    string CategoryName,
    KbArticleStatus Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt,
    int ViewCount,
    int HelpfulCount,
    int NotHelpfulCount);

public sealed record KbArticleDto(
    Guid Id,
    string Title,
    string Slug,
    Guid CategoryId,
    string CategoryName,
    KbArticleStatus Status,
    string BodyMarkdown,
    string BodyHtml,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt,
    int ViewCount,
    int HelpfulCount,
    int NotHelpfulCount);

public sealed record KbPreviewDto(string Html);

public static class KbErrors
{
    public static readonly Error CategoryNotFound = Error.NotFound("kb-category-not-found", "The category was not found.");

    public static readonly Error ArticleNotFound = Error.NotFound("kb-article-not-found", "The article was not found.");

    public static readonly Error CategoryNameTaken =
        Error.Conflict("kb-category-name-taken", "A category with this name already exists.");

    public static readonly Error CategoryNotEmpty =
        Error.Conflict("kb-category-not-empty", "Move or delete the articles in this category first.");

    public static readonly Error UnknownCategory =
        Error.Validation("kb-unknown-category", "The selected category does not exist.");
}

/// <summary>Renders article Markdown to HTML that is safe to show to anyone.</summary>
public interface IMarkdownRenderer
{
    string ToHtml(string markdown);
}

public interface IKbSearch
{
    /// <summary>Narrows articles to those whose title or text match the term.</summary>
    IQueryable<KbArticle> Matching(IQueryable<KbArticle> articles, string term);
}

internal static class KbQueries
{
    public static IQueryable<KbArticleListItem> ToListItems(this IQueryable<KbArticle> articles, IAppDbContext db) =>
        articles
            .Join(db.KbCategories, article => article.CategoryId, category => category.Id, (article, category) => new { article, category })
            .OrderBy(row => row.article.Title)
            .ThenBy(row => row.article.Id)
            .Select(row => new KbArticleListItem(
                row.article.Id,
                row.article.Title,
                row.article.Slug,
                row.category.Id,
                row.category.Name,
                row.article.Status,
                row.article.PublishedAt,
                row.article.UpdatedAt,
                row.article.ViewCount,
                row.article.HelpfulCount,
                row.article.NotHelpfulCount));

    public static async Task<KbArticleDto> ToDto(
        IAppDbContext db,
        IMarkdownRenderer markdown,
        KbArticle article,
        CancellationToken cancellationToken)
    {
        var categoryName = await db.KbCategories.AsNoTracking()
            .Where(c => c.Id == article.CategoryId)
            .Select(c => c.Name)
            .FirstAsync(cancellationToken);
        return new KbArticleDto(
            article.Id,
            article.Title,
            article.Slug,
            article.CategoryId,
            categoryName,
            article.Status,
            article.BodyMarkdown,
            markdown.ToHtml(article.BodyMarkdown),
            article.PublishedAt,
            article.UpdatedAt,
            article.ViewCount,
            article.HelpfulCount,
            article.NotHelpfulCount);
    }

    public static async Task<List<KbCategoryDto>> Categories(
        IAppDbContext db,
        bool publishedOnly,
        CancellationToken cancellationToken)
    {
        var counts = await db.KbArticles.AsNoTracking()
            .Where(a => !publishedOnly || a.Status == KbArticleStatus.Published)
            .GroupBy(a => a.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CategoryId, g => g.Count, cancellationToken);
        var categories = await db.KbCategories.AsNoTracking()
            .OrderBy(c => c.Position)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
        return categories
            .Select(c => new KbCategoryDto(c.Id, c.Name, c.Description, c.Position, counts.GetValueOrDefault(c.Id)))
            .Where(c => !publishedOnly || c.ArticleCount > 0)
            .ToList();
    }

    /// <summary>Returns the slug, or the slug with the smallest numeric suffix that is still free.</summary>
    public static async Task<string> FreeSlug(IAppDbContext db, string title, CancellationToken cancellationToken)
    {
        var baseSlug = KbArticle.Slugify(title);
        var taken = await db.KbArticles.AsNoTracking()
            .Where(a => a.Slug == baseSlug || a.Slug.StartsWith(baseSlug + "-"))
            .Select(a => a.Slug)
            .ToListAsync(cancellationToken);
        if (!taken.Contains(baseSlug))
        {
            return baseSlug;
        }

        var suffix = 2;
        while (taken.Contains($"{baseSlug}-{suffix}"))
        {
            suffix++;
        }

        return $"{baseSlug}-{suffix}";
    }
}

public sealed record ListKbCategoriesQuery : IQuery<IReadOnlyList<KbCategoryDto>>;

internal sealed class ListKbCategoriesHandler(IAppDbContext db)
    : IQueryHandler<ListKbCategoriesQuery, IReadOnlyList<KbCategoryDto>>
{
    public async Task<Result<IReadOnlyList<KbCategoryDto>>> Handle(ListKbCategoriesQuery query, CancellationToken cancellationToken) =>
        await KbQueries.Categories(db, publishedOnly: false, cancellationToken);
}

public sealed record SaveKbCategoryCommand(Guid? CategoryId, string Name, string? Description, int Position)
    : ICommand<KbCategoryDto>;

internal sealed class SaveKbCategoryValidator : AbstractValidator<SaveKbCategoryCommand>
{
    public SaveKbCategoryValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(KbCategory.NameMaxLength);
        RuleFor(c => c.Description).MaximumLength(KbCategory.DescriptionMaxLength);
        RuleFor(c => c.Position).InclusiveBetween(0, 1000);
    }
}

internal sealed class SaveKbCategoryHandler(IAppDbContext db) : ICommandHandler<SaveKbCategoryCommand, KbCategoryDto>
{
    public async Task<Result<KbCategoryDto>> Handle(SaveKbCategoryCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim().ToLowerInvariant();
        if (await db.KbCategories.AnyAsync(c => c.Name.ToLower() == name && c.Id != command.CategoryId, cancellationToken))
        {
            return KbErrors.CategoryNameTaken;
        }

        KbCategory? category;
        if (command.CategoryId is { } id)
        {
            category = await db.KbCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (category is null)
            {
                return KbErrors.CategoryNotFound;
            }

            category.Update(command.Name, command.Description, command.Position);
        }
        else
        {
            category = KbCategory.Create(command.Name, command.Description, command.Position);
            db.KbCategories.Add(category);
        }

        await db.SaveChangesAsync(cancellationToken);
        var articles = await db.KbArticles.CountAsync(a => a.CategoryId == category.Id, cancellationToken);
        return new KbCategoryDto(category.Id, category.Name, category.Description, category.Position, articles);
    }
}

public sealed record DeleteKbCategoryCommand(Guid CategoryId) : ICommand<Success>;

internal sealed class DeleteKbCategoryHandler(IAppDbContext db) : ICommandHandler<DeleteKbCategoryCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteKbCategoryCommand command, CancellationToken cancellationToken)
    {
        var category = await db.KbCategories.FirstOrDefaultAsync(c => c.Id == command.CategoryId, cancellationToken);
        if (category is null)
        {
            return KbErrors.CategoryNotFound;
        }

        if (await db.KbArticles.AnyAsync(a => a.CategoryId == category.Id, cancellationToken))
        {
            return KbErrors.CategoryNotEmpty;
        }

        db.KbCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record ListKbArticlesQuery(
    Guid? CategoryId,
    KbArticleStatus? Status,
    string? Search,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize) : IQuery<PagedResult<KbArticleListItem>>, IPagedRequest;

internal sealed class ListKbArticlesValidator : AbstractValidator<ListKbArticlesQuery>
{
    public ListKbArticlesValidator()
    {
        this.AddPagingRules();
        RuleFor(q => q.Search).MaximumLength(200);
    }
}

internal sealed class ListKbArticlesHandler(IAppDbContext db, IKbSearch search)
    : IQueryHandler<ListKbArticlesQuery, PagedResult<KbArticleListItem>>
{
    public async Task<Result<PagedResult<KbArticleListItem>>> Handle(
        ListKbArticlesQuery query,
        CancellationToken cancellationToken)
    {
        var articles = db.KbArticles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            articles = search.Matching(articles, query.Search);
        }

        if (query.CategoryId is { } categoryId)
        {
            articles = articles.Where(a => a.CategoryId == categoryId);
        }

        if (query.Status is { } status)
        {
            articles = articles.Where(a => a.Status == status);
        }

        return await articles.ToListItems(db).ToPagedAsync(query, cancellationToken);
    }
}

public sealed record GetKbArticleQuery(Guid ArticleId) : IQuery<KbArticleDto>;

internal sealed class GetKbArticleHandler(IAppDbContext db, IMarkdownRenderer markdown)
    : IQueryHandler<GetKbArticleQuery, KbArticleDto>
{
    public async Task<Result<KbArticleDto>> Handle(GetKbArticleQuery query, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == query.ArticleId, cancellationToken);
        return article is null ? KbErrors.ArticleNotFound : await KbQueries.ToDto(db, markdown, article, cancellationToken);
    }
}

public sealed record SaveKbArticleCommand(Guid? ArticleId, Guid CategoryId, string Title, string BodyMarkdown, bool? Publish)
    : ICommand<KbArticleDto>;

internal sealed class SaveKbArticleValidator : AbstractValidator<SaveKbArticleCommand>
{
    public SaveKbArticleValidator()
    {
        RuleFor(c => c.Title).NotEmpty().MaximumLength(KbArticle.TitleMaxLength);
        RuleFor(c => c.BodyMarkdown).NotEmpty().MaximumLength(KbArticle.BodyMaxLength);
    }
}

internal sealed class SaveKbArticleHandler(IAppDbContext db, IMarkdownRenderer markdown, TimeProvider clock)
    : ICommandHandler<SaveKbArticleCommand, KbArticleDto>
{
    public async Task<Result<KbArticleDto>> Handle(SaveKbArticleCommand command, CancellationToken cancellationToken)
    {
        if (!await db.KbCategories.AnyAsync(c => c.Id == command.CategoryId, cancellationToken))
        {
            return KbErrors.UnknownCategory;
        }

        KbArticle? article;
        if (command.ArticleId is { } id)
        {
            article = await db.KbArticles.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (article is null)
            {
                return KbErrors.ArticleNotFound;
            }

            // The slug stays as it was, so links to a published article keep working after a retitle.
            article.Update(command.CategoryId, command.Title, command.BodyMarkdown);
        }
        else
        {
            article = KbArticle.Create(
                command.CategoryId,
                command.Title,
                await KbQueries.FreeSlug(db, command.Title, cancellationToken),
                command.BodyMarkdown);
            db.KbArticles.Add(article);
        }

        if (command.Publish == true)
        {
            article.Publish(clock.GetUtcNow());
        }
        else if (command.Publish == false)
        {
            article.Unpublish();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await KbQueries.ToDto(db, markdown, article, cancellationToken);
    }
}

public sealed record SetKbArticlePublishedCommand(Guid ArticleId, bool Published) : ICommand<KbArticleDto>;

internal sealed class SetKbArticlePublishedHandler(IAppDbContext db, IMarkdownRenderer markdown, TimeProvider clock)
    : ICommandHandler<SetKbArticlePublishedCommand, KbArticleDto>
{
    public async Task<Result<KbArticleDto>> Handle(SetKbArticlePublishedCommand command, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.FirstOrDefaultAsync(a => a.Id == command.ArticleId, cancellationToken);
        if (article is null)
        {
            return KbErrors.ArticleNotFound;
        }

        if (command.Published)
        {
            article.Publish(clock.GetUtcNow());
        }
        else
        {
            article.Unpublish();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await KbQueries.ToDto(db, markdown, article, cancellationToken);
    }
}

public sealed record DeleteKbArticleCommand(Guid ArticleId) : ICommand<Success>;

internal sealed class DeleteKbArticleHandler(IAppDbContext db) : ICommandHandler<DeleteKbArticleCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteKbArticleCommand command, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.FirstOrDefaultAsync(a => a.Id == command.ArticleId, cancellationToken);
        if (article is null)
        {
            return KbErrors.ArticleNotFound;
        }

        db.KbArticles.Remove(article);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record PreviewMarkdownQuery(string Markdown) : IQuery<KbPreviewDto>;

internal sealed class PreviewMarkdownValidator : AbstractValidator<PreviewMarkdownQuery>
{
    public PreviewMarkdownValidator() => RuleFor(q => q.Markdown).NotNull().MaximumLength(KbArticle.BodyMaxLength);
}

internal sealed class PreviewMarkdownHandler(IMarkdownRenderer markdown) : IQueryHandler<PreviewMarkdownQuery, KbPreviewDto>
{
    public Task<Result<KbPreviewDto>> Handle(PreviewMarkdownQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<KbPreviewDto>>(new KbPreviewDto(markdown.ToHtml(query.Markdown)));
}

// ---- What the public help centre can see: published articles only. ----

public sealed record PublicKbArticleListItem(string Title, string Slug, string CategoryName, string Excerpt);

public sealed record PublicKbArticleDto(
    Guid Id,
    string Title,
    string Slug,
    Guid CategoryId,
    string CategoryName,
    string BodyHtml,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt);

public sealed record ListPublicKbCategoriesQuery : IQuery<IReadOnlyList<KbCategoryDto>>;

internal sealed class ListPublicKbCategoriesHandler(IAppDbContext db)
    : IQueryHandler<ListPublicKbCategoriesQuery, IReadOnlyList<KbCategoryDto>>
{
    public async Task<Result<IReadOnlyList<KbCategoryDto>>> Handle(
        ListPublicKbCategoriesQuery query,
        CancellationToken cancellationToken) =>
        await KbQueries.Categories(db, publishedOnly: true, cancellationToken);
}

public sealed record ListPublicKbArticlesQuery(Guid? CategoryId, string? Search, int Limit = 50)
    : IQuery<IReadOnlyList<PublicKbArticleListItem>>;

internal sealed class ListPublicKbArticlesValidator : AbstractValidator<ListPublicKbArticlesQuery>
{
    public ListPublicKbArticlesValidator()
    {
        RuleFor(q => q.Search).MaximumLength(200);
        RuleFor(q => q.Limit).InclusiveBetween(1, 100);
    }
}

internal sealed class ListPublicKbArticlesHandler(IAppDbContext db, IKbSearch search)
    : IQueryHandler<ListPublicKbArticlesQuery, IReadOnlyList<PublicKbArticleListItem>>
{
    private const int ExcerptLength = 180;

    public async Task<Result<IReadOnlyList<PublicKbArticleListItem>>> Handle(
        ListPublicKbArticlesQuery query,
        CancellationToken cancellationToken)
    {
        var articles = db.KbArticles.AsNoTracking().Where(a => a.Status == KbArticleStatus.Published);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            articles = search.Matching(articles, query.Search);
        }

        if (query.CategoryId is { } categoryId)
        {
            articles = articles.Where(a => a.CategoryId == categoryId);
        }

        var rows = await articles
            .Join(db.KbCategories, a => a.CategoryId, c => c.Id, (a, c) => new { a.Title, a.Slug, Category = c.Name, a.BodyMarkdown })
            .OrderBy(row => row.Title)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        return rows
            .Select(r => new PublicKbArticleListItem(r.Title, r.Slug, r.Category, Excerpt(r.BodyMarkdown)))
            .ToList();
    }

    /// <summary>The start of the article as plain text, without Markdown punctuation.</summary>
    private static string Excerpt(string markdown)
    {
        var text = string.Join(' ', markdown
            .Split('\n')
            .Select(line => line.Trim().TrimStart('#', '>', '-', '*', ' '))
            .Where(line => line.Length > 0))
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
        return text.Length <= ExcerptLength ? text : text[..ExcerptLength].TrimEnd() + "…";
    }
}

public sealed record GetPublicKbArticleQuery(string Slug) : IQuery<PublicKbArticleDto>;

internal sealed class GetPublicKbArticleHandler(IAppDbContext db, IMarkdownRenderer markdown)
    : IQueryHandler<GetPublicKbArticleQuery, PublicKbArticleDto>
{
    public async Task<Result<PublicKbArticleDto>> Handle(GetPublicKbArticleQuery query, CancellationToken cancellationToken)
    {
        // Drafts answer exactly like articles that do not exist.
        var article = await db.KbArticles.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Slug == query.Slug && a.Status == KbArticleStatus.Published, cancellationToken);
        if (article is null)
        {
            return KbErrors.ArticleNotFound;
        }

        await db.KbArticles.Where(a => a.Id == article.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(a => a.ViewCount, a => a.ViewCount + 1), cancellationToken);
        var dto = await KbQueries.ToDto(db, markdown, article, cancellationToken);
        return new PublicKbArticleDto(
            dto.Id, dto.Title, dto.Slug, dto.CategoryId, dto.CategoryName, dto.BodyHtml, dto.PublishedAt, dto.UpdatedAt);
    }
}

public sealed record SubmitKbFeedbackCommand(string Slug, bool Helpful) : ICommand<Success>;

internal sealed class SubmitKbFeedbackHandler(IAppDbContext db) : ICommandHandler<SubmitKbFeedbackCommand, Success>
{
    public async Task<Result<Success>> Handle(SubmitKbFeedbackCommand command, CancellationToken cancellationToken)
    {
        var published = db.KbArticles.Where(a => a.Slug == command.Slug && a.Status == KbArticleStatus.Published);
        var updated = command.Helpful
            ? await published.ExecuteUpdateAsync(
                update => update.SetProperty(a => a.HelpfulCount, a => a.HelpfulCount + 1), cancellationToken)
            : await published.ExecuteUpdateAsync(
                update => update.SetProperty(a => a.NotHelpfulCount, a => a.NotHelpfulCount + 1), cancellationToken);
        return updated == 0 ? KbErrors.ArticleNotFound : default(Success);
    }
}
