using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;
using NexDesk.Domain.Tickets;
using NexDesk.Infrastructure.Persistence;
using NexDesk.Infrastructure.Persistence.Configurations;
using NpgsqlTypes;

namespace NexDesk.Infrastructure.Tickets;

/// <summary>Full-text search over ticket subjects and conversations, using PostgreSQL tsvector columns.</summary>
internal sealed class TicketSearch(AppDbContext db) : ITicketSearch
{
    public IQueryable<Ticket> Matching(IQueryable<Ticket> tickets, string term)
    {
        var trimmed = term.Trim();
        var words = Words(trimmed);
        var number = int.TryParse(trimmed.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (int?)null;
        if (words.Count == 0)
        {
            return tickets.Where(t => t.Number == number);
        }

        // Every word must match, and the last one matches as a prefix so results appear while typing.
        var query = string.Join(" & ", words.Select((word, index) => index == words.Count - 1 ? $"{word}:*" : word));
        var lowered = trimmed.ToLowerInvariant();
        return tickets.Where(t =>
            EF.Property<NpgsqlTsVector>(t, TicketSchema.SearchVector)
                .Matches(EF.Functions.ToTsQuery(TicketSchema.SearchConfig, query))
            || db.TicketMessages.Any(m => m.TicketId == t.Id
                && EF.Property<NpgsqlTsVector>(m, TicketSchema.SearchVector)
                    .Matches(EF.Functions.ToTsQuery(TicketSchema.SearchConfig, query)))
            || db.Customers.Any(c => c.Id == t.RequesterId
                && (c.FullName.ToLower().Contains(lowered) || c.Email.Contains(lowered)))
            || t.Number == number);
    }

    /// <summary>Splits the term into letter-and-digit words, dropping everything tsquery treats as syntax.</summary>
    private static List<string> Words(string term)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var character in term)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
