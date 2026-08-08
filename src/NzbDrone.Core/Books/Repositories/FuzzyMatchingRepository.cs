using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books.Repositories
{
    public interface IFuzzyMatchingRepository
    {
        List<Book> SearchBooksWithFts5(int authorId, string searchTerm);
        List<Book> SearchBooksWithTrigrams(int authorId, List<string> trigrams, int minMatches);
        void UpdateBookTrigrams(int bookId, List<string> trigrams, string normalizedTitle);
        void UpdateAuthorTrigrams(int authorId, List<string> trigrams, string normalizedName);
        void PopulateFts5Tables();
    }

    public class FuzzyMatchingRepository : IFuzzyMatchingRepository
    {
        private readonly IMainDatabase _database;
        private readonly DatabaseType _dbType;

        public FuzzyMatchingRepository(IMainDatabase database)
        {
            _database = database;
            _dbType = database.DatabaseType;
        }

        public List<Book> SearchBooksWithFts5(int authorId, string searchTerm)
        {
            using (var connection = _database.OpenConnection())
            {
                try
                {
                    if (_dbType == DatabaseType.PostgreSQL)
                    {
                        // PostgreSQL full-text search
                        var terms = Regex.Matches(searchTerm ?? string.Empty, @"[a-zA-Z0-9]+")
                            .Cast<Match>()
                            .Select(m => m.Value)
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (terms.Count == 0)
                        {
                            return new List<Book>();
                        }

                        var tsQuery = string.Join(" & ", terms.Select(t => t + ":*"));
                        
                        var sql = @"
                            SELECT DISTINCT b.*
                            FROM ""Books"" b
                            WHERE b.""AuthorId"" = @authorId
                            AND to_tsvector('simple', COALESCE(b.""Title"", '') || ' ' || COALESCE(b.""CleanTitle"", '')) 
                                @@ to_tsquery('simple', @tsQuery)
                            LIMIT 50";

                        return connection.Query<Book>(sql, new { authorId, tsQuery }).ToList();
                    }
                    else
                    {
                        // SQLite FTS5
                        var sql = @"
                            SELECT DISTINCT b.*
                            FROM ""Books"" b
                            JOIN book_fts bf ON b.""Id"" = bf.""BookId""
                            WHERE b.""AuthorId"" = @authorId
                            AND bf.Title MATCH @search
                            ORDER BY rank
                            LIMIT 50";

                        return connection.Query<Book>(sql, new { authorId, search = searchTerm }).ToList();
                    }
                }
                catch
                {
                    // FTS not available
                    return new List<Book>();
                }
            }
        }

        public List<Book> SearchBooksWithTrigrams(int authorId, List<string> trigrams, int minMatches)
        {
            using (var connection = _database.OpenConnection())
            {
                // Build the IN clause manually for SQLite compatibility
                var trigramParams = new Dictionary<string, object>();
                var trigramList = new List<string>();
                for (int i = 0; i < trigrams.Count; i++)
                {
                    var paramName = $"trigram{i}";
                    trigramList.Add($"@{paramName}");
                    trigramParams[paramName] = trigrams[i];
                }

                var inClause = string.Join(", ", trigramList);

                var sql = _dbType == DatabaseType.PostgreSQL
                    ? $@"
                        WITH matches AS (
                            SELECT bt.""BookId"" AS ""BookId"", COUNT(DISTINCT bt.""Trigram"") AS ""MatchCount""
                            FROM book_trigrams bt
                            INNER JOIN ""Books"" b ON b.""Id"" = bt.""BookId""
                            WHERE b.""AuthorId"" = @authorId
                              AND bt.""Trigram"" IN ({inClause})
                            GROUP BY bt.""BookId""
                            HAVING COUNT(DISTINCT bt.""Trigram"") >= @minMatches
                        )
                        SELECT b.*, m.""MatchCount"" AS MatchCount
                        FROM matches m
                        INNER JOIN ""Books"" b ON b.""Id"" = m.""BookId""
                        ORDER BY m.""MatchCount"" DESC
                        LIMIT 50"
                    : $@"
                        SELECT DISTINCT b.*, COUNT(DISTINCT bt.""Trigram"") as MatchCount
                        FROM ""Books"" b
                        JOIN book_trigrams bt ON b.""Id"" = bt.""BookId""
                        WHERE b.""AuthorId"" = @authorId
                        AND bt.""Trigram"" IN ({inClause})
                        GROUP BY b.""Id""
                        HAVING COUNT(DISTINCT bt.""Trigram"") >= @minMatches
                        ORDER BY MatchCount DESC
                        LIMIT 50";

                var parameters = new DynamicParameters();
                parameters.Add("authorId", authorId);
                parameters.Add("minMatches", minMatches);
                foreach (var param in trigramParams)
                {
                    parameters.Add(param.Key, param.Value);
                }

                return connection.Query<Book>(sql, parameters).ToList();
            }
        }

        public void UpdateBookTrigrams(int bookId, List<string> trigrams, string normalizedTitle)
        {
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    // Delete existing trigrams
                    connection.Execute("DELETE FROM book_trigrams WHERE \"BookId\" = @bookId", new { bookId }, transaction);
                    connection.Execute("DELETE FROM book_normalized WHERE \"BookId\" = @bookId", new { bookId }, transaction);

                    // Insert new trigrams
                    foreach (var trigram in trigrams)
                    {
                        connection.Execute(
                            "INSERT INTO book_trigrams (\"BookId\", \"Trigram\") VALUES (@bookId, @trigram)",
                            new { bookId, trigram }, transaction);
                    }

                    // Insert normalized title
                    connection.Execute(
                        "INSERT INTO book_normalized (\"BookId\", \"NormalizedTitle\") VALUES (@bookId, @title)",
                        new { bookId, title = normalizedTitle }, transaction);

                    // Update FTS5 if available
                    if (_dbType == DatabaseType.SQLite)
                    {
                        try
                        {
                            connection.Execute("DELETE FROM book_fts WHERE BookId = @bookId", new { bookId }, transaction);

                            var title = connection.QueryFirstOrDefault<string>(
                                "SELECT \"Title\" FROM \"Books\" WHERE \"Id\" = @bookId", new { bookId }, transaction);

                            if (!string.IsNullOrEmpty(title))
                            {
                                connection.Execute(
                                    "INSERT INTO book_fts (BookId, Title) VALUES (@bookId, @title)",
                                    new { bookId, title }, transaction);
                            }
                        }
                        catch
                        {
                            // FTS5 might not be available
                        }
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public void UpdateAuthorTrigrams(int authorId, List<string> trigrams, string normalizedName)
        {
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    // Delete existing trigrams
                    connection.Execute("DELETE FROM author_trigrams WHERE \"AuthorId\" = @authorId", new { authorId }, transaction);
                    connection.Execute("DELETE FROM author_normalized WHERE \"AuthorId\" = @authorId", new { authorId }, transaction);

                    // Insert new trigrams
                    foreach (var trigram in trigrams)
                    {
                        connection.Execute(
                            "INSERT INTO author_trigrams (\"AuthorId\", \"Trigram\") VALUES (@authorId, @trigram)",
                            new { authorId, trigram }, transaction);
                    }

                    // Insert normalized name
                    connection.Execute(
                        "INSERT INTO author_normalized (\"AuthorId\", \"NormalizedName\") VALUES (@authorId, @name)",
                        new { authorId, name = normalizedName }, transaction);

                    // Update FTS5 if available
                    if (_dbType == DatabaseType.SQLite)
                    {
                        try
                        {
                            connection.Execute("DELETE FROM author_name_fts WHERE AuthorId = @authorId", new { authorId }, transaction);

                            var name = connection.QueryFirstOrDefault<string>(
                                "SELECT \"Name\" FROM \"Authors\" WHERE \"Id\" = @authorId",
                                new { authorId }, transaction);

                            if (!string.IsNullOrEmpty(name))
                            {
                                connection.Execute(
                                    "INSERT INTO author_name_fts (AuthorId, Name) VALUES (@authorId, @name)",
                                    new { authorId, name }, transaction);
                            }
                        }
                        catch
                        {
                            // FTS5 might not be available
                        }
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        public void PopulateFts5Tables()
        {
            if (_dbType != DatabaseType.SQLite)
            {
                return;
            }

            using (var connection = _database.OpenConnection())
            {
                try
                {
                    // Populate book_fts table
                    connection.Execute(@"
                        INSERT INTO book_fts (BookId, Title)
                        SELECT b.Id, b.Title
                        FROM ""Books"" b
                        LEFT JOIN book_fts bf ON b.Id = bf.BookId
                        WHERE bf.BookId IS NULL");

                    // Populate author_fts table
                    connection.Execute(@"
                        INSERT INTO author_name_fts (AuthorId, Name)
                        SELECT a.Id, a.Name
                        FROM ""Authors"" a
                        LEFT JOIN author_name_fts af ON a.Id = af.AuthorId
                        WHERE af.AuthorId IS NULL");
                }
                catch
                {
                    // FTS5 might not be available
                }
            }
        }
    }
}
