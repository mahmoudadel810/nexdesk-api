using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Identity;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Tickets;

/// <summary>The ticket list filter a saved view stores. Assignee is "me", "unassigned" or a user id.</summary>
public sealed record TicketViewFilter(
    IReadOnlyList<TicketStatus>? Status,
    IReadOnlyList<TicketPriority>? Priority,
    string? Assignee,
    Guid? TeamId,
    string? Tag,
    string? Search,
    string? Sort);

public sealed record SavedViewDto(Guid Id, string Name, bool IsShared, bool IsMine, TicketViewFilter Filter);

public static class SavedViewErrors
{
    public static readonly Error NotFound = Error.NotFound("view-not-found", "The view was not found.");

    public static readonly Error SharedViewsRestricted =
        Error.Forbidden("shared-views-restricted", "Only admins can create or change views shared with everyone.");
}

internal static class SavedViewMapping
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Serialize(TicketViewFilter filter) => JsonSerializer.Serialize(filter, Json);

    public static SavedViewDto ToDto(SavedView view, Guid viewerId) => new(
        view.Id,
        view.Name,
        view.IsShared,
        view.OwnerId == viewerId,
        JsonSerializer.Deserialize<TicketViewFilter>(view.FilterJson, Json)!);

    public static void AddViewRules<T>(this AbstractValidator<T> validator, Func<T, string> name, Func<T, TicketViewFilter> filter)
    {
        validator.RuleFor(c => name(c)).NotEmpty().MaximumLength(SavedView.NameMaxLength).OverridePropertyName("Name");
        validator.RuleFor(c => filter(c))
            .NotNull()
            .Must(f => f is null
                || ((f.Status ?? []).All(Enum.IsDefined)
                    && (f.Priority ?? []).All(Enum.IsDefined)
                    && (f.Assignee is null or "me" or "unassigned" || Guid.TryParse(f.Assignee, out _))
                    && (f.Tag?.Length ?? 0) <= Ticket.TagMaxLength
                    && (f.Search?.Length ?? 0) <= 200
                    && (f.Sort?.Length ?? 0) <= 40))
            .WithMessage("The filter is not valid.")
            .OverridePropertyName("Filter");
    }

    public static async Task<bool> CanShare(IAppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var role = await db.Users.Where(u => u.Id == userId).Select(u => (Role?)u.Role).FirstOrDefaultAsync(cancellationToken);
        return role is { } value && Permissions.For(value).Contains(Permissions.ViewsManageShared);
    }
}

public sealed record ListSavedViewsQuery : IQuery<IReadOnlyList<SavedViewDto>>;

internal sealed class ListSavedViewsHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListSavedViewsQuery, IReadOnlyList<SavedViewDto>>
{
    public async Task<Result<IReadOnlyList<SavedViewDto>>> Handle(ListSavedViewsQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var views = await db.SavedViews.AsNoTracking()
            .Where(v => v.IsShared || v.OwnerId == userId)
            .OrderByDescending(v => v.IsShared)
            .ThenBy(v => v.Name)
            .ToListAsync(cancellationToken);
        return views.Select(v => SavedViewMapping.ToDto(v, userId)).ToList();
    }
}

public sealed record CreateSavedViewCommand(string Name, bool IsShared, TicketViewFilter Filter) : ICommand<SavedViewDto>;

internal sealed class CreateSavedViewValidator : AbstractValidator<CreateSavedViewCommand>
{
    public CreateSavedViewValidator() => this.AddViewRules(c => c.Name, c => c.Filter);
}

internal sealed class CreateSavedViewHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<CreateSavedViewCommand, SavedViewDto>
{
    public async Task<Result<SavedViewDto>> Handle(CreateSavedViewCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        if (command.IsShared && !await SavedViewMapping.CanShare(db, userId, cancellationToken))
        {
            return SavedViewErrors.SharedViewsRestricted;
        }

        var view = SavedView.Create(userId, command.Name, command.IsShared, SavedViewMapping.Serialize(command.Filter));
        db.SavedViews.Add(view);
        await db.SaveChangesAsync(cancellationToken);
        return SavedViewMapping.ToDto(view, userId);
    }
}

public sealed record UpdateSavedViewCommand(Guid ViewId, string Name, bool IsShared, TicketViewFilter Filter)
    : ICommand<SavedViewDto>;

internal sealed class UpdateSavedViewValidator : AbstractValidator<UpdateSavedViewCommand>
{
    public UpdateSavedViewValidator() => this.AddViewRules(c => c.Name, c => c.Filter);
}

internal sealed class UpdateSavedViewHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<UpdateSavedViewCommand, SavedViewDto>
{
    public async Task<Result<SavedViewDto>> Handle(UpdateSavedViewCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var view = await SavedViewAccess.FindEditable(db, command.ViewId, userId, cancellationToken);
        if (!view.IsSuccess)
        {
            return view.Error;
        }

        if (command.IsShared && !await SavedViewMapping.CanShare(db, userId, cancellationToken))
        {
            return SavedViewErrors.SharedViewsRestricted;
        }

        view.Value.Update(command.Name, command.IsShared, SavedViewMapping.Serialize(command.Filter));
        await db.SaveChangesAsync(cancellationToken);
        return SavedViewMapping.ToDto(view.Value, userId);
    }
}

public sealed record DeleteSavedViewCommand(Guid ViewId) : ICommand<Success>;

internal sealed class DeleteSavedViewHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<DeleteSavedViewCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteSavedViewCommand command, CancellationToken cancellationToken)
    {
        var view = await SavedViewAccess.FindEditable(db, command.ViewId, currentUser.RequireUserId(), cancellationToken);
        if (!view.IsSuccess)
        {
            return view.Error;
        }

        db.SavedViews.Remove(view.Value);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

internal static class SavedViewAccess
{
    /// <summary>A view can be changed by its owner, and a shared one also by anyone who manages shared views.</summary>
    public static async Task<Result<SavedView>> FindEditable(
        IAppDbContext db,
        Guid viewId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var view = await db.SavedViews.FirstOrDefaultAsync(
            v => v.Id == viewId && (v.IsShared || v.OwnerId == userId),
            cancellationToken);
        if (view is null)
        {
            return SavedViewErrors.NotFound;
        }

        return view.OwnerId == userId || await SavedViewMapping.CanShare(db, userId, cancellationToken)
            ? view
            : SavedViewErrors.SharedViewsRestricted;
    }
}
