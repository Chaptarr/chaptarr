using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Parser
{
    public sealed class ReleaseIdentityEvidence
    {
        public bool HasStructuredAuthorMismatch { get; set; }
        public bool HasAuthorIdentityEvidence { get; set; }

        public static ReleaseIdentityEvidence Analyze(ReleaseInfo release, Author expectedAuthor, TitleMatchResult titleMatch)
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

        internal static IEnumerable<(int Start, int End, bool Inverted)> FindAuthorNameSpans(string source, IReadOnlyList<TitleTokenSpan> releaseTokens, Author expectedAuthor)
        {
            return GetKnownAuthorNames(expectedAuthor)
                .SelectMany(expectedName => AuthorNameMatcher.FindNameSpans(source, releaseTokens, expectedName));
        }

        internal static bool HasAuthorEvidence(string source, IReadOnlyList<TitleTokenSpan> tokens, IEnumerable<(int Start, int End, bool Inverted)> authorSpans, int matchedStart = -1, int matchedEnd = -1)
        {
            return authorSpans.Any(span => AuthorNameMatcher.IsCoherentNameSpan(source, tokens, span, matchedStart, matchedEnd));
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
    }
}
