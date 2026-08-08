using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books.Search
{
    public class PostgresTsvectorBackend : ISearchBackend
    {
        private readonly IMainDatabase _database;
        private readonly Logger _logger;
        private bool? _isAvailable;

        public string BackendType => "PostgreSQL tsvector";

        public PostgresTsvectorBackend(IMainDatabase database, Logger logger)
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
                    // Check for tsvector columns on Authors and Editions tables
                    var sql = @"SELECT COUNT(*)
                                FROM information_schema.columns
                                WHERE table_name IN ('Authors', 'Editions')
                                  AND column_name LIKE '%TsVector'";
                    var count = conn.ExecuteScalar<int>(sql);
                    _isAvailable = count >= 3; // At least 3 tsvector columns

                    if (_isAvailable.Value)
                    {
                        _logger.Debug("PostgreSQL tsvector backend available with generated columns");
                    }
                    else
                    {
                        _logger.Warn("PostgreSQL tsvector backend not available - missing tsvector columns");
                    }

                    return _isAvailable.Value;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error checking PostgreSQL tsvector availability");
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

                // Map field names to tsvector columns
                var fieldToColumn = new Dictionary<string, string>
                {
                    { "author", "NameTsVector" },
                    { "name", "NameTsVector" },
                    { "sortname", "SortNameTsVector" },
                    { "slug", "TitleSlugTsVector" }
                };

                foreach (var field in fieldTerms)
                {
                    if (!fieldToColumn.ContainsKey(field.Key.ToLower()))
                        continue;

                    var tsColumn = fieldToColumn[field.Key.ToLower()];

                    foreach (var term in field.Value)
                    {
                        var tsQuery = BuildPgTsQuery(term);
                        if (!string.IsNullOrWhiteSpace(tsQuery))
                        {
                            searchClauses.Add($@"
                                SELECT 
                                    ""Id"" as author_id,
                                    ""Name"" as author_name,
                                    '{field.Key}' as field_name,
                                    @term{paramIndex} as search_term,
                                    ts_rank(""{tsColumn}"", phraseto_tsquery('english', @tsq{paramIndex})) * 1000 as score
                                FROM ""Authors""
                                WHERE ""{tsColumn}"" @@ phraseto_tsquery('english', @tsq{paramIndex})");
                            
                            parameters.Add($"term{paramIndex}", term);
                            parameters.Add($"tsq{paramIndex}", tsQuery);
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
                            author_name,
                            field_name,
                            search_term,
                            score,
                            ROW_NUMBER() OVER (PARTITION BY author_id, field_name ORDER BY score DESC) as field_rank
                        FROM all_matches
                    ),
                    aggregated AS (
                        SELECT 
                            author_id,
                            author_name,
                            field_name,
                            SUM(1.0 / field_rank) as field_score,
                            STRING_AGG(DISTINCT search_term, ',') as matched_terms
                        FROM field_ranked
                        GROUP BY author_id, author_name, field_name
                    )
                    SELECT 
                        author_id as ""AuthorId"",
                        author_name as ""AuthorName"",
                        STRING_AGG(field_name || ':' || CAST(field_score AS TEXT), ',') as ""FieldScoresStr"",
                        SUM(field_score) as ""TotalScore"",
                        STRING_AGG(DISTINCT matched_terms, ',') as ""MatchedTermsStr""
                    FROM aggregated
                    GROUP BY author_id, author_name
                    ORDER BY ""TotalScore"" DESC
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

                _logger.Debug("PostgreSQL tsvector found {0} authors", results.Count);
                return results;
            }
        }

        public List<EditionSearchResult> SearchEditions(List<int> authorIds, Dictionary<string, List<string>> fieldTerms, BookMediaType? mediaType, int limit)
        {
            if (!IsAvailable() || !authorIds.Any() || !fieldTerms.Any())
            {
                return new List<EditionSearchResult>();
            }

            using (var conn = _database.OpenConnection())
            {
                var parameters = new DynamicParameters();
                var searchClauses = new List<string>();
                int paramIndex = 0;

                // Map field names to tsvector columns  
                var fieldToColumn = new Dictionary<string, string>
                {
                    { "title", "TitleTsVector" },
                    { "subtitle", "SubtitleTsVector" },
                    { "overview", "OverviewTsVector" }
                };

                foreach (var field in fieldTerms)
                {
                    if (!fieldToColumn.ContainsKey(field.Key.ToLower()))
                        continue;

                    var tsColumn = fieldToColumn[field.Key.ToLower()];

                    foreach (var term in field.Value)
                    {
                        var tsQuery = BuildPgTsQuery(term);
                        if (!string.IsNullOrWhiteSpace(tsQuery))
                        {
                            searchClauses.Add($@"
                                SELECT 
                                    e.""Id"" as edition_id,
                                    e.""Title"" as edition_title,
                                    b.""AuthorId"" as author_id,
                                    a.""Name"" as author_name,
                                    '{field.Key}' as field_name,
                                    @term{paramIndex} as search_term,
                                    ts_rank(e.""{tsColumn}"", phraseto_tsquery('english', @tsq{paramIndex})) * 1000 as score
                                FROM ""Editions"" e
                                JOIN ""Books"" b ON b.""Id"" = e.""BookId""
                                JOIN ""Authors"" a ON a.""Id"" = b.""AuthorId""
                                WHERE e.""{tsColumn}"" @@ phraseto_tsquery('english', @tsq{paramIndex})
                                  AND b.""AuthorId"" = ANY(@authorIds)"
                                + (mediaType.HasValue ? @" AND b.""MediaType"" = @mediaType" : ""));
                            
                            parameters.Add($"term{paramIndex}", term);
                            parameters.Add($"tsq{paramIndex}", tsQuery);
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
                            STRING_AGG(DISTINCT search_term, ',') as matched_terms
                        FROM field_ranked
                        GROUP BY edition_id, edition_title, author_id, author_name, field_name
                    )
                    SELECT 
                        edition_id as ""EditionId"",
                        edition_title as ""EditionTitle"",
                        author_id as ""AuthorId"",
                        author_name as ""AuthorName"",
                        STRING_AGG(field_name || ':' || CAST(field_score AS TEXT), ',') as ""FieldScoresStr"",
                        SUM(field_score) as ""TotalScore"",
                        STRING_AGG(DISTINCT matched_terms, ',') as ""MatchedTermsStr""
                    FROM aggregated
                    GROUP BY edition_id, edition_title, author_id, author_name
                    ORDER BY ""TotalScore"" DESC
                    LIMIT @limit";

                parameters.Add("authorIds", authorIds.ToArray());
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

                _logger.Debug("PostgreSQL tsvector found {0} editions for {1} authors", results.Count, authorIds.Count);
                return results;
            }
        }

        private string BuildPgTsQuery(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return string.Empty;
            }

            // Split into tokens and clean each one
            var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Replace("'", "")
                              .Replace("&", " ")
                              .Replace("|", " ")
                              .Replace("!", " ")
                              .Replace("(", " ")
                              .Replace(")", " ")
                              .Replace(":", " ")
                              .Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            if (tokens.Length == 0)
            {
                return string.Empty;
            }

            // Use <-> operator for phrase matching
            return string.Join(" <-> ", tokens);
        }
    }
}