using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using MediaCoverModel = NzbDrone.Core.MediaCover.MediaCover;

namespace NzbDrone.Core.Books.Services
{
    public class LibrarySearchResult
    {
        public List<LibrarySearchAuthor> Authors { get; set; } = new();
        public List<LibrarySearchBook> Books { get; set; } = new();
    }

    public class LibrarySearchAuthor
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public List<MediaCoverModel> Images { get; set; } = new();
        public string SelectedPosterHash { get; set; }
    }

    public class LibrarySearchBook
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string TitleSlug { get; set; }
        public string MediaType { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public bool Monitored { get; set; }
        public List<MediaCoverModel> Images { get; set; } = new();
        public string ForeignBookId { get; set; }
        public string ForeignAuthorId { get; set; }
        public string HardcoverBookId { get; set; }
        public string GoodreadsBookId { get; set; }
        public string GoodreadsWorkId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string GoogleBooksId { get; set; }
        public string ASIN { get; set; }
        public string AudibleASIN { get; set; }
        public string HardcoverAuthorId { get; set; }
        public string GoodreadsAuthorId { get; set; }
        public string OpenLibraryAuthorId { get; set; }
        public string GoogleBooksAuthorId { get; set; }
        public string AudnexusAuthorId { get; set; }
        public List<LibrarySearchBookInstance> LocalAudiobookBooks { get; set; } = new();
        public List<LibrarySearchBookInstance> LocalEbookBooks { get; set; } = new();
    }

    public class LibrarySearchBookInstance
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string TitleSlug { get; set; }
        public string MediaType { get; set; }
        public string Narrator { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
    }

    internal class LibrarySearchBookRow
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string TitleSlug { get; set; }
        public int MediaTypeInt { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
        public List<MediaCoverModel> Images { get; set; } = new();
        public string BaseBookId { get; set; }
        public string HardcoverBookId { get; set; }
        public string GoodreadsBookId { get; set; }
        public string GoodreadsWorkId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string GoogleBooksId { get; set; }
        public string ASIN { get; set; }
        public string AudibleASIN { get; set; }
        public string HardcoverAuthorId { get; set; }
        public string GoodreadsAuthorId { get; set; }
        public string OpenLibraryAuthorId { get; set; }
        public string GoogleBooksAuthorId { get; set; }
        public string AudnexusAuthorId { get; set; }
        public string Narrator { get; set; }
        public double Score { get; set; }
    }

    public interface ILibrarySearchService
    {
        LibrarySearchResult Search(string term, int limit);
    }

    public class LibrarySearchService : ILibrarySearchService
    {
        private const int MaxTokens = 6;
        private readonly IMainDatabase _database;
        private readonly Logger _logger;

        public LibrarySearchService(IMainDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public LibrarySearchResult Search(string term, int limit)
        {
            var trimmed = (term ?? string.Empty).Trim();
            if (trimmed.Length < 2)
            {
                return new LibrarySearchResult();
            }

            limit = Math.Clamp(limit, 1, 50);

            try
            {
                return _database.DatabaseType switch
                {
                    DatabaseType.SQLite => SearchSqlite(trimmed, limit),
                    DatabaseType.PostgreSQL => SearchPostgres(trimmed, limit),
                    _ => new LibrarySearchResult()
                };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[LIBRARY-SEARCH] Failed searching for '{0}'", trimmed);
                return new LibrarySearchResult();
            }
        }

        private LibrarySearchResult SearchSqlite(string term, int limit)
        {
            using (var conn = _database.OpenConnection())
            {
                var authors = SearchAuthorsSqlite(conn, term, limit);
                var books = SearchBooksSqlite(conn, term, limit);

                return new LibrarySearchResult
                {
                    Authors = authors,
                    Books = books
                };
            }
        }

        private List<LibrarySearchAuthor> SearchAuthorsSqlite(IDbConnection conn, string term, int limit)
        {
            var ftsQuery = BuildFtsPrefixQuery(term);
            if (string.IsNullOrWhiteSpace(ftsQuery))
            {
                return new List<LibrarySearchAuthor>();
            }

            const string sql = @"
SELECT
    a.Id AS Id,
    a.Name AS Name,
    a.Images AS Images,
    a.SelectedPosterHash AS SelectedPosterHash
FROM author_fts
JOIN Authors a ON a.Id = author_fts.rowid
WHERE author_fts MATCH @ftsQuery
ORDER BY bm25(author_fts)
LIMIT @limit";

            return conn.Query<LibrarySearchAuthor>(sql, new { ftsQuery, limit }).ToList();
        }

        private List<LibrarySearchBook> SearchBooksSqlite(IDbConnection conn, string term, int limit)
        {
            var innerQuery = BuildColumnFtsPrefixQuery("MatchingTitle", term);
            if (string.IsNullOrWhiteSpace(innerQuery))
            {
                return new List<LibrarySearchBook>();
            }

            const string sql = @"
WITH matches AS (
    SELECT
        b.Id AS Id,
        b.Title AS Title,
        b.TitleSlug AS TitleSlug,
        b.MediaType AS MediaTypeInt,
        b.AuthorId AS AuthorId,
        a.Name AS AuthorName,
        CASE WHEN b.MediaType = 0 THEN b.AudiobookMonitored ELSE b.EbookMonitored END AS Monitored,
        CASE WHEN EXISTS (
            SELECT 1
            FROM Editions ef
            JOIN BookFiles bf ON bf.EditionId = ef.Id
            WHERE ef.BookId = b.Id
        ) THEN 1 ELSE 0 END AS HasFiles,
        b.Images AS Images,
        b.BaseBookId AS BaseBookId,
        b.HardcoverBookId AS HardcoverBookId,
        b.GoodreadsBookId AS GoodreadsBookId,
        b.GoodreadsWorkId AS GoodreadsWorkId,
        b.OpenLibraryWorkId AS OpenLibraryWorkId,
        b.GoogleBooksId AS GoogleBooksId,
        b.ASIN AS ASIN,
        b.AudibleASIN AS AudibleASIN,
        a.HardcoverAuthorId AS HardcoverAuthorId,
        a.GoodreadsAuthorId AS GoodreadsAuthorId,
        a.OpenLibraryAuthorId AS OpenLibraryAuthorId,
        a.GoogleBooksAuthorId AS GoogleBooksAuthorId,
        a.AudnexusAuthorId AS AudnexusAuthorId,
        b.Narrator AS Narrator,
        bm25(edition_fts) AS Score
    FROM edition_fts
    JOIN Editions e ON e.Id = edition_fts.rowid
    JOIN Books b ON b.Id = e.BookId
    JOIN Authors a ON a.Id = b.AuthorId
    WHERE edition_fts MATCH @ftsQuery
),
ranked AS (
    SELECT
        *,
        ROW_NUMBER() OVER (PARTITION BY Id ORDER BY Score) AS rn
    FROM matches
)
SELECT
    Id,
    Title,
    TitleSlug,
    MediaTypeInt,
    AuthorId,
    AuthorName,
    Monitored,
    HasFiles,
    Images,
    BaseBookId,
    HardcoverBookId,
    GoodreadsBookId,
    GoodreadsWorkId,
    OpenLibraryWorkId,
    GoogleBooksId,
    ASIN,
    AudibleASIN,
    HardcoverAuthorId,
    GoodreadsAuthorId,
    OpenLibraryAuthorId,
    GoogleBooksAuthorId,
    AudnexusAuthorId,
    Narrator,
    Score
FROM ranked
WHERE rn = 1
ORDER BY Score
LIMIT @rowLimit";

            var rows = conn.Query<LibrarySearchBookRow>(sql, new { ftsQuery = innerQuery, rowLimit = limit * 4 }).ToList();
            return BuildGroupedBooks(rows, limit);
        }

        private LibrarySearchResult SearchPostgres(string term, int limit)
        {
            using (var conn = _database.OpenConnection())
            {
                var pattern = $"%{EscapeLikePattern(term)}%";

                const string authorsSql = @"
SELECT
    ""Id"" AS Id,
    ""Name"" AS Name,
    ""Images"" AS Images,
    ""SelectedPosterHash"" AS SelectedPosterHash
FROM ""Authors""
WHERE ""Name"" ILIKE @pattern ESCAPE '\'
   OR ""SortName"" ILIKE @pattern ESCAPE '\'
   OR ""TitleSlug"" ILIKE @pattern ESCAPE '\'
ORDER BY ""Name""
LIMIT @limit";

                const string booksSql = @"
SELECT
    b.""Id"" AS Id,
    b.""Title"" AS Title,
    b.""TitleSlug"" AS TitleSlug,
    b.""MediaType"" AS MediaTypeInt,
    b.""AuthorId"" AS AuthorId,
    a.""Name"" AS AuthorName,
    CASE WHEN b.""MediaType"" = 0 THEN b.""AudiobookMonitored"" ELSE b.""EbookMonitored"" END AS Monitored,
    CASE WHEN EXISTS (
        SELECT 1
        FROM ""Editions"" ef
        JOIN ""BookFiles"" bf ON bf.""EditionId"" = ef.""Id""
        WHERE ef.""BookId"" = b.""Id""
    ) THEN true ELSE false END AS HasFiles,
    b.""Images"" AS Images,
    b.""BaseBookId"" AS BaseBookId,
    b.""HardcoverBookId"" AS HardcoverBookId,
    b.""GoodreadsBookId"" AS GoodreadsBookId,
    b.""GoodreadsWorkId"" AS GoodreadsWorkId,
    b.""OpenLibraryWorkId"" AS OpenLibraryWorkId,
    b.""GoogleBooksId"" AS GoogleBooksId,
    b.""ASIN"" AS ASIN,
    b.""AudibleASIN"" AS AudibleASIN,
    a.""HardcoverAuthorId"" AS HardcoverAuthorId,
    a.""GoodreadsAuthorId"" AS GoodreadsAuthorId,
    a.""OpenLibraryAuthorId"" AS OpenLibraryAuthorId,
    a.""GoogleBooksAuthorId"" AS GoogleBooksAuthorId,
    a.""AudnexusAuthorId"" AS AudnexusAuthorId,
    b.""Narrator"" AS Narrator,
    0 AS Score
FROM ""Books"" b
JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
WHERE b.""Title"" ILIKE @pattern ESCAPE '\'
ORDER BY b.""Title""
LIMIT @rowLimit";

                var bookRows = conn.Query<LibrarySearchBookRow>(booksSql, new { pattern, rowLimit = limit * 4 }).ToList();

                return new LibrarySearchResult
                {
                    Authors = conn.Query<LibrarySearchAuthor>(authorsSql, new { pattern, limit }).ToList(),
                    Books = BuildGroupedBooks(bookRows, limit)
                };
            }
        }

        private static List<LibrarySearchBook> BuildGroupedBooks(List<LibrarySearchBookRow> rows, int limit)
        {
            if (rows == null || rows.Count == 0)
            {
                return new List<LibrarySearchBook>();
            }

            var orderedRows = rows
                .Where(r => r != null)
                .OrderBy(r => r.Score)
                .ThenBy(r => r.Title)
                .ThenBy(r => r.Id)
                .ToList();

            return orderedRows
                .GroupBy(GetBookGroupKey)
                .Select(group =>
                {
                    var groupRows = group
                        .OrderBy(r => r.Score)
                        .ThenBy(r => r.MediaTypeInt)
                        .ThenBy(r => r.Id)
                        .ToList();
                    var primary = groupRows.First();

                    return new LibrarySearchBook
                    {
                        Id = primary.Id,
                        Title = primary.Title,
                        TitleSlug = primary.TitleSlug,
                        MediaType = ToMediaTypeName(primary.MediaTypeInt),
                        AuthorId = primary.AuthorId,
                        AuthorName = primary.AuthorName,
                        Monitored = primary.Monitored,
                        Images = primary.Images ?? new List<MediaCoverModel>(),
                        ForeignBookId = FirstProviderId(
                            ("hc", primary.HardcoverBookId),
                            ("gr", primary.GoodreadsWorkId),
                            ("gr", primary.GoodreadsBookId),
                            ("ol", primary.OpenLibraryWorkId),
                            ("gb", primary.GoogleBooksId),
                            ("az", primary.AudibleASIN),
                            ("az", primary.ASIN)),
                        ForeignAuthorId = FirstProviderId(
                            ("hc", primary.HardcoverAuthorId),
                            ("gr", primary.GoodreadsAuthorId),
                            ("ol", primary.OpenLibraryAuthorId),
                            ("gb", primary.GoogleBooksAuthorId),
                            ("az", primary.AudnexusAuthorId)),
                        HardcoverBookId = primary.HardcoverBookId,
                        GoodreadsBookId = primary.GoodreadsBookId,
                        GoodreadsWorkId = primary.GoodreadsWorkId,
                        OpenLibraryWorkId = primary.OpenLibraryWorkId,
                        GoogleBooksId = primary.GoogleBooksId,
                        ASIN = primary.ASIN,
                        AudibleASIN = primary.AudibleASIN,
                        HardcoverAuthorId = primary.HardcoverAuthorId,
                        GoodreadsAuthorId = primary.GoodreadsAuthorId,
                        OpenLibraryAuthorId = primary.OpenLibraryAuthorId,
                        GoogleBooksAuthorId = primary.GoogleBooksAuthorId,
                        AudnexusAuthorId = primary.AudnexusAuthorId,
                        LocalAudiobookBooks = groupRows
                            .Where(r => r.MediaTypeInt == (int)BookMediaType.Audiobook)
                            .Select(ToLocalInstance)
                            .ToList(),
                        LocalEbookBooks = groupRows
                            .Where(r => r.MediaTypeInt == (int)BookMediaType.Ebook)
                            .Select(ToLocalInstance)
                            .ToList()
                    };
                })
                .Take(limit)
                .ToList();
        }

        private static LibrarySearchBookInstance ToLocalInstance(LibrarySearchBookRow row)
        {
            return new LibrarySearchBookInstance
            {
                Id = row.Id,
                Title = row.Title,
                TitleSlug = row.TitleSlug,
                MediaType = ToMediaTypeName(row.MediaTypeInt),
                Narrator = row.Narrator,
                Monitored = row.Monitored,
                HasFiles = row.HasFiles
            };
        }

        private static string GetBookGroupKey(LibrarySearchBookRow row)
        {
            var stableId = FirstNonEmpty(
                row.BaseBookId,
                row.HardcoverBookId,
                row.GoodreadsWorkId,
                row.OpenLibraryWorkId,
                row.GoogleBooksId,
                row.AudibleASIN,
                row.ASIN);

            if (!string.IsNullOrWhiteSpace(stableId))
            {
                return stableId.Trim().ToLowerInvariant();
            }

            return $"{row.AuthorId}:{(row.Title ?? string.Empty).Trim().ToLowerInvariant()}";
        }

        private static string ToMediaTypeName(int mediaType)
        {
            return mediaType == (int)BookMediaType.Ebook ? "ebook" : "audiobook";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static string FirstProviderId(params (string Provider, string Id)[] values)
        {
            foreach (var (provider, id) in values)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var trimmed = id.Trim();
                return trimmed.Contains(':') ? trimmed : $"{provider}:{trimmed}";
            }

            return null;
        }

        private static string EscapeLikePattern(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
        }

        private static string BuildColumnFtsPrefixQuery(string column, string term)
        {
            var inner = BuildFtsPrefixQuery(term);
            if (string.IsNullOrWhiteSpace(inner))
            {
                return string.Empty;
            }

            return $"{column}:({inner})";
        }

        private static string BuildFtsPrefixQuery(string term)
        {
            var trimmed = (term ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            var tokens = trimmed
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(MaxTokens)
                .ToList();

            if (tokens.Count == 0)
            {
                return string.Empty;
            }

            // Quote tokens to avoid query parsing issues (hyphen/period are tokenchars, but also query operators).
            // Apply prefix search for responsive "typeahead" behavior.
            var escapedTokens = tokens
                .Select(t => t.Replace("\"", "\"\""))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => $"\"{t}\"*")
                .ToList();

            return string.Join(" AND ", escapedTokens);
        }
    }
}
