using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using FluentMigrator;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.MatchBench;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class MatchBenchLiveSchemaFixture
    {
        [TestCase(-1)]
        [TestCase(1)]
        public void live_audit_should_refuse_a_different_schema_without_changing_it(int offset)
        {
            WithDatabase((path, connection, current) =>
            {
                connection.Execute("CREATE TABLE VersionInfo (Version INTEGER NOT NULL); INSERT INTO VersionInfo VALUES (@version)", new { version = current + offset });
                Assert.That(() => Program.ValidateLiveAuditSchema(path), Throws.InvalidOperationException);
                Assert.That(connection.ExecuteScalar<long>("SELECT Version FROM VersionInfo"), Is.EqualTo(current + offset));
                Assert.That(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table'"), Is.EqualTo(1));
            });
        }

        [Test]
        public void live_audit_should_accept_the_current_schema_and_reject_missing_version_history()
        {
            WithDatabase((path, connection, current) =>
            {
                Assert.That(() => Program.ValidateLiveAuditSchema(path), Throws.TypeOf<SqliteException>());
                connection.Execute("CREATE TABLE VersionInfo (Version INTEGER NOT NULL)");
                connection.Execute("INSERT INTO VersionInfo VALUES (@Version)", typeof(NzbDroneMigrationBase).Assembly.GetTypes()
                    .Where(type => type.Namespace == "NzbDrone.Core.Datastore.Migration")
                    .Select(type => type.GetCustomAttribute<MigrationAttribute>()).Where(attribute => attribute != null));
                Assert.That(() => Program.ValidateLiveAuditSchema(path), Throws.Nothing);
                connection.Execute("DELETE FROM VersionInfo WHERE Version = (SELECT MIN(Version) FROM VersionInfo)");
                Assert.That(() => Program.ValidateLiveAuditSchema(path), Throws.InvalidOperationException);
            });
        }

        private static void WithDatabase(Action<string, SqliteConnection, long> run)
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "live-schema-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                connection.Open();
                var current = typeof(NzbDroneMigrationBase).Assembly.GetTypes()
                    .Where(type => type.Namespace == "NzbDrone.Core.Datastore.Migration")
                    .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Version)
                    .Where(version => version.HasValue).Max().Value;
                run(path, connection, current);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
