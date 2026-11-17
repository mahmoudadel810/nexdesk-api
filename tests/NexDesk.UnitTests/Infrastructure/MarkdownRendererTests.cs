using NexDesk.Domain.KnowledgeBase;
using NexDesk.Infrastructure.KnowledgeBase;

namespace NexDesk.UnitTests.Infrastructure;

public sealed class MarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void Renders_headings_lists_emphasis_tables_and_links()
    {
        var html = _renderer.ToHtml(
            "## Steps\n\n1. Open **Billing**\n2. Read the [guide](https://example.test/guide)\n\n| A | B |\n|---|---|\n| 1 | 2 |");

        html.ShouldContain("<h2>Steps</h2>");
        html.ShouldContain("<ol>");
        html.ShouldContain("<strong>Billing</strong>");
        html.ShouldContain("<table>");
        html.ShouldContain("href=\"https://example.test/guide\"");
        html.ShouldContain("rel=\"noopener noreferrer nofollow\"");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Hello <img src=x onerror=alert(1)> world")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    public void Raw_html_is_shown_as_text_and_never_passed_through(string markdown)
    {
        var html = _renderer.ToHtml(markdown);

        html.ShouldNotContain("<script");
        html.ShouldNotContain("<img");
        html.ShouldNotContain("<iframe");
        html.ShouldNotContain("<a href=\"javascript");
        html.ShouldContain("&lt;");
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](JaVaScRiPt:alert(1))")]
    [InlineData("[click](&#106;avascript:alert(1))")]
    [InlineData("[click](<java\tscript:alert(1)>)")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click](vbscript:msgbox(1))")]
    [InlineData("![pic](javascript:alert(1))")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[click]: javascript:alert(1)\n\n[click]")]
    public void Links_with_a_scripting_scheme_are_neutralised(string markdown)
    {
        var html = _renderer.ToHtml(markdown);

        html.ShouldNotContain("javascript:", Case.Insensitive);
        html.ShouldNotContain("vbscript:", Case.Insensitive);
        html.ShouldNotContain("data:", Case.Insensitive);
        html.ShouldNotContain("script:alert", Case.Insensitive);
    }

    [Theory]
    [InlineData("[a](https://example.test)", "https://example.test")]
    [InlineData("[a](mailto:help@example.test)", "mailto:help@example.test")]
    [InlineData("[a](/portal/acme/kb)", "/portal/acme/kb")]
    [InlineData("[a](#steps)", "#steps")]
    [InlineData("[a](guide?time=10:30)", "guide?time=10:30")]
    public void Ordinary_links_are_kept(string markdown, string url) =>
        _renderer.ToHtml(markdown).ShouldContain($"href=\"{url}\"");

    [Fact]
    public void Plain_text_drops_every_kind_of_markup()
    {
        var text = _renderer.ToPlainText(
            "## Steps\n\n1. Open **Billing**\n2. Read the [guide](https://example.test/guide) and `save`\n\n> Note\n\n<b>raw</b>");

        text.ShouldStartWith("Steps Open Billing Read the guide and save Note");
        text.ShouldNotContain("**");
        text.ShouldNotContain("](");
        text.ShouldNotContain("#");
        text.ShouldNotContain("\n");
    }

    [Theory]
    [InlineData("How to download an invoice", "how-to-download-an-invoice")]
    [InlineData("  What's new?!  ", "what-s-new")]
    [InlineData("كيفية تنزيل الفاتورة", "كيفية-تنزيل-الفاتورة")]
    [InlineData("???", "article")]
    public void Slugs_keep_letters_of_any_script(string title, string slug) =>
        KbArticle.Slugify(title).ShouldBe(slug);
}
