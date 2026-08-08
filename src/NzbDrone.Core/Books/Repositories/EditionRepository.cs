using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dapper;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IEditionRepository : IBasicRepository<Edition>
    {
        List<Edition> GetAllMonitoredEditions();
        Edition FindByForeignEditionId(string foreignEditionId);
        List<Edition> FindAllByForeignEditionId(string foreignEditionId);
        Edition FindByHardcoverEditionId(string hardcoverEditionId);
        List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId);
        Edition FindByGoodreadsEditionId(long goodreadsEditionId);
        List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId);
        Edition FindByOpenLibraryEditionId(string openLibraryEditionId);
        List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId);
        Edition FindByGoogleBooksEditionId(string googleBooksEditionId);
        List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId);
        List<Edition> FindByBook(IEnumerable<int> ids);
        List<Edition> FindByAuthor(int id);
        List<Edition> FindByAuthorId(int id, bool onlyMonitored);
        Edition FindByTitle(int authorId, string title);
        List<Edition> GetEditionsForRefresh(int bookId);
        List<Edition> SetMonitored(Edition edition, bool isManualSelection = false);
        HashSet<string> FindExistingTitleSlugsForUniqueness(IEnumerable<string> baseTitleSlugs);
        List<Edition> FindAllByAsin(string asin);
        List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType);
        Edition FindByIsbn(string isbn);
        List<Edition> FindAllByIsbn(string isbn);
        int CountMissingMatchingTitles();
        List<Edition> GetMissingMatchingTitles(int afterId, int limit);
    }

    public class EditionRepository : BasicRepository<Edition>, IEditionRepository
    {
        private readonly Logger _logger;

        public EditionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
            _logger = LogManager.GetCurrentClassLogger();
        }

        public List<Edition> GetAllMonitoredEditions()
        {
            // WhereBuilderSqlite requires a concrete value; avoid boolean-expression simplification.
            var monitored = true;
            return Query(x => x.Monitored == monitored);
        }

        public int CountMissingMatchingTitles()
        {
            var sql = $"SELECT COUNT(*) FROM \"{_table}\" WHERE (\"MatchingTitle\" IS NULL OR \"MatchingTitle\" = '') AND (\"Title\" IS NOT NULL AND \"Title\" <> '')";
            using (var conn = _database.OpenConnection())
            {
                return conn.ExecuteScalar<int>(sql);
            }
        }

        public List<Edition> GetMissingMatchingTitles(int afterId, int limit)
        {
            var sql = $"SELECT * FROM \"{_table}\" WHERE \"Id\" > @afterId AND (\"MatchingTitle\" IS NULL OR \"MatchingTitle\" = '') AND (\"Title\" IS NOT NULL AND \"Title\" <> '') ORDER BY \"Id\" LIMIT @limit";
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<Edition>(sql, new { afterId, limit }).ToList();
            }
        }

        public Edition FindByForeignEditionId(string foreignEditionId)
        {
            return ChooseOldest(FindAllByForeignEditionId(foreignEditionId), nameof(Edition.ForeignEditionId), foreignEditionId);
        }

        public List<Edition> FindAllByForeignEditionId(string foreignEditionId)
        {
            return Query(x => x.ForeignEditionId == foreignEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        public Edition FindByHardcoverEditionId(string hardcoverEditionId)
        {
            return ChooseOldest(FindAllByHardcoverEditionId(hardcoverEditionId), nameof(Edition.HardcoverEditionId), hardcoverEditionId);
        }

        public List<Edition> FindAllByHardcoverEditionId(string hardcoverEditionId)
        {
            return Query(x => x.HardcoverEditionId == hardcoverEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        public Edition FindByGoodreadsEditionId(long goodreadsEditionId)
        {
            return ChooseOldest(FindAllByGoodreadsEditionId(goodreadsEditionId), nameof(Edition.GoodreadsEditionId), goodreadsEditionId.ToString());
        }

        public List<Edition> FindAllByGoodreadsEditionId(long goodreadsEditionId)
        {
            return Query(x => x.GoodreadsEditionId == goodreadsEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        public Edition FindByOpenLibraryEditionId(string openLibraryEditionId)
        {
            return ChooseOldest(FindAllByOpenLibraryEditionId(openLibraryEditionId), nameof(Edition.OpenLibraryEditionId), openLibraryEditionId);
        }

        public List<Edition> FindAllByOpenLibraryEditionId(string openLibraryEditionId)
        {
            return Query(x => x.OpenLibraryEditionId == openLibraryEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        public Edition FindByGoogleBooksEditionId(string googleBooksEditionId)
        {
            return ChooseOldest(FindAllByGoogleBooksEditionId(googleBooksEditionId), nameof(Edition.GoogleBooksEditionId), googleBooksEditionId);
        }

        public List<Edition> FindAllByGoogleBooksEditionId(string googleBooksEditionId)
        {
            return Query(x => x.GoogleBooksEditionId == googleBooksEditionId)
                .OrderBy(x => x.Id)
                .ToList();
        }

        private Edition ChooseOldest(List<Edition> editions, string field, string value, bool expectedPlural = false)
        {
            var matches = (editions ?? new List<Edition>())
                .Where(e => e != null)
                .OrderBy(e => e.Id)
                .ToList();

            var chosen = matches.FirstOrDefault();
            if (matches.Count > 1)
            {
                var message = "Found {0} editions with {1} {2}; legacy singular lookup using oldest (Id={3})";
                if (expectedPlural)
                {
                    _logger.Debug(message, matches.Count, field, value, chosen?.Id);
                }
                else
                {
                    _logger.Warn(message, matches.Count, field, value, chosen?.Id);
                }
            }

            return chosen;
        }

        public List<Edition> GetEditionsForRefresh(int bookId)
        {
            // IMPORTANT: In Chaptarr's multi-copy architecture, ForeignEditionId is not globally unique.
            // Each physical book copy has its own set of Edition rows (and thus duplicated ForeignEditionIds).
            // Refresh must only load editions for the specific BookId being refreshed; otherwise editions (and attached
            // files via EditionId) can get re-parented across copies, collapsing multiple books into one.
            return Query(r => r.BookId == bookId);
        }

        public List<Edition> FindByBook(IEnumerable<int> ids)
        {
            if (ids == null)
            {
                return new List<Edition>();
            }

            var bookIds = ids.Distinct().ToArray();
            if (bookIds.Length == 0)
            {
                return new List<Edition>();
            }

            // populate the books, author metadata, and author also
            // this hopefully speeds up the track matching a lot
            List<Edition> QueryForBookIds(int[] chunkBookIds)
            {
                var builder = new SqlBuilder(_database.DatabaseType)
                    .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                    .LeftJoin<Book, Author>((b, au) => b.AuthorId == au.Id)
	                    .Where<Edition>(r => Enumerable.Contains(chunkBookIds, r.BookId));

                return _database.QueryJoined<Edition, Book, Author>(builder, (edition, book, author) =>
                        {
                            if (book != null)
                            {
                                book.Author = author;
                                edition.Book = book;
                            }

                            return edition;
                        }).ToList();
            }

            if (_database.DatabaseType == DatabaseType.SQLite && bookIds.Length > SqliteVariableLimit.MaxParameters)
            {
                var editions = new List<Edition>();
                foreach (var batch in bookIds.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    editions.AddRange(QueryForBookIds(batch.ToArray()));
                }

                return editions.DistinctBy(e => e.Id).ToList();
            }

            return QueryForBookIds(bookIds);
        }

        public List<Edition> FindByAuthor(int id)
        {
            return Query(Builder().Join<Edition, Book>((e, b) => e.BookId == b.Id)
                         .Join<Book, Author>((b, a) => b.AuthorId == a.Id)
                         .Where<Author>(a => a.Id == id));
        }

        public List<Edition> FindByAuthorId(int authorId, bool onlyMonitored)
        {
            var builder = Builder().Join<Edition, Book>((e, b) => e.BookId == b.Id)
                .Where<Book>(b => b.AuthorId == authorId);

            if (onlyMonitored)
            {
                builder = builder.OrWhere<Edition>(e => e.Monitored == true);
                builder = builder.OrWhere<Book>(b => b.AnyEditionOk == true);
            }

            return Query(builder);
        }

        public Edition FindByTitle(int authorId, string title)
        {
            return Query(Builder().Join<Edition, Book>((e, b) => e.BookId == b.Id)
                .Where<Book>(b => b.AuthorId == authorId)
                .Where<Edition>(e => e.Monitored == true)
                .Where<Edition>(e => e.Title == title))
                .FirstOrDefault();
        }

        public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false)
        {
            var allEditions = FindByBook(new[] { edition.BookId });
            allEditions.ForEach(r => {
                if (r.Id == edition.Id)
                {
                    r.Monitored = true;
                    // CRITICAL: Only mark as manually selected if this was actually a manual selection
                    // Don't set ManualAdd=true for automatic imports from root folders
                    if (isManualSelection)
                    {
                        r.ManualAdd = true;
                        _logger.Debug("Setting ManualAdd=true for edition {0} due to manual selection", r.Id);
                    }
                }
                else
                {
                    r.Monitored = false;
                    // ManualAdd is the persisted user-selected edition. It must not
                    // float independently from the single monitored edition.
                    r.ManualAdd = false;
                }
            });

            // With multi-edition architecture, we might have books with different editions in separate directories
            // Ensure at least one edition is monitored, but allow for cases where no editions exist yet
            var monitoredCount = allEditions.Count(x => x.Monitored);
            if (allEditions.Any())
            {
                Ensure.That(monitoredCount <= 1).IsTrue();
                if (monitoredCount == 0)
                {
                    // If no edition is monitored, monitor the first available edition
                    var firstEdition = allEditions.FirstOrDefault();
                    if (firstEdition != null)
                    {
                        firstEdition.Monitored = true;
                        // Note: Don't set ManualAdd here since this is automatic selection
                    }
                }
            }

            UpdateMany(allEditions);
            return allEditions;
        }

        public HashSet<string> FindExistingTitleSlugsForUniqueness(IEnumerable<string> baseTitleSlugs)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;

            var baseSlugs = baseTitleSlugs?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(comparer)
                .ToArray() ?? Array.Empty<string>();

            var existing = new HashSet<string>(comparer);
            if (baseSlugs.Length == 0)
            {
                return existing;
            }

            var isPostgres = _database.DatabaseType == DatabaseType.PostgreSQL;
            var chunkSize = isPostgres ? 5000 : SqliteVariableLimit.MaxParameters / 2;

            foreach (var chunk in baseSlugs.Chunk(chunkSize))
            {
                var sql = new StringBuilder();
                var parameters = new DynamicParameters();

                parameters.Add("slugs", isPostgres ? chunk.ToArray() : chunk);

                var inClause = isPostgres ? "= ANY(@slugs)" : "IN @slugs";
                sql.Append($"SELECT DISTINCT \"TitleSlug\" FROM \"{_table}\" WHERE \"TitleSlug\" IS NOT NULL AND (\"TitleSlug\" {inClause}");

                var i = 0;
                foreach (var slug in chunk)
                {
                    var paramName = $"p{i}";
                    parameters.Add(paramName, EscapeLikePattern(slug) + "\\_%");
                    sql.Append($" OR \"TitleSlug\" LIKE @{paramName} ESCAPE '\\'");
                    i++;
                }

                sql.Append(")");

                foreach (var slug in _database.Query<string>(sql.ToString(), parameters))
                {
                    if (!string.IsNullOrWhiteSpace(slug))
                    {
                        existing.Add(slug.Trim());
                    }
                }
            }

            return existing;
        }

        private static string EscapeLikePattern(string value)
        {
            // Escape special LIKE wildcards for a backslash ESCAPE clause.
            // Also escape backslash itself.
            return value
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
        }

        public List<Edition> FindAllByAsin(string asin)
        {
            return FindAllByAsin(asin, null);
        }

        public List<Edition> FindAllByAsin(string asin, BookMediaType? mediaType)
        {
            if (string.IsNullOrWhiteSpace(asin))
            {
                return new List<Edition>();
            }

            var normalizedAsin = asin.Trim().ToUpperInvariant();
            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Edition)) ?? "Editions";

            // Stage 1: Try exact match on indexed Asin/AudibleASIN columns first (fast path)
            var builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .OrderBy($"\"{tableName}\".\"Id\"");

            // PostgreSQL uses functional indexes on UPPER(Asin/AudibleASIN) for case-insensitive lookups.
            // SQLite uses partial indexes on the raw columns.
            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                builder.Where(
                    $"(UPPER(\"{tableName}\".\"Asin\") = @asin OR UPPER(\"{tableName}\".\"AudibleASIN\") = @asin)",
                    new { asin = normalizedAsin });
            }
            else
            {
                builder.Where<Edition>(e => e.Asin == normalizedAsin || e.AudibleASIN == normalizedAsin);
            }

            if (mediaType.HasValue)
            {
                builder.Where<Book>(b => b.MediaType == mediaType.Value);
            }

            var results = _database.QueryJoined<Edition, Book, Author>(builder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();
            if (results.Any())
            {
                return results;
            }

            // Stage 2: Fallback to LIKE search on Asins JSON array (slower but comprehensive)
            // Pattern matches: %"ASIN"% within JSON array like ["ASIN1","ASIN2"]
            var likePattern = $"%\"{normalizedAsin}\"%";
            var fallbackBuilder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where($"UPPER(\"{tableName}\".\"Asins\") LIKE @pattern", new { pattern = likePattern })
                .OrderBy($"\"{tableName}\".\"Id\"");

            if (mediaType.HasValue)
            {
                fallbackBuilder.Where<Book>(b => b.MediaType == mediaType.Value);
            }

            return _database.QueryJoined<Edition, Book, Author>(fallbackBuilder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();
        }

        private static Edition MapEditionBookAuthor(Edition edition, Book book, Author author)
        {
            if (book != null)
            {
                book.Author = author;
                edition.Book = book;
            }

            return edition;
        }

        public Edition FindByIsbn(string isbn)
        {
            return ChooseOldest(FindAllByIsbn(isbn), nameof(Edition.Isbn13), isbn, expectedPlural: true);
        }

        public List<Edition> FindAllByIsbn(string isbn)
        {
            if (string.IsNullOrWhiteSpace(isbn))
            {
                return new List<Edition>();
            }

            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Edition)) ?? "Editions";
            var builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where<Edition>(e => e.Isbn13 == isbn)
                .OrderBy($"\"{tableName}\".\"Id\"");

            var editions = _database.QueryJoined<Edition, Book, Author>(builder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();

            if (editions.Any())
            {
                return editions;
            }

            builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
                .LeftJoin<Book, Author>((b, a) => b.AuthorId == a.Id)
                .Where<Edition>(e => e.Isbn10 == isbn)
                .OrderBy($"\"{tableName}\".\"Id\"");

            return _database.QueryJoined<Edition, Book, Author>(builder, MapEditionBookAuthor)
                .OrderBy(e => e.Id)
                .ToList();

        }
    }
}
