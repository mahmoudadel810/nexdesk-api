using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Domain.Automation;

public enum AutomationTrigger
{
    TicketCreated,
    TicketUpdated,

    /// <summary>Checked on a schedule, for conditions such as "pending for 48 hours".</summary>
    TimeBased,
}

public enum ConditionMatch
{
    All,
    Any,
}

public enum ConditionField
{
    Status,
    Priority,
    Type,
    Channel,
    Assignee,
    Team,
    Tags,
    Subject,
    HoursSinceLastActivity,
    HoursSinceCreated,
}

public enum ConditionOperator
{
    Is,
    IsNot,
    Contains,
    NotContains,
    AtLeast,

    /// <summary>The field was changed by the update that triggered the rule.</summary>
    Changed,
}

public enum RuleActionType
{
    AssignUser,
    AssignTeam,
    SetPriority,
    SetStatus,
    AddTag,
    SendEmail,
    Notify,
}

public sealed record RuleCondition(ConditionField Field, ConditionOperator Operator, string? Value = null);

/// <summary>
/// Something a rule does. <see cref="Value"/> holds the target (a user, team, priority, status, tag or
/// notification audience); emails and notifications also carry text.
/// </summary>
public sealed record RuleAction(RuleActionType Type, string? Value = null, string? Subject = null, string? Body = null);

public sealed record RuleDefinition(
    ConditionMatch Match,
    IReadOnlyList<RuleCondition> Conditions,
    IReadOnlyList<RuleAction> Actions)
{
    public const int MaxConditions = 10;
    public const int MaxActions = 10;

    /// <summary>The value of an assignee or team condition that stands for "nobody".</summary>
    public const string None = "none";

    public bool HasCondition(ConditionField field) => Conditions.Any(c => c.Field == field);

    /// <summary>
    /// Whether the ticket satisfies the conditions. <paramref name="changes"/> lists what the triggering
    /// update changed and is empty for the other triggers.
    /// </summary>
    public bool Matches(Ticket ticket, IReadOnlyCollection<TicketEventType> changes, DateTimeOffset now)
    {
        if (Conditions.Count == 0)
        {
            return true;
        }

        return Match == ConditionMatch.All
            ? Conditions.All(condition => Holds(condition, ticket, changes, now))
            : Conditions.Any(condition => Holds(condition, ticket, changes, now));
    }

    private static bool Holds(
        RuleCondition condition,
        Ticket ticket,
        IReadOnlyCollection<TicketEventType> changes,
        DateTimeOffset now)
    {
        var value = condition.Value?.Trim() ?? string.Empty;
        if (condition.Operator == ConditionOperator.Changed)
        {
            return ChangeOf(condition.Field) is { } change && changes.Contains(change);
        }

        return condition.Field switch
        {
            ConditionField.Status => Compare(ticket.Status.ToString(), value, condition.Operator),
            ConditionField.Priority => Compare(ticket.Priority.ToString(), value, condition.Operator),
            ConditionField.Type => Compare(ticket.Type.ToString(), value, condition.Operator),
            ConditionField.Channel => Compare(ticket.Channel.ToString(), value, condition.Operator),
            ConditionField.Assignee => Compare(ticket.AssigneeId?.ToString() ?? None, value, condition.Operator),
            ConditionField.Team => Compare(ticket.TeamId?.ToString() ?? None, value, condition.Operator),
            ConditionField.Tags => ticket.Tags.Contains(value.ToLowerInvariant())
                == (condition.Operator == ConditionOperator.Contains),
            ConditionField.Subject => ticket.Subject.Contains(value, StringComparison.OrdinalIgnoreCase)
                == (condition.Operator == ConditionOperator.Contains),
            ConditionField.HoursSinceLastActivity => HoursReached(ticket.LastActivityAt, value, now),
            ConditionField.HoursSinceCreated => HoursReached(ticket.CreatedAt, value, now),
            _ => false,
        };
    }

    private static bool Compare(string actual, string expected, ConditionOperator @operator) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) == (@operator == ConditionOperator.Is);

    private static bool HoursReached(DateTimeOffset since, string hours, DateTimeOffset now) =>
        int.TryParse(hours, out var parsed) && now - since >= TimeSpan.FromHours(parsed);

    private static TicketEventType? ChangeOf(ConditionField field) => field switch
    {
        ConditionField.Status => TicketEventType.StatusChanged,
        ConditionField.Priority => TicketEventType.PriorityChanged,
        ConditionField.Type => TicketEventType.TypeChanged,
        ConditionField.Assignee => TicketEventType.AssigneeChanged,
        ConditionField.Team => TicketEventType.TeamChanged,
        ConditionField.Tags => TicketEventType.TagsChanged,
        ConditionField.Subject => TicketEventType.SubjectChanged,
        _ => null,
    };
}

/// <summary>"When this happens to a ticket and these conditions hold, do that."</summary>
public sealed class AutomationRule : AuditableEntity, ITenantOwned
{
    public const int NameMaxLength = 120;

    private AutomationRule()
    {
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public AutomationTrigger Trigger { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>Rules with the same trigger run in this order, lowest first.</summary>
    public int Position { get; private set; }

    public RuleDefinition Definition { get; private set; } = null!;

    public int RunCount { get; private set; }

    public DateTimeOffset? LastRunAt { get; private set; }

    public static AutomationRule Create(
        string name,
        AutomationTrigger trigger,
        RuleDefinition definition,
        int position,
        bool isEnabled) => new()
        {
            Name = name.Trim(),
            Trigger = trigger,
            Definition = definition,
            Position = position,
            IsEnabled = isEnabled,
        };

    public void Update(string name, AutomationTrigger trigger, RuleDefinition definition, int position, bool isEnabled)
    {
        Name = name.Trim();
        Trigger = trigger;
        Definition = definition;
        Position = position;
        IsEnabled = isEnabled;
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;

    public void RecordRun(DateTimeOffset now)
    {
        RunCount++;
        LastRunAt = now;
    }
}

public enum ExecutionOutcome
{
    /// <summary>Every action was carried out.</summary>
    Applied,

    /// <summary>Some actions could not be carried out, for example because a user was deactivated.</summary>
    Partial,

    Failed,
}

/// <summary>What became of one action when a rule ran.</summary>
public sealed record ExecutionStep(RuleActionType Type, string? Value, bool Applied);

/// <summary>One entry of a rule's execution log.</summary>
public sealed class AutomationExecution : Entity, ITenantOwned
{
    private AutomationExecution()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RuleId { get; private set; }

    public Guid TicketId { get; private set; }

    public int TicketNumber { get; private set; }

    public string TicketSubject { get; private set; } = null!;

    public ExecutionOutcome Outcome { get; private set; }

    public IReadOnlyList<ExecutionStep> Steps { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public static AutomationExecution Record(
        AutomationRule rule,
        Ticket ticket,
        IReadOnlyList<ExecutionStep> steps,
        DateTimeOffset now) => new()
        {
            RuleId = rule.Id,
            TicketId = ticket.Id,
            TicketNumber = ticket.Number,
            TicketSubject = ticket.Subject,
            Steps = steps,
            Outcome = steps.All(step => step.Applied)
                ? ExecutionOutcome.Applied
                : steps.Any(step => step.Applied) ? ExecutionOutcome.Partial : ExecutionOutcome.Failed,
            CreatedAt = now,
        };
}
