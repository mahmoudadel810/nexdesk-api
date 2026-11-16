using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record BulkUpdateTicketsCommand(
    IReadOnlyCollection<Guid> TicketIds,
    TicketStatus? Status,
    TicketPriority? Priority,
    bool SetAssignee,
    Guid? AssigneeId,
    bool SetTeam,
    Guid? TeamId,
    IReadOnlyCollection<string>? AddTags,
    IReadOnlyCollection<string>? RemoveTags) : ICommand<BulkResult>;

public sealed record BulkFailure(Guid TicketId, string Code);

public sealed record BulkResult(int Updated, IReadOnlyList<BulkFailure> Failed);

internal sealed class BulkUpdateTicketsValidator : AbstractValidator<BulkUpdateTicketsCommand>
{
    public const int MaxTickets = 100;

    public BulkUpdateTicketsValidator()
    {
        RuleFor(c => c.TicketIds)
            .NotEmpty()
            .Must(ids => ids is null || ids.Count <= MaxTickets)
            .WithMessage($"Select at most {MaxTickets} tickets.");
        RuleFor(c => c.Status!.Value).IsInEnum().When(c => c.Status is not null);
        RuleFor(c => c.Priority!.Value).IsInEnum().When(c => c.Priority is not null);
        RuleFor(c => c.AddTags).ValidTags();
        RuleFor(c => c.RemoveTags).ValidTags();
        RuleFor(c => c)
            .Must(c => c.Status is not null || c.Priority is not null || c.SetAssignee || c.SetTeam
                || c.AddTags is { Count: > 0 } || c.RemoveTags is { Count: > 0 })
            .WithMessage("Choose at least one change to apply.")
            .OverridePropertyName("changes");
    }
}

internal sealed class BulkUpdateTicketsHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    TicketWriter writer,
    TimeProvider clock) : ICommandHandler<BulkUpdateTicketsCommand, BulkResult>
{
    public async Task<Result<BulkResult>> Handle(BulkUpdateTicketsCommand command, CancellationToken cancellationToken)
    {
        var assignment = await writer.ResolveAssignment(
            command.SetAssignee ? command.AssigneeId : null,
            command.SetTeam ? command.TeamId : null,
            cancellationToken);
        if (!assignment.IsSuccess)
        {
            return assignment.Error;
        }

        var updated = 0;
        var failed = new List<BulkFailure>();
        foreach (var ticketId in command.TicketIds.Distinct())
        {
            // Each ticket is saved on its own, so one closed or missing ticket does not block the rest.
            var result = await Concurrency.RetryAsync(
                db,
                () => Apply(ticketId, command, assignment.Value, cancellationToken));
            if (result.IsSuccess)
            {
                updated++;
            }
            else
            {
                failed.Add(new BulkFailure(ticketId, result.Error.Code));
            }

            db.DiscardChanges();
        }

        return new BulkResult(updated, failed);
    }

    private async Task<Result<Success>> Apply(
        Guid ticketId,
        BulkUpdateTicketsCommand command,
        (string? AssigneeName, string? TeamName) names,
        CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(t => t.Watchers)
            .FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        if (ticket.IsClosed)
        {
            return TicketErrors.Closed;
        }

        var actor = currentUser.AsActor();
        var now = clock.GetUtcNow();
        if (command.Priority is { } priority)
        {
            ticket.ChangePriority(priority, actor, now);
        }

        if (command.AddTags is { Count: > 0 } || command.RemoveTags is { Count: > 0 })
        {
            var remove = Ticket.NormalizeTags(command.RemoveTags);
            ticket.SetTags(ticket.Tags.Concat(command.AddTags ?? []).Except(remove), actor, now);
        }

        if (command.SetTeam)
        {
            var previous = ticket.TeamId is { } teamId
                ? await db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
                : null;
            ticket.RouteTo(command.TeamId, names.TeamName, previous, actor, now);
        }

        if (command.SetAssignee)
        {
            var previous = ticket.AssigneeId is { } assigneeId
                ? await db.Users.Where(u => u.Id == assigneeId).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken)
                : null;
            ticket.AssignTo(command.AssigneeId, names.AssigneeName, previous, actor, now);
        }

        if (command.Status is { } status)
        {
            ticket.ChangeStatus(status, actor, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record MergeTicketCommand(Guid TicketId, Guid TargetTicketId) : ICommand<TicketDetailDto>;

internal sealed class MergeTicketHandler(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<MergeTicketCommand, TicketDetailDto>
{
    public Task<Result<TicketDetailDto>> Handle(MergeTicketCommand command, CancellationToken cancellationToken) =>
        Concurrency.RetryAsync(db, () => Attempt(command, cancellationToken));

    private async Task<Result<TicketDetailDto>> Attempt(MergeTicketCommand command, CancellationToken cancellationToken)
    {
        if (command.TicketId == command.TargetTicketId)
        {
            return TicketErrors.MergeIntoSelf;
        }

        var tickets = await db.Tickets.Include(t => t.Watchers)
            .Where(t => t.Id == command.TicketId || t.Id == command.TargetTicketId)
            .ToListAsync(cancellationToken);
        var duplicate = tickets.FirstOrDefault(t => t.Id == command.TicketId);
        var target = tickets.FirstOrDefault(t => t.Id == command.TargetTicketId);
        if (duplicate is null || target is null)
        {
            return TicketErrors.NotFound;
        }

        if (duplicate.IsClosed || target.IsClosed)
        {
            return TicketErrors.Closed;
        }

        var messages = await db.TicketMessages.Where(m => m.TicketId == duplicate.Id).ToListAsync(cancellationToken);
        var attachments = await db.Attachments.Where(a => a.TicketId == duplicate.Id).ToListAsync(cancellationToken);
        target.Absorb(duplicate, messages, attachments, currentUser.AsActor(), clock.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);
        return (await TicketQueries.LoadDetail(db, target.Id, currentUser.UserId, cancellationToken))!;
    }
}
