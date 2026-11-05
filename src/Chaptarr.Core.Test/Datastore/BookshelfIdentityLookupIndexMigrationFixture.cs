using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookshelfIdentityLookupIndexMigrationFixture
    {
        [TestCase("SourceId", "Book", 31)]
        [TestCase("TargetId", "bOoK", 100031)]
        [TestCase("SourceId", "bookfile", 30)]
        [TestCase("TargetId", "BookFile", 100030)]
        public void should_seek_the_case_insensitive_entity_and_requested_identity(string column, string entity, int id)
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "bookshelf_lookup_index_" + Guid.NewGuid().ToString("N") + ".db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                var migrationController = new MigrationController(LogManager.GetLogger(nameof(BookshelfIdentityLookupIndexMigrationFixture)), null);
                migrationController.Migrate(connectionString, new MigrationContext(MigrationType.Main, 125), DatabaseType.SQLite);
                connection.Execute(@"
WITH RECURSIVE ids(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM ids WHERE i<20000)
INSERT INTO BookshelfLegacyIdentityMap (Source,EntityType,SourceId,TargetId,MediaType) SELECT 'BookshelfNG', CASE WHEN i%2=0 THEN 'BookFile' ELSE 'bOoK' END, i, i+100000, 0 FROM ids;");
                var before = connection.QuerySingle<int>("SELECT COUNT(*) FROM BookshelfLegacyIdentityMap");
                migrationController.Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

                // These are the lookup predicates used by the identity-map service.
                var query = $@"SELECT Source, EntityType, SourceId, TargetId, MediaType
FROM BookshelfLegacyIdentityMap
WHERE Source=@source AND LOWER(EntityType)=LOWER(@entity) AND {column} IN (@id)";
                var parameters = new { source = "BookshelfNG", entity, id };
                var rows = connection.Query(query, parameters).AsList();
                var plan = string.Join("\n", connection.Query<PlanRow>("EXPLAIN QUERY PLAN " + query, parameters).AsList().ConvertAll(row => row.Detail));
                var expected = column == "SourceId" ? "IX_BookshelfLegacyIdentityMap_SourceCaseLookup" : "IX_BookshelfLegacyIdentityMap_TargetCaseLookup";
                Assert.Multiple(() =>
                {
                    Assert.That(rows.Count, Is.EqualTo(1));
                    Assert.That(plan, Does.Contain(expected));
                    Assert.That(plan, Does.Contain(column + "=?"));
                    Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM BookshelfLegacyIdentityMap"), Is.EqualTo(before));
                });
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private sealed class PlanRow
        {
            public string Detail { get; set; }
        }
    }
}
