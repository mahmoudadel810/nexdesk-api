using NexDesk.Infrastructure.Seeding;

namespace NexDesk.Api;

/// <summary>The <c>--seed</c> command line: fill the database with demo data, then exit.</summary>
public static class DemoSeedCommand
{
    public static SeedSize? Parse(string[] args)
    {
        var flag = args.FirstOrDefault(arg => arg == "--seed" || arg.StartsWith("--seed=", StringComparison.Ordinal));
        return flag switch
        {
            null => null,
            "--seed=small" => SeedSize.Small,
            _ => SeedSize.Full,
        };
    }

    public static async Task<int> RunAsync(WebApplication app, SeedSize size, string[] args)
    {
        var logger = app.Services.GetRequiredService<ILogger<DemoSeeder>>();

        // Every demo account shares a published password, which must never reach a real installation.
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing") && !args.Contains("--force"))
        {
            logger.LogError(
                "Refusing to seed demo data in the {Environment} environment: the demo accounts use a public password. "
                + "Pass --force to seed anyway.",
                app.Environment.EnvironmentName);
            return 1;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(size, CancellationToken.None);
        logger.LogInformation("{Message}", result.Message);
        return 0;
    }
}
