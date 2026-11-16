using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Hangfire;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using NexDesk.Application.Common;

namespace NexDesk.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    [Required]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 1025;

    public bool UseStartTls { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = "no-reply@nexdesk.local";

    [Required]
    public string FromName { get; set; } = "NexDesk";
}

public sealed record RenderedEmail(string Subject, string Html, string Text);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Turns a template name and model into a localized HTML and plain-text email.</summary>
public sealed class EmailTemplateRenderer
{
    private const string ResourcePrefix = "NexDesk.Infrastructure.Email.Templates.";

    private readonly string _layout = ReadResource("layout.html");

    private readonly FrozenDictionary<string, Dictionary<string, Dictionary<string, string>>> _strings =
        SupportedLanguages.All.ToFrozenDictionary(
            language => language,
            language => JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                ReadResource($"strings-{language}.json"))!);

    public RenderedEmail Render(EmailMessage message)
    {
        var language = SupportedLanguages.Normalize(message.Language);
        if (!_strings[language].TryGetValue(message.Template, out var strings))
        {
            throw new InvalidOperationException($"Email template '{message.Template}' is not defined.");
        }

        string Text(string key) => Fill(strings[key], message.Model, encode: false);
        string Html(string key) => Fill(strings[key], message.Model, encode: true);

        message.Model.TryGetValue("link", out var link);
        var button = link is null || !strings.ContainsKey("button")
            ? string.Empty
            : $"""
               <p style="margin:24px 0;"><a href="{WebUtility.HtmlEncode(link)}" style="display:inline-block;background:#0b57d0;color:#ffffff;text-decoration:none;padding:12px 24px;border-radius:24px;font-weight:600;">{Html("button")}</a></p>
               <p style="margin:0;color:#5c6370;font-size:13px;word-break:break-all;">{WebUtility.HtmlEncode(link)}</p>
               """;

        var subject = Text("subject");
        var html = _layout
            .Replace("{{lang}}", language, StringComparison.Ordinal)
            .Replace("{{dir}}", language == "ar" ? "rtl" : "ltr", StringComparison.Ordinal)
            .Replace("{{subject}}", WebUtility.HtmlEncode(subject), StringComparison.Ordinal)
            .Replace("{{heading}}", Html("heading"), StringComparison.Ordinal)
            .Replace("{{body}}", $"<p style=\"margin:0;\">{Html("body")}</p>", StringComparison.Ordinal)
            .Replace("{{button}}", button, StringComparison.Ordinal)
            .Replace("{{footer}}", Html("footer"), StringComparison.Ordinal);

        var text = string.Join(
            "\n\n",
            new[] { Text("heading"), Text("body"), link, Text("footer") }.Where(part => !string.IsNullOrEmpty(part)));
        return new RenderedEmail(subject, html, text);
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> model, bool encode)
    {
        var result = encode ? WebUtility.HtmlEncode(template) : template;
        foreach (var (key, value) in model)
        {
            result = result.Replace($"{{{key}}}", encode ? WebUtility.HtmlEncode(value) : value, StringComparison.Ordinal);
        }

        return result;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(EmailTemplateRenderer).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"Embedded email resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed class SmtpEmailSender(EmailTemplateRenderer renderer, IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var rendered = renderer.Render(message);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mime.Subject = rendered.Subject;
        mime.Body = new BodyBuilder { HtmlBody = rendered.Html, TextBody = rendered.Text }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(
            settings.Host,
            settings.Port,
            settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(new MailKit.SendRequest(mime), cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

public sealed class SendEmailJob(IEmailSender sender)
{
    [AutomaticRetry(Attempts = 5)]
    public Task RunAsync(EmailMessage message, CancellationToken cancellationToken) =>
        sender.SendAsync(message, cancellationToken);
}

internal sealed class HangfireEmailDispatcher(IBackgroundJobClient jobs) : IEmailDispatcher
{
    public void Enqueue(EmailMessage message) =>
        jobs.Enqueue<SendEmailJob>(job => job.RunAsync(message, CancellationToken.None));
}
