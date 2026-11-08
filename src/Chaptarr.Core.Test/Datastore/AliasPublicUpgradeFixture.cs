using System;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class AliasPublicUpgradeFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void public_107_upgrade_should_add_narration_and_backfill_existing_edition_aliases(bool postgres)
        {
            var identity = postgres ? "OVERRIDING SYSTEM VALUE" : string.Empty;
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"qaudit_{Guid.NewGuid():N}.db");
            var name = "qaudit_" + Guid.NewGuid().ToString("N");
            var adminString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
            if (postgres && string.IsNullOrWhiteSpace(adminString)) Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL persistence");
            var connectionString = postgres ? new NpgsqlConnectionStringBuilder(adminString) { Database = name, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            if (postgres) { using var admin = new NpgsqlConnection(adminString); admin.Open(); admin.Execute($"CREATE DATABASE \"{name}\""); }
            IDbConnection Open() { IDbConnection c = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString); c.Open(); return c; }
            try
            {
                var controller = new MigrationController(LogManager.GetCurrentClassLogger(), null);
                var type = postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite;
                controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 107), type);
                using (var c = Open())
                {
                    var cols = postgres ? c.ExecuteScalar<int>("SELECT count(*) FROM information_schema.columns WHERE table_name='ImportListBookIdentityCache' AND column_name IN ('EditionHasNarration', 'EditionNarratorNames')")
                        : c.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('ImportListBookIdentityCache') WHERE name IN ('EditionHasNarration', 'EditionNarratorNames')");
                    Assert.That(cols, Is.Zero);
                    c.Execute($@"INSERT INTO ""Authors"" (""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"") {identity} VALUES (1,'Audit Author','audit author','C:\qaudit',@Yes,@Now)", new { Yes = true, Now = DateTime.UtcNow });
                    c.Execute($@"INSERT INTO ""Books"" (""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""MediaType"", ""Added"", ""AnyEditionOk"") {identity} VALUES (100,1,'Public upgrade book','public upgrade book',0,@Now,@Yes)", new { Now = DateTime.UtcNow, Yes = true });
                    c.Execute($@"INSERT INTO ""Editions"" (""Id"", ""BookId"", ""Title"", ""Monitored"", ""ForeignEditionId"", ""GoodreadsEditionId"", ""Asins"") {identity} VALUES (200,100,'Public upgrade edition',@Yes,'gr:123-audio',456,'[""B00QALIAS1""]')", new { Yes = true });
                    Assert.That(c.ExecuteScalar<int>(@"SELECT count(*) FROM ""ProviderAliasIndex"" WHERE ""EntityType""='Edition'"), Is.Zero);
                }
                controller.Migrate(connectionString, new MigrationContext(MigrationType.Main, 112), type);
                using (var c = Open())
                {
                    var cols = postgres ? c.ExecuteScalar<int>("SELECT count(*) FROM information_schema.columns WHERE table_name='ImportListBookIdentityCache' AND column_name IN ('EditionHasNarration', 'EditionNarratorNames')")
                        : c.ExecuteScalar<int>("SELECT count(*) FROM pragma_table_info('ImportListBookIdentityCache') WHERE name IN ('EditionHasNarration', 'EditionNarratorNames')");
                    Assert.That(cols, Is.EqualTo(2));
                    var aliases = c.Query<string>(@"SELECT ""Provider"" || ':' || ""NormalizedProviderId"" FROM ""ProviderAliasIndex"" WHERE ""EntityType""='Edition' AND ""EntityId""=200 ORDER BY 1").ToArray();
                    Assert.That(aliases, Has.Some.EqualTo("gr:456"));
                    Assert.That(aliases, Has.Some.EqualTo("az:B00QALIAS1"));
                    Assert.That(c.ExecuteScalar<int>(@"SELECT max(""Version"") FROM ""VersionInfo"""), Is.EqualTo(112));
                    TestContext.Progress.WriteLine($"Public107 upgrade {type}: narration columns present; aliases={string.Join(",", aliases)}; max migration112.");
                }
            }
            finally
            {
                if (postgres) { using var admin = new NpgsqlConnection(adminString); admin.Open(); admin.Execute($"DROP DATABASE \"{name}\""); }
                else File.Delete(path);
            }
        }

    }
}
