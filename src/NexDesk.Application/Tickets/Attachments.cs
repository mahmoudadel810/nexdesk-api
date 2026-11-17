using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record UploadAttachmentCommand(string FileName, string ContentType, long SizeBytes, Stream Content)
    : ICommand<AttachmentDto>;

internal sealed class UploadAttachmentHandler(AttachmentStore store, ICurrentUser currentUser)
    : ICommandHandler<UploadAttachmentCommand, AttachmentDto>
{
    public Task<Result<AttachmentDto>> Handle(UploadAttachmentCommand command, CancellationToken cancellationToken) =>
        store.UploadAsync(
            currentUser.AsActor(), command.FileName, command.ContentType, command.SizeBytes, command.Content, cancellationToken);
}

/// <summary>Validates and stores an uploaded file for whoever uploads it: an agent or a portal customer.</summary>
public sealed class AttachmentStore(IAppDbContext db, ITenantContext tenant, IFileStorage storage, TimeProvider clock)
{
    public async Task<Result<AttachmentDto>> UploadAsync(
        NexDesk.Domain.Common.Actor uploader,
        string? fileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (sizeBytes is <= 0 or > Attachment.MaxSizeBytes)
        {
            return TicketErrors.FileTooLarge;
        }

        var name = Path.GetFileName(fileName ?? string.Empty);
        if (name.Length is 0 or > Attachment.FileNameMaxLength || !Attachment.IsAllowedFileName(name))
        {
            return TicketErrors.FileTypeBlocked;
        }

        var attachment = Attachment.Create(tenant.TenantId!.Value, name, contentType, sizeBytes, uploader, clock.GetUtcNow());
        await storage.SaveAsync(attachment.StorageKey, content, cancellationToken);
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return new AttachmentDto(attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes);
    }
}

public sealed record AttachmentContent(string FileName, string ContentType, Stream Content);

public sealed record DownloadAttachmentQuery(Guid AttachmentId) : IQuery<AttachmentContent>;

internal sealed class DownloadAttachmentHandler(IAppDbContext db, IFileStorage storage)
    : IQueryHandler<DownloadAttachmentQuery, AttachmentContent>
{
    public async Task<Result<AttachmentContent>> Handle(DownloadAttachmentQuery query, CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == query.AttachmentId, cancellationToken);
        if (attachment is null
            || (attachment.TicketId is { } ticketId && !await db.Tickets.AnyAsync(t => t.Id == ticketId, cancellationToken)))
        {
            return TicketErrors.AttachmentNotFound;
        }

        var content = await storage.OpenReadAsync(attachment.StorageKey, cancellationToken);
        return content is null
            ? TicketErrors.AttachmentNotFound
            : new AttachmentContent(attachment.FileName, attachment.ContentType, content);
    }
}
