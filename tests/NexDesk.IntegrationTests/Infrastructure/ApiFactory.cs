using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexDesk.Application.Common;
using NexDesk.Infrastructure.Email;
using Testcontainers.PostgreSql;

namespace NexDesk.IntegrationTests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("nexdesk_tests")
        .Build();

    public CapturedEmails Emails { get; } = new();

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Emails);
            services.RemoveAll<IEmailDispatcher>();
            services.AddScoped<IEmailDispatcher, CapturingEmailDispatcher>();
        });
    }
}

/// <summary>Emails the API queued, rendered with the real templates instead of being sent.</summary>
public sealed class CapturedEmails
{
    private readonly ConcurrentQueue<CapturedEmail> _messages = new();

    public IReadOnlyCollection<CapturedEmail> All => [.. _messages];

    public void Add(CapturedEmail email) => _messages.Enqueue(email);

    public CapturedEmail Last(string toAddress, string template) =>
        _messages.Last(m => m.Message.Template == template
            && string.Equals(m.Message.ToAddress, toAddress, StringComparison.OrdinalIgnoreCase));

    public int Count(string toAddress, string template) =>
        _messages.Count(m => m.Message.Template == template
            && string.Equals(m.Message.ToAddress, toAddress, StringComparison.OrdinalIgnoreCase));
}

public sealed record CapturedEmail(EmailMessage Message, RenderedEmail Rendered)
{
    public string Token
    {
        get
        {
            var link = Message.Model["link"];
            return Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        }
    }
}

internal sealed class CapturingEmailDispatcher(CapturedEmails emails, EmailTemplateRenderer renderer) : IEmailDispatcher
{
    public void Enqueue(EmailMessage message) => emails.Add(new CapturedEmail(message, renderer.Render(message)));
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
