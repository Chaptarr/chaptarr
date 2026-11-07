using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookshelfDatabaseConverterFixture
    {
        private string _directory;
        private string _sourcePath;
        private string _sourceConnectionString;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"bookshelf-converter-{Guid.NewGuid().ToString("N").Substring(0, 8)}");
            Directory.CreateDirectory(_directory);
            _sourcePath = Path.Combine(_directory, "readarr.db");
            _sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _sourcePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString;

            using var source = new SqliteConnection(_sourceConnectionString);
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = @"
                CREATE TABLE VersionInfo (Version INTEGER NOT NULL);
                INSERT INTO VersionInfo VALUES (158);
                CREATE TABLE AuthorMetadata (Id INTEGER PRIMARY KEY, ForeignAuthorId TEXT NOT NULL);
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    AuthorMetadataId INTEGER NOT NULL,
                    EbookPath TEXT,
                    AudiobookPath TEXT,
                    Path TEXT,
                    FOREIGN KEY (AuthorMetadataId) REFERENCES AuthorMetadata(Id));
                CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorMetadataId INTEGER NOT NULL, ForeignBookId TEXT NOT NULL);
                CREATE TABLE Commands (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);
                INSERT INTO AuthorMetadata VALUES (4, 'fixture-author');
                INSERT INTO Authors VALUES (8, 4, '/books/ebook', '/books/audio', '/books');
                INSERT INTO Books VALUES (12, 4, 'fixture-book');
                INSERT INTO Commands VALUES (16, 'stale command');";
            command.ExecuteNonQuery();
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

        private static BookshelfDatabaseImportReport CreateImportReport()
        {
            var report = new BookshelfDatabaseImportReport();
            report.RecordTable("VersionInfo", 1, 0, 0, 0, 1, "Chaptarr migration history is created independently.");
            return report;
        }

        [Test]
        public void should_promote_a_valid_staged_database_and_retain_a_source_backup()
        {
            var converter = new BookshelfDatabaseConverter();
            var result = converter.Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    using var read = source.CreateCommand();
                    read.CommandText = "SELECT Id, Path FROM Authors WHERE Id = 8;";
                    using var reader = read.ExecuteReader();
                    Assert.That(reader.Read(), Is.True);
                    var authorId = reader.GetInt32(0);
                    var authorPath = reader.GetString(1);
                    reader.Close();

                    using var metadata = source.CreateCommand();
                    metadata.CommandText = "SELECT ForeignAuthorId FROM AuthorMetadata WHERE Id = 4;";
                    var foreignAuthorId = metadata.ExecuteScalar()?.ToString();

                    using var insert = target.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = @"
                        INSERT INTO Authors (Id, ForeignAuthorId, Name, Path, AudiobookRootFolderPath, EbookRootFolderPath)
                        VALUES (@id, @foreignAuthorId, 'Fixture Author', @path, @audio, @ebook);
                        INSERT INTO Books (Id, AuthorId, ForeignBookId)
                        VALUES (12, @id, 'fixture-book');";
                    insert.Parameters.AddWithValue("@id", authorId);
                    insert.Parameters.AddWithValue("@foreignAuthorId", foreignAuthorId);
                    insert.Parameters.AddWithValue("@path", authorPath);
                    insert.Parameters.AddWithValue("@audio", "/books/audio");
                    insert.Parameters.AddWithValue("@ebook", "/books/ebook");
                    insert.ExecuteNonQuery();

                    var report = CreateImportReport();
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("Books", 1, 0, 1, 0, 0, "mapped to target Books");
                    report.RecordTable("Commands", 1, 0, 0, 0, 1, "stale queued commands are reset; native startup recreates scheduled work");
                    return report;
                });
            var backupPath = result.BackupPath;

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(backupPath), Is.True);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(ReadAuthorName(_sourcePath), Is.EqualTo("/books"));
                Assert.That(ReadBookAuthorId(_sourcePath), Is.EqualTo(8));
                Assert.That(DatabaseLineageGuard.Detect(ConnectionStringFor(backupPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadBookId(backupPath), Is.EqualTo("fixture-book"));
                Assert.That(Directory.GetFiles(_directory, Path.GetFileName(_sourcePath) + ".bookshelf-snapshot-*"), Is.Empty);
            });
        }

        [Test]
        public void should_preview_a_valid_conversion_without_changing_or_backing_up_the_source()
        {
            var result = new BookshelfDatabaseConverter().Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    var report = CreateImportReport();
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("Books", 1, 0, 1, 0, 0, "mapped to target Books");
                    report.RecordTable("Commands", 1, 0, 0, 0, 1, "stale queued commands are reset");
                    return report;
                },
                previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.True);
                Assert.That(result.BackupPath, Is.Null);
                Assert.That(result.ImportReport.Tables.Count, Is.EqualTo(5));
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadBookId(_sourcePath), Is.EqualTo("fixture-book"));
                Assert.That(Directory.GetFiles(_directory, "*.chaptarr-stage-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-snapshot-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_acquire_a_persistent_lock_without_write_permission()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("Unix file-mode behavior is validated on Linux builds.");
            }

            var lockPath = _sourcePath + ".bookshelfng-conversion.lock";
            File.WriteAllText(lockPath, string.Empty);
            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            var result = new BookshelfDatabaseConverter().Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    var report = CreateImportReport();
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("Books", 1, 0, 1, 0, 0, "mapped to target Books");
                    report.RecordTable("Commands", 1, 0, 0, 0, 1, "stale queued commands are reset");
                    return report;
                },
                previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.True);
                Assert.That((File.GetUnixFileMode(lockPath) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == UnixFileMode.None, Is.True);
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
            });
        }

        [Test]
        public void should_reject_a_second_conversion_while_the_persistent_lock_is_held()
        {
            var lockPath = _sourcePath + ".bookshelfng-conversion.lock";
            using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);

            var exception = Assert.Throws<InvalidOperationException>(() => new BookshelfDatabaseConverter().Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) => CreateImportReport(),
                previewOnly: true));

            Assert.That(exception.Message, Does.Contain("Another BookshelfNG database conversion"));
        }

        [Test]
        public void should_not_promote_when_a_source_table_is_missing_from_the_import_report()
        {
            var converter = new BookshelfDatabaseConverter();

            Assert.Throws<InvalidDataException>(() => converter.Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    var report = CreateImportReport();
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    return report;
                }));

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadBookId(_sourcePath), Is.EqualTo("fixture-book"));
                Assert.That(Directory.GetFiles(_directory, "*.chaptarr-stage-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-snapshot-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_not_promote_when_the_importer_leaves_source_rows_unaccounted_for()
        {
            var converter = new BookshelfDatabaseConverter();

            Assert.Throws<InvalidDataException>(() => converter.Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    var report = CreateImportReport();
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    report.RecordTable("Books", 1, 0, 0, 0, 0, "pending target mapping");
                    report.RecordTable("Commands", 1, 0, 0, 0, 1, "reset stale commands");
                    return report;
                }));

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadBookId(_sourcePath), Is.EqualTo("fixture-book"));
                Assert.That(Directory.GetFiles(_directory, "*.chaptarr-stage-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-snapshot-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelfng-backup-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_report_the_source_and_classified_counts_when_importer_source_count_is_wrong()
        {
            var converter = new BookshelfDatabaseConverter();

            var exception = Assert.Throws<InvalidDataException>(() => converter.Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    var report = CreateImportReport();
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    report.RecordTable("Books", 0, 0, 0, 0, 0, "reported as empty");
                    report.RecordTable("Commands", 1, 0, 0, 0, 1, "reset stale commands");
                    return report;
                }));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("reported source rows 0/1; classified rows 0/1"));
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadBookId(_sourcePath), Is.EqualTo("fixture-book"));
            });
        }

        [Test]
        public void should_leave_the_source_usable_when_import_fails()
        {
            var converter = new BookshelfDatabaseConverter();

            Assert.Throws<InvalidOperationException>(() => converter.Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) => throw new InvalidOperationException("synthetic import failure")));

            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(_sourceConnectionString, DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadBookId(_sourcePath), Is.EqualTo("fixture-book"));
                Assert.That(Directory.GetFiles(_directory, "*.chaptarr-stage-*.db"), Is.Empty);
                Assert.That(Directory.GetFiles(_directory, "*.bookshelf-snapshot-*.db"), Is.Empty);
            });
        }

        [Test]
        public void should_clean_abandoned_conversion_snapshots_before_retry_without_deleting_retained_backups()
        {
            var orphanSnapshot = _sourcePath + ".bookshelf-snapshot-interrupted.db";
            var orphanStage = _sourcePath + ".chaptarr-stage-interrupted.db";
            var orphanMigrationCopy = _sourcePath + ".bookshelf-migrate-interrupted.db";
            var retainedBackup = _sourcePath + ".bookshelfng-backup-retained.db";
            File.WriteAllText(orphanSnapshot, "incomplete snapshot");
            File.WriteAllText(orphanSnapshot + "-wal", "incomplete wal");
            File.WriteAllText(orphanStage, "incomplete target");
            File.WriteAllText(orphanMigrationCopy, "incomplete migrated source");
            File.WriteAllText(retainedBackup, "operator rollback backup");

            string cleanedCount = null;
            var result = new BookshelfDatabaseConverter().Convert(
                _sourceConnectionString,
                CreateNativeTarget,
                (source, target, transaction) =>
                {
                    var report = CreateImportReport();
                    report.RecordTable("AuthorMetadata", 1, 0, 1, 0, 0, "merged into target Authors metadata");
                    report.RecordTable("Authors", 1, 0, 1, 0, 0, "converted to a managed target Author");
                    report.RecordTable("Books", 1, 0, 1, 0, 0, "mapped to target Books");
                    report.RecordTable("Commands", 1, 0, 0, 0, 1, "stale queued commands are reset");
                    return report;
                },
                reportOutcome: (name, value) =>
                {
                    if (name == "orphan-stages-cleaned")
                    {
                        cleanedCount = value;
                    }
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.False);
                Assert.That(cleanedCount, Is.EqualTo("3"));
                Assert.That(File.Exists(orphanSnapshot), Is.False);
                Assert.That(File.Exists(orphanSnapshot + "-wal"), Is.False);
                Assert.That(File.Exists(orphanStage), Is.False);
                Assert.That(File.Exists(orphanMigrationCopy), Is.False);
                Assert.That(File.Exists(retainedBackup), Is.True);
            });
        }

        private static void CreateNativeTarget(string connectionString)
        {
            using var target = new SqliteConnection(connectionString);
            target.Open();
            using var command = target.CreateCommand();
            command.CommandText = @"
                CREATE TABLE Authors (
                    Id INTEGER PRIMARY KEY,
                    ForeignAuthorId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Path TEXT,
                    AudiobookRootFolderPath TEXT,
                    EbookRootFolderPath TEXT);";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE TABLE Books (Id INTEGER PRIMARY KEY, AuthorId INTEGER NOT NULL, ForeignBookId TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        private static string ConnectionStringFor(string databasePath) => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString;

        private static string ReadBookId(string databasePath)
        {
            using var connection = new SqliteConnection(ConnectionStringFor(databasePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ForeignBookId FROM Books WHERE Id = 12;";
            return command.ExecuteScalar()?.ToString();
        }

        private static string ReadAuthorName(string databasePath)
        {
            using var connection = new SqliteConnection(ConnectionStringFor(databasePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Path FROM Authors WHERE Id = 8;";
            return command.ExecuteScalar()?.ToString();
        }

        private static int ReadBookAuthorId(string databasePath)
        {
            using var connection = new SqliteConnection(ConnectionStringFor(databasePath));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT AuthorId FROM Books WHERE Id = 12;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }
}
