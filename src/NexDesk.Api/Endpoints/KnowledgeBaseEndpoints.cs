using NexDesk.Api.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.KnowledgeBase;
using NexDesk.Domain.Identity;
using NexDesk.Domain.KnowledgeBase;

namespace NexDesk.Api.Endpoints;

public sealed record SaveKbCategoryRequest(string Name, string? Description = null, int Position = 0);

public sealed record SaveKbArticleRequest(Guid CategoryId, string Title, string BodyMarkdown, bool? Publish = null);

public sealed record PreviewMarkdownRequest(string Markdown);

public sealed record KbFeedbackRequest(bool Helpful);

public sealed record ListKbArticlesRequest(
    Guid? CategoryId = null,
    KbArticleStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

public sealed record ListPublicKbArticlesRequest(Guid? CategoryId = null, string? Search = null, int Limit = 50);

public static class KnowledgeBaseEndpoints
{
    public static RouteGroupBuilder MapKnowledgeBaseEndpoints(this RouteGroupBuilder api)
    {
        var kb = api.MapGroup("/kb").WithTags("Knowledge base");

        kb.MapGet("/categories", async (
            IQueryHandler<ListKbCategoriesQuery, IReadOnlyList<KbCategoryDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListKbCategoriesQuery(), ct)).ToOk())
            .RequirePermission(Permissions.KnowledgeBaseView)
            .WithName("ListKbCategories")
            .WithSummary("Lists the article categories with the number of articles in each, drafts included.")
            .Produces<IReadOnlyList<KbCategoryDto>>();

        kb.MapPost("/categories", async (
            SaveKbCategoryRequest request,
            ICommandHandler<SaveKbCategoryCommand, KbCategoryDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SaveKbCategoryCommand(null, request.Name, request.Description, request.Position), ct))
            .ToHttp(category => TypedResults.Created((string?)null, category)))
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("CreateKbCategory")
            .WithSummary("Adds a category.")
            .Produces<KbCategoryDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        kb.MapPut("/categories/{id:guid}", async (
            Guid id,
            SaveKbCategoryRequest request,
            ICommandHandler<SaveKbCategoryCommand, KbCategoryDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SaveKbCategoryCommand(id, request.Name, request.Description, request.Position), ct)).ToOk())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("UpdateKbCategory")
            .WithSummary("Renames, describes or reorders a category.")
            .Produces<KbCategoryDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        kb.MapDelete("/categories/{id:guid}", async (
            Guid id,
            ICommandHandler<DeleteKbCategoryCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeleteKbCategoryCommand(id), ct)).ToNoContent())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("DeleteKbCategory")
            .WithSummary("Removes a category that has no articles left.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        kb.MapGet("/articles", async (
            [AsParameters] ListKbArticlesRequest request,
            IQueryHandler<ListKbArticlesQuery, PagedResult<KbArticleListItem>> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new ListKbArticlesQuery(request.CategoryId, request.Status, request.Search, request.Page, request.PageSize), ct))
            .ToOk())
            .RequirePermission(Permissions.KnowledgeBaseView)
            .WithName("ListKbArticles")
            .WithSummary("Lists articles by title, optionally narrowed by category, status or a search term.")
            .Produces<PagedResult<KbArticleListItem>>()
            .ProducesValidationProblem();

        kb.MapGet("/articles/{id:guid}", async (
            Guid id,
            IQueryHandler<GetKbArticleQuery, KbArticleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetKbArticleQuery(id), ct)).ToOk())
            .RequirePermission(Permissions.KnowledgeBaseView)
            .WithName("GetKbArticle")
            .WithSummary("Returns an article with its Markdown source and the HTML it renders to.")
            .Produces<KbArticleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapPost("/articles", async (
            SaveKbArticleRequest request,
            ICommandHandler<SaveKbArticleCommand, KbArticleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new SaveKbArticleCommand(null, request.CategoryId, request.Title, request.BodyMarkdown, request.Publish), ct))
            .ToHttp(article => TypedResults.Created($"/api/v1/kb/articles/{article.Id}", article)))
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("CreateKbArticle")
            .WithSummary("Writes an article. It stays a draft unless `publish` is true.")
            .Produces<KbArticleDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        kb.MapPut("/articles/{id:guid}", async (
            Guid id,
            SaveKbArticleRequest request,
            ICommandHandler<SaveKbArticleCommand, KbArticleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new SaveKbArticleCommand(id, request.CategoryId, request.Title, request.BodyMarkdown, request.Publish), ct))
            .ToOk())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("UpdateKbArticle")
            .WithSummary("Edits an article. Its address in the help centre does not change.")
            .Produces<KbArticleDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapPost("/articles/{id:guid}/publish", async (
            Guid id,
            ICommandHandler<SetKbArticlePublishedCommand, KbArticleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetKbArticlePublishedCommand(id, true), ct)).ToOk())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("PublishKbArticle")
            .WithSummary("Makes an article visible in the help centre.")
            .Produces<KbArticleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapPost("/articles/{id:guid}/unpublish", async (
            Guid id,
            ICommandHandler<SetKbArticlePublishedCommand, KbArticleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetKbArticlePublishedCommand(id, false), ct)).ToOk())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("UnpublishKbArticle")
            .WithSummary("Turns an article back into a draft, hiding it from the help centre.")
            .Produces<KbArticleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapDelete("/articles/{id:guid}", async (
            Guid id,
            ICommandHandler<DeleteKbArticleCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeleteKbArticleCommand(id), ct)).ToNoContent())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("DeleteKbArticle")
            .WithSummary("Deletes an article.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapPost("/preview", async (
            PreviewMarkdownRequest request,
            IQueryHandler<PreviewMarkdownQuery, KbPreviewDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new PreviewMarkdownQuery(request.Markdown), ct)).ToOk())
            .RequirePermission(Permissions.KnowledgeBaseManage)
            .WithName("PreviewMarkdown")
            .WithSummary("Renders Markdown exactly as the help centre will show it.")
            .Produces<KbPreviewDto>()
            .ProducesValidationProblem();

        return api;
    }

    /// <summary>The public, read-only side of the knowledge base. No sign-in is needed.</summary>
    public static void MapPortalKnowledgeBase(this RouteGroupBuilder kb)
    {
        kb.MapGet("/categories", async (
            IQueryHandler<ListPublicKbCategoriesQuery, IReadOnlyList<KbCategoryDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListPublicKbCategoriesQuery(), ct)).ToOk())
            .WithName("ListPortalKbCategories")
            .WithSummary("Lists the categories that have at least one published article.")
            .Produces<IReadOnlyList<KbCategoryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapGet("/articles", async (
            [AsParameters] ListPublicKbArticlesRequest request,
            IQueryHandler<ListPublicKbArticlesQuery, IReadOnlyList<PublicKbArticleListItem>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListPublicKbArticlesQuery(request.CategoryId, request.Search, request.Limit), ct)).ToOk())
            .WithName("ListPortalKbArticles")
            .WithSummary("Lists or searches published articles. The last search word matches as a prefix.")
            .Produces<IReadOnlyList<PublicKbArticleListItem>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapGet("/articles/{articleSlug}", async (
            string articleSlug,
            IQueryHandler<GetPublicKbArticleQuery, PublicKbArticleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetPublicKbArticleQuery(articleSlug), ct)).ToOk())
            .WithName("GetPortalKbArticle")
            .WithSummary("Returns a published article as HTML and counts the view. Drafts are not found.")
            .Produces<PublicKbArticleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        kb.MapPost("/articles/{articleSlug}/feedback", async (
            string articleSlug,
            KbFeedbackRequest request,
            ICommandHandler<SubmitKbFeedbackCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SubmitKbFeedbackCommand(articleSlug, request.Helpful), ct)).ToNoContent())
            .WithName("SubmitPortalKbFeedback")
            .WithSummary("Records whether a reader found the article helpful.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
