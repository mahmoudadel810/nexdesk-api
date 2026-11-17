using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Application.Tickets;
using NexDesk.Domain.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Portal;

public sealed record PortalTicketListItem(
    Guid Id,
    int Number,
    string Subject,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

public sealed record PortalMessageDto(
    Guid Id,
    bool FromSupport,
    string AuthorName,
    string Body,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AttachmentDto> Attachments);

public sealed record CsatDto(int Rating, string? Comment);

public sealed record PortalTicketDto(
    Guid Id,
    int Number,
    string Subject,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PortalMessageDto> Messages,
    bool CanReply,
    bool CanRate,
    CsatDto? Csat);

/// <summary>What a customer sees before rating through an emailed survey link.</summary>
public sealed record CsatSurveyDto(int TicketNumber, string TicketSubject, CsatDto? Csat);

public static class CsatErrors
{
    public static readonly Error NotSolved =
        Error.Conflict("ticket-not-solved", "A request can be rated once it has been solved.");
}

internal static class PortalTicketQueries
{
    /// <summary>The ticket as its requester may see it: public messages only, no internal history.</summary>
    public static async Task<PortalTicketDto?> Load(
        IAppDbContext db,
        Guid ticketId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticketId && t.RequesterId == customerId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var workspace = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == ticket.TenantId)
            .Select(t => t.Name)
            .FirstAsync(cancellationToken);
        var timeline = await TimelineReader.Load(db, ticketId, includeInternal: false, cancellationToken);
        var csat = await db.CsatResponses.AsNoTracking()
            .Where(c => c.TicketId == ticketId)
            .Select(c => new CsatDto(c.Rating, c.Comment))
            .FirstOrDefaultAsync(cancellationToken);

        return new PortalTicketDto(
            ticket.Id,
            ticket.Number,
            ticket.Subject,
            ticket.Status,
            ticket.CreatedAt,
            [
                .. timeline.Select(item => new PortalMessageDto(
                    item.Id,
                    item.Actor.Type == ActorType.Staff,
                    item.Actor.Name ?? workspace,
                    item.Body ?? string.Empty,
                    item.CreatedAt,
                    item.Attachments)),
            ],
            CanReply: !ticket.IsClosed,
            CanRate: ticket.Status is TicketStatus.Solved or TicketStatus.Closed,
            csat);
    }
}

public sealed record ListPortalTicketsQuery(int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<PortalTicketListItem>>, IPagedRequest;

internal sealed class ListPortalTicketsValidator : AbstractValidator<ListPortalTicketsQuery>
{
    public ListPortalTicketsValidator() => this.AddPagingRules();
}

internal sealed class ListPortalTicketsHandler(IAppDbContext db, ICurrentCustomer currentCustomer)
    : IQueryHandler<ListPortalTicketsQuery, PagedResult<PortalTicketListItem>>
{
    public async Task<Result<PagedResult<PortalTicketListItem>>> Handle(
        ListPortalTicketsQuery query,
        CancellationToken cancellationToken)
    {
        if (currentCustomer.CustomerId is not { } customerId)
        {
            return PortalErrors.NotSignedIn;
        }

        return await db.Tickets.AsNoTracking()
            .Where(t => t.RequesterId == customerId)
            .OrderByDescending(t => t.LastActivityAt)
            .ThenByDescending(t => t.Number)
            .Select(t => new PortalTicketListItem(t.Id, t.Number, t.Subject, t.Status, t.CreatedAt, t.LastActivityAt))
            .ToPagedAsync(query, cancellationToken);
    }
}

public sealed record GetPortalTicketQuery(Guid TicketId) : IQuery<PortalTicketDto>;

internal sealed class GetPortalTicketHandler(IAppDbContext db, ICurrentCustomer currentCustomer)
    : IQueryHandler<GetPortalTicketQuery, PortalTicketDto>
{
    public async Task<Result<PortalTicketDto>> Handle(GetPortalTicketQuery query, CancellationToken cancellationToken)
    {
        if (currentCustomer.CustomerId is not { } customerId)
        {
            return PortalErrors.NotSignedIn;
        }

        var ticket = await PortalTicketQueries.Load(db, query.TicketId, customerId, cancellationToken);
        return ticket is null ? TicketErrors.NotFound : ticket;
    }
}

public sealed record CreatePortalTicketCommand(string Subject, string Description, IReadOnlyCollection<Guid>? AttachmentIds)
    : ICommand<PortalTicketDto>;

internal sealed class CreatePortalTicketValidator : AbstractValidator<CreatePortalTicketCommand>
{
    public CreatePortalTicketValidator()
    {
        RuleFor(c => c.Subject).NotEmpty().MaximumLength(Ticket.SubjectMaxLength);
        RuleFor(c => c.Description).NotEmpty().MaximumLength(TicketMessage.BodyMaxLength);
    }
}

internal sealed class CreatePortalTicketHandler(IAppDbContext db, ICurrentCustomer currentCustomer, TicketWriter writer)
    : ICommandHandler<CreatePortalTicketCommand, PortalTicketDto>
{
    public async Task<Result<PortalTicketDto>> Handle(CreatePortalTicketCommand command, CancellationToken cancellationToken)
    {
        if (currentCustomer.CustomerId is not { } customerId)
        {
            return PortalErrors.NotSignedIn;
        }

        var created = await writer.CreateAsync(
            new TicketDraft(
                command.Subject,
                command.Description,
                customerId,
                RequesterEmail: null,
                RequesterName: null,
                TicketPriority.Normal,
                TicketType.Question,
                AssigneeId: null,
                TeamId: null,
                Tags: null,
                command.AttachmentIds),
            Actor.Customer(customerId),
            TicketChannel.Portal,
            cancellationToken);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await PortalTicketQueries.Load(db, created.Value.Id, customerId, cancellationToken))!;
    }
}

public sealed record AddPortalReplyCommand(Guid TicketId, string Body, IReadOnlyCollection<Guid>? AttachmentIds)
    : ICommand<PortalTicketDto>;

internal sealed class AddPortalReplyValidator : AbstractValidator<AddPortalReplyCommand>
{
    public AddPortalReplyValidator() => RuleFor(c => c.Body).NotEmpty().MaximumLength(TicketMessage.BodyMaxLength);
}

internal sealed class AddPortalReplyHandler(IAppDbContext db, ICurrentCustomer currentCustomer, TicketWriter writer)
    : ICommandHandler<AddPortalReplyCommand, PortalTicketDto>
{
    public Task<Result<PortalTicketDto>> Handle(AddPortalReplyCommand command, CancellationToken cancellationToken) =>
        Concurrency.RetryAsync(db, () => Attempt(command, cancellationToken));

    private async Task<Result<PortalTicketDto>> Attempt(AddPortalReplyCommand command, CancellationToken cancellationToken)
    {
        if (currentCustomer.CustomerId is not { } customerId)
        {
            return PortalErrors.NotSignedIn;
        }

        var ticket = await db.Tickets.Include(t => t.Watchers)
            .FirstOrDefaultAsync(t => t.Id == command.TicketId && t.RequesterId == customerId, cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        var message = await writer.AddMessageAsync(
            ticket, Actor.Customer(customerId), command.Body, isInternal: false, command.AttachmentIds, cancellationToken);
        if (!message.IsSuccess)
        {
            return message.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await PortalTicketQueries.Load(db, ticket.Id, customerId, cancellationToken))!;
    }
}

public sealed record UploadPortalAttachmentCommand(string FileName, string ContentType, long SizeBytes, Stream Content)
    : ICommand<AttachmentDto>;

internal sealed class UploadPortalAttachmentHandler(AttachmentStore store, ICurrentCustomer currentCustomer)
    : ICommandHandler<UploadPortalAttachmentCommand, AttachmentDto>
{
    public async Task<Result<AttachmentDto>> Handle(UploadPortalAttachmentCommand command, CancellationToken cancellationToken) =>
        currentCustomer.CustomerId is { } customerId
            ? await store.UploadAsync(
                Actor.Customer(customerId), command.FileName, command.ContentType, command.SizeBytes, command.Content, cancellationToken)
            : PortalErrors.NotSignedIn;
}

public sealed record DownloadPortalAttachmentQuery(Guid AttachmentId) : IQuery<AttachmentContent>;

internal sealed class DownloadPortalAttachmentHandler(IAppDbContext db, ICurrentCustomer currentCustomer, IFileStorage storage)
    : IQueryHandler<DownloadPortalAttachmentQuery, AttachmentContent>
{
    public async Task<Result<AttachmentContent>> Handle(DownloadPortalAttachmentQuery query, CancellationToken cancellationToken)
    {
        if (currentCustomer.CustomerId is not { } customerId)
        {
            return PortalErrors.NotSignedIn;
        }

        // Only files on public messages of the customer's own tickets; never files on internal notes.
        var attachment = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == query.AttachmentId
                && db.Tickets.Any(t => t.Id == a.TicketId && t.RequesterId == customerId)
                && db.TicketMessages.Any(m => m.Id == a.MessageId && !m.IsInternal))
            .FirstOrDefaultAsync(cancellationToken);
        var content = attachment is null ? null : await storage.OpenReadAsync(attachment.StorageKey, cancellationToken);
        return content is null
            ? TicketErrors.AttachmentNotFound
            : new AttachmentContent(attachment!.FileName, attachment.ContentType, content);
    }
}

public sealed record RatePortalTicketCommand(Guid TicketId, int Rating, string? Comment) : ICommand<PortalTicketDto>;

internal sealed class RatePortalTicketValidator : AbstractValidator<RatePortalTicketCommand>
{
    public RatePortalTicketValidator()
    {
        RuleFor(c => c.Rating).InclusiveBetween(CsatResponse.MinRating, CsatResponse.MaxRating);
        RuleFor(c => c.Comment).MaximumLength(CsatResponse.CommentMaxLength);
    }
}

internal sealed class RatePortalTicketHandler(IAppDbContext db, ICurrentCustomer currentCustomer, CsatRecorder recorder)
    : ICommandHandler<RatePortalTicketCommand, PortalTicketDto>
{
    public async Task<Result<PortalTicketDto>> Handle(RatePortalTicketCommand command, CancellationToken cancellationToken)
    {
        if (currentCustomer.CustomerId is not { } customerId)
        {
            return PortalErrors.NotSignedIn;
        }

        var ticket = await db.Tickets.FirstOrDefaultAsync(
            t => t.Id == command.TicketId && t.RequesterId == customerId,
            cancellationToken);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        var rated = await recorder.RateAsync(ticket, command.Rating, command.Comment, cancellationToken);
        return rated.IsSuccess
            ? (await PortalTicketQueries.Load(db, ticket.Id, customerId, cancellationToken))!
            : rated.Error;
    }
}

/// <summary>Stores a satisfaction rating for a ticket, whichever way the customer arrived.</summary>
public sealed class CsatRecorder(IAppDbContext db, TimeProvider clock)
{
    public async Task<Result<Success>> RateAsync(Ticket ticket, int rating, string? comment, CancellationToken cancellationToken)
    {
        if (ticket.Status is not (TicketStatus.Solved or TicketStatus.Closed))
        {
            return CsatErrors.NotSolved;
        }

        var existing = await db.CsatResponses.FirstOrDefaultAsync(c => c.TicketId == ticket.Id, cancellationToken);
        if (ticket.Rate(existing, rating, comment, ticket.RequesterId, clock.GetUtcNow()) is { } created)
        {
            db.CsatResponses.Add(created);
        }

        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record GetCsatSurveyQuery(string Token) : IQuery<CsatSurveyDto>;

public sealed record SubmitCsatSurveyCommand(string Token, int Rating, string? Comment) : ICommand<CsatSurveyDto>;

internal sealed class SubmitCsatSurveyValidator : AbstractValidator<SubmitCsatSurveyCommand>
{
    public SubmitCsatSurveyValidator()
    {
        RuleFor(c => c.Rating).InclusiveBetween(CsatResponse.MinRating, CsatResponse.MaxRating);
        RuleFor(c => c.Comment).MaximumLength(CsatResponse.CommentMaxLength);
    }
}

/// <summary>Lets a customer rate from the link in the "solved" email without signing in.</summary>
internal sealed class CsatSurveyHandlers(IAppDbContext db, ITokenService tokens, CsatRecorder recorder, TimeProvider clock)
    : IQueryHandler<GetCsatSurveyQuery, CsatSurveyDto>, ICommandHandler<SubmitCsatSurveyCommand, CsatSurveyDto>
{
    public async Task<Result<CsatSurveyDto>> Handle(GetCsatSurveyQuery query, CancellationToken cancellationToken)
    {
        var ticket = await FindTicket(query.Token, track: false, cancellationToken);
        return ticket is null ? Auth.AuthErrors.InvalidToken : await Describe(ticket, cancellationToken);
    }

    public async Task<Result<CsatSurveyDto>> Handle(SubmitCsatSurveyCommand command, CancellationToken cancellationToken)
    {
        var ticket = await FindTicket(command.Token, track: true, cancellationToken);
        if (ticket is null)
        {
            return Auth.AuthErrors.InvalidToken;
        }

        var rated = await recorder.RateAsync(ticket, command.Rating, command.Comment, cancellationToken);
        return rated.IsSuccess ? await Describe(ticket, cancellationToken) : rated.Error;
    }

    // The survey token stays valid until it expires, so a customer can change their mind.
    private async Task<Ticket?> FindTicket(string? secret, bool track, CancellationToken cancellationToken)
    {
        var hash = tokens.HashSecret(secret ?? string.Empty);
        var now = clock.GetUtcNow();
        var ticketId = await db.OneTimeTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.Purpose == OneTimeTokenPurpose.CsatSurvey && t.ExpiresAt > now)
            .Select(t => (Guid?)t.SubjectId)
            .FirstOrDefaultAsync(cancellationToken);
        var tickets = track ? db.Tickets : db.Tickets.AsNoTracking();
        return ticketId is null ? null : await tickets.FirstOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
    }

    private async Task<CsatSurveyDto> Describe(Ticket ticket, CancellationToken cancellationToken) => new(
        ticket.Number,
        ticket.Subject,
        await db.CsatResponses.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id)
            .Select(c => new CsatDto(c.Rating, c.Comment))
            .FirstOrDefaultAsync(cancellationToken));
}
