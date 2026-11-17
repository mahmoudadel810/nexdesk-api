using NexDesk.Api.Auth;
using NexDesk.Application.Automation;
using NexDesk.Application.Common;
using NexDesk.Domain.Automation;
using NexDesk.Domain.Identity;

namespace NexDesk.Api.Endpoints;

public sealed record SaveAutomationRuleRequest(
    string Name,
    AutomationTrigger Trigger,
    RuleDefinition Definition,
    bool IsEnabled = true,
    int Position = 0);

public sealed record ListAutomationExecutionsRequest(int Page = 1, int PageSize = Paging.DefaultPageSize);

public static class AutomationEndpoints
{
    public static RouteGroupBuilder MapAutomationEndpoints(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/automation/rules")
            .WithTags("Automation")
            .RequirePermission(Permissions.AutomationManage);

        rules.MapGet("/", async (
            IQueryHandler<ListAutomationRulesQuery, IReadOnlyList<AutomationRuleDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListAutomationRulesQuery(), ct)).ToOk())
            .WithName("ListAutomationRules")
            .WithSummary("Lists the workspace's automation rules in the order they run.")
            .Produces<IReadOnlyList<AutomationRuleDto>>();

        rules.MapGet("/{id:guid}", async (
            Guid id,
            IQueryHandler<GetAutomationRuleQuery, AutomationRuleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new GetAutomationRuleQuery(id), ct)).ToOk())
            .WithName("GetAutomationRule")
            .WithSummary("Returns one rule with its conditions and actions.")
            .Produces<AutomationRuleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapPost("/", async (
            SaveAutomationRuleRequest request,
            ICommandHandler<SaveAutomationRuleCommand, AutomationRuleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new SaveAutomationRuleCommand(
                    null, request.Name, request.Trigger, request.IsEnabled, request.Position, request.Definition),
                ct))
            .ToHttp(rule => TypedResults.Created($"/api/v1/automation/rules/{rule.Id}", rule)))
            .WithName("CreateAutomationRule")
            .WithSummary("Adds a rule: a trigger, conditions joined by all/any, and the actions to carry out.")
            .Produces<AutomationRuleDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        rules.MapPut("/{id:guid}", async (
            Guid id,
            SaveAutomationRuleRequest request,
            ICommandHandler<SaveAutomationRuleCommand, AutomationRuleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(
                new SaveAutomationRuleCommand(
                    id, request.Name, request.Trigger, request.IsEnabled, request.Position, request.Definition),
                ct))
            .ToOk())
            .WithName("UpdateAutomationRule")
            .WithSummary("Replaces a rule's trigger, conditions and actions.")
            .Produces<AutomationRuleDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapPost("/{id:guid}/enable", async (
            Guid id,
            ICommandHandler<SetAutomationRuleEnabledCommand, AutomationRuleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetAutomationRuleEnabledCommand(id, true), ct)).ToOk())
            .WithName("EnableAutomationRule")
            .WithSummary("Turns a rule on.")
            .Produces<AutomationRuleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapPost("/{id:guid}/disable", async (
            Guid id,
            ICommandHandler<SetAutomationRuleEnabledCommand, AutomationRuleDto> handler,
            CancellationToken ct) =>
            (await handler.Handle(new SetAutomationRuleEnabledCommand(id, false), ct)).ToOk())
            .WithName("DisableAutomationRule")
            .WithSummary("Turns a rule off without deleting it or its log.")
            .Produces<AutomationRuleDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapDelete("/{id:guid}", async (
            Guid id,
            ICommandHandler<DeleteAutomationRuleCommand, Success> handler,
            CancellationToken ct) =>
            (await handler.Handle(new DeleteAutomationRuleCommand(id), ct)).ToNoContent())
            .WithName("DeleteAutomationRule")
            .WithSummary("Deletes a rule and its execution log.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapGet("/{id:guid}/executions", async (
            Guid id,
            [AsParameters] ListAutomationExecutionsRequest request,
            IQueryHandler<ListAutomationExecutionsQuery, PagedResult<AutomationExecutionDto>> handler,
            CancellationToken ct) =>
            (await handler.Handle(new ListAutomationExecutionsQuery(id, request.Page, request.PageSize), ct)).ToOk())
            .WithName("ListAutomationExecutions")
            .WithSummary("Lists when the rule ran, on which ticket and what became of each action, newest first.")
            .Produces<PagedResult<AutomationExecutionDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }
}
