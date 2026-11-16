using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Sla;

namespace NexDesk.Application.Sla;

public sealed record BusinessDayDto(DayOfWeek Day, bool IsWorkingDay, TimeOnly StartTime, TimeOnly EndTime);

public sealed record BusinessHoursDto(string Timezone, IReadOnlyList<BusinessDayDto> Days);

internal static class BusinessHoursQueries
{
    public static async Task<BusinessHoursDto> Load(
        IAppDbContext db,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var timezone = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => t.Timezone)
            .FirstAsync(cancellationToken);
        var days = await db.BusinessHours.AsNoTracking()
            .OrderBy(d => d.Day)
            .Select(d => new BusinessDayDto(d.Day, d.IsWorkingDay, d.StartTime, d.EndTime))
            .ToListAsync(cancellationToken);
        return new BusinessHoursDto(timezone, days);
    }
}

public sealed record GetBusinessHoursQuery : IQuery<BusinessHoursDto>;

internal sealed class GetBusinessHoursHandler(IAppDbContext db, ITenantContext tenant)
    : IQueryHandler<GetBusinessHoursQuery, BusinessHoursDto>
{
    public async Task<Result<BusinessHoursDto>> Handle(GetBusinessHoursQuery query, CancellationToken cancellationToken) =>
        await BusinessHoursQueries.Load(db, tenant, cancellationToken);
}

public sealed record UpdateBusinessHoursCommand(string Timezone, IReadOnlyList<BusinessDayDto> Days)
    : ICommand<BusinessHoursDto>;

internal sealed class UpdateBusinessHoursValidator : AbstractValidator<UpdateBusinessHoursCommand>
{
    public UpdateBusinessHoursValidator()
    {
        RuleFor(c => c.Timezone)
            .NotEmpty()
            .Must(id => id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            .WithMessage("The time zone is not recognised.");
        RuleFor(c => c.Days)
            .NotNull()
            .Must(days => days is not null
                && days.Count == 7
                && days.Select(d => d.Day).Distinct().Count() == 7
                && days.All(d => Enum.IsDefined(d.Day)))
            .WithMessage("Provide exactly one entry for each day of the week.");
        RuleFor(c => c.Days)
            .Must(days => days is null || days.Any(d => d.IsWorkingDay))
            .WithMessage("At least one day must be a working day.");
        RuleForEach(c => c.Days)
            .Must(day => !day.IsWorkingDay || day.EndTime > day.StartTime)
            .WithMessage("A working day must end after it starts.");
    }
}

internal sealed class UpdateBusinessHoursHandler(IAppDbContext db, ITenantContext tenantContext)
    : ICommandHandler<UpdateBusinessHoursCommand, BusinessHoursDto>
{
    public async Task<Result<BusinessHoursDto>> Handle(
        UpdateBusinessHoursCommand command,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantContext.TenantId, cancellationToken);
        var days = await db.BusinessHours.ToListAsync(cancellationToken);
        foreach (var update in command.Days)
        {
            var day = days.FirstOrDefault(d => d.Day == update.Day);
            if (day is null)
            {
                db.BusinessHours.Add(BusinessHoursDay.Create(
                    update.Day, update.IsWorkingDay, update.StartTime, update.EndTime));
            }
            else
            {
                day.Set(update.IsWorkingDay, update.StartTime, update.EndTime);
            }
        }

        tenant.SetTimezone(command.Timezone);
        await db.SaveChangesAsync(cancellationToken);
        return await BusinessHoursQueries.Load(db, tenantContext, cancellationToken);
    }
}
