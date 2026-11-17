using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Identity;

public sealed class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";

    /// <summary>The platform admin's sign-in address. Leave empty to create no admin.</summary>
    public string? Email { get; set; }

    /// <summary>Used only when the admin is first created; later changes go through the profile page.</summary>
    public string? Password { get; set; }

    public string FullName { get; set; } = "Platform Admin";
}

/// <summary>
/// Creates the platform admin from configuration the first time the API starts with one set. An existing
/// account is left as it is, so the configured password never overwrites one changed since.
/// </summary>
internal sealed class PlatformAdminBootstrap(
    IServiceScopeFactory scopes,
    IOptions<PlatformAdminOptions> options,
    TimeProvider clock,
    ILogger<PlatformAdminBootstrap> logger) : IHostedService
{
    public const int PasswordMinLength = 12;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Email) || string.IsNullOrEmpty(settings.Password))
        {
            return;
        }

        if (settings.Password.Length < PasswordMinLength)
        {
            logger.LogWarning("The platform admin was not created: its password must have at least {Length} characters", PasswordMinLength);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IIdentityDirectory>();
        if (await directory.FindUserByEmailAsync(settings.Email, cancellationToken) is not null)
        {
            return;
        }

        var admin = User.CreatePlatformAdmin(settings.Email, settings.FullName, clock.GetUtcNow());
        admin.SetPasswordHash(scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(settings.Password));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(admin);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created the platform admin {Email}", admin.Email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
