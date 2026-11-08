using System.Diagnostics;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(120)]
    public class fold_header_author_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfDatabase("sqlite").Execute.WithConnection((connection, transaction) =>
            {
                var stopwatch = Stopwatch.StartNew();
                const string insert = @"INSERT INTO author_fts(rowid, SearchName)
                    SELECT Id, COALESCE(SearchName, '') || ' ' || replace(COALESCE(SearchName, ''), '.', '') || ' ' || COALESCE(TitleSlug, '') FROM Authors";
                connection.Execute(@"
                    DROP TRIGGER IF EXISTS author_fts_ai;
                    DROP TRIGGER IF EXISTS author_fts_ad;
                    DROP TRIGGER IF EXISTS author_fts_au;
                    DROP TABLE IF EXISTS author_fts;
                    CREATE VIRTUAL TABLE author_fts USING fts5(SearchName, content='', contentless_delete=1,
                        tokenize='unicode61 remove_diacritics 1');", transaction: transaction);
                connection.Execute(insert, transaction: transaction);
                connection.Execute($@"
                    CREATE TRIGGER author_fts_ai AFTER INSERT ON Authors BEGIN
                        {insert} WHERE Id = new.Id;
                    END;
                    CREATE TRIGGER author_fts_ad AFTER DELETE ON Authors BEGIN
                        DELETE FROM author_fts WHERE rowid = old.Id;
                    END;
                    CREATE TRIGGER author_fts_au AFTER UPDATE ON Authors BEGIN
                        DELETE FROM author_fts WHERE rowid = old.Id;
                        {insert} WHERE Id = new.Id;
                    END;", transaction: transaction);
                var count = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM author_fts", transaction: transaction);
                _logger.Info("Rebuilt header author search index: {0} entries in {1} ms", count, stopwatch.ElapsedMilliseconds);
            });
        }
    }
}
