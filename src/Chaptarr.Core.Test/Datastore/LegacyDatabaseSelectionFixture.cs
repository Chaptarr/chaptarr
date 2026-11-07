using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class LegacyDatabaseSelectionFixture
    {
        private string _directory;
        private string _primaryPath;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"database-selection-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _primaryPath = Path.Combine(_directory, "chaptarr.db");
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Test]
        public void should_select_the_only_legacy_database_when_primary_has_no_tables()
        {
            CreateEmptyDatabase(_primaryPath);
            var bookshelfPath = Path.Combine(_directory, "readarr.db");
            CreateBookshelfDatabase(bookshelfPath);
            var originalBytes = File.ReadAllBytes(bookshelfPath);

            var selectedPath = ResolveDatabasePath();
            Assert.That(selectedPath, Is.EqualTo(bookshelfPath));
            Assert.Throws<InvalidDataException>(() => DatabaseLineageGuard.Validate(
                new SqliteConnectionStringBuilder { DataSource = selectedPath, Pooling = false }.ConnectionString,
                DatabaseType.SQLite));
            Assert.That(File.ReadAllBytes(bookshelfPath), Is.EqualTo(originalBytes));
        }

        [Test]
        public void should_ignore_an_empty_legacy_file_when_a_populated_sibling_exists()
        {
            File.WriteAllBytes(Path.Combine(_directory, "readarr.db"), Array.Empty<byte>());
            var audioDatabasePath = Path.Combine(_directory, "audioarr.db");
            CreateBookshelfDatabase(audioDatabasePath);

            Assert.That(ResolveDatabasePath(), Is.EqualTo(audioDatabasePath));
        }

        [Test]
        public void should_stop_when_an_initialized_primary_and_a_legacy_database_both_exist()
        {
            CreateChaptarrDatabase(_primaryPath);
            var bookshelfPath = Path.Combine(_directory, "readarr.db");
            CreateBookshelfDatabase(bookshelfPath);
            var primaryBytes = File.ReadAllBytes(_primaryPath);
            var bookshelfBytes = File.ReadAllBytes(bookshelfPath);

            var selectedPath = ResolveDatabasePath();
            var error = Assert.Throws<InvalidDataException>(() => DatabaseLineageGuard.Validate(
                new SqliteConnectionStringBuilder { DataSource = selectedPath, Pooling = false }.ConnectionString,
                DatabaseType.SQLite));

            Assert.Multiple(() =>
            {
                Assert.That(error.Message, Does.Contain("both exist"));
                Assert.That(selectedPath, Is.EqualTo(_primaryPath));
                Assert.That(File.ReadAllBytes(_primaryPath), Is.EqualTo(primaryBytes));
                Assert.That(File.ReadAllBytes(bookshelfPath), Is.EqualTo(bookshelfBytes));
            });
        }

        [Test]
        public void should_select_native_target_for_the_known_dual_source_pair()
        {
            CreateBookshelfDatabase(Path.Combine(_directory, "readarr.db"));
            CreateChaptarrDatabase(Path.Combine(_directory, "audioarr.db"));

            Assert.That(ResolveDatabasePath(), Is.EqualTo(_primaryPath));
            Assert.That(File.Exists(_primaryPath), Is.False, "path selection must not create the native target before migration validation");
        }

        [Test]
        public void should_not_silently_ignore_an_unsupported_legacy_database_file()
        {
            CreateEmptyDatabase(_primaryPath);
            CreateDatabase(Path.Combine(_directory, "readarr.db"),
                "CREATE TABLE UnrelatedData (Value TEXT); INSERT INTO UnrelatedData VALUES ('keep');");

            var selectedPath = ResolveDatabasePath();
            Assert.That(selectedPath, Is.EqualTo(Path.Combine(_directory, "readarr.db")));
            Assert.Throws<InvalidDataException>(() => DatabaseLineageGuard.Validate(
                new SqliteConnectionStringBuilder { DataSource = selectedPath, Pooling = false }.ConnectionString,
                DatabaseType.SQLite));
        }

        [Test]
        public void should_keep_the_configured_path_when_no_legacy_database_exists()
        {
            Assert.That(
                ResolveDatabasePath(),
                Is.EqualTo(_primaryPath));
        }

        [Test]
        public void should_keep_an_initialized_native_database_when_no_legacy_database_exists()
        {
            CreateChaptarrDatabase(_primaryPath);

            Assert.That(ResolveDatabasePath(), Is.EqualTo(_primaryPath));
        }

        [Test]
        public void should_not_open_a_corrupt_primary_during_path_resolution()
        {
            var corruptBytes = new byte[] { 0x43, 0x48, 0x41, 0x50, 0x54, 0x41, 0x52, 0x52 };
            File.WriteAllBytes(_primaryPath, corruptBytes);
            CreateBookshelfDatabase(Path.Combine(_directory, "readarr.db"));

            Assert.Multiple(() =>
            {
                Assert.That(ResolveDatabasePath(), Is.EqualTo(_primaryPath));
                Assert.That(File.ReadAllBytes(_primaryPath), Is.EqualTo(corruptBytes));
            });
        }

        private static void CreateEmptyDatabase(string path)
        {
            using var connection = Open(path);
        }

        private string ResolveDatabasePath() => new TestAppFolderInfo(_directory).GetDatabase();

        private static void CreateBookshelfDatabase(string path)
        {
            CreateDatabase(path, @"
                CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, ForeignAuthorId TEXT);
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    AuthorMetadataId INTEGER,
                    EbookPath TEXT,
                    AudiobookPath TEXT);
                CREATE TABLE Books (
                    Id INTEGER PRIMARY KEY,
                    AuthorMetadataId INTEGER,
                    ForeignBookId TEXT);");
        }

        private static void CreateChaptarrDatabase(string path)
        {
            CreateDatabase(path, @"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    AudiobookRootFolderPath TEXT,
                    EbookRootFolderPath TEXT);");
        }

        private static void CreateDatabase(string path, string sql)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- sql is fixed fixture setup authored in this test.
            command.ExecuteNonQuery();
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString);
            connection.Open();
            return connection;
        }

        private sealed class TestAppFolderInfo : IAppFolderInfo
        {
            public TestAppFolderInfo(string appDataFolder)
            {
                AppDataFolder = appDataFolder;
            }

            public string AppDataFolder { get; }
            public string TempFolder => AppDataFolder;
            public string StartUpFolder => AppDataFolder;
        }
    }
}
