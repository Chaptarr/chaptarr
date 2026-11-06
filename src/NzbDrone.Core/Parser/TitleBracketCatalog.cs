using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Parser
{
    // Bracket pairs around title qualifiers, including the full-width forms used in CJK titles.
    internal static class TitleBracketCatalog
    {
        private static readonly (char Open, char Close, char AsciiOpen, char AsciiClose)[] Pairs =
        {
            ('(', ')', '(', ')'),
            ('[', ']', '[', ']'),
            ('（', '）', '(', ')'),
            ('［', '］', '[', ']'),
            ('【', '】', '[', ']'),
            ('{', '}', '(', ')')
        };

        public static IReadOnlyList<char> Openers { get; } = Pairs.Select(pair => pair.Open).ToArray();

        // One correctly paired segment whose inner text is captured as "label", e.g. "(Unabridged)" or "【典藏版】".
        public static string PairedSegmentPattern { get; } = string.Join("|", Pairs.Select(pair =>
            EscapeLiteral(pair.Open) + "(?<label>[^" + EscapeForClass(pair.Open) + EscapeForClass(pair.Close) + "]*)" + EscapeLiteral(pair.Close)));

        // Fold only parser input; title proof and stored release names keep their original brackets.
        public static string NormalizeForParsing(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return title;
            }

            foreach (var pair in Pairs)
            {
                if (pair.Open != pair.AsciiOpen)
                {
                    title = title.Replace(pair.Open, pair.AsciiOpen).Replace(pair.Close, pair.AsciiClose);
                }
            }

            return title;
        }

        // The title without its trailing bracket groups, or the title itself when nothing else would remain.
        public static string BaseTitle(string title, List<string> removedGroups = null)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var remaining = title.Trim();
            while (remaining.Length > 0 && TryGetOpener(remaining[remaining.Length - 1], out var opener))
            {
                var open = FindMatchingOpen(remaining, opener, remaining[remaining.Length - 1]);
                if (open <= 0)
                {
                    break;
                }

                var inside = remaining.Substring(open + 1, remaining.Length - open - 2).Trim();
                var before = remaining.Substring(0, open).TrimEnd();
                if (string.IsNullOrWhiteSpace(inside) || string.IsNullOrWhiteSpace(before))
                {
                    break;
                }

                removedGroups?.Add(inside);
                remaining = before;
            }

            return remaining;
        }

        // The full title, then its base title when that differs.
        public static IEnumerable<string> FullThenBase(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            yield return title;

            var baseTitle = BaseTitle(title);
            if (!string.Equals(baseTitle, title.Trim(), StringComparison.Ordinal))
            {
                yield return baseTitle;
            }
        }

        // Cuts a bracket that opens after some text and never closes, as in a truncated "Title (Unabr".
        public static string TrimUnclosedTail(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            foreach (var pair in Pairs)
            {
                var open = title.LastIndexOf(pair.Open);
                if (open > 0 && open > title.LastIndexOf(pair.Close) && !string.IsNullOrWhiteSpace(title[..open]))
                {
                    title = title[..open];
                }
            }

            return title.Trim();
        }

        // The opener that closes the final bracket, skipping nested groups of the same kind.
        private static int FindMatchingOpen(string text, char open, char close)
        {
            var depth = 0;
            for (var i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] == close)
                {
                    depth++;
                }
                else if (text[i] == open && --depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryGetOpener(char close, out char open)
        {
            foreach (var pair in Pairs)
            {
                if (pair.Close == close)
                {
                    open = pair.Open;
                    return true;
                }
            }

            open = default;
            return false;
        }

        private static string EscapeForClass(char value) =>
            value is '\\' or ']' or '[' or '^' or '-' ? "\\" + value : value.ToString();

        private static string EscapeLiteral(char value) =>
            Regex.Escape(value.ToString()).Replace("]", "\\]");
    }
}
