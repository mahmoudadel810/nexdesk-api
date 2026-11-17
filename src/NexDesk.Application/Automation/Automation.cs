using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Automation;

public sealed record AutomationRuleDto(
    Guid Id,
    string Name,
    AutomationTrigger Trigger,
    bool IsEnabled,
    int Position,
    RuleDefinition Definition,
    int RunCount,
    DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt)
{
    public static AutomationRuleDto From(AutomationRule rule) => new(
        rule.Id, rule.Name, rule.Trigger, rule.IsEnabled, rule.Position, rule.Definition, rule.RunCount, rule.LastRunAt, rule.CreatedAt);
}

public sealed record AutomationExecutionDto(
    Guid Id,
    Guid RuleId,
    Guid TicketId,
    int TicketNumber,
    string TicketSubject,
    ExecutionOutcome Outcome,
    IReadOnlyList<ExecutionStep> Steps,
    DateTimeOffset CreatedAt);

public static class AutomationErrors
{
    public static readonly Error RuleNotFound = Error.NotFound("automation-rule-not-found", "The rule was not found.");

    public static Error InvalidCondition(int index, string reason) =>
        Error.Validation("automation-invalid-condition", $"Condition {index + 1}: {reason}");

    public static Error InvalidAction(int index, string reason) =>
        Error.Validation("automation-invalid-action", $"Action {index + 1}: {reason}");

    public static readonly Error TimeRuleNeedsDuration = Error.Validation(
        "automation-time-rule-needs-duration",
        "A time-based rule must match all conditions and include one that measures hours.");
}

/// <summary>Checks that a rule only refers to things that exist and combines fields and operators sensibly.</summary>
internal sealed class RuleDefinitionChecker(IAppDbContext db)
{
    public const int MaxHours = 8760;
    public const int EmailSubjectMaxLength = 200;
    public const int TextMaxLength = 4000;
    public const string AssigneeAudience = "assignee";
    public const string AdminsAudience = "admins";

    private static readonly Dictionary<ConditionField, ConditionOperator[]> Operators = new()
    {
        [ConditionField.Status] = [ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.Changed],
        [ConditionField.Priority] = [ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.Changed],
        [ConditionField.Type] = [ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.Changed],
        [ConditionField.Channel] = [ConditionOperator.Is, ConditionOperator.IsNot],
        [ConditionField.Assignee] = [ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.Changed],
        [ConditionField.Team] = [ConditionOperator.Is, ConditionOperator.IsNot, ConditionOperator.Changed],
        [ConditionField.Tags] = [ConditionOperator.Contains, ConditionOperator.NotContains, ConditionOperator.Changed],
        [ConditionField.Subject] = [ConditionOperator.Contains, ConditionOperator.NotContains, ConditionOperator.Changed],
        [ConditionField.HoursSinceLastActivity] = [ConditionOperator.AtLeast],
        [ConditionField.HoursSinceCreated] = [ConditionOperator.AtLeast],
    };

    public async Task<Error?> Check(AutomationTrigger trigger, RuleDefinition definition, CancellationToken cancellationToken)
    {
        for (var index = 0; index < definition.Conditions.Count; index++)
        {
            if (await CheckCondition(trigger, definition.Conditions[index], cancellationToken) is { } reason)
            {
                return AutomationErrors.InvalidCondition(index, reason);
            }
        }

        for (var index = 0; index < definition.Actions.Count; index++)
        {
            if (await CheckAction(definition.Actions[index], cancellationToken) is { } reason)
            {
                return AutomationErrors.InvalidAction(index, reason);
            }
        }

        var measuresTime = definition.HasCondition(ConditionField.HoursSinceLastActivity)
            || definition.HasCondition(ConditionField.HoursSinceCreated);
        return trigger == AutomationTrigger.TimeBased && (!measuresTime || definition.Match != ConditionMatch.All)
            ? AutomationErrors.TimeRuleNeedsDuration
            : null;
    }

    private async Task<string?> CheckCondition(AutomationTrigger trigger, RuleCondition condition, CancellationToken cancellationToken)
    {
        if (!Operators.TryGetValue(condition.Field, out var allowed) || !allowed.Contains(condition.Operator))
        {
            return "this comparison cannot be used with this field.";
        }

        if (condition.Operator == ConditionOperator.Changed)
        {
            return trigger == AutomationTrigger.TicketUpdated ? null : "\"changed\" only works when a ticket is updated.";
        }

        var value = condition.Value?.Trim() ?? string.Empty;
        return condition.Field switch
        {
            ConditionField.Status => IsEnum<TicketStatus>(value),
            ConditionField.Priority => IsEnum<TicketPriority>(value),
            ConditionField.Type => IsEnum<TicketType>(value),
            ConditionField.Channel => IsEnum<TicketChannel>(value),
            ConditionField.Assignee => value == RuleDefinition.None ? null : await IsUser(value, cancellationToken),
            ConditionField.Team => value == RuleDefinition.None ? null : await IsTeam(value, cancellationToken),
            ConditionField.Tags or ConditionField.Subject => value.Length is > 0 and <= 100 ? null : "enter a value of up to 100 characters.",
            _ => int.TryParse(value, out var hours) && hours is >= 1 and <= MaxHours ? null : $"enter a number of hours between 1 and {MaxHours}.",
        };
    }

    private async Task<string?> CheckAction(RuleAction action, CancellationToken cancellationToken)
    {
        var value = action.Value?.Trim() ?? string.Empty;
        switch (action.Type)
        {
            case RuleActionType.AssignUser:
                return await IsUser(value, cancellationToken, assignable: true);
            case RuleActionType.AssignTeam:
                return await IsTeam(value, cancellationToken);
            case RuleActionType.SetPriority:
                return IsEnum<TicketPriority>(value);
            case RuleActionType.SetStatus:
                return IsEnum<TicketStatus>(value) ?? (value == nameof(TicketStatus.New) ? "a ticket cannot be set back to new." : null);
            case RuleActionType.AddTag:
                return value.Length is > 0 and <= 40 ? null : "enter a tag of up to 40 characters.";
            case RuleActionType.SendEmail:
                return string.IsNullOrWhiteSpace(action.Subject) || action.Subject.Length > EmailSubjectMaxLength
                    ? $"enter an email subject of up to {EmailSubjectMaxLength} characters."
                    : CheckText(action.Body);
            case RuleActionType.Notify:
                var audience = value is AssigneeAudience or AdminsAudience ? null : await IsUser(value, cancellationToken);
                return audience ?? CheckText(action.Body);
            default:
                return "unknown action.";
        }
    }

    private static string? CheckText(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Length > TextMaxLength ? $"enter a message of up to {TextMaxLength} characters." : null;

    private static string? IsEnum<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _)
            ? null
            : "choose one of the listed values.";

    private async Task<string?> IsUser(string value, CancellationToken cancellationToken, bool assignable = false) =>
        Guid.TryParse(value, out var id)
        && await db.Users.AnyAsync(u => u.Id == id && u.IsActive && (!assignable || u.Role != Role.Viewer), cancellationToken)
            ? null
            : "choose an active team member.";

    private async Task<string?> IsTeam(string value, CancellationToken cancellationToken) =>
        Guid.TryParse(value, out var id) && await db.Teams.AnyAsync(t => t.Id == id, cancellationToken)
            ? null
            : "choose an existing team.";
}

public sealed record ListAutomationRulesQuery : IQuery<IReadOnlyList<AutomationRuleDto>>;

internal sealed class ListAutomationRulesHandler(IAppDbContext db)
    : IQueryHandler<ListAutomationRulesQuery, IReadOnlyList<AutomationRuleDto>>
{
    public async Task<Result<IReadOnlyList<AutomationRuleDto>>> Handle(ListAutomationRulesQuery query, CancellationToken cancellationToken)
    {
        var rules = await db.AutomationRules.AsNoTracking()
            .OrderBy(r => r.Position)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return rules.Select(AutomationRuleDto.From).ToList();
    }
}

public sealed record GetAutomationRuleQuery(Guid RuleId) : IQuery<AutomationRuleDto>;

internal sealed class GetAutomationRuleHandler(IAppDbContext db) : IQueryHandler<GetAutomationRuleQuery, AutomationRuleDto>
{
    public async Task<Result<AutomationRuleDto>> Handle(GetAutomationRuleQuery query, CancellationToken cancellationToken)
    {
        var rule = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.RuleId, cancellationToken);
        return rule is null ? AutomationErrors.RuleNotFound : AutomationRuleDto.From(rule);
    }
}

public sealed record SaveAutomationRuleCommand(
    Guid? RuleId,
    string Name,
    AutomationTrigger Trigger,
    bool IsEnabled,
    int Position,
    RuleDefinition Definition) : ICommand<AutomationRuleDto>;

internal sealed class SaveAutomationRuleValidator : AbstractValidator<SaveAutomationRuleCommand>
{
    public SaveAutomationRuleValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(AutomationRule.NameMaxLength);
        RuleFor(c => c.Trigger).IsInEnum();
        RuleFor(c => c.Position).InclusiveBetween(0, 1000);
        RuleFor(c => c.Definition).NotNull();
        When(c => c.Definition is not null, () =>
        {
            RuleFor(c => c.Definition.Match).IsInEnum();
            RuleFor(c => c.Definition.Conditions).NotNull()
                .Must(conditions => conditions is null || conditions.Count <= RuleDefinition.MaxConditions)
                .WithMessage($"A rule can have at most {RuleDefinition.MaxConditions} conditions.");
            RuleFor(c => c.Definition.Actions).NotEmpty()
                .Must(actions => actions is null || actions.Count <= RuleDefinition.MaxActions)
                .WithMessage($"A rule can have at most {RuleDefinition.MaxActions} actions.");
        });
    }
}

internal sealed class SaveAutomationRuleHandler(IAppDbContext db, RuleDefinitionChecker checker)
    : ICommandHandler<SaveAutomationRuleCommand, AutomationRuleDto>
{
    public async Task<Result<AutomationRuleDto>> Handle(SaveAutomationRuleCommand command, CancellationToken cancellationToken)
    {
        if (await checker.Check(command.Trigger, command.Definition, cancellationToken) is { } error)
        {
            return error;
        }

        AutomationRule? rule;
        if (command.RuleId is { } id)
        {
            rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (rule is null)
            {
                return AutomationErrors.RuleNotFound;
            }

            rule.Update(command.Name, command.Trigger, command.Definition, command.Position, command.IsEnabled);
        }
        else
        {
            rule = AutomationRule.Create(command.Name, command.Trigger, command.Definition, command.Position, command.IsEnabled);
            db.AutomationRules.Add(rule);
        }

        await db.SaveChangesAsync(cancellationToken);
        return AutomationRuleDto.From(rule);
    }
}

public sealed record SetAutomationRuleEnabledCommand(Guid RuleId, bool IsEnabled) : ICommand<AutomationRuleDto>;

internal sealed class SetAutomationRuleEnabledHandler(IAppDbContext db)
    : ICommandHandler<SetAutomationRuleEnabledCommand, AutomationRuleDto>
{
    public async Task<Result<AutomationRuleDto>> Handle(SetAutomationRuleEnabledCommand command, CancellationToken cancellationToken)
    {
        var rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == command.RuleId, cancellationToken);
        if (rule is null)
        {
            return AutomationErrors.RuleNotFound;
        }

        rule.SetEnabled(command.IsEnabled);
        await db.SaveChangesAsync(cancellationToken);
        return AutomationRuleDto.From(rule);
    }
}

public sealed record DeleteAutomationRuleCommand(Guid RuleId) : ICommand<Success>;

internal sealed class DeleteAutomationRuleHandler(IAppDbContext db) : ICommandHandler<DeleteAutomationRuleCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteAutomationRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = await db.AutomationRules.FirstOrDefaultAsync(r => r.Id == command.RuleId, cancellationToken);
        if (rule is null)
        {
            return AutomationErrors.RuleNotFound;
        }

        db.AutomationRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record ListAutomationExecutionsQuery(Guid RuleId, int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<AutomationExecutionDto>>, IPagedRequest;

internal sealed class ListAutomationExecutionsValidator : AbstractValidator<ListAutomationExecutionsQuery>
{
    public ListAutomationExecutionsValidator() => this.AddPagingRules();
}

internal sealed class ListAutomationExecutionsHandler(IAppDbContext db)
    : IQueryHandler<ListAutomationExecutionsQuery, PagedResult<AutomationExecutionDto>>
{
    public async Task<Result<PagedResult<AutomationExecutionDto>>> Handle(
        ListAutomationExecutionsQuery query,
        CancellationToken cancellationToken)
    {
        if (!await db.AutomationRules.AnyAsync(r => r.Id == query.RuleId, cancellationToken))
        {
            return AutomationErrors.RuleNotFound;
        }

        var page = await db.AutomationExecutions.AsNoTracking()
            .Where(e => e.RuleId == query.RuleId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToPagedAsync(query, cancellationToken);
        return new PagedResult<AutomationExecutionDto>(
            page.Items
                .Select(e => new AutomationExecutionDto(
                    e.Id, e.RuleId, e.TicketId, e.TicketNumber, e.TicketSubject, e.Outcome, e.Steps, e.CreatedAt))
                .ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }
}
