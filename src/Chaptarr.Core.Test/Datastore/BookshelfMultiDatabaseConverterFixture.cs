using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class BookshelfMultiDatabaseConverterFixture
    {
        private string _appDataDirectory;
        private string _readarrPath;
        private string _audioarrPath;
        private string _previousHardcover;
        private string _previousHardcoverNative;
        private string _previousBookshelfUrlBase;

        [SetUp]
        public void SetUp()
        {
            _previousHardcover = Environment.GetEnvironmentVariable("HARDCOVER");
            _previousHardcoverNative = Environment.GetEnvironmentVariable("HARDCOVER_NATIVE");
            _previousBookshelfUrlBase = Environment.GetEnvironmentVariable("BOOKSHELF_URLBASE");
            // The populated fixture uses BookshelfNG's legacy Goodreads
            // interpretation for bare numeric provider IDs. State it explicitly
            // because migration now rejects an ambiguous, unset provider mode.
            Environment.SetEnvironmentVariable("HARDCOVER", "false");
            Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", null);
            Environment.SetEnvironmentVariable("BOOKSHELF_URLBASE", null);

            _appDataDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bookshelf-multi-database-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_appDataDirectory);
            _readarrPath = Path.Combine(_appDataDirectory, "readarr.db");
            _audioarrPath = Path.Combine(_appDataDirectory, "audioarr.db");
            CreatePopulatedBookshelfDatabase(_readarrPath);
            File.Copy(_readarrPath, _audioarrPath);
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_appDataDirectory))
            {
                Directory.Delete(_appDataDirectory, recursive: true);
            }

            Environment.SetEnvironmentVariable("HARDCOVER", _previousHardcover);
            Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", _previousHardcoverNative);
            Environment.SetEnvironmentVariable("BOOKSHELF_URLBASE", _previousBookshelfUrlBase);
        }

        [Test]
        public void should_merge_identical_populated_sources_and_keep_both_original_databases()
        {
            var readarrFingerprint = Fingerprint(_readarrPath);
            var audioarrFingerprint = Fingerprint(_audioarrPath);
            var sourceAuthorCount = CountRows(_readarrPath, "Authors");

            var result = RunMerge(previewOnly: false);

            var nativePath = Path.Combine(_appDataDirectory, "chaptarr.db");
            var retainedBackup = result.BackupPath;
            var receiptPath = Path.Combine(_appDataDirectory, "bookshelfng-merge.receipt");

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.False);
                Assert.That(File.Exists(nativePath), Is.True);
                Assert.That(DatabaseLineageGuard.Detect(ConnectionString(nativePath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(DatabaseLineageGuard.Detect(ConnectionString(_readarrPath), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(Fingerprint(_readarrPath), Is.EqualTo(readarrFingerprint));
                Assert.That(Fingerprint(_audioarrPath), Is.EqualTo(audioarrFingerprint));
                Assert.That(CountRows(nativePath, "Authors"), Is.EqualTo(sourceAuthorCount), "identical rows from the secondary database must not be duplicated");
                Assert.That(File.Exists(retainedBackup), Is.True);
                Assert.That(DatabaseLineageGuard.Detect(ConnectionString(retainedBackup), DatabaseType.SQLite), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(File.ReadAllText(receiptPath), Does.Contain("complete"));
                Assert.That(Directory.GetDirectories(_appDataDirectory, ".chaptarr-bookshelf-merge-*"), Is.Empty);
            });
        }

        [Test]
        public void should_preview_the_merge_without_changing_either_source_or_promoting_a_target()
        {
            var readarrFingerprint = Fingerprint(_readarrPath);
            var audioarrFingerprint = Fingerprint(_audioarrPath);

            var result = RunMerge(previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.PreviewOnly, Is.True);
                Assert.That(result.BackupPath, Is.Null);
                Assert.That(Fingerprint(_readarrPath), Is.EqualTo(readarrFingerprint));
                Assert.That(Fingerprint(_audioarrPath), Is.EqualTo(audioarrFingerprint));
                Assert.That(File.Exists(Path.Combine(_appDataDirectory, "chaptarr.db")), Is.False);
                Assert.That(File.Exists(Path.Combine(_appDataDirectory, "bookshelfng-merge.receipt")), Is.False);
                Assert.That(Directory.GetFiles(_appDataDirectory, "*.bookshelfng-backup-*.db"), Is.Empty);
                Assert.That(Directory.GetDirectories(_appDataDirectory, ".chaptarr-bookshelf-merge-*"), Is.Empty);
            });
        }

        [Test]
        public void should_stop_on_a_conflicting_unique_setting_without_changing_the_sources()
        {
            using (var audioarr = new SqliteConnection(ConnectionString(_audioarrPath, SqliteOpenMode.ReadWrite)))
            {
                audioarr.Open();
                using var command = audioarr.CreateCommand();
                command.CommandText = @"
                    UPDATE Config
                    SET Value = Value || '-conflicting-fixture'
                    WHERE Key = (SELECT Key FROM Config ORDER BY Key LIMIT 1);";
                command.ExecuteNonQuery();
            }

            var readarrFingerprint = Fingerprint(_readarrPath);
            var audioarrFingerprint = Fingerprint(_audioarrPath);
            var exception = Assert.Throws<InvalidOperationException>(() => RunMerge(previewOnly: false));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("without changing either original source"));
                Assert.That(Fingerprint(_readarrPath), Is.EqualTo(readarrFingerprint));
                Assert.That(Fingerprint(_audioarrPath), Is.EqualTo(audioarrFingerprint));
                Assert.That(File.Exists(Path.Combine(_appDataDirectory, "chaptarr.db")), Is.False);
                Assert.That(File.Exists(Path.Combine(_appDataDirectory, "bookshelfng-merge.receipt")), Is.False);
            });
        }

        private BookshelfDatabaseConversionResult RunMerge(bool previewOnly)
        {
            var appFolderInfo = new Mock<IAppFolderInfo>();
            appFolderInfo.SetupGet(folder => folder.AppDataFolder).Returns(_appDataDirectory);
            var controller = new MigrationController(
                LogManager.GetLogger(nameof(BookshelfMultiDatabaseConverterFixture)),
                null,
                appFolderInfo.Object);

            return controller.MigrateBookshelf(
                ConnectionString(_readarrPath),
                new MigrationContext(MigrationType.Main),
                DatabaseType.SQLite,
                previewOnly,
                mergeMultipleSqliteSources: true);
        }

        private static void CreatePopulatedBookshelfDatabase(string path)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWriteCreate));
            connection.Open();

            var schemaResourceName = assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith("BookshelfNG.Schema.158.sql", StringComparison.Ordinal));
            using (var schema = assembly.GetManifestResourceStream(schemaResourceName))
            using (var reader = new StreamReader(schema))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            var dataResourceName = assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith("BookshelfNG.PopulatedMigration158.sql", StringComparison.Ordinal));
            using (var data = assembly.GetManifestResourceStream(dataResourceName))
            using (var reader = new StreamReader(data))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }
        }

        private static string ConnectionString(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false
            }.ConnectionString;
        }

        private static long CountRows(string path, string table)
        {
            using var connection = new SqliteConnection(ConnectionString(path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- test passes fixed fixture table names only.
            return System.Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Fingerprint(string path)
        {
            var digest = SHA256.HashData(File.ReadAllBytes(path));
            return "sha256:" + System.Convert.ToHexString(digest).ToLowerInvariant();
        }
    }
}
