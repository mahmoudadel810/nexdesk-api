using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexDesk.Application.Common;
using NexDesk.Domain.Common;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Tickets;

internal sealed class TicketNumberAllocator(AppDbContext db, ITenantContext tenant) : ITicketNumberAllocator
{
    public async Task<int> NextAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("A ticket number can only be allocated inside a tenant scope.");

        // One statement, so two concurrent tickets of the same tenant can never share a number.
        var numbers = await db.Database
            .SqlQuery<int>($"""
                INSERT INTO ticket_sequences (tenant_id, last_number) VALUES ({tenantId}, 1)
                ON CONFLICT (tenant_id) DO UPDATE SET last_number = ticket_sequences.last_number + 1
                RETURNING last_number AS "Value"
                """)
            .ToListAsync(cancellationToken);
        return numbers[0];
    }
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Folder for uploaded files. A relative path is resolved against the content root.</summary>
    [Required]
    public string RootPath { get; set; } = "storage";
}

internal sealed class LocalDiskFileStorage(IOptions<StorageOptions> options, IHostEnvironment environment) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(
        Path.IsPathRooted(options.Value.RootPath)
            ? options.Value.RootPath
            : Path.Combine(environment.ContentRootPath, options.Value.RootPath));

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        File.Delete(Resolve(key));
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new InvalidOperationException("The storage key points outside the storage root.");
    }
}

public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the handlers of each event after the change is committed. A failing handler is logged and does not
/// undo the change or stop the other handlers.
/// </summary>
internal sealed class DomainEventDispatcher(IServiceProvider services, ILogger<DomainEventDispatcher> logger)
    : IDomainEventDispatcher
{
    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in domainEvents)
        {
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(domainEvent.GetType());
            foreach (var handler in services.GetServices(handlerType))
            {
                try
                {
                    await (Task)handlerType
                        .GetMethod(nameof(IDomainEventHandler<IDomainEvent>.Handle))!
                        .Invoke(handler, [domainEvent, cancellationToken])!;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(
                        exception.InnerException ?? exception,
                        "Handler {Handler} failed for {Event}",
                        handler!.GetType().Name,
                        domainEvent.GetType().Name);
                }
            }
        }
    }
}
