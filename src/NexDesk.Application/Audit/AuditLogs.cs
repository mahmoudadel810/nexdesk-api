using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Audit;
using NexDesk.Domain.Common;

namespace NexDesk.Application.Audit;

public sealed record AuditLogDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    ActorDto Actor,
    string EntityType,
    Guid EntityId,
    string? EntityLabel,
    AuditOperation Operation,
    IReadOnlyList<AuditChange> Changes,
    string? IpAddress);

public sealed record ListAuditLogsQuery(
    string? EntityType,
    AuditOperation? Operation,
    Guid? ActorId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize) : IQuery<PagedResult<AuditLogDto>>, IPagedRequest;

internal sealed class ListAuditLogsValidator : AbstractValidator<ListAuditLogsQuery>
{
    public ListAuditLogsValidator()
    {
        this.AddPagingRules();
        RuleFor(q => q.EntityType).MaximumLength(AuditLog.EntityTypeMaxLength);
        RuleFor(q => q.Operation).IsInEnum();
        RuleFor(q => q.Search).MaximumLength(200);
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("The end of the period must not be before its start.");
    }
}

internal sealed class ListAuditLogsHandler(IAppDbContext db) : IQueryHandler<ListAuditLogsQuery, PagedResult<AuditLogDto>>
{
    public async Task<Result<PagedResult<AuditLogDto>>> Handle(ListAuditLogsQuery query, CancellationToken cancellationToken)
    {
        var logs = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            logs = logs.Where(a => a.EntityType == query.EntityType.Trim());
        }

        if (query.Operation is { } operation)
        {
            logs = logs.Where(a => a.Operation == operation);
        }

        if (query.ActorId is { } actorId)
        {
            logs = logs.Where(a => a.ActorId == actorId);
        }

        if (query.From is { } from)
        {
            logs = logs.Where(a => a.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            logs = logs.Where(a => a.OccurredAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var lowered = term.ToLowerInvariant();
            logs = Guid.TryParse(term, out var id)
                ? logs.Where(a => a.EntityId == id || a.ActorId == id)
                : logs.Where(a => a.EntityLabel != null && a.EntityLabel.ToLower().Contains(lowered));
        }

        var page = await logs
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .ToPagedAsync(query, cancellationToken);

        var names = await ActorNames.Resolve(
            db, page.Items.Select(a => new Actor(a.ActorType, a.ActorId)), cancellationToken);
        return new PagedResult<AuditLogDto>(
            page.Items.Select(a => new AuditLogDto(
                a.Id,
                a.OccurredAt,
                new ActorDto(a.ActorType, a.ActorId, names.GetValueOrDefault(new Actor(a.ActorType, a.ActorId))),
                a.EntityType,
                a.EntityId,
                a.EntityLabel,
                a.Operation,
                a.Changes,
                a.IpAddress)).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount);
    }
}

/// <summary>Looks up the display names of the people and integrations behind a set of actions.</summary>
public static class ActorNames
{
    public static async Task<Dictionary<Actor, string>> Resolve(
        IAppDbContext db,
        IEnumerable<Actor> actors,
        CancellationToken cancellationToken)
    {
        var distinct = actors.Where(a => a.Id is not null).Distinct().ToList();
        var staffIds = IdsOf(distinct, ActorType.Staff);
        var customerIds = IdsOf(distinct, ActorType.Customer);

        var names = new Dictionary<Actor, string>();
        if (staffIds.Count > 0)
        {
            foreach (var user in await db.Users.AsNoTracking().Where(u => staffIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName }).ToListAsync(cancellationToken))
            {
                names[Actor.Staff(user.Id)] = user.FullName;
            }
        }

        if (customerIds.Count > 0)
        {
            foreach (var customer in await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
                .Select(c => new { c.Id, c.FullName }).ToListAsync(cancellationToken))
            {
                names[Actor.Customer(customer.Id)] = customer.FullName;
            }
        }

        return names;
    }

    private static List<Guid> IdsOf(IEnumerable<Actor> actors, ActorType type) =>
        actors.Where(a => a.Type == type).Select(a => a.Id!.Value).ToList();
}
