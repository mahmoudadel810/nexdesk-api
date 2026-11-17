using Microsoft.EntityFrameworkCore;
using NexDesk.Application.Common;

namespace NexDesk.Application.Tickets;

/// <summary>
/// Finds the team members a message mentions. A mention is <c>@</c> followed by someone's full name, as the
/// composer inserts it, ending at the end of the text, white space or punctuation.
/// </summary>
public static class Mentions
{
    public static IReadOnlyList<Guid> Find(string body, IEnumerable<(Guid Id, string FullName)> people)
    {
        var found = new List<Guid>();
        var taken = new bool[body.Length];

        // Longest names first, so "@Ann Lee" is never read as "@Ann" when both work here.
        foreach (var (id, name) in people.Where(p => !string.IsNullOrWhiteSpace(p.FullName)).OrderByDescending(p => p.FullName.Length))
        {
            var token = "@" + name.Trim();
            for (var at = body.IndexOf(token, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = body.IndexOf(token, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = at + token.Length;
                var startsWord = at == 0 || !char.IsLetterOrDigit(body[at - 1]);
                var endsWord = end == body.Length || !char.IsLetterOrDigit(body[end]);
                if (startsWord && endsWord && !taken[at])
                {
                    Array.Fill(taken, true, at, token.Length);
                    found.Add(id);
                    break;
                }
            }
        }

        return found.Distinct().ToList();
    }

    /// <summary>The active team members of the workspace in scope a message mentions, leaving out its author.</summary>
    public static async Task<IReadOnlyList<Guid>> InAsync(
        IAppDbContext db,
        string body,
        Guid? authorId,
        CancellationToken cancellationToken)
    {
        if (!body.Contains('@', StringComparison.Ordinal))
        {
            return [];
        }

        var people = await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(cancellationToken);
        return Find(body, people.Select(p => (p.Id, p.FullName))).Where(id => id != authorId).ToList();
    }
}
