using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Sla;

public sealed record SlaPolicyDto(TicketPriority Priority, int FirstResponseMinutes, int ResolutionMinutes);

public sealed record HolidayDto(Guid Id, DateOnly Date, string Name);

public sealed record SlaBreachDto(
    Guid Id,
    Guid TicketId,
    int TicketNumber,
    string TicketSubject,
    SlaMetric Metric,
    DateTimeOffset DueAt,
    DateTimeOffset BreachedAt);

public static class SlaErrors
{
    public static readonly Error HolidayNotFound = Error.NotFound("holiday-not-found", "The holiday was not found.");

    public static readonly Error HolidayExists =
        Error.Conflict("holiday-exists", "A holiday is already defined for this date.");
}

public sealed record ListSlaPoliciesQuery : IQuery<IReadOnlyList<SlaPolicyDto>>;

internal sealed class ListSlaPoliciesHandler(IAppDbContext db)
    : IQueryHandler<ListSlaPoliciesQuery, IReadOnlyList<SlaPolicyDto>>
{
    public async Task<Result<IReadOnlyList<SlaPolicyDto>>> Handle(
        ListSlaPoliciesQuery query,
        CancellationToken cancellationToken) =>
        (await db.SlaPolicies.AsNoTracking().OrderByDescending(p => p.Priority).ToListAsync(cancellationToken))
        .Select(p => new SlaPolicyDto(p.Priority, p.FirstResponseMinutes, p.ResolutionMinutes))
        .ToList();
}

public sealed record UpdateSlaPoliciesCommand(IReadOnlyList<SlaPolicyDto> Policies)
    : ICommand<IReadOnlyList<SlaPolicyDto>>;

internal sealed class UpdateSlaPoliciesValidator : AbstractValidator<UpdateSlaPoliciesCommand>
{
    public UpdateSlaPoliciesValidator()
    {
        RuleFor(c => c.Policies)
            .NotEmpty()
            .Must(policies => policies is null || policies.Select(p => p.Priority).Distinct().Count() == policies.Count)
            .WithMessage("Each priority can appear only once.");
        RuleForEach(c => c.Policies).ChildRules(policy =>
        {
            policy.RuleFor(p => p.Priority).IsInEnum();
            policy.RuleFor(p => p.FirstResponseMinutes).InclusiveBetween(1, SlaPolicy.MaxMinutes);
            policy.RuleFor(p => p.ResolutionMinutes).InclusiveBetween(1, SlaPolicy.MaxMinutes);
            policy.RuleFor(p => p.ResolutionMinutes)
                .GreaterThanOrEqualTo(p => p.FirstResponseMinutes)
                .WithMessage("The resolution target cannot be shorter than the first-response target.");
        });
    }
}

internal sealed class UpdateSlaPoliciesHandler(IAppDbContext db)
    : ICommandHandler<UpdateSlaPoliciesCommand, IReadOnlyList<SlaPolicyDto>>
{
    public async Task<Result<IReadOnlyList<SlaPolicyDto>>> Handle(
        UpdateSlaPoliciesCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await db.SlaPolicies.ToDictionaryAsync(p => p.Priority, cancellationToken);
        foreach (var update in command.Policies)
        {
            if (existing.TryGetValue(update.Priority, out var policy))
            {
                policy.SetTargets(update.FirstResponseMinutes, update.ResolutionMinutes);
            }
            else
            {
                db.SlaPolicies.Add(SlaPolicy.Create(update.Priority, update.FirstResponseMinutes, update.ResolutionMinutes));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await db.SlaPolicies.AsNoTracking().OrderByDescending(p => p.Priority).ToListAsync(cancellationToken))
            .Select(p => new SlaPolicyDto(p.Priority, p.FirstResponseMinutes, p.ResolutionMinutes))
            .ToList();
    }
}

public sealed record ListHolidaysQuery : IQuery<IReadOnlyList<HolidayDto>>;

internal sealed class ListHolidaysHandler(IAppDbContext db) : IQueryHandler<ListHolidaysQuery, IReadOnlyList<HolidayDto>>
{
    public async Task<Result<IReadOnlyList<HolidayDto>>> Handle(ListHolidaysQuery query, CancellationToken cancellationToken) =>
        await db.Holidays.AsNoTracking()
            .OrderBy(h => h.Date)
            .Select(h => new HolidayDto(h.Id, h.Date, h.Name))
            .ToListAsync(cancellationToken);
}

public sealed record AddHolidayCommand(DateOnly Date, string Name) : ICommand<HolidayDto>;

internal sealed class AddHolidayValidator : AbstractValidator<AddHolidayCommand>
{
    public AddHolidayValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Holiday.NameMaxLength);
        RuleFor(c => c.Date).InclusiveBetween(new DateOnly(2000, 1, 1), new DateOnly(2100, 12, 31));
    }
}

internal sealed class AddHolidayHandler(IAppDbContext db) : ICommandHandler<AddHolidayCommand, HolidayDto>
{
    public async Task<Result<HolidayDto>> Handle(AddHolidayCommand command, CancellationToken cancellationToken)
    {
        if (await db.Holidays.AnyAsync(h => h.Date == command.Date, cancellationToken))
        {
            return SlaErrors.HolidayExists;
        }

        var holiday = Holiday.Create(command.Date, command.Name);
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return new HolidayDto(holiday.Id, holiday.Date, holiday.Name);
    }
}

public sealed record DeleteHolidayCommand(Guid HolidayId) : ICommand<Success>;

internal sealed class DeleteHolidayHandler(IAppDbContext db) : ICommandHandler<DeleteHolidayCommand, Success>
{
    public async Task<Result<Success>> Handle(DeleteHolidayCommand command, CancellationToken cancellationToken)
    {
        var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.Id == command.HolidayId, cancellationToken);
        if (holiday is null)
        {
            return SlaErrors.HolidayNotFound;
        }

        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync(cancellationToken);
        return default(Success);
    }
}

public sealed record ListSlaBreachesQuery(int Page = 1, int PageSize = Paging.DefaultPageSize)
    : IQuery<PagedResult<SlaBreachDto>>, IPagedRequest;

internal sealed class ListSlaBreachesValidator : AbstractValidator<ListSlaBreachesQuery>
{
    public ListSlaBreachesValidator() => this.AddPagingRules();
}

internal sealed class ListSlaBreachesHandler(IAppDbContext db)
    : IQueryHandler<ListSlaBreachesQuery, PagedResult<SlaBreachDto>>
{
    public async Task<Result<PagedResult<SlaBreachDto>>> Handle(ListSlaBreachesQuery query, CancellationToken cancellationToken) =>
        await db.SlaBreaches.AsNoTracking()
            .Join(db.Tickets, b => b.TicketId, t => t.Id, (b, t) => new { b, t })
            .OrderByDescending(x => x.b.BreachedAt)
            .ThenBy(x => x.b.Id)
            .Select(x => new SlaBreachDto(x.b.Id, x.t.Id, x.t.Number, x.t.Subject, x.b.Metric, x.b.DueAt, x.b.BreachedAt))
            .ToPagedAsync(query, cancellationToken);
}
