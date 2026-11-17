using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;

namespace NexDesk.Application.Reports;

/// <summary>
/// The days a report covers, both inclusive, in the workspace's own time zone, with the matching
/// instants and the size of the buckets time series are grouped into.
/// </summary>
public sealed record ReportPeriod(
    DateOnly From,
    DateOnly To,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtcExclusive,
    string Timezone,
    ReportBucket Bucket)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    /// <summary>Ranges up to this many days are shown per day; longer ones per week.</summary>
    public const int MaxDailyBuckets = 62;

    public int Days => To.DayNumber - From.DayNumber + 1;
}

public enum ReportBucket
{
    Day,
    Week,
}

public sealed record ReportRangeDto(DateOnly From, DateOnly To, string Timezone, ReportBucket Bucket);

public sealed record OverviewDto(
    ReportRangeDto Range,
    int Created,
    int Solved,
    int PreviousCreated,
    int PreviousSolved,
    int OpenBacklog,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    double? SlaCompliancePercent,
    double? CsatAverage,
    int CsatResponses);

public sealed record VolumePoint(DateOnly Date, int Created, int Solved);

public sealed record StatusCount(TicketStatus Status, int Count);

public sealed record PriorityCount(TicketPriority Priority, int Count);

/// <summary>Tickets that still need work, as they stand right now.</summary>
public sealed record BacklogDto(IReadOnlyList<StatusCount> ByStatus, IReadOnlyList<PriorityCount> ByPriority, int Unassigned);

public sealed record ResponseTimePoint(DateOnly Date, double? AvgFirstResponseMinutes, double? AvgResolutionMinutes);

public sealed record SlaComplianceRow(
    TicketPriority Priority,
    int FirstResponseMet,
    int FirstResponseBreached,
    int ResolutionMet,
    int ResolutionBreached)
{
    public double? FirstResponsePercent => Percent(FirstResponseMet, FirstResponseBreached);

    public double? ResolutionPercent => Percent(ResolutionMet, ResolutionBreached);

    public static double? Percent(int met, int breached) =>
        met + breached == 0 ? null : Math.Round(100.0 * met / (met + breached), 1);
}

public sealed record SlaComplianceDto(
    double? FirstResponsePercent,
    double? ResolutionPercent,
    IReadOnlyList<SlaComplianceRow> ByPriority);

public sealed record RatingCount(int Rating, int Count);

public sealed record CsatPoint(DateOnly Date, double? Average, int Responses);

public sealed record CsatReportDto(
    double? Average,
    int Responses,
    double? SatisfiedPercent,
    IReadOnlyList<RatingCount> Distribution,
    IReadOnlyList<CsatPoint> Trend);

public sealed record AgentRow(
    Guid UserId,
    string Name,
    int AssignedOpen,
    int Solved,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    double? CsatAverage,
    int CsatResponses);

public sealed record TicketExportRow(
    int Number,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    TicketType Type,
    TicketChannel Channel,
    string RequesterEmail,
    string? Assignee,
    string? Team,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? SolvedAt,
    bool FirstResponseBreached,
    bool ResolutionBreached);

/// <summary>The aggregations behind the reports, each computed by the database in one statement.</summary>
public interface IReportQueries
{
    Task<OverviewDto> Overview(ReportPeriod period, CancellationToken cancellationToken);

    Task<IReadOnlyList<VolumePoint>> Volume(ReportPeriod period, CancellationToken cancellationToken);

    Task<BacklogDto> Backlog(CancellationToken cancellationToken);

    Task<IReadOnlyList<ResponseTimePoint>> ResponseTimes(ReportPeriod period, CancellationToken cancellationToken);

    Task<SlaComplianceDto> SlaCompliance(ReportPeriod period, CancellationToken cancellationToken);

    Task<CsatReportDto> Csat(ReportPeriod period, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentRow>> Agents(ReportPeriod period, CancellationToken cancellationToken);
}

public static class ReportErrors
{
    public static readonly Error InvalidRange = Error.Validation(
        "invalid-report-range", $"Choose a start date on or before the end date, at most {ReportPeriod.MaxDays} days apart.");

    public static readonly Error UnknownReport = Error.NotFound("report-not-found", "This report does not exist.");
}

/// <summary>Works out which days a report covers and hands the work to the database.</summary>
public sealed class ReportService(IAppDbContext db, ITenantContext tenant, IReportQueries queries, TimeProvider clock)
{
    public const int MaxExportedTickets = 10_000;

    public static readonly IReadOnlyList<string> ExportNames =
        ["volume", "backlog", "response-times", "sla-compliance", "csat", "agents", "tickets"];

    /// <summary>
    /// Turns optional dates into a period. Without dates it is the last 30 days ending today, where
    /// "today" is the workspace's, not the server's.
    /// </summary>
    public async Task<Result<ReportPeriod>> Period(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var timezoneId = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => t.Timezone)
            .FirstAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(1 - ReportPeriod.DefaultDays);
        var days = end.DayNumber - start.DayNumber + 1;
        if (days is < 1 or > ReportPeriod.MaxDays)
        {
            return ReportErrors.InvalidRange;
        }

        return new ReportPeriod(
            start,
            end,
            StartOfDay(start, zone),
            StartOfDay(end.AddDays(1), zone),
            timezoneId,
            days <= ReportPeriod.MaxDailyBuckets ? ReportBucket.Day : ReportBucket.Week);
    }

    public async Task<Result<T>> Run<T>(
        DateOnly? from,
        DateOnly? to,
        Func<IReportQueries, ReportPeriod, CancellationToken, Task<T>> report,
        CancellationToken cancellationToken)
    {
        var period = await Period(from, to, cancellationToken);
        return period.IsSuccess ? await report(queries, period.Value, cancellationToken) : period.Error;
    }

    public async Task<Result<BacklogDto>> Backlog(CancellationToken cancellationToken) =>
        await queries.Backlog(cancellationToken);

    /// <summary>Builds the CSV file of one report.</summary>
    public async Task<Result<CsvFile>> Export(string name, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var resolved = await Period(from, to, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var period = resolved.Value;
        var csv = new CsvBuilder();
        switch (name)
        {
            case "volume":
                csv.Row("Date", "Created", "Solved");
                foreach (var point in await queries.Volume(period, cancellationToken))
                {
                    csv.Row(point.Date, point.Created, point.Solved);
                }

                break;
            case "backlog":
                var backlog = await queries.Backlog(cancellationToken);
                csv.Row("Group", "Value", "Tickets");
                foreach (var status in backlog.ByStatus)
                {
                    csv.Row("Status", status.Status, status.Count);
                }

                foreach (var priority in backlog.ByPriority)
                {
                    csv.Row("Priority", priority.Priority, priority.Count);
                }

                csv.Row("Assignment", "Unassigned", backlog.Unassigned);
                break;
            case "response-times":
                csv.Row("Date", "Average first response (minutes)", "Average resolution (minutes)");
                foreach (var point in await queries.ResponseTimes(period, cancellationToken))
                {
                    csv.Row(point.Date, point.AvgFirstResponseMinutes, point.AvgResolutionMinutes);
                }

                break;
            case "sla-compliance":
                csv.Row(
                    "Priority", "First response met", "First response breached", "First response %",
                    "Resolution met", "Resolution breached", "Resolution %");
                foreach (var row in (await queries.SlaCompliance(period, cancellationToken)).ByPriority)
                {
                    csv.Row(
                        row.Priority, row.FirstResponseMet, row.FirstResponseBreached, row.FirstResponsePercent,
                        row.ResolutionMet, row.ResolutionBreached, row.ResolutionPercent);
                }

                break;
            case "csat":
                csv.Row("Date", "Average rating", "Responses");
                foreach (var point in (await queries.Csat(period, cancellationToken)).Trend)
                {
                    csv.Row(point.Date, point.Average, point.Responses);
                }

                break;
            case "agents":
                csv.Row(
                    "Agent", "Open tickets", "Solved", "Average first response (minutes)",
                    "Average resolution (minutes)", "CSAT average", "CSAT responses");
                foreach (var agent in await queries.Agents(period, cancellationToken))
                {
                    csv.Row(
                        agent.Name, agent.AssignedOpen, agent.Solved, agent.AvgFirstResponseMinutes,
                        agent.AvgResolutionMinutes, agent.CsatAverage, agent.CsatResponses);
                }

                break;
            case "tickets":
                csv.Row(
                    "Number", "Subject", "Status", "Priority", "Type", "Channel", "Requester", "Assignee", "Team",
                    "Created (UTC)", "First response (UTC)", "Solved (UTC)", "First response breached", "Resolution breached");
                foreach (var ticket in await Tickets(period, cancellationToken))
                {
                    csv.Row(
                        ticket.Number, ticket.Subject, ticket.Status, ticket.Priority, ticket.Type, ticket.Channel,
                        ticket.RequesterEmail, ticket.Assignee, ticket.Team, ticket.CreatedAt, ticket.FirstRespondedAt,
                        ticket.SolvedAt, ticket.FirstResponseBreached, ticket.ResolutionBreached);
                }

                break;
            default:
                return ReportErrors.UnknownReport;
        }

        return new CsvFile($"nexdesk-{name}-{period.From:yyyy-MM-dd}-to-{period.To:yyyy-MM-dd}.csv", csv.ToBytes());
    }

    private async Task<List<TicketExportRow>> Tickets(ReportPeriod period, CancellationToken cancellationToken)
    {
        var rows = await (
            from ticket in db.Tickets.AsNoTracking()
            where ticket.CreatedAt >= period.FromUtc && ticket.CreatedAt < period.ToUtcExclusive
            join requester in db.Customers on ticket.RequesterId equals requester.Id
            join user in db.Users on ticket.AssigneeId equals (Guid?)user.Id into assignees
            from assignee in assignees.DefaultIfEmpty()
            join team in db.Teams on ticket.TeamId equals (Guid?)team.Id into teams
            from team in teams.DefaultIfEmpty()
            orderby ticket.Number
            select new
            {
                ticket.Number,
                ticket.Subject,
                ticket.Status,
                ticket.Priority,
                ticket.Type,
                ticket.Channel,
                RequesterEmail = requester.Email,
                Assignee = assignee == null ? null : assignee.FullName,
                Team = team == null ? null : team.Name,
                ticket.CreatedAt,
                ticket.FirstRespondedAt,
                ticket.SolvedAt,
                FirstResponseBreached = ticket.FirstResponseBreachedAt != null,
                ResolutionBreached = ticket.ResolutionBreachedAt != null,
            }).Take(MaxExportedTickets).ToListAsync(cancellationToken);
        return rows
            .Select(r => new TicketExportRow(
                r.Number, r.Subject, r.Status, r.Priority, r.Type, r.Channel, r.RequesterEmail, r.Assignee, r.Team,
                r.CreatedAt, r.FirstRespondedAt, r.SolvedAt, r.FirstResponseBreached, r.ResolutionBreached))
            .ToList();
    }

    /// <summary>The instant a calendar day begins in a time zone, also across daylight-saving changes.</summary>
    private static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

public sealed record CsvFile(string FileName, byte[] Content);

/// <summary>
/// Writes RFC 4180 CSV that spreadsheets open correctly: UTF-8 with a byte order mark, quoted where
/// needed, and with text that looks like a formula defused so a ticket subject cannot run one.
/// </summary>
public sealed class CsvBuilder
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];
    private readonly StringBuilder _text = new();

    public void Row(params object?[] cells)
    {
        _text.AppendJoin(',', cells.Select(Cell)).Append("\r\n");
    }

    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_text.ToString())];

    public override string ToString() => _text.ToString();

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            string s => Defuse(s),
            bool b => b ? "Yes" : "No",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset m => m.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double n => Math.Round(n, 1).ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    private static string Defuse(string text) => text.Length > 0 && FormulaStarts.Contains(text[0]) ? "'" + text : text;
}
