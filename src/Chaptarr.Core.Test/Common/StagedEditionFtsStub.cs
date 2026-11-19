using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Common
{
    // Fixture selectors supply their original candidates; staging still gates books before ranking.
    internal abstract class StagedEditionFtsStub : IEditionFtsRepository
    {
        private List<EditionFtsMatch> _recalledEditions = new();

        public abstract bool FtsTableExists();
        public abstract void RebuildIndex();
        public virtual void EnsureIndexPopulated() => RebuildIndex();

        protected abstract List<EditionFtsMatch> SelectEditions(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit);

        public virtual List<BookFtsMatch> RecallBooks(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null, int limit = 20, bool monitoredOnly = false)
        {
            var terms = (tokens ?? Array.Empty<string>()).ToList();
            limit = limit > 0 ? limit : int.MaxValue;
            _recalledEditions = SelectEditions(authorId, terms, mediaType, limit) ?? new List<EditionFtsMatch>();
            var books = _recalledEditions.Where(candidate => candidate != null)
                .GroupBy(candidate => candidate.BookId)
                .Select(group =>
                {
                    var candidate = group.First();
                    return new BookFtsMatch
                    {
                        BookId = candidate.BookId,
                        AuthorId = candidate.AuthorId,
                        AuthorName = candidate.AuthorName,
                        BookTitle = candidate.BookTitle,
                        MatchScore = group.Max(edition => edition.MatchScore)
                    };
                }).Take(limit).ToList();
            trace?.Invoke(new EditionFtsTraceEvent { EventType = "input", Step = "stage1_book_recall", Terms = terms });
            return books;
        }

        public virtual List<EditionFtsMatch> RankEditions(IReadOnlyCollection<BookFtsMatch> recalledBooks,
            IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries, BookMediaType mediaType, Action<EditionFtsTraceEvent> trace = null)
        {
            var books = (recalledBooks ?? Array.Empty<BookFtsMatch>()).ToDictionary(book => book.BookId);
            var queries = fieldQueries ?? Array.Empty<EditionFtsFieldQuery>();
            var editions = _recalledEditions.Where(candidate => candidate != null && books.ContainsKey(candidate.BookId)).ToList();
            foreach (var candidate in editions)
            {
                var titleTokens = ReleaseTitleMatchScorer.Tokenize(candidate.MatchingTitle ?? candidate.EditionTitle ?? candidate.BookTitle).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var detailTokens = ReleaseTitleMatchScorer.Tokenize(mediaType == BookMediaType.Audiobook ? candidate.NarratorNames : candidate.Publisher).ToHashSet(StringComparer.OrdinalIgnoreCase);
                candidate.BroadRecallScore = books[candidate.BookId].MatchScore;
                candidate.Stage2FieldHits = queries.Select(query => new EditionFtsFieldHit
                {
                    FieldKey = query.Key,
                    SourceFields = query.SourceFields,
                    TitleHit = query.Terms?.Any(titleTokens.Contains) == true,
                    DetailHit = query.Terms?.Any(detailTokens.Contains) == true,
                    TitleBm25 = query.Terms?.Any(titleTokens.Contains) == true ? 1 : 0,
                    DetailBm25 = query.Terms?.Any(detailTokens.Contains) == true ? 1 : 0
                }).Where(hit => hit.TitleHit || hit.DetailHit).ToList();
            }

            for (var index = 0; index < editions.Count; index++)
            {
                var candidate = editions[index];
                trace?.Invoke(new EditionFtsTraceEvent
                {
                    EventType = "candidate", Step = "stage2_field_ranking", RawRank = index + 1,
                    EditionId = candidate.EditionId, BookId = candidate.BookId, AuthorId = candidate.AuthorId,
                    Score = candidate.MatchScore, EditionTitle = candidate.EditionTitle, BookTitle = candidate.BookTitle,
                    AuthorName = candidate.AuthorName
                });
            }

            return editions;
        }
    }
}
