using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record ListTicketsQuery(
    IReadOnlyCollection<TicketStatus>? Status,
    IReadOnlyCollection<TicketPriority>? Priority,
    Guid? AssigneeId,
    bool? Unassigned,
    Guid? TeamId,
    Guid? RequesterId,
    string? Tag,
    string? Search,
    string? Sort,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    TicketSlaFilter? Sla = null) : IQuery<PagedResult<TicketListItem>>, IPagedRequest;

public enum TicketSlaFilter
{
    /// <summary>A deadline was missed.</summary>
    Breached,

    /// <summary>A deadline is close but not yet missed.</summary>
    AtRisk,
}

internal sealed class ListTicketsValidator : AbstractValidator<ListTicketsQuery>
{
    public ListTicketsValidator()
    {
        this.AddPagingRules();
        RuleFor(q => q.Tag).MaximumLength(Ticket.TagMaxLength);
        RuleFor(q => q.Search).MaximumLength(200);
        RuleForEach(q => q.Status).IsInEnum();
        RuleForEach(q => q.Priority).IsInEnum();
    }
}

internal sealed class ListTicketsHandler(IAppDbContext db, ITicketSearch search)
    : IQueryHandler<ListTicketsQuery, PagedResult<TicketListItem>>
{
    public async Task<Result<PagedResult<TicketListItem>>> Handle(
        ListTicketsQuery query,
        CancellationToken cancellationToken)
    {
        var tickets = db.Tickets.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            tickets = search.Matching(tickets, query.Search);
        }

        if (query.Status is { Count: > 0 } statuses)
        {
            tickets = tickets.Where(t => statuses.Contains(t.Status));
        }

        if (query.Priority is { Count: > 0 } priorities)
        {
            tickets = tickets.Where(t => priorities.Contains(t.Priority));
        }

        if (query.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssigneeId == null);
        }
        else if (query.AssigneeId is { } assigneeId)
        {
            tickets = tickets.Where(t => t.AssigneeId == assigneeId);
        }

        if (query.TeamId is { } teamId)
        {
            tickets = tickets.Where(t => t.TeamId == teamId);
        }

        if (query.RequesterId is { } requesterId)
        {
            tickets = tickets.Where(t => t.RequesterId == requesterId);
        }

        tickets = query.Sla switch
        {
            TicketSlaFilter.Breached => tickets.Where(t => t.FirstResponseBreachedAt != null || t.ResolutionBreachedAt != null),
            TicketSlaFilter.AtRisk => tickets.Where(t =>
                t.Status != TicketStatus.Closed
                && ((t.FirstResponseWarnedAt != null && t.FirstResponseBreachedAt == null && t.FirstRespondedAt == null)
                    || (t.ResolutionWarnedAt != null && t.ResolutionBreachedAt == null && t.ResolutionDueAt != null))),
            _ => tickets,
        };

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim().ToLowerInvariant();
            tickets = tickets.Where(t => t.Tags.Contains(tag));
        }

        // Searching for "42" or "#42" also matches text that mentions 42; ticket #42 itself comes first.
        var exactNumber = int.TryParse(
            query.Search?.Trim().TrimStart('#'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (int?)null;
        var ordered = tickets.OrderByDescending(t => exactNumber != null && t.Number == exactNumber);

        tickets = Paging.ParseSort(query.Sort, "-lastactivityat") switch
        {
            ("number", false) => ordered.ThenBy(t => t.Number),
            ("number", true) => ordered.ThenByDescending(t => t.Number),
            ("createdat", false) => ordered.ThenBy(t => t.CreatedAt),
            ("createdat", true) => ordered.ThenByDescending(t => t.CreatedAt),
            ("priority", false) => ordered.ThenBy(t => t.Priority).ThenByDescending(t => t.LastActivityAt),
            ("priority", true) => ordered.ThenByDescending(t => t.Priority).ThenByDescending(t => t.LastActivityAt),
            ("lastactivityat", false) => ordered.ThenBy(t => t.LastActivityAt),
            ("due", _) => ordered
                .ThenBy(t => t.ResolutionDueAt == null && t.FirstResponseDueAt == null)
                .ThenBy(t => t.FirstRespondedAt == null && t.FirstResponseDueAt != null && t.FirstResponseDueAt < t.ResolutionDueAt
                    ? t.FirstResponseDueAt
                    : t.ResolutionDueAt),
            _ => ordered.ThenByDescending(t => t.LastActivityAt).ThenByDescending(t => t.Number),
        };

        var page = await tickets.ToPagedAsync(query, cancellationToken);
        var items = await TicketQueries.ToListItems(db, page.Items, cancellationToken);
        return new PagedResult<TicketListItem>(items, page.Page, page.PageSize, page.TotalCount);
    }
}

public sealed record GetTicketQuery(Guid TicketId) : IQuery<TicketDetailDto>;

internal sealed class GetTicketHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetTicketQuery, TicketDetailDto>
{
    public async Task<Result<TicketDetailDto>> Handle(GetTicketQuery query, CancellationToken cancellationToken)
    {
        var detail = await TicketQueries.LoadDetail(db, query.TicketId, currentUser.UserId, cancellationToken);
        return detail is null ? TicketErrors.NotFound : detail;
    }
}

public sealed record GetTicketTimelineQuery(Guid TicketId) : IQuery<IReadOnlyList<TimelineItem>>;

internal sealed class GetTicketTimelineHandler(IAppDbContext db)
    : IQueryHandler<GetTicketTimelineQuery, IReadOnlyList<TimelineItem>>
{
    public async Task<Result<IReadOnlyList<TimelineItem>>> Handle(
        GetTicketTimelineQuery query,
        CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == query.TicketId, cancellationToken))
        {
            return TicketErrors.NotFound;
        }

        return await TimelineReader.Load(db, query.TicketId, includeInternal: true, cancellationToken);
    }
}

internal static class TimelineReader
{
    public static async Task<List<TimelineItem>> Load(
        IAppDbContext db,
        Guid ticketId,
        bool includeInternal,
        CancellationToken cancellationToken)
    {
        var messages = await db.TicketMessages.AsNoTracking()
            .Where(m => m.TicketId == ticketId && (includeInternal || !m.IsInternal))
            .ToListAsync(cancellationToken);
        var events = includeInternal
            ? await db.TicketEvents.AsNoTracking().Where(e => e.TicketId == ticketId).ToListAsync(cancellationToken)
            : [];
        var messageIds = messages.Select(m => m.Id).ToList();
        var attachments = (await db.Attachments.AsNoTracking()
                .Where(a => a.MessageId != null && messageIds.Contains(a.MessageId.Value))
                .OrderBy(a => a.FileName)
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.MessageId!.Value);

        var names = await ActorNames.Resolve(
            db,
            messages.Select(m => new Actor(m.AuthorType, m.AuthorId))
                .Concat(events.Select(e => new Actor(e.ActorType, e.ActorId))),
            cancellationToken);

        ActorDto Describe(ActorType type, Guid? id) => new(type, id, names.GetValueOrDefault(new Actor(type, id)));

        return messages
            .Select(m => new TimelineItem(
                m.Id,
                TimelineKind.Message,
                m.CreatedAt,
                Describe(m.AuthorType, m.AuthorId),
                m.Body,
                m.IsInternal,
                [.. attachments[m.Id].Select(a => new AttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes))],
                null,
                null,
                null,
                null))
            .Concat(events.Select(e => new TimelineItem(
                e.Id,
                TimelineKind.Event,
                e.CreatedAt,
                Describe(e.ActorType, e.ActorId),
                null,
                false,
                [],
                e.Type,
                e.Field,
                e.OldValue,
                e.NewValue)))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToList();
    }
}

public sealed record CustomerDto(Guid Id, string Email, string FullName, DateTimeOffset CreatedAt);

public sealed record ListCustomersQuery(string? Search, int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<CustomerDto>>, IPagedRequest;

internal sealed class ListCustomersValidator : AbstractValidator<ListCustomersQuery>
{
    public ListCustomersValidator()
    {
        this.AddPagingRules();
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

internal sealed class ListCustomersHandler(IAppDbContext db)
    : IQueryHandler<ListCustomersQuery, PagedResult<CustomerDto>>
{
    public async Task<Result<PagedResult<CustomerDto>>> Handle(
        ListCustomersQuery query,
        CancellationToken cancellationToken)
    {
        var customers = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            customers = customers.Where(c => c.FullName.ToLower().Contains(term) || c.Email.Contains(term));
        }

        return await customers
            .OrderBy(c => c.FullName)
            .ThenBy(c => c.Id)
            .Select(c => new CustomerDto(c.Id, c.Email, c.FullName, c.CreatedAt))
            .ToPagedAsync(query, cancellationToken);
    }
}
