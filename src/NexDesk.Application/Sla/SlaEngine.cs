using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Sla;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Sla;

/// <summary>
/// Keeps ticket deadlines up to date. It runs before every save that touches a ticket, so no code path
/// (agent, portal, email, automation, bulk) can change a priority or status without the SLA following.
/// </summary>
public sealed class SlaEngine(IAppDbContext db, ITenantContext tenant, TimeProvider clock) : ITicketSaveHook
{
    // Cached per tenant, not per scope: a job or the seeder may switch tenants inside one scope.
    private readonly Dictionary<Guid, BusinessCalendar> _calendars = [];
    private readonly Dictionary<Guid, Dictionary<TicketPriority, SlaPolicy>> _policies = [];

    public async Task BeforeSaveAsync(IReadOnlyCollection<Ticket> tickets, CancellationToken cancellationToken)
    {
        if (tickets.Count == 0 || tenant.TenantId is not { } tenantId)
        {
            return;
        }

        var calendar = await CalendarAsync(cancellationToken);
        if (!_policies.TryGetValue(tenantId, out var policies))
        {
            policies = await db.SlaPolicies.AsNoTracking().ToDictionaryAsync(p => p.Priority, cancellationToken);
            _policies[tenantId] = policies;
        }

        var now = clock.GetUtcNow();
        foreach (var ticket in tickets)
        {
            ticket.SyncSla(policies.GetValueOrDefault(ticket.Priority), calendar, now);
        }
    }

    /// <summary>The working calendar of the tenant in scope: time zone, opening hours and holidays.</summary>
    public async Task<BusinessCalendar> CalendarAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId
            ?? throw new InvalidOperationException("A business calendar belongs to a tenant; none is in scope.");
        if (_calendars.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var timezone = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => t.Timezone)
            .FirstAsync(cancellationToken);
        var week = await db.BusinessHours.AsNoTracking().ToListAsync(cancellationToken);
        var holidays = await db.Holidays.AsNoTracking().Select(h => h.Date).ToListAsync(cancellationToken);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var found) ? found : TimeZoneInfo.Utc;
        return _calendars[tenantId] = new BusinessCalendar(zone, week, holidays);
    }

    /// <summary>
    /// Finds tickets of the tenant in scope whose deadlines are close or have passed, records breaches and
    /// raises the events that notify people. Returns how many tickets were touched.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The longest warning window is four hours, so anything due later cannot need attention yet.
        var horizon = now + Ticket.WarningWindow(int.MaxValue);
        var candidates = await db.Tickets
            .Where(t => t.Status != TicketStatus.Closed)
            .Where(t =>
                (t.FirstRespondedAt == null && t.FirstResponseBreachedAt == null
                    && t.FirstResponseDueAt != null && t.FirstResponseDueAt <= horizon
                    && (t.FirstResponseWarnedAt == null || t.FirstResponseDueAt <= now))
                || (t.ResolutionBreachedAt == null
                    && t.ResolutionDueAt != null && t.ResolutionDueAt <= horizon
                    && (t.ResolutionWarnedAt == null || t.ResolutionDueAt <= now)))
            .OrderBy(t => t.Number)
            .Take(500)
            .ToListAsync(cancellationToken);

        var touched = 0;
        foreach (var ticket in candidates)
        {
            var changed = false;
            foreach (var metric in Enum.GetValues<SlaMetric>())
            {
                if (ticket.RecordBreachIfOverdue(metric, now) is { } breach)
                {
                    db.SlaBreaches.Add(breach);
                    changed = true;
                }
                else if (ticket.WarnIfApproaching(metric, now))
                {
                    changed = true;
                }
            }

            touched += changed ? 1 : 0;
        }

        if (touched > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return touched;
    }
}
