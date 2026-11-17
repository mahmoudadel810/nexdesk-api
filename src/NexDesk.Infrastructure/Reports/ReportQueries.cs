using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Application.Reports;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;

namespace NexDesk.Infrastructure.Reports;

/// <summary>
/// Report aggregations written as SQL, so the database does the counting, averaging and day bucketing.
/// <para>
/// Hand-written SQL does not pass through the model's query filters. Every statement therefore names
/// the workspace itself (<c>tenant_id = ...</c>) and skips deleted tickets itself.
/// </para>
/// </summary>
internal sealed class ReportQueries(AppDbContext db, ITenantContext tenant) : IReportQueries
{
    private Guid TenantId => tenant.TenantId
        ?? throw new InvalidOperationException("Reports can only be read inside a workspace.");

    public async Task<OverviewDto> Overview(ReportPeriod period, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var from = period.FromUtc;
        var to = period.ToUtcExclusive;
        var previousFrom = from - (to - from);
        var row = (await db.Database.SqlQuery<OverviewRow>(
            $"""
            SELECT
                count(*) FILTER (WHERE created_at >= {from} AND created_at < {to})::int AS created,
                count(*) FILTER (WHERE solved_at >= {from} AND solved_at < {to})::int AS solved,
                count(*) FILTER (WHERE created_at >= {previousFrom} AND created_at < {from})::int AS previous_created,
                count(*) FILTER (WHERE solved_at >= {previousFrom} AND solved_at < {from})::int AS previous_solved,
                count(*) FILTER (WHERE status NOT IN ('Solved', 'Closed'))::int AS open_backlog,
                (avg(extract(epoch FROM first_responded_at - created_at)) FILTER (
                    WHERE created_at >= {from} AND created_at < {to} AND first_responded_at IS NOT NULL) / 60)::float8
                    AS avg_first_response_minutes,
                (avg(extract(epoch FROM solved_at - created_at)) FILTER (
                    WHERE solved_at >= {from} AND solved_at < {to}) / 60)::float8 AS avg_resolution_minutes,
                (count(*) FILTER (WHERE created_at >= {from} AND created_at < {to}
                    AND first_responded_at IS NOT NULL AND first_response_breached_at IS NULL)
                 + count(*) FILTER (WHERE created_at >= {from} AND created_at < {to}
                    AND solved_at IS NOT NULL AND resolution_breached_at IS NULL))::int AS sla_met,
                (count(*) FILTER (WHERE created_at >= {from} AND created_at < {to} AND first_response_breached_at IS NOT NULL)
                 + count(*) FILTER (WHERE created_at >= {from} AND created_at < {to} AND resolution_breached_at IS NOT NULL))::int
                    AS sla_breached,
                (SELECT avg(rating)::float8 FROM csat_responses
                    WHERE tenant_id = {tenantId} AND created_at >= {from} AND created_at < {to}) AS csat_average,
                (SELECT count(*)::int FROM csat_responses
                    WHERE tenant_id = {tenantId} AND created_at >= {from} AND created_at < {to}) AS csat_responses
            FROM tickets
            WHERE tenant_id = {tenantId} AND deleted_at IS NULL
            """).ToListAsync(cancellationToken))[0];

        return new OverviewDto(
            new ReportRangeDto(period.From, period.To, period.Timezone, period.Bucket),
            row.Created,
            row.Solved,
            row.PreviousCreated,
            row.PreviousSolved,
            row.OpenBacklog,
            Rounded(row.AvgFirstResponseMinutes),
            Rounded(row.AvgResolutionMinutes),
            SlaComplianceRow.Percent(row.SlaMet, row.SlaBreached),
            Rounded(row.CsatAverage, 2),
            row.CsatResponses);
    }

    public async Task<IReadOnlyList<VolumePoint>> Volume(ReportPeriod period, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var (bucket, step) = Bucket(period);
        var rows = await db.Database.SqlQuery<VolumeRow>(
            $"""
            WITH buckets AS (
                SELECT generate_series(
                    date_trunc({bucket}, {period.From}::timestamp), {period.To}::timestamp, {step}::interval)::date AS day),
            created AS (
                SELECT date_trunc({bucket}, created_at AT TIME ZONE {period.Timezone})::date AS day, count(*)::int AS tickets
                FROM tickets
                WHERE tenant_id = {tenantId} AND deleted_at IS NULL
                    AND created_at >= {period.FromUtc} AND created_at < {period.ToUtcExclusive}
                GROUP BY 1),
            solved AS (
                SELECT date_trunc({bucket}, solved_at AT TIME ZONE {period.Timezone})::date AS day, count(*)::int AS tickets
                FROM tickets
                WHERE tenant_id = {tenantId} AND deleted_at IS NULL
                    AND solved_at >= {period.FromUtc} AND solved_at < {period.ToUtcExclusive}
                GROUP BY 1)
            SELECT buckets.day AS date, coalesce(created.tickets, 0) AS created, coalesce(solved.tickets, 0) AS solved
            FROM buckets
            LEFT JOIN created ON created.day = buckets.day
            LEFT JOIN solved ON solved.day = buckets.day
            ORDER BY buckets.day
            """).ToListAsync(cancellationToken);
        return rows.Select(r => new VolumePoint(r.Date, r.Created, r.Solved)).ToList();
    }

    public async Task<BacklogDto> Backlog(CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var rows = await db.Database.SqlQuery<BacklogRow>(
            $"""
            SELECT status, priority, (assignee_id IS NULL) AS unassigned, count(*)::int AS tickets
            FROM tickets
            WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND status NOT IN ('Solved', 'Closed')
            GROUP BY status, priority, (assignee_id IS NULL)
            """).ToListAsync(cancellationToken);

        TicketStatus[] open = [TicketStatus.New, TicketStatus.Open, TicketStatus.Pending, TicketStatus.OnHold];
        return new BacklogDto(
            open.Select(status => new StatusCount(status, rows.Where(r => r.Status == status.ToString()).Sum(r => r.Tickets))).ToList(),
            Enum.GetValues<TicketPriority>()
                .OrderByDescending(priority => priority)
                .Select(priority => new PriorityCount(priority, rows.Where(r => r.Priority == (int)priority).Sum(r => r.Tickets)))
                .ToList(),
            rows.Where(r => r.Unassigned).Sum(r => r.Tickets));
    }

    public async Task<IReadOnlyList<ResponseTimePoint>> ResponseTimes(ReportPeriod period, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var (bucket, step) = Bucket(period);
        var rows = await db.Database.SqlQuery<ResponseTimeRow>(
            $"""
            WITH buckets AS (
                SELECT generate_series(
                    date_trunc({bucket}, {period.From}::timestamp), {period.To}::timestamp, {step}::interval)::date AS day),
            responded AS (
                SELECT date_trunc({bucket}, created_at AT TIME ZONE {period.Timezone})::date AS day,
                    (avg(extract(epoch FROM first_responded_at - created_at)) / 60)::float8 AS minutes
                FROM tickets
                WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND first_responded_at IS NOT NULL
                    AND created_at >= {period.FromUtc} AND created_at < {period.ToUtcExclusive}
                GROUP BY 1),
            resolved AS (
                SELECT date_trunc({bucket}, solved_at AT TIME ZONE {period.Timezone})::date AS day,
                    (avg(extract(epoch FROM solved_at - created_at)) / 60)::float8 AS minutes
                FROM tickets
                WHERE tenant_id = {tenantId} AND deleted_at IS NULL
                    AND solved_at >= {period.FromUtc} AND solved_at < {period.ToUtcExclusive}
                GROUP BY 1)
            SELECT buckets.day AS date, responded.minutes AS avg_first_response_minutes, resolved.minutes AS avg_resolution_minutes
            FROM buckets
            LEFT JOIN responded ON responded.day = buckets.day
            LEFT JOIN resolved ON resolved.day = buckets.day
            ORDER BY buckets.day
            """).ToListAsync(cancellationToken);
        return rows
            .Select(r => new ResponseTimePoint(r.Date, Rounded(r.AvgFirstResponseMinutes), Rounded(r.AvgResolutionMinutes)))
            .ToList();
    }

    public async Task<SlaComplianceDto> SlaCompliance(ReportPeriod period, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var rows = await db.Database.SqlQuery<SlaRow>(
            $"""
            SELECT priority,
                count(*) FILTER (WHERE first_responded_at IS NOT NULL AND first_response_breached_at IS NULL)::int AS first_response_met,
                count(*) FILTER (WHERE first_response_breached_at IS NOT NULL)::int AS first_response_breached,
                count(*) FILTER (WHERE solved_at IS NOT NULL AND resolution_breached_at IS NULL)::int AS resolution_met,
                count(*) FILTER (WHERE resolution_breached_at IS NOT NULL)::int AS resolution_breached
            FROM tickets
            WHERE tenant_id = {tenantId} AND deleted_at IS NULL
                AND created_at >= {period.FromUtc} AND created_at < {period.ToUtcExclusive}
            GROUP BY priority
            """).ToListAsync(cancellationToken);

        var byPriority = Enum.GetValues<TicketPriority>()
            .OrderByDescending(priority => priority)
            .Select(priority =>
            {
                var row = rows.FirstOrDefault(r => r.Priority == (int)priority);
                return new SlaComplianceRow(
                    priority,
                    row?.FirstResponseMet ?? 0,
                    row?.FirstResponseBreached ?? 0,
                    row?.ResolutionMet ?? 0,
                    row?.ResolutionBreached ?? 0);
            })
            .ToList();
        return new SlaComplianceDto(
            SlaComplianceRow.Percent(byPriority.Sum(r => r.FirstResponseMet), byPriority.Sum(r => r.FirstResponseBreached)),
            SlaComplianceRow.Percent(byPriority.Sum(r => r.ResolutionMet), byPriority.Sum(r => r.ResolutionBreached)),
            byPriority);
    }

    public async Task<CsatReportDto> Csat(ReportPeriod period, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var (bucket, step) = Bucket(period);
        var ratings = await db.Database.SqlQuery<RatingRow>(
            $"""
            SELECT rating, count(*)::int AS responses
            FROM csat_responses
            WHERE tenant_id = {tenantId} AND created_at >= {period.FromUtc} AND created_at < {period.ToUtcExclusive}
            GROUP BY rating
            """).ToListAsync(cancellationToken);
        var trend = await db.Database.SqlQuery<CsatTrendRow>(
            $"""
            WITH buckets AS (
                SELECT generate_series(
                    date_trunc({bucket}, {period.From}::timestamp), {period.To}::timestamp, {step}::interval)::date AS day),
            rated AS (
                SELECT date_trunc({bucket}, created_at AT TIME ZONE {period.Timezone})::date AS day,
                    avg(rating)::float8 AS average, count(*)::int AS responses
                FROM csat_responses
                WHERE tenant_id = {tenantId} AND created_at >= {period.FromUtc} AND created_at < {period.ToUtcExclusive}
                GROUP BY 1)
            SELECT buckets.day AS date, rated.average AS average, coalesce(rated.responses, 0) AS responses
            FROM buckets
            LEFT JOIN rated ON rated.day = buckets.day
            ORDER BY buckets.day
            """).ToListAsync(cancellationToken);

        var total = ratings.Sum(r => r.Responses);
        var distribution = Enumerable.Range(CsatResponse.MinRating, CsatResponse.MaxRating)
            .Select(rating => new RatingCount(rating, ratings.FirstOrDefault(r => r.Rating == rating)?.Responses ?? 0))
            .ToList();
        return new CsatReportDto(
            total == 0 ? null : Math.Round(ratings.Sum(r => (double)r.Rating * r.Responses) / total, 2),
            total,
            total == 0 ? null : Math.Round(100.0 * ratings.Where(r => r.Rating >= 4).Sum(r => r.Responses) / total, 1),
            distribution,
            trend.Select(r => new CsatPoint(r.Date, Rounded(r.Average, 2), r.Responses)).ToList());
    }

    public async Task<IReadOnlyList<AgentRow>> Agents(ReportPeriod period, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var from = period.FromUtc;
        var to = period.ToUtcExclusive;
        var rows = await db.Database.SqlQuery<AgentSqlRow>(
            $"""
            WITH work AS (
                SELECT assignee_id,
                    count(*) FILTER (WHERE status NOT IN ('Solved', 'Closed'))::int AS assigned_open,
                    count(*) FILTER (WHERE solved_at >= {from} AND solved_at < {to})::int AS solved,
                    (avg(extract(epoch FROM first_responded_at - created_at)) FILTER (
                        WHERE created_at >= {from} AND created_at < {to} AND first_responded_at IS NOT NULL) / 60)::float8
                        AS avg_first_response_minutes,
                    (avg(extract(epoch FROM solved_at - created_at)) FILTER (
                        WHERE solved_at >= {from} AND solved_at < {to}) / 60)::float8 AS avg_resolution_minutes
                FROM tickets
                WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND assignee_id IS NOT NULL
                GROUP BY assignee_id),
            ratings AS (
                SELECT t.assignee_id, avg(c.rating)::float8 AS csat_average, count(*)::int AS csat_responses
                FROM csat_responses c
                JOIN tickets t ON t.id = c.ticket_id AND t.tenant_id = c.tenant_id
                WHERE c.tenant_id = {tenantId} AND t.deleted_at IS NULL AND t.assignee_id IS NOT NULL
                    AND c.created_at >= {from} AND c.created_at < {to}
                GROUP BY t.assignee_id)
            SELECT u.id AS user_id, u.full_name AS name,
                coalesce(work.assigned_open, 0) AS assigned_open,
                coalesce(work.solved, 0) AS solved,
                work.avg_first_response_minutes,
                work.avg_resolution_minutes,
                ratings.csat_average,
                coalesce(ratings.csat_responses, 0) AS csat_responses
            FROM users u
            LEFT JOIN work ON work.assignee_id = u.id
            LEFT JOIN ratings ON ratings.assignee_id = u.id
            WHERE u.tenant_id = {tenantId} AND u.role <> 'Viewer' AND (u.is_active OR work.assignee_id IS NOT NULL)
            ORDER BY coalesce(work.solved, 0) DESC, u.full_name
            """).ToListAsync(cancellationToken);
        return rows
            .Select(r => new AgentRow(
                r.UserId,
                r.Name,
                r.AssignedOpen,
                r.Solved,
                Rounded(r.AvgFirstResponseMinutes),
                Rounded(r.AvgResolutionMinutes),
                Rounded(r.CsatAverage, 2),
                r.CsatResponses))
            .ToList();
    }

    private static (string Bucket, string Step) Bucket(ReportPeriod period) =>
        period.Bucket == ReportBucket.Day ? ("day", "1 day") : ("week", "1 week");

    private static double? Rounded(double? value, int digits = 1) => value is { } number ? Math.Round(number, digits) : null;

    private sealed record OverviewRow(
        int Created,
        int Solved,
        int PreviousCreated,
        int PreviousSolved,
        int OpenBacklog,
        double? AvgFirstResponseMinutes,
        double? AvgResolutionMinutes,
        int SlaMet,
        int SlaBreached,
        double? CsatAverage,
        int CsatResponses);

    private sealed record VolumeRow(DateOnly Date, int Created, int Solved);

    private sealed record BacklogRow(string Status, int Priority, bool Unassigned, int Tickets);

    private sealed record ResponseTimeRow(DateOnly Date, double? AvgFirstResponseMinutes, double? AvgResolutionMinutes);

    private sealed record SlaRow(
        int Priority,
        int FirstResponseMet,
        int FirstResponseBreached,
        int ResolutionMet,
        int ResolutionBreached);

    private sealed record RatingRow(int Rating, int Responses);

    private sealed record CsatTrendRow(DateOnly Date, double? Average, int Responses);

    private sealed record AgentSqlRow(
        Guid UserId,
        string Name,
        int AssignedOpen,
        int Solved,
        double? AvgFirstResponseMinutes,
        double? AvgResolutionMinutes,
        double? CsatAverage,
        int CsatResponses);
}
