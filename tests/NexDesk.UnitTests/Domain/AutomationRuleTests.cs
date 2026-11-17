using NexDesk.Domain.Automation;
using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.UnitTests.Domain;

public sealed class AutomationRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Agent = Actor.Staff(Guid.NewGuid());
    private static readonly TicketEventType[] NoChanges = [];

    private static Ticket NewTicket() => Ticket.Open(
        7,
        "Cannot sign in",
        "It says my password is wrong.",
        Guid.NewGuid(),
        TicketPriority.High,
        TicketType.Incident,
        TicketChannel.Email,
        ["vip"],
        Agent,
        Now);

    private static RuleDefinition Rule(ConditionMatch match, params RuleCondition[] conditions) =>
        new(match, conditions, [new RuleAction(RuleActionType.AddTag, "x")]);

    [Theory]
    [InlineData(ConditionField.Priority, ConditionOperator.Is, "High", true)]
    [InlineData(ConditionField.Priority, ConditionOperator.Is, "high", true)]
    [InlineData(ConditionField.Priority, ConditionOperator.IsNot, "High", false)]
    [InlineData(ConditionField.Status, ConditionOperator.Is, "New", true)]
    [InlineData(ConditionField.Type, ConditionOperator.IsNot, "Question", true)]
    [InlineData(ConditionField.Channel, ConditionOperator.Is, "Email", true)]
    [InlineData(ConditionField.Assignee, ConditionOperator.Is, "none", true)]
    [InlineData(ConditionField.Team, ConditionOperator.IsNot, "none", false)]
    [InlineData(ConditionField.Tags, ConditionOperator.Contains, "VIP", true)]
    [InlineData(ConditionField.Tags, ConditionOperator.NotContains, "vip", false)]
    [InlineData(ConditionField.Tags, ConditionOperator.NotContains, "billing", true)]
    [InlineData(ConditionField.Subject, ConditionOperator.Contains, "SIGN IN", true)]
    [InlineData(ConditionField.Subject, ConditionOperator.NotContains, "refund", true)]
    [InlineData(ConditionField.HoursSinceLastActivity, ConditionOperator.AtLeast, "not a number", false)]
    public void A_condition_compares_one_field_of_the_ticket(
        ConditionField field,
        ConditionOperator @operator,
        string value,
        bool expected) =>
        Rule(ConditionMatch.All, new RuleCondition(field, @operator, value)).Matches(NewTicket(), NoChanges, Now).ShouldBe(expected);

    [Fact]
    public void All_needs_every_condition_and_any_needs_one()
    {
        var ticket = NewTicket();
        RuleCondition[] conditions =
        [
            new(ConditionField.Priority, ConditionOperator.Is, "High"),
            new(ConditionField.Status, ConditionOperator.Is, "Pending"),
        ];

        Rule(ConditionMatch.All, conditions).Matches(ticket, NoChanges, Now).ShouldBeFalse();
        Rule(ConditionMatch.Any, conditions).Matches(ticket, NoChanges, Now).ShouldBeTrue();
        Rule(ConditionMatch.All).Matches(ticket, NoChanges, Now).ShouldBeTrue();
        Rule(ConditionMatch.Any).Matches(ticket, NoChanges, Now).ShouldBeTrue();
    }

    [Fact]
    public void Hours_are_measured_from_the_last_activity()
    {
        var ticket = NewTicket();
        var idle = Rule(ConditionMatch.All, new RuleCondition(ConditionField.HoursSinceLastActivity, ConditionOperator.AtLeast, "48"));

        idle.Matches(ticket, NoChanges, Now.AddHours(47.9)).ShouldBeFalse();
        idle.Matches(ticket, NoChanges, Now.AddHours(48)).ShouldBeTrue();

        ticket.ChangeStatus(TicketStatus.Pending, Agent, Now.AddHours(40));
        idle.Matches(ticket, NoChanges, Now.AddHours(48)).ShouldBeFalse();
        idle.Matches(ticket, NoChanges, Now.AddHours(88)).ShouldBeTrue();
    }

    [Fact]
    public void Changed_looks_at_what_the_triggering_update_touched()
    {
        var ticket = NewTicket();
        var statusChanged = Rule(ConditionMatch.All, new RuleCondition(ConditionField.Status, ConditionOperator.Changed));

        statusChanged.Matches(ticket, [TicketEventType.PriorityChanged], Now).ShouldBeFalse();
        statusChanged.Matches(ticket, [TicketEventType.PriorityChanged, TicketEventType.StatusChanged], Now).ShouldBeTrue();
        Rule(ConditionMatch.All, new RuleCondition(ConditionField.Channel, ConditionOperator.Changed))
            .Matches(ticket, [TicketEventType.StatusChanged], Now).ShouldBeFalse();
    }

    [Fact]
    public void An_update_reports_who_made_it_and_everything_it_changed()
    {
        var ticket = NewTicket();
        ticket.DomainEvents.OfType<TicketChanged>().ShouldBeEmpty();
        ticket.ClearDomainEvents();

        ticket.ChangePriority(TicketPriority.Urgent, Agent, Now);
        ticket.ChangeStatus(TicketStatus.Open, Agent, Now);
        ticket.SetTags(["vip", "billing"], Agent, Now);

        var changed = ticket.DomainEvents.OfType<TicketChanged>().ShouldHaveSingleItem();
        changed.Actor.ShouldBe(Agent);
        changed.Changes.ShouldBe(
            [TicketEventType.PriorityChanged, TicketEventType.StatusChanged, TicketEventType.TagsChanged]);
    }

    [Fact]
    public void An_execution_is_partial_when_only_some_actions_could_be_carried_out()
    {
        var rule = AutomationRule.Create("Rule", AutomationTrigger.TicketCreated, Rule(ConditionMatch.All), 0, true);
        var ticket = NewTicket();
        var done = new ExecutionStep(RuleActionType.AddTag, "x", true);
        var skipped = new ExecutionStep(RuleActionType.AssignUser, "gone", false);

        AutomationExecution.Record(rule, ticket, [done, done], Now).Outcome.ShouldBe(ExecutionOutcome.Applied);
        AutomationExecution.Record(rule, ticket, [done, skipped], Now).Outcome.ShouldBe(ExecutionOutcome.Partial);
        AutomationExecution.Record(rule, ticket, [skipped], Now).Outcome.ShouldBe(ExecutionOutcome.Failed);
    }
}
