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
    string? Sort,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize) : IQuery<PagedResult<TicketListItem>>, IPagedRequest;

internal sealed class ListTicketsValidator : AbstractValidator<ListTicketsQuery>
{
    public ListTicketsValidator()
    {
        this.AddPagingRules();
        RuleFor(q => q.Tag).MaximumLength(Ticket.TagMaxLength);
        RuleForEach(q => q.Status).IsInEnum();
        RuleForEach(q => q.Priority).IsInEnum();
    }
}

internal sealed class ListTicketsHandler(IAppDbContext db)
    : IQueryHandler<ListTicketsQuery, PagedResult<TicketListItem>>
{
    public async Task<Result<PagedResult<TicketListItem>>> Handle(
        ListTicketsQuery query,
        CancellationToken cancellationToken)
    {
        var tickets = db.Tickets.AsNoTracking();
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

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim().ToLowerInvariant();
            tickets = tickets.Where(t => t.Tags.Contains(tag));
        }

        tickets = Paging.ParseSort(query.Sort, "-lastactivityat") switch
        {
            ("number", false) => tickets.OrderBy(t => t.Number),
            ("number", true) => tickets.OrderByDescending(t => t.Number),
            ("createdat", false) => tickets.OrderBy(t => t.CreatedAt),
            ("createdat", true) => tickets.OrderByDescending(t => t.CreatedAt),
            ("priority", false) => tickets.OrderBy(t => t.Priority).ThenByDescending(t => t.LastActivityAt),
            ("priority", true) => tickets.OrderByDescending(t => t.Priority).ThenByDescending(t => t.LastActivityAt),
            ("lastactivityat", false) => tickets.OrderBy(t => t.LastActivityAt),
            _ => tickets.OrderByDescending(t => t.LastActivityAt).ThenByDescending(t => t.Number),
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

        var staffIds = messages.Where(m => m.AuthorType == ActorType.Staff).Select(m => m.AuthorId)
            .Concat(events.Where(e => e.ActorType == ActorType.Staff).Select(e => e.ActorId))
            .OfType<Guid>().Distinct().ToList();
        var customerIds = messages.Where(m => m.AuthorType == ActorType.Customer).Select(m => m.AuthorId)
            .Concat(events.Where(e => e.ActorType == ActorType.Customer).Select(e => e.ActorId))
            .OfType<Guid>().Distinct().ToList();
        var staffNames = await db.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        var customerNames = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.FullName, cancellationToken);

        ActorDto Describe(ActorType type, Guid? id) => new(
            type,
            id,
            id is null
                ? null
                : type == ActorType.Staff ? staffNames.GetValueOrDefault(id.Value) : customerNames.GetValueOrDefault(id.Value));

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
