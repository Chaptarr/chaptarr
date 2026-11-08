using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Books
{
    public sealed class BookSearchTarget
    {
        public int BookId { get; set; }
        public int AuthorId { get; set; }
    }

		    public interface IBookRepository : IBasicRepository<Book>
		    {
		        List<Book> GetBooks(int authorId);
        List<Book> GetImportListBookIdentities();
                List<int> GetLibraryBookIds(IEnumerable<int> bookIds = null) => throw new NotImplementedException();
                // Tolerant lookup for drifting snapshots; unlike Get, missing rows are expected.
                IEnumerable<Book> FindExisting(IEnumerable<int> ids) => throw new NotImplementedException();
		        List<Book> GetLastBooks(IEnumerable<int> authorIds);
		        List<Book> GetNextBooks(IEnumerable<int> authorIds);
		        List<Book> GetBooksByAuthorId(int authorId);
        IReadOnlySet<BookMediaType> GetMediaTypesByAuthor(int authorId) => throw new NotImplementedException();
		        List<Book> GetBooksForRefresh(int authorId, IEnumerable<string> providerIds);
		        List<Book> GetBooksByFileIds(IEnumerable<int> fileIds);

		        Book FindByIsbn(string isbn);
		        Book FindByAsin(string asin);
		        Book FindByProviderIds(string hardcoverBookId = null, string goodreadsBookId = null, string openLibraryWorkId = null);
		        Book FindByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType);
		        List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType);
        Dictionary<string, List<int>> FindIdsByProviderIds(string provider, IEnumerable<string> ids) => throw new NotImplementedException();
		        List<Book> FindBySlug(string titleSlug);
        List<Book> FindTitleSlugConflicts(IEnumerable<string> slugs, IDbConnection connection = null, IDbTransaction transaction = null);
		        List<BookSearchTarget> GetMissingBookSearchTargets(BookMediaType? mediaType, int? authorId) => throw new NotImplementedException();
		        List<BookSearchTarget> GetCutoffUnmetSearchTargets(List<QualitiesBelowCutoff> qualitiesBelowCutoff, BookMediaType? mediaType, int? authorId) => throw new NotImplementedException();
		        PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec);
		        PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec, List<QualitiesBelowCutoff> qualitiesBelowCutoff);
	        List<Book> BooksBetweenDates(DateTime startDate, DateTime endDate, bool includeUnmonitored);
	        List<Book> AuthorBooksBetweenDates(Author author, DateTime startDate, DateTime endDate, bool includeUnmonitored);
        void SetMonitored(IEnumerable<int> ids, bool monitored);
        void SetMonitoring(IList<Book> books) => throw new NotImplementedException();
        void UpdateManyAtomic(IList<Book> books, Action<IDbConnection, IDbTransaction> writeRelated);
        void DeleteManyAtomic(IEnumerable<int> ids, IEnumerable<int> deletedFileIds = null);
        List<Book> GetAuthorBooksWithFiles(Author author);
        List<Book> GetBooksBySeries(int seriesId);
	        void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds = null);
            void SetAuthorId(Book book) => throw new NotImplementedException();
            void SetAuthorId(IList<Book> books) => throw new NotImplementedException();
	        BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null);
	        BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null) => GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded);
	        PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null);
	        PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null) => GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded);
	        List<int> GetBookIds(bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null, bool? library = null) => throw new NotImplementedException();
	    }

    public class BookRepository : BasicRepository<Book>, IBookRepository
    {
        private List<PropertyInfo> _updatePropertiesWithoutAuthorId;

        public BookRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override List<PropertyInfo> GetUpdateProperties()
        {
            return _updatePropertiesWithoutAuthorId ??= base.GetUpdateProperties()
                .Where(property => property.Name != nameof(Book.AuthorId))
                .ToList();
        }

        public void UpdateManyAtomic(IList<Book> books, Action<IDbConnection, IDbTransaction> writeRelated)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var updated = base.UpdateMany(books, connection, transaction);
            if (updated != books.Count)
            {
                throw new ModelConflictException(typeof(Book), 0, "Books changed before their update could be saved.");
            }

            writeRelated(connection, transaction);
            transaction.Commit();
        }

        public void DeleteManyAtomic(IEnumerable<int> ids, IEnumerable<int> deletedFileIds = null)
        {
            var bookIds = ids.Distinct().ToArray();
            if (bookIds.Length == 0) return;

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            foreach (var deletedFiles in (deletedFileIds ?? Enumerable.Empty<int>()).Distinct().Chunk(SqliteVariableLimit.MaxParameters))
            {
                var filePredicate = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                connection.Execute($"DELETE FROM \"BookFiles\" WHERE \"Id\" {filePredicate}", new { ids = deletedFiles }, transaction);
            }
            foreach (var chunk in bookIds.Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : bookIds.Length))
            {
                var predicate = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                var editions = $"SELECT \"Id\" FROM \"Editions\" WHERE \"BookId\" {predicate}";
                var parameters = new { ids = chunk };
                // Only files confirmed by the deletion executor can lose their records.
                // Anything left must remain visible even when its book is removed.
                connection.Execute($"UPDATE \"BookFiles\" SET \"EditionId\" = 0 WHERE \"EditionId\" IN ({editions})", parameters, transaction);
                connection.Execute($"DELETE FROM \"Editions\" WHERE \"BookId\" {predicate}", parameters, transaction);
                base.DeleteMany(chunk, connection, transaction);
            }

            transaction.Commit();
        }

        public void SetAuthorId(Book book)
        {
            if (book == null)
            {
                return;
            }

            SetAuthorId(new List<Book> { book });
        }

        public void SetAuthorId(IList<Book> books)
        {
            var safeBooks = books?.Where(book => book != null).ToList();
            if (safeBooks?.Any() != true)
            {
                return;
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var writer = new LibraryIdentityWriter(connection, transaction, _database.DatabaseType, null);
            foreach (var group in safeBooks.GroupBy(book => book.AuthorId))
                writer.ReassignBooks(group.Select(book => book.Id).ToArray(), group.Key);
            transaction.Commit();
        }

        public IEnumerable<Book> FindExisting(IEnumerable<int> ids)
        {
            return FindMany(ids);
        }

        public List<Book> GetImportListBookIdentities()
        {
            // This snapshot resolves ownership only. Import-list writes reload their selected rows.
            return _database.Query<Book>(@"SELECT ""Id"", ""AuthorId"", ""MediaType"", ""Title"",
                ""HardcoverBookId"", ""GoodreadsWorkId"", ""OpenLibraryWorkId"", ""ProviderIds"",
                ""GoodreadsBookId"", ""OpenLibraryEditionId"", ""GoogleBooksId"", ""ASIN"", ""AudibleASIN"", ""ISBN10"", ""ISBN13""
                FROM ""Books""").ToList();
        }

        public List<Book> GetBooks(int authorId)
        {
            return Query(s => s.AuthorId == authorId);
        }

        public List<int> GetLibraryBookIds(IEnumerable<int> bookIds = null)
        {
            var requestedBookIds = bookIds?.Distinct().ToArray();
            if (requestedBookIds != null && requestedBookIds.Length == 0)
            {
                return new List<int>();
            }

            var databaseType = _database.DatabaseType;
            var bookIdPredicate = requestedBookIds == null ? null : databaseType == DatabaseType.PostgreSQL ? "= ANY(@BookIds)" : "IN @BookIds";
            var sql = $@"SELECT b.""Id"" FROM ""Books"" b WHERE {BuildLibraryBookClause("b", bookIdPredicate)}";

            if (requestedBookIds == null)
            {
                return _database.Query<int>(sql).ToList();
            }

            if (databaseType == DatabaseType.PostgreSQL)
            {
                return _database.Query<int>(sql, new { BookIds = requestedBookIds }).ToList();
            }

            var matchingBookIds = new List<int>();
            foreach (var batch in requestedBookIds.Chunk(SqliteVariableLimit.MaxParameters))
            {
                matchingBookIds.AddRange(_database.Query<int>(sql, new { BookIds = batch }).ToList());
            }

            return matchingBookIds;
        }

	        public List<Book> GetLastBooks(IEnumerable<int> authorIds)
	        {
	            if (authorIds == null)
	            {
	                return new List<Book>();
	            }

	            var authorIdList = authorIds.Distinct().ToList();
	            if (!authorIdList.Any())
	            {
	                return new List<Book>();
	            }

	            var now = DateTime.UtcNow;
	            if (_database.DatabaseType == DatabaseType.SQLite && authorIdList.Count > SqliteVariableLimit.MaxParameters)
	            {
	                var books = new List<Book>();
	                foreach (var chunkIds in authorIdList.Chunk(SqliteVariableLimit.MaxParameters))
	                {
	                    books.AddRange(GetLastBooks(chunkIds, now));
	                }

	                return books.DistinctBy(b => b.Id).ToList();
	            }

	            return GetLastBooks(authorIdList, now);

	            List<Book> GetLastBooks(IEnumerable<int> ids, DateTime asOfUtc)
	            {
	                var idsArray = ids as int[] ?? ids.ToArray();

	                var ranked = Builder()
	                    .Select("\"Books\".\"Id\" as id")
	                    .Select("ROW_NUMBER() OVER (PARTITION BY \"Books\".\"AuthorId\" ORDER BY \"Books\".\"ReleaseDate\" DESC, \"Books\".\"Id\" ASC) as row_number")
	                    .Where<Book>(x => Enumerable.Contains(idsArray, x.AuthorId) && x.ReleaseDate < asOfUtc)
	                    .AddSelectTemplate(typeof(Book));

	                var outer = Builder()
	                    .Join($"({ranked.RawSql}) ranked on ranked.id = \"Books\".\"Id\" and ranked.row_number = 1")
	                    .AddParameters(ranked.Parameters);

	                return Query(outer);
	            }
	        }

	        public List<Book> GetNextBooks(IEnumerable<int> authorIds)
	        {
	            if (authorIds == null)
	            {
	                return new List<Book>();
	            }

	            var authorIdList = authorIds.Distinct().ToList();
	            if (!authorIdList.Any())
	            {
	                return new List<Book>();
	            }

	            var now = DateTime.UtcNow;
	            if (_database.DatabaseType == DatabaseType.SQLite && authorIdList.Count > SqliteVariableLimit.MaxParameters)
	            {
	                var books = new List<Book>();
	                foreach (var chunkIds in authorIdList.Chunk(SqliteVariableLimit.MaxParameters))
	                {
	                    books.AddRange(GetNextBooks(chunkIds, now));
	                }

	                return books.DistinctBy(b => b.Id).ToList();
	            }

	            return GetNextBooks(authorIdList, now);

	            List<Book> GetNextBooks(IEnumerable<int> ids, DateTime asOfUtc)
	            {
	                var idsArray = ids as int[] ?? ids.ToArray();

	                var ranked = Builder()
	                    .Select("\"Books\".\"Id\" as id")
	                    .Select("ROW_NUMBER() OVER (PARTITION BY \"Books\".\"AuthorId\" ORDER BY \"Books\".\"ReleaseDate\" ASC, \"Books\".\"Id\" ASC) as row_number")
	                    .Where<Book>(x => Enumerable.Contains(idsArray, x.AuthorId) && x.ReleaseDate > asOfUtc)
	                    .AddSelectTemplate(typeof(Book));

	                var outer = Builder()
	                    .Join($"({ranked.RawSql}) ranked on ranked.id = \"Books\".\"Id\" and ranked.row_number = 1")
	                    .AddParameters(ranked.Parameters);

	                return Query(outer);
	            }
	        }

        public Dictionary<string, List<int>> FindIdsByProviderIds(string provider, IEnumerable<string> ids)
        {
            var columns = provider switch
            {
                "hc" => new[] { "HardcoverBookId" },
                "gr" => new[] { "GoodreadsWorkId" },
                "ol" => new[] { "OpenLibraryEditionId", "OpenLibraryWorkId" },
                "gb" => new[] { "GoogleBooksId" },
                "az" => new[] { "ASIN", "AudibleASIN" },
                _ => Array.Empty<string>()
            };
            var values = ids.Distinct().ToArray();
            var result = values.ToDictionary(id => id, _ => new List<int>());
            if (columns.Length == 0 || values.Length == 0)
            {
                return result;
            }

            using var connection = _database.OpenConnection();
            foreach (var chunk in values.Chunk(_database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters : values.Length))
            {
                var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@ids)" : "IN @ids";
                // Separate indexed branches avoid a media-type scan; both formats are needed by the lookup response.
                var sql = string.Join(" UNION ", columns.Select(column =>
                    $"SELECT \"Id\", \"{column}\" AS \"ProviderId\" FROM \"Books\" WHERE \"{column}\" {inClause}"));
                foreach (var match in connection.Query<(int Id, string ProviderId)>(sql, new { ids = chunk }))
                {
                    result[match.ProviderId].Add(match.Id);
                }
            }

            return result;
        }

        public IReadOnlySet<BookMediaType> GetMediaTypesByAuthor(int authorId)
        {
            using var connection = _database.OpenConnection();
            return connection.Query<BookMediaType>(
                "SELECT DISTINCT \"MediaType\" FROM \"Books\" WHERE \"AuthorId\" = @authorId", new { authorId }).ToHashSet();
        }

        public List<Book> GetBooksByAuthorId(int authorId)
        {
            return Query(s => s.AuthorId == authorId);
        }

			        public List<Book> GetBooksForRefresh(int authorId, IEnumerable<string> providerIds)
			        {
			            // Refresh must be author-scoped. Provider-ID lookups are handled downstream by the
			            // remote->local matching logic; including global provider matches here can cause
			            // co-authored books to "ping-pong" between authors during refresh.
			            return Query(a => a.AuthorId == authorId);
			        }

	        public List<Book> GetBooksByFileIds(IEnumerable<int> fileIds)
	        {
	            if (fileIds == null)
	            {
	                return new List<Book>();
	            }

	            var fileIdList = fileIds.Distinct().ToList();
	            if (!fileIdList.Any())
	            {
	                return new List<Book>();
	            }

	            if (_database.DatabaseType == DatabaseType.SQLite && fileIdList.Count > SqliteVariableLimit.MaxParameters)
	            {
	                var books = new List<Book>();
	                foreach (var chunkIds in fileIdList.Chunk(SqliteVariableLimit.MaxParameters))
	                {
	                    var idsArray = chunkIds as int[] ?? chunkIds.ToArray();

		                    books.AddRange(Query(new SqlBuilder(_database.DatabaseType)
		                            .Join<Book, Edition>((b, e) => b.Id == e.BookId)
		                            .Join<Edition, BookFile>((l, r) => l.Id == r.EditionId)
		                            .Where<BookFile>(f => Enumerable.Contains(idsArray, f.Id)))
		                        .DistinctBy(x => x.Id));
		                }

	                return books.DistinctBy(x => x.Id).ToList();
	            }

	            return Query(new SqlBuilder(_database.DatabaseType)
	                         .Join<Book, Edition>((b, e) => b.Id == e.BookId)
	                         .Join<Edition, BookFile>((l, r) => l.Id == r.EditionId)
	                         .Where<BookFile>(f => fileIdList.Contains(f.Id)))
	                .DistinctBy(x => x.Id)
	                .ToList();
	        }

	        public Book FindByProviderIds(string hardcoverBookId = null, string goodreadsBookId = null, string openLibraryWorkId = null)
	        {
	            var query = All().AsQueryable();

            if (!string.IsNullOrEmpty(hardcoverBookId))
            {
                query = query.Where(s => s.HardcoverBookId == hardcoverBookId);
            }

            if (!string.IsNullOrEmpty(goodreadsBookId))
            {
                query = query.Where(s => s.GoodreadsBookId == goodreadsBookId);
            }

            if (!string.IsNullOrEmpty(openLibraryWorkId))
            {
                query = query.Where(s => s.OpenLibraryWorkId == openLibraryWorkId);
            }

	            return query.FirstOrDefault();
	        }

			        public Book FindByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType)
			        {
			            var matches = FindAllByProviderIdAndMediaType(provider, providerId, mediaType);
			            if (matches.Count == 0)
			            {
			                return null;
			            }

			            // Deterministic selection: prefer non-narrator variants, then lowest Id.
			            return matches.FirstOrDefault();
			        }

		        public List<Book> FindAllByProviderIdAndMediaType(string provider, string providerId, BookMediaType mediaType)
		        {
		            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
		            {
		                return new List<Book>();
		            }

		            provider = provider.Trim().ToLowerInvariant();
		            providerId = providerId.Trim();

		            List<Book> matches;
		            switch (provider)
		            {
		                case "hc":
		                    matches = Query(b => b.HardcoverBookId == providerId && b.MediaType == mediaType);
		                    break;
		                case "gr":
		                    matches = Query(b => (b.GoodreadsBookId == providerId || b.GoodreadsWorkId == providerId) && b.MediaType == mediaType);
		                    break;
		                case "ol":
		                    matches = Query(b => (b.OpenLibraryEditionId == providerId || b.OpenLibraryWorkId == providerId) && b.MediaType == mediaType);
		                    break;
		                case "gb":
		                    matches = Query(b => b.GoogleBooksId == providerId && b.MediaType == mediaType);
		                    break;
		                case "az":
                    {
                        var normalizedAz = providerId.Contains(":")
                            ? ProviderIdHelper.Normalize(providerId, "az")
                            : ProviderIdHelper.WithPrefix("az", providerId);

                        var asin = ProviderIdHelper.StripPrefix(normalizedAz);
                        matches = Query(b =>
                            (b.ASIN == asin ||
                             b.AudibleASIN == asin) &&
                            b.MediaType == mediaType);
                    }
                    break;
                case "isbn":
                    {
                        var isbn = ProviderIdHelper.StripPrefix(providerId)?.Replace("-", string.Empty).Replace(" ", string.Empty);
                        matches = Query(b =>
                            (b.ISBN13 == isbn ||
                             b.ISBN10 == isbn) &&
                            b.MediaType == mediaType);
                    }
                    break;
                default:
                    return new List<Book>();
		            }

		            if (matches == null || matches.Count == 0)
		            {
		                return new List<Book>();
		            }

			            return matches
			                .Where(b => b != null)
			                .OrderBy(b => b.Id)
			                .ToList();
			        }

	        public Book FindByIsbn(string isbn)
	        {
	            if (string.IsNullOrWhiteSpace(isbn))
	            {
	                return null;
	            }

	            var matches = Query(b => b.ISBN13 == isbn || b.ISBN10 == isbn);
	            if (matches == null || matches.Count == 0)
	            {
	                return null;
	            }

                return matches
                    .Where(b => b != null)
                    .OrderBy(b => b.Id)
                    .FirstOrDefault();
		        }

	        public Book FindByAsin(string asin)
	        {
	            if (string.IsNullOrWhiteSpace(asin))
	            {
	                return null;
	            }

	            var matches = Query(b => b.ASIN == asin || b.AudibleASIN == asin);
	            if (matches == null || matches.Count == 0)
	            {
	                return null;
	            }

                return matches
                    .Where(b => b != null)
                    .OrderBy(b => b.Id)
                    .FirstOrDefault();
		        }

        private string SlugKey => _database.DatabaseType == DatabaseType.PostgreSQL
            ? "lower(\"Books\".\"TitleSlug\") COLLATE \"C\"" : "lower(\"Books\".\"TitleSlug\")";

        public List<Book> FindBySlug(string titleSlug)
        {
            // The leading index column is the media side; an unscoped link can return both.
            return Query(Builder().Where($"\"Books\".\"MediaType\" IN ({(int)BookMediaType.Audiobook}, {(int)BookMediaType.Ebook}) AND {SlugKey} = @slug",
                new { slug = titleSlug?.ToLowerInvariant() }));
        }

        public List<Book> FindTitleSlugConflicts(IEnumerable<string> slugs, IDbConnection connection = null, IDbTransaction transaction = null)
        {
            var matches = new Dictionary<int, Book>();
            // Exact slug plus its numeric-suffix range; no whole-catalog materialization.
            foreach (var chunk in slugs.Distinct(StringComparer.Ordinal).Chunk(SqliteVariableLimit.MaxParameters / 3))
            {
                var predicates = new List<string>();
                var parameters = new DynamicParameters();
                for (var i = 0; i < chunk.Length; i++)
                {
                    parameters.Add($"slug{i}", chunk[i]);
                    parameters.Add($"start{i}", chunk[i] + "_0");
                    parameters.Add($"end{i}", chunk[i] + "_:");
                    // SQL LENGTH counts characters on both engines, unlike UTF-16 string.Length.
                    var suffix = $"substr({SlugKey}, LENGTH(@slug{i}) + 2)";
                    var numericSuffix = _database.DatabaseType == DatabaseType.PostgreSQL
                        ? $"{suffix} ~ '^[0-9]+$'"
                        : $"{suffix} <> '' AND {suffix} NOT GLOB '*[^0-9]*'";
                    predicates.Add($"(\"MediaType\" IN ({(int)BookMediaType.Audiobook}, {(int)BookMediaType.Ebook}) AND {SlugKey} = @slug{i}) OR (\"MediaType\" IN ({(int)BookMediaType.Audiobook}, {(int)BookMediaType.Ebook}) AND {SlugKey} >= @start{i} AND {SlugKey} < @end{i} AND {numericSuffix})");
                }
                var sql = $"SELECT \"Id\", \"AuthorId\", \"MediaType\", \"TitleSlug\" FROM \"Books\" WHERE {string.Join(" OR ", predicates)}";
                var rows = connection == null ? _database.Query<Book>(sql, parameters) : connection.Query<Book>(sql, parameters, transaction);
                foreach (var book in rows) matches[book.Id] = book;
            }
            return matches.Values.ToList();
        }

        //x.Id == null is converted to SQL, so warning incorrect
#pragma warning disable CS0472
        private SqlBuilder BooksWithoutFilesBuilder(DateTime currentTime) => Builder()
            .Join<Book, Author>((l, r) => l.AuthorId == r.Id)
            .Join<Book, Edition>((b, e) => b.Id == e.BookId)
            .LeftJoin<Edition, BookFile>((t, f) => t.Id == f.EditionId)
            .Where<BookFile>(f => f.Id == null)
            .Where<Edition>(e => e.Monitored == true)
            .Where<Book>(a => a.ReleaseDate <= currentTime);
#pragma warning restore CS0472

        public PagingSpec<Book> BooksWithoutFiles(PagingSpec<Book> pagingSpec)
        {
            var currentTime = DateTime.UtcNow;

            pagingSpec.Records = GetPagedRecords(BooksWithoutFilesBuilder(currentTime), pagingSpec, QueryBooksWithAuthor);
            pagingSpec.TotalRecords = GetPagedRecordCount(BooksWithoutFilesBuilder(currentTime).SelectCountDistinct<Book>(x => x.Id), pagingSpec);

            return pagingSpec;
        }

        public List<BookSearchTarget> GetMissingBookSearchTargets(BookMediaType? mediaType, int? authorId)
        {
            var builder = BooksWithoutFilesBuilder(DateTime.UtcNow)
                .Where<Book>(AuthorExtensions.GetBookMonitoringFilter(mediaType, monitored: true));

            if (authorId.HasValue)
            {
                builder.Where<Book>(book => book.AuthorId == authorId.Value);
            }

            return GetSearchTargets(builder);
        }

        private List<BookSearchTarget> GetSearchTargets(SqlBuilder builder)
        {
            var template = builder.AddTemplate(@"
                SELECT DISTINCT
                    ""Books"".""Id"" AS ""BookId"",
                    ""Books"".""AuthorId"" AS ""AuthorId""
                FROM ""Books""
                /**join**/
                /**leftjoin**/
                /**where**/");

            using (var connection = _database.OpenConnection())
            {
                return connection.Query<BookSearchTarget>(template.RawSql, template.Parameters)
                    .OrderBy(target => target.AuthorId)
                    .ThenBy(target => target.BookId)
                    .ToList();
            }
        }

        private IEnumerable<Book> QueryBooksWithAuthor(SqlBuilder builder)
        {
            var books = _database.QueryJoined<Book, Author>(
                builder,
                (book, author) =>
                {
                    if (author != null)
                    {
                        book.Author = author;
                    }

                    return book;
                }).ToList();

            return books;
        }

        private SqlBuilder BooksWhereCutoffUnmetBuilder(List<QualitiesBelowCutoff> qualitiesBelowCutoff) => Builder()
            .Join<Book, Author>((l, r) => l.AuthorId == r.Id)
            .Join<Book, Edition>((b, e) => b.Id == e.BookId)
            .LeftJoin<Edition, BookFile>((t, f) => t.Id == f.EditionId)
            .Where<Edition>(e => e.Monitored == true)
            .Where(BuildQualityCutoffWhereClause(qualitiesBelowCutoff));

        public List<BookSearchTarget> GetCutoffUnmetSearchTargets(List<QualitiesBelowCutoff> qualitiesBelowCutoff, BookMediaType? mediaType, int? authorId)
        {
            if (qualitiesBelowCutoff == null || qualitiesBelowCutoff.Count == 0)
            {
                return new List<BookSearchTarget>();
            }

            var builder = BooksWhereCutoffUnmetBuilder(qualitiesBelowCutoff)
                .Where<Book>(AuthorExtensions.GetBookMonitoringFilter(mediaType, monitored: true));

            if (authorId.HasValue)
            {
                builder.Where<Book>(book => book.AuthorId == authorId.Value);
            }

            return GetSearchTargets(builder);
        }

        private string BuildQualityCutoffWhereClause(List<QualitiesBelowCutoff> qualitiesBelowCutoff)
        {
            var clauses = new List<string>();

            foreach (var profile in qualitiesBelowCutoff)
            {
                var profileColumn = profile.ProfileType == NzbDrone.Core.Profiles.Qualities.ProfileType.Ebook
                    ? "\"Authors\".\"EbookQualityProfileId\""
                    : "\"Authors\".\"AudiobookQualityProfileId\"";
                var bookMediaType = profile.ProfileType == NzbDrone.Core.Profiles.Qualities.ProfileType.Ebook ? 1 : 0;

                foreach (var belowCutoff in profile.QualityIds)
                {
                    clauses.Add(string.Format("({0} = {1} AND \"Books\".\"MediaType\" = {2} AND \"BookFiles\".\"Quality\" LIKE '%_quality_: {3},%')", profileColumn, profile.ProfileId, bookMediaType, belowCutoff));
                }
            }

            return string.Format("({0})", string.Join(" OR ", clauses));
        }

        public PagingSpec<Book> BooksWhereCutoffUnmet(PagingSpec<Book> pagingSpec, List<QualitiesBelowCutoff> qualitiesBelowCutoff)
        {
            // Each result is one book. Child-table columns are not single-valued
            // after grouping, so use the normal default for those sort requests.
            var sortTable = TableMapping.Mapper.GetSortKey(pagingSpec.SortKey).Table;
            if (sortTable != null && sortTable != "Books" && sortTable != "Authors")
                pagingSpec.SortKey = null;

            pagingSpec.Records = GetPagedRecords(BooksWhereCutoffUnmetBuilder(qualitiesBelowCutoff)
                .GroupBy<Book>(book => book.Id).GroupBy<Author>(author => author.Id), pagingSpec, PagedQuery);
            pagingSpec.TotalRecords = GetPagedRecordCount(BooksWhereCutoffUnmetBuilder(qualitiesBelowCutoff)
                .SelectCountDistinct<Book>(book => book.Id), pagingSpec);

            return pagingSpec;
        }

        public List<Book> BooksBetweenDates(DateTime startDate, DateTime endDate, bool includeUnmonitored)
        {
            var builder = Builder().Where<Book>(rg => rg.ReleaseDate >= startDate && rg.ReleaseDate <= endDate);

            if (!includeUnmonitored)
            {
                builder = builder.Join<Book, Author>((l, r) => l.AuthorId == r.Id)
                    .Where<Book>(AuthorExtensions.GetBookMonitoringFilter(null, monitored: true));
            }

            return Query(builder);
        }

        public List<Book> AuthorBooksBetweenDates(Author author, DateTime startDate, DateTime endDate, bool includeUnmonitored)
        {
            var builder = Builder().Where<Book>(rg => rg.ReleaseDate >= startDate &&
                                                 rg.ReleaseDate <= endDate &&
                                                 rg.AuthorId == author.Id);

            if (!includeUnmonitored)
            {
                builder = builder.Join<Book, Author>((l, r) => l.AuthorId == r.Id)
                    .Where<Book>(AuthorExtensions.GetBookMonitoringFilter(null, monitored: true));
            }

            return Query(builder);
        }

        public void SetMonitored(IEnumerable<int> ids, bool monitored)
        {
            var books = Get(ids).Where(book => book.MediaType is BookMediaType.Audiobook or BookMediaType.Ebook).ToList();
            books.ForEach(book => book.SetMonitored(monitored));
            SetMonitoring(books);
        }

        public void SetMonitoring(IList<Book> books)
        {
            if (books.Count == 0) return;
            if (books.Any(book => book.Id == 0)) throw new InvalidOperationException("Can't update model with ID 0");

            var sql = "UPDATE \"Books\" SET \"AudiobookMonitored\" = @AudiobookMonitored, \"EbookMonitored\" = @EbookMonitored WHERE \"Id\" "
                + (_database.DatabaseType == DatabaseType.PostgreSQL ? "= ANY(@Ids)" : "IN @Ids");
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            foreach (var group in books.GroupBy(book => (book.AudiobookMonitored, book.EbookMonitored)))
            {
                var ids = group.Select(book => book.Id).Distinct().ToArray();
                var batchSize = _database.DatabaseType == DatabaseType.SQLite ? SqliteVariableLimit.MaxParameters - 2 : ids.Length;
                foreach (var batch in ids.Chunk(batchSize))
                {
                    connection.Execute(sql, new { group.Key.AudiobookMonitored, group.Key.EbookMonitored, Ids = batch }, transaction);
                }
            }

            transaction.Commit();
        }

        public List<Book> GetAuthorBooksWithFiles(Author author)
        {
            return Query(Builder()
                         .Join<Book, Edition>((b, e) => b.Id == e.BookId)
                         .Join<Edition, BookFile>((t, f) => t.Id == f.EditionId)
                         .Where<Book>(x => x.AuthorId == author.Id)
                         .Where<Edition>(e => e.Monitored == true));
        }

        public List<Book> GetBooksBySeries(int seriesId)
        {
            return Query(Builder()
                         .Join<Book, SeriesBookLink>((b, sbl) => b.Id == sbl.BookId)
                         .Where<SeriesBookLink>(sbl => sbl.SeriesId == seriesId))
                         .OrderBy(b => BookSeries.PositionOrder(b.SeriesLinks?.FirstOrDefault(link => link.SeriesId == seriesId)?.Position)).ToList();
        }

        public void UpdateMonitoringByAuthorAndMediaType(int authorId, BookMediaType mediaType, bool monitored, IEnumerable<int> exceptBookIds = null)
        {
            // Build the SQL query with proper MediaType filtering and exception handling
            string columnName = mediaType == BookMediaType.Audiobook ? "AudiobookMonitored" : "EbookMonitored";

            var sql = $"UPDATE \"Books\" SET \"{columnName}\" = @monitored WHERE \"AuthorId\" = @authorId AND \"MediaType\" = @mediaType";

            using (var conn = _database.OpenConnection())
            {
                var exceptIds = exceptBookIds?.Distinct().ToList();

                // No exceptions: single UPDATE, no IN/NOT IN lists needed.
                if (exceptIds == null || exceptIds.Count == 0)
                {
                    conn.Execute(sql, new { monitored = monitored, authorId = authorId, mediaType = (int)mediaType });
                    return;
                }

                // PostgreSQL: keep efficient NOT ANY(...) path.
                if (_database.DatabaseType == DatabaseType.PostgreSQL)
                {
                    var pgSql = sql + " AND NOT (\"Id\" = ANY(@ExceptIds))";
                    conn.Execute(pgSql, new { monitored = monitored, authorId = authorId, mediaType = (int)mediaType, ExceptIds = exceptIds.ToArray() });
                    return;
                }

                // SQLite: avoid NOT IN @ExceptIds with large lists (bind-variable limit) by inverting to IN on the complement.
                if (_database.DatabaseType == DatabaseType.SQLite && exceptIds.Count > SqliteVariableLimit.MaxParameters)
                {
                    using (var tran = conn.BeginTransaction())
                    {
                        var allIds = conn.Query<int>(
                            "SELECT \"Id\" FROM \"Books\" WHERE \"AuthorId\" = @authorId AND \"MediaType\" = @mediaType",
                            new { authorId = authorId, mediaType = (int)mediaType },
                            tran).ToList();

                        if (allIds.Count == 0)
                        {
                            tran.Commit();
                            return;
                        }

                        var exceptSet = exceptIds.ToHashSet();
                        var idsToUpdate = allIds.Where(id => !exceptSet.Contains(id)).Distinct().ToList();

                        if (idsToUpdate.Count == 0)
                        {
                            tran.Commit();
                            return;
                        }

                        var updateByIdSql = $"UPDATE \"Books\" SET \"{columnName}\" = @monitored WHERE \"Id\" IN @Ids";
                        foreach (var batch in idsToUpdate.Chunk(SqliteVariableLimit.MaxParameters))
                        {
                            conn.Execute(updateByIdSql, new { monitored = monitored, Ids = batch.ToArray() }, tran);
                        }

                        tran.Commit();
                    }

                    return;
                }

                // SQLite: small exception list is safe to run directly.
                var sqliteSql = sql + " AND \"Id\" NOT IN @ExceptIds";
                conn.Execute(sqliteSql, new { monitored = monitored, authorId = authorId, mediaType = (int)mediaType, ExceptIds = exceptIds.ToArray() });
            }
        }

        public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null)
        {
            var sortKeyNormalized = sortKey?.Trim().ToLowerInvariant();
            var sortDirectionNormalized = sortDirection?.Trim().ToUpperInvariant() == "DESC" ? "DESC" : "ASC";

            // Default to title sorting (which uses CleanTitle) to match client-side behavior.
            if (string.IsNullOrWhiteSpace(sortKeyNormalized))
            {
                sortKeyNormalized = "title";
            }

            var joinClause = string.Empty;
            var sortExpr = string.Empty;
            var orderByExpr = string.Empty;

            // Buckets are based on the primary sort key.
            var bucketSourceExpr = string.Empty;

            switch (sortKeyNormalized)
            {
                case "authortitle":
                    joinClause = "INNER JOIN \"Authors\" a ON a.\"Id\" = b.\"AuthorId\"";
                    sortExpr = "LOWER(COALESCE(a.\"Name\", ''))";
                    bucketSourceExpr = sortExpr;
                    orderByExpr = $"{sortExpr} {sortDirectionNormalized}, LOWER(COALESCE(b.\"CleanTitle\", b.\"Title\", '')) {sortDirectionNormalized}, b.\"Id\" {sortDirectionNormalized}";
                    break;

                case "cleantitle":
                case "title":
                    sortExpr = "LOWER(COALESCE(b.\"CleanTitle\", b.\"Title\", ''))";
                    bucketSourceExpr = sortExpr;
                    orderByExpr = $"{sortExpr} {sortDirectionNormalized}, b.\"Id\" {sortDirectionNormalized}";
                    break;

                default:
                    // Fallback to title buckets when the sort key doesn't have a sensible A-Z jump bar.
                    sortExpr = "LOWER(COALESCE(b.\"CleanTitle\", b.\"Title\", ''))";
                    bucketSourceExpr = sortExpr;
                    orderByExpr = $"{sortExpr} {sortDirectionNormalized}, b.\"Id\" {sortDirectionNormalized}";
                    break;
            }

            var whereConditions = new List<string>();
            var parameters = new DynamicParameters();
            string normalizedMediaTypeForFiles = null;
            var normalizedMediaType = mediaType?.Trim().ToLowerInvariant();

            var missingOrWanted = missing == true || wanted == true;
            var effectiveMonitored = missingOrWanted ? true : monitored;

            if (!effectiveMonitored.HasValue && !includeUnmonitored && library != true)
            {
                var trueIndicator = _database.DatabaseType == DatabaseType.PostgreSQL ? "true" : "1";
                whereConditions.Add($"(b.\"AudiobookMonitored\" = {trueIndicator} OR b.\"EbookMonitored\" = {trueIndicator})");
            }

            if (!string.IsNullOrWhiteSpace(normalizedMediaType))
            {
                if (normalizedMediaType == "audiobook" || normalizedMediaType == "ebook")
                {
                    normalizedMediaTypeForFiles = normalizedMediaType;
                    var mediaTypeValue = normalizedMediaType == "ebook" ? (int)BookMediaType.Ebook : (int)BookMediaType.Audiobook;
                    whereConditions.Add("b.\"MediaType\" = @mediaType");
                    parameters.Add("mediaType", mediaTypeValue);
                }
            }

            if (effectiveMonitored.HasValue)
            {
                var monitoredValue = _database.DatabaseType == DatabaseType.PostgreSQL ? (object)effectiveMonitored.Value : (effectiveMonitored.Value ? 1 : 0);
                parameters.Add("monitored", monitoredValue);

                if (normalizedMediaTypeForFiles == "audiobook")
                {
                    whereConditions.Add("b.\"AudiobookMonitored\" = @monitored");
                }
                else if (normalizedMediaTypeForFiles == "ebook")
                {
                    whereConditions.Add("b.\"EbookMonitored\" = @monitored");
                }
                else
                {
                    whereConditions.Add("((b.\"MediaType\" = @audiobookMediaType AND b.\"AudiobookMonitored\" = @monitored) OR (b.\"MediaType\" = @ebookMediaType AND b.\"EbookMonitored\" = @monitored))");
                    parameters.Add("audiobookMediaType", (int)BookMediaType.Audiobook);
                    parameters.Add("ebookMediaType", (int)BookMediaType.Ebook);
                }
            }

            if (missingOrWanted)
            {
                whereConditions.Add(BuildAuthorMonitoredExistsClause("b", normalizedMediaTypeForFiles));
            }

            if (missingOrWanted || downloaded.HasValue)
            {
                var existsClause = "EXISTS (SELECT 1 FROM \"Editions\" e INNER JOIN \"BookFiles\" bf ON bf.\"EditionId\" = e.\"Id\" WHERE e.\"BookId\" = b.\"Id\")";

                whereConditions.Add(missingOrWanted || downloaded == false ? $"NOT {existsClause}" : existsClause);
            }

            if (wanted == true)
            {
                whereConditions.Add("b.\"ReleaseDate\" <= @wantedCutoff");
                parameters.Add("wantedCutoff", DateTime.UtcNow);
            }

            if (library == true)
            {
                whereConditions.Add(BuildLibraryBookClause("b"));
            }

            var whereClause = whereConditions.Any()
                ? $"WHERE {string.Join(" AND ", whereConditions)}"
                : string.Empty;

            // Use a window function to calculate the true start index of each bucket in the actual sort order.
            // This avoids assuming bucket ordering matches collation and supports descending sorts.
            var sql = $@"
                WITH ordered AS (
                    SELECT
                        b.""Id"" AS Id,
                        CASE
                            WHEN UPPER(SUBSTR({bucketSourceExpr}, 1, 1)) BETWEEN 'A' AND 'Z'
                                THEN UPPER(SUBSTR({bucketSourceExpr}, 1, 1))
                            WHEN SUBSTR({bucketSourceExpr}, 1, 1) BETWEEN '0' AND '9'
                                THEN '0-9'
                            ELSE '#'
                        END AS Bucket,
                        (ROW_NUMBER() OVER (ORDER BY {orderByExpr}) - 1) AS RowNum
                    FROM ""Books"" b
                    {joinClause}
                    {whereClause}
                )
                SELECT
                    Bucket,
                    COUNT(*) AS Count,
                    MIN(RowNum) AS StartIndex
                FROM ordered
                GROUP BY Bucket;";

            using (var conn = _database.OpenConnection())
            {
                var results = conn.Query<BookBucketRow>(sql, parameters);
                var footerStatistics = GetBookFooterStatistics(conn, includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);

                var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var cumulativeIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var totalCount = 0;

                foreach (var row in results)
                {
                    var bucket = row.Bucket;
                    var count = row.Count;
                    var startIndex = row.StartIndex;

                    buckets[bucket] = count;
                    cumulativeIndexes[bucket] = startIndex;
                    totalCount += count;
                }

                return new BookBucketResource
                {
                    Buckets = buckets,
                    TotalCount = totalCount,
                    CumulativeIndexes = cumulativeIndexes,
                    FooterStatistics = footerStatistics
                };
            }
        }

        public BookBucketResource GetBookBuckets(string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null)
        {
            return GetBookBuckets(sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, null, null, null);
        }

        private sealed class BookBucketRow
        {
            public string Bucket { get; set; }
            public int Count { get; set; }
            public int StartIndex { get; set; }
        }

        private BookFooterStatistics GetBookFooterStatistics(IDbConnection conn, bool includeUnmonitored, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null, bool? library = null)
        {
            var builder = CreatePagedBooksBuilder(includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);
            var template = builder.AddTemplate(@"
                WITH filtered_books AS (
                    SELECT
                        ""Id"",
                        ""AuthorId"",
                        CASE
                            WHEN ""MediaType"" = @audiobook THEN CASE WHEN ""AudiobookMonitored"" = @true THEN 1 ELSE 0 END
                            WHEN ""MediaType"" = @ebook THEN CASE WHEN ""EbookMonitored"" = @true THEN 1 ELSE 0 END
                            ELSE 0
                        END AS ""IsMonitored""
                    FROM ""Books""
                    /**where**/
                ),
                file_stats AS (
                    SELECT
                        CAST(COUNT(bf.""Id"") AS INTEGER) AS ""FileCount"",
                        COALESCE(SUM(bf.""Size""), 0) AS ""TotalFileSize""
                    FROM filtered_books fb
                    LEFT JOIN ""Editions"" e ON e.""BookId"" = fb.""Id""
                    LEFT JOIN ""BookFiles"" bf ON bf.""EditionId"" = e.""Id""
                )
                SELECT
                    CAST(COUNT(*) AS INTEGER) AS ""TotalBooks"",
                    CAST(COUNT(DISTINCT fb.""AuthorId"") AS INTEGER) AS ""AuthorCount"",
                    CAST(COALESCE(SUM(fb.""IsMonitored""), 0) AS INTEGER) AS ""MonitoredBooks"",
                    CAST(COALESCE(MAX(fs.""FileCount""), 0) AS INTEGER) AS ""FileCount"",
                    COALESCE(MAX(fs.""TotalFileSize""), 0) AS ""TotalFileSize""
                FROM filtered_books fb
                CROSS JOIN file_stats fs;");

            var parameters = new DynamicParameters(template.Parameters);
            parameters.Add("audiobook", (int)BookMediaType.Audiobook);
            parameters.Add("ebook", (int)BookMediaType.Ebook);
            parameters.Add("true", _database.DatabaseType == DatabaseType.PostgreSQL ? (object)true : 1);

            return conn.QuerySingleOrDefault<BookFooterStatistics>(template.RawSql, parameters) ?? new BookFooterStatistics();
        }

        private SqlBuilder CreatePagedBooksBuilder(bool includeUnmonitored, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null, bool? library = null)
        {
            var builder = Builder();
            string normalizedMediaTypeForFiles = null;
            var normalizedMediaType = mediaType?.Trim().ToLowerInvariant();
            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Book));
            var missingOrWanted = missing == true || wanted == true;
            var effectiveMonitored = missingOrWanted ? true : monitored;

            if (!effectiveMonitored.HasValue && !includeUnmonitored && library != true)
            {
                var trueIndicator = _database.DatabaseType == DatabaseType.PostgreSQL ? "true" : "1";
                builder.Where($"(\"AudiobookMonitored\" = {trueIndicator} OR \"EbookMonitored\" = {trueIndicator})");
            }

            if (!string.IsNullOrWhiteSpace(normalizedMediaType))
            {
                if (normalizedMediaType == "audiobook" || normalizedMediaType == "ebook")
                {
                    normalizedMediaTypeForFiles = normalizedMediaType;
                    var mediaTypeValue = normalizedMediaType == "audiobook" ? 0 : 1;
                    builder.Where("\"MediaType\" = @mediaType", new { mediaType = mediaTypeValue });
                }
            }

            if (effectiveMonitored.HasValue)
            {
                var monitoredValue = _database.DatabaseType == DatabaseType.PostgreSQL ? (object)effectiveMonitored.Value : (effectiveMonitored.Value ? 1 : 0);

                if (normalizedMediaTypeForFiles == "audiobook")
                {
                    builder.Where("\"AudiobookMonitored\" = @monitored", new { monitored = monitoredValue });
                }
                else if (normalizedMediaTypeForFiles == "ebook")
                {
                    builder.Where("\"EbookMonitored\" = @monitored", new { monitored = monitoredValue });
                }
                else
                {
                    builder.Where("((\"MediaType\" = @audiobookMediaType AND \"AudiobookMonitored\" = @monitored) OR (\"MediaType\" = @ebookMediaType AND \"EbookMonitored\" = @monitored))",
                        new
                        {
                            audiobookMediaType = (int)BookMediaType.Audiobook,
                            ebookMediaType = (int)BookMediaType.Ebook,
                            monitored = monitoredValue
                        });
                }
            }

            if (missingOrWanted)
            {
                builder.Where(BuildAuthorMonitoredExistsClause($@"""{tableName}""", normalizedMediaTypeForFiles));
            }

            if (missingOrWanted || downloaded.HasValue)
            {
                var hasFiles = !(missingOrWanted || downloaded == false);
                var existsClause = $@"EXISTS (SELECT 1 FROM ""Editions"" e INNER JOIN ""BookFiles"" bf ON bf.""EditionId"" = e.""Id"" WHERE e.""BookId"" = ""{tableName}"".""Id"")";
                builder.Where(hasFiles ? existsClause : $"NOT {existsClause}");
            }

            if (wanted == true)
            {
                builder.Where($@"""{tableName}"".""ReleaseDate"" <= @wantedCutoff", new { wantedCutoff = DateTime.UtcNow });
            }

            if (library == true)
            {
                builder.Where(BuildLibraryBookClause($@"""{tableName}"""));
            }

            return builder;
        }

        private string BuildLibraryBookClause(string bookReference, string bookIdPredicate = null)
        {
            var trueIndicator = _database.DatabaseType == DatabaseType.PostgreSQL ? "true" : "1";
            var monitoredScope = bookIdPredicate == null ? string.Empty : $@" AND library_book.""Id"" {bookIdPredicate}";
            var ownedScope = bookIdPredicate == null ? string.Empty : $@" WHERE e.""BookId"" {bookIdPredicate}";
            // Keeping the monitoring union inside membership permits a spillable join; scoped eligibility reads only candidate-owned evidence.
            return $@"{bookReference}.""Id"" IN (
                SELECT library_book.""Id"" FROM ""Books"" library_book
                WHERE ((library_book.""MediaType"" = {(int)BookMediaType.Audiobook} AND library_book.""AudiobookMonitored"" = {trueIndicator})
                    OR (library_book.""MediaType"" = {(int)BookMediaType.Ebook} AND library_book.""EbookMonitored"" = {trueIndicator})){monitoredScope}
                UNION
                SELECT e.""BookId"" FROM ""Editions"" e
                INNER JOIN ""BookFiles"" bf ON bf.""EditionId"" = e.""Id""{ownedScope})";
        }

        private string BuildAuthorMonitoredExistsClause(string bookReference, string normalizedMediaType)
        {
            var trueIndicator = _database.DatabaseType == DatabaseType.PostgreSQL ? "true" : "1";
            var authorGate = normalizedMediaType switch
            {
                "audiobook" => $@"monitoring_author.""AudiobookMonitored"" = {trueIndicator}",
                "ebook" => $@"monitoring_author.""EbookMonitored"" = {trueIndicator}",
                _ => $@"(({bookReference}.""MediaType"" = {(int)BookMediaType.Audiobook} AND monitoring_author.""AudiobookMonitored"" = {trueIndicator}) OR ({bookReference}.""MediaType"" = {(int)BookMediaType.Ebook} AND monitoring_author.""EbookMonitored"" = {trueIndicator}))"
            };

            return $@"EXISTS (SELECT 1 FROM ""Authors"" monitoring_author WHERE monitoring_author.""Id"" = {bookReference}.""AuthorId"" AND ({authorGate}))";
        }

        public List<int> GetBookIds(bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null, bool? monitored = null, bool? missing = null, bool? wanted = null, bool? library = null)
        {
            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Book));
            var builder = CreatePagedBooksBuilder(includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);
            var template = builder.AddTemplate($"SELECT \"{tableName}\".\"Id\" FROM \"{tableName}\" /**where**/");

            using (var conn = _database.OpenConnection())
            {
                return conn.Query<int>(template.RawSql, template.Parameters).ToList();
            }
        }

        public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored, string mediaType, bool? downloaded, bool? monitored, bool? missing = null, bool? wanted = null, bool? library = null)
        {
            offset = Math.Max(0, offset);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var allowedSortKeys = new HashSet<string> { "cleantitle", "title", "authortitle", "releasedate", "added", "id", "sizeondisk" };
            if (string.IsNullOrWhiteSpace(sortKey) || !allowedSortKeys.Contains(sortKey.ToLowerInvariant()))
            {
                sortKey = "title";
            }

            sortDirection = sortDirection?.ToUpperInvariant() == "DESC" ? "DESC" : "ASC";

            var tableName = TableMapping.Mapper.TableNameMapping(typeof(Book));
            var columnMap = new Dictionary<string, string>
            {
                ["cleantitle"] = $"LOWER(COALESCE(\"{tableName}\".\"CleanTitle\", \"{tableName}\".\"Title\", ''))",
                ["title"] = $"LOWER(COALESCE(\"{tableName}\".\"CleanTitle\", \"{tableName}\".\"Title\", ''))",
                ["authortitle"] = "LOWER(\"Authors\".\"Name\")",
                ["releasedate"] = $"\"{tableName}\".\"ReleaseDate\"",
                ["added"] = $"\"{tableName}\".\"Added\"",
                ["id"] = $"\"{tableName}\".\"Id\"",
                ["sizeondisk"] = "COALESCE(\"FileStatistics\".\"SizeOnDisk\", 0)"
            };

            var normalizedSortKey = sortKey.ToLowerInvariant();
            var sortColumn = columnMap[normalizedSortKey];
            var countBuilder = CreatePagedBooksBuilder(includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);
            var countTemplate = countBuilder.AddTemplate($"SELECT COUNT(*) FROM \"{tableName}\" /**where**/");

            using (var conn = _database.OpenConnection())
            {
                var totalCount = conn.QuerySingle<int>(countTemplate.RawSql, countTemplate.Parameters);
                var builder = CreatePagedBooksBuilder(includeUnmonitored, mediaType, downloaded, monitored, missing, wanted, library);
                builder.Select(typeof(Book));

                if (normalizedSortKey == "sizeondisk")
                {
                    builder.LeftJoin($@"({BookFileStatisticsSql.GroupedByBook}) AS ""FileStatistics"" ON ""FileStatistics"".""BookId"" = ""{tableName}"".""Id""");
                }

                if (normalizedSortKey == "authortitle")
                {
                    builder.InnerJoin($"\"Authors\" ON \"Authors\".\"Id\" = \"{tableName}\".\"AuthorId\"");
                    builder.OrderBy($"{sortColumn} {sortDirection}, LOWER(COALESCE(\"{tableName}\".\"CleanTitle\", \"{tableName}\".\"Title\", '')) {sortDirection}, \"{tableName}\".\"Id\" {sortDirection}");
                }
                else if (normalizedSortKey == "id")
                {
                    builder.OrderBy($"{sortColumn} {sortDirection}");
                }
                else
                {
                    builder.OrderBy($"{sortColumn} {sortDirection}, \"{tableName}\".\"Id\" {sortDirection}");
                }

                var template = builder.AddTemplate(
                    $"SELECT /**select**/ FROM \"{tableName}\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/ /**orderby**/ LIMIT {pageSize} OFFSET {offset}"
                );

                var books = conn.Query<Book>(template.RawSql, template.Parameters).ToList();

                return new PagedBookResource
                {
                    Records = books,
                    TotalCount = totalCount,
                    Offset = offset,
                    PageSize = pageSize
                };
            }
        }

        public PagedBookResource GetBooksPaged(int offset, int pageSize, string sortKey, string sortDirection, bool includeUnmonitored = false, string mediaType = null, bool? downloaded = null)
        {
            return GetBooksPaged(offset, pageSize, sortKey, sortDirection, includeUnmonitored, mediaType, downloaded, null, null, null);
        }
    }
}
