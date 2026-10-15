using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Books
{
    public interface IEditionFtsRepository
    {
        bool FtsTableExists();
        void RebuildIndex();
        void EnsureIndexPopulated();

        // Separate recall from ranking so author proof gates sibling edition expansion.
        List<BookFtsMatch> RecallBooks(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null,
            int limit = 20,
            bool monitoredOnly = false);

        List<EditionFtsMatch> RankEditions(
            IReadOnlyCollection<BookFtsMatch> recalledBooks,
            IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null);
    }

    public sealed class BookFtsMatch
    {
        public int BookId { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string BookTitle { get; set; }
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }
        public double MatchScore { get; set; }
    }

    /// <summary>
    /// One residual physical tag occurrence after deterministic context cleanup. Key identifies
    /// the occurrence; equal values in independent raw fields deliberately remain independent.
    /// </summary>
    public sealed class EditionFtsFieldQuery
    {
        public string Key { get; set; }
        public string ResidualValue { get; set; }
        public IReadOnlyList<string> Terms { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> SourceFields { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Database recall diagnostics for one candidate Edition and one physical field. These hits
    /// only narrow candidates; the matcher confirms ordered, whole-phrase representation.
    /// </summary>
    public sealed class EditionFtsFieldHit
    {
        public string FieldKey { get; set; }
        public IReadOnlyList<string> SourceFields { get; set; } = Array.Empty<string>();
        public bool TitleHit { get; set; }
        public bool DetailHit { get; set; }
        public double TitleBm25 { get; set; }
        public double DetailBm25 { get; set; }
    }

    public sealed class EditionFtsTraceEvent
    {
        public string EventType { get; set; }
        public string Step { get; set; }
        public IReadOnlyList<string> Terms { get; set; } = Array.Empty<string>();
        public string Columns { get; set; }
        public string Query { get; set; }
        public string FieldKey { get; set; }
        public int? RawRank { get; set; }
        public int? DistinctBookRank { get; set; }
        public int? EditionId { get; set; }
        public int? BookId { get; set; }
        public int? AuthorId { get; set; }
        public double? Score { get; set; }
        public double? BroadRecallScore { get; set; }
        public double? Stage2TitleScore { get; set; }
        public double? Stage2DetailScore { get; set; }
        public string Stage2TitleSourceFields { get; set; }
        public string Stage2DetailSourceFields { get; set; }
        public int? Stage2MatchedFieldCount { get; set; }
        public int? Stage2TitleFieldCount { get; set; }
        public int? Stage2DetailFieldCount { get; set; }
        public string EditionTitle { get; set; }
        public string BookTitle { get; set; }
        public string AuthorName { get; set; }
        public string NarratorNames { get; set; }
        public string Publisher { get; set; }
        public int? DurationSeconds { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? ReadingFormatId { get; set; }
        public long? ElapsedMilliseconds { get; set; }
        public long? TotalElapsedMilliseconds { get; set; }
        public int? ResultCount { get; set; }
        public int? DistinctBookCount { get; set; }
        public string ResultSource { get; set; }
    }

    public class EditionFtsRepository : IEditionFtsRepository
    {
        private const string SqliteTokenizerSql = "unicode61 remove_diacritics 1 tokenchars ''-.''";
        private readonly IMainDatabase _database;
        private readonly Logger _logger;
        private bool? _ftsTableExists;
        private readonly DatabaseType _dbType;

        public EditionFtsRepository(IMainDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
            _dbType = database.DatabaseType;
        }

        public bool FtsTableExists()
        {
            if (_ftsTableExists.HasValue && _ftsTableExists.Value)
            {
                return true;
            }

            // PostgreSQL uses built-in full-text search functions. We don't require a separate FTS table.
            if (_dbType == DatabaseType.PostgreSQL)
            {
                _ftsTableExists = true;
                return true;
            }

            using (var conn = _database.OpenConnection())
            {
                try
                {
                    // SQLite FTS5 check
                    var sql = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='edition_fts')";
                    var count = conn.ExecuteScalar<int>(sql);
                    var exists = count > 0;
                    if (exists)
                    {
                        _ftsTableExists = true;
                        _logger.Debug("[FTS-EXISTS] FTS5 edition search table found and ready");
                        if (HasUnindexedEditions(conn))
                        {
                            _logger.Warn("[EDITION-FTS] Edition search index is empty with stored editions; run the existing UpdateFts maintenance command to restore it");
                        }
                    }
                    else
                    {
                        _ftsTableExists = null;
                        _logger.Warn("[FTS-EXISTS] FTS5 edition search table not found - falling back to standard search");
                    }
                    return exists;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[FTS-EXISTS] Error checking for edition FTS table/column existence");
                    _ftsTableExists = null;
                    return false;
                }
            }
        }

        private static bool HasUnindexedEditions(IDbConnection connection)
        {
            return connection.ExecuteScalar<bool>("SELECT NOT EXISTS(SELECT 1 FROM edition_fts) AND EXISTS(SELECT 1 FROM Editions)");
        }

        public void EnsureIndexPopulated()
        {
            if (_dbType != DatabaseType.SQLite) return;
            try
            {
                using (var conn = _database.OpenConnection())
                {
                    if (conn.ExecuteScalar<bool>("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='edition_fts')") && HasUnindexedEditions(conn))
                    {
                        _logger.Warn("[EDITION-FTS] Restoring empty edition search index during FTS maintenance");
                        RebuildIndex();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[EDITION-FTS] FTS maintenance could not restore the edition search index");
            }
        }

        public void RebuildIndex()
        {
            if (_dbType != DatabaseType.SQLite)
            {
                _logger.Info("Edition FTS rebuild not needed for PostgreSQL (expression indexes update with stored projections)");
                return;
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            RebuildSqliteIndex(connection, transaction, _logger);
            transaction.Commit();
        }

        private static readonly (string Name, string Event, string Delete, string Where, string When)[] SqliteIndexTriggers = new[]
        {
            (Name: "ai", Event: "AFTER INSERT ON Editions", Delete: "", Where: "e.Id = new.Id", When: (string)null),
            (Name: "ad", Event: "AFTER DELETE ON Editions",
                Delete: "DELETE FROM edition_fts WHERE rowid = old.Id;", Where: (string)null, When: (string)null),
            (Name: "au", Event: "AFTER UPDATE OF Id, BookId, MatchingTitle, SearchSubtitle, SearchNarratorNames, SearchNarrator, SearchPublisher ON Editions",
                Delete: "DELETE FROM edition_fts WHERE rowid = old.Id;", Where: "e.Id = new.Id",
                When: "old.Id IS NOT new.Id OR old.BookId IS NOT new.BookId OR COALESCE(old.MatchingTitle, '') IS NOT COALESCE(new.MatchingTitle, '') OR COALESCE(old.SearchSubtitle, '') IS NOT COALESCE(new.SearchSubtitle, '') OR COALESCE(old.SearchNarratorNames, '') IS NOT COALESCE(new.SearchNarratorNames, '') OR COALESCE(old.SearchNarrator, '') IS NOT COALESCE(new.SearchNarrator, '') OR COALESCE(old.SearchPublisher, '') IS NOT COALESCE(new.SearchPublisher, '')"),
            (Name: "book_au", Event: "AFTER UPDATE OF SearchSeriesName, AuthorId ON Books",
                Delete: "DELETE FROM edition_fts WHERE rowid IN (SELECT Id FROM Editions WHERE BookId = old.Id);",
                Where: "e.BookId = new.Id", When: "old.SearchSeriesName IS NOT new.SearchSeriesName OR old.AuthorId IS NOT new.AuthorId"),
            (Name: "author_au", Event: "AFTER UPDATE OF SearchName ON Authors",
                Delete: "DELETE FROM edition_fts WHERE rowid IN (SELECT e.Id FROM Editions e JOIN Books b ON e.BookId = b.Id WHERE b.AuthorId = old.Id);",
                Where: "b.AuthorId = new.Id", When: "old.SearchName IS NOT new.SearchName")
        };

        internal static void DropSqliteIndexTriggers(IDbConnection connection, IDbTransaction transaction)
        {
            foreach (var trigger in SqliteIndexTriggers)
            {
                connection.Execute($"DROP TRIGGER IF EXISTS edition_fts_{trigger.Name};", transaction: transaction);
            }
        }

        internal static void RebuildSqliteIndex(IDbConnection connection, IDbTransaction transaction, Logger logger)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Reuse one projection for rebuilding and every insert/update trigger.
            // Deletes must need only the row ID, even after a parent has disappeared.
            const string insert = @"
                INSERT INTO edition_fts(rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                SELECT e.Id, COALESCE(e.MatchingTitle, ''), COALESCE(b.SearchSeriesName, ''),
                       COALESCE(a.SearchName, ''), COALESCE(e.SearchSubtitle, ''),
                       COALESCE(NULLIF(e.SearchNarratorNames, ''), e.SearchNarrator, ''), COALESCE(e.SearchPublisher, '')
                FROM Editions e JOIN Books b ON e.BookId = b.Id JOIN Authors a ON b.AuthorId = a.Id";

            DropSqliteIndexTriggers(connection, transaction);

            connection.Execute(@"
                DROP TABLE IF EXISTS edition_fts;
                CREATE VIRTUAL TABLE edition_fts USING fts5(
                    MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher,
                    content='', content_rowid='rowid', contentless_delete=1,
                    tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.''');", transaction: transaction);
            connection.Execute(insert, transaction: transaction);

            foreach (var trigger in SqliteIndexTriggers)
            {
                var populate = trigger.Where == null ? string.Empty : $"{insert} WHERE {trigger.Where};";
                var guard = trigger.When == null ? string.Empty : $" WHEN {trigger.When}";
                connection.Execute($"CREATE TRIGGER edition_fts_{trigger.Name} {trigger.Event}{guard} BEGIN {trigger.Delete} {populate} END;", transaction: transaction);
            }

            var indexed = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM edition_fts;", transaction: transaction);
            logger.Info("Rebuilt folded edition search index: {0} entries, one population statement, elapsed {1} ms", indexed, stopwatch.ElapsedMilliseconds);
        }

        private static string BuildPostgresTsQuery(IEnumerable<string> tokens, bool requireAllTerms = false)
        {
            if (tokens == null)
            {
                return string.Empty;
            }

            // Build exact-term queries for broad recall and complete-evidence ordering.
            // Scalar boundaries preserve supplementary letters without admitting tsquery operators.
            var parts = NormalizeFtsTerms(tokens)
                .SelectMany(t => UnicodeComparisonNormalizer.WordSpans(t).Select(m => m.Value))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return parts.Count == 0 ? string.Empty : string.Join(requireAllTerms ? " & " : " | ", parts);
        }

        /// <summary>
        /// Check if a token is valid for FTS query.
        /// Accepts any Unicode letters/digits (Chinese, Japanese, Arabic, Cyrillic, etc.)
        /// Rejects pure punctuation tokens.
        /// </summary>
        private static bool IsValidFtsToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            // Token must contain at least one letter or digit (any Unicode script)
            // This allows Chinese, Japanese, Korean, Arabic, Cyrillic, Greek, Hebrew, etc.
            // Rejects pure punctuation tokens like "..." or "---"
            return UnicodeComparisonNormalizer.WordSpans(token).Any();
        }

        private static IReadOnlyDictionary<string, string> PrepareSqliteQueryTermKeys(
            IDbConnection connection,
            IEnumerable<string> terms,
            Action<EditionFtsTraceEvent> trace = null)
        {
            var inputs = terms.Distinct(StringComparer.Ordinal).ToList();
            if (inputs.Count < 2)
            {
                return inputs.ToDictionary(term => term, term => term, StringComparer.Ordinal);
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            connection.Execute(@"
                DROP TABLE IF EXISTS temp.query_terms_vocab;
                DROP TABLE IF EXISTS temp.query_terms_fts;");
            try
            {
                connection.Execute($@"
                    CREATE VIRTUAL TABLE temp.query_terms_fts USING fts5(
                        Terms, content='', content_rowid='rowid',
                        tokenize = '{SqliteTokenizerSql}');
                    CREATE VIRTUAL TABLE temp.query_terms_vocab USING fts5vocab(
                        'temp', 'query_terms_fts', 'instance');");
                connection.Execute(@"
                    INSERT INTO temp.query_terms_fts(rowid, Terms)
                    SELECT CAST(key AS INTEGER) + 1, value FROM json_each(@terms);",
                    new { terms = System.Text.Json.JsonSerializer.Serialize(inputs) });
                // Native token sequences remove duplicate query weight without changing whole-term syntax.
                var sequences = connection.Query<(int Doc, int Offset, string Term)>(@"
                    SELECT doc, offset, term FROM temp.query_terms_vocab ORDER BY doc, offset;")
                    .GroupBy(token => token.Doc)
                    .ToDictionary(group => group.Key, group => string.Join("\u001f", group.Select(token => token.Term)));
                return inputs.Select((term, index) => new { Term = term, Key = sequences.GetValueOrDefault(index + 1, string.Empty) })
                    .ToDictionary(input => input.Term, input => input.Key, StringComparer.Ordinal);
            }
            finally
            {
                connection.Execute(@"
                    DROP TABLE IF EXISTS temp.query_terms_vocab;
                    DROP TABLE IF EXISTS temp.query_terms_fts;");
                stopwatch.Stop();
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "normalization",
                    Step = "sqlite_query_tokenization",
                    Terms = inputs,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                });
            }
        }

        private static List<string> NormalizeFtsTerms(IEnumerable<string> tokens)
        {
            return tokens?
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .SelectMany(token => StringSuperNormalizer.GetFtsQueryTerms(token))
                .Where(token => !string.IsNullOrWhiteSpace(token) && IsValidFtsToken(token))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();
        }

        /// <summary>
        /// Render literal text without allowing it to become FTS query syntax.
        /// </summary>
        private static string TokenToFtsQueryTerm(string token)
        {
            return $"\"{token.Replace("\"", "\"\"")}\"";
        }

        private static void EmitFtsTrace(Action<EditionFtsTraceEvent> trace, EditionFtsTraceEvent evt)
        {
            if (trace == null || evt == null)
            {
                return;
            }

            try
            {
                trace(evt);
            }
            catch
            {
                // Diagnostics must never change a production matching decision.
            }
        }

        public List<BookFtsMatch> RecallBooks(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null,
            int limit = 20,
            bool monitoredOnly = false)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (!FtsTableExists())
            {
                return new List<BookFtsMatch>();
            }

            var terms = NormalizeFtsTerms(tokens);
            if (terms.Count == 0)
            {
                return new List<BookFtsMatch>();
            }

            var results = _dbType == DatabaseType.PostgreSQL
                ? RecallBooksPostgres(authorId, terms, mediaType, trace, limit, monitoredOnly)
                : RecallBooksSqlite(authorId, terms, mediaType, trace, limit, monitoredOnly);
            _logger.Debug("Edition FTS recall {0}: author {1}, media {2}, terms {3}, books {4}, elapsed {5} ms",
                _dbType, authorId, mediaType, terms.Count, results.Count, stopwatch.ElapsedMilliseconds);
            return results;
        }

        public List<EditionFtsMatch> RankEditions(
            IReadOnlyCollection<BookFtsMatch> recalledBooks,
            IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var recalls = (recalledBooks ?? Array.Empty<BookFtsMatch>())
                .Where(recall => recall != null && recall.BookId > 0)
                .GroupBy(recall => recall.BookId)
                .Select(group => group.OrderByDescending(recall => recall.MatchScore).First())
                .ToList();
            if (recalls.Count == 0)
            {
                return new List<EditionFtsMatch>();
            }

            var queries = (fieldQueries ?? Array.Empty<EditionFtsFieldQuery>())
                .Where(query => query?.Terms != null)
                .Select(query => new EditionFtsFieldQuery
                {
                    Key = query.Key,
                    ResidualValue = query.ResidualValue,
                    Terms = NormalizeFtsTerms(query.Terms),
                    SourceFields = query.SourceFields
                })
                .Where(query => query.Terms.Count > 0)
                .GroupBy(query => query.Key ?? string.Join(" ", query.Terms), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var results = _dbType == DatabaseType.PostgreSQL
                ? RankEditionsPostgres(recalls, queries, mediaType, trace)
                : RankEditionsSqlite(recalls, queries, mediaType, trace);
            _logger.Debug("Edition FTS rank {0}: media {1}, books {2}, fields {3}, editions {4}, elapsed {5} ms",
                _dbType, mediaType, recalls.Count, queries.Count, results.Count, stopwatch.ElapsedMilliseconds);
            return results;
        }

        private List<BookFtsMatch> RecallBooksSqlite(
            int? authorId,
            IReadOnlyList<string> terms,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace,
            int limit,
            bool monitoredOnly)
        {
            using var connection = _database.OpenConnection();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var termKeys = PrepareSqliteQueryTermKeys(connection, terms, trace);
            terms = terms.DistinctBy(term => termKeys[term], StringComparer.Ordinal).ToList();
            var columns = "MatchingTitle SeriesName AuthorName";
            var query = string.Join(" OR ", terms.Select(term => $"{{{columns}}}:{TokenToFtsQueryTerm(term)}"));
            var titleAliasGroups = BuildTitleLookupAliasGroups(terms);
            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "query",
                Step = "stage1_book_recall",
                Terms = terms,
                Columns = columns,
                Query = query
            });

            var monitoredBooks = monitoredOnly ? BuildMonitoredBookIds(mediaType) : null;
            var parameters = monitoredBooks == null
                ? new DynamicParameters()
                : new DynamicParameters(monitoredBooks.Parameters);
            parameters.Add("ftsQuery", query);
            parameters.Add("allTermsQuery", string.Join(" AND ", terms.Select(term => $"{{{columns}}}:{TokenToFtsQueryTerm(term)}")));
            parameters.Add("mediaType", (int)mediaType);
            parameters.Add("limit", limit);
            var ftsMatchSelects = new List<string>
            {
                $@"
                    SELECT edition_fts.rowid, bm25(edition_fts) AS bm25_score
                    FROM edition_fts
                    INNER JOIN Editions selected_editions ON selected_editions.Id = edition_fts.rowid
                    WHERE edition_fts MATCH @ftsQuery
                      {(monitoredOnly ? "AND selected_editions.Monitored = 1" : string.Empty)}"
            };

            AddFtsTraceForAliasGroups(trace, "stage1_book_recall", titleAliasGroups);
            for (var index = 0; index < titleAliasGroups.Count; index++)
            {
                var group = titleAliasGroups[index];
                var parameterName = $"titleAliasQuery{index}";
                var languageFilter = string.Empty;
                parameters.Add(parameterName, BuildSqliteTitleAliasQuery(group.Terms));
                if (group.Language != null)
                {
                    var languageParameter = $"titleAliasLanguage{index}";
                    parameters.Add(languageParameter, NzbDrone.Core.Books.Calibre.Extensions.GetLanguageAliases(group.Language).ToArray());
                    var languageExpression = BuildSqliteCanonicalLanguageExpression("selected_editions.Language");
                    languageFilter = $"AND {languageExpression} IN @{languageParameter}";
                }

                ftsMatchSelects.Add($@"
                    SELECT edition_fts.rowid, bm25(edition_fts) AS bm25_score
                    FROM edition_fts
                    INNER JOIN Editions selected_editions ON selected_editions.Id = edition_fts.rowid
                    WHERE edition_fts MATCH @{parameterName}
                      {languageFilter}
                      {(monitoredOnly ? "AND selected_editions.Monitored = 1" : string.Empty)}");
            }

            if (authorId.HasValue)
            {
                parameters.Add("authorId", authorId.Value);
            }

            var sql = $@"
                WITH fts_matches AS MATERIALIZED (
                    {string.Join(" UNION ALL ", ftsMatchSelects)}
                )
                SELECT
                    b.Id AS BookId,
                    b.AuthorId AS AuthorId,
                    a.Name AS AuthorName,
                    b.Title AS BookTitle,
                    b.SeriesName AS SeriesName,
                    b.SeriesPosition AS SeriesPosition,
                    MAX(0 - fts_matches.bm25_score) AS MatchScore
                FROM fts_matches
                INNER JOIN Editions e ON e.Id = fts_matches.rowid
                INNER JOIN Books b ON b.Id = e.BookId
                INNER JOIN Authors a ON a.Id = b.AuthorId
                {(monitoredBooks == null ? string.Empty : $"INNER JOIN ({monitoredBooks.RawSql}) monitored_books ON monitored_books.\"Id\" = b.Id")}
                WHERE b.MediaType = @mediaType
                  {(authorId.HasValue ? "AND b.AuthorId = @authorId" : string.Empty)}
                GROUP BY b.Id, b.AuthorId, a.Name, b.Title, b.SeriesName, b.SeriesPosition
                ORDER BY MAX(fts_matches.rowid IN (SELECT rowid FROM edition_fts WHERE edition_fts MATCH @allTermsQuery)) DESC,
                         MIN(fts_matches.bm25_score)
                LIMIT @limit";
            var results = connection.Query<BookFtsMatch>(sql, parameters).ToList();
            stopwatch.Stop();
            EmitBookRecallTrace(trace, results, stopwatch.ElapsedMilliseconds);
            return results;
        }

        private List<BookFtsMatch> RecallBooksPostgres(
            int? authorId,
            IReadOnlyList<string> terms,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace,
            int limit,
            bool monitoredOnly)
        {
            var query = BuildPostgresTsQuery(terms);
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<BookFtsMatch>();
            }

            var titleAliasGroups = BuildTitleLookupAliasGroups(terms);
            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "query",
                Step = "stage1_book_recall",
                Terms = terms,
                Columns = "MatchingTitle SeriesName AuthorName",
                Query = query
            });

            using var connection = _database.OpenConnection();
            var monitoredBooks = monitoredOnly ? BuildMonitoredBookIds(mediaType) : null;
            var parameters = monitoredBooks == null
                ? new DynamicParameters()
                : new DynamicParameters(monitoredBooks.Parameters);
            parameters.Add("tsQuery", query);
            parameters.Add("allTermsQuery", BuildPostgresTsQuery(terms, requireAllTerms: true));
            parameters.Add("mediaType", (int)mediaType);
            parameters.Add("limit", limit);
            var titleAliasCandidateSelects = new List<string>();
            var titleAliasScoreExpressions = new List<string>();
            AddFtsTraceForAliasGroups(trace, "stage1_book_recall", titleAliasGroups, postgres: true);
            for (var index = 0; index < titleAliasGroups.Count; index++)
            {
                var group = titleAliasGroups[index];
                var parameterName = $"titleAliasTsQuery{index}";
                var titleVector = "to_tsvector('simple', COALESCE(e.\"MatchingTitle\", ''))";
                var titleQuery = $"to_tsquery('simple', @{parameterName})";
                var languageFilter = string.Empty;
                parameters.Add(parameterName, BuildPostgresPhraseTsQuery(group.Terms));
                if (group.Language != null)
                {
                    var languageParameter = $"titleAliasLanguage{index}";
                    parameters.Add(languageParameter, NzbDrone.Core.Books.Calibre.Extensions.GetLanguageAliases(group.Language).ToArray());
                    var languageExpression = BuildPostgresCanonicalLanguageExpression("e.\"Language\"");
                    languageFilter = $"AND {languageExpression} = ANY(@{languageParameter})";
                }

                titleAliasCandidateSelects.Add($@"
                    SELECT e.""BookId"" AS ""Id""
                    FROM ""Editions"" e
                    WHERE {titleVector} @@ {titleQuery}
                      {languageFilter}
                      {(monitoredOnly ? "AND e.\"Monitored\" = true" : string.Empty)}");
                titleAliasScoreExpressions.Add($@"
                    CASE WHEN {titleVector} @@ {titleQuery} {languageFilter}
                         THEN ts_rank({titleVector}, {titleQuery}) ELSE 0 END");
            }

            if (authorId.HasValue)
            {
                parameters.Add("authorId", authorId.Value);
            }

            // Keep the existing broad query on its indexed fields. Numeric aliases are title-only
            // predicates, so they cannot make author or series text establish a candidate.
            var sql = $@"
                WITH candidates AS (
                    SELECT e.""BookId"" AS ""Id""
                    FROM ""Editions"" e
                    WHERE to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) @@ to_tsquery('simple', @tsQuery)
                      {(monitoredOnly ? "AND e.\"Monitored\" = true" : string.Empty)}
                    UNION
                    SELECT b.""Id""
                    FROM ""Books"" b
                    WHERE to_tsvector('simple', COALESCE(b.""SearchSeriesName"", '')) @@ to_tsquery('simple', @tsQuery)
                    UNION
                    SELECT b.""Id""
                    FROM ""Books"" b
                    INNER JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
                    WHERE to_tsvector('simple', COALESCE(a.""SearchName"", '') || ' ' || COALESCE(a.""CleanName"", '') || ' ' || COALESCE(a.""TitleSlug"", '')) @@ to_tsquery('simple', @tsQuery)
                    {(titleAliasCandidateSelects.Count == 0 ? string.Empty : "UNION\n" + string.Join("\nUNION\n", titleAliasCandidateSelects))}
                )
                SELECT
                    b.""Id"" AS BookId,
                    b.""AuthorId"" AS AuthorId,
                    a.""Name"" AS AuthorName,
                    b.""Title"" AS BookTitle,
                    b.""SeriesName"" AS SeriesName,
                    b.""SeriesPosition"" AS SeriesPosition,
                    MAX(GREATEST(
                        ts_rank(to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')), to_tsquery('simple', @tsQuery)) +
                        ts_rank(to_tsvector('simple', COALESCE(b.""SearchSeriesName"", '')), to_tsquery('simple', @tsQuery)) +
                        ts_rank(to_tsvector('simple', COALESCE(a.""SearchName"", '') || ' ' || COALESCE(a.""CleanName"", '') || ' ' || COALESCE(a.""TitleSlug"", '')), to_tsquery('simple', @tsQuery))
                        {(titleAliasScoreExpressions.Count == 0 ? string.Empty : ", " + string.Join(", ", titleAliasScoreExpressions))}
                    )) AS MatchScore
                FROM ""Editions"" e
                INNER JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                INNER JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
                {(monitoredBooks == null ? string.Empty : $"INNER JOIN ({monitoredBooks.RawSql}) monitored_books ON monitored_books.\"Id\" = b.\"Id\"")}
                WHERE b.""MediaType"" = @mediaType
                  {(monitoredOnly ? "AND e.\"Monitored\" = true" : string.Empty)}
                  AND b.""Id"" IN (SELECT ""Id"" FROM candidates)
                  {(authorId.HasValue ? "AND b.\"AuthorId\" = @authorId" : string.Empty)}
                GROUP BY b.""Id"", b.""AuthorId"", a.""Name"", b.""Title"", b.""SeriesName"", b.""SeriesPosition""
                ORDER BY BOOL_OR((
                    to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) ||
                    to_tsvector('simple', COALESCE(b.""SearchSeriesName"", '')) ||
                    to_tsvector('simple', COALESCE(a.""SearchName"", '') || ' ' || COALESCE(a.""CleanName"", '') || ' ' || COALESCE(a.""TitleSlug"", ''))
                ) @@ to_tsquery('simple', @allTermsQuery)) DESC, MatchScore DESC
                LIMIT @limit";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var results = connection.Query<BookFtsMatch>(sql, parameters).ToList();
            stopwatch.Stop();
            EmitBookRecallTrace(trace, results, stopwatch.ElapsedMilliseconds);
            return results;
        }

        private sealed class TitleLookupAliasGroup
        {
            public string Language { get; set; }
            public IReadOnlyList<string> Terms { get; set; }
        }

        private static List<TitleLookupAliasGroup> BuildTitleLookupAliasGroups(IEnumerable<string> terms)
        {
            var words = (terms ?? Array.Empty<string>())
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(StringSuperNormalizer.NormalizeForFts)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(term => UnicodeComparisonNormalizer.WordSpans(term)
                    .Select(match => match.Value))
                .ToList();
            if (words.Count == 0)
            {
                return new List<TitleLookupAliasGroup>();
            }

            var groups = new List<TitleLookupAliasGroup>();
            var languageIndependent = GetTitleLookupAlternatives(words, "unknown");
            var independentKeys = new HashSet<string>(
                languageIndependent.Select(NormalizeFtsAlias),
                StringComparer.OrdinalIgnoreCase);
            if (languageIndependent.Count > 0)
            {
                groups.Add(new TitleLookupAliasGroup { Language = null, Terms = languageIndependent });
            }

            foreach (var language in TitleNumberCatalog.SupportedLanguages)
            {
                var languageAliases = GetTitleLookupAlternatives(words, language)
                    .Where(alias => !independentKeys.Contains(NormalizeFtsAlias(alias)))
                    .ToList();
                if (languageAliases.Count > 0)
                {
                    groups.Add(new TitleLookupAliasGroup { Language = language, Terms = languageAliases });
                }
            }

            return groups;
        }

        private static List<string> GetTitleLookupAlternatives(IReadOnlyList<string> words, string language)
        {
            var alternatives = new List<string>();
            for (var index = 0; index < words.Count;)
            {
                var numberMatch = TitleNumberCatalog.GetNumberMatches(words, index, language).FirstOrDefault();
                if (numberMatch.TokenCount <= 0)
                {
                    index++;
                    continue;
                }

                var source = string.Join(" ", words.Skip(index).Take(numberMatch.TokenCount));
                var normalizedSource = NormalizeFtsAlias(source);
                foreach (var alternative in TitleNumberCatalog.GetLookupAlternatives(source, language))
                {
                    if (NormalizeFtsAlias(alternative).Equals(normalizedSource, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!alternatives.Contains(alternative, StringComparer.OrdinalIgnoreCase))
                    {
                        alternatives.Add(alternative);
                    }
                }

                index += numberMatch.TokenCount;
            }

            return alternatives;
        }

        private static string NormalizeFtsAlias(string value)
        {
            return StringSuperNormalizer.NormalizeForFts(value ?? string.Empty).Trim();
        }

        private static string BuildSqliteCanonicalLanguageExpression(string languageExpression)
        {
            var normalized = $"LOWER(REPLACE(TRIM(COALESCE({languageExpression}, '')), '_', '-'))";
            return $"CASE WHEN INSTR({normalized}, '-') > 0 THEN TRIM(SUBSTR({normalized}, 1, INSTR({normalized}, '-') - 1)) ELSE TRIM({normalized}) END";
        }

        private static string BuildPostgresCanonicalLanguageExpression(string languageExpression)
        {
            return $"TRIM(SPLIT_PART(LOWER(REPLACE(TRIM(COALESCE({languageExpression}, '')), '_', '-')), '-', 1))";
        }

        private static string BuildSqliteTitleAliasQuery(IEnumerable<string> aliases)
        {
            return string.Join(" OR ", aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => $"{{MatchingTitle}}:{TokenToFtsQueryTerm(alias)}"));
        }

        private static string BuildPostgresPhraseTsQuery(IEnumerable<string> aliases)
        {
            var phrases = (aliases ?? Array.Empty<string>())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => UnicodeComparisonNormalizer.WordSpans(StringSuperNormalizer.NormalizeForFts(alias))
                    .Select(match => match.Value)
                    .ToList())
                .Where(words => words.Count > 0)
                .Select(words => string.Join(" <-> ", words))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return phrases.Count == 0 ? string.Empty : string.Join(" | ", phrases);
        }

        private static void AddFtsTraceForAliasGroups(
            Action<EditionFtsTraceEvent> trace,
            string step,
            IReadOnlyList<TitleLookupAliasGroup> groups,
            bool postgres = false)
        {
            foreach (var group in groups)
            {
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "query",
                    Step = step,
                    Terms = group.Terms,
                    Columns = group.Language == null ? "MatchingTitle numeric aliases" : $"MatchingTitle numeric aliases ({group.Language})",
                    Query = postgres ? BuildPostgresPhraseTsQuery(group.Terms) : BuildSqliteTitleAliasQuery(group.Terms)
                });
            }
        }

        private SqlBuilder.Template BuildMonitoredBookIds(BookMediaType mediaType)
        {
            var builder = new SqlBuilder(_dbType)
                .Join<Book, Author>((book, author) => book.AuthorId == author.Id)
                .Where<Book>(AuthorExtensions.GetBookMonitoringFilter(mediaType, monitored: true));

            return builder.AddTemplate(@"
                SELECT ""Books"".""Id""
                FROM ""Books""
                /**join**/
                /**where**/");
        }

        private List<EditionFtsMatch> RankEditionsSqlite(
            IReadOnlyList<BookFtsMatch> recalls,
            IReadOnlyList<EditionFtsFieldQuery> queries,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace)
        {
            using var connection = _database.OpenConnection();
            var editions = LoadEditionsSqlite(connection, recalls.Select(recall => recall.BookId).ToList(), mediaType);
            ApplyRecallScores(editions, recalls);
            var titleScores = new Dictionary<int, double>();
            var detailScores = new Dictionary<int, double>();
            var titleSourceFields = new Dictionary<int, string>();
            var detailSourceFields = new Dictionary<int, string>();
            var matchedFields = new Dictionary<int, HashSet<string>>();
            var fieldHits = new Dictionary<int, Dictionary<string, EditionFtsFieldHit>>();
            var detailColumn = mediaType == BookMediaType.Ebook ? "Publisher" : "Narrator";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var termKeys = PrepareSqliteQueryTermKeys(connection, queries.SelectMany(query => query.Terms), trace);
            queries = queries.Select(query => new EditionFtsFieldQuery
            {
                Key = query.Key,
                ResidualValue = query.ResidualValue,
                Terms = query.Terms.DistinctBy(term => termKeys[term], StringComparer.Ordinal).ToList(),
                SourceFields = query.SourceFields
            }).ToList();
            PrepareSqliteStage2Indexes(connection, recalls, mediaType);
            try
            {
                AccumulateSqliteFieldScores(
                    connection,
                    queries,
                    "stage2_title_fts",
                    "MatchingTitle",
                    "MatchingTitle",
                    titleScores,
                    titleSourceFields,
                    matchedFields,
                    fieldHits,
                    true,
                    trace);
                AccumulateSqliteFieldScores(
                    connection,
                    queries,
                    "stage2_detail_fts",
                    "Detail",
                    detailColumn,
                    detailScores,
                    detailSourceFields,
                    matchedFields,
                    fieldHits,
                    false,
                    trace);
            }
            finally
            {
                connection.Execute(@"
                    DROP TABLE IF EXISTS temp.stage2_title_fts;
                    DROP TABLE IF EXISTS temp.stage2_detail_fts;");
            }

            ApplyStage2Scores(editions, titleScores, detailScores, titleSourceFields, detailSourceFields, matchedFields, fieldHits);
            stopwatch.Stop();
            var ordered = OrderStage2Results(editions, recalls, mediaType);
            EmitStage2Summary(trace, ordered, queries.Count, stopwatch.ElapsedMilliseconds);
            return ordered;
        }

        private List<EditionFtsMatch> RankEditionsPostgres(
            IReadOnlyList<BookFtsMatch> recalls,
            IReadOnlyList<EditionFtsFieldQuery> queries,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace)
        {
            using var connection = _database.OpenConnection();
            var editions = LoadEditionsPostgres(connection, recalls.Select(recall => recall.BookId).ToList(), mediaType);
            ApplyRecallScores(editions, recalls);
            var titleScores = new Dictionary<int, double>();
            var detailScores = new Dictionary<int, double>();
            var titleSourceFields = new Dictionary<int, string>();
            var detailSourceFields = new Dictionary<int, string>();
            var matchedFields = new Dictionary<int, HashSet<string>>();
            var fieldHits = new Dictionary<int, Dictionary<string, EditionFtsFieldHit>>();
            var detailExpression = mediaType == BookMediaType.Ebook
                ? @"COALESCE(e.""SearchPublisher"", '')"
                : @"COALESCE(NULLIF(e.""SearchNarratorNames"", ''), e.""SearchNarrator"", '')";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            AccumulatePostgresFieldScores(
                connection,
                recalls,
                queries,
                @"COALESCE(e.""MatchingTitle"", '')",
                "MatchingTitle",
                titleScores,
                titleSourceFields,
                matchedFields,
                fieldHits,
                true,
                trace);
            AccumulatePostgresFieldScores(
                connection,
                recalls,
                queries,
                detailExpression,
                mediaType == BookMediaType.Ebook ? "Publisher" : "Narrator",
                detailScores,
                detailSourceFields,
                matchedFields,
                fieldHits,
                false,
                trace);

            ApplyStage2Scores(editions, titleScores, detailScores, titleSourceFields, detailSourceFields, matchedFields, fieldHits);
            stopwatch.Stop();
            var ordered = OrderStage2Results(editions, recalls, mediaType);
            EmitStage2Summary(trace, ordered, queries.Count, stopwatch.ElapsedMilliseconds);
            return ordered;
        }

        private static void EmitBookRecallTrace(
            Action<EditionFtsTraceEvent> trace,
            IReadOnlyList<BookFtsMatch> recalls,
            long elapsedMilliseconds)
        {
            for (var index = 0; index < recalls.Count; index++)
            {
                var recall = recalls[index];
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "candidate",
                    Step = "stage1_book_recall",
                    RawRank = index + 1,
                    DistinctBookRank = index + 1,
                    BookId = recall.BookId,
                    AuthorId = recall.AuthorId,
                    AuthorName = recall.AuthorName,
                    BookTitle = recall.BookTitle,
                    Score = recall.MatchScore,
                    BroadRecallScore = recall.MatchScore
                });
            }

            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "summary",
                Step = "stage1_book_recall",
                ElapsedMilliseconds = elapsedMilliseconds,
                ResultCount = recalls.Count,
                DistinctBookCount = recalls.Select(recall => recall.BookId).Distinct().Count()
            });
        }

        private static List<EditionFtsMatch> LoadEditionsSqlite(
            IDbConnection connection,
            IReadOnlyList<int> bookIds,
            BookMediaType mediaType)
        {
            var parameters = new DynamicParameters();
            parameters.Add("mediaType", (int)mediaType);
            var placeholders = new List<string>();
            for (var index = 0; index < bookIds.Count; index++)
            {
                var name = $"bookId{index}";
                placeholders.Add($"@{name}");
                parameters.Add(name, bookIds[index]);
            }

            var sql = $@"
                SELECT
                    e.Id AS EditionId,
                    e.ForeignEditionId AS ForeignEditionId,
                    e.BookId AS BookId,
                    COALESCE(NULLIF(LOWER(TRIM(e.Language)), ''), 'unknown') AS Lang,
                    e.Title AS EditionTitle,
                    e.MatchingTitle AS MatchingTitle,
                    e.Subtitle AS EditionSubTitle,
                    b.Title AS BookTitle,
                    b.AuthorId AS AuthorId,
                    a.Name AS AuthorName,
                    CASE
                        WHEN COALESCE(e.SearchNarratorNames, '') <> ''
                            THEN COALESCE((SELECT GROUP_CONCAT(value, '; ') FROM (SELECT value FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != '' ORDER BY CAST(key AS INTEGER))), COALESCE(e.Narrator, ''))
                        ELSE COALESCE(e.Narrator, '')
                    END AS NarratorNames,
                    e.Publisher AS Publisher,
                    e.Images AS CoverUrl,
                    e.DurationSeconds AS DurationSeconds,
                    e.ReleaseDate AS ReleaseDate,
                    e.ReadingFormatId AS ReadingFormatId
                FROM Editions e
                INNER JOIN Books b ON b.Id = e.BookId
                INNER JOIN Authors a ON a.Id = b.AuthorId
                WHERE b.MediaType = @mediaType
                  AND b.Id IN ({string.Join(",", placeholders)})";
            return connection.Query<EditionFtsMatch>(sql, parameters).ToList();
        }

        private static List<EditionFtsMatch> LoadEditionsPostgres(
            IDbConnection connection,
            IReadOnlyList<int> bookIds,
            BookMediaType mediaType)
        {
            const string sql = @"
                SELECT
                    e.""Id"" AS EditionId,
                    e.""ForeignEditionId"" AS ForeignEditionId,
                    e.""BookId"" AS BookId,
                    COALESCE(NULLIF(LOWER(TRIM(e.""Language"")), ''), 'unknown') AS Lang,
                    e.""Title"" AS EditionTitle,
                    e.""MatchingTitle"" AS MatchingTitle,
                    e.""Subtitle"" AS EditionSubTitle,
                    b.""Title"" AS BookTitle,
                    b.""AuthorId"" AS AuthorId,
                    a.""Name"" AS AuthorName,
                    CASE
                        WHEN COALESCE(e.""SearchNarratorNames"", '') <> ''
                            THEN COALESCE((SELECT string_agg(names.value, '; ' ORDER BY names.position)
                                FROM jsonb_array_elements_text(e.""NarratorNames""::jsonb) WITH ORDINALITY AS names(value, position)
                                WHERE names.value IS NOT NULL AND names.value <> ''), COALESCE(e.""Narrator"", ''))
                        ELSE COALESCE(e.""Narrator"", '')
                    END AS NarratorNames,
                    e.""Publisher"" AS Publisher,
                    e.""Images"" AS CoverUrl,
                    e.""DurationSeconds"" AS DurationSeconds,
                    e.""ReleaseDate"" AS ReleaseDate,
                    e.""ReadingFormatId"" AS ReadingFormatId
                FROM ""Editions"" e
                INNER JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                INNER JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
                WHERE b.""MediaType"" = @mediaType
                  AND b.""Id"" = ANY(@bookIds)";
            return connection.Query<EditionFtsMatch>(sql, new
            {
                mediaType = (int)mediaType,
                bookIds = bookIds.ToArray()
            }).ToList();
        }

        private static void ApplyRecallScores(
            IEnumerable<EditionFtsMatch> editions,
            IReadOnlyList<BookFtsMatch> recalls)
        {
            var scores = recalls.ToDictionary(recall => recall.BookId, recall => recall.MatchScore);
            foreach (var edition in editions)
            {
                if (scores.TryGetValue(edition.BookId, out var score))
                {
                    edition.BroadRecallScore = score;
                    edition.MatchScore = score;
                }
            }
        }

        private static void PrepareSqliteStage2Indexes(
            IDbConnection connection,
            IReadOnlyList<BookFtsMatch> recalls,
            BookMediaType mediaType)
        {
            var parameters = new DynamicParameters();
            var placeholders = new List<string>();
            for (var index = 0; index < recalls.Count; index++)
            {
                var name = $"stage2BookId{index}";
                placeholders.Add($"@{name}");
                parameters.Add(name, recalls[index].BookId);
            }

            connection.Execute(@"
                DROP TABLE IF EXISTS temp.stage2_title_fts;
                DROP TABLE IF EXISTS temp.stage2_detail_fts;");
            connection.Execute($@"
                CREATE VIRTUAL TABLE temp.stage2_title_fts USING fts5(
                    MatchingTitle,
                    content='',
                    content_rowid='rowid',
                    tokenize = '{SqliteTokenizerSql}'
                );
                CREATE VIRTUAL TABLE temp.stage2_detail_fts USING fts5(
                    Detail,
                    content='',
                    content_rowid='rowid',
                    tokenize = '{SqliteTokenizerSql}'
                );");
            connection.Execute($@"
                INSERT INTO stage2_title_fts(rowid, MatchingTitle)
                SELECT
                    e.Id,
                    COALESCE(e.MatchingTitle, '')
                FROM Editions e
                WHERE e.BookId IN ({string.Join(",", placeholders)})",
                parameters);
            var detailExpression = mediaType == BookMediaType.Ebook
                ? "COALESCE(e.SearchPublisher, '')"
                : "COALESCE(NULLIF(e.SearchNarratorNames, ''), e.SearchNarrator, '')";
            connection.Execute($@"
                INSERT INTO stage2_detail_fts(rowid, Detail)
                SELECT
                    e.Id,
                    {detailExpression}
                FROM Editions e
                WHERE e.BookId IN ({string.Join(",", placeholders)})",
                parameters);
        }

        private static void AccumulateSqliteFieldScores(
            IDbConnection connection,
            IReadOnlyList<EditionFtsFieldQuery> fieldQueries,
            string ftsTable,
            string ftsColumn,
            string traceColumn,
            IDictionary<int, double> scores,
            IDictionary<int, string> winningSourceFields,
            IDictionary<int, HashSet<string>> matchedFields,
            IDictionary<int, Dictionary<string, EditionFtsFieldHit>> fieldHits,
            bool titleColumn,
            Action<EditionFtsTraceEvent> trace)
        {
            var preparedQueries = fieldQueries
                .Select(fieldQuery => new
                {
                    Query = fieldQuery,
                    Terms = fieldQuery.Terms
                        .Where(term => !string.IsNullOrWhiteSpace(term) && IsValidFtsToken(term))
                        .ToList()
                })
                .Where(item => item.Terms.Count > 0)
                .ToList();
            if (preparedQueries.Count == 0)
            {
                return;
            }

            var parameters = new DynamicParameters();
            var scoreSelects = new List<string>();
            var queriesByKey = new Dictionary<string, EditionFtsFieldQuery>(StringComparer.OrdinalIgnoreCase);
            for (var queryIndex = 0; queryIndex < preparedQueries.Count; queryIndex++)
            {
                var prepared = preparedQueries[queryIndex];
                var fieldKey = prepared.Query.Key ?? string.Join(" ", prepared.Terms);
                var aliasGroups = titleColumn ? BuildTitleLookupAliasGroups(prepared.Terms) : new List<TitleLookupAliasGroup>();
                var independentGroup = aliasGroups.FirstOrDefault(group => group.Language == null);
                var ftsQuery = string.Join(" OR ", prepared.Terms.Select(term => $"{{{ftsColumn}}}:{TokenToFtsQueryTerm(term)}"));
                if (independentGroup != null)
                {
                    ftsQuery += " OR " + BuildSqliteTitleAliasQuery(independentGroup.Terms);
                }

                parameters.Add($"fieldKey{queryIndex}", fieldKey);
                parameters.Add($"ftsQuery{queryIndex}", ftsQuery);
                queriesByKey[fieldKey] = prepared.Query;
                scoreSelects.Add($@"
                    SELECT
                        rowid AS EditionId,
                        @fieldKey{queryIndex} AS FieldKey,
                        0 - bm25({ftsTable}) AS Score
                    FROM {ftsTable}
                    WHERE {ftsTable} MATCH @ftsQuery{queryIndex}");
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "query",
                    Step = "stage2_field_ranking",
                    FieldKey = string.Join(", ", prepared.Query.SourceFields ?? Array.Empty<string>()),
                    Terms = prepared.Terms.Concat(independentGroup?.Terms ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Columns = traceColumn,
                    Query = ftsQuery
                });

                if (!titleColumn)
                {
                    continue;
                }

                for (var aliasIndex = 0; aliasIndex < aliasGroups.Count; aliasIndex++)
                {
                    var group = aliasGroups[aliasIndex];
                    if (group.Language == null)
                    {
                        continue;
                    }

                    var queryParameter = $"titleAliasQuery{queryIndex}_{aliasIndex}";
                    var languageParameter = $"titleAliasLanguage{queryIndex}_{aliasIndex}";
                    var aliasQuery = BuildSqliteTitleAliasQuery(group.Terms);
                    parameters.Add(queryParameter, aliasQuery);
                    parameters.Add(languageParameter, NzbDrone.Core.Books.Calibre.Extensions.GetLanguageAliases(group.Language).ToArray());
                    var languageExpression = BuildSqliteCanonicalLanguageExpression("alias_editions.Language");
                    scoreSelects.Add($@"
                        SELECT
                            {ftsTable}.rowid AS EditionId,
                            @fieldKey{queryIndex} AS FieldKey,
                            0 - bm25({ftsTable}) AS Score
                        FROM {ftsTable}
                        INNER JOIN Editions alias_editions ON alias_editions.Id = {ftsTable}.rowid
                        WHERE {ftsTable} MATCH @{queryParameter}
                          AND {languageExpression} IN @{languageParameter}");
                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "query",
                        Step = "stage2_field_ranking",
                        FieldKey = string.Join(", ", prepared.Query.SourceFields ?? Array.Empty<string>()),
                        Terms = group.Terms,
                        Columns = traceColumn,
                        Query = aliasQuery
                    });
                }
            }

            var sql = $@"
                WITH field_scores AS MATERIALIZED (
                    {string.Join(" UNION ALL ", scoreSelects)}
                )
                SELECT field_scores.EditionId, field_scores.FieldKey, field_scores.Score
                FROM field_scores";
            foreach (var row in connection.Query<Stage2ScoreRow>(sql, parameters))
            {
                if (string.IsNullOrWhiteSpace(row.FieldKey) || !queriesByKey.TryGetValue(row.FieldKey, out var fieldQuery))
                {
                    continue;
                }

                if (!scores.TryGetValue(row.EditionId, out var existing) || row.Score > existing)
                {
                    scores[row.EditionId] = row.Score;
                    winningSourceFields[row.EditionId] = string.Join(", ", fieldQuery.SourceFields ?? Array.Empty<string>());
                }
                if (!matchedFields.TryGetValue(row.EditionId, out var fields))
                {
                    fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    matchedFields[row.EditionId] = fields;
                }

                fields.Add(fieldQuery.Key);
                RecordStage2FieldHit(fieldHits, row.EditionId, fieldQuery, titleColumn, row.Score);
            }
        }

        private static void AccumulatePostgresFieldScores(
            IDbConnection connection,
            IReadOnlyList<BookFtsMatch> recalls,
            IReadOnlyList<EditionFtsFieldQuery> fieldQueries,
            string valueExpression,
            string column,
            IDictionary<int, double> scores,
            IDictionary<int, string> winningSourceFields,
            IDictionary<int, HashSet<string>> matchedFields,
            IDictionary<int, Dictionary<string, EditionFtsFieldHit>> fieldHits,
            bool titleColumn,
            Action<EditionFtsTraceEvent> trace)
        {
            var parameters = new DynamicParameters();
            parameters.Add("bookIds", recalls.Select(recall => recall.BookId).ToArray());
            var scoreSelects = new List<string>();
            var queriesByKey = new Dictionary<string, EditionFtsFieldQuery>(StringComparer.OrdinalIgnoreCase);
            for (var queryIndex = 0; queryIndex < fieldQueries.Count; queryIndex++)
            {
                var fieldQuery = fieldQueries[queryIndex];
                var query = BuildPostgresTsQuery(fieldQuery.Terms);
                if (string.IsNullOrWhiteSpace(query))
                {
                    continue;
                }

                var fieldKey = fieldQuery.Key ?? string.Join(" ", fieldQuery.Terms);
                var aliasGroups = titleColumn ? BuildTitleLookupAliasGroups(fieldQuery.Terms) : new List<TitleLookupAliasGroup>();
                var independentGroup = aliasGroups.FirstOrDefault(group => group.Language == null);
                if (independentGroup != null)
                {
                    var aliases = BuildPostgresPhraseTsQuery(independentGroup.Terms);
                    if (!string.IsNullOrWhiteSpace(aliases))
                    {
                        query = $"({query}) | ({aliases})";
                    }
                }

                parameters.Add($"fieldKey{queryIndex}", fieldKey);
                parameters.Add($"tsQuery{queryIndex}", query);
                queriesByKey[fieldKey] = fieldQuery;
                scoreSelects.Add($@"
                    SELECT
                        e.""Id"" AS EditionId,
                        @fieldKey{queryIndex} AS FieldKey,
                        ts_rank(to_tsvector('simple', {valueExpression}), to_tsquery('simple', @tsQuery{queryIndex})) AS Score
                    FROM ""Editions"" e
                    WHERE e.""BookId"" = ANY(@bookIds)
                      AND to_tsvector('simple', {valueExpression}) @@ to_tsquery('simple', @tsQuery{queryIndex})");
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "query",
                    Step = "stage2_field_ranking",
                    FieldKey = string.Join(", ", fieldQuery.SourceFields ?? Array.Empty<string>()),
                    Terms = fieldQuery.Terms.Concat(independentGroup?.Terms ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Columns = column,
                    Query = query
                });

                if (!titleColumn)
                {
                    continue;
                }

                for (var aliasIndex = 0; aliasIndex < aliasGroups.Count; aliasIndex++)
                {
                    var group = aliasGroups[aliasIndex];
                    if (group.Language == null)
                    {
                        continue;
                    }

                    var queryParameter = $"titleAliasTsQuery{queryIndex}_{aliasIndex}";
                    var languageParameter = $"titleAliasLanguage{queryIndex}_{aliasIndex}";
                    var aliasQuery = BuildPostgresPhraseTsQuery(group.Terms);
                    parameters.Add(queryParameter, aliasQuery);
                    parameters.Add(languageParameter, NzbDrone.Core.Books.Calibre.Extensions.GetLanguageAliases(group.Language).ToArray());
                    var languageExpression = BuildPostgresCanonicalLanguageExpression("e.\"Language\"");
                    scoreSelects.Add($@"
                        SELECT
                            e.""Id"" AS EditionId,
                            @fieldKey{queryIndex} AS FieldKey,
                            ts_rank(to_tsvector('simple', {valueExpression}), to_tsquery('simple', @{queryParameter})) AS Score
                        FROM ""Editions"" e
                        WHERE e.""BookId"" = ANY(@bookIds)
                          AND {languageExpression} = ANY(@{languageParameter})
                          AND to_tsvector('simple', {valueExpression}) @@ to_tsquery('simple', @{queryParameter})");
                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "query",
                        Step = "stage2_field_ranking",
                        FieldKey = string.Join(", ", fieldQuery.SourceFields ?? Array.Empty<string>()),
                        Terms = group.Terms,
                        Columns = column,
                        Query = aliasQuery
                    });
                }
            }

            if (scoreSelects.Count == 0)
            {
                return;
            }

            var sql = string.Join(" UNION ALL ", scoreSelects);
            foreach (var row in connection.Query<Stage2ScoreRow>(sql, parameters))
            {
                if (string.IsNullOrWhiteSpace(row.FieldKey) || !queriesByKey.TryGetValue(row.FieldKey, out var fieldQuery))
                {
                    continue;
                }

                if (!scores.TryGetValue(row.EditionId, out var existing) || row.Score > existing)
                {
                    scores[row.EditionId] = row.Score;
                    winningSourceFields[row.EditionId] = string.Join(", ", fieldQuery.SourceFields ?? Array.Empty<string>());
                }
                if (!matchedFields.TryGetValue(row.EditionId, out var fields))
                {
                    fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    matchedFields[row.EditionId] = fields;
                }

                fields.Add(fieldQuery.Key);
                RecordStage2FieldHit(fieldHits, row.EditionId, fieldQuery, titleColumn, row.Score);
            }
        }

        private static void RecordStage2FieldHit(
            IDictionary<int, Dictionary<string, EditionFtsFieldHit>> fieldHits,
            int editionId,
            EditionFtsFieldQuery fieldQuery,
            bool titleColumn,
            double score)
        {
            if (!fieldHits.TryGetValue(editionId, out var hitsByField))
            {
                hitsByField = new Dictionary<string, EditionFtsFieldHit>(StringComparer.OrdinalIgnoreCase);
                fieldHits[editionId] = hitsByField;
            }

            var fieldKey = fieldQuery.Key ?? string.Join(" ", fieldQuery.Terms ?? Array.Empty<string>());
            if (!hitsByField.TryGetValue(fieldKey, out var hit))
            {
                hit = new EditionFtsFieldHit
                {
                    FieldKey = fieldKey,
                    SourceFields = fieldQuery.SourceFields?.ToList() ?? new List<string>()
                };
                hitsByField[fieldKey] = hit;
            }

            if (titleColumn)
            {
                hit.TitleHit = true;
                hit.TitleBm25 = Math.Max(hit.TitleBm25, score);
            }
            else
            {
                hit.DetailHit = true;
                hit.DetailBm25 = Math.Max(hit.DetailBm25, score);
            }
        }

        private static void ApplyStage2Scores(
            IEnumerable<EditionFtsMatch> editions,
            IReadOnlyDictionary<int, double> titleScores,
            IReadOnlyDictionary<int, double> detailScores,
            IReadOnlyDictionary<int, string> titleSourceFields,
            IReadOnlyDictionary<int, string> detailSourceFields,
            IReadOnlyDictionary<int, HashSet<string>> matchedFields,
            IReadOnlyDictionary<int, Dictionary<string, EditionFtsFieldHit>> fieldHits)
        {
            foreach (var edition in editions)
            {
                edition.Stage2TitleScore = titleScores.TryGetValue(edition.EditionId, out var titleScore) ? titleScore : 0.0;
                edition.Stage2DetailScore = detailScores.TryGetValue(edition.EditionId, out var detailScore) ? detailScore : 0.0;
                edition.Stage2TitleSourceFields = titleSourceFields.TryGetValue(edition.EditionId, out var titleFields) ? titleFields : string.Empty;
                edition.Stage2DetailSourceFields = detailSourceFields.TryGetValue(edition.EditionId, out var detailFields) ? detailFields : string.Empty;
                edition.Stage2MatchedFieldCount = matchedFields.TryGetValue(edition.EditionId, out var fields) ? fields.Count : 0;
                edition.Stage2FieldHits = fieldHits.TryGetValue(edition.EditionId, out var hits)
                    ? hits.Values.OrderBy(hit => hit.FieldKey, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<EditionFtsFieldHit>();

                // Preserve each edition's title score for the final edition comparison. Broad
                // recall locates Books; it cannot distinguish their sibling Editions. Keep detail
                // evidence separate so one field cannot earn both title and narrator/publisher credit.
                edition.MatchScore = edition.Stage2TitleScore;
            }
        }

        private static List<EditionFtsMatch> OrderStage2Results(
            IEnumerable<EditionFtsMatch> editions,
            IReadOnlyList<BookFtsMatch> recalls,
            BookMediaType mediaType)
        {
            var recallRank = recalls
                .Select((recall, index) => new { recall.BookId, Rank = index })
                .ToDictionary(item => item.BookId, item => item.Rank);
            return editions
                .OrderBy(edition => recallRank.TryGetValue(edition.BookId, out var rank) ? rank : int.MaxValue)
                .ThenBy(edition => string.IsNullOrWhiteSpace(edition.ForeignEditionId) ? 1 : 0)
                .ThenBy(edition => edition.ForeignEditionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(edition => edition.EditionId)
                .ToList();
        }

        private static void EmitStage2Summary(
            Action<EditionFtsTraceEvent> trace,
            IReadOnlyList<EditionFtsMatch> editions,
            int fieldCount,
            long elapsedMilliseconds)
        {
            var distinctBookRanks = new Dictionary<int, int>();
            foreach (var ranked in editions.Take(100).Select((edition, index) => new { Edition = edition, Rank = index + 1 }))
            {
                var edition = ranked.Edition;
                if (!distinctBookRanks.TryGetValue(edition.BookId, out var distinctBookRank))
                {
                    distinctBookRank = distinctBookRanks.Count + 1;
                    distinctBookRanks[edition.BookId] = distinctBookRank;
                }

                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "candidate",
                    Step = "stage2_field_ranking",
                    RawRank = ranked.Rank,
                    DistinctBookRank = distinctBookRank,
                    EditionId = edition.EditionId,
                    BookId = edition.BookId,
                    AuthorId = edition.AuthorId,
                    EditionTitle = edition.EditionTitle,
                    BookTitle = edition.BookTitle,
                    AuthorName = edition.AuthorName,
                    NarratorNames = edition.NarratorNames,
                    Publisher = edition.Publisher,
                    DurationSeconds = edition.DurationSeconds,
                    ReleaseDate = edition.ReleaseDate,
                    ReadingFormatId = edition.ReadingFormatId,
                    Score = edition.MatchScore,
                    BroadRecallScore = edition.BroadRecallScore,
                    Stage2TitleScore = edition.Stage2TitleScore,
                    Stage2DetailScore = edition.Stage2DetailScore,
                    Stage2TitleSourceFields = edition.Stage2TitleSourceFields,
                    Stage2DetailSourceFields = edition.Stage2DetailSourceFields,
                    Stage2MatchedFieldCount = edition.Stage2MatchedFieldCount,
                    Stage2TitleFieldCount = edition.Stage2FieldHits.Count(hit => hit.TitleHit),
                    Stage2DetailFieldCount = edition.Stage2FieldHits.Count(hit => hit.DetailHit)
                });
            }

            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "summary",
                Step = "stage2_field_ranking",
                ElapsedMilliseconds = elapsedMilliseconds,
                ResultCount = editions.Count,
                DistinctBookCount = editions.Select(edition => edition.BookId).Distinct().Count(),
                ResultSource = $"{fieldCount} residual field queries"
            });
        }

        private sealed class Stage2ScoreRow
        {
            public int EditionId { get; set; }
            public string FieldKey { get; set; }
            public double Score { get; set; }
        }


    }

	    public class EditionFtsMatch
	    {
	        public int EditionId { get; set; }
	        public string ForeignEditionId { get; set; }
	        public int BookId { get; set; }
	        public string EditionTitle { get; set; }
	        public string MatchingTitle { get; set; }
	        public string EditionSubTitle { get; set; }
	        public string BookTitle { get; set; }
	        public int AuthorId { get; set; }
	        public string AuthorName { get; set; }
		        public string Lang { get; set; }
		        public string NarratorNames { get; set; }
		        public string Publisher { get; set; }
		        public string CoverUrl { get; set; }
		        public DateTime? ReleaseDate { get; set; }
		        public int? DurationSeconds { get; set; }
		        public int? ReadingFormatId { get; set; }
	        public double MatchScore { get; set; }
	        public double BroadRecallScore { get; set; }
	        public double Stage2TitleScore { get; set; }
	        public double Stage2DetailScore { get; set; }
	        public string Stage2TitleSourceFields { get; set; }
	        public string Stage2DetailSourceFields { get; set; }
	        public int Stage2MatchedFieldCount { get; set; }
	        public List<EditionFtsFieldHit> Stage2FieldHits { get; set; } = new();
	        public string MatchedField { get; set; }
	        public double? FieldScore { get; set; }
	        // Diagnostics for deterministic ID matches
	        public int IdMatch { get; set; } // 1 for ID hit, 0 otherwise
        public string MatchedBy { get; set; } // ASIN, AUDIBLE_ASIN, ISBN10, ISBN13
        public string MatchedValue { get; set; }
    }

}
