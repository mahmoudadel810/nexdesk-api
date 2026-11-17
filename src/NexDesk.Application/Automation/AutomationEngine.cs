using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Application.Notifications;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Automation;

/// <summary>Remembers, for the current request or job, that the rules engine is saving its own changes.</summary>
public sealed class AutomationRunState
{
    public bool IsRunning { get; set; }
}

/// <summary>
/// Evaluates the workspace's rules against a ticket and carries out the actions of those that match.
/// <para>
/// Loop protection: rules never react to changes made by rules. The engine ignores ticket events while
/// it is saving and ignores updates made by the system actor, and every rule runs at most once per
/// event. Within one pass, a later rule does see what an earlier rule changed.
/// </para>
/// </summary>
public sealed class AutomationEngine(
    IAppDbContext db,
    AutomationRunState state,
    Notifier notifier,
    IEmailDispatcher emails,
    TimeProvider clock,
    IOptions<AppOptions> appOptions,
    ILogger<AutomationEngine> logger)
{
    private const int MaxAttempts = 3;
    private const int SweepBatchSize = 500;

    /// <summary>Runs the rules of an event trigger for one ticket.</summary>
    public async Task RunAsync(
        AutomationTrigger trigger,
        Guid ticketId,
        IReadOnlyCollection<TicketEventType> changes,
        CancellationToken cancellationToken)
    {
        if (state.IsRunning)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var rules = await db.AutomationRules
                .Where(r => r.IsEnabled && r.Trigger == trigger)
                .OrderBy(r => r.Position)
                .ThenBy(r => r.CreatedAt)
                .ToListAsync(cancellationToken);
            if (rules.Count == 0)
            {
                return;
            }

            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
            if (ticket is null)
            {
                return;
            }

            try
            {
                await ApplyAsync(rules, ticket, changes, clock.GetUtcNow(), cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Someone else changed the ticket meanwhile: evaluate the rules again on what is there now.
                db.DiscardChanges();
            }
        }
    }

    /// <summary>Runs the time-based rules of the current workspace. Returns how many times a rule ran.</summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ruleIds = await db.AutomationRules.AsNoTracking()
            .Where(r => r.IsEnabled && r.Trigger == AutomationTrigger.TimeBased)
            .OrderBy(r => r.Position)
            .ThenBy(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var runs = 0;
        foreach (var ruleId in ruleIds)
        {
            var definition = await db.AutomationRules.AsNoTracking()
                .Where(r => r.Id == ruleId)
                .Select(r => r.Definition)
                .FirstAsync(cancellationToken);
            foreach (var ticketId in await DueTicketIds(ruleId, definition, now, cancellationToken))
            {
                try
                {
                    var rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.IsEnabled, cancellationToken);
                    var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
                    if (rule is not null && ticket is not null)
                    {
                        runs += await ApplyAsync([rule], ticket, [], now, cancellationToken);
                    }
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The ticket just changed, so it is no longer idle. The next sweep looks at it again.
                    db.DiscardChanges();
                }
            }
        }

        return runs;
    }

    /// <summary>
    /// Open tickets that have waited long enough and that the rule has not handled yet. A rule that
    /// measures inactivity handles a ticket once per quiet period; any other rule handles it once.
    /// </summary>
    private async Task<List<Guid>> DueTicketIds(
        Guid ruleId,
        RuleDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tickets = db.Tickets.AsNoTracking().Where(t => t.Status != TicketStatus.Closed);
        var measuresInactivity = false;
        foreach (var condition in definition.Conditions)
        {
            var value = condition.Value?.Trim() ?? string.Empty;
            switch (condition.Field)
            {
                case ConditionField.HoursSinceLastActivity when int.TryParse(value, out var hours):
                    var idleSince = now.AddHours(-hours);
                    tickets = tickets.Where(t => t.LastActivityAt <= idleSince);
                    measuresInactivity = true;
                    break;
                case ConditionField.HoursSinceCreated when int.TryParse(value, out var hours):
                    var createdBefore = now.AddHours(-hours);
                    tickets = tickets.Where(t => t.CreatedAt <= createdBefore);
                    break;
                case ConditionField.Status when Enum.TryParse<TicketStatus>(value, out var status):
                    tickets = condition.Operator == ConditionOperator.Is
                        ? tickets.Where(t => t.Status == status)
                        : tickets.Where(t => t.Status != status);
                    break;
                case ConditionField.Priority when Enum.TryParse<TicketPriority>(value, out var priority):
                    tickets = condition.Operator == ConditionOperator.Is
                        ? tickets.Where(t => t.Priority == priority)
                        : tickets.Where(t => t.Priority != priority);
                    break;
            }
        }

        tickets = measuresInactivity
            ? tickets.Where(t => !db.AutomationExecutions.Any(e =>
                e.RuleId == ruleId && e.TicketId == t.Id && e.CreatedAt >= t.LastActivityAt))
            : tickets.Where(t => !db.AutomationExecutions.Any(e => e.RuleId == ruleId && e.TicketId == t.Id));
        return await tickets
            .OrderBy(t => t.LastActivityAt)
            .Select(t => t.Id)
            .Take(SweepBatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<int> ApplyAsync(
        IReadOnlyList<AutomationRule> rules,
        Ticket ticket,
        IReadOnlyCollection<TicketEventType> changes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var afterSave = new List<Func<Task>>();
        var runs = 0;
        foreach (var rule in rules)
        {
            if (ticket.IsClosed || !rule.Definition.Matches(ticket, changes, now))
            {
                continue;
            }

            var steps = new List<ExecutionStep>();
            foreach (var action in rule.Definition.Actions)
            {
                steps.Add(await Perform(rule, action, ticket, now, afterSave, cancellationToken));
            }

            db.AutomationExecutions.Add(AutomationExecution.Record(rule, ticket, steps, now));
            rule.RecordRun(now);
            runs++;
        }

        if (runs == 0)
        {
            return 0;
        }

        state.IsRunning = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var effect in afterSave)
            {
                await effect();
            }
        }
        finally
        {
            state.IsRunning = false;
        }

        logger.LogInformation("Automation ran {Count} rule(s) on ticket {TicketId}", runs, ticket.Id);
        return runs;
    }

    private async Task<ExecutionStep> Perform(
        AutomationRule rule,
        RuleAction action,
        Ticket ticket,
        DateTimeOffset now,
        List<Func<Task>> afterSave,
        CancellationToken cancellationToken)
    {
        var value = action.Value?.Trim() ?? string.Empty;
        ExecutionStep Done(string? shown) => new(action.Type, shown, true);
        ExecutionStep Skipped() => new(action.Type, value, false);

        // A ticket closed by an earlier action of the same rule takes no further changes.
        if (ticket.IsClosed && action.Type is not (RuleActionType.SendEmail or RuleActionType.Notify))
        {
            return Skipped();
        }

        switch (action.Type)
        {
            case RuleActionType.AssignUser:
                var assigneeName = Guid.TryParse(value, out var userId)
                    ? await db.Users
                        .Where(u => u.Id == userId && u.IsActive && u.Role != Role.Viewer)
                        .Select(u => u.FullName)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
                if (assigneeName is null)
                {
                    return Skipped();
                }

                var previousAssignee = ticket.AssigneeId is { } currentAssignee
                    ? await db.Users.Where(u => u.Id == currentAssignee).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken)
                    : null;
                ticket.AssignTo(userId, assigneeName, previousAssignee, Actor.System, now);
                return Done(assigneeName);

            case RuleActionType.AssignTeam:
                var teamName = Guid.TryParse(value, out var teamId)
                    ? await db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
                    : null;
                if (teamName is null)
                {
                    return Skipped();
                }

                var previousTeam = ticket.TeamId is { } currentTeam
                    ? await db.Teams.Where(t => t.Id == currentTeam).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
                    : null;
                ticket.RouteTo(teamId, teamName, previousTeam, Actor.System, now);
                return Done(teamName);

            case RuleActionType.SetPriority when Enum.TryParse<TicketPriority>(value, out var priority):
                ticket.ChangePriority(priority, Actor.System, now);
                return Done(priority.ToString());

            case RuleActionType.SetStatus when Enum.TryParse<TicketStatus>(value, out var status):
                ticket.ChangeStatus(status, Actor.System, now);
                return Done(status.ToString());

            case RuleActionType.AddTag when value.Length > 0:
                ticket.SetTags([.. ticket.Tags, value], Actor.System, now);
                return Done(value.ToLowerInvariant());

            case RuleActionType.SendEmail:
                var requester = await db.Customers.AsNoTracking()
                    .Where(c => c.Id == ticket.RequesterId)
                    .Select(c => new { c.Email, c.FullName })
                    .FirstOrDefaultAsync(cancellationToken);
                if (requester is null)
                {
                    return Skipped();
                }

                afterSave.Add(() => EmailRequester(ticket, requester.Email, requester.FullName, action, cancellationToken));
                return Done(Fill(action.Subject, ticket, requester.FullName));

            case RuleActionType.Notify:
                var (recipients, audience) = await Audience(value, ticket, cancellationToken);
                if (recipients.Count == 0)
                {
                    return Skipped();
                }

                afterSave.Add(() => notifier.NotifyAsync(
                    NotificationType.Automation, ticket, recipients, rule.Name, Fill(action.Body, ticket, null), cancellationToken));
                return Done(audience);

            default:
                return Skipped();
        }
    }

    private async Task<(List<Guid> Recipients, string Shown)> Audience(string value, Ticket ticket, CancellationToken cancellationToken)
    {
        switch (value)
        {
            case RuleDefinitionChecker.AssigneeAudience:
                return (ticket.AssigneeId is { } assignee ? [assignee] : [], value);
            case RuleDefinitionChecker.AdminsAudience:
                var admins = await db.Users.AsNoTracking()
                    .Where(u => u.IsActive && (u.Role == Role.Owner || u.Role == Role.Admin))
                    .Select(u => u.Id)
                    .ToListAsync(cancellationToken);
                return (admins, value);
            default:
                var name = Guid.TryParse(value, out var userId)
                    ? await db.Users.AsNoTracking()
                        .Where(u => u.Id == userId && u.IsActive)
                        .Select(u => u.FullName)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
                return name is null ? ([], value) : ([userId], name);
        }
    }

    private async Task EmailRequester(Ticket ticket, string email, string name, RuleAction action, CancellationToken cancellationToken)
    {
        var workspace = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == ticket.TenantId)
            .Select(t => new { t.Name, t.Slug, t.DefaultLanguage })
            .FirstAsync(cancellationToken);
        emails.Enqueue(new EmailMessage(
            email,
            name,
            "automation-message",
            workspace.DefaultLanguage,
            new Dictionary<string, string>
            {
                ["name"] = name,
                ["workspace"] = workspace.Name,
                ["number"] = ticket.Number.ToString(CultureInfo.InvariantCulture),
                ["subject"] = ticket.Subject,
                ["title"] = Fill(action.Subject, ticket, name),
                ["message"] = Fill(action.Body, ticket, name),
                ["link"] = $"{appOptions.Value.WebBaseUrl.TrimEnd('/')}/portal/{workspace.Slug}/tickets/{ticket.Id}",
            }));
    }

    /// <summary>Replaces the placeholders a rule's text may contain.</summary>
    private static string Fill(string? text, Ticket ticket, string? customerName) =>
        (text ?? string.Empty)
            .Replace("{{ticket.number}}", ticket.Number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{{ticket.subject}}", ticket.Subject, StringComparison.OrdinalIgnoreCase)
            .Replace("{{customer.name}}", customerName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
}

internal sealed class AutomationEventHandlers(AutomationEngine engine)
    : IDomainEventHandler<TicketCreated>, IDomainEventHandler<TicketChanged>
{
    public Task Handle(TicketCreated domainEvent, CancellationToken cancellationToken) =>
        engine.RunAsync(AutomationTrigger.TicketCreated, domainEvent.TicketId, [], cancellationToken);

    public Task Handle(TicketChanged domainEvent, CancellationToken cancellationToken) =>
        domainEvent.Actor.Type == ActorType.System
            ? Task.CompletedTask
            : engine.RunAsync(AutomationTrigger.TicketUpdated, domainEvent.TicketId, domainEvent.Changes, cancellationToken);
}
