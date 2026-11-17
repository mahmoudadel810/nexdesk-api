using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Common;
using NexDesk.Application.KnowledgeBase;
using NexDesk.Domain.Identity;
using NexDesk.Domain.KnowledgeBase;
using NexDesk.IntegrationTests.Infrastructure;
using NexDesk.IntegrationTests.Portal;

namespace NexDesk.IntegrationTests.KnowledgeBase;

public static class KbTestHelpers
{
    public static async Task<KbCategoryDto> CreateKbCategoryAsync(this TestWorkspace workspace, string name = "Billing") =>
        await (await workspace.Client.PostAsJsonAsync("/api/v1/kb/categories", new SaveKbCategoryRequest(name)))
            .ReadAsync<KbCategoryDto>();

    public static async Task<KbArticleDto> CreateKbArticleAsync(
        this TestWorkspace workspace,
        Guid categoryId,
        string title = "How to download an invoice",
        string body = "Open **Billing** and choose *Download PDF*.",
        bool publish = true)
    {
        var response = await workspace.Client.PostAsJsonAsync(
            "/api/v1/kb/articles",
            new SaveKbArticleRequest(categoryId, title, body, publish));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadAsync<KbArticleDto>();
    }
}

[Collection(ApiCollection.Name)]
public sealed class KnowledgeBaseTests(ApiFactory factory)
{
    [Fact]
    public async Task Admins_write_articles_that_stay_drafts_until_published()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var category = await workspace.CreateKbCategoryAsync();

        var draft = await workspace.CreateKbArticleAsync(category.Id, publish: false);
        draft.Status.ShouldBe(KbArticleStatus.Draft);
        draft.PublishedAt.ShouldBeNull();
        draft.Slug.ShouldBe("how-to-download-an-invoice");
        draft.CategoryName.ShouldBe("Billing");
        draft.BodyHtml.ShouldContain("<strong>Billing</strong>");

        var published = await (await workspace.Client.PostAsync($"/api/v1/kb/articles/{draft.Id}/publish", null))
            .ReadAsync<KbArticleDto>();
        published.Status.ShouldBe(KbArticleStatus.Published);
        published.PublishedAt.ShouldNotBeNull();

        var edited = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/kb/articles/{draft.Id}",
            new SaveKbArticleRequest(category.Id, "Downloading invoices", "New text."))).ReadAsync<KbArticleDto>();
        edited.Title.ShouldBe("Downloading invoices");
        edited.Slug.ShouldBe(draft.Slug);
        edited.Status.ShouldBe(KbArticleStatus.Published);

        var unpublished = await (await workspace.Client.PostAsync($"/api/v1/kb/articles/{draft.Id}/unpublish", null))
            .ReadAsync<KbArticleDto>();
        unpublished.Status.ShouldBe(KbArticleStatus.Draft);
        // The database keeps microseconds, so the first publication date is compared with a little slack.
        unpublished.PublishedAt!.Value.ShouldBe(published.PublishedAt.Value, TimeSpan.FromMilliseconds(1));

        var list = await workspace.Client.GetAsync<PagedResult<KbArticleListItem>>("/api/v1/kb/articles?status=Draft");
        list.Items.ShouldHaveSingleItem().Id.ShouldBe(draft.Id);
        (await workspace.Client.GetAsync<PagedResult<KbArticleListItem>>("/api/v1/kb/articles?status=Published"))
            .TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Articles_with_the_same_title_get_distinct_addresses_and_deleted_ones_free_theirs()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var category = await workspace.CreateKbCategoryAsync();

        var first = await workspace.CreateKbArticleAsync(category.Id);
        var second = await workspace.CreateKbArticleAsync(category.Id);
        var third = await workspace.CreateKbArticleAsync(category.Id);
        new[] { first.Slug, second.Slug, third.Slug }.ShouldBe(
            ["how-to-download-an-invoice", "how-to-download-an-invoice-2", "how-to-download-an-invoice-3"]);

        (await workspace.Client.DeleteAsync($"/api/v1/kb/articles/{first.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync($"/api/v1/kb/articles/{first.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await workspace.CreateKbArticleAsync(category.Id)).Slug.ShouldBe("how-to-download-an-invoice");

        var arabic = await workspace.CreateKbArticleAsync(category.Id, "كيفية تنزيل الفاتورة", "افتح صفحة **الفواتير**.");
        arabic.Slug.ShouldBe("كيفية-تنزيل-الفاتورة");
        var read = await factory.CreateBareClient().GetAsync<PublicKbArticleDto>(
            workspace.PortalPath($"/kb/articles/{Uri.EscapeDataString(arabic.Slug)}"));
        read.Title.ShouldBe("كيفية تنزيل الفاتورة");
    }

    [Fact]
    public async Task Categories_must_be_unique_and_empty_before_they_can_be_removed()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var billing = await workspace.CreateKbCategoryAsync();
        var duplicate = await workspace.Client.PostAsJsonAsync("/api/v1/kb/categories", new SaveKbCategoryRequest(" billing "));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.ProblemCodeAsync()).ShouldBe("kb-category-name-taken");

        var renamed = await (await workspace.Client.PutAsJsonAsync(
            $"/api/v1/kb/categories/{billing.Id}",
            new SaveKbCategoryRequest("Payments", "Invoices and refunds.", 3))).ReadAsync<KbCategoryDto>();
        renamed.ShouldBe(new KbCategoryDto(billing.Id, "Payments", "Invoices and refunds.", 3, 0));

        var article = await workspace.CreateKbArticleAsync(billing.Id);
        var blocked = await workspace.Client.DeleteAsync($"/api/v1/kb/categories/{billing.Id}");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await blocked.ProblemCodeAsync()).ShouldBe("kb-category-not-empty");

        (await workspace.Client.DeleteAsync($"/api/v1/kb/articles/{article.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.DeleteAsync($"/api/v1/kb/categories/{billing.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync<List<KbCategoryDto>>("/api/v1/kb/categories")).ShouldBeEmpty();

        var orphan = await workspace.Client.PostAsJsonAsync(
            "/api/v1/kb/articles",
            new SaveKbArticleRequest(billing.Id, "Orphan", "Text"));
        orphan.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await orphan.ProblemCodeAsync()).ShouldBe("kb-unknown-category");
    }

    [Fact]
    public async Task Agents_can_read_the_knowledge_base_but_only_admins_can_change_it()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var category = await workspace.CreateKbCategoryAsync();
        var article = await workspace.CreateKbArticleAsync(category.Id, publish: false);
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);

        (await agent.Client.GetAsync<KbArticleDto>($"/api/v1/kb/articles/{article.Id}")).Title.ShouldBe(article.Title);
        (await agent.Client.GetAsync<List<KbCategoryDto>>("/api/v1/kb/categories")).ShouldHaveSingleItem().ArticleCount.ShouldBe(1);

        var request = new SaveKbArticleRequest(category.Id, "Nope", "Text");
        (await agent.Client.PostAsJsonAsync("/api/v1/kb/articles", request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PutAsJsonAsync($"/api/v1/kb/articles/{article.Id}", request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PostAsync($"/api/v1/kb/articles/{article.Id}/publish", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.DeleteAsync($"/api/v1/kb/articles/{article.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PostAsJsonAsync("/api/v1/kb/categories", new SaveKbCategoryRequest("Mine")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PostAsJsonAsync("/api/v1/kb/preview", new PreviewMarkdownRequest("x")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.CreateBareClient().GetAsync("/api/v1/kb/articles")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_help_centre_shows_published_articles_to_anyone_and_hides_drafts()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var billing = await workspace.CreateKbCategoryAsync();
        var empty = await workspace.CreateKbCategoryAsync("Internal notes");
        var published = await workspace.CreateKbArticleAsync(billing.Id);
        var draft = await workspace.CreateKbArticleAsync(empty.Id, "Secret roadmap", "Not ready.", publish: false);
        var visitor = factory.CreateBareClient();

        var categories = await visitor.GetAsync<List<KbCategoryDto>>(workspace.PortalPath("/kb/categories"));
        categories.ShouldHaveSingleItem().ShouldBe(new KbCategoryDto(billing.Id, "Billing", null, 0, 1));

        var articles = await visitor.GetAsync<List<PublicKbArticleListItem>>(workspace.PortalPath("/kb/articles"));
        var item = articles.ShouldHaveSingleItem();
        item.Slug.ShouldBe(published.Slug);
        item.Excerpt.ShouldBe("Open Billing and choose Download PDF.");

        var article = await visitor.GetAsync<PublicKbArticleDto>(workspace.PortalPath($"/kb/articles/{published.Slug}"));
        article.BodyHtml.ShouldContain("<strong>Billing</strong>");

        var hidden = await visitor.GetAsync(workspace.PortalPath($"/kb/articles/{draft.Slug}"));
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await hidden.ProblemCodeAsync()).ShouldBe("kb-article-not-found");
        (await visitor.PostAsJsonAsync(workspace.PortalPath($"/kb/articles/{draft.Slug}/feedback"), new KbFeedbackRequest(true)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await visitor.GetAsync<List<PublicKbArticleListItem>>(workspace.PortalPath("/kb/articles?search=roadmap"))).ShouldBeEmpty();

        // Unpublishing takes an article out of the help centre again.
        await workspace.Client.PostAsync($"/api/v1/kb/articles/{published.Id}/unpublish", null);
        (await visitor.GetAsync(workspace.PortalPath($"/kb/articles/{published.Slug}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await visitor.GetAsync(workspace.PortalPath("/kb/articles").Replace(workspace.Slug, "no-such-workspace", StringComparison.Ordinal)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Readers_search_articles_while_typing_and_leave_feedback()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var category = await workspace.CreateKbCategoryAsync();
        var invoice = await workspace.CreateKbArticleAsync(category.Id);
        await workspace.CreateKbArticleAsync(category.Id, "Reset your password", "Use the forgot password link on the sign-in page.");
        await workspace.CreateKbArticleAsync(category.Id, "استرجاع المبلغ", "يمكنك طلب استرجاع المبلغ خلال ثلاثين يوما.");
        var visitor = factory.CreateBareClient();

        async Task<List<string>> Search(string term) =>
            (await visitor.GetAsync<List<PublicKbArticleListItem>>(
                workspace.PortalPath($"/kb/articles?search={Uri.EscapeDataString(term)}"))).Select(a => a.Title).ToList();

        (await Search("invo")).ShouldBe(["How to download an invoice"]);
        (await Search("download pdf")).ShouldBe(["How to download an invoice"]);
        (await Search("forgot pass")).ShouldBe(["Reset your password"]);
        (await Search("استرجاع")).ShouldBe(["استرجاع المبلغ"]);
        (await Search("nothing-like-this")).ShouldBeEmpty();

        // A match in the title outranks one in the text, whatever the alphabetical order.
        await workspace.CreateKbArticleAsync(category.Id, "Account security", "Choose a long password and keep it private.");
        (await Search("password")).ShouldBe(["Reset your password", "Account security"]);
        (await Search("' | !! & (")).ShouldBeEmpty();

        var staff = await workspace.Client.GetAsync<PagedResult<KbArticleListItem>>("/api/v1/kb/articles?search=password");
        staff.Items.Select(a => a.Title).ShouldBe(["Reset your password", "Account security"]);

        var path = workspace.PortalPath($"/kb/articles/{invoice.Slug}");
        await visitor.GetAsync<PublicKbArticleDto>(path);
        await visitor.GetAsync<PublicKbArticleDto>(path);
        (await visitor.PostAsJsonAsync(path + "/feedback", new KbFeedbackRequest(true))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await visitor.PostAsJsonAsync(path + "/feedback", new KbFeedbackRequest(true))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await visitor.PostAsJsonAsync(path + "/feedback", new KbFeedbackRequest(false))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var counted = await workspace.Client.GetAsync<KbArticleDto>($"/api/v1/kb/articles/{invoice.Id}");
        (counted.ViewCount, counted.HelpfulCount, counted.NotHelpfulCount).ShouldBe((2, 2, 1));
    }

    [Fact]
    public async Task Scripts_and_unsafe_links_in_an_article_never_reach_the_reader()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var category = await workspace.CreateKbCategoryAsync();
        const string Hostile = "<script>alert(1)</script>\n\n[click](javascript:alert(1))\n\n<img src=x onerror=alert(1)>";
        var article = await workspace.CreateKbArticleAsync(category.Id, "Hostile", Hostile);

        var preview = await (await workspace.Client.PostAsJsonAsync("/api/v1/kb/preview", new PreviewMarkdownRequest(Hostile)))
            .ReadAsync<KbPreviewDto>();
        var read = await factory.CreateBareClient().GetAsync<PublicKbArticleDto>(
            workspace.PortalPath($"/kb/articles/{article.Slug}"));

        foreach (var html in new[] { article.BodyHtml, preview.Html, read.BodyHtml })
        {
            html.ShouldNotContain("<script");
            html.ShouldNotContain("<img");
            html.ShouldNotContain("javascript:");
            html.ShouldContain("&lt;script&gt;");
        }
    }

    [Fact]
    public async Task One_workspace_never_sees_or_changes_another_workspaces_articles()
    {
        var acme = await factory.CreateWorkspaceAsync();
        var globex = await factory.CreateWorkspaceAsync();
        var category = await acme.CreateKbCategoryAsync();
        var article = await acme.CreateKbArticleAsync(category.Id);

        (await globex.Client.GetAsync<List<KbCategoryDto>>("/api/v1/kb/categories")).ShouldBeEmpty();
        (await globex.Client.GetAsync<PagedResult<KbArticleListItem>>("/api/v1/kb/articles")).TotalCount.ShouldBe(0);
        (await globex.Client.GetAsync<PagedResult<KbArticleListItem>>("/api/v1/kb/articles?search=invoice")).TotalCount.ShouldBe(0);
        (await globex.Client.GetAsync($"/api/v1/kb/articles/{article.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var request = new SaveKbArticleRequest(category.Id, "Hijacked", "Text", true);
        (await globex.Client.PutAsJsonAsync($"/api/v1/kb/articles/{article.Id}", request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await globex.Client.PostAsJsonAsync("/api/v1/kb/articles", request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await globex.Client.PostAsync($"/api/v1/kb/articles/{article.Id}/unpublish", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.DeleteAsync($"/api/v1/kb/articles/{article.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.DeleteAsync($"/api/v1/kb/categories/{category.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.PutAsJsonAsync($"/api/v1/kb/categories/{category.Id}", new SaveKbCategoryRequest("Mine")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The same title in two workspaces gets the same address without colliding.
        var globexCategory = await globex.CreateKbCategoryAsync();
        (await globex.CreateKbArticleAsync(globexCategory.Id)).Slug.ShouldBe(article.Slug);

        var visitor = factory.CreateBareClient();
        (await visitor.GetAsync<List<PublicKbArticleListItem>>(globex.PortalPath("/kb/articles"))).Count.ShouldBe(1);
        (await visitor.PostAsJsonAsync(globex.PortalPath($"/kb/articles/{article.Slug}/feedback"), new KbFeedbackRequest(true)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var untouched = await acme.Client.GetAsync<KbArticleDto>($"/api/v1/kb/articles/{article.Id}");
        (untouched.Title, untouched.HelpfulCount, untouched.Status).ShouldBe((article.Title, 0, KbArticleStatus.Published));
    }
}
