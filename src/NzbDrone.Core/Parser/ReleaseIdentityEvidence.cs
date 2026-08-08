using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Parser
{
    public sealed class ReleaseIdentityEvidence
    {
        private static readonly Regex SegmentSeparatorRegex = new Regex(@"\s*[-\u2013\u2014]\s*", RegexOptions.Compiled);

        private static readonly HashSet<string> StructuralConnectorTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "by", "of", "the"
        };

        private static readonly HashSet<string> CreditLeadTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "read", "narrated", "performed"
        };

        private static readonly HashSet<string> SeriesPositionMarkerTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "book", "bk", "vol", "volume", "tome", "series"
        };

        private sealed class TokenSegment
        {
            public string Text { get; set; }
            public List<string> Tokens { get; set; } = new List<string>();
            public int Start { get; set; }
            public int End { get; set; }
        }

        public bool HasStructuredAuthorMismatch { get; set; }
        public bool HasPositiveIdentityEvidence { get; set; }

        public static ReleaseIdentityEvidence Analyze(ReleaseInfo release, Author expectedAuthor, Book targetBook, TitleMatchResult titleMatch)
        {
            var result = new ReleaseIdentityEvidence();

            if (release == null || expectedAuthor == null || titleMatch == null)
            {
                return result;
            }

            if (release.Author.IsNotNullOrWhiteSpace())
            {
                if (StructuredAuthorMatches(expectedAuthor, release.Author))
                {
                    result.HasPositiveIdentityEvidence = true;
                    return result;
                }

                result.HasStructuredAuthorMismatch = true;
                return result;
            }

            if (titleMatch.MatchedVariant.IsNullOrWhiteSpace() || release.Title.IsNullOrWhiteSpace())
            {
                return result;
            }

            var cleanedReleaseTitle = Parser.CleanReleaseTitleForParsing(release.Title);
            var releaseTokens = ReleaseTitleMatchScorer.Tokenize(cleanedReleaseTitle);
            if (!TryResolveMatchedSpan(releaseTokens, titleMatch, out var matchedStart, out var matchedEnd))
            {
                return result;
            }

            var segments = BuildTokenSegments(cleanedReleaseTitle);
            var titleSegmentIndex = FindContainingSegment(segments, matchedStart, matchedEnd);

            if (HasAuthorEditionTitleIdentity(releaseTokens, matchedStart, matchedEnd, expectedAuthor, targetBook, titleMatch) ||
                HasBoundaryIdentity(releaseTokens, segments, titleSegmentIndex, matchedStart, matchedEnd, expectedAuthor, targetBook))
            {
                result.HasPositiveIdentityEvidence = true;
                return result;
            }

            return result;
        }

        private static bool TryResolveMatchedSpan(IReadOnlyList<string> releaseTokens, TitleMatchResult titleMatch, out int matchedStart, out int matchedEnd)
        {
            matchedStart = titleMatch?.MatchedStart ?? -1;
            matchedEnd = titleMatch?.MatchedEnd ?? -1;

            if (releaseTokens != null &&
                matchedStart >= 0 &&
                matchedEnd >= matchedStart &&
                matchedEnd < releaseTokens.Count)
            {
                return true;
            }

            var titleTokens = ReleaseTitleMatchScorer.Tokenize(titleMatch?.MatchedVariant);
            foreach (var span in ReleaseTitleMatchScorer.FindExactTitleSpans(releaseTokens, titleTokens))
            {
                matchedStart = span.Start;
                matchedEnd = span.End;
                return true;
            }

            return false;
        }

        private static bool StructuredAuthorMatches(Author expectedAuthor, string releaseAuthor)
        {
            var candidates = SplitContributorList(releaseAuthor).ToList();
            return GetKnownAuthorNames(expectedAuthor)
                .Any(expectedName => CandidatePartMatchesName(expectedName, releaseAuthor) ||
                                     candidates.Any(candidate => CandidatePartMatchesName(expectedName, candidate)));
        }

        private static IEnumerable<string> SplitContributorList(string value)
        {
            return Regex.Split(value ?? string.Empty, @"\s*(?:,|&|\band\b)\s*", RegexOptions.IgnoreCase)
                .Select(part => part.Trim())
                .Where(part => part.IsNotNullOrWhiteSpace());
        }

        private static List<TokenSegment> BuildTokenSegments(string cleanedReleaseTitle)
        {
            var segments = new List<TokenSegment>();
            var tokenOffset = 0;

            foreach (var rawSegment in SegmentSeparatorRegex.Split(cleanedReleaseTitle ?? string.Empty))
            {
                var text = rawSegment.Trim();
                var tokens = ReleaseTitleMatchScorer.Tokenize(text);

                if (tokens.Count == 0)
                {
                    continue;
                }

                segments.Add(new TokenSegment
                {
                    Text = text,
                    Tokens = tokens,
                    Start = tokenOffset,
                    End = tokenOffset + tokens.Count - 1
                });

                tokenOffset += tokens.Count;
            }

            return segments;
        }

        private static int FindContainingSegment(IReadOnlyList<TokenSegment> segments, int matchedStart, int matchedEnd)
        {
            for (var index = 0; index < segments.Count; index++)
            {
                if (matchedStart >= segments[index].Start && matchedEnd <= segments[index].End)
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool HasAuthorEditionTitleIdentity(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, Author expectedAuthor, Book targetBook, TitleMatchResult titleMatch)
        {
            // Positive identity is the expected author (or provider alias) plus an exact edition
            // title. Extra series or release metadata is neutral unless the title scorer found a
            // concrete problem such as a better-explained sibling book.
            return titleMatch?.ProblemCode == TitleMatchProblemCode.None &&
                   IsExactEditionTitleMatch(titleMatch, targetBook) &&
                   HasExpectedAuthorOutsideTitle(releaseTokens, matchedStart, matchedEnd, expectedAuthor);
        }

        private static bool IsExactEditionTitleMatch(TitleMatchResult titleMatch, Book targetBook)
        {
            var matchedTokens = ReleaseTitleMatchScorer.Tokenize(titleMatch?.MatchedVariant);
            return matchedTokens.Count > 0 &&
                   (targetBook?.Editions ?? Enumerable.Empty<Edition>())
                   .Where(edition => edition?.Title.IsNotNullOrWhiteSpace() == true)
                   .Select(edition => ReleaseTitleMatchScorer.Tokenize(edition.Title))
                   .Any(editionTokens => TokenSequencesMatch(matchedTokens, editionTokens));
        }

        private static bool HasExpectedAuthorOutsideTitle(IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, Author expectedAuthor)
        {
            return GetKnownAuthorNames(expectedAuthor)
                .SelectMany(expectedName => FindNameSpans(releaseTokens, expectedName))
                .Any(span => span.End < matchedStart || span.Start > matchedEnd);
        }

        private static List<string> GetMeaningfulSeriesTokens(IEnumerable<string> tokens)
        {
            return (tokens ?? Enumerable.Empty<string>())
                .Where(token => !IsStructuralConnectorToken(token) &&
                                !IsSeriesPositionMarker(token) &&
                                !IsPureNumberToken(token))
                .ToList();
        }

        private static bool TokenSequencesMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            return left.Count == right.Count &&
                   left.Select((token, index) => ReleaseTitleMatchScorer.TokensMatch(token, right[index])).All(matches => matches);
        }

        private static bool HasBoundaryIdentity(IReadOnlyList<string> releaseTokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedStart, int matchedEnd, Author expectedAuthor, Book targetBook)
        {
            var leftTokens = GetLeftBoundaryTokens(releaseTokens, segments, titleSegmentIndex, matchedStart);
            var rightTokens = GetRightBoundaryTokens(releaseTokens, segments, titleSegmentIndex, matchedEnd);
            var leftText = GetLeftBoundaryText(releaseTokens, segments, titleSegmentIndex, matchedStart);
            var rightText = GetRightBoundaryText(releaseTokens, segments, titleSegmentIndex, matchedEnd);

            return BoundaryMatchesExpectedAuthor(leftText, leftTokens, true, expectedAuthor) ||
                   BoundaryMatchesExpectedAuthor(rightText, rightTokens, false, expectedAuthor) ||
                   BoundaryMatchesKnownNarrator(leftText, leftTokens, true, targetBook) ||
                   BoundaryMatchesKnownNarrator(rightText, rightTokens, false, targetBook) ||
                   BoundaryMatchesKnownSeries(leftText, leftTokens, true, targetBook) ||
                   BoundaryMatchesKnownSeries(rightText, rightTokens, false, targetBook);
        }

        private static bool BoundaryMatchesExpectedAuthor(string rawCandidate, IReadOnlyList<string> tokens, bool leftOfTitle, Author expectedAuthor)
        {
            var expectedNames = GetKnownAuthorNames(expectedAuthor).ToList();
            return expectedNames.Any(expectedName => CandidateMatchesName(expectedName, rawCandidate)) ||
                   BoundaryCandidates(tokens, leftOfTitle)
                       .Any(candidate => expectedNames.Any(expectedName => CandidateMatchesName(expectedName, candidate)));
        }

        private static bool BoundaryMatchesKnownNarrator(string rawCandidate, IReadOnlyList<string> tokens, bool leftOfTitle, Book targetBook)
        {
            var narrators = GetKnownNarrators(targetBook).ToList();
            if (narrators.Count == 0)
            {
                return false;
            }

            if (narrators.Any(narrator => CandidateMatchesName(narrator, rawCandidate)))
            {
                return true;
            }

            foreach (var candidate in BoundaryCandidates(tokens, leftOfTitle))
            {
                if (narrators.Any(narrator => CandidateMatchesName(narrator, candidate)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool BoundaryMatchesKnownSeries(string rawCandidate, IReadOnlyList<string> tokens, bool leftOfTitle, Book targetBook)
        {
            return CandidateMatchesSeries(targetBook, rawCandidate) ||
                   BoundaryCandidates(tokens, leftOfTitle)
                       .Any(candidate => CandidateMatchesSeries(targetBook, candidate));
        }

        private static IEnumerable<string> BoundaryCandidates(IReadOnlyList<string> tokens, bool leftOfTitle)
        {
            if (tokens == null || tokens.Count == 0)
            {
                yield break;
            }

            if (leftOfTitle)
            {
                for (var start = tokens.Count - 1; start >= 0; start--)
                {
                    yield return string.Join(" ", tokens.Skip(start));
                }
            }
            else
            {
                for (var length = 1; length <= tokens.Count; length++)
                {
                    yield return string.Join(" ", tokens.Take(length));
                }
            }
        }

        private static bool CandidateMatchesName(string expectedName, string candidate)
        {
            if (expectedName.IsNullOrWhiteSpace() || candidate.IsNullOrWhiteSpace())
            {
                return false;
            }

            return GetCandidateNameForms(candidate)
                .SelectMany(SplitContributorList)
                .Any(part => CandidatePartMatchesName(expectedName, part));
        }

        private static IEnumerable<string> GetCandidateNameForms(string candidate)
        {
            if (candidate.IsNullOrWhiteSpace())
            {
                yield break;
            }

            yield return candidate;

            var tokens = ReleaseTitleMatchScorer.Tokenize(candidate);
            if (tokens.Count == 0)
            {
                yield break;
            }

            if (tokens[0].Equals("by", StringComparison.OrdinalIgnoreCase) && tokens.Count > 1)
            {
                yield return string.Join(" ", tokens.Skip(1));
            }

            if (tokens.Count > 2 &&
                CreditLeadTokens.Contains(tokens[0]) &&
                tokens[1].Equals("by", StringComparison.OrdinalIgnoreCase))
            {
                yield return string.Join(" ", tokens.Skip(2));
            }
        }

        private static bool CandidatePartMatchesName(string expectedName, string candidate)
        {
            if (expectedName.IsNullOrWhiteSpace() || candidate.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (AuthorNameMatcher.ExplainAuthorNamesMatch(expectedName, candidate).IsMatch)
            {
                return true;
            }

            var expectedTokens = ReleaseTitleMatchScorer.Tokenize(expectedName);
            var candidateTokens = ReleaseTitleMatchScorer.Tokenize(candidate);
            if (expectedTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return false;
            }

            var expectedLast = expectedTokens.Last();
            var candidateLast = candidateTokens.Last();
            if (!candidateLast.Equals(expectedLast + "s", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            candidateTokens[candidateTokens.Count - 1] = expectedLast;
            return AuthorNameMatcher.ExplainAuthorNamesMatch(expectedName, string.Join(" ", candidateTokens)).IsMatch;
        }

        private static bool CandidateMatchesSeries(Book targetBook, string candidate)
        {
            if (targetBook?.SeriesName.IsNullOrWhiteSpace() != false || candidate.IsNullOrWhiteSpace())
            {
                return false;
            }

            var candidateTokens = GetMeaningfulSeriesTokens(ReleaseTitleMatchScorer.Tokenize(candidate));
            var seriesTokens = GetMeaningfulSeriesTokens(ReleaseTitleMatchScorer.Tokenize(targetBook.SeriesName));
            if (candidateTokens.Count == 0 || seriesTokens.Count == 0)
            {
                return false;
            }

            // Series-only corroboration must preserve the full stored label. The release may add
            // detail and one small spelling error is tolerated, but an arbitrary suffix such as
            // "History" cannot stand in for "A Targaryen History" without author evidence.
            return TryAlignSeriesAtBoundary(seriesTokens, candidateTokens);
        }

        private static bool TryAlignSeriesAtBoundary(IReadOnlyList<string> requiredTokens, IReadOnlyList<string> fieldTokens)
        {
            return TitleTokenAlignment.TryAlignOrdered(
                       requiredTokens,
                       fieldTokens,
                       allowNearExact: true,
                       allowTransposition: true,
                       out var alignment) &&
                   (alignment.ConsumedFieldIndexes.First() == 0 ||
                    alignment.ConsumedFieldIndexes.Last() == fieldTokens.Count - 1);
        }

        private static IEnumerable<string> GetKnownNarrators(Book targetBook)
        {
            if (targetBook == null)
            {
                yield break;
            }

            if (targetBook.Narrator.IsNotNullOrWhiteSpace())
            {
                yield return targetBook.Narrator;
            }

            foreach (var edition in targetBook.Editions ?? Enumerable.Empty<Edition>())
            {
                if (edition?.Narrator.IsNotNullOrWhiteSpace() == true)
                {
                    yield return edition.Narrator;
                }

                foreach (var narrator in edition?.NarratorNames ?? Enumerable.Empty<string>())
                {
                    if (narrator.IsNotNullOrWhiteSpace())
                    {
                        yield return narrator;
                    }
                }
            }
        }

        private static IEnumerable<string> GetKnownAuthorNames(Author author)
        {
            if (author?.Name.IsNotNullOrWhiteSpace() == true)
            {
                yield return author.Name;
            }

            foreach (var name in (author?.Aliases ?? Enumerable.Empty<string>())
                         .Concat(author?.Pseudonyms ?? Enumerable.Empty<string>())
                         .Where(name => name.IsNotNullOrWhiteSpace())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return name;
            }
        }

        private static IEnumerable<(int Start, int End)> FindNameSpans(IReadOnlyList<string> tokens, string expectedName)
        {
            var expectedTokens = ReleaseTitleMatchScorer.Tokenize(expectedName);
            if (expectedTokens.Count == 0)
            {
                yield break;
            }

            var maxLength = Math.Min(tokens.Count, Math.Max(expectedTokens.Count + 1, 4));
            for (var start = 0; start < tokens.Count; start++)
            {
                for (var length = 1; length <= maxLength && start + length <= tokens.Count; length++)
                {
                    var candidate = string.Join(" ", tokens.Skip(start).Take(length));
                    if (!CandidatePartMatchesName(expectedName, candidate))
                    {
                        continue;
                    }

                    yield return (start, start + length - 1);
                }
            }
        }

        private static List<string> GetLeftBoundaryTokens(IReadOnlyList<string> releaseTokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedStart)
        {
            if (titleSegmentIndex >= 0)
            {
                var titleSegment = segments[titleSegmentIndex];
                if (matchedStart > titleSegment.Start)
                {
                    return Slice(releaseTokens, titleSegment.Start, matchedStart - 1);
                }

                if (titleSegmentIndex > 0)
                {
                    return segments[titleSegmentIndex - 1].Tokens.ToList();
                }
            }

            return Slice(releaseTokens, 0, matchedStart - 1);
        }

        private static string GetLeftBoundaryText(IReadOnlyList<string> releaseTokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedStart)
        {
            if (titleSegmentIndex >= 0)
            {
                var titleSegment = segments[titleSegmentIndex];
                if (matchedStart > titleSegment.Start)
                {
                    return string.Join(" ", Slice(releaseTokens, titleSegment.Start, matchedStart - 1));
                }

                if (titleSegmentIndex > 0)
                {
                    return segments[titleSegmentIndex - 1].Text;
                }
            }

            return string.Join(" ", Slice(releaseTokens, 0, matchedStart - 1));
        }

        private static List<string> GetRightBoundaryTokens(IReadOnlyList<string> releaseTokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedEnd)
        {
            if (titleSegmentIndex >= 0)
            {
                var titleSegment = segments[titleSegmentIndex];
                if (matchedEnd < titleSegment.End)
                {
                    return Slice(releaseTokens, matchedEnd + 1, titleSegment.End);
                }

                if (titleSegmentIndex + 1 < segments.Count)
                {
                    return segments[titleSegmentIndex + 1].Tokens.ToList();
                }
            }

            return Slice(releaseTokens, matchedEnd + 1, releaseTokens.Count - 1);
        }

        private static string GetRightBoundaryText(IReadOnlyList<string> releaseTokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedEnd)
        {
            if (titleSegmentIndex >= 0)
            {
                var titleSegment = segments[titleSegmentIndex];
                if (matchedEnd < titleSegment.End)
                {
                    return string.Join(" ", Slice(releaseTokens, matchedEnd + 1, titleSegment.End));
                }

                if (titleSegmentIndex + 1 < segments.Count)
                {
                    return segments[titleSegmentIndex + 1].Text;
                }
            }

            return string.Join(" ", Slice(releaseTokens, matchedEnd + 1, releaseTokens.Count - 1));
        }

        private static List<string> Slice(IReadOnlyList<string> tokens, int start, int end)
        {
            if (tokens == null || start < 0 || end < start || start >= tokens.Count)
            {
                return new List<string>();
            }

            var safeEnd = Math.Min(end, tokens.Count - 1);
            return tokens.Skip(start).Take(safeEnd - start + 1).ToList();
        }

        private static string NormalizeNumberToken(string token)
        {
            if (token.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (!decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }

        private static bool IsPureNumberToken(string token)
        {
            return NormalizeNumberToken(token).IsNotNullOrWhiteSpace();
        }

        private static bool IsSeriesPositionMarker(string token)
        {
            return token.IsNotNullOrWhiteSpace() && SeriesPositionMarkerTokens.Contains(token);
        }

        private static bool IsStructuralConnectorToken(string token)
        {
            return token.IsNullOrWhiteSpace() || StructuralConnectorTokens.Contains(token);
        }
    }
}
