using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class AuthorMediaMonitoringMigrationFixture
    {
        private static readonly LegacyMonitoringCase[] Cases =
        {
            new(null, null, null, null),
            new(null, false, null, null),
            new(null, true, true, NewItemMonitorTypes.New),
            new(0, null, false, NewItemMonitorTypes.None),
            new(0, false, false, NewItemMonitorTypes.None),
            new(0, true, true, NewItemMonitorTypes.New),
            new(1, null, true, NewItemMonitorTypes.All),
            new(1, false, true, NewItemMonitorTypes.All),
            new(1, true, true, NewItemMonitorTypes.All),
            new(2, null, true, NewItemMonitorTypes.None),
            new(2, false, true, NewItemMonitorTypes.None),
            new(2, true, true, NewItemMonitorTypes.New)
        };

        [Test]
        public void should_apply_legacy_gate_and_new_item_policy_to_authors_and_pending_imports()
        {
            WithDatabase((connection, connectionString) =>
            {
                for (var index = 0; index < Cases.Length; index++)
                {
                    var testCase = Cases[index];
                    connection.Execute(@"
                        INSERT INTO ""Authors"" (""Id"", ""Monitored"", ""AudiobookMonitorExisting"", ""AudiobookMonitorFuture"", ""EbookMonitorExisting"", ""EbookMonitorFuture"")
                        VALUES (@Id, FALSE, @Existing, @Future, @Existing, @Future);

                        INSERT INTO ""PendingAuthorImport"" (""Id"", ""AudiobookMonitorExisting"", ""AudiobookMonitorFuture"", ""EbookMonitorExisting"", ""EbookMonitorFuture"")
                        VALUES (@Id, @Existing, @Future, @Existing, @Future);",
                        new
                        {
                            Id = index + 1,
                            Existing = testCase.Existing,
                            Future = testCase.Future
                        });
                }

                ApplyMigration(connectionString);

                var authors = connection.Query<MonitoringProjection>(@"
                    SELECT ""Id"", ""Monitored"", ""AudiobookMonitored"", ""AudiobookMonitorNewItems"", ""EbookMonitored"", ""EbookMonitorNewItems""
                    FROM ""Authors"" ORDER BY ""Id"";").ToList();
                var pending = connection.Query<PendingMonitoringProjection>(@"
                    SELECT ""Id"", ""AudiobookMonitored"", ""AudiobookMonitorNewItems"", ""AudiobookMonitorExistingMode"",
                           ""EbookMonitored"", ""EbookMonitorNewItems"", ""EbookMonitorExistingMode""
                    FROM ""PendingAuthorImport"" ORDER BY ""Id"";").ToList();

                Assert.That(authors, Has.Count.EqualTo(Cases.Length));
                Assert.That(pending, Has.Count.EqualTo(Cases.Length));

                for (var index = 0; index < Cases.Length; index++)
                {
                    var testCase = Cases[index];
                    var author = authors[index];
                    var pendingImport = pending[index];

                    Assert.That(author.AudiobookMonitored, Is.EqualTo(testCase.Gate), $"author audio gate case {index}");
                    Assert.That(author.EbookMonitored, Is.EqualTo(testCase.Gate), $"author ebook gate case {index}");
                    Assert.That(author.Monitored, Is.EqualTo(testCase.Gate == true), $"author legacy projection case {index}");
                    Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo((int?)testCase.Policy), $"author audio policy case {index}");
                    Assert.That(author.EbookMonitorNewItems, Is.EqualTo((int?)testCase.Policy), $"author ebook policy case {index}");

                    Assert.That(pendingImport.AudiobookMonitored, Is.EqualTo(testCase.Gate), $"pending audio gate case {index}");
                    Assert.That(pendingImport.EbookMonitored, Is.EqualTo(testCase.Gate), $"pending ebook gate case {index}");
                    Assert.That(pendingImport.AudiobookMonitorNewItems, Is.EqualTo((int?)testCase.Policy), $"pending audio policy case {index}");
                    Assert.That(pendingImport.EbookMonitorNewItems, Is.EqualTo((int?)testCase.Policy), $"pending ebook policy case {index}");

                    var expectedInitialMode = testCase.Existing switch
                    {
                        0 => (int?)MonitorTypes.None,
                        1 => (int?)MonitorTypes.All,
                        2 => (int?)MonitorTypes.SpecificBook,
                        _ => null
                    };
                    Assert.That(pendingImport.AudiobookMonitorExistingMode, Is.EqualTo(expectedInitialMode), $"pending audio initial mode case {index}");
                    Assert.That(pendingImport.EbookMonitorExistingMode, Is.EqualTo(expectedInitialMode), $"pending ebook initial mode case {index}");
                }

                Assert.That(HasColumn(connection, "Authors", "AudiobookMonitorExisting"), Is.False);
                Assert.That(HasColumn(connection, "Authors", "AudiobookMonitorFuture"), Is.False);
                Assert.That(HasColumn(connection, "PendingAuthorImport", "AudiobookMonitorExisting"), Is.False);
                Assert.That(HasColumn(connection, "PendingAuthorImport", "AudiobookMonitorFuture"), Is.False);
            });
        }

        private static void ApplyMigration(string connectionString)
        {
            var migrationController = new MigrationController(LogManager.GetLogger("AuthorMediaMonitoringMigrationFixture"), null);
            migrationController.Migrate(
                connectionString,
                new MigrationContext(MigrationType.Main, 104),
                DatabaseType.SQLite);
        }

        private static bool HasColumn(SqliteConnection connection, string table, string column)
        {
            return connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM pragma_table_info(@Table) WHERE name = @Column;",
                new { Table = table, Column = column }) == 1;
        }

        private static void WithDatabase(Action<SqliteConnection, string> test)
        {
            var databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"author_media_monitoring_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                CreateSchema(connection);
                test(connection, connectionString);
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }

        private static void CreateSchema(SqliteConnection connection)
        {
            connection.Execute(@"
                CREATE TABLE ""VersionInfo"" (
                    ""Version"" INTEGER PRIMARY KEY,
                    ""AppliedOn"" TEXT NULL,
                    ""Description"" TEXT NULL
                );
                WITH RECURSIVE versions(version) AS
                (
                    SELECT 1
                    UNION ALL
                    SELECT version + 1 FROM versions WHERE version < 103
                )
                INSERT INTO ""VersionInfo"" (""Version"", ""AppliedOn"", ""Description"")
                SELECT version, CURRENT_TIMESTAMP, 'test baseline' FROM versions;

                CREATE TABLE ""Authors"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""Monitored"" INTEGER NOT NULL,
                    ""AudiobookMonitorExisting"" INTEGER NULL,
                    ""AudiobookMonitorFuture"" INTEGER NULL,
                    ""EbookMonitorExisting"" INTEGER NULL,
                    ""EbookMonitorFuture"" INTEGER NULL
                );

                CREATE TABLE ""PendingAuthorImport"" (
                    ""Id"" INTEGER PRIMARY KEY,
                    ""AudiobookMonitorExisting"" INTEGER NULL,
                    ""AudiobookMonitorFuture"" INTEGER NULL,
                    ""EbookMonitorExisting"" INTEGER NULL,
                    ""EbookMonitorFuture"" INTEGER NULL
                );
            ");
        }

        private sealed class LegacyMonitoringCase
        {
            public LegacyMonitoringCase(int? existing, bool? future, bool? gate, NewItemMonitorTypes? policy)
            {
                Existing = existing;
                Future = future;
                Gate = gate;
                Policy = policy;
            }

            public int? Existing { get; }
            public bool? Future { get; }
            public bool? Gate { get; }
            public NewItemMonitorTypes? Policy { get; }
        }

        private sealed class MonitoringProjection
        {
            public int Id { get; set; }
            public bool Monitored { get; set; }
            public bool? AudiobookMonitored { get; set; }
            public int? AudiobookMonitorNewItems { get; set; }
            public bool? EbookMonitored { get; set; }
            public int? EbookMonitorNewItems { get; set; }
        }

        private sealed class PendingMonitoringProjection
        {
            public int Id { get; set; }
            public bool? AudiobookMonitored { get; set; }
            public int? AudiobookMonitorNewItems { get; set; }
            public int? AudiobookMonitorExistingMode { get; set; }
            public bool? EbookMonitored { get; set; }
            public int? EbookMonitorNewItems { get; set; }
            public int? EbookMonitorExistingMode { get; set; }
        }
    }
}
