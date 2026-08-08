using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.History
{
    public interface IHistoryRepository : IBasicRepository<EntityHistory>
    {
        PagingSpec<EntityHistory> GetPaged(PagingSpec<EntityHistory> pagingSpec, BookMediaType mediaType);
        EntityHistory MostRecentForBook(int bookId);
        EntityHistory MostRecentForDownloadId(string downloadId);
        List<EntityHistory> FindByDownloadId(string downloadId);
        List<EntityHistory> FindByDownloadIds(List<string> downloadIds, EntityHistoryEventType? eventType);
        List<EntityHistory> GetByAuthor(int authorId, EntityHistoryEventType? eventType);
        List<EntityHistory> GetByBook(int bookId, EntityHistoryEventType? eventType);
        List<EntityHistory> FindDownloadHistory(int idAuthorId, QualityModel quality);
        void DeleteForAuthor(int authorId);
        List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType);
    }

    public class HistoryRepository : BasicRepository<EntityHistory>, IHistoryRepository
    {
        public HistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public EntityHistory MostRecentForBook(int bookId)
        {
            return Query(h => h.BookId == bookId).MaxBy(h => h.Date);
        }

        public EntityHistory MostRecentForDownloadId(string downloadId)
        {
            return Query(h => h.DownloadId == downloadId).MaxBy(h => h.Date);
        }

        public List<EntityHistory> FindByDownloadId(string downloadId)
        {
            return FindByDownloadIds(new List<string> { downloadId }, null);
        }

        public List<EntityHistory> FindByDownloadIds(List<string> downloadIds, EntityHistoryEventType? eventType)
        {
            var ids = (downloadIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ids.Length == 0)
            {
                return new List<EntityHistory>();
            }

            List<EntityHistory> QueryChunk(string[] chunkIds)
            {
                var builder = Builder()
                    .LeftJoin<EntityHistory, Author>((h, a) => h.AuthorId == a.Id)
                    .LeftJoin<EntityHistory, Book>((h, a) => h.BookId == a.Id)
                    .Where<EntityHistory>(h => Enumerable.Contains(chunkIds, h.DownloadId));

                if (eventType.HasValue)
                {
                    var eventTypeValue = eventType.Value;
                    builder.Where<EntityHistory>(h => h.EventType == eventTypeValue);
                }

                return _database.QueryJoined<EntityHistory, Author, Book>(
                    builder,
                    (history, author, book) =>
                    {
                        history.Author = author;
                        history.Book = book;
                        return history;
                    }).ToList();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && ids.Length > SqliteVariableLimit.MaxParameters)
            {
                var results = new List<EntityHistory>();

                foreach (var batch in ids.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    results.AddRange(QueryChunk(batch));
                }

                return results.DistinctBy(h => h.Id).ToList();
            }

            return QueryChunk(ids);
        }

        public List<EntityHistory> GetByAuthor(int authorId, EntityHistoryEventType? eventType)
        {
            var builder = Builder()
                .LeftJoin<EntityHistory, Book>((h, b) => h.BookId == b.Id)
                .Where<EntityHistory>(h => h.AuthorId == authorId);

            if (eventType.HasValue)
            {
                builder.Where<EntityHistory>(h => h.EventType == eventType);
            }

            return _database.QueryJoined<EntityHistory, Book>(
                builder,
                (history, book) =>
                {
                    history.Book = book;
                    return history;
                }).OrderByDescending(h => h.Date).ToList();
        }

        public List<EntityHistory> GetByBook(int bookId, EntityHistoryEventType? eventType)
        {
            var builder = Builder()
                .Join<EntityHistory, Book>((h, a) => h.BookId == a.Id)
                .Where<EntityHistory>(h => h.BookId == bookId);

            if (eventType.HasValue)
            {
                builder.Where<EntityHistory>(h => h.EventType == eventType);
            }

            return _database.QueryJoined<EntityHistory, Book>(
                builder,
                (history, book) =>
                {
                    history.Book = book;
                    return history;
                }).OrderByDescending(h => h.Date).ToList();
        }

        public List<EntityHistory> FindDownloadHistory(int idAuthorId, QualityModel quality)
        {
            var allowed = new[] { (int)EntityHistoryEventType.Grabbed, (int)EntityHistoryEventType.DownloadFailed, (int)EntityHistoryEventType.BookFileImported };

            return Query(h => h.AuthorId == idAuthorId &&
                         h.Quality == quality &&
                         allowed.Contains((int)h.EventType));
        }

        public void DeleteForAuthor(int authorId)
        {
            Delete(c => c.AuthorId == authorId);
        }

        protected override SqlBuilder PagedBuilder() => new SqlBuilder(_database.DatabaseType)
            .LeftJoin<EntityHistory, Author>((h, a) => h.AuthorId == a.Id)
            .LeftJoin<EntityHistory, Book>((h, a) => h.BookId == a.Id);

        protected override IEnumerable<EntityHistory> PagedQuery(SqlBuilder builder) =>
            _database.QueryJoined<EntityHistory, Author, Book>(builder, (history, author, book) =>
                    {
                        history.Author = author;
                        history.Book = book;
                        return history;
                    });

        public PagingSpec<EntityHistory> GetPaged(PagingSpec<EntityHistory> pagingSpec, BookMediaType mediaType)
        {
            var recordsBuilder = PagedBuilder()
                .Where<Book>(book => book.MediaType == mediaType);

            var countBuilder = PagedBuilder()
                .SelectCount()
                .Where<Book>(book => book.MediaType == mediaType);

            pagingSpec.Records = GetPagedRecords(recordsBuilder, pagingSpec, PagedQuery);
            pagingSpec.TotalRecords = GetPagedRecordCount(countBuilder, pagingSpec);

            return pagingSpec;
        }

        public List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType)
        {
            var builder = Builder()
                .LeftJoin<EntityHistory, Author>((h, a) => h.AuthorId == a.Id)
                .LeftJoin<EntityHistory, Book>((h, b) => h.BookId == b.Id)
                .Where<EntityHistory>(x => x.Date >= date);

            if (eventType.HasValue)
            {
                builder.Where<EntityHistory>(h => h.EventType == eventType);
            }

            return _database.QueryJoined<EntityHistory, Author, Book>(builder, (history, author, book) =>
            {
                history.Author = author;
                history.Book = book;
                return history;
            }).OrderBy(h => h.Date).ToList();
        }
    }
}
