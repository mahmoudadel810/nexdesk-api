using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record TicketDraft(
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
    IReadOnlyDictionary<string, string?>? CustomFields = null);

/// <summary>
/// Creates tickets and messages for every channel (agent, portal, email, API), so numbering, requester
/// matching and attachment linking behave the same everywhere. The caller saves the context.
/// </summary>
public sealed class TicketWriter(IAppDbContext db, ITicketNumberAllocator numbers, TimeProvider clock)
{
    public const int MaxAttachmentsPerMessage = 10;

    public async Task<Result<Ticket>> CreateAsync(
        TicketDraft draft,
        Actor actor,
        TicketChannel channel,
        CancellationToken cancellationToken)
    {
        var requester = await ResolveRequester(draft, cancellationToken);
        if (!requester.IsSuccess)
        {
            return requester.Error;
        }

        var assignment = await ResolveAssignment(draft.AssigneeId, draft.TeamId, cancellationToken);
        if (!assignment.IsSuccess)
        {
            return assignment.Error;
        }

        var attachments = await LoadAttachments(draft.AttachmentIds, actor, cancellationToken);
        if (!attachments.IsSuccess)
        {
            return attachments.Error;
        }

        var now = clock.GetUtcNow();
        var ticket = Ticket.Open(
            await numbers.NextAsync(cancellationToken),
            draft.Subject,
            draft.Description,
            requester.Value.Id,
            draft.Priority,
            draft.Type,
            channel,
            draft.Tags,
            actor,
            now);
        if (draft.CustomFields is not null)
        {
            ticket.InitializeCustomFields(draft.CustomFields);
        }

        ticket.RouteTo(draft.TeamId, assignment.Value.TeamName, null, actor, now);
        ticket.AssignTo(draft.AssigneeId, assignment.Value.AssigneeName, null, actor, now);
        if (actor is { Type: ActorType.Staff, Id: { } staffId })
        {
            ticket.Watch(staffId);
        }

        var firstMessage = ticket.Messages.First();
        foreach (var attachment in attachments.Value)
        {
            attachment.AttachTo(firstMessage);
        }

        db.Tickets.Add(ticket);
        return ticket;
    }

    public async Task<Result<TicketMessage>> AddMessageAsync(
        Ticket ticket,
        Actor author,
        string body,
        bool isInternal,
        IReadOnlyCollection<Guid>? attachmentIds,
        CancellationToken cancellationToken)
    {
        if (ticket.IsClosed)
        {
            return TicketErrors.Closed;
        }

        var attachments = await LoadAttachments(attachmentIds, author, cancellationToken);
        if (!attachments.IsSuccess)
        {
            return attachments.Error;
        }

        var message = ticket.AddMessage(author, body, isInternal, clock.GetUtcNow());
        foreach (var attachment in attachments.Value)
        {
            attachment.AttachTo(message);
        }

        return message;
    }

    public async Task<Result<(string? AssigneeName, string? TeamName)>> ResolveAssignment(
        Guid? assigneeId,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        string? assigneeName = null;
        if (assigneeId is { } userId)
        {
            assigneeName = await db.Users
                .Where(u => u.Id == userId && u.IsActive && u.Role != Role.Viewer)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(cancellationToken);
            if (assigneeName is null)
            {
                return TicketErrors.InvalidAssignee;
            }
        }

        string? teamName = null;
        if (teamId is { } id)
        {
            teamName = await db.Teams.Where(t => t.Id == id).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken);
            if (teamName is null)
            {
                return TicketErrors.UnknownTeam;
            }
        }

        return (assigneeName, teamName);
    }

    private async Task<Result<Customer>> ResolveRequester(TicketDraft draft, CancellationToken cancellationToken)
    {
        if (draft.RequesterId is { } requesterId)
        {
            var existing = await db.Customers.FirstOrDefaultAsync(c => c.Id == requesterId, cancellationToken);
            return existing is null ? TicketErrors.UnknownRequester : existing;
        }

        if (string.IsNullOrWhiteSpace(draft.RequesterEmail))
        {
            return TicketErrors.RequesterRequired;
        }

        var email = User.NormalizeEmail(draft.RequesterEmail);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (customer is null)
        {
            customer = Customer.Create(email, draft.RequesterName);
            db.Customers.Add(customer);
        }

        return customer;
    }

    private async Task<Result<List<Attachment>>> LoadAttachments(
        IReadOnlyCollection<Guid>? attachmentIds,
        Actor owner,
        CancellationToken cancellationToken)
    {
        var ids = (attachmentIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new List<Attachment>();
        }

        if (ids.Count > MaxAttachmentsPerMessage)
        {
            return TicketErrors.InvalidAttachment;
        }

        var attachments = await db.Attachments.Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
        return attachments.Count == ids.Count && attachments.All(a => a.IsUnattachedAndOwnedBy(owner))
            ? attachments
            : TicketErrors.InvalidAttachment;
    }
}
