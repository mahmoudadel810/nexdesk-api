using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Notifications;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Notifications;

public sealed record NotificationDto(
    Guid Id,
    NotificationType Type,
    Guid TicketId,
    int TicketNumber,
    string TicketSubject,
    string? ActorName,
    string? Preview,
    DateTimeOffset CreatedAt,
    bool IsRead)
{
    public static NotificationDto From(Notification n) => new(
        n.Id, n.Type, n.TicketId, n.TicketNumber, n.TicketSubject, n.ActorName, n.Preview, n.CreatedAt, n.ReadAt is not null);
}

public sealed record NotificationPreferenceDto(NotificationType Type, bool InApp, bool Email);

/// <summary>
/// Creates in-app notifications, queues notification emails and pushes both to connected browsers.
/// Recipients who switched a kind of notification off are skipped for that channel.
/// </summary>
public sealed class Notifier(
    IAppDbContext db,
    ITenantContext tenant,
    IRealtimeNotifier realtime,
    IEmailDispatcher emails,
    TimeProvider clock,
    IOptions<AppOptions> appOptions)
{
    public async Task NotifyAsync(
        NotificationType type,
        Ticket ticket,
        IEnumerable<Guid> recipientIds,
        string? actorName,
        string? preview,
        CancellationToken cancellationToken)
    {
        var ids = recipientIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var recipients = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.Email, u.FullName, u.Language })
            .ToListAsync(cancellationToken);
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .Where(p => ids.Contains(p.UserId) && p.Type == type)
            .ToDictionaryAsync(p => p.UserId, cancellationToken);

        var now = clock.GetUtcNow();
        var created = new List<Notification>();
        foreach (var recipient in recipients)
        {
            var preference = preferences.GetValueOrDefault(recipient.Id);
            if (preference?.InApp != false)
            {
                var notification = Notification.Create(
                    recipient.Id, type, ticket.Id, ticket.Number, ticket.Subject, actorName, preview, now);
                db.Notifications.Add(notification);
                created.Add(notification);
            }

            if (preference?.Email != false)
            {
                emails.Enqueue(new EmailMessage(
                    recipient.Email,
                    recipient.FullName,
                    $"notify-{Slug(type)}",
                    recipient.Language,
                    new Dictionary<string, string>
                    {
                        ["name"] = recipient.FullName,
                        ["number"] = ticket.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["subject"] = ticket.Subject,
                        ["actor"] = actorName ?? "NexDesk",
                        ["preview"] = Notification.Shorten(preview) ?? string.Empty,
                        ["link"] = $"{appOptions.Value.WebBaseUrl.TrimEnd('/')}/app/tickets/{ticket.Id}",
                    }));
            }
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var notification in created)
            {
                await realtime.NotificationAsync(notification.UserId, NotificationDto.From(notification), cancellationToken);
            }
        }
    }

    public Task TicketChangedAsync(Guid ticketId, string change, CancellationToken cancellationToken) =>
        tenant.TenantId is { } tenantId
            ? realtime.TicketChangedAsync(tenantId, ticketId, change, cancellationToken)
            : Task.CompletedTask;

    private static string Slug(NotificationType type) => type switch
    {
        NotificationType.TicketAssigned => "assigned",
        NotificationType.TicketReply => "reply",
        NotificationType.TicketNote => "note",
        NotificationType.SlaWarning => "sla-warning",
        _ => "sla-breach",
    };
}

/// <summary>Turns ticket events into live updates, notifications and emails.</summary>
internal sealed class TicketNotificationHandlers(IAppDbContext db, Notifier notifier, IEmailDispatcher emails, IOptions<AppOptions> appOptions)
    : IDomainEventHandler<TicketCreated>,
        IDomainEventHandler<TicketChanged>,
        IDomainEventHandler<TicketAssigned>,
        IDomainEventHandler<TicketMessageAdded>,
        IDomainEventHandler<TicketSlaApproaching>,
        IDomainEventHandler<TicketSlaBreached>
{
    public Task Handle(TicketCreated domainEvent, CancellationToken cancellationToken) =>
        notifier.TicketChangedAsync(domainEvent.TicketId, "created", cancellationToken);

    public Task Handle(TicketChanged domainEvent, CancellationToken cancellationToken) =>
        notifier.TicketChangedAsync(domainEvent.TicketId, "updated", cancellationToken);

    public async Task Handle(TicketAssigned domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.AssignedBy.Id == domainEvent.AssigneeId)
        {
            return;
        }

        var ticket = await Load(domainEvent.TicketId, cancellationToken);
        if (ticket is not null)
        {
            await notifier.NotifyAsync(
                NotificationType.TicketAssigned,
                ticket,
                [domainEvent.AssigneeId],
                await ActorName(domainEvent.AssignedBy, cancellationToken),
                null,
                cancellationToken);
        }
    }

    public async Task Handle(TicketMessageAdded domainEvent, CancellationToken cancellationToken)
    {
        await notifier.TicketChangedAsync(domainEvent.TicketId, "message", cancellationToken);
        var ticket = await Load(domainEvent.TicketId, cancellationToken);
        var body = await db.TicketMessages.AsNoTracking()
            .Where(m => m.Id == domainEvent.MessageId)
            .Select(m => m.Body)
            .FirstOrDefaultAsync(cancellationToken);
        if (ticket is null || body is null)
        {
            return;
        }

        var actorName = await ActorName(domainEvent.Author, cancellationToken);
        var watchers = await db.TicketWatchers.AsNoTracking()
            .Where(w => w.TicketId == ticket.Id)
            .Select(w => w.UserId)
            .ToListAsync(cancellationToken);
        var recipients = watchers
            .Concat(ticket.AssigneeId is { } assigneeId ? [assigneeId] : [])
            .Where(id => domainEvent.Author.Type != ActorType.Staff || id != domainEvent.Author.Id);
        await notifier.NotifyAsync(
            domainEvent.IsInternal ? NotificationType.TicketNote : NotificationType.TicketReply,
            ticket,
            recipients,
            actorName,
            body,
            cancellationToken);

        if (!domainEvent.IsInternal && domainEvent.Author.Type == ActorType.Staff)
        {
            await EmailRequester(ticket, actorName, body, cancellationToken);
        }
    }

    public async Task Handle(TicketSlaApproaching domainEvent, CancellationToken cancellationToken)
    {
        var ticket = await Load(domainEvent.TicketId, cancellationToken);
        if (ticket is not null)
        {
            await notifier.NotifyAsync(
                NotificationType.SlaWarning,
                ticket,
                await Responsible(ticket, includeAdmins: false, cancellationToken),
                null,
                domainEvent.Metric.ToString(),
                cancellationToken);
        }
    }

    public async Task Handle(TicketSlaBreached domainEvent, CancellationToken cancellationToken)
    {
        await notifier.TicketChangedAsync(domainEvent.TicketId, "updated", cancellationToken);
        var ticket = await Load(domainEvent.TicketId, cancellationToken);
        if (ticket is not null)
        {
            // A missed deadline is escalated: the admins hear about it as well as the assignee.
            await notifier.NotifyAsync(
                NotificationType.SlaBreach,
                ticket,
                await Responsible(ticket, includeAdmins: true, cancellationToken),
                null,
                domainEvent.Metric.ToString(),
                cancellationToken);
        }
    }

    private Task<Ticket?> Load(Guid ticketId, CancellationToken cancellationToken) =>
        db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

    /// <summary>The assignee, or the admins when nobody is assigned or an escalation is due.</summary>
    private async Task<List<Guid>> Responsible(Ticket ticket, bool includeAdmins, CancellationToken cancellationToken)
    {
        var recipients = ticket.AssigneeId is { } assigneeId ? new List<Guid> { assigneeId } : [];
        if (includeAdmins || recipients.Count == 0)
        {
            recipients.AddRange(await db.Users.AsNoTracking()
                .Where(u => u.IsActive && (u.Role == Role.Owner || u.Role == Role.Admin))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken));
        }

        return recipients;
    }

    private async Task<string?> ActorName(Actor actor, CancellationToken cancellationToken) => actor switch
    {
        { Type: ActorType.Staff, Id: { } userId } => await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken),
        { Type: ActorType.Customer, Id: { } customerId } => await db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId).Select(c => c.FullName).FirstOrDefaultAsync(cancellationToken),
        _ => null,
    };

    private async Task EmailRequester(Ticket ticket, string? agentName, string body, CancellationToken cancellationToken)
    {
        var requester = await db.Customers.AsNoTracking()
            .Where(c => c.Id == ticket.RequesterId)
            .Select(c => new { c.Email, c.FullName })
            .FirstOrDefaultAsync(cancellationToken);
        var workspace = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == ticket.TenantId)
            .Select(t => new { t.Name, t.Slug, t.DefaultLanguage })
            .FirstAsync(cancellationToken);
        if (requester is null)
        {
            return;
        }

        emails.Enqueue(new EmailMessage(
            requester.Email,
            requester.FullName,
            "customer-reply",
            workspace.DefaultLanguage,
            new Dictionary<string, string>
            {
                ["name"] = requester.FullName,
                ["workspace"] = workspace.Name,
                ["agent"] = agentName ?? workspace.Name,
                ["number"] = ticket.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["subject"] = ticket.Subject,
                ["message"] = body,
                ["link"] = $"{appOptions.Value.WebBaseUrl.TrimEnd('/')}/portal/{workspace.Slug}/tickets/{ticket.Id}",
            }));
    }
}

public sealed record ListNotificationsQuery(bool UnreadOnly = false, int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<NotificationDto>>, IPagedRequest;

internal sealed class ListNotificationsValidator : AbstractValidator<ListNotificationsQuery>
{
    public ListNotificationsValidator() => this.AddPagingRules();
}

internal sealed class ListNotificationsHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListNotificationsQuery, PagedResult<NotificationDto>>
{
    public async Task<Result<PagedResult<NotificationDto>>> Handle(
        ListNotificationsQuery query,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var page = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && (!query.UnreadOnly || n.ReadAt == null))
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .ToPagedAsync(query, cancellationToken);
        return new PagedResult<NotificationDto>(
            [.. page.Items.Select(NotificationDto.From)], page.Page, page.PageSize, page.TotalCount);
    }
}

public sealed record UnreadCountDto(int Count);

public sealed record GetUnreadCountQuery : IQuery<UnreadCountDto>;

internal sealed class GetUnreadCountHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetUnreadCountQuery, UnreadCountDto>
{
    public async Task<Result<UnreadCountDto>> Handle(GetUnreadCountQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        return new UnreadCountDto(
            await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken));
    }
}

/// <summary>Marks one notification read, or all of the user's when no id is given.</summary>
public sealed record MarkNotificationsReadCommand(Guid? NotificationId) : ICommand<UnreadCountDto>;

internal sealed class MarkNotificationsReadHandler(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<MarkNotificationsReadCommand, UnreadCountDto>
{
    public async Task<Result<UnreadCountDto>> Handle(MarkNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.GetUtcNow();
        var updated = await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null
                && (command.NotificationId == null || n.Id == command.NotificationId))
            .ExecuteUpdateAsync(update => update.SetProperty(n => n.ReadAt, now), cancellationToken);
        if (command.NotificationId is { } id && updated == 0
            && !await db.Notifications.AnyAsync(n => n.Id == id && n.UserId == userId, cancellationToken))
        {
            return Error.NotFound("notification-not-found", "The notification was not found.");
        }

        return new UnreadCountDto(
            await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken));
    }
}

public sealed record GetNotificationPreferencesQuery : IQuery<IReadOnlyList<NotificationPreferenceDto>>;

internal sealed class GetNotificationPreferencesHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetNotificationPreferencesQuery, IReadOnlyList<NotificationPreferenceDto>>
{
    public async Task<Result<IReadOnlyList<NotificationPreferenceDto>>> Handle(
        GetNotificationPreferencesQuery query,
        CancellationToken cancellationToken) =>
        await PreferenceQueries.Load(db, currentUser.RequireUserId(), cancellationToken);
}

public sealed record UpdateNotificationPreferencesCommand(IReadOnlyList<NotificationPreferenceDto> Preferences)
    : ICommand<IReadOnlyList<NotificationPreferenceDto>>;

internal sealed class UpdateNotificationPreferencesValidator : AbstractValidator<UpdateNotificationPreferencesCommand>
{
    public UpdateNotificationPreferencesValidator()
    {
        RuleFor(c => c.Preferences)
            .NotNull()
            .Must(p => p is null || p.Select(x => x.Type).Distinct().Count() == p.Count)
            .WithMessage("Each notification type can appear only once.");
        RuleForEach(c => c.Preferences).ChildRules(p => p.RuleFor(x => x.Type).IsInEnum());
    }
}

internal sealed class UpdateNotificationPreferencesHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<UpdateNotificationPreferencesCommand, IReadOnlyList<NotificationPreferenceDto>>
{
    public async Task<Result<IReadOnlyList<NotificationPreferenceDto>>> Handle(
        UpdateNotificationPreferencesCommand command,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var existing = await db.NotificationPreferences
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Type, cancellationToken);
        foreach (var update in command.Preferences)
        {
            if (existing.TryGetValue(update.Type, out var preference))
            {
                preference.Set(update.InApp, update.Email);
            }
            else
            {
                db.NotificationPreferences.Add(
                    NotificationPreference.Create(userId, update.Type, update.InApp, update.Email));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await PreferenceQueries.Load(db, userId, cancellationToken);
    }
}

internal static class PreferenceQueries
{
    /// <summary>One entry per notification type; types without a stored choice default to on for both channels.</summary>
    public static async Task<List<NotificationPreferenceDto>> Load(
        IAppDbContext db,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var stored = await db.NotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Type, cancellationToken);
        return Enum.GetValues<NotificationType>()
            .Select(type => stored.TryGetValue(type, out var preference)
                ? new NotificationPreferenceDto(type, preference.InApp, preference.Email)
                : new NotificationPreferenceDto(type, true, true))
            .ToList();
    }
}
