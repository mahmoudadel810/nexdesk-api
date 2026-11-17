using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Auth;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Common;
using NexDesk.Domain.Customers;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Inbound;

public sealed record ProcessInboundEmailCommand(string From, string? FromName, string Subject, string Text)
    : ICommand<InboundEmailResult>;

public enum InboundEmailOutcome
{
    TicketCreated,
    ReplyAdded,
}

public sealed record InboundEmailResult(InboundEmailOutcome Outcome, Guid TicketId, int TicketNumber);

internal sealed class ProcessInboundEmailValidator : AbstractValidator<ProcessInboundEmailCommand>
{
    public ProcessInboundEmailValidator()
    {
        RuleFor(c => c.From).ValidEmail();
        RuleFor(c => c.FromName).MaximumLength(Customer.FullNameMaxLength);
        RuleFor(c => c.Subject).NotEmpty().MaximumLength(500);
        RuleFor(c => c.Text).NotEmpty().MaximumLength(100_000);
    }
}

/// <summary>
/// Turns an email into a ticket, or into a reply when its subject carries a ticket reference such as
/// <c>[#42]</c> and the sender is that ticket's requester.
/// </summary>
internal sealed partial class ProcessInboundEmailHandler(IAppDbContext db, TicketWriter writer)
    : ICommandHandler<ProcessInboundEmailCommand, InboundEmailResult>
{
    public Task<Result<InboundEmailResult>> Handle(ProcessInboundEmailCommand command, CancellationToken cancellationToken) =>
        Concurrency.RetryAsync(db, () => Attempt(command, cancellationToken));

    private async Task<Result<InboundEmailResult>> Attempt(ProcessInboundEmailCommand command, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(command.From);
        var body = InboundEmailText.StripQuotedReply(command.Text);
        if (body.Length == 0)
        {
            body = command.Text.Trim();
        }

        body = body.Length > TicketMessage.BodyMaxLength ? body[..TicketMessage.BodyMaxLength] : body;

        var reference = TicketReference().Match(command.Subject);
        if (reference.Success && int.TryParse(reference.Groups[1].Value, out var number))
        {
            var sender = await db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
            var existing = sender is null
                ? null
                : await db.Tickets.Include(t => t.Watchers).FirstOrDefaultAsync(
                    t => t.Number == number && t.RequesterId == sender.Id && t.Status != TicketStatus.Closed,
                    cancellationToken);
            if (existing is not null)
            {
                var message = await writer.AddMessageAsync(
                    existing, Actor.Customer(sender!.Id), body, isInternal: false, attachmentIds: null, cancellationToken);
                if (!message.IsSuccess)
                {
                    return message.Error;
                }

                await db.SaveChangesAsync(cancellationToken);
                return new InboundEmailResult(InboundEmailOutcome.ReplyAdded, existing.Id, existing.Number);
            }
        }

        // No usable reference (or a closed ticket, or a different sender): start a new conversation.
        var subject = InboundEmailText.CleanSubject(command.Subject);
        var created = await writer.CreateAsync(
            new TicketDraft(
                subject.Length > Ticket.SubjectMaxLength ? subject[..Ticket.SubjectMaxLength] : subject,
                body,
                RequesterId: null,
                email,
                command.FromName,
                TicketPriority.Normal,
                TicketType.Question,
                AssigneeId: null,
                TeamId: null,
                Tags: null,
                AttachmentIds: null),
            Actor.System,
            TicketChannel.Email,
            cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new InboundEmailResult(InboundEmailOutcome.TicketCreated, created.Value.Id, created.Value.Number);
    }

    [GeneratedRegex(@"\[#(\d{1,9})\]")]
    private static partial Regex TicketReference();
}

public static partial class InboundEmailText
{
    /// <summary>Removes reply prefixes and ticket references, leaving the subject the customer wrote.</summary>
    public static string CleanSubject(string subject)
    {
        var cleaned = ReferenceTag().Replace(subject, string.Empty);
        string previous;
        do
        {
            previous = cleaned;
            cleaned = ReplyPrefix().Replace(cleaned.Trim(), string.Empty);
        }
        while (cleaned != previous);

        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? "(no subject)" : cleaned;
    }

    /// <summary>Keeps what the sender wrote and drops the quoted earlier message that mail clients append.</summary>
    public static string StripQuotedReply(string text)
    {
        var kept = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (QuoteHeader().IsMatch(trimmed) || trimmed.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!trimmed.StartsWith('>'))
            {
                kept.Add(line.TrimEnd());
            }
        }

        return string.Join('\n', kept).Trim();
    }

    [GeneratedRegex(@"\[#\d{1,9}\]")]
    private static partial Regex ReferenceTag();

    [GeneratedRegex(@"^(re|fw|fwd|رد)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"^On .{5,200} wrote:$", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteHeader();
}
