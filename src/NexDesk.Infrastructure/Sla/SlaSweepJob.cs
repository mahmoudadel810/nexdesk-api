using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexDesk.Application.Common;
using NexDesk.Application.Sla;
using NexDesk.Domain.Tenants;
using NexDesk.Infrastructure.Automation;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Sla;

/// <summary>Checks every active workspace for deadlines that are close or missed. Runs once a minute.</summary>
public sealed class SlaSweepJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SlaSweepJob> logger)
{
    public const string JobId = "sla-sweep";

    [DisableConcurrentExecution(timeoutInSeconds: 50)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<Guid> tenantIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenantIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants
                .Where(t => t.Status == TenantStatus.Active)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var tenantId in tenantIds)
        {
            // Each workspace gets its own scope, so its query filter and calendar cannot leak into the next.
            await using var scope = scopes.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Begin(tenantId);
            try
            {
                var touched = await scope.ServiceProvider.GetRequiredService<SlaEngine>().SweepAsync(now, cancellationToken);
                if (touched > 0)
                {
                    logger.LogInformation("SLA sweep flagged {Count} tickets for tenant {TenantId}", touched, tenantId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "SLA sweep failed for tenant {TenantId}", tenantId);
            }
        }
    }
}

/// <summary>Registers the recurring jobs once the application has started.</summary>
internal sealed class RecurringJobRegistrar(IRecurringJobManager jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jobs.AddOrUpdate<SlaSweepJob>(SlaSweepJob.JobId, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
        jobs.AddOrUpdate<AutomationSweepJob>(
            AutomationSweepJob.JobId, job => job.RunAsync(CancellationToken.None), AutomationSweepJob.Schedule);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
