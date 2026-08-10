using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class ReleaseTitleMatchSpecification : IDecisionEngineSpecification
    {
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ReleaseTitleMatchSpecification(Logger logger, IConfigService configService = null)
        {
            _configService = configService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.Release?.Title == null)
            {
                return Decision.Accept();
            }

            if (subject.PackDetection?.Verdict == ReleasePackDetectionVerdict.MultipleBooks)
            {
                return Decision.Accept();
            }

            var context = GetMatchContext(subject, searchCriteria);
            if (context == null)
            {
                return Decision.Accept();
            }

            var authorHint = subject.Release.Author;
            if (string.IsNullOrWhiteSpace(authorHint))
            {
                authorHint = subject.ParsedBookInfo?.AuthorName;
            }

            var best = context.ExistingMatch;
            if (best == null)
            {
                best = ReleaseTitleMatchScorer.FindBestMatch(subject.Release.Title, context.AuthorName, context.Books, authorHint, context.AuthorCatalog);
            }

            if (best == null)
            {
                _logger.Debug("[TITLE-MATCH] Soft-filter mismatch (no match candidate). Release='{0}' ReleaseAuthor='{1}' SearchAuthor='{2}' Books={3}",
                    subject.Release.Title,
                    authorHint ?? "<null>",
                    context.AuthorName ?? "<null>",
                    context.Books?.Count ?? 0);
                return Decision.RejectSoftFilter("Title/Author mismatch", "Matching");
            }

            var identity = ReleaseIdentityEvidence.Analyze(subject.Release, context.Author, best.Book, best);
            if (identity.HasStructuredAuthorMismatch)
            {
                return Decision.RejectSoftFilter("Title/Author mismatch", "Matching");
            }

            if (IsAcceptedMatch(best, identity, _configService?.BookMatchingStrictness ?? BookMatchingStrictness.Balanced))
            {
                return Decision.Accept();
            }

            if (!best.IsMatch)
            {
                _logger.Debug("[TITLE-MATCH] Mismatch. Release='{0}' ReleaseAuthor='{1}' BestBook='{2}' Variant='{3}' Problem={4} Leftovers=[{5}]",
                    subject.Release.Title,
                    authorHint ?? "<null>",
                    best.Book?.Title ?? "<null>",
                    best.MatchedVariant ?? "<none>",
                    best.ProblemCode,
                    string.Join(", ", best.MeaningfulLeftovers.Take(8)));
                return GetMismatchDecision(best);
            }

            return Decision.Accept();
        }

        internal static bool IsAcceptedMatch(TitleMatchResult match, ReleaseIdentityEvidence identity, BookMatchingStrictness strictness)
        {
            if (match?.IsMatch == true ||
                (identity?.HasPositiveIdentityEvidence == true && match?.ProblemCode == TitleMatchProblemCode.None))
            {
                return true;
            }

            if (strictness == BookMatchingStrictness.Strict ||
                match?.Problems?.Any() != true)
            {
                return false;
            }

            return match.Problems.All(problem =>
                problem.Code == TitleMatchProblemCode.SeriesPositionMismatch ||
                problem.Code == TitleMatchProblemCode.SuspiciousAdjacentNumber);
        }

        private static TitleMatchContext GetMatchContext(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (searchCriteria?.Author != null && searchCriteria.Books?.Any() == true)
            {
                var books = searchCriteria.Books.Where(book => book != null).ToList();
                if (books.Count == 0)
                {
                    return null;
                }

                return new TitleMatchContext
                {
                    Author = searchCriteria.Author,
                    AuthorName = searchCriteria.Author.Name,
                    Books = books,
                    AuthorCatalog = searchCriteria.AuthorCatalog?.Any() == true
                        ? searchCriteria.AuthorCatalog
                        : searchCriteria.Author.Books?.Any() == true ? searchCriteria.Author.Books : books,
                    ExistingMatch = subject.SearchCriteriaMatch
                };
            }

            if (subject?.Author == null || subject.Books?.Count != 1)
            {
                return null;
            }

            return new TitleMatchContext
            {
                Author = subject.Author,
                AuthorName = subject.Author.Name,
                Books = subject.Books,
                AuthorCatalog = subject.Author.Books?.Any() == true ? subject.Author.Books : subject.Books,
                ExistingMatch = subject.SearchCriteriaMatch
            };
        }

        private static Decision GetMismatchDecision(TitleMatchResult best)
        {
            switch (best.ProblemCode)
            {
                case TitleMatchProblemCode.SeriesPositionMismatch:
                    return Decision.RejectHardFilter("Series position mismatch", "Matching");

                case TitleMatchProblemCode.SiblingTitleContradiction:
                    return Decision.RejectHardFilter("Release appears to match a different book by this author", "Matching");

                case TitleMatchProblemCode.SuspiciousAdjacentNumber:
                    return Decision.RejectSoftFilter("Unexplained number after matched title", "Matching");

                default:
                    return Decision.RejectSoftFilter("Title/Author mismatch", "Matching");
            }
        }

        private sealed class TitleMatchContext
        {
            public Author Author { get; set; }
            public string AuthorName { get; set; }
            public List<Book> Books { get; set; }
            public IEnumerable<Book> AuthorCatalog { get; set; }
            public TitleMatchResult ExistingMatch { get; set; }
        }
    }
}
