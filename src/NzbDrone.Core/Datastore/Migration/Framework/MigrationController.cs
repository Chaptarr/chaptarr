using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Conventions;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Extensions.Logging;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using Npgsql;

namespace NzbDrone.Core.Datastore.Migration.Framework
{
    public interface IMigrationController
    {
        void Migrate(string connectionString, MigrationContext migrationContext, DatabaseType databaseType);
        void MigrateBookshelfSource(string connectionString);
        BookshelfDatabaseConversionResult MigrateBookshelf(
            string connectionString,
            MigrationContext migrationContext,
            DatabaseType databaseType,
            bool previewOnly,
            bool mergeMultipleSqliteSources = false);
    }

    public class MigrationController : IMigrationController
    {
        private readonly Logger _logger;
        private readonly ILoggerProvider _migrationLoggerProvider;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IConfigFileProvider _configFileProvider;

        public MigrationController(Logger logger,
                                   ILoggerProvider migrationLoggerProvider)
            : this(logger, migrationLoggerProvider, null, null)
        {
        }

        public MigrationController(Logger logger,
                                   ILoggerProvider migrationLoggerProvider,
                                   IAppFolderInfo appFolderInfo)
            : this(logger, migrationLoggerProvider, appFolderInfo, null)
        {
        }

        public MigrationController(Logger logger,
                                   ILoggerProvider migrationLoggerProvider,
                                   IAppFolderInfo appFolderInfo,
                                   IConfigFileProvider configFileProvider)
        {
            _logger = logger;
            _migrationLoggerProvider = migrationLoggerProvider;
            _appFolderInfo = appFolderInfo;
            _configFileProvider = configFileProvider;
        }

        public void Migrate(string connectionString, MigrationContext migrationContext, DatabaseType databaseType)
        {
            if (migrationContext.MigrationType == MigrationType.Main &&
                databaseType == DatabaseType.SQLite &&
                !migrationContext.DesiredVersion.HasValue &&
                HasBothPopulatedBookshelfSources())
            {
                var targetLineage = DatabaseLineageGuard.Detect(connectionString, DatabaseType.SQLite);
                if (targetLineage == DatabaseLineage.BookshelfNG)
                {
                    throw new InvalidDataException(
                        "A BookshelfNG database already exists at the ChaptarrNG database path beside both readarr.db and audioarr.db. No database was changed; preserve all three files and resolve the source before restarting.");
                }

                if (targetLineage == DatabaseLineage.Empty)
                {
                    var primarySourcePath = Path.Combine(_appFolderInfo.AppDataFolder, "readarr.db");
                    var primarySourceConnectionString = new SqliteConnectionStringBuilder
                    {
                        DataSource = primarySourcePath,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false
                    }.ConnectionString;

                    _logger.Info("Both BookshelfNG SQLite databases contain data. Validating and merging them into the new ChaptarrNG database before normal startup.");
                    var merge = MigrateBookshelf(
                        primarySourceConnectionString,
                        migrationContext,
                        databaseType,
                        previewOnly: false,
                        mergeMultipleSqliteSources: true);
                    _logger.Info("BookshelfNG multi-database migration completed. Both original databases remain in place; merged-source rollback backup retained at {0}", merge.BackupPath);
                    return;
                }

                // A native target with a validated merge receipt is handled by
                // the normal lineage guard below. Unsupported targets also
                // continue there and remain fail-closed.
            }

            if (migrationContext.MigrationType == MigrationType.Main &&
                (databaseType is DatabaseType.SQLite or DatabaseType.PostgreSQL) &&
                DatabaseLineageGuard.Detect(connectionString, databaseType) == DatabaseLineage.BookshelfNG)
            {
                if (migrationContext.DesiredVersion.HasValue)
                {
                    throw new InvalidDataException("A BookshelfNG database can only be converted during a full Chaptarr startup migration. The source database was not changed.");
                }

                var result = MigrateBookshelf(connectionString, migrationContext, databaseType, previewOnly: false);
                _logger.Info("BookshelfNG database conversion completed. Original database backup retained at {0}", result.BackupPath);
                var mediaMapBasePath = GetMediaMapBasePath(connectionString, databaseType);
                var mediaReviewPath = mediaMapBasePath + ".bookshelf-media-review.json";
                if (File.Exists(mediaReviewPath))
                {
                    _logger.Warn("BookshelfNG books with unclear media types were preserved in both catalogs; source-monitored books remain monitored in both formats. Review or narrow the migration choices at {0}", mediaReviewPath);
                }

                return;
            }

            MigrateNative(connectionString, migrationContext, databaseType);
        }

        private bool HasBothPopulatedBookshelfSources()
        {
            if (string.IsNullOrWhiteSpace(_appFolderInfo?.AppDataFolder))
            {
                return false;
            }

            var readarrPath = Path.Combine(_appFolderInfo.AppDataFolder, "readarr.db");
            var audioarrPath = Path.Combine(_appFolderInfo.AppDataFolder, "audioarr.db");
            return HasSqliteSourceData(readarrPath) && HasSqliteSourceData(audioarrPath);
        }

        private static bool HasSqliteSourceData(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (new FileInfo(path).Length > 0)
            {
                return true;
            }

            return new[] { "-journal", "-wal" }
                .Any(suffix => File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0);
        }

        public BookshelfDatabaseConversionResult MigrateBookshelf(
            string connectionString,
            MigrationContext migrationContext,
            DatabaseType databaseType,
            bool previewOnly,
            bool mergeMultipleSqliteSources = false)
        {
            if (databaseType is not (DatabaseType.SQLite or DatabaseType.PostgreSQL) ||
                DatabaseLineageGuard.Detect(connectionString, databaseType) != DatabaseLineage.BookshelfNG)
            {
                throw new InvalidDataException("BookshelfNG migration requires a supported BookshelfNG SQLite or PostgreSQL database. No database was changed.");
            }

            if (migrationContext?.DesiredVersion.HasValue == true)
            {
                throw new InvalidDataException("BookshelfNG conversion requires a full Chaptarr database migration. No database was changed.");
            }

            var effectiveMigrationContext = migrationContext ?? new MigrationContext(MigrationType.Main);
            var journal = previewOnly ? null : new BookshelfMigrationJournal(GetJournalDataDirectory(connectionString, databaseType));
            var sourceApiBasePath = GetSourceApiBasePath();
            journal?.RecordOutcome("source-engine", databaseType == DatabaseType.SQLite ? "sqlite" : "postgresql");
            if (journal != null)
            {
                var configPath = Path.Combine(GetJournalDataDirectory(connectionString, databaseType), "config.xml");
                var executablePath = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    executablePath = Environment.ProcessPath;
                }

                if (File.Exists(configPath) && (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) == 0)
                {
                    journal.RecordFingerprint("source-config", configPath);
                }

                if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
                {
                    journal.RecordFingerprint("target-executable", executablePath);
                }
            }

            if (databaseType == DatabaseType.PostgreSQL)
            {
                if (mergeMultipleSqliteSources)
                {
                    throw new InvalidOperationException("The BookshelfNG multi-database merge applies only to SQLite app-data databases.");
                }

                return new BookshelfPostgresDatabaseConverter().Convert(
                    connectionString,
                    targetConnectionString => MigrateNative(targetConnectionString, effectiveMigrationContext, DatabaseType.PostgreSQL),
                    GetMediaMapBasePath(connectionString, databaseType),
                    previewOnly,
                    sourceSnapshotConnectionString => MigrateBookshelfSource(sourceSnapshotConnectionString, DatabaseType.PostgreSQL),
                    journal == null ? null : journal.RecordConverterStage,
                    journal == null ? null : journal.RecordOutcome,
                    sourceApiBasePath);
            }

            var sourceDatabasePath = Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);
            if (mergeMultipleSqliteSources)
            {
                if (_appFolderInfo == null || string.IsNullOrWhiteSpace(_appFolderInfo.AppDataFolder))
                {
                    throw new InvalidOperationException("The BookshelfNG multi-database merge requires an application data directory.");
                }

                return new BookshelfMultiDatabaseConverter().Convert(
                    sourceDatabasePath,
                    _appFolderInfo.AppDataFolder,
                    targetConnectionString => MigrateNative(targetConnectionString, effectiveMigrationContext, DatabaseType.SQLite),
                    (source, target, transaction, sourceSchemaVersion, mediaMapBasePath) => BookshelfDatabaseImporter.Import(
                        source,
                        target,
                        transaction,
                        mediaMapBasePath,
                        sourceSchemaVersion,
                        sourceApiBasePath),
                    previewOnly,
                    MigrateBookshelfSource,
                    journal == null ? null : journal.RecordConverterStage,
                    journal == null ? null : journal.RecordOutcome);
            }

            return new BookshelfDatabaseConverter().ConvertWithSourceSchema(
                connectionString,
                targetConnectionString => MigrateNative(targetConnectionString, effectiveMigrationContext, DatabaseType.SQLite),
                (source, target, transaction, sourceSchemaVersion) => BookshelfDatabaseImporter.Import(
                    source,
                    target,
                    transaction,
                    sourceDatabasePath,
                    sourceSchemaVersion,
                    sourceApiBasePath),
                previewOnly,
                MigrateBookshelfSource,
                journal == null ? null : journal.RecordConverterStage,
                journal == null ? null : journal.RecordOutcome);
        }

        private string GetSourceApiBasePath()
        {
            if (_configFileProvider != null)
            {
                return BookshelfNGCompatibilityProfile.NormalizeApiBasePath(_configFileProvider.UrlBase);
            }

            var legacyValue = Environment.GetEnvironmentVariable("BOOKSHELF_URLBASE") ??
                              Environment.GetEnvironmentVariable("BOOKSHELF__SERVER__URLBASE");
            return BookshelfNGCompatibilityProfile.NormalizeApiBasePath(legacyValue);
        }

        public BookshelfDatabaseRollbackResult RestoreBookshelf(
            string currentConnectionString,
            DatabaseType databaseType,
            string bookshelfBackup,
            bool previewOnly)
        {
            if (databaseType is not (DatabaseType.SQLite or DatabaseType.PostgreSQL))
            {
                throw new InvalidDataException("BookshelfNG rollback requires a supported SQLite or PostgreSQL database. No database was changed.");
            }

            if (databaseType == DatabaseType.PostgreSQL)
            {
                var result = new BookshelfPostgresDatabaseConverter().Restore(currentConnectionString, bookshelfBackup, previewOnly);
                if (!previewOnly)
                {
                    RecordRollbackInJournal(currentConnectionString, databaseType, result);
                }

                return result;
            }

            var sqliteResult = new BookshelfDatabaseConverter().Restore(
                currentConnectionString,
                bookshelfBackup,
                previewOnly,
                MigrateBookshelfSource);
            if (!previewOnly)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(_appFolderInfo?.AppDataFolder))
                    {
                        BookshelfMergeReceipt.MarkRolledBack(_appFolderInfo.AppDataFolder);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _logger.Warn(exception, "The BookshelfNG merge receipt could not be marked rolled back; the SQLite restore itself completed.");
                }

                RecordRollbackInJournal(currentConnectionString, databaseType, sqliteResult);
            }

            return sqliteResult;
        }

        private void RecordRollbackInJournal(
            string connectionString,
            DatabaseType databaseType,
            BookshelfDatabaseRollbackResult result)
        {
            try
            {
                var journal = new BookshelfMigrationJournal(GetJournalDataDirectory(connectionString, databaseType));
                journal.RecordOutcome("rollback", "restored-source");
                journal.RecordOutcome("rollback-source-artifact", SanitizeJournalToken(Path.GetFileName(result.BookshelfBackupPath)));
                journal.RecordOutcome("rollback-displaced-target", SanitizeJournalToken(Path.GetFileName(result.ChaptarrBackupPath)));
                if (databaseType == DatabaseType.SQLite)
                {
                    if (File.Exists(result.BookshelfBackupPath))
                    {
                        journal.RecordFingerprint("rollback-source-fingerprint", result.BookshelfBackupPath);
                    }

                    if (!string.IsNullOrWhiteSpace(result.ChaptarrBackupPath) && File.Exists(result.ChaptarrBackupPath))
                    {
                        journal.RecordFingerprint("rollback-displaced-target-fingerprint", result.ChaptarrBackupPath);
                    }
                }

                journal.RecordState("rolled-back");
            }
            catch
            {
                // Restoring the validated database has already completed. The
                // package-level rollback hook records its own completion state.
            }
        }

        private static string SanitizeJournalToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "none";
            }

            var sanitized = new string(value.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ':' ? character : '_').ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }

        private string GetJournalDataDirectory(string connectionString, DatabaseType databaseType)
        {
            if (!string.IsNullOrWhiteSpace(_appFolderInfo?.AppDataFolder))
            {
                return _appFolderInfo.AppDataFolder;
            }

            if (databaseType == DatabaseType.SQLite)
            {
                return Path.GetDirectoryName(Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource));
            }

            throw new InvalidOperationException("BookshelfNG migration journaling requires the ChaptarrNG app-data folder.");
        }

        private string GetMediaMapBasePath(string connectionString, DatabaseType databaseType)
        {
            if (databaseType == DatabaseType.SQLite)
            {
                return Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);
            }

            var dataDirectory = _appFolderInfo?.AppDataFolder;
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                throw new InvalidOperationException("PostgreSQL BookshelfNG migration requires the ChaptarrNG app-data folder for its review files. No database was changed.");
            }

            return Path.Combine(dataDirectory, "readarr.db");
        }

        private static string GetPostgresCurrentSchema(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT current_schema();";
            var schema = command.ExecuteScalar()?.ToString();
            if (string.IsNullOrWhiteSpace(schema))
            {
                throw new InvalidOperationException("PostgreSQL migrations require a current schema.");
            }

            return schema;
        }

        public void MigrateBookshelfSource(string connectionString)
        {
            if (DatabaseLineageGuard.Detect(connectionString, DatabaseType.SQLite) != DatabaseLineage.BookshelfNG)
            {
                throw new InvalidDataException("The database is not a supported BookshelfNG SQLite source. No database was changed.");
            }

            using (var source = new SqliteConnection(connectionString))
            {
                source.Open();
                BookshelfDatabaseImporter.ValidateSourceBackup(source);
            }

            var serviceProvider = new ServiceCollection()
                .AddLogging(b => b.AddNLog())
                .AddFluentMigratorCore()
                .Configure<RunnerOptions>(cfg => cfg.IncludeUntaggedMaintenances = true)
                .ConfigureRunner(
                    builder => builder
                    .AddNzbDroneSQLite()
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(Assembly.GetExecutingAssembly()).For.All())
                .Configure<TypeFilterOptions>(opt => opt.Namespace = "NzbDrone.Core.Datastore.BookshelfMigration")
                .Configure<ProcessorOptions>(opt =>
                {
                    opt.PreviewOnly = false;
                    opt.Timeout = TimeSpan.FromMinutes(5);
                })
                .Configure<SelectingProcessorAccessorOptions>(cfg => cfg.ProcessorId = "SQLite")
                .Configure<SelectingGeneratorAccessorOptions>(cfg => cfg.GeneratorId = "SQLite")
                .BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
                MigrationContext.Current = new MigrationContext(MigrationType.Main);
                try
                {
                    runner.MigrateUp();
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException(
                        "BookshelfNG source migrations failed on the disposable snapshot. The original database was not changed.",
                        exception);
                }
                finally
                {
                    MigrationContext.Current = null;
                }
            }
        }

        public void MigrateBookshelfSource(string connectionString, DatabaseType databaseType)
        {
            if (databaseType == DatabaseType.SQLite)
            {
                MigrateBookshelfSource(connectionString);
                return;
            }

            if (databaseType != DatabaseType.PostgreSQL)
            {
                throw new InvalidDataException("BookshelfNG source migrations support only SQLite or PostgreSQL.");
            }

            var sourceSchema = GetPostgresCurrentSchema(connectionString);
            using (var source = new NpgsqlConnection(connectionString))
            {
                source.Open();
                if (DatabaseLineageGuard.Detect(source, DatabaseType.PostgreSQL) != DatabaseLineage.BookshelfNG)
                {
                    throw new InvalidDataException("The database is not a supported BookshelfNG PostgreSQL source. No database was changed.");
                }

                BookshelfDatabaseImporter.ValidateSourceBackup(source);
            }

            var migrationServices = new ServiceCollection()
                .AddLogging(builder => builder.AddNLog())
                .AddFluentMigratorCore()
                .Configure<RunnerOptions>(options => options.IncludeUntaggedMaintenances = true)
                .ConfigureRunner(builder => builder
                    .AddPostgres()
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(Assembly.GetExecutingAssembly()).For.All())
                .Configure<TypeFilterOptions>(options => options.Namespace = "NzbDrone.Core.Datastore.BookshelfMigration")
                .Configure<ProcessorOptions>(options =>
                {
                    options.PreviewOnly = false;
                    options.Timeout = TimeSpan.FromMinutes(5);
                })
                .Configure<SelectingProcessorAccessorOptions>(options => options.ProcessorId = "PostgreSQL")
                .Configure<SelectingGeneratorAccessorOptions>(options => options.GeneratorId = "PostgreSQL");
            migrationServices.AddSingleton<IConventionSet>(new DefaultConventionSet(sourceSchema, null));

            using (var serviceProvider = migrationServices.BuildServiceProvider())
            using (var scope = serviceProvider.CreateScope())
            {
                MigrationContext.Current = new MigrationContext(MigrationType.Main);
                try
                {
                    scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException(
                        "BookshelfNG PostgreSQL source migrations failed on the disposable snapshot. The original database was not changed.",
                        exception);
                }
                finally
                {
                    MigrationContext.Current = null;
                }
            }

            using (var migratedSource = new NpgsqlConnection(connectionString))
            {
                migratedSource.Open();
                BookshelfDatabaseImporter.ValidateSourceSchema(migratedSource);
            }
        }

        private void MigrateNative(string connectionString, MigrationContext migrationContext, DatabaseType databaseType)
        {
            if (migrationContext.MigrationType == MigrationType.Main)
            {
                DatabaseLineageGuard.Validate(connectionString, databaseType);
            }

            var sw = Stopwatch.StartNew();

            _logger.Info("*** Migrating {0} database ***", databaseType);

            ServiceProvider serviceProvider;

            // FluentMigrator processor/generator ids are case-sensitive and use "SQLite"/"PostgreSQL"
            // (older versions used "sqlite"/"postgres").
            var db = databaseType == DatabaseType.SQLite ? "SQLite" : "PostgreSQL";
            var processorId = databaseType == DatabaseType.PostgreSQL
                ? SchemaScopedPostgresProcessor.ProcessorId
                : db;
            var defaultSchema = databaseType == DatabaseType.PostgreSQL
                ? GetPostgresCurrentSchema(connectionString)
                : null;
            var postgresVersionTableMetaData = defaultSchema == null
                ? null
                : new SchemaScopedVersionTableMetaData(
                    new DefaultConventionSet(defaultSchema, null),
                    Options.Create(new RunnerOptions { IncludeUntaggedMaintenances = true }),
                    defaultSchema);

            var migrationServices = new ServiceCollection()
                .AddLogging(b => b.AddNLog())
                .AddFluentMigratorCore()
                .Configure<RunnerOptions>(cfg => cfg.IncludeUntaggedMaintenances = true)
                .ConfigureRunner(builder =>
                {
                    var runnerBuilder = builder
                        .AddPostgres()
                        .AddSQLite() // Use SQLite runner (Microsoft.Data.Sqlite)
                        .WithGlobalConnectionString(connectionString);

                    if (postgresVersionTableMetaData != null)
                    {
                        // FluentMigrator's VersionInfo table is independent of the migration convention set.
                        runnerBuilder.WithVersionTable(postgresVersionTableMetaData);
                    }

                    runnerBuilder.ScanIn(Assembly.GetExecutingAssembly()).For.All();
                })
                .Configure<TypeFilterOptions>(opt => opt.Namespace = "NzbDrone.Core.Datastore.Migration")
                .Configure<ProcessorOptions>(opt =>
                {
                    opt.PreviewOnly = false;
                    opt.Timeout = TimeSpan.FromMinutes(5);
                })
                .Configure<SelectingProcessorAccessorOptions>(cfg =>
                {
                    cfg.ProcessorId = processorId;
                })
                .Configure<SelectingGeneratorAccessorOptions>(cfg =>
                {
                    cfg.GeneratorId = db;
                });

            if (databaseType == DatabaseType.PostgreSQL)
            {
                migrationServices.AddSingleton<IConventionSet>(new DefaultConventionSet(defaultSchema, null));
                SchemaScopedPostgresProcessor.Register(migrationServices, defaultSchema);
            }

            serviceProvider = migrationServices.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

                if (postgresVersionTableMetaData != null &&
                    !string.Equals(
                        scope.ServiceProvider.GetRequiredService<IVersionTableMetaData>().SchemaName,
                        defaultSchema,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "PostgreSQL migration history was not scoped to the active schema. No migration was run.");
                }

                MigrationContext.Current = migrationContext;

                if (migrationContext.DesiredVersion.HasValue)
                {
                    runner.MigrateUp(migrationContext.DesiredVersion.Value);
                }
                else
                {
                    runner.MigrateUp();
                }

                MigrationContext.Current = null;
            }

            sw.Stop();

            _logger.Debug("Took: {0}", sw.Elapsed);

            // Optional: validate baseline schema checksum on fresh installs
            try
            {
                if (databaseType == DatabaseType.SQLite && !string.IsNullOrWhiteSpace(BaselineSchema.ExpectedSqliteSha256))
                {
                    var actual = SchemaChecksum.ComputeSqliteHash(connectionString);
                    if (!string.Equals(actual, BaselineSchema.ExpectedSqliteSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ApplicationException($"Schema checksum mismatch. Expected {BaselineSchema.ExpectedSqliteSha256}, got {actual}");
                    }
                    _logger.Info("Baseline schema checksum OK: {0}", actual);
                }
                else if (databaseType == DatabaseType.PostgreSQL && !string.IsNullOrWhiteSpace(BaselineSchema.ExpectedPostgresSha256))
                {
                    var actual = SchemaChecksum.ComputePostgresHash(connectionString);
                    if (!string.Equals(actual, BaselineSchema.ExpectedPostgresSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ApplicationException($"Postgres schema checksum mismatch. Expected {BaselineSchema.ExpectedPostgresSha256}, got {actual}");
                    }
                    _logger.Info("Baseline Postgres schema checksum OK: {0}", actual);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Schema checksum validation failed");
                throw;
            }
        }

        private sealed class SchemaScopedVersionTableMetaData : DefaultVersionTableMetaData
        {
            public SchemaScopedVersionTableMetaData(
                IConventionSet conventionSet,
                IOptions<RunnerOptions> runnerOptions,
                string schemaName)
                : base(conventionSet, runnerOptions)
            {
                SchemaName = schemaName;
            }
        }
    }
}
