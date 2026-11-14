using System;
using System.Linq;
using System.Data;
using System.Collections.Generic;
using Newtonsoft.Json;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [NonParallelizable]
    public class ProviderIdentityProjectionMigrationFixture
    {
        [Test]
        public void upgrade_preserves_every_owned_alias_including_pruned_editions_until_refresh([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var author = connection.ExecuteScalar<int>(@"INSERT INTO ""Authors"" (""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"")
                    VALUES ('Fixture Author', 'fixture author', '/fixture', @on, @now) RETURNING ""Id""", new { on = true, now = DateTime.UtcNow });
                var book = connection.ExecuteScalar<int>(@"INSERT INTO ""Books"" (""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""Added"", ""AnyEditionOk"")
                    VALUES (@author, 'Ready Player One', 'ready player one', 0, @now, @on) RETURNING ""Id""", new { author, on = true, now = DateTime.UtcNow });
                connection.Execute(@"INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
                    VALUES ('Book', @book, 'work', 'hc', '26363', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                           ('Book', @book, 'edition', 'hc', '30402322', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                           ('Book', 999999, 'work', 'gr', '999999', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)", new { book });
                var before = connection.Query<string>(@"SELECT ""Scope"" || ':' || ""Provider"" || ':' || ""NormalizedProviderId""
                    FROM ""ProviderAliasIndex"" WHERE ""EntityId"" = @book ORDER BY 1", new { book }).ToArray();
                new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connection.ConnectionString,
                    new MigrationContext(MigrationType.Main, 126), database.DatabaseType);
                Assert.That(connection.Query<string>(@"SELECT ""Scope"" || ':' || ""Provider"" || ':' || ""NormalizedProviderId""
                    FROM ""ProviderAliasIndex"" WHERE ""EntityId"" = @book ORDER BY 1", new { book }), Is.EqualTo(before));
                Assert.That(connection.ExecuteScalar<string>("SELECT \"ProviderIds\" FROM \"Books\" WHERE \"Id\" = @book", new { book }), Does.Contain("30402322"));
                Assert.That(connection.ExecuteScalar<int>("SELECT count(*) FROM \"ProviderAliasIndex\" WHERE \"EntityId\" = 999999"), Is.Zero);
                connection.Execute("UPDATE \"Books\" SET \"ProviderIds\" = @ids WHERE \"Id\" = @book", new { book, ids = "{\"work\":[\"hc:30391354\"]}" });
                Assert.That(connection.Query<string>("SELECT \"NormalizedProviderId\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\" = 'Book' AND \"EntityId\" = @book", new { book }), Is.EqualTo(new[] { "30391354" }));
            }, desiredVersion: 125);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void paged_backfill_matches_released_bytes_and_completed_versions_are_skipped(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                SeedPages(connection, postgres);
                string expected;
                using (var transaction = connection.BeginTransaction())
                {
                    // Released 126 SQL is the independent oracle, including its SQLite ordering.
                    foreach (var entity in new[] { "Author", "Book", "Edition" })
                    {
                        var table = entity + "s";
                        connection.Execute($"ALTER TABLE \"{table}\" ADD COLUMN \"ProviderIds\" TEXT", transaction: transaction);
                        var array = postgres
                            ? "jsonb_agg(\"Provider\" || ':' || \"NormalizedProviderId\" ORDER BY \"Provider\", \"NormalizedProviderId\")"
                            : "json_group_array(\"Provider\" || ':' || \"NormalizedProviderId\")";
                        var obj = postgres ? "jsonb_object_agg(\"Scope\", ids)::text" : "json_group_object(\"Scope\", json(ids))";
                        connection.Execute($@"WITH scopes AS (
                            SELECT ""EntityId"", ""Scope"", {array} AS ids FROM ""ProviderAliasIndex""
                            WHERE ""EntityType"" = '{entity}' GROUP BY ""EntityId"", ""Scope""
                        ), snapshots AS (
                            SELECT ""EntityId"", {obj} AS ids FROM scopes GROUP BY ""EntityId""
                        ) UPDATE ""{table}"" SET ""ProviderIds"" = snapshots.ids FROM snapshots WHERE ""Id"" = snapshots.""EntityId"";
                        DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = '{entity}'
                            AND NOT EXISTS (SELECT 1 FROM ""{table}"" WHERE ""Id"" = ""EntityId"");", transaction: transaction);
                    }
                    expected = Snapshot(connection, transaction);
                    transaction.Rollback();
                }
                Upgrade(connection, database.DatabaseType, 126);
                Assert.That(Snapshot(connection), Is.EqualTo(expected), "Exact JSON text and every retained alias column must match released 126.");
                // An already migrated user's newer snapshot must not be replaced by the backfill.
                connection.Execute(@"UPDATE ""Books"" SET ""ProviderIds"" = '{""work"":[""hc:newer-refresh""]}' WHERE ""Id""=2");
                var refreshed = Snapshot(connection);
                foreach (var version in new[] { 126, 127, 128 })
                {
                    Upgrade(connection, database.DatabaseType, version);
                    var invoked = new List<int>();
                    new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connection.ConnectionString,
                        new MigrationContext(MigrationType.Main, version) { BeforeMigration = migration => invoked.Add(migration.Version) }, database.DatabaseType);
                    Assert.That(invoked, Does.Not.Contain(126).And.Not.Contain(127).And.Not.Contain(128));
                    Assert.That(Snapshot(connection), Is.EqualTo(refreshed));
                }
            }, desiredVersion: 125);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_later_page_rolls_back_columns_alias_cleanup_and_version_then_retries(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                SeedPages(connection, postgres);
                var before = Aliases(connection, null);
                connection.Execute(postgres
                    ? @"CREATE FUNCTION fail_projection_page() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                        IF NEW.""Id"" >= 1802 THEN RAISE EXCEPTION 'fixture later page'; END IF; RETURN NEW; END; $$;
                        CREATE TRIGGER fail_projection_page BEFORE UPDATE ON ""Editions"" FOR EACH ROW EXECUTE FUNCTION fail_projection_page();"
                    : @"CREATE TRIGGER fail_projection_page BEFORE UPDATE ON ""Editions"" WHEN NEW.""Id"" >= 1802
                        BEGIN SELECT RAISE(ABORT, 'fixture later page'); END;");
                var capture = new MigrationRebuildBudgetFixture.Capture();
                var failure = Assert.Catch<Exception>(() => MigrationRebuildBudgetFixture.Upgrade(database, postgres, 126, capture));
                Assert.That(failure.ToString(), Does.Contain("fixture later page"));
                var reads = capture.Commands.Where(command => command.Operation == "ExecuteReader" && command.Sql.Contains("FROM \"Editions\"")).ToList();
                Assert.Multiple(() =>
                {
                    Assert.That(reads, Has.Count.GreaterThanOrEqualTo(3), "Recovery must reach a third page before the injected fault.");
                    Assert.That(reads.Select(command => command.Sql), Has.All.Contains("WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize"));
                    Assert.That(capture.Commands.Count(command => command.Operation == "ExecuteNonQuery" && command.Sql.Contains("UPDATE \"Editions\" SET \"ProviderIds\"")),
                        Is.GreaterThanOrEqualTo(2), "Two page writes must succeed before the fault; the released single statement cannot satisfy this.");
                });
                Assert.That(connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(125));
                Assert.That(Aliases(connection, null), Is.EqualTo(before));
                foreach (var table in new[] { "Authors", "Books", "Editions" })
                {
                    var columns = postgres
                        ? connection.ExecuteScalar<int>("SELECT count(*) FROM information_schema.columns WHERE table_name=@table AND column_name='ProviderIds'", new { table })
                        : connection.ExecuteScalar<int>($"SELECT count(*) FROM pragma_table_info('{table}') WHERE name='ProviderIds'");
                    Assert.That(columns, Is.Zero, table);
                }
                connection.Execute(postgres ? "DROP TRIGGER fail_projection_page ON \"Editions\"; DROP FUNCTION fail_projection_page();" : "DROP TRIGGER fail_projection_page;");
                Upgrade(connection, database.DatabaseType, 126);
                Assert.That(connection.ExecuteScalar<int>("SELECT MAX(\"Version\") FROM \"VersionInfo\""), Is.EqualTo(126));
                Assert.That(connection.ExecuteScalar<int>("SELECT count(*) FROM \"ProviderAliasIndex\" WHERE \"EntityId\" < 0"), Is.Zero);
            }, desiredVersion: 125);
        }

        private static void Upgrade(IDbConnection connection, DatabaseType type, int version) =>
            new MigrationController(LogManager.GetCurrentClassLogger(), null).Migrate(connection.ConnectionString,
                new MigrationContext(MigrationType.Main, version), type);

        private static string Aliases(IDbConnection connection, IDbTransaction transaction) => JsonConvert.SerializeObject(
            connection.Query("SELECT * FROM \"ProviderAliasIndex\" ORDER BY \"Id\"", transaction: transaction));

        private static string Snapshot(IDbConnection connection, IDbTransaction transaction = null) =>
            string.Join("\n", new[] { "Authors", "Books", "Editions" }.Select(table => JsonConvert.SerializeObject(
                connection.Query($"SELECT \"Id\", \"ProviderIds\" FROM \"{table}\" ORDER BY \"Id\"", transaction: transaction)))) + "\n" + Aliases(connection, transaction);

        private static void SeedPages(IDbConnection connection, bool postgres)
        {
            connection.Execute(@"INSERT INTO ""Authors"" (""Name"",""CleanName"",""Path"",""Monitored"",""Added"")
                VALUES ('Projection Author','projection author','/fixture',@Yes,@Now)", new { Yes = true, Now = DateTime.UtcNow });
            var identity = postgres ? "OVERRIDING SYSTEM VALUE" : "";
            connection.Execute($@"WITH RECURSIVE n(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM n WHERE id<951)
                INSERT INTO ""Books"" (""Id"",""AuthorId"",""Title"",""CleanTitle"",""MediaType"",""Added"",""AnyEditionOk"") {identity}
                SELECT id*2,1,'Projection book','projection book',0,@Now,@Yes FROM n;
                WITH RECURSIVE n(id) AS (SELECT 1 UNION ALL SELECT id+1 FROM n WHERE id<951)
                INSERT INTO ""Editions"" (""Id"",""BookId"",""Title"",""Monitored"") {identity}
                SELECT id*2,id*2,'Projection edition',@Yes FROM n", new { Yes = true, Now = DateTime.UtcNow });
            foreach (var entity in new[] { "Author", "Book", "Edition" })
                connection.Execute($@"INSERT INTO ""ProviderAliasIndex"" (""EntityType"",""EntityId"",""Scope"",""Provider"",""NormalizedProviderId"",""CreatedAt"",""UpdatedAt"")
                    SELECT @Entity, ""Id"", 'edition', 'hc', 'edition:' || CAST(""Id"" AS TEXT), @Old, @Old FROM ""{entity}s"" WHERE ""Id"" % 7 <> 0;
                    INSERT INTO ""ProviderAliasIndex"" (""EntityType"",""EntityId"",""Scope"",""Provider"",""NormalizedProviderId"",""CreatedAt"",""UpdatedAt"")
                    SELECT @Entity, ""Id"", 'work', 'hc', CAST(""Id"" AS TEXT), @Old, @Old FROM ""{entity}s"" WHERE ""Id"" % 7 <> 0;
                    WITH RECURSIVE n(id) AS (SELECT -951 UNION ALL SELECT id+1 FROM n WHERE id<0)
                    INSERT INTO ""ProviderAliasIndex"" (""EntityType"",""EntityId"",""Scope"",""Provider"",""NormalizedProviderId"",""CreatedAt"",""UpdatedAt"")
                    SELECT @Entity,id,'work','gr','orphan',@Old,@Old FROM n", new { Entity = entity, Old = new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc) });
        }
    }
}
