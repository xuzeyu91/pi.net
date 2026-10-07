using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Result of a fuzzy match: whether it matched and the score (lower is better).</summary>
public readonly record struct FuzzyMatch(bool Matches, double Score);

/// <summary>Port of <c>fuzzy.ts</c>: subsequence matching with word-boundary and consecutive bonuses.</summary>
public static partial class Fuzzy
{
    [GeneratedRegex(@"^(?<letters>[a-z]+)(?<digits>[0-9]+)$")]
    private static partial Regex AlphaNumericRegex();

    [GeneratedRegex(@"^(?<digits>[0-9]+)(?<letters>[a-z]+)$")]
    private static partial Regex NumericAlphaRegex();

    [GeneratedRegex(@"[\s\-_./:]")]
    private static partial Regex WordBoundaryRegex();

    /// <summary>
    /// JS <c>/[\s/]+/</c>. The character class is spelled out because .NET's <c>\s</c> is a
    /// different set from JavaScript's (it includes U+0085 and excludes U+FEFF).
    /// </summary>
    [GeneratedRegex("[\\f\\n\\r\\t\\v\\u0020\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff/]+")]
    private static partial Regex TokenSplitRegex();

    public static FuzzyMatch Match(string query, string text)
    {
        var queryLower = JsString.ToLowerCase(query);
        var textLower = JsString.ToLowerCase(text);

        FuzzyMatch MatchQuery(string normalizedQuery)
        {
            if (normalizedQuery.Length == 0)
            {
                return new FuzzyMatch(true, 0);
            }
            if (normalizedQuery.Length > textLower.Length)
            {
                return new FuzzyMatch(false, 0);
            }

            var queryIndex = 0;
            double score = 0;
            var lastMatchIndex = -1;
            var consecutiveMatches = 0;

            while (queryIndex < normalizedQuery.Length)
            {
                var i = textLower.IndexOf(normalizedQuery[queryIndex], lastMatchIndex + 1);
                if (i == -1)
                {
                    break;
                }

                var isWordBoundary = i == 0 || WordBoundaryRegex().IsMatch(textLower[i - 1].ToString());

                if (lastMatchIndex == i - 1)
                {
                    consecutiveMatches++;
                    score -= consecutiveMatches * 5;
                }
                else
                {
                    consecutiveMatches = 0;
                    if (lastMatchIndex >= 0)
                    {
                        score += (i - lastMatchIndex - 1) * 2;
                    }
                }

                if (isWordBoundary)
                {
                    score -= 10;
                }

                score += i * 0.1;

                lastMatchIndex = i;
                queryIndex++;
            }

            if (queryIndex < normalizedQuery.Length)
            {
                return new FuzzyMatch(false, 0);
            }

            if (normalizedQuery == textLower)
            {
                score -= 100;
            }

            return new FuzzyMatch(true, score);
        }

        var primary = MatchQuery(queryLower);
        if (primary.Matches)
        {
            return primary;
        }

        var alphaNumeric = AlphaNumericRegex().Match(queryLower);
        var numericAlpha = NumericAlphaRegex().Match(queryLower);
        string swappedQuery;
        if (alphaNumeric.Success)
        {
            swappedQuery = alphaNumeric.Groups["digits"].Value + alphaNumeric.Groups["letters"].Value;
        }
        else if (numericAlpha.Success)
        {
            swappedQuery = numericAlpha.Groups["letters"].Value + numericAlpha.Groups["digits"].Value;
        }
        else
        {
            swappedQuery = "";
        }

        if (swappedQuery.Length == 0)
        {
            return primary;
        }

        var swapped = MatchQuery(swappedQuery);
        if (!swapped.Matches)
        {
            return primary;
        }

        return new FuzzyMatch(true, swapped.Score + 5);
    }

    /// <summary>Filter and sort items by fuzzy match quality (best matches first).</summary>
    public static List<T> Filter<T>(IEnumerable<T> items, string query, Func<T, string> getText)
    {
        var itemList = items.ToList();
        var trimmed = JsString.Trim(query);
        if (trimmed.Length == 0)
        {
            return itemList;
        }

        var tokens = TokenSplitRegex().Split(trimmed).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0)
        {
            return itemList;
        }

        var results = new List<(T Item, double TotalScore)>();
        foreach (var item in itemList)
        {
            var text = getText(item);
            double totalScore = 0;
            var allMatch = true;

            foreach (var token in tokens)
            {
                var match = Match(token, text);
                if (match.Matches)
                {
                    totalScore += match.Score;
                }
                else
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                results.Add((item, totalScore));
            }
        }

        return results.OrderBy(r => r.TotalScore).Select(r => r.Item).ToList();
    }
}
