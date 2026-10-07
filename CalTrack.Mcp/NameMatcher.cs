namespace CalTrack.Mcp;

/// <summary>
/// "Did you mean" for menu names. Deliberately only SUGGESTS: the model still has to pick
/// an exact name (or ask), so a fuzzy match can never silently log the wrong food.
/// </summary>
public static class NameMatcher
{
    public static List<string> Closest(string query, IEnumerable<string> names, int max)
    {
        var q = Normalize(query);
        var qTokens = Tokens(q);
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Score: Score(q, qTokens, Normalize(n))))
            .Where(x => x.Score >= 0.34)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    private static double Score(string q, HashSet<string> qTokens, string n)
    {
        if (q.Length == 0 || n.Length == 0) return 0;
        if (n.Contains(q) || q.Contains(n)) return 0.9;
        var nTokens = Tokens(n);
        var shared = qTokens.Count(t => nTokens.Any(u => u == t || (t.Length > 3 && (u.StartsWith(t) || t.StartsWith(u)))));
        var tokenScore = qTokens.Count == 0 ? 0 : shared / (double)Math.Max(qTokens.Count, nTokens.Count);
        var editScore = 1 - Levenshtein(q, n) / (double)Math.Max(q.Length, n.Length);
        return Math.Max(tokenScore, editScore);
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray()).Trim();

    private static HashSet<string> Tokens(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            // "eggs" should meet "egg": compare singular-ish stems.
            .Select(t => t.Length > 3 && t.EndsWith('s') ? t[..^1] : t)
            .ToHashSet();

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
