using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class DatabaseLineageGuardFixture
    {
        private string _path;
        private string _walSnapshotPath;
        private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), $"lineage-{Guid.NewGuid():N}.db");
            _walSnapshotPath = _path + ".wal-snapshot";
        }

        [TearDown]
        public void TearDown()
        {
            File.Delete(_path);
            File.Delete(_path + "-wal");
            File.Delete(_path + "-shm");
            File.Delete(_walSnapshotPath);
            File.Delete(_walSnapshotPath + "-wal");
            File.Delete(_walSnapshotPath + "-shm");
        }

        [Test]
        public void should_not_create_a_database_during_preflight()
        {
            DatabaseLineageGuard.Validate(ConnectionString, DatabaseType.SQLite);
            Assert.That(File.Exists(_path), Is.False);
        }

        [Test]
        public void should_accept_an_empty_database()
        {
            Assert.DoesNotThrow(() => DatabaseLineageGuard.Validate(ConnectionString, DatabaseType.SQLite));
            Assert.That(DatabaseLineageGuard.Detect(ConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Empty));
        }

        [TestCase("Path TEXT")]
        [TestCase("AudiobookRootFolderPath TEXT")]
        [TestCase("EbookRootFolderPath TEXT")]
        public void should_reject_foreign_or_partial_schema_without_changing_bytes(string columns)
        {
            Create($"CREATE TABLE Authors (Id INTEGER PRIMARY KEY, {columns}); CREATE TABLE VersionInfo (Version INTEGER); INSERT INTO VersionInfo VALUES (46);");
            var before = File.ReadAllBytes(_path);
            Assert.Throws<InvalidDataException>(() => DatabaseLineageGuard.Validate(ConnectionString, DatabaseType.SQLite));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void should_reject_a_nonempty_database_without_an_authors_table()
        {
            Create("CREATE TABLE UnrelatedData (Value TEXT); INSERT INTO UnrelatedData VALUES ('keep');");
            var before = File.ReadAllBytes(_path);

            Assert.That(DatabaseLineageGuard.Detect(ConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Unsupported));
            Assert.Throws<InvalidDataException>(() => DatabaseLineageGuard.Validate(ConnectionString, DatabaseType.SQLite));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void should_reject_bookshelf_schema_before_numbered_migrations_without_changing_bytes()
        {
            Create(@"
                CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, ForeignAuthorId TEXT);
                CREATE TABLE Authors (Id INTEGER PRIMARY KEY, CleanName TEXT, Path TEXT, EbookPath TEXT, AudiobookPath TEXT, AuthorMetadataId INTEGER);
                CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER, ForeignBookId TEXT, Title TEXT);
                CREATE TABLE VersionInfo (Version INTEGER, AppliedOn TEXT, Description TEXT);
                INSERT INTO VersionInfo VALUES (46, '2026-10-13T00:00:00', 'BookshelfNG source schema');");

            var before = File.ReadAllBytes(_path);
            var migrationController = new MigrationController(LogManager.GetLogger(nameof(DatabaseLineageGuardFixture)), null);

            Assert.That(DatabaseLineageGuard.Detect(ConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));

            Assert.Throws<InvalidDataException>(() => migrationController.Migrate(
                ConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite));

            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void should_recognize_older_bookshelf_schema_before_format_specific_paths_exist()
        {
            Create(@"
                CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, ForeignAuthorId TEXT);
                CREATE TABLE Authors (Id INTEGER PRIMARY KEY, CleanName TEXT, AuthorMetadataId INTEGER);
                CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER, ForeignBookId TEXT);
                CREATE TABLE VersionInfo (Version INTEGER, AppliedOn TEXT, Description TEXT);
                INSERT INTO VersionInfo VALUES (45, '2026-10-13T00:00:00', 'BookshelfNG source schema');");

            Assert.That(DatabaseLineageGuard.Detect(ConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
        }

        [Test]
        public void should_read_bookshelf_schema_from_wal_without_changing_source_bytes()
        {
            var walPath = _path + "-wal";
            using (var initial = new SqliteConnection(ConnectionString))
            {
                initial.Open();
                using var command = initial.CreateCommand();
                command.CommandText = "CREATE TABLE Authors (Id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();
            }

            var safeConnectionString = DatabaseLineageGuard.CreateSafeReadOnlyConnectionString(ConnectionString);
            Assert.That(safeConnectionString, Does.Contain("immutable=1"));
            using (var writer = new SqliteConnection(ConnectionString))
            {
                writer.Open();
                using (var command = writer.CreateCommand())
                {
                    command.CommandText = @"
                        PRAGMA journal_mode=WAL;
                        PRAGMA wal_autocheckpoint=0;
                        CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, ForeignAuthorId TEXT);
                        ALTER TABLE Authors ADD COLUMN CleanName TEXT;
                        ALTER TABLE Authors ADD COLUMN AuthorMetadataId INTEGER;
                        CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER, ForeignBookId TEXT);
                        CREATE TABLE VersionInfo (Version INTEGER, AppliedOn TEXT, Description TEXT);
                        INSERT INTO VersionInfo VALUES (158, '2026-10-13T00:00:00', 'BookshelfNG source schema');";
                    command.ExecuteNonQuery();
                }

                Assert.That(File.Exists(walPath), Is.True);
                Assert.That(new FileInfo(walPath).Length, Is.GreaterThan(0));

                File.Copy(_path, _walSnapshotPath);
                File.Copy(walPath, _walSnapshotPath + "-wal");
                File.Copy(_path + "-shm", _walSnapshotPath + "-shm");
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = _walSnapshotPath, Pooling = false }.ToString();
            var databaseBefore = File.ReadAllBytes(_walSnapshotPath);
            var walBefore = File.ReadAllBytes(_walSnapshotPath + "-wal");

            Assert.That(DatabaseLineageGuard.Detect(safeConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(DatabaseLineageGuard.Detect(connectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(File.ReadAllBytes(_walSnapshotPath), Is.EqualTo(databaseBefore));
            Assert.That(File.ReadAllBytes(_walSnapshotPath + "-wal"), Is.EqualTo(walBefore));
        }

        [Test]
        public void should_inspect_quiescent_database_without_mapping_stale_wal_sidecars()
        {
            Create(@"
                CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, ForeignAuthorId TEXT);
                CREATE TABLE Authors (Id INTEGER PRIMARY KEY, CleanName TEXT, AuthorMetadataId INTEGER);
                CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER, ForeignBookId TEXT);
                CREATE TABLE VersionInfo (Version INTEGER, AppliedOn TEXT, Description TEXT);
                INSERT INTO VersionInfo VALUES (158, '2026-10-13T00:00:00', 'BookshelfNG source schema');");
            File.WriteAllBytes(_path + "-wal", Array.Empty<byte>());
            File.WriteAllBytes(_path + "-shm", new byte[32768]);

            var databaseBefore = File.ReadAllBytes(_path);
            var walBefore = File.ReadAllBytes(_path + "-wal");
            var shmBefore = File.ReadAllBytes(_path + "-shm");
            var safeConnectionString = DatabaseLineageGuard.CreateSafeReadOnlyConnectionString(ConnectionString);

            Assert.That(DatabaseLineageGuard.Detect(ConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(DatabaseLineageGuard.Detect(safeConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(DatabaseLineageGuard.Detect(
                DatabaseLineageGuard.CreateSafeReadOnlyConnectionString(safeConnectionString),
                DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(databaseBefore));
            Assert.That(File.ReadAllBytes(_path + "-wal"), Is.EqualTo(walBefore));
            Assert.That(File.ReadAllBytes(_path + "-shm"), Is.EqualTo(shmBefore));
        }

        [Test]
        public void should_accept_old_chaptarr_schema_without_new_monitoring_columns()
        {
            Create("CREATE TABLE Authors (Id INTEGER PRIMARY KEY, AudiobookRootFolderPath TEXT, EbookRootFolderPath TEXT);");
            Assert.DoesNotThrow(() => DatabaseLineageGuard.Validate(ConnectionString, DatabaseType.SQLite));
        }

        [Test]
        public void should_reject_corrupt_database_without_changing_bytes()
        {
            File.WriteAllText(_path, "not a database");
            Assert.Throws<SqliteException>(() => DatabaseLineageGuard.Validate(ConnectionString, DatabaseType.SQLite));
            Assert.That(File.ReadAllText(_path), Is.EqualTo("not a database"));
        }

        private void Create(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- sql is fixed fixture setup authored in this test.
            command.ExecuteNonQuery();
        }
    }
}
