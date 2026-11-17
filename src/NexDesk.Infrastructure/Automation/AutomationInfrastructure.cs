using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexDesk.Application.Automation;
using NexDesk.Application.Common;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Tenants;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Automation;

internal static class AutomationJson
{
    /// <summary>Rules are stored with readable enum names, so the JSON stays valid when an enum grows.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}

internal sealed class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> builder)
    {
        builder.ToTable("automation_rules");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).HasMaxLength(AutomationRule.NameMaxLength);
        builder.Property(r => r.Definition)
            .HasColumnType("jsonb")
            .HasConversion(
                definition => AutomationJson.Write(definition),
                json => AutomationJson.Read<RuleDefinition>(json),
                new ValueComparer<RuleDefinition>(
                    (left, right) => ReferenceEquals(left, right),
                    definition => definition.GetHashCode(),
                    definition => definition));
        builder.HasIndex(r => new { r.TenantId, r.Trigger, r.IsEnabled });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AutomationExecutionConfiguration : IEntityTypeConfiguration<AutomationExecution>
{
    public void Configure(EntityTypeBuilder<AutomationExecution> builder)
    {
        builder.ToTable("automation_executions");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TicketSubject).HasMaxLength(Ticket.SubjectMaxLength);
        builder.Property(e => e.Steps)
            .HasColumnType("jsonb")
            .HasConversion(
                steps => AutomationJson.Write(steps),
                json => AutomationJson.Read<List<ExecutionStep>>(json),
                new ValueComparer<IReadOnlyList<ExecutionStep>>(
                    (left, right) => ReferenceEquals(left, right),
                    steps => steps.Count,
                    steps => steps));
        builder.HasIndex(e => new { e.RuleId, e.CreatedAt });
        builder.HasIndex(e => new { e.RuleId, e.TicketId, e.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AutomationRule>().WithMany().HasForeignKey(e => e.RuleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Runs the time-based rules of every active workspace. Scheduled every five minutes.</summary>
public sealed class AutomationSweepJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AutomationSweepJob> logger)
{
    public const string JobId = "automation-sweep";
    public const string Schedule = "*/5 * * * *";

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
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
            await using var scope = scopes.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>().Begin(tenantId);
            try
            {
                var runs = await scope.ServiceProvider.GetRequiredService<AutomationEngine>().SweepAsync(now, cancellationToken);
                if (runs > 0)
                {
                    logger.LogInformation("Automation sweep ran {Count} rule(s) for tenant {TenantId}", runs, tenantId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Automation sweep failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
