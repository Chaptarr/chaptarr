using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Parser
{
    public static class AuthorNameMatcher
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        // Preserve the released name-proof alphabet; unifying persisted keys needs a separate rebuild.
        private static readonly Regex Possessive = new Regex(@"['\u2019\u02bc]s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string HardContributorPattern = @"[;\uff1b/|+\u3001\uff0c\u00d7]|\s[-\u2013\u2014\u00b7\u2022]\s";
        private static readonly Regex ContributorBoundary = new Regex(@"[&\uff06]|" + HardContributorPattern, RegexOptions.Compiled);
        private static readonly Regex HardContributorBoundary = new Regex(HardContributorPattern, RegexOptions.Compiled);
        // Standard conjunctions from Unicode CLDR list patterns, with dictionary-attested variants.
        // Maintained spellings, provenance and verified scope: docs/NAME_JOINER_POLICY.md.
        internal static IReadOnlyList<string> NameJoiners { get; } = Array.AsReadOnly(new[]
        {
            "and", "und", "et", "y", "e", "en", "och", "og", "i", "и", "ja",
            "ed", "és", "ve", "și", "a", "та", "і", "&",
            "και", "ir", "un", "in", "dan", "và", "dhe", "agus", "eta", "və"
        });
        private static readonly HashSet<string> NameJoinerKeys = NameJoiners
            .Select(joiner => joiner == "&" ? "&" : UnicodeComparisonNormalizer.NormalizeKey(joiner)).ToHashSet(StringComparer.Ordinal);

        private static bool IsNameJoiner(string source, TitleTokenSpan token)
        {
            var key = source[token.Start] == '&' || source[token.Start] == '\uff06' ? "&" : token.Value;
            if (!NameJoinerKeys.Contains(key))
            {
                return false;
            }

            // Comparison folding must not turn explicitly supplied initials into credit separators.
            return !IsInitial(key) ||
                (!source.Substring(token.Start, token.End - token.Start).EnumerateRunes().Any(Rune.IsUpper) &&
                 (token.End == source.Length || source[token.End] != '.'));
        }

        internal static IEnumerable<string> SplitCredits(string value, IEnumerable<string> knownNames = null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            var known = knownNames?.Where(name => !string.IsNullOrWhiteSpace(name)).Select(PrepareName).ToList();
            var result = new List<string>();
            foreach (var group in ContributorBoundary.Split(value).Where(part => !string.IsNullOrWhiteSpace(part)))
            {
                var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(group);
                var start = 0;
                var firstToken = 0;
                for (var index = 1; index + 2 < tokens.Count; index++)
                {
                    if (index <= firstToken || !IsNameJoiner(group, tokens[index]) ||
                        !char.IsWhiteSpace(group[tokens[index].Start - 1]) ||
                        tokens[index].End == group.Length || !char.IsWhiteSpace(group[tokens[index].End]))
                    {
                        continue;
                    }

                    // Exact stored names resolve separator ambiguity; compatibility cannot invent a contributor.
                    if (known?.Any(name => Enumerable.Range(Math.Max(firstToken, index - name.Tokens.Count + 1),
                        index - Math.Max(firstToken, index - name.Tokens.Count + 1) + 1).Any(nameStart =>
                            nameStart + name.Tokens.Count <= tokens.Count &&
                            AuthorNamesExactlyMatch(name, PrepareName(group, tokens, nameStart, nameStart + name.Tokens.Count - 1)))) == true)
                    {
                        continue;
                    }

                    AddCommaCredits(group.Substring(start, tokens[index].Start - start));
                    start = tokens[index].End;
                    firstToken = index + 1;
                }

                AddCommaCredits(group.Substring(start));
            }

            return result;

            bool IsKnown(string text)
            {
                var prepared = PrepareName(text);
                return known?.Any(name => AuthorNamesExactlyMatch(name, prepared)) == true;
            }

            void AddCommaCredits(string text)
            {
                text = text.Trim();
                if (text.Length == 0)
                {
                    return;
                }

                var parts = text.Split(',');
                // A single comma may invert one name; only known complete names can resolve that ambiguity.
                if (parts.Length == 1 || (known == null && parts.Length == 2) ||
                    IsKnown(text))
                {
                    result.Add(text);
                    return;
                }

                for (var index = 0; index < parts.Length; index++)
                {
                    var part = parts[index].Trim();
                    if (index + 1 < parts.Length && IsKnown(part + "," + parts[index + 1]))
                    {
                        part += "," + parts[++index];
                    }

                    if (part.Length > 0)
                    {
                        result.Add(part);
                    }
                }
            }
        }

        public static bool AuthorNamesMatch(string expected, string candidate)
        {
            return AuthorNamesMatch(PrepareName(expected), PrepareName(candidate));
        }

        internal static (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) PrepareName(string name)
        {
            name ??= string.Empty;
            var spans = ReleaseTitleMatchScorer.TokenizeWithSpans(name);
            return PrepareName(name, spans);
        }

        internal static (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) PrepareName(string name, IReadOnlyList<TitleTokenSpan> spans)
            => PrepareName(name, spans, 0, spans.Count - 1);

        internal static (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) PrepareName(string source, IReadOnlyList<TitleTokenSpan> spans, int start, int end, bool inverted = false)
        {
            if (end < start)
            {
                return (new List<string>(), 1, 1, 0, false, false, string.Empty, Array.Empty<int>());
            }

            var sourceStart = spans[start].Start;
            var sourceEnd = spans[end].End;
            var boundary = HardContributorBoundary.Match(source, sourceStart);
            var hasContributorBoundary = boundary.Success && boundary.Index < sourceEnd;

            var comma = source.IndexOf(',', sourceStart, sourceEnd - sourceStart);
            if (comma >= 0 && source.IndexOf(',', comma + 1, sourceEnd - comma - 1) >= 0)
            {
                // Multiple commas retain complete spelling without guessing contributor or suffix roles.
                comma = -1;
                hasContributorBoundary = true;
            }
            var lastCount = 1;
            var givenStart = inverted ? start + lastCount : start;
            if (comma >= 0)
            {
                lastCount = 0;
                givenStart = start;
                while (givenStart <= end && spans[givenStart].Start < comma)
                {
                    lastCount += NormalizeNameToken(source, spans[givenStart]).Length > 0 ? 1 : 0;
                    givenStart++;
                }
            }
            else if (!inverted)
            {
                while (lastCount < end - start + 1 && JoinedByHyphen(source, spans[end - lastCount].End, spans[end - lastCount + 1].Start))
                {
                    lastCount++;
                }
            }

            var firstCount = 1;
            while (givenStart + firstCount <= end && JoinedByHyphen(source, spans[givenStart + firstCount - 1].End, spans[givenStart + firstCount].Start))
            {
                firstCount++;
            }

            var tokens = new List<string>(end - start + 1);
            List<int> joiners = null;
            var literal = new StringBuilder(sourceEnd - sourceStart);
            var previousEnd = sourceStart;
            for (var index = start; index <= end; index++)
            {
                var token = spans[index];
                var value = NormalizeNameToken(source, token);
                if (value.Length > 0)
                {
                    var gap = ContributorBoundary.Match(source, previousEnd, token.Start - previousEnd);
                    if (gap.Success && (gap.Value == "&" || gap.Value == "\uff06") &&
                        (tokens.Count == 0 || tokens[^1] != "&") && value != "&")
                    {
                        (joiners ??= new List<int>()).Add(tokens.Count);
                        tokens.Add("&");
                    }

                    if (IsNameJoiner(source, token))
                    {
                        (joiners ??= new List<int>()).Add(tokens.Count);
                    }

                    tokens.Add(value);
                    if (value == "&")
                    {
                        continue;
                    }

                    if (literal.Length > 0)
                    {
                        literal.Append(LiteralSeparator(source, previousEnd, token.Start));
                    }

                    literal.Append(value);
                    previousEnd = token.End;
                }
            }

            var literalKey = literal.ToString();
            if (comma >= 0 || inverted)
            {
                tokens = tokens.Skip(lastCount).Concat(tokens.Take(lastCount)).ToList();
                joiners = joiners?.Select(index => index >= lastCount ? index - lastCount : tokens.Count - lastCount + index).ToList();
            }

            return (tokens, firstCount, lastCount, tokens.Sum(token => token.Length), HasInitialGroup(tokens, tokens.Count - lastCount), hasContributorBoundary, literalKey, (IReadOnlyList<int>)joiners ?? Array.Empty<int>());
        }

        private static bool JoinedByHyphen(string text, int start, int end)
        {
            return end == start + 1 && (text[start] == '-' || text[start] == '\u2010' || text[start] == '\u2011');
        }

        private static string LiteralSeparator(string source, int start, int end)
        {
            return ContributorBoundary.Match(source, start, end - start).Success ? " ; " : " ";
        }

        internal static bool AuthorNamesExactlyMatch((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) left, (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) right)
        {
            return left.Tokens.Count > 0 && right.Tokens.Count > 0 &&
                (string.Equals(left.LiteralKey, right.LiteralKey, StringComparison.OrdinalIgnoreCase) ||
                 (!left.LiteralKey.Contains(';') && !right.LiteralKey.Contains(';') &&
                  left.Tokens.SequenceEqual(right.Tokens, StringComparer.OrdinalIgnoreCase)));
        }

        internal static bool AuthorNamesMatch((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) left, (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) right, bool allowSurnameSuffix = true)
        {
            if (left.Tokens.Count == 0 || right.Tokens.Count == 0)
            {
                return false;
            }

            // Compact formatting is compatible only when the complete normalized spelling is retained.
            if (AuthorNamesExactlyMatch(left, right) ||
                (left.CharacterCount == right.CharacterCount &&
                 !left.LiteralKey.Contains(';') && !right.LiteralKey.Contains(';') &&
                 string.Equals(string.Concat(left.Tokens), string.Concat(right.Tokens), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var surnameCount = Math.Max(left.LastCount, right.LastCount);
            // Complete contributor spellings retain their boundaries; joiners may share a family credit.
            if (left.HasContributorBoundary || right.HasContributorBoundary ||
                (left.LiteralKey.Contains(';') && right.LiteralKey.Contains(';')))
            {
                return false;
            }

            var leftJoiners = SharedFamilyJoiners(left);
            var rightJoiners = SharedFamilyJoiners(right);
            if (leftJoiners.Count > 0 || rightJoiners.Count > 0)
            {
                return leftJoiners.Count == 0 ? MatchesJoinedMember(left, right, rightJoiners, allowSurnameSuffix) :
                    rightJoiners.Count == 0 && MatchesJoinedMember(right, left, leftJoiners, allowSurnameSuffix);
            }

            if (left.JoinerIndexes.Any(index => left.Tokens[index] == "&") ||
                right.JoinerIndexes.Any(index => right.Tokens[index] == "&"))
            {
                return false;
            }

            if (left.Tokens.Count <= surnameCount || right.Tokens.Count <= surnameCount)
            {
                return false;
            }

            if (!FamilyNamesMatch(left.Tokens, right.Tokens, surnameCount, allowSurnameSuffix))
            {
                return false;
            }

            var leftGivenCount = left.Tokens.Count - surnameCount;
            var rightGivenCount = right.Tokens.Count - surnameCount;
            var required = Math.Max(left.FirstCount, right.FirstCount);
            // With no supplied middle parts or compact-initial expansion, only the first group remains to compare.
            if ((leftGivenCount == required || rightGivenCount == required) &&
                !left.HasGivenInitialGroup && !right.HasGivenInitialGroup)
            {
                if (leftGivenCount < required || rightGivenCount < required)
                {
                    return false;
                }

                for (var index = 0; index < required; index++)
                {
                    if (!GivenTokenMatches(left.Tokens[index], right.Tokens[index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            var leftGiven = left.Tokens.Take(left.Tokens.Count - surnameCount).ToList();
            var rightGiven = right.Tokens.Take(right.Tokens.Count - surnameCount).ToList();
            var expandedLeft = ExpandCompatibleInitials(leftGiven, rightGiven);
            var expandedRight = ExpandCompatibleInitials(rightGiven, leftGiven);
            // Missing middle parts are compatible; every explicitly supplied part must still match in order.
            var shorter = expandedLeft.Count <= expandedRight.Count ? expandedLeft : expandedRight;
            var longer = expandedLeft.Count <= expandedRight.Count ? expandedRight : expandedLeft;
            return SuppliedGivenPartsMatch(shorter, longer, required);
        }

        private static bool SurnameTokenMatches(string left, string right, bool allowSurnameSuffix = true)
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var shorter = left.Length < right.Length ? left : right;
            var longer = left.Length < right.Length ? right : left;
            return allowSurnameSuffix && shorter.Length > 0 && longer.Length == shorter.Length + 1 &&
                (longer[^1] == 's' || longer[^1] == 'S') &&
                longer.AsSpan(0, shorter.Length).Equals(shorter.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasSurnameSuffixAnchor(IReadOnlyList<string> expected, IReadOnlyList<string> supplied)
        {
            if (expected.Count == 0)
            {
                return false;
            }

            foreach (var token in supplied)
            {
                if (!string.Equals(expected[^1], token, StringComparison.OrdinalIgnoreCase) &&
                    SurnameTokenMatches(expected[^1], token))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool FamilyNamesMatch(IReadOnlyList<string> left, IReadOnlyList<string> right, int count, bool allowSurnameSuffix = true)
        {
            if (left.Count < count || right.Count < count)
            {
                return false;
            }

            for (var index = 1; index <= count; index++)
            {
                // Only the final family token allows an apostrophe-less possessive suffix.
                if (index == 1 ? !SurnameTokenMatches(left[^index], right[^index], allowSurnameSuffix) :
                    !string.Equals(left[^index], right[^index], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<int> SharedFamilyJoiners((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) credit)
            => credit.JoinerIndexes.Count == 0 ? Array.Empty<int>() :
                credit.JoinerIndexes.Where(index => index >= credit.FirstCount && index + 1 < credit.Tokens.Count - credit.LastCount).ToArray();

        private static bool MatchesJoinedMember((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) expected, (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) credit, IReadOnlyList<int> joiners, bool allowSurnameSuffix = true)
        {
            var expectedGiven = expected.Tokens.Take(expected.Tokens.Count - expected.LastCount).ToList();
            var sharedFamily = FamilyNamesMatch(expected.Tokens, credit.Tokens, expected.LastCount, allowSurnameSuffix);
            var start = 0;
            foreach (var end in joiners.Append(credit.Tokens.Count))
            {
                var supplied = credit.Tokens.Skip(start).Take(end - start).ToList();
                if (supplied.SequenceEqual(expected.Tokens, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                var complete = supplied.Count > expected.LastCount && FamilyNamesMatch(expected.Tokens, supplied, expected.LastCount, allowSurnameSuffix);
                if (complete || (end < credit.Tokens.Count && sharedFamily))
                {
                    var given = complete ? supplied.Take(supplied.Count - expected.LastCount).ToList() : supplied;
                    // Sharing a family credit must not discard any explicitly supplied name part.
                    if (SuppliedGivenPartsMatch(ExpandCompatibleInitials(given, expectedGiven),
                        ExpandCompatibleInitials(expectedGiven, given), Math.Max(expected.FirstCount, start == 0 ? credit.FirstCount : 1)))
                    {
                        return true;
                    }
                }

                start = end + 1;
            }

            return false;
        }

        private static bool SuppliedGivenPartsMatch(IReadOnlyList<string> supplied, IReadOnlyList<string> expected, int required)
        {
            if (supplied.Count < required || expected.Count < required)
            {
                return false;
            }

            for (var index = 0; index < required; index++)
            {
                if (!GivenTokenMatches(supplied[index], expected[index]))
                {
                    return false;
                }
            }

            var next = required;
            for (var index = required; index < expected.Count; index++)
            {
                if (next < supplied.Count && GivenTokenMatches(supplied[next], expected[index]))
                {
                    next++;
                }
            }

            return next == supplied.Count;
        }

        private static List<string> ExpandCompatibleInitials(List<string> given, IReadOnlyList<string> other)
        {
            if (!HasInitialGroup(other, other.Count))
            {
                return given;
            }

            var result = new List<string>();
            foreach (var token in given)
            {
                var letters = token.EnumerateRunes().ToArray();
                var matchesGroup = letters.Length > 1 && letters.All(Rune.IsLetter) &&
                    Enumerable.Range(0, Math.Max(0, other.Count - letters.Length + 1)).Any(start =>
                        GivenTokenMatches(letters[0].ToString(), other[start]) &&
                        Enumerable.Range(0, letters.Length).All(index => IsInitial(other[start + index])));
                if (matchesGroup)
                {
                    result.AddRange(letters.Select(letter => letter.ToString()));
                }
                else
                {
                    result.Add(token);
                }
            }

            return result;
        }

        private static bool HasInitialGroup(IReadOnlyList<string> tokens, int count)
        {
            for (var index = 1; index < count; index++)
            {
                if (IsInitial(tokens[index - 1]) && IsInitial(tokens[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool GivenTokenMatches(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                (IsInitial(left) && right.StartsWith(left, StringComparison.OrdinalIgnoreCase)) ||
                (IsInitial(right) && left.StartsWith(right, StringComparison.OrdinalIgnoreCase));
        }

        public static bool ContainsAuthorName(string expectedName, string fieldValue)
        {
            return ContainsAuthorName(PrepareName(expectedName), fieldValue);
        }

        internal static bool ContainsAuthorName((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) expectedName, string fieldValue)
        {
            return ContainsAuthorName(expectedName, fieldValue, ReleaseTitleMatchScorer.TokenizeWithSpans(fieldValue));
        }

        internal static bool ContainsAuthorName((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) expectedName, string fieldValue, IReadOnlyList<TitleTokenSpan> tokens, bool allowSurnameSuffix = true)
        {
            return ContainsAuthorName(expectedName, fieldValue, tokens, Array.Empty<(int Start, int End)>(), allowSurnameSuffix);
        }

        internal static bool ContainsAuthorName((List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) expectedName, string fieldValue, IReadOnlyList<TitleTokenSpan> tokens, IReadOnlyList<(int Start, int End)> exactTitleSpans, bool allowSurnameSuffix = true)
        {
            return FindNameSpans(fieldValue, tokens, expectedName, allowSurnameSuffix).Any(span =>
                IsCoherentNameSpan(fieldValue, tokens, span) ||
                exactTitleSpans.Any(title => IsCoherentNameSpan(fieldValue, tokens, span, title.Start, title.End)));
        }

        internal static IEnumerable<(int Start, int End, bool Inverted)> FindNameSpans(string source, IReadOnlyList<TitleTokenSpan> tokens, (List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes) expected, bool allowSurnameSuffix = true)
        {
            if (expected.Tokens.Count == 0)
            {
                yield break;
            }

            var surname = expected.Tokens.Last();
            var compactName = string.Concat(expected.Tokens);
            var hasLiteralOrder = expected.LiteralKey != string.Join(" ", expected.Tokens);
            var nameTokens = tokens.Select(token => NormalizeNameToken(source, token)).ToArray();
            var started = Logger.IsTraceEnabled ? Stopwatch.GetTimestamp() : 0;
            var surnameIndexes = Enumerable.Range(0, tokens.Count)
                .Where(index => SurnameTokenMatches(surname, nameTokens[index], allowSurnameSuffix) ||
                                string.Equals(compactName, nameTokens[index], StringComparison.OrdinalIgnoreCase) ||
                                (hasLiteralOrder && nameTokens[index].Length > 0 && expected.LiteralKey.EndsWith(nameTokens[index], StringComparison.OrdinalIgnoreCase)))
                .ToList();
            // Actual field tokens bound the search; only compatible first/family anchors require comparison.
            var maxLength = tokens.Count;
            var examined = 0;
            var matched = 0;
            try
            {
                if (surnameIndexes.Count == 0)
                {
                    yield break;
                }

                var surnamePositions = surnameIndexes.ToHashSet();
                var hasComma = source.Contains(',');
                for (var start = 0; start < tokens.Count; start++)
                {
                    var firstMatches = GivenTokenMatches(expected.Tokens[0], nameTokens[start]) ||
                                       string.Equals(compactName, nameTokens[start], StringComparison.OrdinalIgnoreCase) ||
                                       (hasLiteralOrder && nameTokens[start].Length > 0 && expected.LiteralKey.StartsWith(nameTokens[start], StringComparison.OrdinalIgnoreCase));
                    if (!firstMatches && !surnamePositions.Contains(start) && !hasComma)
                    {
                        continue;
                    }

                    var boundary = HardContributorBoundary.Match(source, tokens[start].Start);
                    var limit = boundary.Success && !expected.HasContributorBoundary && !expected.LiteralKey.Contains(';') ? boundary.Index : source.Length;
                    var comma = source.IndexOf(',', tokens[start].Start);
                    if (comma >= limit)
                    {
                        comma = -1;
                    }

                    if (firstMatches)
                    {
                        foreach (var end in surnameIndexes.Where(end => end >= start && tokens[end].End <= limit))
                        {
                            examined++;
                            var candidate = PrepareName(source, tokens, start, end);
                            var startsWithSharedJoiner = start > 0 && IsInitial(candidate.Tokens[0]) && candidate.JoinerIndexes.Contains(0) &&
                                candidate.Tokens.Count - candidate.LastCount > candidate.FirstCount &&
                                !HasNameBoundary(source, tokens[start - 1].End, tokens[start].Start);
                            var borrowsExpandedInitial = start > 0 && IsInitial(nameTokens[start]) && !IsInitial(expected.Tokens[0]) &&
                                !nameTokens[start - 1].All(char.IsDigit) && !IsNameJoiner(source, tokens[start - 1]) &&
                                !HasNameBoundary(source, tokens[start - 1].End, tokens[start].Start);
                            if (AuthorNamesExactlyMatch(expected, candidate) ||
                                (!startsWithSharedJoiner && !borrowsExpandedInitial && (comma < 0 || tokens[end].End <= comma) && AuthorNamesMatch(expected, candidate, allowSurnameSuffix)))
                            {
                                matched++;
                                yield return (start, end, false);
                                // A complete credit already proves every title that its supersets could prove.
                                if (IsCoherentNameSpan(source, tokens, (start, end, false)))
                                {
                                    break;
                                }
                            }
                        }
                    }

                    if (surnamePositions.Contains(start))
                    {
                        foreach (var span in InvertedSpans(start, start, comma >= 0 ? comma : limit))
                        {
                            yield return span;
                        }
                    }

                    if (comma >= 0)
                    {
                        var familyEnd = start;
                        while (familyEnd + 1 < tokens.Count && tokens[familyEnd + 1].Start < comma)
                        {
                            familyEnd++;
                        }

                        if (surnamePositions.Contains(familyEnd))
                        {
                            var secondComma = source.IndexOf(',', comma + 1);
                            foreach (var span in InvertedSpans(start, familyEnd, secondComma >= 0 ? Math.Min(secondComma, limit) : limit))
                            {
                                yield return span;
                            }
                        }
                    }
                }
            }
            finally
            {
                if (started != 0)
                {
                    Logger.Trace("Author span search: tokens={0}, maxSpan={1}, examined={2}, matched={3}, elapsedMs={4:F3}, possessiveAnchors={5}",
                        tokens.Count, maxLength, examined, matched, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        surnameIndexes.Count(index => !string.Equals(surname, nameTokens[index], StringComparison.OrdinalIgnoreCase) &&
                            SurnameTokenMatches(surname, nameTokens[index], allowSurnameSuffix)));
                }
            }

            IEnumerable<(int Start, int End, bool Inverted)> InvertedSpans(int start, int familyEnd, int limit)
            {
                var givenStart = familyEnd + 1;
                if (givenStart >= tokens.Count || tokens[givenStart].End > limit ||
                    !GivenTokenMatches(expected.Tokens[0], tokens[givenStart].Value))
                {
                    yield break;
                }

                // Reuse the prepared prefix; appending a given part preserves the declared family group.
                var candidate = PrepareName(source, tokens, start, givenStart, true);
                var previousEnd = tokens[givenStart].End;
                var boundary = HardContributorBoundary.Match(source, tokens[start].Start);
                for (var end = givenStart; end < tokens.Count && tokens[end].End <= limit; end++)
                {
                    candidate.HasContributorBoundary |= boundary.Success && boundary.Index < tokens[end].End;
                    if (end > givenStart)
                    {
                        var value = NormalizeNameToken(source, tokens[end]);
                        if (value.Length == 0)
                        {
                            continue;
                        }

                        if (candidate.Tokens.Count - candidate.LastCount == candidate.FirstCount &&
                            JoinedByHyphen(source, tokens[end - 1].End, tokens[end].Start))
                        {
                            candidate.FirstCount++;
                        }

                        var givenCount = candidate.Tokens.Count - candidate.LastCount;
                        var gap = ContributorBoundary.Match(source, previousEnd, tokens[end].Start - previousEnd);
                        if (gap.Success && (gap.Value == "&" || gap.Value == "\uff06") && value != "&" &&
                            (givenCount == 0 || candidate.Tokens[givenCount - 1] != "&"))
                        {
                            candidate.JoinerIndexes = candidate.JoinerIndexes.Append(givenCount).ToArray();
                            candidate.Tokens.Insert(givenCount++, "&");
                            candidate.CharacterCount++;
                        }

                        candidate.HasGivenInitialGroup |= givenCount > 0 && IsInitial(candidate.Tokens[givenCount - 1]) && IsInitial(value);
                        candidate.CharacterCount += value.Length;
                        candidate.Tokens.Insert(givenCount, value);
                        if (IsNameJoiner(source, tokens[end]))
                        {
                            candidate.JoinerIndexes = candidate.JoinerIndexes.Append(givenCount).ToArray();
                        }

                        if (value != "&")
                        {
                            candidate.LiteralKey += LiteralSeparator(source, previousEnd, tokens[end].Start) + value;
                            previousEnd = tokens[end].End;
                        }
                    }

                    examined++;
                    if (AuthorNamesMatch(expected, candidate, allowSurnameSuffix))
                    {
                        matched++;
                        yield return (start, end, true);
                    }
                }
            }

        }

        private static string NormalizeNameToken(string source, TitleTokenSpan token)
        {
            // Person-name evidence must not inherit words inserted by title normalization.
            if (source[token.Start] == '&' || source[token.Start] == '\uff06')
            {
                return "&";
            }

            if (!token.Value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                return token.Value;
            }

            var raw = source.Substring(token.Start, token.End - token.Start);
            return Possessive.IsMatch(raw) ? UnicodeComparisonNormalizer.NormalizeKey(Possessive.Replace(raw, string.Empty)) : token.Value;
        }

        internal static bool IsCoherentNameSpan(string source, IReadOnlyList<TitleTokenSpan> tokens, (int Start, int End, bool Inverted) span, int matchedStart = -1, int matchedEnd = -1)
        {
            if (span.Start <= matchedEnd && span.End >= matchedStart)
            {
                return false;
            }

            var inverted = span.Inverted || source.IndexOf(',', tokens[span.Start].Start,
                tokens[span.End].End - tokens[span.Start].Start) >= 0;
            // Inverted credits retain both supplied groups instead of borrowing another person's name parts.
            if (inverted && span.Start > 0 && span.Start != matchedEnd + 1 &&
                !tokens[span.Start - 1].Value.All(char.IsDigit) &&
                !HasNameBoundary(source, tokens[span.Start - 1].End, tokens[span.Start].Start, allowJoiners: false))
            {
                return false;
            }

            if (inverted && span.End + 1 < tokens.Count && span.End + 1 != matchedStart &&
                !tokens[span.End + 1].Value.All(char.IsDigit) &&
                !HasNameBoundary(source, tokens[span.End].End, tokens[span.End + 1].Start, allowJoiners: false))
            {
                return false;
            }

            if (span.Start > 0 && span.Start != matchedEnd + 1 &&
                (JoinedByHyphen(source, tokens[span.Start - 1].End, tokens[span.Start].Start) ||
                 (IsInitial(tokens[span.Start - 1].Value) && !IsSharedJoinerBeforeSpan(source, tokens, span) && !HasNameBoundary(source, tokens[span.Start - 1].End, tokens[span.Start].Start, allowPadding: false))))
            {
                return false;
            }

            if (span.End + 1 < tokens.Count && span.End + 1 != matchedStart &&
                (JoinedByHyphen(source, tokens[span.End].End, tokens[span.End + 1].Start) ||
                 (IsInitial(tokens[span.End + 1].Value) && !IsSharedJoinerAfterSpan(source, tokens, span) && !HasNameBoundary(source, tokens[span.End].End, tokens[span.End + 1].Start, allowPadding: false))))
            {
                return false;
            }

            return true;
        }

        private static bool IsSharedJoinerBeforeSpan(string source, IReadOnlyList<TitleTokenSpan> tokens, (int Start, int End, bool Inverted) span)
        {
            return span.Start >= 2 && span.End > span.Start &&
                   IsNameJoiner(source, tokens[span.Start - 1]) &&
                   !HasNameBoundary(source, tokens[span.Start - 2].End, tokens[span.Start - 1].Start);
        }

        private static bool IsSharedJoinerAfterSpan(string source, IReadOnlyList<TitleTokenSpan> tokens, (int Start, int End, bool Inverted) span)
        {
            return span.End + 2 < tokens.Count && span.End > span.Start &&
                   IsNameJoiner(source, tokens[span.End + 1]) &&
                   !HasNameBoundary(source, tokens[span.End + 1].End, tokens[span.End + 2].Start);
        }

        private static bool IsInitial(string token)
        {
            return !string.IsNullOrEmpty(token) && Rune.TryGetRuneAt(token, 0, out var rune) && rune.Utf16SequenceLength == token.Length && Rune.IsLetter(rune);
        }

        private static bool HasNameBoundary(string source, int start, int end, bool allowPadding = true, bool allowJoiners = true)
        {
            for (var index = start; index < end; index++)
            {
                if (!allowJoiners && (source[index] == '&' || source[index] == '\uff06'))
                {
                    continue;
                }

                if (!char.IsWhiteSpace(source[index]) && source[index] != '.' && source[index] != '_')
                {
                    return true;
                }

                if (allowPadding && source[index] == '.' && index + 1 < end && source[index + 1] == '.')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
