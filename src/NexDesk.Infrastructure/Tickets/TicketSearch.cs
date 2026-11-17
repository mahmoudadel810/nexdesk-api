using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;
using NpgsqlTypes;

namespace NexDesk.Infrastructure.Tickets;

/// <summary>Full-text search over ticket subjects and conversations, using PostgreSQL tsvector columns.</summary>
internal sealed class TicketSearch(AppDbContext db) : ITicketSearch
{
    public IQueryable<Ticket> Matching(IQueryable<Ticket> tickets, string term)
    {
        var trimmed = term.Trim();
        var query = FullText.PrefixQuery(trimmed);
        var number = int.TryParse(trimmed.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (int?)null;
        if (query is null)
        {
            return tickets.Where(t => t.Number == number);
        }

        var lowered = trimmed.ToLowerInvariant();
        return tickets.Where(t =>
            EF.Property<NpgsqlTsVector>(t, FullText.SearchVector)
                .Matches(EF.Functions.ToTsQuery(FullText.Config, query))
            || db.TicketMessages.Any(m => m.TicketId == t.Id
                && EF.Property<NpgsqlTsVector>(m, FullText.SearchVector)
                    .Matches(EF.Functions.ToTsQuery(FullText.Config, query)))
            || db.Customers.Any(c => c.Id == t.RequesterId
                && (c.FullName.ToLower().Contains(lowered) || c.Email.Contains(lowered)))
            || t.Number == number);
    }
}
