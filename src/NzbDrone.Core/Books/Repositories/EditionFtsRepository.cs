using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NzbDrone.Core.Datastore;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Books
{
    public interface IEditionFtsRepository
    {
        bool FtsTableExists();
        void RebuildIndex();


        /// <summary>
        /// Rank distinct books from edition title plus book series/author text, then return every
        /// edition under those recalled books. All sibling editions share the book-level FTS score;
        /// the production evidence evaluator owns narrator, duration, year, publisher, and format
        /// selection. The historical method name is retained while callers migrate.
        /// </summary>
        List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20);
    }

    /// <summary>
    /// Optional, read-only diagnostics for production book recall and edition expansion.
    /// Normal callers use <see cref="IEditionFtsRepository"/>; MatchBench opts into
    /// this interface so it can observe the exact production query without
    /// maintaining a second implementation.
    /// </summary>
    public interface IEditionFtsTraceRepository
    {
        List<EditionFtsMatch> SearchWithTwoStepWithTrace(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace,
            int limit = 20);
    }

    /// <summary>
    /// Production staged matching contract. Book recall and Edition ranking are deliberately
    /// separate so the matcher can prove candidate authors from the original file tags before
    /// any sibling Editions are expanded or allowed to earn a score.
    /// </summary>
    public interface IStagedEditionFtsRepository
    {
        List<BookFtsMatch> RecallBooks(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null,
            int limit = 20);

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

    public class EditionFtsRepository : IEditionFtsRepository, IEditionFtsTraceRepository, IStagedEditionFtsRepository
    {
        private readonly IMainDatabase _database;
        private readonly Logger _logger;
        private bool? _ftsTableExists;
        private readonly DatabaseType _dbType;
        private bool _indexCheckedOnce;

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
                    var sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='edition_fts'";
                    var count = conn.ExecuteScalar<int>(sql);
                    var exists = count > 0;
                    if (exists)
                    {
                        _ftsTableExists = true;
                        _logger.Debug("[FTS-EXISTS] FTS5 edition search table found and ready");
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

        private void EnsureIndexPopulated()
        {
            if (_dbType != DatabaseType.SQLite) return;
            if (_indexCheckedOnce) return;
            try
            {
                using (var conn = _database.OpenConnection())
                {
                    var tbl = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='edition_fts'");
                    if (tbl > 0)
                    {
                        var n = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts");
                        if (n == 0)
                        {
                            _logger.Debug("[EDITION-FTS] edition_fts is empty on first use; rebuilding index now");
                            RebuildIndex();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[EDITION-FTS] EnsureIndexPopulated check failed; continuing");
            }
            finally
            {
                _indexCheckedOnce = true;
            }
        }


        public void RebuildIndex()
        {
            if (_dbType != DatabaseType.SQLite)
            {
                _logger.Info("Edition FTS rebuild not needed for PostgreSQL (uses generated column)");
                return;
            }

            using (var conn = _database.OpenConnection())
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    _logger.Info("Rebuilding edition FTS index...");

                    // Detect schema version by checking columns
                    // Migration 016+: MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher
                    // Migration 010+: MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator (Title removed)
                    // Migration 009:  Title, MatchingTitle, SeriesName, AuthorName, Narrator
                    // Migration 008:  Title, SeriesName, AuthorName, Narrator
                    bool hasMatchingTitle = false;
                    bool hasSeriesAuthor = false;
                    bool hasSubtitle = false;
                    bool hasTitle = false;
                    bool hasPublisher = false;
                    try
                    {
                        var colNames = conn.Query<(int cid, string name, object type, int notnull, object dflt, int pk)>("PRAGMA table_info('edition_fts')")
                            .Select(x => x.name?.ToLowerInvariant()).ToHashSet();
                        hasMatchingTitle = colNames.Contains("matchingtitle");
                        hasSeriesAuthor = colNames.Contains("seriesname") && colNames.Contains("authorname");
                        hasSubtitle = colNames.Contains("subtitle");
                        hasTitle = colNames.Contains("title");
                        hasPublisher = colNames.Contains("publisher");
                    }
                    catch { /* fallback safe */ }

                    using var transaction = conn.BeginTransaction();
                    int Exec(string sql) => conn.Execute(sql, transaction: transaction);

                    // Clear existing index
                    var deleteStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    // edition_fts is an FTS5 contentless table (content=''), so direct DELETE is not allowed.
                    // Use the special FTS5 command instead.
                    Exec("INSERT INTO edition_fts(edition_fts) VALUES('delete-all');");
                    deleteStopwatch.Stop();
                    _logger.Debug("[DB-TIMING][FTS-INDEX] Cleared existing FTS index in {0}ms", deleteStopwatch.ElapsedMilliseconds);

                    var insertStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    if (hasPublisher && hasMatchingTitle && hasSubtitle && hasSeriesAuthor)
                    {
                        // Current schema (migration 016+): MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher
                        // Condition includes hasSeriesAuthor as safety guard since INSERT targets those columns
                        Exec(@"
                            INSERT INTO edition_fts (rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)
                            SELECT e.Id,
                                   COALESCE(e.MatchingTitle, ''),
                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(b.SeriesName, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(a.Name, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Subtitle, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                       WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                           THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                       ELSE COALESCE(e.Narrator, '')
	                                   END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Publisher, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                            FROM Editions e
                            JOIN Books b ON e.BookId = b.Id
                            JOIN Authors a ON b.AuthorId = a.Id
                            WHERE e.Title IS NOT NULL");
                        _logger.Debug("[FTS-INDEX] Using 6-column schema (MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator, Publisher)");
                    }
                    else if (hasMatchingTitle)
                    {
                        if (hasSubtitle)
                        {
                            if (hasTitle)
                            {
                                // Legacy/unknown schema: Title, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator
                                Exec(@"
                                    INSERT INTO edition_fts (rowid, Title, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                                    SELECT e.Id,
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Title, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                           COALESCE(e.MatchingTitle, ''),
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(b.SeriesName, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(a.Name, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Subtitle, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                               ELSE COALESCE(e.Narrator, '')
	                                           END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                                    FROM Editions e
                                    JOIN Books b ON e.BookId = b.Id
                                    JOIN Authors a ON b.AuthorId = a.Id
                                    WHERE e.Title IS NOT NULL");
                                _logger.Debug("[FTS-INDEX] Using 6-column schema (Title, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)");
                            }
                            else
                            {
                                // Schema (migration 010+): MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator
                                Exec(@"
                                    INSERT INTO edition_fts (rowid, MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)
                                    SELECT e.Id,
                                           COALESCE(e.MatchingTitle, ''),
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(b.SeriesName, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(a.Name, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Subtitle, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                           COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                               WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                                   THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                               ELSE COALESCE(e.Narrator, '')
	                                           END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                                    FROM Editions e
                                    JOIN Books b ON e.BookId = b.Id
                                    JOIN Authors a ON b.AuthorId = a.Id
                                    WHERE e.Title IS NOT NULL");
                                _logger.Debug("[FTS-INDEX] Using 5-column schema (MatchingTitle, SeriesName, AuthorName, Subtitle, Narrator)");
                            }
                        }
                        else
                        {
                            // Migration 009 schema: Title, MatchingTitle, SeriesName, AuthorName, Narrator
                            Exec(@"
                                INSERT INTO edition_fts (rowid, Title, MatchingTitle, SeriesName, AuthorName, Narrator)
                                SELECT e.Id,
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Title, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(e.MatchingTitle, ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(b.SeriesName, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(a.Name, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                           WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                               THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                           ELSE COALESCE(e.Narrator, '')
	                                       END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                                FROM Editions e
                                JOIN Books b ON e.BookId = b.Id
                                JOIN Authors a ON b.AuthorId = a.Id
                                WHERE e.Title IS NOT NULL");
                            _logger.Debug("[FTS-INDEX] Using 5-column schema (Title, MatchingTitle, SeriesName, AuthorName, Narrator)");
                        }
                    }
                    else if (hasSeriesAuthor)
                    {
                        if (hasSubtitle)
                        {
                            // Schema without MatchingTitle but with Subtitle
                            Exec(@"
                                INSERT INTO edition_fts (rowid, Title, SeriesName, AuthorName, Subtitle, Narrator)
                                SELECT e.Id,
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Title, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(b.SeriesName, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(a.Name, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Subtitle, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                           WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                               THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                           ELSE COALESCE(e.Narrator, '')
	                                       END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                                FROM Editions e
                                JOIN Books b ON e.BookId = b.Id
                                JOIN Authors a ON b.AuthorId = a.Id
                                WHERE e.Title IS NOT NULL");
                            _logger.Debug("[FTS-INDEX] Using 5-column schema (Title, SeriesName, AuthorName, Subtitle, Narrator)");
                        }
                        else
                        {
                            // Migration 008 schema: Title, SeriesName, AuthorName, Narrator
                            Exec(@"
                                INSERT INTO edition_fts (rowid, Title, SeriesName, AuthorName, Narrator)
                                SELECT e.Id,
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Title, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(b.SeriesName, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(a.Name, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                       COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                           WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                               THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                           ELSE COALESCE(e.Narrator, '')
	                                       END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                                FROM Editions e
                                JOIN Books b ON e.BookId = b.Id
                                JOIN Authors a ON b.AuthorId = a.Id
                                WHERE e.Title IS NOT NULL");
                            _logger.Debug("[FTS-INDEX] Using 4-column schema (Title, SeriesName, AuthorName, Narrator)");
                        }
                    }
                    else
                    {
                        // Legacy schema - should not happen after migration 008
                        _logger.Warn("[FTS-INDEX] Detected legacy FTS schema - consider running migrations");
                        Exec(@"
                            INSERT INTO edition_fts (rowid, Title, Narrator)
                            SELECT e.Id,
                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(e.Title, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), ''),
	                                   COALESCE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(CASE
	                                       WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
	                                           THEN COALESCE((SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
	                                       ELSE COALESCE(e.Narrator, '')
	                                   END, '&#39;', ''''), '&apos;', ''''), '&quot;', '""'), '&amp;', '&'), '&nbsp;', ' '), '')
                            FROM Editions e
                            WHERE e.Title IS NOT NULL");
                        _logger.Debug("[FTS-INDEX] Using legacy 2-column schema (Title, Narrator)");
                    }
                    insertStopwatch.Stop();

                    transaction.Commit();
                    
                    var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM edition_fts");
                    stopwatch.Stop();
                    
                    _logger.Debug("[DB-TIMING][FTS-INDEX] Edition FTS index rebuilt with {0} entries in {1}ms total (delete: {2}ms, insert: {3}ms)",
                        count, stopwatch.ElapsedMilliseconds, deleteStopwatch.ElapsedMilliseconds, insertStopwatch.ElapsedMilliseconds);
                    
                    // Special logging for large rebuilds
                    if (count > 1000)
                    {
                        _logger.Debug("[DB-TIMING][FTS-INDEX][LARGE-REBUILD] Rebuilt FTS for {0} editions at {1} editions/sec",
                            count, (count * 1000.0) / stopwatch.ElapsedMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error rebuilding edition FTS index");
                    throw;
                }
            }
        }


        private static string BuildPostgresTsQuery(IEnumerable<string> tokens)
        {
            if (tokens == null)
            {
                return string.Empty;
            }

            // Build a safe tsquery string (OR'ed terms) using Unicode-aware lexeme extraction.
            // \p{L} = any Unicode letter, \p{Nd} = any Unicode digit
            // This supports Chinese, Japanese, Korean, Arabic, Cyrillic, etc.
            var parts = tokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .SelectMany(t => Regex.Matches(t, @"[\p{L}\p{Nd}]+").Cast<Match>().Select(m => m.Value))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return parts.Count == 0 ? string.Empty : string.Join(" | ", parts);
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
            return token.Any(c => char.IsLetterOrDigit(c));
        }

        /// <summary>
        /// Convert token to FTS query term. Quote if contains special chars.
        /// </summary>
        private static string TokenToFtsQueryTerm(string token)
        {
            // If token contains hyphen, period, or is pure digits, quote it
            // Otherwise FTS interprets '-' as MINUS operator
            if (token.Contains('-') || token.Contains('.') || token.All(char.IsDigit))
            {
                // Escape double quotes for FTS5: " -> ""
                return $"\"{token.Replace("\"", "\"\"")}\"";
            }
            return token;
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

        private static void EmitFtsCandidates(
            Action<EditionFtsTraceEvent> trace,
            string step,
            IReadOnlyList<EditionFtsMatch> candidates)
        {
            if (trace == null || candidates == null)
            {
                return;
            }

            var distinctBookRanks = new Dictionary<int, int>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null)
                {
                    continue;
                }

                if (!distinctBookRanks.TryGetValue(candidate.BookId, out var distinctBookRank))
                {
                    distinctBookRank = distinctBookRanks.Count + 1;
                    distinctBookRanks[candidate.BookId] = distinctBookRank;
                }

                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "candidate",
                    Step = step,
                    RawRank = i + 1,
                    DistinctBookRank = distinctBookRank,
                    EditionId = candidate.EditionId > 0 ? candidate.EditionId : null,
                    BookId = candidate.BookId > 0 ? candidate.BookId : null,
                    AuthorId = candidate.AuthorId > 0 ? candidate.AuthorId : null,
                    Score = candidate.MatchScore,
                    EditionTitle = candidate.EditionTitle,
                    BookTitle = candidate.BookTitle,
                    AuthorName = candidate.AuthorName,
                    NarratorNames = candidate.NarratorNames,
                    Publisher = candidate.Publisher,
                    DurationSeconds = candidate.DurationSeconds,
                    ReleaseDate = candidate.ReleaseDate,
                    ReadingFormatId = candidate.ReadingFormatId
                });
            }
        }

        private static List<EditionFtsMatch> ApplyBookRecallRanking(
            IReadOnlyList<EditionFtsMatch> recalledBooks,
            IEnumerable<EditionFtsMatch> editions)
        {
            var recallByBookId = recalledBooks
                .Select((candidate, index) => new
                {
                    candidate.BookId,
                    candidate.MatchScore,
                    Rank = index
                })
                .Where(candidate => candidate.BookId > 0)
                .GroupBy(candidate => candidate.BookId)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        Rank = group.Min(candidate => candidate.Rank),
                        Score = group.Max(candidate => candidate.MatchScore)
                    });

            var expanded = new List<EditionFtsMatch>();
            foreach (var edition in editions ?? Enumerable.Empty<EditionFtsMatch>())
            {
                if (edition == null || !recallByBookId.TryGetValue(edition.BookId, out var recall))
                {
                    continue;
                }

                // FTS ranks books only. Every sibling edition receives the same book-level score;
                // edition selection remains the responsibility of the production evidence evaluator.
                edition.MatchScore = recall.Score;
                expanded.Add(edition);
            }

            return expanded
                .OrderBy(edition => recallByBookId[edition.BookId].Rank)
                .ThenBy(edition => string.IsNullOrWhiteSpace(edition.ForeignEditionId) ? 1 : 0)
                .ThenBy(edition => edition.ForeignEditionId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(edition => edition.EditionId)
                .ToList();
        }

        public List<BookFtsMatch> RecallBooks(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null,
            int limit = 20)
        {
            EnsureIndexPopulated();
            if (!FtsTableExists())
            {
                return new List<BookFtsMatch>();
            }

            var terms = tokens?
                .Where(token => !string.IsNullOrWhiteSpace(token) && IsValidFtsToken(token))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            if (terms.Count == 0)
            {
                return new List<BookFtsMatch>();
            }

            return _dbType == DatabaseType.PostgreSQL
                ? RecallBooksPostgres(authorId, terms, mediaType, trace, limit)
                : RecallBooksSqlite(authorId, terms, mediaType, trace, limit);
        }

        public List<EditionFtsMatch> RankEditions(
            IReadOnlyCollection<BookFtsMatch> recalledBooks,
            IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace = null)
        {
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
                .Where(query => query?.Terms != null && query.Terms.Any(IsValidFtsToken))
                .GroupBy(query => query.Key ?? string.Join(" ", query.Terms), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            return _dbType == DatabaseType.PostgreSQL
                ? RankEditionsPostgres(recalls, queries, mediaType, trace)
                : RankEditionsSqlite(recalls, queries, mediaType, trace);
        }

        private List<BookFtsMatch> RecallBooksSqlite(
            int? authorId,
            IReadOnlyList<string> terms,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace,
            int limit)
        {
            var columns = "MatchingTitle SeriesName AuthorName";
            var query = string.Join(" OR ", terms.Select(term => $"{{{columns}}}:{TokenToFtsQueryTerm(term)}"));
            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "query",
                Step = "stage1_book_recall",
                Terms = terms,
                Columns = columns,
                Query = query
            });

            using var connection = _database.OpenConnection();
            var parameters = new DynamicParameters();
            parameters.Add("ftsQuery", query);
            parameters.Add("mediaType", (int)mediaType);
            parameters.Add("limit", limit);
            if (authorId.HasValue)
            {
                parameters.Add("authorId", authorId.Value);
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var sql = $@"
                WITH fts_matches AS MATERIALIZED (
                    SELECT rowid, bm25(edition_fts) AS bm25_score
                    FROM edition_fts
                    WHERE edition_fts MATCH @ftsQuery
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
                WHERE b.MediaType = @mediaType
                  {(authorId.HasValue ? "AND b.AuthorId = @authorId" : string.Empty)}
                GROUP BY b.Id, b.AuthorId, a.Name, b.Title, b.SeriesName, b.SeriesPosition
                ORDER BY MIN(fts_matches.bm25_score)
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
            int limit)
        {
            var query = BuildPostgresTsQuery(terms);
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<BookFtsMatch>();
            }

            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "query",
                Step = "stage1_book_recall",
                Terms = terms,
                Columns = "MatchingTitle SeriesName AuthorName",
                Query = query
            });

            using var connection = _database.OpenConnection();
            var parameters = new DynamicParameters();
            parameters.Add("tsQuery", query);
            parameters.Add("mediaType", (int)mediaType);
            parameters.Add("limit", limit);
            if (authorId.HasValue)
            {
                parameters.Add("authorId", authorId.Value);
            }

            var sql = $@"
                SELECT
                    b.""Id"" AS BookId,
                    b.""AuthorId"" AS AuthorId,
                    a.""Name"" AS AuthorName,
                    b.""Title"" AS BookTitle,
                    b.""SeriesName"" AS SeriesName,
                    b.""SeriesPosition"" AS SeriesPosition,
                    MAX(
                        ts_rank(to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')), to_tsquery('simple', @tsQuery)) +
                        ts_rank(to_tsvector('simple', COALESCE(b.""SeriesName"", '')), to_tsquery('simple', @tsQuery)) +
                        ts_rank(to_tsvector('simple', COALESCE(a.""Name"", '') || ' ' || COALESCE(a.""CleanName"", '') || ' ' || COALESCE(a.""TitleSlug"", '')), to_tsquery('simple', @tsQuery))
                    ) AS MatchScore
                FROM ""Editions"" e
                INNER JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                INNER JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
                WHERE b.""MediaType"" = @mediaType
                  AND (
                    to_tsvector('simple', COALESCE(e.""MatchingTitle"", '')) @@ to_tsquery('simple', @tsQuery)
                    OR to_tsvector('simple', COALESCE(b.""SeriesName"", '')) @@ to_tsquery('simple', @tsQuery)
                    OR to_tsvector('simple', COALESCE(a.""Name"", '') || ' ' || COALESCE(a.""CleanName"", '') || ' ' || COALESCE(a.""TitleSlug"", '')) @@ to_tsquery('simple', @tsQuery)
                  )
                  {(authorId.HasValue ? "AND b.\"AuthorId\" = @authorId" : string.Empty)}
                GROUP BY b.""Id"", b.""AuthorId"", a.""Name"", b.""Title"", b.""SeriesName"", b.""SeriesPosition""
                ORDER BY MatchScore DESC
                LIMIT @limit";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var results = connection.Query<BookFtsMatch>(sql, parameters).ToList();
            stopwatch.Stop();
            EmitBookRecallTrace(trace, results, stopwatch.ElapsedMilliseconds);
            return results;
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
                ? @"COALESCE(e.""Publisher"", '')"
                : @"COALESCE(e.""NarratorNames"", '') || ' ' || COALESCE(e.""Narrator"", '')";
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
                        WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                            THEN COALESCE((SELECT GROUP_CONCAT(value, ', ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
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
                    COALESCE(NULLIF(translate(e.""NarratorNames"", '[]""', ''), ''), COALESCE(e.""Narrator"", '')) AS NarratorNames,
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
            connection.Execute(@"
                CREATE VIRTUAL TABLE temp.stage2_title_fts USING fts5(
                    MatchingTitle,
                    content='',
                    content_rowid='rowid',
                    tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.'''
                );
                CREATE VIRTUAL TABLE temp.stage2_detail_fts USING fts5(
                    Detail,
                    content='',
                    content_rowid='rowid',
                    tokenize = 'unicode61 remove_diacritics 1 tokenchars ''-.'''
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
                ? "COALESCE(e.Publisher, '')"
                : @"CASE
                        WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
                            THEN COALESCE(
                                (SELECT GROUP_CONCAT(value, ' ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''),
                                COALESCE(e.Narrator, ''))
                        ELSE COALESCE(e.Narrator, '')
                    END";
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
                        .Distinct(StringComparer.OrdinalIgnoreCase)
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
                var ftsQuery = string.Join(" OR ", prepared.Terms.Select(term => $"{{{ftsColumn}}}:{TokenToFtsQueryTerm(term)}"));
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
                    Terms = prepared.Terms,
                    Columns = traceColumn,
                    Query = ftsQuery
                });
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
                    Terms = fieldQuery.Terms,
                    Columns = column,
                    Query = query
                });
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

                // BM25/ts_rank is diagnostic here. Cross-field sums reward rarity and allow one
                // physical field to earn both title and narrator/publisher credit.
                edition.MatchScore = edition.BroadRecallScore;
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

        /// <summary>
        /// FTS ranks distinct books from MatchingTitle, SeriesName, and AuthorName, then expands
        /// every recalled book to all sibling editions without edition-level reranking.
        /// </summary>
        public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
        {
            return SearchWithTwoStepCore(authorId, tokens, mediaType, limit, null);
        }

        public List<EditionFtsMatch> SearchWithTwoStepWithTrace(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            Action<EditionFtsTraceEvent> trace,
            int limit = 20)
        {
            return SearchWithTwoStepCore(authorId, tokens, mediaType, limit, trace);
        }

        private List<EditionFtsMatch> SearchWithTwoStepCore(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            int limit,
            Action<EditionFtsTraceEvent> trace)
        {
            EnsureIndexPopulated();
            if (!FtsTableExists())
            {
                _logger.Warn("[TWO-STEP] FTS table does not exist");
                return new List<EditionFtsMatch>();
            }

            if (_dbType == DatabaseType.PostgreSQL)
            {
                return SearchWithTwoStepPostgres(authorId, tokens, mediaType, limit, trace);
            }

            var tokenList = tokens?.Where(t => !string.IsNullOrWhiteSpace(t) && IsValidFtsToken(t)).ToList();
            if (tokenList == null || tokenList.Count == 0)
            {
                _logger.Warn("[TWO-STEP] No valid tokens provided");
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "completed",
                    Step = "input",
                    Terms = Array.Empty<string>(),
                    ResultCount = 0,
                    DistinctBookCount = 0,
                    ResultSource = "no_valid_terms"
                });
                return new List<EditionFtsMatch>();
            }

            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "input",
                Step = "input",
                Terms = tokenList.ToList(),
                ResultCount = tokenList.Count
            });

            using (var conn = _database.OpenConnection())
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    // Step 1: Search title columns only (no Subtitle, no Narrator) to find the right BOOK
                    // Column restriction syntax: {col1 col2}:term
                    // NOTE: Title removed from FTS to avoid double-counting with MatchingTitle
                    // NOTE: Subtitle excluded from Step 1 - should only influence EDITION selection, not BOOK selection
                    var titleColumns = "MatchingTitle SeriesName AuthorName";
                    var step1QueryParts = tokenList.Select(t => $"{{{titleColumns}}}:{TokenToFtsQueryTerm(t)}");
                    var step1Query = string.Join(" OR ", step1QueryParts);

                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "query",
                        Step = "step1_book_recall",
                        Terms = tokenList.ToList(),
                        Columns = titleColumns,
                        Query = step1Query
                    });

                    _logger.Debug("[TWO-STEP] Step 1 query (title only): {0}",
                        step1Query.Length > 200 ? step1Query.Substring(0, 200) + "..." : step1Query);

                    // DISABLED 2025-12-28: Token count ranking was causing series-name-as-title books
                    // (e.g., "The Codex Alera") to outrank correct individual books (e.g., "Academ's Fury")
                    // because series tokens in file tags inflated token counts for compilations.
                    // BM25 alone + smoke test should handle ranking correctly.
                    // See: Codex Alera investigation - raw BM25 ranked correctly, token count broke it.
                    //
                    // var tokenCountParts = tokenList.Select(t =>
                    // {
                    //     var escapedToken = TokenToFtsQueryTerm(t).Replace("'", "''");
                    //     return $"COALESCE((SELECT 1 FROM edition_fts WHERE edition_fts.rowid = fts_matches.rowid AND edition_fts MATCH '{{MatchingTitle}}:{escapedToken}'), 0)";
                    // });
                    // var tokenCountExpr = tokenList.Count > 0 ? string.Join(" + ", tokenCountParts) : "0";

                    var p1 = new DynamicParameters();
                    p1.Add("ftsQuery", step1Query);
                    p1.Add("mediaType", (int)mediaType);
                    p1.Add("limit", limit);

                    string sql1;
                    if (authorId.HasValue)
                    {
                        p1.Add("authorId", authorId.Value);
                        sql1 = $@"
                            WITH fts_matches AS MATERIALIZED (
                                SELECT rowid, bm25(edition_fts) as bm25_score
                                FROM edition_fts
                                WHERE edition_fts MATCH @ftsQuery
                            )
                            SELECT
                                e.BookId AS BookId,
                                MAX(0 - fts_matches.bm25_score) AS MatchScore
                            FROM fts_matches
                            INNER JOIN Editions e ON e.Id = fts_matches.rowid
                            INNER JOIN Books b ON e.BookId = b.Id
                            WHERE b.AuthorId = @authorId
                              AND b.MediaType = @mediaType
                            GROUP BY e.BookId
                            ORDER BY MIN(fts_matches.bm25_score)
                            LIMIT @limit";
                    }
                    else
                    {
                        sql1 = $@"
                            WITH fts_matches AS MATERIALIZED (
                                SELECT rowid, bm25(edition_fts) as bm25_score
                                FROM edition_fts
                                WHERE edition_fts MATCH @ftsQuery
                            )
                            SELECT
                                e.BookId AS BookId,
                                MAX(0 - fts_matches.bm25_score) AS MatchScore
                            FROM fts_matches
                            INNER JOIN Editions e ON e.Id = fts_matches.rowid
                            INNER JOIN Books b ON e.BookId = b.Id
                            WHERE b.MediaType = @mediaType
                            GROUP BY e.BookId
                            ORDER BY MIN(fts_matches.bm25_score)
                            LIMIT @limit";
                    }

                    var step1Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var step1Results = conn.Query<EditionFtsMatch>(sql1, p1).ToList();
                    step1Stopwatch.Stop();
                    _logger.Debug("[TWO-STEP] Step 1 returned {0} results", step1Results.Count);

                    EmitFtsCandidates(trace, "step1_book_recall", step1Results);
                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "summary",
                        Step = "step1_book_recall",
                        ElapsedMilliseconds = step1Stopwatch.ElapsedMilliseconds,
                        TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        ResultCount = step1Results.Count,
                        DistinctBookCount = step1Results.Select(result => result.BookId).Distinct().Count()
                    });

                    if (step1Results.Count == 0)
                    {
                        stopwatch.Stop();
                        _logger.Debug("[TWO-STEP] No Step 1 results, returning empty");
                        EmitFtsTrace(trace, new EditionFtsTraceEvent
                        {
                            EventType = "completed",
                            Step = "completed",
                            TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                            ResultCount = 0,
                            DistinctBookCount = 0,
                            ResultSource = "step1_empty"
                        });
                        return new List<EditionFtsMatch>();
                    }

                    var bookIds = step1Results.Select(r => r.BookId).Distinct().ToList();
                    _logger.Debug("[BOOK-RECALL] Expanding {0} recalled books to all sibling editions", bookIds.Count);
                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "query",
                        Step = "edition_expansion",
                        Columns = "all editions for recalled books",
                        DistinctBookCount = bookIds.Count
                    });

                    var p2 = new DynamicParameters();
                    p2.Add("mediaType", (int)mediaType);

                    var bookIdPlaceholders = new List<string>();
                    for (int i = 0; i < bookIds.Count; i++)
                    {
                        var pname = $"@bookId{i}";
                        bookIdPlaceholders.Add(pname);
                        p2.Add($"bookId{i}", bookIds[i]);
                    }
                    var bookIdInClause = string.Join(",", bookIdPlaceholders);

                    string sql2;
                    if (authorId.HasValue)
                    {
                        p2.Add("authorId", authorId.Value);
                        sql2 = $@"
                            SELECT
                                e.Id AS EditionId,
                                e.ForeignEditionId AS ForeignEditionId,
                                e.BookId AS BookId,
                                COALESCE(NULLIF(LOWER(TRIM(e.Language)), ''), 'unknown') AS Lang,
                                e.Title AS EditionTitle,
                                e.Subtitle AS EditionSubTitle,
                                b.Title AS BookTitle,
	                                b.AuthorId AS AuthorId,
		                                a.Name AS AuthorName,
		                                CASE
		                                    WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
		                                        THEN COALESCE((SELECT GROUP_CONCAT(value, ', ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
		                                    ELSE COALESCE(e.Narrator, '')
		                                END AS NarratorNames,
		                                e.Publisher AS Publisher,
		                                e.Images AS CoverUrl,
		                                e.DurationSeconds AS DurationSeconds,
		                                e.ReleaseDate AS ReleaseDate,
		                                e.ReadingFormatId AS ReadingFormatId,
	                                0.0 AS MatchScore
                            FROM Editions e
                            INNER JOIN Books b ON e.BookId = b.Id
                            INNER JOIN Authors a ON b.AuthorId = a.Id
                            WHERE b.AuthorId = @authorId
                              AND b.MediaType = @mediaType
                              AND b.Id IN ({bookIdInClause})";
                    }
                    else
                    {
                        sql2 = $@"
                            SELECT
                                e.Id AS EditionId,
                                e.ForeignEditionId AS ForeignEditionId,
                                e.BookId AS BookId,
                                COALESCE(NULLIF(LOWER(TRIM(e.Language)), ''), 'unknown') AS Lang,
                                e.Title AS EditionTitle,
                                e.Subtitle AS EditionSubTitle,
                                b.Title AS BookTitle,
	                                b.AuthorId AS AuthorId,
		                                a.Name AS AuthorName,
		                                CASE
		                                    WHEN json_valid(e.NarratorNames) AND json_type(e.NarratorNames) = 'array'
		                                        THEN COALESCE((SELECT GROUP_CONCAT(value, ', ') FROM json_each(e.NarratorNames) WHERE value IS NOT NULL AND value != ''), COALESCE(e.Narrator, ''))
		                                    ELSE COALESCE(e.Narrator, '')
		                                END AS NarratorNames,
		                                e.Publisher AS Publisher,
		                                e.Images AS CoverUrl,
		                                e.DurationSeconds AS DurationSeconds,
		                                e.ReleaseDate AS ReleaseDate,
		                                e.ReadingFormatId AS ReadingFormatId,
	                                0.0 AS MatchScore
                            FROM Editions e
                            INNER JOIN Books b ON e.BookId = b.Id
                            INNER JOIN Authors a ON b.AuthorId = a.Id
                            WHERE b.MediaType = @mediaType
                              AND b.Id IN ({bookIdInClause})";
                    }

                    var expansionStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var expandedResults = ApplyBookRecallRanking(
                        step1Results,
                        conn.Query<EditionFtsMatch>(sql2, p2).ToList());
                    expansionStopwatch.Stop();
                    stopwatch.Stop();

                    EmitFtsCandidates(trace, "edition_expansion", expandedResults);
                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "summary",
                        Step = "edition_expansion",
                        ElapsedMilliseconds = expansionStopwatch.ElapsedMilliseconds,
                        TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        ResultCount = expandedResults.Count,
                        DistinctBookCount = expandedResults.Select(result => result.BookId).Distinct().Count()
                    });

                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "completed",
                        Step = "completed",
                        TotalElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        ResultCount = expandedResults.Count,
                        DistinctBookCount = expandedResults.Select(result => result.BookId).Distinct().Count(),
                        ResultSource = "book_recall_edition_expansion"
                    });
                    return expandedResults;
                }
                catch (SqliteException ex) when (ex.Message?.Contains("no such column") == true || ex.Message?.Contains("malformed") == true)
                {
                    _logger.Error(ex, "[TWO-STEP] FTS query error - query may be malformed");
                    return new List<EditionFtsMatch>();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[TWO-STEP] Error performing two-step search");
                    return new List<EditionFtsMatch>();
                }
            }
        }

        private List<EditionFtsMatch> SearchWithTwoStepPostgres(
            int? authorId,
            IEnumerable<string> tokens,
            BookMediaType mediaType,
            int limit,
            Action<EditionFtsTraceEvent> trace)
        {
            var inputTerms = tokens?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList() ?? new List<string>();
            var tsQuery = BuildPostgresTsQuery(inputTerms);
            if (string.IsNullOrWhiteSpace(tsQuery))
            {
                _logger.Warn("[TWO-STEP] No valid tokens for PostgreSQL tsquery");
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "completed",
                    Step = "input",
                    Terms = inputTerms,
                    ResultCount = 0,
                    DistinctBookCount = 0,
                    ResultSource = "no_valid_terms"
                });
                return new List<EditionFtsMatch>();
            }

            // Extract individual tokens for token count expression
            // Unicode-aware: \p{L} = any letter, \p{Nd} = any digit
            var tokenList = inputTerms
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .SelectMany(t => Regex.Matches(t, @"[\p{L}\p{Nd}]+").Cast<Match>().Select(m => m.Value))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            EmitFtsTrace(trace, new EditionFtsTraceEvent
            {
                EventType = "input",
                Step = "input",
                Terms = inputTerms,
                Query = tsQuery,
                ResultCount = tokenList.Count
            });

            using (var conn = _database.OpenConnection())
            {
                var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var p1 = new DynamicParameters();
                p1.Add("tsQuery", tsQuery);
                p1.Add("mediaType", (int)mediaType);
                p1.Add("limit", limit);

                var queryExpr = "to_tsquery('simple', @tsQuery)";
                var seriesVectorExpr = @"to_tsvector('simple', COALESCE(b.""SeriesName"", ''))";

                // Match baseline idx_authors_fts expression (migration 001) so the index can be used.
                var authorVectorExpr = @"
                    to_tsvector('simple',
                        COALESCE(a.""Name"", '') || ' ' ||
                        COALESCE(a.""CleanName"", '') || ' ' ||
                        COALESCE(a.""TitleSlug"", '')
                    )";

                // DISABLED 2025-12-28: Token count ranking disabled - see SQLite comment above.
                // BM25/ts_rank alone + smoke test should handle ranking correctly.
                //
                // var matchingTitleVector = @"to_tsvector('simple', COALESCE(e.""MatchingTitle"", ''))";
                // var pgTokenCountParts = tokenList.Select(t =>
                //     $"CASE WHEN {matchingTitleVector} @@ to_tsquery('simple', '{t.Replace("'", "''")}') THEN 1 ELSE 0 END");
                // var pgTokenCountExpr = tokenList.Count > 0 ? string.Join(" + ", pgTokenCountParts) : "0";

                // STEP 1: PARITY WITH SQLITE - Title columns only (no Subtitle, no Narrator)
                // SQLite uses: {MatchingTitle SeriesName AuthorName}:token
                // So we match: MatchingTitle, SeriesName, AuthorName (via JOINs)
                var step1EditionVectorExpr = @"to_tsvector('simple', COALESCE(e.""MatchingTitle"", ''))";
                var step1ScoreExpr = $"(ts_rank({step1EditionVectorExpr}, {queryExpr}) + ts_rank({seriesVectorExpr}, {queryExpr}) + ts_rank({authorVectorExpr}, {queryExpr}))";

                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "query",
                    Step = "step1_book_recall",
                    Terms = inputTerms,
                    Columns = "MatchingTitle SeriesName AuthorName",
                    Query = tsQuery
                });

                // Order by ts_rank only (token count disabled)
                var step1Sql = $@"
                    SELECT
                        b.""Id"" AS BookId,
                        MAX({step1ScoreExpr}) AS MatchScore
                    FROM ""Editions"" e
                    INNER JOIN ""Books"" b ON e.""BookId"" = b.""Id""
                    INNER JOIN ""Authors"" a ON b.""AuthorId"" = a.""Id""
                    WHERE b.""MediaType"" = @mediaType
                      AND (
                        {step1EditionVectorExpr} @@ {queryExpr}
                        OR {seriesVectorExpr} @@ {queryExpr}
                        OR {authorVectorExpr} @@ {queryExpr}
                      )
                      {(authorId.HasValue ? "AND b.\"AuthorId\" = @authorId" : string.Empty)}
                    GROUP BY b.""Id""
                    ORDER BY MAX({step1ScoreExpr}) DESC
                    LIMIT @limit";

                if (authorId.HasValue)
                {
                    p1.Add("authorId", authorId.Value);
                }

                var step1Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var step1Results = conn.Query<EditionFtsMatch>(step1Sql, p1).ToList();
                step1Stopwatch.Stop();
                var bookIds = step1Results.Select(result => result.BookId).ToList();

                EmitFtsCandidates(trace, "step1_book_recall", step1Results);
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "summary",
                    Step = "step1_book_recall",
                    ElapsedMilliseconds = step1Stopwatch.ElapsedMilliseconds,
                    TotalElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds,
                    ResultCount = step1Results.Count,
                    DistinctBookCount = bookIds.Distinct().Count()
                });

                if (bookIds.Count == 0)
                {
                    totalStopwatch.Stop();
                    EmitFtsTrace(trace, new EditionFtsTraceEvent
                    {
                        EventType = "completed",
                        Step = "completed",
                        TotalElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds,
                        ResultCount = 0,
                        DistinctBookCount = 0,
                        ResultSource = "step1_empty"
                    });
                    return new List<EditionFtsMatch>();
                }

                var p2 = new DynamicParameters();
                p2.Add("mediaType", (int)mediaType);
                p2.Add("bookIds", bookIds.ToArray()); // Dapper requires array for = ANY(@param)

                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "query",
                    Step = "edition_expansion",
                    Columns = "all editions for recalled books",
                    DistinctBookCount = bookIds.Count
                });

                var expansionSql = $@"
                    SELECT
                        e.""Id"" AS EditionId,
                        e.""ForeignEditionId"" AS ForeignEditionId,
                        e.""BookId"" AS BookId,
                        COALESCE(NULLIF(LOWER(TRIM(e.""Language"")), ''), 'unknown') AS Lang,
                        e.""Title"" AS EditionTitle,
                        e.""Subtitle"" AS EditionSubTitle,
                        b.""Title"" AS BookTitle,
	                        b.""AuthorId"" AS AuthorId,
		                        a.""Name"" AS AuthorName,
		                        COALESCE(NULLIF(translate(e.""NarratorNames"", '[]""', ''), ''), COALESCE(e.""Narrator"", '')) AS NarratorNames,
		                        e.""Publisher"" AS Publisher,
		                        e.""Images"" AS CoverUrl,
		                        e.""DurationSeconds"" AS DurationSeconds,
		                        e.""ReleaseDate"" AS ReleaseDate,
		                        e.""ReadingFormatId"" AS ReadingFormatId,
	                        0.0 AS MatchScore
                    FROM ""Editions"" e
                    INNER JOIN ""Books"" b ON e.""BookId"" = b.""Id""
                    INNER JOIN ""Authors"" a ON b.""AuthorId"" = a.""Id""
                    WHERE b.""MediaType"" = @mediaType
                      AND b.""Id"" = ANY(@bookIds)
                      {(authorId.HasValue ? "AND b.\"AuthorId\" = @authorId" : string.Empty)}";

                if (authorId.HasValue)
                {
                    p2.Add("authorId", authorId.Value);
                }

                var expansionStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var expandedResults = ApplyBookRecallRanking(
                    step1Results,
                    conn.Query<EditionFtsMatch>(expansionSql, p2).ToList());
                expansionStopwatch.Stop();
                totalStopwatch.Stop();

                EmitFtsCandidates(trace, "edition_expansion", expandedResults);
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "summary",
                    Step = "edition_expansion",
                    ElapsedMilliseconds = expansionStopwatch.ElapsedMilliseconds,
                    TotalElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds,
                    ResultCount = expandedResults.Count,
                    DistinctBookCount = expandedResults.Select(result => result.BookId).Distinct().Count()
                });
                EmitFtsTrace(trace, new EditionFtsTraceEvent
                {
                    EventType = "completed",
                    Step = "completed",
                    TotalElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds,
                    ResultCount = expandedResults.Count,
                    DistinctBookCount = expandedResults.Select(result => result.BookId).Distinct().Count(),
                    ResultSource = "book_recall_edition_expansion"
                });

                return expandedResults;
            }
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

	    // Typed DTO for SQLite in-memory grouping — avoids dynamic/binder issues
		    internal class ScoredEditionRow
		    {
	        public int EditionId { get; set; }
	        public int BookId { get; set; }
	        public string Lang { get; set; }
	        public string EditionTitle { get; set; }
	        public string BookTitle { get; set; }
	        public int AuthorId { get; set; }
	        public string AuthorName { get; set; }
	        public string NarratorNames { get; set; }
	        public DateTime? ReleaseDate { get; set; }
	        public int? DurationSeconds { get; set; }
	        public double MatchScore { get; set; }
	    }

}
