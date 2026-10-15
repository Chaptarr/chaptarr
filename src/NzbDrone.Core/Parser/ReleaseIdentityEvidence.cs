using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Parser
{
    public sealed class ReleaseIdentityEvidence
    {
        private static readonly Regex SegmentSeparatorRegex = new Regex(@"\s*[-\u2013\u2014]\s*", RegexOptions.Compiled);

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

        // Author evidence specifically, as opposed to a known narrator or series. Acceptance keys on
        // the author, so tolerances that could admit another author's product require this one.
        public bool HasAuthorIdentityEvidence { get; set; }

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
                    result.HasAuthorIdentityEvidence = true;
                    return result;
                }

                result.HasStructuredAuthorMismatch = true;
                return result;
            }

            if (titleMatch.MatchedVariant.IsNullOrWhiteSpace() || release.Title.IsNullOrWhiteSpace())
            {
                return result;
            }

            var cleanedReleaseTitle = Parser.CleanReleaseTitleForParsing(titleMatch.EvidenceTitle ?? release.Title);
            var releaseTokenSpans = ReleaseTitleMatchScorer.TokenizeWithSpans(cleanedReleaseTitle);
            var releaseTokens = releaseTokenSpans.Select(token => token.Value).ToList();
            if (!TryResolveMatchedSpan(releaseTokens, titleMatch, out var matchedStart, out var matchedEnd))
            {
                return result;
            }

            result.HasAuthorIdentityEvidence = HasAuthorEvidence(cleanedReleaseTitle, releaseTokenSpans,
                FindAuthorNameSpans(cleanedReleaseTitle, releaseTokenSpans, expectedAuthor), matchedStart, matchedEnd);
            if (result.HasAuthorIdentityEvidence)
            {
                result.HasPositiveIdentityEvidence = true;
                return result;
            }

            var segments = BuildTokenSegments(cleanedReleaseTitle);
            var titleSegmentIndex = FindContainingSegment(segments, matchedStart, matchedEnd);
            result.HasPositiveIdentityEvidence = HasBoundaryIdentity(cleanedReleaseTitle, releaseTokenSpans, releaseTokens, segments, titleSegmentIndex, matchedStart, matchedEnd, targetBook);
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

        internal static bool StructuredAuthorMatches(Author expectedAuthor, string releaseAuthor)
        {
            return GetKnownAuthorNames(expectedAuthor)
                .Any(expectedName => AuthorNameMatcher.ContainsAuthorName(expectedName, releaseAuthor));
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

        internal static IEnumerable<(int Start, int End, bool Inverted)> FindAuthorNameSpans(string source, IReadOnlyList<TitleTokenSpan> releaseTokens, Author expectedAuthor)
        {
            return GetKnownAuthorNames(expectedAuthor)
                .SelectMany(expectedName => AuthorNameMatcher.FindNameSpans(source, releaseTokens, expectedName));
        }

        internal static bool HasAuthorEvidence(string source, IReadOnlyList<TitleTokenSpan> tokens, IEnumerable<(int Start, int End, bool Inverted)> authorSpans, int matchedStart = -1, int matchedEnd = -1)
        {
            return authorSpans.Any(span => AuthorNameMatcher.IsCoherentNameSpan(source, tokens, span, matchedStart, matchedEnd));
        }

        private static List<string> GetMeaningfulSeriesTokens(IEnumerable<string> tokens)
        {
            return (tokens ?? Enumerable.Empty<string>())
                .Where(token => !IsSeriesPositionMarker(token) &&
                                !IsPureNumberToken(token))
                .ToList();
        }

        private static bool HasBoundaryIdentity(string source, IReadOnlyList<TitleTokenSpan> tokenSpans, IReadOnlyList<string> releaseTokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedStart, int matchedEnd, Book targetBook)
        {
            var leftTokens = GetLeftBoundaryTokens(releaseTokens, segments, titleSegmentIndex, matchedStart);
            var rightTokens = GetRightBoundaryTokens(releaseTokens, segments, titleSegmentIndex, matchedEnd);
            var leftText = GetLeftBoundaryText(source, tokenSpans, segments, titleSegmentIndex, matchedStart);
            var rightText = GetRightBoundaryText(source, tokenSpans, segments, titleSegmentIndex, matchedEnd);

            return BoundaryMatchesKnownNarrator(leftText, targetBook) ||
                   BoundaryMatchesKnownNarrator(rightText, targetBook) ||
                   BoundaryMatchesKnownSeries(leftText, leftTokens, true, targetBook) ||
                   BoundaryMatchesKnownSeries(rightText, rightTokens, false, targetBook);
        }

        private static bool BoundaryMatchesKnownNarrator(string rawCandidate, Book targetBook)
        {
            if (rawCandidate.IsNullOrWhiteSpace())
            {
                return false;
            }

            var narrators = GetKnownNarrators(targetBook).Select(AuthorNameMatcher.PrepareName).ToList();
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(rawCandidate);
            return narrators.Any(narrator => AuthorNameMatcher.ContainsAuthorName(narrator, rawCandidate, tokens));
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

        internal static IEnumerable<string> GetKnownNarrators(Book book)
        {
            return (book?.Editions ?? Enumerable.Empty<Edition>())
                .SelectMany(ReleaseNarratorEvidenceExtractor.GetKnownNarratorNames)
                .Concat(ReleaseNarratorEvidenceExtractor.SplitCredits(book?.Narrator));
        }

        internal static IEnumerable<(List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes)> GetKnownAuthorNames(Author author)
        {
            var canonical = AuthorNameMatcher.PrepareName(author?.Name);
            if (canonical.Tokens.Count > 0)
            {
                yield return canonical;
            }

            foreach (var name in (author?.Pseudonyms ?? Enumerable.Empty<string>())
                         .Where(name => name.IsNotNullOrWhiteSpace())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var alternate = AuthorNameMatcher.PrepareName(name);
                // A redundant shortened spelling must not erase conflicting evidence against the canonical name.
                if (!AuthorNameMatcher.AuthorNamesMatch(canonical, alternate))
                {
                    yield return alternate;
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

        private static string GetLeftBoundaryText(string source, IReadOnlyList<TitleTokenSpan> tokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedStart)
        {
            if (titleSegmentIndex < 0)
            {
                titleSegmentIndex = FindContainingSegment(segments, matchedStart, matchedStart);
            }

            if (titleSegmentIndex >= 0)
            {
                var titleSegment = segments[titleSegmentIndex];
                if (matchedStart > titleSegment.Start)
                {
                    return SliceText(source, tokens, titleSegment.Start, matchedStart - 1);
                }

                if (titleSegmentIndex > 0)
                {
                    return segments[titleSegmentIndex - 1].Text;
                }
            }

            return SliceText(source, tokens, 0, matchedStart - 1);
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

        private static string GetRightBoundaryText(string source, IReadOnlyList<TitleTokenSpan> tokens, IReadOnlyList<TokenSegment> segments, int titleSegmentIndex, int matchedEnd)
        {
            if (titleSegmentIndex < 0)
            {
                titleSegmentIndex = FindContainingSegment(segments, matchedEnd, matchedEnd);
            }

            if (titleSegmentIndex >= 0)
            {
                var titleSegment = segments[titleSegmentIndex];
                if (matchedEnd < titleSegment.End)
                {
                    return SliceText(source, tokens, matchedEnd + 1, titleSegment.End);
                }

                if (titleSegmentIndex + 1 < segments.Count)
                {
                    return segments[titleSegmentIndex + 1].Text;
                }
            }

            return SliceText(source, tokens, matchedEnd + 1, tokens.Count - 1);
        }

        private static string SliceText(string source, IReadOnlyList<TitleTokenSpan> tokens, int start, int end)
        {
            if (tokens == null || start < 0 || end < start || start >= tokens.Count)
            {
                return string.Empty;
            }

            return source.Substring(tokens[start].Start, tokens[Math.Min(end, tokens.Count - 1)].End - tokens[start].Start);
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

    }
}
