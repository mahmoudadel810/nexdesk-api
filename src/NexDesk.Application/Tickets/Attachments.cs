using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

public sealed record UploadAttachmentCommand(string FileName, string ContentType, long SizeBytes, Stream Content)
    : ICommand<AttachmentDto>;

internal sealed class UploadAttachmentHandler(
    IAppDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IFileStorage storage,
    TimeProvider clock) : ICommandHandler<UploadAttachmentCommand, AttachmentDto>
{
    public async Task<Result<AttachmentDto>> Handle(UploadAttachmentCommand command, CancellationToken cancellationToken)
    {
        if (command.SizeBytes is <= 0 or > Attachment.MaxSizeBytes)
        {
            return TicketErrors.FileTooLarge;
        }

        var fileName = Path.GetFileName(command.FileName ?? string.Empty);
        if (fileName.Length is 0 or > Attachment.FileNameMaxLength || !Attachment.IsAllowedFileName(fileName))
        {
            return TicketErrors.FileTypeBlocked;
        }

        var attachment = Attachment.Create(
            tenant.TenantId!.Value,
            fileName,
            command.ContentType,
            command.SizeBytes,
            currentUser.AsActor(),
            clock.GetUtcNow());
        await storage.SaveAsync(attachment.StorageKey, command.Content, cancellationToken);
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
