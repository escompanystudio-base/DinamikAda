using System.Globalization;
using System.Text;

namespace DinamikAda.Core.MathAndSearch;

public static class FuzzyMatcher
{
    public static int Score(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query)) return 100;
        if (string.IsNullOrWhiteSpace(target)) return 0;

        string q = NormalizeText(query.Trim());
        string t = NormalizeText(target);
        if (q.Length == 0 || t.Length == 0) return 0;

        if (t == q) return 1000;
        if (t.StartsWith(q, StringComparison.Ordinal))
        {
            return Math.Max(250, 800 - Math.Min(400, t.Length - q.Length));
        }

        int idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0)
        {
            return Math.Max(150, 600 - Math.Min(350, idx * 2));
        }

        if (q.Contains(' '))
        {
            string[] tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1)
            {
                int total = 0;
                foreach (string token in tokens)
                {
                    int tokenScore = ScoreSingle(token, t);
                    if (tokenScore <= 0) return 0;
                    total += tokenScore;
                }
                return Math.Max(1, total / tokens.Length);
            }
        }

        return ScoreSingle(q, t);
    }

    private static int ScoreSingle(string q, string t)
    {
        if (t == q) return 900;
        if (t.StartsWith(q, StringComparison.Ordinal))
        {
            return Math.Max(250, 800 - Math.Min(400, t.Length - q.Length));
        }

        int idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0)
        {
            return Math.Max(150, 600 - Math.Min(350, idx * 2));
        }

        int qi = 0;
        int consecutive = 0;
        int score = 0;
        for (int ti = 0; ti < t.Length && qi < q.Length; ti++)
        {
            if (t[ti] == q[qi])
            {
                score += 18 + consecutive * 12;
                if (ti == 0 || t[ti - 1] is ' ' or '-' or '_' or '.' or '/' or '\\')
                {
                    score += 20;
                }
                consecutive++;
                qi++;
            }
            else
            {
                consecutive = 0;
            }
        }

        if (qi != q.Length) return 0;
        int penalty = Math.Min(Math.Max(0, score - 10), Math.Max(0, t.Length - q.Length));
        return Math.Max(1, score - penalty);
    }

    private static string NormalizeText(string input)
    {
        string decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char folded = ch switch
            {
                'İ' or 'ı' => 'i',
                'Ş' or 'ş' => 's',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' => 'u',
                'Ö' or 'ö' => 'o',
                'Ç' or 'ç' => 'c',
                _ => char.ToLowerInvariant(ch)
            };
            sb.Append(folded);
        }
        return sb.ToString();
    }
}
