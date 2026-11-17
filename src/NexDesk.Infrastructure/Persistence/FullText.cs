using System.Text;

namespace NexDesk.Infrastructure.Persistence;

internal static class FullText
{
    /// <summary>Text search configuration without stemming, so it works for English and Arabic alike.</summary>
    public const string Config = "simple";

    /// <summary>Name of the generated tsvector column on every searchable table.</summary>
    public const string SearchVector = "SearchVector";

    /// <summary>
    /// Builds a tsquery in which every word must match and the last one matches as a prefix, so results
    /// appear while typing. Returns null when the term holds no letters or digits.
    /// </summary>
    public static string? PrefixQuery(string term)
    {
        var words = Words(term);
        return words.Count == 0
            ? null
            : string.Join(" & ", words.Select((word, index) => index == words.Count - 1 ? $"{word}:*" : word));
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
