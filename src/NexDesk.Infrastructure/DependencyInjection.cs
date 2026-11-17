using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexDesk.Application.Common;
using NexDesk.Application.KnowledgeBase;
using NexDesk.Infrastructure.Automation;
using NexDesk.Infrastructure.Email;
using NexDesk.Infrastructure.Identity;
using NexDesk.Infrastructure.KnowledgeBase;
using NexDesk.Infrastructure.Persistence;
using NexDesk.Infrastructure.Sla;
using NexDesk.Infrastructure.Tenancy;
using NexDesk.Infrastructure.Tickets;

namespace NexDesk.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantScopeFactory>(provider => provider.GetRequiredService<TenantContext>());

        services.AddOptions<AppOptions>().BindConfiguration(AppOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IFileStorage, LocalDiskFileStorage>();
        services.AddScoped<ITicketNumberAllocator, TicketNumberAllocator>();
        services.AddScoped<ITicketSearch, TicketSearch>();
        services.AddScoped<IKbSearch, KbSearch>();
        services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        services.AddMemoryCache();
        services.AddScoped<IIdentityDirectory, IdentityDirectory>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<SessionStateCache>();
        services.AddSingleton<ISessionStateCache>(provider => provider.GetRequiredService<SessionStateCache>());

        services.AddSingleton<EmailTemplateRenderer>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IEmailDispatcher, HangfireEmailDispatcher>();
        services.AddScoped<SendEmailJob>();

        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                storage => storage.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions { QueuePollInterval = TimeSpan.FromSeconds(2) }));
        services.AddHangfireServer(server => server.WorkerCount = 4);
        services.AddScoped<SlaSweepJob>();
        services.AddScoped<AutomationSweepJob>();
        if (configuration.GetValue("Jobs:RecurringEnabled", true))
        {
            services.AddHostedService<RecurringJobRegistrar>();
        }

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(cancellationToken);
    }
}
