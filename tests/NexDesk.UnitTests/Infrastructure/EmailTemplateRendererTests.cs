using NexDesk.Application.Common;
using NexDesk.Infrastructure.Email;

namespace NexDesk.UnitTests.Infrastructure;

public sealed class EmailTemplateRendererTests
{
    private readonly EmailTemplateRenderer _renderer = new();

    private static EmailMessage Message(string template, string language, string name = "Olivia") => new(
        "olivia@example.test",
        name,
        template,
        language,
        new Dictionary<string, string>
        {
            ["name"] = name,
            ["link"] = "http://localhost:4200/auth/verify-email?token=abc&x=1",
            ["hours"] = "48",
            ["minutes"] = "60",
        });

    [Theory]
    [InlineData("verify-email")]
    [InlineData("reset-password")]
    public void Renders_every_template_in_english_and_arabic_without_leftover_placeholders(string template)
    {
        foreach (var language in SupportedLanguages.All)
        {
            var email = _renderer.Render(Message(template, language));

            email.Subject.ShouldNotBeNullOrWhiteSpace();
            email.Html.ShouldNotContain("{{");
            email.Html.ShouldNotContain("{name}");
            email.Html.ShouldContain("token=abc&amp;x=1");
            email.Text.ShouldContain("token=abc&x=1");
        }
    }

    [Fact]
    public void Arabic_emails_are_right_to_left()
    {
        _renderer.Render(Message("verify-email", "ar")).Html.ShouldContain("dir=\"rtl\"");
        _renderer.Render(Message("verify-email", "en")).Html.ShouldContain("dir=\"ltr\"");
    }

    [Fact]
    public void Model_values_are_html_encoded()
    {
        var email = _renderer.Render(Message("verify-email", "en", "<script>alert(1)</script>"));

        email.Html.ShouldNotContain("<script>");
        email.Html.ShouldContain("&lt;script&gt;");
    }

    private static EmailMessage Reply(string body) => new(
        "casey@example.test",
        "Casey",
        "customer-reply",
        "en",
        new Dictionary<string, string>
        {
            ["name"] = "Casey",
            ["workspace"] = "Acme",
            ["agent"] = "Maya",
            ["number"] = "271",
            ["subject"] = "Invoices",
            ["link"] = "http://localhost:4200/portal/acme/tickets/1",
            ["message"] = body,
        });

    [Fact]
    public void Web_addresses_in_a_quoted_reply_are_links_and_the_rest_stays_encoded()
    {
        var email = _renderer.Render(Reply(
            "See \"Download an invoice\": http://localhost:4200/portal/acme/kb/download-an-invoice?a=1&b=2. <b>Thanks</b>"));

        email.Html.ShouldContain(
            "<a href=\"http://localhost:4200/portal/acme/kb/download-an-invoice?a=1&amp;b=2\" style=\"color:#0b57d0;\">" +
            "http://localhost:4200/portal/acme/kb/download-an-invoice?a=1&amp;b=2</a>. &lt;b&gt;Thanks&lt;/b&gt;");
        email.Html.ShouldContain("See &quot;Download an invoice&quot;: <a ");
        email.Text.ShouldContain("http://localhost:4200/portal/acme/kb/download-an-invoice?a=1&b=2.");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>x</script>")]
    [InlineData("https://")]
    public void Only_web_addresses_become_links(string body)
    {
        var html = _renderer.Render(Reply(body)).Html;
        var quote = html[html.IndexOf("<blockquote", StringComparison.Ordinal)..html.IndexOf("</blockquote>", StringComparison.Ordinal)];

        quote.ShouldNotContain("<a ");
        quote.ShouldNotContain("<script>");
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english()
    {
        _renderer.Render(Message("verify-email", "fr")).Subject.ShouldBe("Confirm your email address");
    }

    [Fact]
    public void An_unknown_template_is_rejected()
    {
        Should.Throw<InvalidOperationException>(() => _renderer.Render(Message("no-such-template", "en")));
    }
}
