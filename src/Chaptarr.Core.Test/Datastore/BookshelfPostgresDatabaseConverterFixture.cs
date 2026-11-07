using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Conventions;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class BookshelfPostgresDatabaseConverterFixture
    {
        private string _baseConnectionString;
        private string _sourceSchema;
        private string _mediaMapBasePath;
        private string _temporaryDirectory;
        private HashSet<string> _schemasBefore;
        private NpgsqlConnection _testLockConnection;
        private string _previousHardcoverMode;
        private string _previousHardcoverNativeMode;
        private bool _foreignVersionInfoCreated;
        private readonly List<string> _knownBackupSchemas = new();

        [SetUp]
        public void SetUp()
        {
            _previousHardcoverMode = Environment.GetEnvironmentVariable("HARDCOVER");
            _previousHardcoverNativeMode = Environment.GetEnvironmentVariable("HARDCOVER_NATIVE");
            Environment.SetEnvironmentVariable("HARDCOVER", null);
            Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", null);
            _knownBackupSchemas.Clear();
            _baseConnectionString = Environment.GetEnvironmentVariable("CHAPTARRNG_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                Assert.Ignore("Set CHAPTARRNG_POSTGRES_TEST_CONNECTION to run the BookshelfNG PostgreSQL conversion smoke test.");
            }

            _testLockConnection = new NpgsqlConnection(_baseConnectionString);
            _testLockConnection.Open();
            using (var lockCommand = _testLockConnection.CreateCommand())
            {
                lockCommand.CommandText = "SELECT pg_advisory_lock(hashtext(current_database()), hashtext('ChaptarrNG Bookshelf PostgreSQL converter fixture'));";
                lockCommand.ExecuteNonQuery();
            }

            _temporaryDirectory = Path.Combine(Path.GetTempPath(), "chaptarr-bookshelf-postgres-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            _mediaMapBasePath = Path.Combine(_temporaryDirectory, "readarr.db");
            _sourceSchema = "bookshelf_test_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _schemasBefore = GetSchemaNames(_baseConnectionString);
            CreateSchema(_baseConnectionString, _sourceSchema);
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                Environment.SetEnvironmentVariable("HARDCOVER", _previousHardcoverMode);
                Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", _previousHardcoverNativeMode);
                return;
            }

            try
            {
                if (_foreignVersionInfoCreated)
                {
                    DropForeignVersionInfo(_baseConnectionString);
                }

                var generatedSchemas = _knownBackupSchemas
                    .Append(_sourceSchema)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                foreach (var schema in generatedSchemas)
                {
                    DropSchema(_baseConnectionString, schema);
                }

                if (_temporaryDirectory != null && Directory.Exists(_temporaryDirectory))
                {
                    Directory.Delete(_temporaryDirectory, recursive: true);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("HARDCOVER", _previousHardcoverMode);
                Environment.SetEnvironmentVariable("HARDCOVER_NATIVE", _previousHardcoverNativeMode);
                if (_testLockConnection != null)
                {
                    try
                    {
                        using var unlockCommand = _testLockConnection.CreateCommand();
                        unlockCommand.CommandText = "SELECT pg_advisory_unlock(hashtext(current_database()), hashtext('ChaptarrNG Bookshelf PostgreSQL converter fixture'));";
                        unlockCommand.ExecuteNonQuery();
                    }
                    finally
                    {
                        _testLockConnection.Dispose();
                        _testLockConnection = null;
                    }
                }
            }
        }

        [TestCase(true, "127")]
        [TestCase(false, "126")]
        public void should_preserve_source_quality_profile_sequence_across_row_only_snapshot(bool isCalled, string expectedCounter)
        {
            var sourceConnectionString = BuildConnectionString(_baseConnectionString, _sourceSchema);
            MigrateBookshelfSourceToLatest(sourceConnectionString);
            SeedMatchingThreshold(sourceConnectionString);
            SeedCurrentBookshelfSourceWithLibrary(sourceConnectionString);
            using (var source = new NpgsqlConnection(sourceConnectionString))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "SELECT setval(pg_get_serial_sequence('\"QualityProfiles\"','Id'),127,@called);";
                command.Parameters.AddWithValue("called", isCalled);
                command.ExecuteScalar();
            }

            Environment.SetEnvironmentVariable("HARDCOVER", "true");
            var controller = new MigrationController(LogManager.GetLogger(nameof(BookshelfPostgresDatabaseConverterFixture)),
                null, new TestAppFolderInfo(_temporaryDirectory));
            var converted = controller.MigrateBookshelf(sourceConnectionString,
                new MigrationContext(MigrationType.Main), DatabaseType.PostgreSQL, previewOnly: false);
            _knownBackupSchemas.Add(converted.BackupPath);

            Assert.That(ReadConfigValue(sourceConnectionString, "bookshelfnglegacyqualityprofilesequence"), Is.EqualTo(expectedCounter));
            using var retainedSource = new NpgsqlConnection(BuildConnectionString(_baseConnectionString, converted.BackupPath));
            retainedSource.Open();
            Assert.That(BookshelfDatabaseImporter.ReadQualityProfileIdentityHighWater(retainedSource), Is.EqualTo(int.Parse(expectedCounter)));
        }

        [Test]
        public void should_preview_convert_and_restore_a_current_bookshelf_postgres_schema()
        {
            var sourceConnectionString = BuildConnectionString(_baseConnectionString, _sourceSchema);
            MigrateBookshelfSourceToLatest(sourceConnectionString);
            SeedMatchingThreshold(sourceConnectionString);
            SeedCurrentBookshelfSourceWithLibrary(sourceConnectionString);
            var maxSourceConfigId = ReadScalarInt(sourceConnectionString, "SELECT COALESCE(MAX(\"Id\"), 0) FROM \"Config\";");
            SeedForeignVersionInfo(_baseConnectionString);

            var controller = new MigrationController(
                LogManager.GetLogger(nameof(BookshelfPostgresDatabaseConverterFixture)),
                null,
                new TestAppFolderInfo(_temporaryDirectory));

            Environment.SetEnvironmentVariable("HARDCOVER", "true");

            var preview = controller.MigrateBookshelf(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL,
                previewOnly: true);

            Assert.That(preview.PreviewOnly, Is.True);
            Assert.That(preview.BackupPath, Is.Null);
            Assert.That(preview.ImportReport.Tables["Books"].SourceRows, Is.EqualTo(1));
            Assert.That(preview.ImportReport.Tables["BookFiles"].SourceRows, Is.EqualTo(1));
            Assert.That(preview.ImportReport.Tables["Books"].TargetMapping, Does.Contain("Hardcover"));
            Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(GetSchemaNames(_baseConnectionString).Except(_schemasBefore, StringComparer.Ordinal).Any(schema => schema.StartsWith("chaptarr_stage_", StringComparison.Ordinal)), Is.False);

            controller.Migrate(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL);
            var bookshelfBackupSchema = GetSchemaNames(_baseConnectionString)
                .Except(_schemasBefore, StringComparer.Ordinal)
                .Single(schema => schema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal));
            _knownBackupSchemas.Add(bookshelfBackupSchema);

            Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));
            Assert.That(ReadConfigValue(sourceConnectionString, "bookmatchingstrictness"), Is.EqualTo("balanced"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfMatchCompatibility"), Is.EqualTo("true"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGDatabaseMigrated"), Is.EqualTo("true"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGCompatibilityProfileVersion"), Is.EqualTo("2"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGSourceProduct"), Is.EqualTo("BookshelfNG"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGSourceSchemaVersion"), Is.EqualTo("158"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGProviderDialect"), Is.EqualTo("hc"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGFormatSemantics"), Is.EqualTo("audiobook-ebook-split-v1"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGIdentityMapVersion"), Is.EqualTo("1"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookshelfNGMigrationOutcome"), Is.EqualTo("converted"));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));
            Assert.That(ReadConfigValue(sourceConnectionString, "ExperienceMode"), Is.EqualTo("simple"));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"NamingConfig\";"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT \"Id\" FROM \"NamingConfig\";"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT \"ColonReplacementFormat\" FROM \"NamingConfig\";"), Is.EqualTo(4));
            Assert.That(ReadScalarString(sourceConnectionString, "SELECT \"AuthorFolderFormat\" FROM \"NamingConfig\";"), Is.EqualTo("{Author Name}"));
            Assert.That(ReadScalarString(sourceConnectionString, "SELECT \"StandardBookFormat\" FROM \"NamingConfig\";"), Is.EqualTo("{Book Title}/{Author Name} - {Book Title}{ (PartNumber)}"));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT \"EbookColonReplacementFormat\" FROM \"NamingConfig\";"), Is.EqualTo(4));
            Assert.That(ReadScalarString(sourceConnectionString, "SELECT \"EbookAuthorFolderFormat\" FROM \"NamingConfig\";"), Is.EqualTo("{Author Name}"));
            Assert.That(ReadScalarString(sourceConnectionString, "SELECT \"EbookStandardBookFormat\" FROM \"NamingConfig\";"), Is.EqualTo("{Book Title}/{Author Name} - {Book Title}{ (PartNumber)}"));
            Assert.That(ReadConfigValue(sourceConnectionString, "HardcoverApiToken"), Is.EqualTo("pg-fixture-token"));
            Assert.That(ReadConfigValue(sourceConnectionString, "HardcoverAuth"), Is.EqualTo("Bearer pg-fixture-token"));
            Assert.That(ReadConfigValue(sourceConnectionString, "MetadataServerUrl"), Is.EqualTo("https://bookshelf-metadata.example/catalog"));
            Assert.That(ReadConfigValue(sourceConnectionString, "MetadataSource"), Is.EqualTo("https://bookshelf-metadata.example/catalog"));
            var mappedConfigIds = new[]
            {
                ReadConfigId(sourceConnectionString, "bookmatchingstrictness"),
                ReadConfigId(sourceConnectionString, "BookshelfMatchCompatibility"),
                ReadConfigId(sourceConnectionString, "ExperienceMode"),
                ReadConfigId(sourceConnectionString, "BookshelfNGDatabaseMigrated"),
                ReadConfigId(sourceConnectionString, "HardcoverApiToken"),
                ReadConfigId(sourceConnectionString, "HardcoverEnabled"),
                ReadConfigId(sourceConnectionString, "MetadataServerUrl")
            };
            Assert.That(mappedConfigIds, Is.All.GreaterThan(maxSourceConfigId));
            Assert.That(mappedConfigIds, Is.Unique);
            Assert.That(ReadScalarLong(sourceConnectionString, "SELECT nextval(pg_get_serial_sequence('\"Config\"', 'Id'));"), Is.GreaterThan(mappedConfigIds.Max()));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Authors\" WHERE \"Id\" = 20 AND \"HardcoverAuthorId\" = 'hc:10';"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Books\" WHERE \"Id\" = 30 AND \"HardcoverBookId\" = 'hc:30';"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Editions\" WHERE \"Id\" = 40 AND \"HardcoverEditionId\" = '40';"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Series\" WHERE \"Id\" = 70 AND \"HardcoverSeriesId\" = 'hc:70';"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Books\" WHERE \"Id\" = 30 AND \"MediaType\" = 0 AND \"AudiobookMonitored\" = TRUE;"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Editions\" WHERE \"Id\" = 40 AND \"BookId\" = 30 AND \"Monitored\" = TRUE;"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"BookFiles\" WHERE \"Id\" = 50 AND \"EditionId\" = 40 AND \"Path\" = '/library/Fixture Author/book.m4b';"), Is.EqualTo(1));
            Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"BookFiles\" WHERE \"Id\" = 50 AND \"LastRemoteSearchTime\"::timestamp without time zone = TIMESTAMP '2026-01-04 05:06:07';"), Is.EqualTo(1),
                "successful catalog-search timestamps must survive PostgreSQL source conversion");
            Assert.That(ReadScalarLong(sourceConnectionString, "SELECT nextval(pg_get_serial_sequence('\"BookFiles\"', 'Id'));"), Is.GreaterThan(50));

            var bookshelfBackupConnectionString = BuildConnectionString(_baseConnectionString, bookshelfBackupSchema);
            Assert.That(DatabaseLineageGuard.Detect(bookshelfBackupConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(ReadConfigValue(bookshelfBackupConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));
            Assert.That(ReadConfigValue(bookshelfBackupConnectionString, "MetadataSource"), Is.EqualTo("https://bookshelf-metadata.example/catalog"));
            Assert.That(ReadScalarInt(bookshelfBackupConnectionString, "SELECT COUNT(*) FROM \"Config\" WHERE \"Key\" = 'BookshelfMatchCompatibility';"), Is.Zero);
            Assert.That(ReadConfigValue(bookshelfBackupConnectionString, "HardcoverAuth"), Is.EqualTo("Bearer pg-fixture-token"));
            WriteConfigValue(sourceConnectionString, "FixtureSetting", "after-migration");

            var restorePreview = controller.RestoreBookshelf(
                sourceConnectionString,
                DatabaseType.PostgreSQL,
                bookshelfBackupSchema,
                previewOnly: true);
            Assert.That(restorePreview.PreviewOnly, Is.True);
            Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));
            Assert.That(ReadConfigValue(sourceConnectionString, "FixtureSetting"), Is.EqualTo("after-migration"));

            var restored = controller.RestoreBookshelf(
                sourceConnectionString,
                DatabaseType.PostgreSQL,
                bookshelfBackupSchema,
                previewOnly: false);
            _knownBackupSchemas.Add(restored.ChaptarrBackupPath);

            Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
            Assert.That(ReadConfigValue(sourceConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));
            Assert.That(ReadConfigValue(sourceConnectionString, "HardcoverAuth"), Is.EqualTo("Bearer pg-fixture-token"));
            Assert.That(ReadConfigValue(sourceConnectionString, "FixtureSetting"), Is.EqualTo("preserved"));
            var chaptarrBackupConnectionString = BuildConnectionString(_baseConnectionString, restored.ChaptarrBackupPath);
            Assert.That(DatabaseLineageGuard.Detect(chaptarrBackupConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));
            Assert.That(ReadConfigValue(chaptarrBackupConnectionString, "FixtureSetting"), Is.EqualTo("after-migration"));
        }

        [Test]
        [TestCase(1)]
        [TestCase(13)]
        [TestCase(22)]
        [TestCase(33)]
        public void should_migrate_an_older_postgres_source_on_a_snapshot_and_restore_its_original_schema(int sourceVersion)
        {
            var sourceConnectionString = BuildConnectionString(_baseConnectionString, _sourceSchema);
            MigrateBookshelfSourceToVersion(sourceConnectionString, sourceVersion);
            SeedMatchingThreshold(sourceConnectionString);
            var originalVersion = ReadScalarInt(sourceConnectionString, "SELECT MAX(\"Version\") FROM \"VersionInfo\";");
            Assert.That(originalVersion, Is.EqualTo(sourceVersion));

            var controller = new MigrationController(
                LogManager.GetLogger(nameof(BookshelfPostgresDatabaseConverterFixture)),
                null,
                new TestAppFolderInfo(_temporaryDirectory));

            // These historical fixtures seed Hardcover credentials but no
            // native Hardcover provider IDs. Declare the legacy Goodreads
            // primary-provider mode explicitly; an unset mode is rejected.
            Environment.SetEnvironmentVariable("HARDCOVER", "false");

            var preview = controller.MigrateBookshelf(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL,
                previewOnly: true);

            Assert.Multiple(() =>
            {
                Assert.That(preview.PreviewOnly, Is.True);
                Assert.That(preview.ImportReport.BlockingIssues, Is.Empty);
                Assert.That(preview.ImportReport.Tables["Books"].SourceRows, Is.Zero);
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(originalVersion));
                Assert.That(ReadConfigValue(sourceConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));

                var schemas = GetSchemaNames(_baseConnectionString).Except(_schemasBefore, StringComparer.Ordinal).ToArray();
                Assert.That(schemas.Any(schema => schema.StartsWith("bookshelf_snapshot_", StringComparison.Ordinal)), Is.False);
                Assert.That(schemas.Any(schema => schema.StartsWith("chaptarr_stage_", StringComparison.Ordinal)), Is.False);
                Assert.That(schemas.Any(schema => schema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal)), Is.False);
            });

            var converted = controller.MigrateBookshelf(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL,
                previewOnly: false);
            var bookshelfBackupSchema = converted.BackupPath;
            _knownBackupSchemas.Add(bookshelfBackupSchema);

            var bookshelfBackupConnectionString = BuildConnectionString(_baseConnectionString, bookshelfBackupSchema);
            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(ReadConfigValue(sourceConnectionString, "HardcoverApiToken"), Is.EqualTo("pg-fixture-token"));
                Assert.That(DatabaseLineageGuard.Detect(bookshelfBackupConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadScalarInt(bookshelfBackupConnectionString, "SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(originalVersion));
                Assert.That(ReadConfigValue(bookshelfBackupConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));
            });

            var restorePreview = controller.RestoreBookshelf(
                sourceConnectionString,
                DatabaseType.PostgreSQL,
                bookshelfBackupSchema,
                previewOnly: true);
            Assert.That(restorePreview.PreviewOnly, Is.True);
            Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));

            var restored = controller.RestoreBookshelf(
                sourceConnectionString,
                DatabaseType.PostgreSQL,
                bookshelfBackupSchema,
                previewOnly: false);
            _knownBackupSchemas.Add(restored.ChaptarrBackupPath);

            var chaptarrBackupConnectionString = BuildConnectionString(_baseConnectionString, restored.ChaptarrBackupPath);
            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(originalVersion));
                Assert.That(ReadConfigValue(sourceConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));
                Assert.That(DatabaseLineageGuard.Detect(chaptarrBackupConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(ReadConfigValue(chaptarrBackupConnectionString, "HardcoverApiToken"), Is.EqualTo("pg-fixture-token"));
            });
        }

        [Test]
        public void should_convert_a_bookshelf_postgres_source_during_normal_startup_migration()
        {
            var sourceConnectionString = BuildConnectionString(_baseConnectionString, _sourceSchema);
            MigrateBookshelfSourceToLatest(sourceConnectionString);
            SeedMatchingThreshold(sourceConnectionString);
            SeedCurrentBookshelfSourceWithLibrary(sourceConnectionString);

            var controller = new MigrationController(
                LogManager.GetLogger(nameof(BookshelfPostgresDatabaseConverterFixture)),
                null,
                new TestAppFolderInfo(_temporaryDirectory));

            // This fixture seeds bare numeric IDs from BookshelfNG's native
            // Hardcover mode. Startup migration must receive the same explicit
            // source mode as the preview/apply workflow.
            Environment.SetEnvironmentVariable("HARDCOVER", "true");

            controller.Migrate(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL);

            var createdSchemas = GetSchemaNames(_baseConnectionString)
                .Except(_schemasBefore, StringComparer.Ordinal)
                .ToArray();
            var bookshelfBackupSchema = createdSchemas
                .Single(schema => schema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal));
            _knownBackupSchemas.Add(bookshelfBackupSchema);

            var backupConnectionString = BuildConnectionString(_baseConnectionString, bookshelfBackupSchema);
            Assert.Multiple(() =>
            {
                Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.Chaptarr));
                Assert.That(DatabaseLineageGuard.Detect(backupConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Books\" WHERE \"Id\" = 30;"), Is.EqualTo(1));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"BookFiles\" WHERE \"Id\" = 50 AND \"EditionId\" = 40;"), Is.EqualTo(1));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Authors\" WHERE \"Id\" = 20 AND \"HardcoverAuthorId\" = 'hc:10';"), Is.EqualTo(1));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT COUNT(*) FROM \"Books\" WHERE \"Id\" = 30 AND \"HardcoverBookId\" = 'hc:30';"), Is.EqualTo(1));
                Assert.That(ReadConfigValue(backupConnectionString, "BookImportMinimumMatchPercent"), Is.EqualTo("70"));
                Assert.That(ReadScalarInt(backupConnectionString, "SELECT COUNT(*) FROM \"Books\" WHERE \"Id\" = 30;"), Is.EqualTo(1));
            });
        }

        [Test]
        public void should_reject_a_newer_postgres_source_without_changing_its_tables()
        {
            var sourceConnectionString = BuildConnectionString(_baseConnectionString, _sourceSchema);
            MigrateBookshelfSourceToLatest(sourceConnectionString);
            using (var connection = new NpgsqlConnection(sourceConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE \"VersionInfo\" SET \"Version\" = 159 WHERE \"Version\" = 158;";
                Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
            }

            var controller = new MigrationController(
                LogManager.GetLogger(nameof(BookshelfPostgresDatabaseConverterFixture)),
                null,
                new TestAppFolderInfo(_temporaryDirectory));
            var exception = Assert.Throws<InvalidDataException>(() => controller.MigrateBookshelf(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL,
                previewOnly: false));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("schema version 159 is outside the bundled migration chain"));
                Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(159));
                var newSchemas = GetSchemaNames(_baseConnectionString).Except(_schemasBefore, StringComparer.Ordinal).ToArray();
                Assert.That(newSchemas.Any(schema => schema.StartsWith("bookshelf_snapshot_", StringComparison.Ordinal)), Is.False);
                Assert.That(newSchemas.Any(schema => schema.StartsWith("chaptarr_stage_", StringComparison.Ordinal)), Is.False);
                Assert.That(newSchemas.Any(schema => schema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal)), Is.False);
            });
        }

        [Test]
        public void should_reject_a_postgres_source_view_without_changing_its_tables()
        {
            var sourceConnectionString = BuildConnectionString(_baseConnectionString, _sourceSchema);
            MigrateBookshelfSourceToLatest(sourceConnectionString);
            using (var connection = new NpgsqlConnection(sourceConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE VIEW \"BookshelfFixtureView\" AS SELECT 1 AS \"Value\";";
                command.ExecuteNonQuery();
            }

            var controller = new MigrationController(
                LogManager.GetLogger(nameof(BookshelfPostgresDatabaseConverterFixture)),
                null,
                new TestAppFolderInfo(_temporaryDirectory));
            var exception = Assert.Throws<InvalidDataException>(() => controller.MigrateBookshelf(
                sourceConnectionString,
                new MigrationContext(MigrationType.Main),
                DatabaseType.PostgreSQL,
                previewOnly: true));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("view"));
                Assert.That(DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.PostgreSQL), Is.EqualTo(DatabaseLineage.BookshelfNG));
                Assert.That(ReadScalarInt(sourceConnectionString, "SELECT MAX(\"Version\") FROM \"VersionInfo\";"), Is.EqualTo(158));
                var newSchemas = GetSchemaNames(_baseConnectionString).Except(_schemasBefore, StringComparer.Ordinal).ToArray();
                Assert.That(newSchemas.Any(schema => schema.StartsWith("bookshelf_snapshot_", StringComparison.Ordinal)), Is.False);
                Assert.That(newSchemas.Any(schema => schema.StartsWith("chaptarr_stage_", StringComparison.Ordinal)), Is.False);
                Assert.That(newSchemas.Any(schema => schema.StartsWith("bookshelfng_backup_", StringComparison.Ordinal)), Is.False);
            });
        }

        private void MigrateBookshelfSourceToLatest(string connectionString)
        {
            MigrateBookshelfSourceToVersion(connectionString, null);
        }

        private void MigrateBookshelfSourceToVersion(string connectionString, int? version)
        {
            var migrationServices = new ServiceCollection()
                .AddLogging(builder => builder.AddNLog())
                .AddFluentMigratorCore()
                .Configure<RunnerOptions>(options => options.IncludeUntaggedMaintenances = true)
                .ConfigureRunner(builder => builder
                    .AddPostgres()
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(typeof(BookshelfDatabaseImporter).Assembly).For.All())
                .Configure<TypeFilterOptions>(options => options.Namespace = "NzbDrone.Core.Datastore.BookshelfMigration")
                .Configure<ProcessorOptions>(options =>
                {
                    options.PreviewOnly = false;
                    options.Timeout = TimeSpan.FromMinutes(5);
                })
                .Configure<SelectingProcessorAccessorOptions>(options => options.ProcessorId = "PostgreSQL")
                .Configure<SelectingGeneratorAccessorOptions>(options => options.GeneratorId = "PostgreSQL");
            migrationServices.AddSingleton<IConventionSet>(new DefaultConventionSet(_sourceSchema, null));
            using var serviceProvider = migrationServices.BuildServiceProvider();

            using var scope = serviceProvider.CreateScope();
            MigrationContext.Current = new MigrationContext(MigrationType.Main);
            try
            {
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
                if (version.HasValue)
                {
                    runner.MigrateUp(version.Value);
                }
                else
                {
                    runner.MigrateUp();
                }
            }
            finally
            {
                MigrationContext.Current = null;
            }

            using (var connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT COUNT(*)
                      FROM information_schema.tables
                     WHERE table_schema = current_schema()
                       AND table_type = 'BASE TABLE'
                       AND table_name IN ('Config', 'VersionInfo');";
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(2),
                    "BookshelfNG source migrations must create Config and VersionInfo in the requested PostgreSQL schema.");
            }
        }

        private static void SeedMatchingThreshold(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ""Config"" (""Key"", ""Value"")
                VALUES
                    ('BookImportMinimumMatchPercent', '70'),
                    ('HardcoverAuth', 'Bearer pg-fixture-token')
                ON CONFLICT (""Key"") DO UPDATE SET ""Value"" = EXCLUDED.""Value"";";
            command.ExecuteNonQuery();
        }

        private static void SeedCurrentBookshelfSourceWithLibrary(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ""QualityProfiles"" (""Id"", ""Name"", ""Cutoff"", ""Items"", ""UpgradeAllowed"") OVERRIDING SYSTEM VALUE
                VALUES (1, 'Audio default', 1, '[]', TRUE);
                INSERT INTO ""MetadataProfiles"" (""Id"", ""Name"", ""MinPopularity"", ""SkipMissingDate"", ""SkipMissingIsbn"", ""SkipPartsAndSets"", ""SkipSeriesSecondary"", ""AllowedLanguages"", ""PreferredEditionTerms"", ""PreferredSeries"") OVERRIDING SYSTEM VALUE
                VALUES (1, 'Audio metadata', 0, FALSE, FALSE, FALSE, FALSE, '[]', '3|audible', 'The Fixture Series');
                INSERT INTO ""RootFolders"" (""Id"", ""Path"", ""Name"", ""DefaultMetadataProfileId"", ""DefaultQualityProfileId"", ""DefaultMonitorOption"", ""DefaultTags"", ""IsCalibreLibrary"", ""CalibreSettings"", ""DefaultNewItemMonitorOption"") OVERRIDING SYSTEM VALUE
                VALUES (1, '/library', 'Library', 1, 1, 0, '[]', FALSE, NULL, 2);
                INSERT INTO ""Config"" (""Id"", ""Key"", ""Value"") OVERRIDING SYSTEM VALUE VALUES (3, 'FixtureSetting', 'preserved');
                INSERT INTO ""Config"" (""Id"", ""Key"", ""Value"") OVERRIDING SYSTEM VALUE VALUES (4, 'MetadataSource', 'https://bookshelf-metadata.example/catalog');
                INSERT INTO ""AuthorMetadata"" (""Id"", ""ForeignAuthorId"", ""TitleSlug"", ""Name"", ""Overview"", ""Status"", ""Images"", ""Aliases"", ""SortName"", ""NameLastFirst"", ""SortNameLastFirst"") OVERRIDING SYSTEM VALUE
                VALUES (10, '10', 'fixture-author', 'Fixture Author', 'Fixture overview', 0, '[]', '[]', 'Fixture Author', 'Author, Fixture', 'Author, Fixture');
                INSERT INTO ""Authors"" (""Id"", ""CleanName"", ""Path"", ""Monitored"", ""QualityProfileId"", ""Tags"", ""Added"", ""MetadataProfileId"", ""AuthorMetadataId"", ""MonitorNewItems"", ""EbookPath"", ""AudiobookPath"", ""EbookQualityProfileId"", ""AudiobookQualityProfileId"", ""EbookMetadataProfileId"", ""AudiobookMetadataProfileId"") OVERRIDING SYSTEM VALUE
                VALUES (20, 'fixtureauthor', '/library/Fixture Author', TRUE, 1, '[]', '2026-01-01 00:00:00+00', 1, 10, 2, '/library/Fixture Author/Ebooks', '/library/Fixture Author/Audiobooks', NULL, 1, NULL, 1);
                INSERT INTO ""Books"" (""Id"", ""AuthorMetadataId"", ""ForeignBookId"", ""TitleSlug"", ""Title"", ""Links"", ""Genres"", ""Ratings"", ""CleanTitle"", ""Monitored"", ""AnyEditionOk"", ""Added"", ""RelatedBooks"", ""LastSearchTime"") OVERRIDING SYSTEM VALUE
                VALUES (30, 10, '30', 'fixture-book', 'Fixture Book', '[]', '[]', '{}', 'fixture book', TRUE, FALSE, '2026-01-02 00:00:00+00', '[]', NULL);
                INSERT INTO ""Editions"" (""Id"", ""BookId"", ""ForeignEditionId"", ""Isbn13"", ""Asin"", ""Title"", ""TitleSlug"", ""Images"", ""Links"", ""IsEbook"", ""Monitored"", ""ManualAdd"", ""Narrator"") OVERRIDING SYSTEM VALUE
                VALUES (40, 30, '40', NULL, NULL, 'Fixture Audiobook', 'audio-40', '[]', '[]', FALSE, TRUE, FALSE, 'A Narrator');
                INSERT INTO ""Series"" (""Id"", ""ForeignSeriesId"", ""Title"", ""Numbered"", ""WorkCount"", ""PrimaryWorkCount"") OVERRIDING SYSTEM VALUE
                VALUES (70, '70', 'Fixture Series', TRUE, 1, 1);
                INSERT INTO ""BookFiles"" (""Id"", ""EditionId"", ""CalibreId"", ""Quality"", ""Size"", ""DateAdded"", ""MediaInfo"", ""Modified"", ""Path"", ""Part"", ""LastRemoteSearchTime"") OVERRIDING SYSTEM VALUE
                VALUES (50, 40, 0, '{}', 100, '2026-01-02 00:00:00+00', '{}', '2026-01-02 00:00:00+00', '/library/Fixture Author/book.m4b', 0, '2026-01-04 05:06:07+00');";
            command.ExecuteNonQuery();
        }

        private static string ReadConfigValue(string connectionString, string key)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Value\" FROM \"Config\" WHERE \"Key\" = @key;";
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar()?.ToString();
        }

        private static long ReadConfigId(string connectionString, string key)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Id\" FROM \"Config\" WHERE \"Key\" = @key;";
            command.Parameters.AddWithValue("@key", key);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void WriteConfigValue(string connectionString, string key, string value)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE \"Config\" SET \"Value\" = @value WHERE \"Key\" = @key;";
            command.Parameters.AddWithValue("@value", value);
            command.Parameters.AddWithValue("@key", key);
            Assert.That(command.ExecuteNonQuery(), Is.EqualTo(1));
        }

        private static int ReadScalarInt(string connectionString, string sql)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- callers pass fixed test queries; any dynamic values use parameters.
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ReadScalarString(string connectionString, string sql)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- callers pass fixed test queries; any dynamic values use parameters.
            return command.ExecuteScalar()?.ToString();
        }

        private static long ReadScalarLong(string connectionString, string sql)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- callers pass fixed test queries; any dynamic values use parameters.
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string BuildConnectionString(string connectionString, string searchPath)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = searchPath,
                Pooling = false,
                Enlist = false
            };
            return builder.ConnectionString;
        }

        private void SeedForeignVersionInfo(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT to_regclass('public.\"VersionInfo\"') IS NOT NULL;";
            if (Convert.ToBoolean(command.ExecuteScalar()))
            {
                // Use an existing public history table as the unrelated schema fixture without changing it.
                return;
            }

            command.CommandText = @"
                CREATE TABLE public.""VersionInfo"" (
                    ""Version"" bigint NOT NULL PRIMARY KEY,
                    ""AppliedOn"" timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""Description"" varchar(1024) NULL);";
            command.ExecuteNonQuery();
            _foreignVersionInfoCreated = true;

            command.CommandText = @"
                INSERT INTO public.""VersionInfo"" (""Version"", ""Description"")
                VALUES (10, 'unrelated schema history');";
            command.ExecuteNonQuery();
        }

        private static void DropForeignVersionInfo(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS public.\"VersionInfo\";";
            command.ExecuteNonQuery();
        }

        private static HashSet<string> GetSchemaNames(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT schema_name FROM information_schema.schemata;";
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }

        private static void CreateSchema(string connectionString, string schema)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE SCHEMA \"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- schema is a test identifier with embedded quotes escaped.
            command.ExecuteNonQuery();
        }

        private static void DropSchema(string connectionString, string schema)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP SCHEMA IF EXISTS \"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" CASCADE;"; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- schema is a test identifier with embedded quotes escaped.
            command.ExecuteNonQuery();
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
