using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

internal static class TicketRules
{
    public static IRuleBuilderOptions<T, IReadOnlyCollection<string>?> ValidTags<T>(
        this IRuleBuilder<T, IReadOnlyCollection<string>?> rule) =>
        rule.Must(tags => tags is null
                || (tags.Count <= Ticket.MaxTags && tags.All(tag => tag is not null && tag.Trim().Length <= Ticket.TagMaxLength)))
            .WithMessage($"Use at most {Ticket.MaxTags} tags of up to {Ticket.TagMaxLength} characters each.");
}

public sealed record CreateTicketCommand(
    string Subject,
    string Description,
    Guid? RequesterId,
    string? RequesterEmail,
    string? RequesterName,
    TicketPriority Priority,
    TicketType Type,
    Guid? AssigneeId,
    Guid? TeamId,
    IReadOnlyCollection<string>? Tags,
    IReadOnlyCollection<Guid>? AttachmentIds,
    IReadOnlyDictionary<string, string?>? CustomFields = null) : ICommand<TicketDetailDto>;

internal sealed class CreateTicketValidator : AbstractValidator<CreateTicketCommand>
{
    public CreateTicketValidator()
    {
        RuleFor(c => c.Subject).NotEmpty().MaximumLength(Ticket.SubjectMaxLength);
        RuleFor(c => c.Description).NotEmpty().MaximumLength(TicketMessage.BodyMaxLength);
        RuleFor(c => c.RequesterEmail!)
            .EmailAddress()
            .MaximumLength(User.EmailMaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.RequesterEmail));
        RuleFor(c => c.RequesterName).MaximumLength(Customer.FullNameMaxLength);
        RuleFor(c => c.Priority).IsInEnum();
        RuleFor(c => c.Type).IsInEnum();
        RuleFor(c => c.Tags).ValidTags();
    }
}

internal sealed class CreateTicketHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    TicketWriter writer,
    CustomFieldValues customFields) : ICommandHandler<CreateTicketCommand, TicketDetailDto>
{
    public async Task<Result<TicketDetailDto>> Handle(CreateTicketCommand command, CancellationToken cancellationToken)
    {
        var fields = await customFields.ValidateAsync(
            command.CustomFields, enforceRequired: true, existing: null, cancellationToken);
        if (!fields.IsSuccess)
        {
            return fields.Error;
        }

        var draft = new TicketDraft(
            command.Subject,
            command.Description,
            command.RequesterId,
            command.RequesterEmail,
            command.RequesterName,
            command.Priority,
            command.Type,
            command.AssigneeId,
            command.TeamId,
            command.Tags,
            command.AttachmentIds,
            fields.Value.Values);
        var actor = currentUser.AsActor();
        var channel = actor.Type == ActorType.ApiKey ? TicketChannel.Api : TicketChannel.Agent;
        var created = await writer.CreateAsync(draft, actor, channel, cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await TicketQueries.LoadDetail(db, created.Value.Id, currentUser.UserId, cancellationToken))!;
    }
}

public sealed record UpdateTicketCommand(
    Guid TicketId,
    string? Subject,
    TicketStatus? Status,
    TicketPriority? Priority,
    TicketType? Type,
    IReadOnlyCollection<string>? Tags,
    IReadOnlyDictionary<string, string?>? CustomFields = null,
    uint? Version = null) : ICommand<TicketDetailDto>;

internal sealed class UpdateTicketValidator : AbstractValidator<UpdateTicketCommand>
{
    public UpdateTicketValidator()
    {
        RuleFor(c => c.Subject!).NotEmpty().MaximumLength(Ticket.SubjectMaxLength).When(c => c.Subject is not null);
        RuleFor(c => c.Status!.Value).IsInEnum().When(c => c.Status is not null);
        RuleFor(c => c.Priority!.Value).IsInEnum().When(c => c.Priority is not null);
        RuleFor(c => c.Type!.Value).IsInEnum().When(c => c.Type is not null);
        RuleFor(c => c.Tags).ValidTags();
    }
}

internal sealed class UpdateTicketHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    CustomFieldValues customFields,
    TimeProvider clock) : ICommandHandler<UpdateTicketCommand, TicketDetailDto>
{
    public async Task<Result<TicketDetailDto>> Handle(UpdateTicketCommand command, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        if (command.Version is { } version && version != ticket.Version)
        {
            return TicketErrors.Stale;
        }

        if (ticket.IsClosed)
        {
            return TicketErrors.Closed;
        }

        var actor = currentUser.AsActor();
        var now = clock.GetUtcNow();
        if (command.CustomFields is not null)
        {
            var fields = await customFields.ValidateAsync(
                command.CustomFields, enforceRequired: true, ticket.CustomFields, cancellationToken);
            if (!fields.IsSuccess)
            {
                return fields.Error;
            }

            ticket.SetCustomFields(fields.Value.Values, fields.Value.Labels, actor, now);
        }

        if (command.Subject is not null)
        {
            ticket.Rename(command.Subject, actor, now);
        }

        if (command.Priority is { } priority)
        {
            ticket.ChangePriority(priority, actor, now);
        }

        if (command.Type is { } type)
        {
            ticket.ChangeType(type, actor, now);
        }

        if (command.Tags is not null)
        {
            ticket.SetTags(command.Tags, actor, now);
        }

        // Status goes last so a ticket can be edited and closed in one request.
        if (command.Status is { } status)
        {
            ticket.ChangeStatus(status, actor, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TicketErrors.Stale;
        }

        return (await TicketQueries.LoadDetail(db, ticket.Id, currentUser.UserId, cancellationToken))!;
    }
}

public sealed record AssignTicketCommand(Guid TicketId, Guid? AssigneeId, Guid? TeamId, uint? Version = null)
    : ICommand<TicketDetailDto>;

internal sealed class AssignTicketHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    TicketWriter writer,
    TimeProvider clock) : ICommandHandler<AssignTicketCommand, TicketDetailDto>
{
    public async Task<Result<TicketDetailDto>> Handle(AssignTicketCommand command, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(t => t.Watchers)
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        if (command.Version is { } version && version != ticket.Version)
        {
            return TicketErrors.Stale;
        }

        if (ticket.IsClosed)
        {
            return TicketErrors.Closed;
        }

        var assignment = await writer.ResolveAssignment(command.AssigneeId, command.TeamId, cancellationToken);
        if (!assignment.IsSuccess)
        {
            return assignment.Error;
        }

        var previousAssignee = ticket.AssigneeId is { } assigneeId
            ? await db.Users.Where(u => u.Id == assigneeId).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken)
            : null;
        var previousTeam = ticket.TeamId is { } teamId
            ? await db.Teams.Where(t => t.Id == teamId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        var actor = currentUser.AsActor();
        var now = clock.GetUtcNow();
        ticket.RouteTo(command.TeamId, assignment.Value.TeamName, previousTeam, actor, now);
        ticket.AssignTo(command.AssigneeId, assignment.Value.AssigneeName, previousAssignee, actor, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TicketErrors.Stale;
        }

        return (await TicketQueries.LoadDetail(db, ticket.Id, currentUser.UserId, cancellationToken))!;
    }
}

public sealed record AddTicketMessageCommand(
    Guid TicketId,
    string Body,
    bool IsInternal,
    IReadOnlyCollection<Guid>? AttachmentIds,
    TicketStatus? SetStatus) : ICommand<TicketDetailDto>;

internal sealed class AddTicketMessageValidator : AbstractValidator<AddTicketMessageCommand>
{
    public AddTicketMessageValidator()
    {
        RuleFor(c => c.Body).NotEmpty().MaximumLength(TicketMessage.BodyMaxLength);
        RuleFor(c => c.SetStatus!.Value).IsInEnum().When(c => c.SetStatus is not null);
    }
}

internal sealed class AddTicketMessageHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    TicketWriter writer,
    TimeProvider clock) : ICommandHandler<AddTicketMessageCommand, TicketDetailDto>
{
    public Task<Result<TicketDetailDto>> Handle(AddTicketMessageCommand command, CancellationToken cancellationToken) =>
        Concurrency.RetryAsync(db, () => Attempt(command, cancellationToken));

    private async Task<Result<TicketDetailDto>> Attempt(AddTicketMessageCommand command, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(t => t.Watchers)
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        var actor = currentUser.AsActor();
        var message = await writer.AddMessageAsync(
            ticket, actor, command.Body, command.IsInternal, command.AttachmentIds, cancellationToken);
        if (!message.IsSuccess)
        {
            return message.Error;
        }

        if (command.SetStatus is { } status)
        {
            ticket.ChangeStatus(status, actor, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await TicketQueries.LoadDetail(db, ticket.Id, currentUser.UserId, cancellationToken))!;
    }
}

public sealed record SetWatchingCommand(Guid TicketId, bool Watching) : ICommand<TicketDetailDto>;

internal sealed class SetWatchingHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<SetWatchingCommand, TicketDetailDto>
{
    public async Task<Result<TicketDetailDto>> Handle(SetWatchingCommand command, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(t => t.Watchers)
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        var userId = currentUser.RequireUserId();
        var changed = command.Watching ? ticket.Watch(userId) : ticket.Unwatch(userId);
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await TicketQueries.LoadDetail(db, ticket.Id, userId, cancellationToken))!;
    }
}

public sealed record DeleteTicketCommand(Guid TicketId) : ICommand<Success>;

internal sealed class DeleteTicketHandler(IAppDbContext db) : ICommandHandler<DeleteTicketCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteTicketCommand command, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        db.Tickets.Remove(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}
