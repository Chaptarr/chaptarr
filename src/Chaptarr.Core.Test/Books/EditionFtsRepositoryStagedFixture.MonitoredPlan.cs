using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    public partial class EditionFtsRepositoryStagedFixture
    {
        [Test]
        public void monitored_recall_preserves_wanted_selection_scores_and_cutoff(
            [Values(DatabaseType.SQLite, DatabaseType.PostgreSQL)] DatabaseType engine,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values("beacon", "Ready Player 2", "The Beacon")] string query)
        {
            string sql = null;
            DynamicParameters parameters = null;
            void Observe(IDbCommand command)
            {
                if (!command.CommandText.Contains("WITH fts_matches AS MATERIALIZED", StringComparison.Ordinal) &&
                    !command.CommandText.Contains("WITH candidates AS", StringComparison.Ordinal)) return;
                sql = command.CommandText;
                parameters = new DynamicParameters();
                foreach (IDataParameter parameter in command.Parameters)
                    parameters.Add(parameter.ParameterName, parameter.Value);
            }

            WithSeededFtsRepository(engine, (repository, connection) =>
            {
                connection.Execute("UPDATE \"Authors\" SET \"AudiobookMonitored\"=@yes, \"EbookMonitored\"=@yes; " +
                    "UPDATE \"Books\" SET \"AudiobookMonitored\"=@no, \"EbookMonitored\"=@no; " +
                    "INSERT INTO \"Authors\" (\"Id\",\"Name\") VALUES (2,'Paused Author');", new { yes = true, no = false });
                foreach (var id in Enumerable.Range(90, 35).Reverse())
                {
                    connection.Execute("INSERT INTO \"Books\" (\"Id\",\"AuthorId\",\"Title\",\"MediaType\",\"AudiobookMonitored\",\"EbookMonitored\") " +
                        "VALUES (@id,@author,'The Beacon Ready Player Two',@media,@monitored,@monitored); " +
                        "INSERT INTO \"Editions\" (\"Id\",\"BookId\",\"Title\",\"MatchingTitle\",\"Language\",\"Monitored\",\"NarratorNames\",\"Images\") " +
                        "VALUES (@id,@id,'The Beacon Ready Player Two','the beacon ready player two','eng',@selected,'[]','[]');",
                        new { id, author = id == 91 ? 2 : 1, media = id == 92 ? 1 - (int)media : (int)media,
                            monitored = id >= 100 || id is 91 or 92 or 93, selected = id != 93 });
                }
                RefreshProjections(connection);
                if (engine == DatabaseType.SQLite) repository.RebuildIndex();
                var actual = repository.RecallBooks(null, query.Split(' '), media, monitoredOnly: true);
                Assert.That(sql, Is.Not.Null);

                string oldSql;
                if (engine == DatabaseType.SQLite)
                {
                    var plan = connection.Query("EXPLAIN QUERY PLAN " + sql, parameters)
                        .Select(row => (string)row.detail).ToList();
                    Assert.That(plan.Any(row => row.Contains("edition_fts VIRTUAL TABLE INDEX", StringComparison.Ordinal)), Is.True);
                    Assert.That(plan.Any(row => row.Contains("VIRTUAL TABLE INDEX", StringComparison.Ordinal) && row.Contains("=", StringComparison.Ordinal)), Is.False,
                        "FTS must scan MATCH hits, not repeat an equality probe for every monitored edition");
                    if (query != "Ready Player 2")
                        Assert.That(plan.Any(row => row.Contains("SEARCH selected_editions", StringComparison.Ordinal)), Is.False);
                    oldSql = ReplaceBalancedFilter(sql, "AND +edition_fts.rowid IN (", "AND selected_editions.Monitored = 1");
                    oldSql = Regex.Replace(oldSql, @"FROM edition_fts\s+WHERE edition_fts MATCH (?=@(?:ftsQuery|titleAliasQuery))", "FROM edition_fts INNER JOIN Editions selected_editions ON selected_editions.Id = edition_fts.rowid WHERE edition_fts MATCH ");
                }
                else
                {
                    // PostgreSQL production SQL is unchanged: measured inner-branch filtering
                    // did not reliably improve it. Selection/cutoff controls below still apply.
                    oldSql = sql;
                }

                Assert.That(actual.Select(row => row.BookId), Is.EqualTo(Enumerable.Range(100, 20)));

                if (engine == DatabaseType.SQLite)
                {
                    // RecallBooks fetches one extra row to report a cut, then returns the requested 20.
                    var previous = connection.Query<BookFtsMatch>(oldSql, parameters).Take(20).ToList();
                    Assert.That(actual.Select(row => (row.BookId, row.AuthorId, row.MatchScore)),
                        Is.EqualTo(previous.Select(row => (row.BookId, row.AuthorId, row.MatchScore))),
                        "The optimization must retain exact scores, ordering and the tied cutoff");
                }
            }, Observe);
        }

        // Reconstruct only the old filter placement; all scoring, parameters and tie ordering stay identical.
        private static string ReplaceBalancedFilter(string sql, string prefix, string replacement)
        {
            int start;
            while ((start = sql.IndexOf(prefix, StringComparison.Ordinal)) >= 0)
            {
                var cursor = start + prefix.Length;
                var depth = 1;
                while (depth > 0 && cursor < sql.Length)
                {
                    if (sql[cursor] == '(') depth++;
                    if (sql[cursor] == ')') depth--;
                    cursor++;
                }
                Assert.That(depth, Is.Zero);
                sql = sql.Substring(0, start) + replacement + sql.Substring(cursor);
            }
            return sql;
        }
    }
}
