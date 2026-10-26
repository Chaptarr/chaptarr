using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Utilities;
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
        public bool ExactMatch { get; set; }
        public string Name { get; set; }
        public List<MediaCoverModel> Images { get; set; } = new();
        public string SelectedPosterHash { get; set; }
    }

    public class LibrarySearchBook
    {
        public int Id { get; set; }
        public bool ExactMatch { get; set; }
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
        public bool ExactMatch { get; set; }
        public long MatchedEditions { get; set; }
    }

    public interface ILibrarySearchService
    {
        LibrarySearchResult Search(string term, int limit);
    }

    public class LibrarySearchService : ILibrarySearchService
    {
        // Hyphen components are already indexed. Numeric components can be signed lexemes (Catch-22).
        private const string PostgresPrefixQuery = @"
WITH terms AS (
    SELECT (SELECT string_agg(CASE WHEN lexeme ~ '^[0-9]+(\.[0-9]+)?$'
                THEN '(' || quote_literal(lexeme) || ':* | ' || quote_literal('-' || lexeme) || ':* | ' || quote_literal('+' || lexeme) || ':*)'
                ELSE quote_literal(lexeme) || ':*' END, ' & ')::tsquery
            FROM unnest(to_tsvector('simple', replace(term, '-', ' ')))) AS query
    FROM unnest(@terms::text[]) AS term
), query AS (
    SELECT string_agg('(' || query::text || ')', ' & ')::tsquery AS value FROM terms
)";
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
                var normalized = StringSuperNormalizer.NormalizeForFts(trimmed);
                var terms = StringSuperNormalizer.GetFtsQueryTerms(trimmed).ToArray();
                if (terms.Length == 0)
                {
                    return new LibrarySearchResult();
                }

                using var conn = _database.OpenConnection();
                return new LibrarySearchResult
                {
                    Authors = SearchAuthors(conn, trimmed, normalized, terms, limit),
                    Books = SearchBooks(conn, normalized, terms, limit)
                };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[LIBRARY-SEARCH] Failed searching for '{0}'", trimmed);
                return new LibrarySearchResult();
            }
        }

        private List<LibrarySearchAuthor> SearchAuthors(IDbConnection conn, string term, string normalized, string[] terms, int limit)
        {
            string sql;
            var parameters = new DynamicParameters(new { terms, normalized, limit });
            if (_database.DatabaseType == DatabaseType.SQLite)
            {
                var ftsQuery = BuildFtsPrefixQuery(UnicodeComparisonNormalizer.WordSpans(term.ToLowerInvariant(), preserveFtsConnectors: true).Select(span => span.Value));
                var foldedQuery = BuildFtsPrefixQuery(terms);
                if (!string.Equals(ftsQuery, foldedQuery, StringComparison.Ordinal))
                {
                    ftsQuery = string.IsNullOrEmpty(ftsQuery) ? foldedQuery : $"({ftsQuery}) OR ({foldedQuery})";
                }

                parameters.Add("ftsQuery", ftsQuery);
                sql = @"
SELECT a.Id AS Id, a.Name AS Name, a.Images AS Images,
       a.SelectedPosterHash AS SelectedPosterHash,
       CASE WHEN a.SearchName = @normalized THEN true ELSE false END AS ExactMatch
FROM author_fts
JOIN Authors a ON a.Id = author_fts.rowid
WHERE author_fts MATCH @ftsQuery
ORDER BY ExactMatch DESC, bm25(author_fts), a.Name, a.Id
LIMIT @limit";
            }
            else
            {
                sql = PostgresPrefixQuery + @"
SELECT ""Id"" AS Id, ""Name"" AS Name, ""Images"" AS Images,
       ""SelectedPosterHash"" AS SelectedPosterHash,
       CASE WHEN ""SearchName"" = @normalized THEN true ELSE false END AS ExactMatch
FROM ""Authors"" CROSS JOIN query
WHERE to_tsvector('simple', COALESCE(""SearchName"", '') || ' ' || COALESCE(""CleanName"", '') || ' ' || COALESCE(""TitleSlug"", '')) @@ query.value
  AND to_tsvector('simple', COALESCE(""SearchName"", '') || ' ' || replace(COALESCE(""SearchName"", ''), '.', '') || ' ' || COALESCE(""TitleSlug"", '')) @@ query.value
ORDER BY ExactMatch DESC, ""Name"", ""Id""
LIMIT @limit";
            }

            var stopwatch = Stopwatch.StartNew();
            var authors = conn.Query<LibrarySearchAuthor>(sql, parameters).ToList();
            _logger.Debug("[LIBRARY-SEARCH] {0}: {1} returned authors in {2} ms", _database.DatabaseType, authors.Count, stopwatch.ElapsedMilliseconds);
            return authors;
        }

        private List<LibrarySearchBook> SearchBooks(IDbConnection conn, string normalized, string[] terms, int limit)
        {
            var stopwatch = Stopwatch.StartNew();
            var parameters = new DynamicParameters(new { terms, normalized, rowLimit = limit * 4 });
            string scoresSql;
            if (_database.DatabaseType == DatabaseType.SQLite)
            {
                parameters.Add("ftsQuery", BuildFtsPrefixQuery(terms.SelectMany(term => term.Split('-', StringSplitOptions.RemoveEmptyEntries))));
                scoresSql = @"
WITH scores AS MATERIALIZED (
    SELECT e.BookId, e.MatchingTitle = @normalized AS ExactMatch, bm25(edition_header_fts) AS Score
    FROM edition_header_fts
    JOIN Editions e ON e.Id = edition_header_fts.rowid
    WHERE edition_header_fts MATCH @ftsQuery
)";
            }
            else
            {
                scoresSql = PostgresPrefixQuery + @",
matches AS MATERIALIZED (
    SELECT e.""BookId"", e.""MatchingTitle"" = @normalized AS ExactMatch,
           to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) AS vector
    FROM ""Editions"" e CROSS JOIN query
    WHERE to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) @@ query.value
), scores AS (
    SELECT ""BookId"", ExactMatch, -ts_rank(vector, query.value) AS Score
    FROM matches CROSS JOIN query
)";
            }

            var sql = scoresSql + @",
ranked AS (
    SELECT *, COUNT(*) OVER () AS MatchedEditions,
           ROW_NUMBER() OVER (PARTITION BY ""BookId"" ORDER BY ExactMatch DESC, Score) AS rn
    FROM scores
)
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
    r.Score,
    r.ExactMatch,
    r.MatchedEditions
FROM ranked r
JOIN ""Books"" b ON b.""Id"" = r.""BookId""
JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
WHERE r.rn = 1
ORDER BY r.ExactMatch DESC, r.Score, b.""Title"", b.""Id""
LIMIT @rowLimit";
            var rows = conn.Query<LibrarySearchBookRow>(sql, parameters).ToList();
            var books = BuildGroupedBooks(rows, limit);
            _logger.Debug("[LIBRARY-SEARCH] {0}: {1} matching editions, {2} returned books in {3} ms",
                _database.DatabaseType, rows.FirstOrDefault()?.MatchedEditions ?? 0, books.Count, stopwatch.ElapsedMilliseconds);
            return books;
        }

        private static List<LibrarySearchBook> BuildGroupedBooks(List<LibrarySearchBookRow> rows, int limit)
        {
            if (rows == null || rows.Count == 0)
            {
                return new List<LibrarySearchBook>();
            }

            var orderedRows = rows
                .Where(r => r != null)
                .OrderByDescending(r => r.ExactMatch)
                .ThenBy(r => r.Score)
                .ThenBy(r => r.Title)
                .ThenBy(r => r.Id)
                .ToList();

            return orderedRows
                .GroupBy(GetBookGroupKey)
                .Select(group =>
                {
                    var groupRows = group
                        .OrderByDescending(r => r.ExactMatch)
                        .ThenBy(r => r.Score)
                        .ThenBy(r => r.MediaTypeInt)
                        .ThenBy(r => r.Id)
                        .ToList();
                    var primary = groupRows.First();

                    return new LibrarySearchBook
                    {
                        Id = primary.Id,
                        ExactMatch = primary.ExactMatch,
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

        private static string BuildFtsPrefixQuery(IEnumerable<string> terms) =>
            string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\"*"));
    }
}
