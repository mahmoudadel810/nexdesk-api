using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record PersonRef(Guid Id, string Name, string? Email);

public sealed record TeamRef(Guid Id, string Name);

/// <summary>Deadlines of a ticket. A due date is null when no policy applies or the clock is paused.</summary>
public sealed record TicketSlaDto(
    DateTimeOffset? FirstResponseDueAt,
    bool FirstResponseBreached,
    bool FirstResponseMet,
    DateTimeOffset? ResolutionDueAt,
    bool ResolutionBreached,
    bool IsPaused,
    bool IsEscalated)
{
    public static TicketSlaDto From(Ticket ticket) => new(
        ticket.FirstResponseDueAt,
        ticket.FirstResponseBreachedAt is not null,
        ticket.FirstRespondedAt is not null && ticket.FirstResponseBreachedAt is null,
        ticket.ResolutionDueAt,
        ticket.ResolutionBreachedAt is not null,
        ticket.IsSlaPaused,
        ticket.IsEscalated);
}

public sealed record TicketListItem(
    Guid Id,
    int Number,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    TicketType Type,
    TicketChannel Channel,
    PersonRef Requester,
    PersonRef? Assignee,
    TeamRef? Team,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    TicketSlaDto Sla);

public sealed record TicketDetailDto(
    Guid Id,
    int Number,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    TicketType Type,
    TicketChannel Channel,
    PersonRef Requester,
    PersonRef? Assignee,
    TeamRef? Team,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? SolvedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<PersonRef> Watchers,
    bool IsWatching,
    IReadOnlyDictionary<string, string> CustomFields,
    TicketLink? MergedInto,
    uint Version,
    TicketSlaDto Sla,
    Portal.CsatDto? Csat);

/// <summary>A pointer to another ticket.</summary>
public sealed record TicketLink(Guid Id, int Number, string Subject);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes);

public sealed record ActorDto(ActorType Type, Guid? Id, string? Name);

public enum TimelineKind
{
    Message,
    Event,
}

public sealed record TimelineItem(
    Guid Id,
    TimelineKind Kind,
    DateTimeOffset CreatedAt,
    ActorDto Actor,
    string? Body,
    bool IsInternal,
    IReadOnlyList<AttachmentDto> Attachments,
    TicketEventType? EventType,
    string? Field,
    string? OldValue,
    string? NewValue);

public static class TicketErrors
{
    public static readonly Error NotFound = Error.NotFound("ticket-not-found", "The ticket was not found.");

    public static readonly Error Closed = Error.Conflict("ticket-closed", "A closed ticket cannot be changed.");

    public static readonly Error Stale = Error.Conflict(
        "stale-ticket", "Someone else changed this ticket. Reload it and apply your change again.");

    public static readonly Error MergeIntoSelf =
        Error.Validation("merge-into-self", "A ticket cannot be merged into itself.");

    public static readonly Error UnknownCustomField =
        Error.Validation("unknown-custom-field", "One of the custom fields does not exist.");

    public static readonly Error InvalidCustomFieldValue =
        Error.Validation("invalid-custom-field-value", "A custom field has a value that does not fit its type.");

    public static readonly Error RequiredCustomFieldMissing =
        Error.Validation("required-custom-field-missing", "Fill in every required custom field.");

    public static readonly Error RequesterRequired =
        Error.Validation("requester-required", "Choose an existing customer or enter their email address.");

    public static readonly Error UnknownRequester =
        Error.Validation("unknown-requester", "The selected customer does not exist.");

    public static readonly Error InvalidAssignee =
        Error.Validation("invalid-assignee", "The assignee must be an active agent of this workspace.");

    public static readonly Error UnknownTeam = Error.Validation("unknown-team", "The selected team does not exist.");

    public static readonly Error InvalidAttachment =
        Error.Validation("invalid-attachment", "One or more attachments could not be used. Upload them again.");

    public static readonly Error AttachmentNotFound =
        Error.NotFound("attachment-not-found", "The attachment was not found.");

    public static readonly Error FileTooLarge =
        Error.Validation("file-too-large", "Files can be at most 10 MB.");

    public static readonly Error FileTypeBlocked =
        Error.Validation("file-type-blocked", "This type of file cannot be uploaded.");
}

internal static class TicketQueries
{
    /// <summary>Maps tickets to list items in the given order, looking up the people and teams they refer to.</summary>
    public static async Task<List<TicketListItem>> ToListItems(
        IAppDbContext db,
        IReadOnlyList<Ticket> tickets,
        CancellationToken cancellationToken)
    {
        var requesterIds = tickets.Select(t => t.RequesterId).Distinct().ToList();
        var assigneeIds = tickets.Select(t => t.AssigneeId).OfType<Guid>().Distinct().ToList();
        var teamIds = tickets.Select(t => t.TeamId).OfType<Guid>().Distinct().ToList();

        var requesters = await db.Customers.AsNoTracking()
            .Where(c => requesterIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new PersonRef(c.Id, c.FullName, c.Email), cancellationToken);
        var assignees = assigneeIds.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => assigneeIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => new PersonRef(u.Id, u.FullName, u.Email), cancellationToken);
        var teams = teamIds.Count == 0
            ? []
            : await db.Teams.AsNoTracking()
                .Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => new TeamRef(t.Id, t.Name), cancellationToken);

        return tickets
            .Select(ticket => new TicketListItem(
                ticket.Id,
                ticket.Number,
                ticket.Subject,
                ticket.Status,
                ticket.Priority,
                ticket.Type,
                ticket.Channel,
                requesters[ticket.RequesterId],
                ticket.AssigneeId is { } assigneeId ? assignees.GetValueOrDefault(assigneeId) : null,
                ticket.TeamId is { } teamId ? teams.GetValueOrDefault(teamId) : null,
                ticket.Tags,
                ticket.CreatedAt,
                ticket.LastActivityAt,
                TicketSlaDto.From(ticket)))
            .ToList();
    }

    public static async Task<TicketDetailDto?> LoadDetail(
        IAppDbContext db,
        Guid ticketId,
        Guid? viewerId,
        CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var item = (await ToListItems(db, [ticket], cancellationToken))[0];
        var watchers = (await db.TicketWatchers.AsNoTracking()
                .Where(w => w.TicketId == ticketId)
                .Join(db.Users, w => w.UserId, u => u.Id, (w, u) => new { u.Id, u.FullName, u.Email })
                .OrderBy(u => u.FullName)
                .ToListAsync(cancellationToken))
            .Select(u => new PersonRef(u.Id, u.FullName, u.Email))
            .ToList();

        return new TicketDetailDto(
            item.Id,
            item.Number,
            item.Subject,
            item.Status,
            item.Priority,
            item.Type,
            item.Channel,
            item.Requester,
            item.Assignee,
            item.Team,
            item.Tags,
            item.CreatedAt,
            item.LastActivityAt,
            ticket.FirstRespondedAt,
            ticket.SolvedAt,
            ticket.ClosedAt,
            watchers,
            viewerId is not null && watchers.Any(w => w.Id == viewerId),
            ticket.CustomFields,
            ticket.MergedIntoId is { } mergedIntoId
                ? await db.Tickets.AsNoTracking()
                    .Where(t => t.Id == mergedIntoId)
                    .Select(t => new TicketLink(t.Id, t.Number, t.Subject))
                    .FirstOrDefaultAsync(cancellationToken)
                : null,
            ticket.Version,
            item.Sla,
            await db.CsatResponses.AsNoTracking()
                .Where(c => c.TicketId == ticketId)
                .Select(c => new Portal.CsatDto(c.Rating, c.Comment))
                .FirstOrDefaultAsync(cancellationToken));
    }
}
