using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexDesk.Application.Common;
using NexDesk.Application.Developer;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Developer;
using NexDesk.Infrastructure.Email;
using Testcontainers.PostgreSql;

namespace NexDesk.IntegrationTests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("nexdesk_tests")
        .Build();

    public const string InboundSecret = "integration-tests-inbound-secret";

    public CapturedEmails Emails { get; } = new();

    public WebhookReceiver Webhooks { get; } = new();

    /// <summary>Ids of tickets whose creation event reached a domain event handler.</summary>
    public ConcurrentBag<Guid> CreatedTicketEvents { get; } = [];

    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"nexdesk-tests-{Guid.NewGuid():N}");

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Storage:RootPath", _storageRoot);
        builder.UseSetting("Jobs:RecurringEnabled", "false");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Inbound:Secret", InboundSecret);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, LoopbackAddressFilter>();
            services.AddSingleton(Emails);
            services.RemoveAll<IEmailDispatcher>();
            services.AddScoped<IEmailDispatcher, CapturingEmailDispatcher>();
            services.AddSingleton(CreatedTicketEvents);
            services.AddSingleton(Webhooks);
            services.AddHttpClient(DeliverWebhookJob.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(provider => new ReceiverHandler(provider.GetRequiredService<WebhookReceiver>()));
            services.RemoveAll<IWebhookDispatcher>();
            services.AddScoped<IWebhookDispatcher, InlineWebhookDispatcher>();
            services.AddScoped<IDomainEventHandler<TicketCreated>, FailingTicketCreatedHandler>();
            services.AddScoped<IDomainEventHandler<TicketCreated>, RecordingTicketCreatedHandler>();
        });
    }
}

/// <summary>The in-memory test server leaves the caller's address empty; a real host always has one.</summary>
internal sealed class LoopbackAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress ??= System.Net.IPAddress.Loopback;
            return nextMiddleware(context);
        });
        next(app);
    };
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

internal sealed class RecordingTicketCreatedHandler(ConcurrentBag<Guid> created) : IDomainEventHandler<TicketCreated>
{
    public Task Handle(TicketCreated domainEvent, CancellationToken cancellationToken)
    {
        created.Add(domainEvent.TicketId);
        return Task.CompletedTask;
    }
}

/// <summary>Proves that one broken handler neither fails the request nor stops the handlers after it.</summary>
internal sealed class FailingTicketCreatedHandler : IDomainEventHandler<TicketCreated>
{
    public Task Handle(TicketCreated domainEvent, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated handler failure.");
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
