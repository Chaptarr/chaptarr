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
            return IsSatisfiedBy(subject, searchCriteria, null);
        }

        internal Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria, ReleaseTitleMatchScorer.CatalogPreparation catalogPreparation)
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
                // Same entry point the decision maker uses, so a caller that does not pre-populate
                // SearchCriteriaMatch still gets sibling competition instead of the target in isolation.
                best = ReleaseTitleMatchScorer.FindBestMatchForSearch(subject.Release, context.AuthorName, context.Books, authorHint, context.AuthorCatalog, catalogPreparation ?? new ReleaseTitleMatchScorer.CatalogPreparation());
            }

            if (best == null)
            {
                _logger.Trace("[TITLE-MATCH] Soft-filter mismatch (no match candidate). Release='{0}' ReleaseAuthor='{1}' SearchAuthor='{2}' Books={3}",
                    subject.Release.Title,
                    authorHint ?? "<null>",
                    context.AuthorName ?? "<null>",
                    context.Books?.Count ?? 0);
                return Decision.RejectSoftFilter("Title/Author mismatch", "Matching");
            }

            var identity = ReleaseIdentityEvidence.Analyze(subject.Release, context.Author, best);
            if (identity.HasStructuredAuthorMismatch)
            {
                return Decision.RejectSoftFilter("Author mismatch: {0}", "Matching", subject.Release.Author);
            }

            if (IsAcceptedMatch(best, identity, _configService?.BookMatchingStrictness ?? BookMatchingStrictness.Balanced))
            {
                return Decision.Accept();
            }

            if (!best.IsMatch)
            {
                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace("[TITLE-MATCH] Mismatch. Release='{0}' ReleaseAuthor='{1}' BestBook='{2}' Variant='{3}' Problem={4} Leftovers=[{5}]",
                        subject.Release.Title,
                        authorHint ?? "<null>",
                        best.Book?.Title ?? "<null>",
                        best.MatchedVariant ?? "<none>",
                        best.ProblemCode,
                        string.Join(", ", best.MeaningfulLeftovers.Take(8)));
                }

                return GetMismatchDecision(best);
            }

            return Decision.Accept();
        }

        internal static bool IsAcceptedMatch(TitleMatchResult match, ReleaseIdentityEvidence identity, BookMatchingStrictness strictness)
        {
            if (match?.IsMatch == true ||
                (identity?.HasAuthorIdentityEvidence == true && match?.ProblemCode == TitleMatchProblemCode.None))
            {
                return true;
            }

            if (strictness == BookMatchingStrictness.Strict ||
                match?.Problems?.Any() != true)
            {
                return false;
            }

            // A suspicious adjacent number is forgivable only once identity is actually proven.
            // Absent that, a title token followed by an issue number is another publisher's numbered
            // product, not a numbered edition of this book, and forgiving it would accept it outright.
            if (identity?.HasAuthorIdentityEvidence != true)
            {
                return false;
            }

            return match.Problems.All(problem =>
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
                    return Decision.RejectSoftFilter("Unexplained number adjacent to matched title", "Matching");

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
