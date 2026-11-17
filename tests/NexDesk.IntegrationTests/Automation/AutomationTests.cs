using System.Net;
using System.Net.Http.Json;
using NexDesk.Api.Endpoints;
using NexDesk.Application.Automation;
using NexDesk.Application.Common;
using NexDesk.Application.Notifications;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Tickets;
using NexDesk.IntegrationTests.Infrastructure;

namespace NexDesk.IntegrationTests.Automation;

[Collection(ApiCollection.Name)]
public sealed class AutomationTests(ApiFactory factory)
{
    private const string Rules = "/api/v1/automation/rules";

    private static RuleDefinition When(ConditionMatch match, RuleCondition[] conditions, params RuleAction[] actions) =>
        new(match, conditions, actions);

    private static async Task<AutomationRuleDto> CreateRuleAsync(
        HttpClient client,
        string name,
        AutomationTrigger trigger,
        RuleDefinition definition,
        int position = 0,
        bool isEnabled = true)
    {
        var response = await client.PostAsJsonAsync(
            Rules, new SaveAutomationRuleRequest(name, trigger, definition, isEnabled, position), TestJson.Options);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<AutomationRuleDto>();
    }

    private static Task<PagedResult<AutomationExecutionDto>> Log(HttpClient client, Guid ruleId) =>
        client.GetAsync<PagedResult<AutomationExecutionDto>>($"{Rules}/{ruleId}/executions");

    private static async Task<TicketDetailDto> UpdateAsync(HttpClient client, Guid ticketId, UpdateTicketRequest request)
    {
        var response = await client.PatchAsJsonAsync($"/api/v1/tickets/{ticketId}", request, TestJson.Options);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<TicketDetailDto>();
    }

    [Fact]
    public async Task A_rule_for_new_tickets_routes_tags_and_notifies_and_logs_what_it_did()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(workspace, Role.Agent);
        var rule = await CreateRuleAsync(
            workspace.Client,
            "Urgent tickets go to Aaron",
            AutomationTrigger.TicketCreated,
            When(
                ConditionMatch.All,
                [new RuleCondition(ConditionField.Priority, ConditionOperator.Is, "Urgent")],
                new RuleAction(RuleActionType.AssignUser, agent.UserId.ToString()),
                new RuleAction(RuleActionType.AddTag, "VIP"),
                new RuleAction(RuleActionType.Notify, "assignee", Body: "Urgent: {{ticket.subject}}")));

        var ignored = await workspace.Client.CreateTicketAsync(TestTickets.NewTicket(priority: TicketPriority.Low));
        var urgent = await workspace.Client.CreateTicketAsync(
            TestTickets.NewTicket("Checkout is down", priority: TicketPriority.Urgent));

        var after = await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{urgent.Id}");
        after.Assignee!.Id.ShouldBe(agent.UserId);
        after.Tags.ShouldBe(["vip"]);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ignored.Id}")).Assignee.ShouldBeNull();

        var inbox = await agent.Client.GetAsync<PagedResult<NotificationDto>>("/api/v1/notifications");
        var alert = inbox.Items.Single(n => n.Type == NotificationType.Automation);
        alert.ActorName.ShouldBe("Urgent tickets go to Aaron");
        alert.Preview.ShouldBe("Urgent: Checkout is down");
        inbox.Items.ShouldContain(n => n.Type == NotificationType.TicketAssigned && n.TicketId == urgent.Id);

        var entry = (await Log(workspace.Client, rule.Id)).Items.ShouldHaveSingleItem();
        entry.TicketId.ShouldBe(urgent.Id);
        entry.TicketNumber.ShouldBe(urgent.Number);
        entry.Outcome.ShouldBe(ExecutionOutcome.Applied);
        entry.Steps.ShouldBe(
        [
            new ExecutionStep(RuleActionType.AssignUser, "Aaron Agent", true),
            new ExecutionStep(RuleActionType.AddTag, "vip", true),
            new ExecutionStep(RuleActionType.Notify, "assignee", true),
        ]);

        var listed = (await workspace.Client.GetAsync<List<AutomationRuleDto>>(Rules)).ShouldHaveSingleItem();
        listed.RunCount.ShouldBe(1);
        listed.LastRunAt.ShouldNotBeNull();
        listed.Definition.Actions.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_rule_for_updates_can_react_to_what_changed_and_email_the_customer()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var rule = await CreateRuleAsync(
            workspace.Client,
            "Tell the customer we are waiting",
            AutomationTrigger.TicketUpdated,
            When(
                ConditionMatch.All,
                [
                    new RuleCondition(ConditionField.Status, ConditionOperator.Changed),
                    new RuleCondition(ConditionField.Status, ConditionOperator.Is, "Pending"),
                ],
                new RuleAction(
                    RuleActionType.SendEmail,
                    Subject: "We need more information",
                    Body: "Hi {{customer.name}}, request #{{ticket.number}} is waiting for your reply.")));
        var request = TestTickets.NewTicket();
        var ticket = await workspace.Client.CreateTicketAsync(request);

        await UpdateAsync(workspace.Client, ticket.Id, new UpdateTicketRequest(null, null, TicketPriority.High, null, null));
        factory.Emails.Count(request.RequesterEmail!, "automation-message").ShouldBe(0);

        await UpdateAsync(workspace.Client, ticket.Id, new UpdateTicketRequest(null, TicketStatus.Pending, null, null, null));
        var email = factory.Emails.Last(request.RequesterEmail!, "automation-message");
        email.Rendered.Subject.ShouldBe($"We need more information [#{ticket.Number}]");
        email.Rendered.Text.ShouldContain($"Hi Casey Customer, request #{ticket.Number} is waiting for your reply.");

        // The status did not change this time, so the rule stays quiet.
        await UpdateAsync(workspace.Client, ticket.Id, new UpdateTicketRequest("Still cannot sign in", null, null, null, null));
        factory.Emails.Count(request.RequesterEmail!, "automation-message").ShouldBe(1);
        (await Log(workspace.Client, rule.Id)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Rules_never_trigger_each_other_or_themselves_in_a_loop()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        RuleCondition[] always = [];
        var touch = await CreateRuleAsync(
            workspace.Client,
            "Touch every update",
            AutomationTrigger.TicketUpdated,
            When(ConditionMatch.All, always, new RuleAction(RuleActionType.SetPriority, "High")),
            position: 0);
        var up = await CreateRuleAsync(
            workspace.Client,
            "High becomes urgent",
            AutomationTrigger.TicketUpdated,
            When(
                ConditionMatch.All,
                [new RuleCondition(ConditionField.Priority, ConditionOperator.Is, "High")],
                new RuleAction(RuleActionType.SetPriority, "Urgent")),
            position: 1);
        var down = await CreateRuleAsync(
            workspace.Client,
            "Urgent becomes high",
            AutomationTrigger.TicketUpdated,
            When(
                ConditionMatch.All,
                [new RuleCondition(ConditionField.Priority, ConditionOperator.Is, "Urgent")],
                new RuleAction(RuleActionType.SetPriority, "High")),
            position: 2);
        var ticket = await workspace.Client.CreateTicketAsync();

        await UpdateAsync(workspace.Client, ticket.Id, new UpdateTicketRequest("Cannot sign in at all", null, null, null, null));

        // One pass: each rule ran once, saw what the rules before it did, and nothing ran again afterwards.
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).Priority.ShouldBe(TicketPriority.High);
        foreach (var rule in new[] { touch, up, down })
        {
            (await Log(workspace.Client, rule.Id)).TotalCount.ShouldBe(1);
        }

        (await workspace.Client.GetAsync<List<AutomationRuleDto>>(Rules)).ShouldAllBe(r => r.RunCount == 1);
    }

    [Fact]
    public async Task A_time_based_rule_handles_a_ticket_once_per_quiet_period()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var solve = await CreateRuleAsync(
            workspace.Client,
            "Solve requests that wait two days",
            AutomationTrigger.TimeBased,
            When(
                ConditionMatch.All,
                [
                    new RuleCondition(ConditionField.Status, ConditionOperator.Is, "Pending"),
                    new RuleCondition(ConditionField.HoursSinceLastActivity, ConditionOperator.AtLeast, "48"),
                ],
                new RuleAction(RuleActionType.SetStatus, "Solved")));
        var nudge = await CreateRuleAsync(
            workspace.Client,
            "Nudge admins about old tickets",
            AutomationTrigger.TimeBased,
            When(
                ConditionMatch.All,
                [
                    new RuleCondition(ConditionField.Status, ConditionOperator.Is, "Open"),
                    new RuleCondition(ConditionField.HoursSinceLastActivity, ConditionOperator.AtLeast, "24"),
                ],
                new RuleAction(RuleActionType.Notify, "admins", Body: "Nobody touched this for a day.")),
            position: 1);
        var waiting = await workspace.Client.CreateTicketAsync();
        var open = await workspace.Client.CreateTicketAsync();
        await UpdateAsync(workspace.Client, waiting.Id, new UpdateTicketRequest(null, TicketStatus.Pending, null, null, null));
        await UpdateAsync(workspace.Client, open.Id, new UpdateTicketRequest(null, TicketStatus.Open, null, null, null));
        var start = DateTimeOffset.UtcNow;

        (await factory.SweepAutomationAsync(workspace.TenantId, start.AddHours(23))).ShouldBe(0);

        // After a day the open ticket is flagged; flagging does not change it, so it is flagged only once.
        (await factory.SweepAutomationAsync(workspace.TenantId, start.AddHours(25))).ShouldBe(1);
        (await factory.SweepAutomationAsync(workspace.TenantId, start.AddHours(26))).ShouldBe(0);
        (await Log(workspace.Client, nudge.Id)).Items.ShouldHaveSingleItem().TicketId.ShouldBe(open.Id);

        (await factory.SweepAutomationAsync(workspace.TenantId, start.AddHours(49))).ShouldBe(1);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{waiting.Id}")).Status.ShouldBe(TicketStatus.Solved);
        (await factory.SweepAutomationAsync(workspace.TenantId, start.AddHours(50))).ShouldBe(0);
        (await Log(workspace.Client, solve.Id)).Items.ShouldHaveSingleItem().Steps
            .ShouldBe([new ExecutionStep(RuleActionType.SetStatus, "Solved", true)]);
    }

    [Fact]
    public async Task Disabled_rules_do_not_run_and_deleting_a_rule_removes_its_log()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        RuleCondition[] always = [];
        var rule = await CreateRuleAsync(
            workspace.Client,
            "Tag everything",
            AutomationTrigger.TicketCreated,
            When(ConditionMatch.All, always, new RuleAction(RuleActionType.AddTag, "new")),
            isEnabled: false);

        (await workspace.Client.CreateTicketAsync()).Tags.ShouldBeEmpty();
        (await Log(workspace.Client, rule.Id)).TotalCount.ShouldBe(0);

        (await (await workspace.Client.PostAsync($"{Rules}/{rule.Id}/enable", null)).ReadAsync<AutomationRuleDto>())
            .IsEnabled.ShouldBeTrue();
        var ticket = await workspace.Client.CreateTicketAsync();
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).Tags.ShouldBe(["new"]);

        var edited = await (await workspace.Client.PutAsJsonAsync(
            $"{Rules}/{rule.Id}",
            new SaveAutomationRuleRequest(
                "Tag everything twice",
                AutomationTrigger.TicketCreated,
                When(ConditionMatch.Any, always, new RuleAction(RuleActionType.AddTag, "new"), new RuleAction(RuleActionType.AddTag, "inbox")),
                IsEnabled: false,
                Position: 4),
            TestJson.Options)).ReadAsync<AutomationRuleDto>();
        (edited.Name, edited.IsEnabled, edited.Position, edited.RunCount).ShouldBe(("Tag everything twice", false, 4, 1));
        (await workspace.Client.GetAsync<AutomationRuleDto>($"{Rules}/{rule.Id}")).Definition.Actions.Count.ShouldBe(2);

        (await workspace.Client.DeleteAsync($"{Rules}/{rule.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workspace.Client.GetAsync($"{Rules}/{rule.Id}/executions")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await workspace.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ticket.Id}")).Tags.ShouldBe(["new"]);
    }

    [Fact]
    public async Task Rules_that_cannot_work_are_rejected_with_a_reason()
    {
        var workspace = await factory.CreateWorkspaceAsync();
        var other = await factory.CreateWorkspaceAsync();
        var tag = new RuleAction(RuleActionType.AddTag, "x");
        RuleCondition[] none = [];

        async Task Rejected(AutomationTrigger trigger, RuleDefinition definition, string code)
        {
            var response = await workspace.Client.PostAsJsonAsync(
                Rules, new SaveAutomationRuleRequest("Broken", trigger, definition), TestJson.Options);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            (await response.ProblemCodeAsync()).ShouldBe(code);
        }

        await Rejected(AutomationTrigger.TicketCreated, When(ConditionMatch.All, none), "validation");
        await Rejected(
            AutomationTrigger.TimeBased,
            When(ConditionMatch.All, [new RuleCondition(ConditionField.Status, ConditionOperator.Is, "Pending")], tag),
            "automation-time-rule-needs-duration");
        await Rejected(
            AutomationTrigger.TimeBased,
            When(ConditionMatch.Any, [new RuleCondition(ConditionField.HoursSinceCreated, ConditionOperator.AtLeast, "4")], tag),
            "automation-time-rule-needs-duration");
        await Rejected(
            AutomationTrigger.TicketCreated,
            When(ConditionMatch.All, [new RuleCondition(ConditionField.Status, ConditionOperator.Changed)], tag),
            "automation-invalid-condition");
        await Rejected(
            AutomationTrigger.TicketUpdated,
            When(ConditionMatch.All, [new RuleCondition(ConditionField.Status, ConditionOperator.Is, "Asleep")], tag),
            "automation-invalid-condition");
        await Rejected(
            AutomationTrigger.TicketUpdated,
            When(ConditionMatch.All, [new RuleCondition(ConditionField.Subject, ConditionOperator.AtLeast, "3")], tag),
            "automation-invalid-condition");
        await Rejected(
            AutomationTrigger.TimeBased,
            When(ConditionMatch.All, [new RuleCondition(ConditionField.HoursSinceCreated, ConditionOperator.AtLeast, "0")], tag),
            "automation-invalid-condition");
        await Rejected(
            AutomationTrigger.TicketCreated,
            When(ConditionMatch.All, none, new RuleAction(RuleActionType.SetStatus, "New")),
            "automation-invalid-action");
        await Rejected(
            AutomationTrigger.TicketCreated,
            When(ConditionMatch.All, none, new RuleAction(RuleActionType.SendEmail, Subject: "Hello")),
            "automation-invalid-action");

        // People and teams of another workspace do not exist as far as this one is concerned.
        await Rejected(
            AutomationTrigger.TicketCreated,
            When(ConditionMatch.All, none, new RuleAction(RuleActionType.AssignUser, other.OwnerId.ToString())),
            "automation-invalid-action");
        await Rejected(
            AutomationTrigger.TicketCreated,
            When(ConditionMatch.All, none, new RuleAction(RuleActionType.Notify, other.OwnerId.ToString(), Body: "Hi")),
            "automation-invalid-action");

        (await workspace.Client.GetAsync<List<AutomationRuleDto>>(Rules)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_admins_manage_rules_and_a_workspace_never_touches_another_workspaces_rules_or_tickets()
    {
        var acme = await factory.CreateWorkspaceAsync();
        var globex = await factory.CreateWorkspaceAsync();
        var agent = await factory.AddMemberAsync(acme, Role.Agent);
        RuleCondition[] always = [];
        var definition = When(ConditionMatch.All, always, new RuleAction(RuleActionType.AddTag, "acme"));
        var rule = await CreateRuleAsync(acme.Client, "Tag everything", AutomationTrigger.TicketCreated, definition);
        var timed = await CreateRuleAsync(
            acme.Client,
            "Solve old tickets",
            AutomationTrigger.TimeBased,
            When(
                ConditionMatch.All,
                [new RuleCondition(ConditionField.HoursSinceCreated, ConditionOperator.AtLeast, "1")],
                new RuleAction(RuleActionType.SetStatus, "Solved")));

        (await agent.Client.GetAsync(Rules)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await agent.Client.PostAsJsonAsync(
            Rules, new SaveAutomationRuleRequest("Mine", AutomationTrigger.TicketCreated, definition), TestJson.Options))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await factory.CreateBareClient().GetAsync(Rules)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await globex.Client.GetAsync<List<AutomationRuleDto>>(Rules)).ShouldBeEmpty();
        (await globex.Client.GetAsync($"{Rules}/{rule.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.GetAsync($"{Rules}/{rule.Id}/executions")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.PutAsJsonAsync(
            $"{Rules}/{rule.Id}",
            new SaveAutomationRuleRequest("Hijacked", AutomationTrigger.TicketCreated, definition),
            TestJson.Options)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.PostAsync($"{Rules}/{rule.Id}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globex.Client.DeleteAsync($"{Rules}/{rule.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Acme's rules act on Acme's tickets only, for events and for the scheduled sweep alike.
        var theirs = await globex.Client.CreateTicketAsync();
        var ours = await acme.Client.CreateTicketAsync();
        (await globex.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{theirs.Id}")).Tags.ShouldBeEmpty();
        (await acme.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ours.Id}")).Tags.ShouldBe(["acme"]);

        var later = DateTimeOffset.UtcNow.AddHours(2);
        (await factory.SweepAutomationAsync(globex.TenantId, later)).ShouldBe(0);
        (await factory.SweepAutomationAsync(acme.TenantId, later)).ShouldBe(1);
        (await globex.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{theirs.Id}")).Status.ShouldBe(TicketStatus.New);
        (await acme.Client.GetAsync<TicketDetailDto>($"/api/v1/tickets/{ours.Id}")).Status.ShouldBe(TicketStatus.Solved);
        (await Log(acme.Client, timed.Id)).Items.ShouldHaveSingleItem().TicketId.ShouldBe(ours.Id);
        (await acme.Client.GetAsync<AutomationRuleDto>($"{Rules}/{rule.Id}")).Name.ShouldBe("Tag everything");
    }
}
