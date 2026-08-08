using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books.Search
{
    public class SqliteFts5Backend : ISearchBackend
    {
        private readonly IMainDatabase _database;
        private readonly Logger _logger;
        private bool? _isAvailable;

        public string BackendType => "SQLite FTS5";

        public SqliteFts5Backend(IMainDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public bool IsAvailable()
        {
            if (_isAvailable.HasValue)
            {
                return _isAvailable.Value;
            }

            using (var conn = _database.OpenConnection())
            {
                try
                {
                    // Check for author_fts and edition_fts tables
                    var sql = @"SELECT COUNT(*) FROM sqlite_master 
                                WHERE type='table' AND name IN ('author_fts', 'edition_fts')";
                    var count = conn.ExecuteScalar<int>(sql);
                    _isAvailable = count == 2;

                    if (_isAvailable.Value)
                    {
                        _logger.Debug("SQLite FTS5 backend available with author_fts and edition_fts tables");
                    }
                    else
                    {
                        _logger.Warn("SQLite FTS5 backend not available - missing FTS tables");
                    }

                    return _isAvailable.Value;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error checking SQLite FTS5 availability");
                    _isAvailable = false;
                    return false;
                }
            }
        }

        public List<AuthorSearchResult> SearchAuthors(Dictionary<string, List<string>> fieldTerms, int limit)
        {
            if (!IsAvailable() || !fieldTerms.Any())
            {
                return new List<AuthorSearchResult>();
            }

            using (var conn = _database.OpenConnection())
            {
                var parameters = new DynamicParameters();
                var searchClauses = new List<string>();
                int paramIndex = 0;

                foreach (var field in fieldTerms)
                {
                    foreach (var term in field.Value)
                    {
                        var normalized = NormalizeForSearch(term);
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            var ftsQuery = BuildFtsQuery(normalized);
                            searchClauses.Add($@"
                                SELECT 
                                    author_id,
                                    '{field.Key}' as field_name,
                                    @term{paramIndex} as search_term,
                                    CASE 
                                        WHEN highlight(author_fts, 0, '', '') = @term{paramIndex} THEN 1000.0
                                        ELSE bm25(author_fts) * 100.0
                                    END as score
                                FROM author_fts
                                WHERE author_fts MATCH @fts{paramIndex}");
                            
                            parameters.Add($"term{paramIndex}", normalized);
                            parameters.Add($"fts{paramIndex}", ftsQuery);
                            paramIndex++;
                        }
                    }
                }

                if (!searchClauses.Any())
                {
                    return new List<AuthorSearchResult>();
                }

                var sql = $@"
                    WITH all_matches AS (
                        {string.Join(" UNION ALL ", searchClauses)}
                    ),
                    field_ranked AS (
                        SELECT 
                            author_id,
                            field_name,
                            search_term,
                            score,
                            ROW_NUMBER() OVER (PARTITION BY author_id, field_name ORDER BY score DESC) as field_rank
                        FROM all_matches
                    ),
                    aggregated AS (
                        SELECT 
                            fr.author_id,
                            a.Name as author_name,
                            fr.field_name,
                            SUM(1.0 / fr.field_rank) as field_score,
                            GROUP_CONCAT(DISTINCT fr.search_term) as matched_terms
                        FROM field_ranked fr
                        JOIN Authors a ON a.Id = fr.author_id
                        GROUP BY fr.author_id, a.Name, fr.field_name
                    )
                    SELECT 
                        author_id as AuthorId,
                        author_name as AuthorName,
                        GROUP_CONCAT(field_name || ':' || CAST(field_score AS TEXT)) as FieldScoresStr,
                        SUM(field_score) as TotalScore,
                        GROUP_CONCAT(DISTINCT matched_terms) as MatchedTermsStr
                    FROM aggregated
                    GROUP BY author_id, author_name
                    ORDER BY TotalScore DESC
                    LIMIT @limit";

                parameters.Add("limit", limit);

                var results = conn.Query(sql, parameters).Select(row => 
                {
                    var result = new AuthorSearchResult
                    {
                        AuthorId = row.AuthorId,
                        AuthorName = row.AuthorName,
                        TotalScore = row.TotalScore
                    };

                    // Parse field scores
                    if (!string.IsNullOrEmpty(row.FieldScoresStr))
                    {
                        foreach (var fieldScore in ((string)row.FieldScoresStr).Split(','))
                        {
                            var parts = fieldScore.Split(':');
                            if (parts.Length == 2 && double.TryParse(parts[1], out var score))
                            {
                                result.FieldScores[parts[0]] = score;
                            }
                        }
                    }

                    // Parse matched terms
                    if (!string.IsNullOrEmpty(row.MatchedTermsStr))
                    {
                        result.MatchedTerms.AddRange(((string)row.MatchedTermsStr).Split(','));
                    }

                    return result;
                }).ToList();

                _logger.Debug("SQLite FTS5 found {0} authors", results.Count);
                return results;
            }
        }

	        public List<EditionSearchResult> SearchEditions(List<int> authorIds, Dictionary<string, List<string>> fieldTerms, BookMediaType? mediaType, int limit)
	        {
	            if (!IsAvailable() || !authorIds.Any() || !fieldTerms.Any())
	            {
	                return new List<EditionSearchResult>();
	            }

	            // SQLite has a default ~999 bind-variable limit and Dapper expands IN lists into many parameters.
	            // Chunk by authorIds and merge results to keep queries safe on large libraries.
	            if (_database.DatabaseType == DatabaseType.SQLite && authorIds.Distinct().Count() > SqliteVariableLimit.MaxParameters)
	            {
	                var merged = new List<EditionSearchResult>();
	                var distinctAuthorIds = authorIds.Distinct().ToArray();
	                foreach (var batch in distinctAuthorIds.Chunk(SqliteVariableLimit.MaxParameters))
	                {
	                    merged.AddRange(SearchEditions(batch.ToList(), fieldTerms, mediaType, limit));
	                }

	                return merged
	                    .DistinctBy(r => r.EditionId)
	                    .OrderByDescending(r => r.TotalScore)
	                    .Take(limit)
	                    .ToList();
	            }

	            using (var conn = _database.OpenConnection())
	            {
	                var parameters = new DynamicParameters();
	                var searchClauses = new List<string>();
                int paramIndex = 0;

                // Build search clauses for each field/term combination
                foreach (var field in fieldTerms)
                {
                    foreach (var term in field.Value)
                    {
                        var normalized = NormalizeForSearch(term);
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            var ftsQuery = BuildFtsQuery(normalized);
                            searchClauses.Add($@"
                                SELECT 
                                    e.Id as edition_id,
                                    e.Title as edition_title,
                                    b.AuthorId as author_id,
                                    a.Name as author_name,
                                    '{field.Key}' as field_name,
                                    @term{paramIndex} as search_term,
                                    CASE 
                                        WHEN highlight(edition_fts, 0, '', '') = @term{paramIndex} THEN 1000.0
                                        ELSE bm25(edition_fts) * 100.0
                                    END as score
                                FROM edition_fts
                                JOIN Editions e ON e.Id = edition_fts.edition_id
                                JOIN Books b ON b.Id = e.BookId
                                JOIN Authors a ON a.Id = b.AuthorId
                                WHERE edition_fts MATCH @fts{paramIndex}
                                  AND b.AuthorId IN @authorIds"
                                + (mediaType.HasValue ? " AND b.MediaType = @mediaType" : ""));
                            
                            parameters.Add($"term{paramIndex}", normalized);
                            parameters.Add($"fts{paramIndex}", ftsQuery);
                            paramIndex++;
                        }
                    }
                }

                if (!searchClauses.Any())
                {
                    return new List<EditionSearchResult>();
                }

                var sql = $@"
                    WITH all_matches AS (
                        {string.Join(" UNION ALL ", searchClauses)}
                    ),
                    field_ranked AS (
                        SELECT 
                            edition_id,
                            edition_title,
                            author_id,
                            author_name,
                            field_name,
                            search_term,
                            score,
                            ROW_NUMBER() OVER (PARTITION BY edition_id, field_name ORDER BY score DESC) as field_rank
                        FROM all_matches
                    ),
                    aggregated AS (
                        SELECT 
                            edition_id,
                            edition_title,
                            author_id,
                            author_name,
                            field_name,
                            SUM(1.0 / field_rank) as field_score,
                            GROUP_CONCAT(DISTINCT search_term) as matched_terms
                        FROM field_ranked
                        GROUP BY edition_id, edition_title, author_id, author_name, field_name
                    )
                    SELECT 
                        edition_id as EditionId,
                        edition_title as EditionTitle,
                        author_id as AuthorId,
                        author_name as AuthorName,
                        GROUP_CONCAT(field_name || ':' || CAST(field_score AS TEXT)) as FieldScoresStr,
                        SUM(field_score) as TotalScore,
                        GROUP_CONCAT(DISTINCT matched_terms) as MatchedTermsStr
                    FROM aggregated
                    GROUP BY edition_id, edition_title, author_id, author_name
                    ORDER BY TotalScore DESC
                    LIMIT @limit";

                parameters.Add("authorIds", authorIds);
                parameters.Add("limit", limit);
                if (mediaType.HasValue)
                {
                    parameters.Add("mediaType", (int)mediaType.Value);
                }

                var results = conn.Query(sql, parameters).Select(row => 
                {
                    var result = new EditionSearchResult
                    {
                        EditionId = row.EditionId,
                        EditionTitle = row.EditionTitle,
                        AuthorId = row.AuthorId,
                        AuthorName = row.AuthorName,
                        TotalScore = row.TotalScore
                    };

                    // Parse field scores
                    if (!string.IsNullOrEmpty(row.FieldScoresStr))
                    {
                        foreach (var fieldScore in ((string)row.FieldScoresStr).Split(','))
                        {
                            var parts = fieldScore.Split(':');
                            if (parts.Length == 2 && double.TryParse(parts[1], out var score))
                            {
                                result.FieldScores[parts[0]] = score;
                            }
                        }
                    }

                    // Parse matched terms
                    if (!string.IsNullOrEmpty(row.MatchedTermsStr))
                    {
                        result.MatchedTerms.AddRange(((string)row.MatchedTermsStr).Split(','));
                    }

                    return result;
                }).ToList();

                _logger.Debug("SQLite FTS5 found {0} editions for {1} authors", results.Count, authorIds.Count);
                return results;
            }
        }

        private string NormalizeForSearch(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Convert to lowercase
            var normalized = text.ToLowerInvariant();

            // Preserve punctuation that FTS tokenizer handles (apostrophes, hyphens, periods)
            // Only normalize separators
            normalized = normalized.Replace(",", " ");
            normalized = normalized.Replace(":", " ");
            normalized = normalized.Replace(";", " ");
            normalized = normalized.Replace("(", " ");
            normalized = normalized.Replace(")", " ");
            normalized = normalized.Replace("[", " ");
            normalized = normalized.Replace("]", " ");
            normalized = normalized.Replace("/", " ");
            normalized = normalized.Replace("\\", " ");

            // Remove extra spaces
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");

            return normalized.Trim();
        }

        private string BuildFtsQuery(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return string.Empty;
            }

            // Split into tokens for multi-word handling
            var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (tokens.Length == 1)
            {
                // Single token: escape quotes and wrap in quotes
                var escaped = tokens[0].Replace("\"", "\"\"");
                return $"\"{escaped}\"";
            }
            else
            {
                // Multiple tokens: use NEAR syntax
                // NEAR("stephen","king",1) not NEAR("stephen king",1)
                var escapedTokens = tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\"");
                return $"NEAR({string.Join(",", escapedTokens)},1)";
            }
        }
    }
}
