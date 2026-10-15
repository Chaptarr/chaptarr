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
        public int MatchedTerms { get; set; }
        public long MatchedEditions { get; set; }
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
                var books = SearchBooks(conn, term, limit);

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
            var foldedQuery = BuildFtsPrefixQuery(StringSuperNormalizer.NormalizeForFts(term));
            if (!string.IsNullOrWhiteSpace(foldedQuery) && !string.Equals(ftsQuery, foldedQuery, StringComparison.Ordinal))
            {
                ftsQuery = $"({ftsQuery}) OR ({foldedQuery})";
            }

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

            var stopwatch = Stopwatch.StartNew();
            var authors = conn.Query<LibrarySearchAuthor>(sql, new { ftsQuery, limit }).ToList();
            _logger.Debug("[LIBRARY-SEARCH] {0}: {1} returned authors in {2} ms", _database.DatabaseType, authors.Count, stopwatch.ElapsedMilliseconds);
            return authors;
        }

        private List<LibrarySearchBook> SearchBooks(IDbConnection conn, string term, int limit)
        {
            var terms = UnicodeComparisonNormalizer.WordSpans(StringSuperNormalizer.NormalizeForFts(term), preserveFtsConnectors: true)
                .Select(span => span.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTokens).ToArray();
            if (terms.Length == 0)
            {
                return new List<LibrarySearchBook>();
            }

            var stopwatch = Stopwatch.StartNew();
            var parameters = new DynamicParameters(new { terms, rowLimit = limit * 4 });
            string scoresSql;
            if (_database.DatabaseType == DatabaseType.SQLite)
            {
                var queries = terms.Select(value => $"MatchingTitle:(\"{value.Replace("\"", "\"\"")}\"*)").ToArray();
                var termMatches = queries.Select((query, index) =>
                {
                    parameters.Add($"term{index}", query);
                    return $"SELECT rowid FROM edition_fts WHERE edition_fts MATCH @term{index}";
                });
                parameters.Add("ftsQuery", string.Join(" OR ", queries));
                scoresSql = $@"
WITH term_matches AS ({string.Join(" UNION ALL ", termMatches)}),
term_counts AS (SELECT rowid, COUNT(*) AS MatchedTerms FROM term_matches GROUP BY rowid),
scores AS MATERIALIZED (
    SELECT e.BookId, t.MatchedTerms, bm25(edition_fts) AS Score
    FROM edition_fts
    JOIN Editions e ON e.Id = edition_fts.rowid
    JOIN term_counts t ON t.rowid = edition_fts.rowid
    WHERE edition_fts MATCH @ftsQuery
)";
            }
            else
            {
                // Use the index's parser, including its treatment of compound words and signed numbers.
                scoresSql = @"
WITH terms AS MATERIALIZED (
    SELECT (SELECT string_agg(quote_literal(lexeme) || ':*', ' & ')::tsquery
            FROM unnest(to_tsvector('simple', term))) AS query
    FROM unnest(@terms::text[]) AS term
), query AS (
    SELECT string_agg('(' || query::text || ')', ' | ')::tsquery AS value FROM terms
), matches AS MATERIALIZED (
    SELECT e.""BookId"", to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) AS vector
    FROM ""Editions"" e CROSS JOIN query
    WHERE to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) @@ query.value
), scores AS (
    SELECT ""BookId"", (SELECT COUNT(*) FROM terms WHERE vector @@ terms.query) AS MatchedTerms,
           -ts_rank(vector, query.value) AS Score
    FROM matches CROSS JOIN query
)";
            }

            var sql = scoresSql + @",
ranked AS (
    SELECT *, COUNT(*) OVER () AS MatchedEditions,
           ROW_NUMBER() OVER (PARTITION BY ""BookId"" ORDER BY MatchedTerms DESC, Score) AS rn
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
    r.MatchedTerms,
    r.MatchedEditions
FROM ranked r
JOIN ""Books"" b ON b.""Id"" = r.""BookId""
JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
WHERE r.rn = 1
ORDER BY r.MatchedTerms DESC, r.Score, b.""Title"", b.""Id""
LIMIT @rowLimit";
            var rows = conn.Query<LibrarySearchBookRow>(sql, parameters).ToList();
            var books = BuildGroupedBooks(rows, limit);
            _logger.Debug("[LIBRARY-SEARCH] {0}: {1} matching editions, {2} returned books in {3} ms",
                _database.DatabaseType, rows.FirstOrDefault()?.MatchedEditions ?? 0, books.Count, stopwatch.ElapsedMilliseconds);
            return books;
        }

        private LibrarySearchResult SearchPostgres(string term, int limit)
        {
            using (var conn = _database.OpenConnection())
            {
                var pattern = $"%{EscapeLikePattern(term)}%";
                var folded = StringSuperNormalizer.NormalizeForFts(term);
                var foldedPattern = folded.Length == 0 ? null : $"%{EscapeLikePattern(folded)}%";
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
   OR ""SearchName"" ILIKE @foldedPattern ESCAPE '\'
ORDER BY ""Name""
LIMIT @limit";

                var stopwatch = Stopwatch.StartNew();
                var authors = conn.Query<LibrarySearchAuthor>(authorsSql, new { pattern, foldedPattern, limit }).ToList();
                _logger.Debug("[LIBRARY-SEARCH] {0}: {1} returned authors in {2} ms", _database.DatabaseType, authors.Count, stopwatch.ElapsedMilliseconds);

                return new LibrarySearchResult
                {
                    Authors = authors,
                    Books = SearchBooks(conn, term, limit)
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
                .OrderByDescending(r => r.MatchedTerms)
                .ThenBy(r => r.Score)
                .ThenBy(r => r.Title)
                .ThenBy(r => r.Id)
                .ToList();

            return orderedRows
                .GroupBy(GetBookGroupKey)
                .Select(group =>
                {
                    var groupRows = group
                        .OrderByDescending(r => r.MatchedTerms)
                        .ThenBy(r => r.Score)
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
