using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dapper;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.History
{
    public interface IHistoryRepository : IBasicRepository<EntityHistory>
    {
        PagingSpec<EntityHistory> GetPaged(PagingSpec<EntityHistory> pagingSpec, BookMediaType mediaType);
        EntityHistory MostRecentForDownloadId(string downloadId);
        List<EntityHistory> FindByDownloadId(string downloadId);
        List<EntityHistory> FindByDownloadIds(List<string> downloadIds, EntityHistoryEventType? eventType);
        List<EntityHistory> GetByAuthor(int authorId, EntityHistoryEventType? eventType);
        List<EntityHistory> GetByBook(int bookId, EntityHistoryEventType? eventType);
        List<EntityHistory> GetAcquisitionHistory(IEnumerable<int> bookIds);
        List<EntityHistory> FindDownloadHistory(int idAuthorId, QualityModel quality);
        void DeleteForAuthor(int authorId);
        List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType);
    }

    public class HistoryRepository : BasicRepository<EntityHistory>, IHistoryRepository
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public HistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<EntityHistory> GetAcquisitionHistory(IEnumerable<int> bookIds)
        {
            var ids = (bookIds ?? Enumerable.Empty<int>()).Where(id => id > 0).Distinct().ToArray();
            if (ids.Length == 0)
            {
                return new List<EntityHistory>();
            }

            var postgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var idPredicate = postgres ? "= ANY(@ids)" : "IN @ids";
            var materialized = postgres ? "MATERIALIZED " : string.Empty;
            var dataEntries = postgres ? "jsonb_each_text(\"Data\"::jsonb)" : "json_each(\"Data\")";
            var incompatible = $"EXISTS (SELECT 1 FROM {dataEntries} WHERE LOWER(key) = 'alternativecopyincompatible' AND LOWER(TRIM(value)) = 'true') AND EXISTS (SELECT 1 FROM {dataEntries} WHERE LOWER(key) = 'alternativecopies' AND LOWER(TRIM(value)) = 'true')";
            var sql = $@"SELECT ""History"".* FROM ""History"" WHERE ""Id"" IN (
                WITH scoped AS (SELECT ""Id"", ""BookId"", ""Date"", ""EventType"", ""Data"", ""DownloadId"" FROM ""History"" WHERE ""BookId"" {idPredicate}),
                marked AS (SELECT ""Id"" FROM scoped WHERE ""EventType"" = @grabbed AND {incompatible}),
                deleted AS (SELECT ""Id"", ""BookId"", ""Date"", ""Data"",
                    ROW_NUMBER() OVER (PARTITION BY ""BookId"" ORDER BY ""Date"" DESC, ""Id"" DESC) AS rank
                    FROM scoped WHERE ""EventType"" = {(int)EntityHistoryEventType.BookFileDeleted}),
                released AS {materialized}(SELECT ""Id"", ""BookId"", ""Date"" FROM deleted WHERE rank = 1
                    AND EXISTS (SELECT 1 FROM {dataEntries} WHERE LOWER(key) = 'reason'
                        AND value IN ('{nameof(DeleteMediaFileReason.Manual)}', '{nameof(DeleteMediaFileReason.MissingFromDisk)}'))),
                retained AS {materialized}(SELECT DISTINCT e.""BookId"" FROM ""Editions"" e JOIN ""BookFiles"" f ON f.""EditionId"" = e.""Id""
                    WHERE e.""BookId"" {idPredicate}),
                latest AS (SELECT ""Id"", ""BookId"", ""Date"", ""DownloadId"", ROW_NUMBER() OVER (PARTITION BY ""BookId"" ORDER BY ""Date"" DESC, ""Id"" DESC) AS rank
                    FROM scoped WHERE ""Id"" NOT IN (SELECT ""Id"" FROM marked)
                        AND (""EventType"" = @grabbed OR
                            ((""DownloadId"" IS NULL OR ""DownloadId"" = '') AND ""EventType"" IN
                                ({(int)EntityHistoryEventType.DownloadFailed}, {(int)EntityHistoryEventType.DownloadIgnored})))),
                failed AS (SELECT DISTINCT latest.""Id"" FROM latest JOIN ""History"" failure
                    ON failure.""BookId"" = latest.""BookId"" AND failure.""DownloadId"" = latest.""DownloadId""
                    WHERE latest.rank = 1 AND failure.""BookId"" {idPredicate} AND failure.""EventType"" = {(int)EntityHistoryEventType.DownloadFailed}
                        AND (failure.""Date"" > latest.""Date"" OR (failure.""Date"" = latest.""Date"" AND failure.""Id"" > latest.""Id"")))
                SELECT latest.""Id"" FROM latest
                LEFT JOIN failed ON failed.""Id"" = latest.""Id""
                LEFT JOIN released ON released.""BookId"" = latest.""BookId""
                LEFT JOIN retained ON retained.""BookId"" = latest.""BookId""
                WHERE latest.rank = 1
                    AND failed.""Id"" IS NULL
                    AND (released.""Id"" IS NULL OR released.""Date"" < latest.""Date"" OR
                        (released.""Date"" = latest.""Date"" AND released.""Id"" <= latest.""Id"") OR retained.""BookId"" IS NOT NULL)
                UNION SELECT ""Id"" FROM marked) ORDER BY ""Id""";
            // Match failures against indexed History and materialize PostgreSQL deletion/file evidence once for the batch.
            // Rank every deletion reason before allowing manual/confirmed-missing last-file deletion to release a grab.
            // Filter after selecting it so an earlier grab cannot regain protection.
            var stopwatch = Stopwatch.StartNew();
            using var connection = _database.OpenConnection();
            var chunkSize = postgres ? ids.Length : SqliteVariableLimit.MaxParameters - 1;
            var history = ids.Chunk(chunkSize).SelectMany(chunk => connection.Query<EntityHistory>(sql,
                new { ids = chunk, grabbed = (int)EntityHistoryEventType.Grabbed })).ToList();
            Logger.Debug("[ACQUISITION-HISTORY] {0}: {1} requested books, {2} batches, {3} returned rows in {4} ms",
                _database.DatabaseType, ids.Length, (ids.Length + chunkSize - 1) / chunkSize, history.Count, stopwatch.ElapsedMilliseconds);
            return history;
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
                .Where($"(\"Books\".\"MediaType\" = @mediaType OR \"History\".\"EventType\" IN ({(int)EntityHistoryEventType.AuthorsMerged}, {(int)EntityHistoryEventType.AuthorSplit}))", new { mediaType = (int)mediaType });

            var countBuilder = PagedBuilder()
                .SelectCount()
                .Where($"(\"Books\".\"MediaType\" = @mediaType OR \"History\".\"EventType\" IN ({(int)EntityHistoryEventType.AuthorsMerged}, {(int)EntityHistoryEventType.AuthorSplit}))", new { mediaType = (int)mediaType });

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
