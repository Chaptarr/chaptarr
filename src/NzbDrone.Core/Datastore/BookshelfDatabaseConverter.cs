using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace NzbDrone.Core.Datastore
{
    /// <summary>
    /// Stages a BookshelfNG SQLite conversion in a new native Chaptarr database.
    /// The caller must account for every row in every source table before the
    /// staged database can replace the source.
    /// </summary>
    public sealed class BookshelfDatabaseConverter
    {
        public BookshelfDatabaseConversionResult Convert(
            string sourceConnectionString,
            Action<string> migrateNativeTarget,
            Func<SqliteConnection, SqliteConnection, SqliteTransaction, BookshelfDatabaseImportReport> importRows,
            bool previewOnly = false,
            Action<string> migrateBookshelfSource = null,
            Action<string, string> reportProgress = null,
            Action<string, string> reportOutcome = null)
        {
            Func<SqliteConnection, SqliteConnection, SqliteTransaction, int, BookshelfDatabaseImportReport> importWithSourceSchema =
                importRows == null
                    ? null
                    : (source, target, transaction, _) => importRows(source, target, transaction);

            return ConvertWithSourceSchema(
                sourceConnectionString,
                migrateNativeTarget,
                importWithSourceSchema,
                previewOnly,
                migrateBookshelfSource,
                reportProgress,
                reportOutcome);
        }

        public BookshelfDatabaseConversionResult ConvertWithSourceSchema(
            string sourceConnectionString,
            Action<string> migrateNativeTarget,
            Func<SqliteConnection, SqliteConnection, SqliteTransaction, int, BookshelfDatabaseImportReport> importRows,
            bool previewOnly = false,
            Action<string> migrateBookshelfSource = null,
            Action<string, string> reportProgress = null,
            Action<string, string> reportOutcome = null)
        {
            if (migrateNativeTarget == null)
            {
                throw new ArgumentNullException(nameof(migrateNativeTarget));
            }

            if (importRows == null)
            {
                throw new ArgumentNullException(nameof(importRows));
            }

            var sourceBuilder = new SqliteConnectionStringBuilder(sourceConnectionString);
            if (sourceBuilder.Mode == SqliteOpenMode.Memory ||
                string.IsNullOrWhiteSpace(sourceBuilder.DataSource) ||
                string.Equals(sourceBuilder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("BookshelfNG conversion requires a file-backed SQLite database.");
            }

            var sourcePath = Path.GetFullPath(sourceBuilder.DataSource);
            EnsureSourceDatabasePaths(sourcePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The BookshelfNG database does not exist.", sourcePath);
            }

            if (DatabaseLineageGuard.Detect(sourceConnectionString, DatabaseType.SQLite) != DatabaseLineage.BookshelfNG)
            {
                throw new InvalidDataException("The database does not match the supported BookshelfNG SQLite schema.");
            }

            var directory = Path.GetDirectoryName(sourcePath);
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var stagePath = sourcePath + ".chaptarr-stage-" + suffix + ".db";
            var snapshotPath = sourcePath + ".bookshelf-snapshot-" + suffix + ".db";
            var migrationSourcePath = sourcePath + ".bookshelf-migrate-" + suffix + ".db";
            var backupPath = sourcePath + ".bookshelfng-backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + suffix + ".db";
            var lockPath = sourcePath + ".bookshelfng-conversion.lock";
            var promoted = false;
            var backupRetained = false;
            BookshelfDatabaseImportReport importReport = null;
            var sourceSchemaVersion = 0;

            using var conversionLock = AcquireConversionLock(lockPath);

            try
            {
                reportProgress?.Invoke("detected", sourcePath);

                using (var source = OpenConnection(sourcePath, SqliteOpenMode.ReadOnly, sourceBuilder))
                {
                    sourceSchemaVersion = BookshelfDatabaseImporter.ValidateSourceBackup(source);
                }

                if (!previewOnly)
                {
                    var cleanedStages = CleanOrphanedStageFiles(directory, sourcePath);
                    reportOutcome?.Invoke("orphan-stages-cleaned", cleanedStages.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                EnsureConversionSpace(sourcePath);
                reportProgress?.Invoke("preflighted", sourcePath);
                CreateConsistentSnapshot(sourcePath, snapshotPath, sourceBuilder);
                ValidateBookshelfSourceDatabase(snapshotPath);
                reportProgress?.Invoke("snapshotted", snapshotPath);

                // Keep the consistent snapshot as the untouched rollback source. Run
                // Bookshelf's released migrations only on a separate disposable copy so
                // old installations can reach the importer's current schema safely.
                File.Copy(snapshotPath, migrationSourcePath);
                var migrationSourceConnectionString = BuildConnectionString(sourceBuilder, migrationSourcePath, SqliteOpenMode.ReadWrite);
                if (migrateBookshelfSource != null)
                {
                    migrateBookshelfSource(migrationSourceConnectionString);
                    using var migratedSource = OpenConnection(migrationSourcePath, SqliteOpenMode.ReadOnly, sourceBuilder);
                    BookshelfDatabaseImporter.ValidateSourceSchema(migratedSource);
                }
                var staleSeriesBookLinks = ValidateBookshelfSourceDatabase(migrationSourcePath);

                var stageConnectionString = BuildConnectionString(sourceBuilder, stagePath, SqliteOpenMode.ReadWriteCreate);
                migrateNativeTarget(stageConnectionString);

                using (var source = OpenConnection(migrationSourcePath, SqliteOpenMode.ReadOnly, sourceBuilder))
                using (var target = OpenConnection(stagePath, SqliteOpenMode.ReadWrite, sourceBuilder))
                {
                    if (DatabaseLineageGuard.Detect(target.ConnectionString, DatabaseType.SQLite) != DatabaseLineage.Chaptarr)
                    {
                        throw new InvalidDataException("The staged conversion target did not migrate to a native Chaptarr schema.");
                    }

                    using (var transaction = target.BeginTransaction())
                    {
                        var sourceTables = GetSourceTableCounts(source);
                        importReport = importRows(source, target, transaction, sourceSchemaVersion);
                        EnsureStaleSeriesBookLinksAreAccounted(staleSeriesBookLinks, importReport);
                        ValidateImportReport(sourceTables, importReport);
                        transaction.Commit();
                    }
                }

                ValidateDatabase(stagePath, expectedLineage: DatabaseLineage.Chaptarr, checkForeignKeys: true);
                reportProgress?.Invoke("converted", stagePath);

                if (previewOnly)
                {
                    return new BookshelfDatabaseConversionResult(null, importReport, previewOnly: true);
                }

                importReport.ThrowIfBlockingIssues();

                // Retain a consistent SQLite backup before touching the source file or its WAL.
                CheckpointDatabase(snapshotPath, sourceBuilder);
                DeleteSidecars(snapshotPath);
                File.Move(snapshotPath, backupPath);
                backupRetained = true;

                CheckpointDatabase(sourcePath, sourceBuilder);
                CheckpointDatabase(stagePath, sourceBuilder);
                SqliteConnection.ClearAllPools();
                DeleteSidecars(sourcePath);
                DeleteSidecars(stagePath);

                // Replace the existing database atomically on Windows as well as Unix.
                // The source snapshot has already been retained as the rollback copy.
                File.Replace(stagePath, sourcePath, destinationBackupFileName: null);
                promoted = true;
                try
                {
                    reportProgress?.Invoke("promoted", backupPath);
                }
                catch
                {
                    // The database has already been atomically promoted. A journal
                    // filesystem error must not report the successful replacement as
                    // a failed conversion; the package health check can reconcile it.
                }

                return new BookshelfDatabaseConversionResult(backupPath, importReport, previewOnly: false);
            }
            finally
            {
                SqliteConnection.ClearAllPools();

                if (!promoted)
                {
                    DeleteDatabaseFiles(stagePath);
                }

                DeleteDatabaseFiles(migrationSourcePath);

                if (!backupRetained)
                {
                    DeleteDatabaseFiles(snapshotPath);
                }
            }
        }

        /// <summary>
        /// Restores a retained BookshelfNG source backup after a conversion. The current
        /// ChaptarrNG database is kept as a separate sibling file before the source is restored.
        /// </summary>
        public BookshelfDatabaseRollbackResult Restore(
            string currentConnectionString,
            string bookshelfBackupPath,
            bool previewOnly = false,
            Action<string> migrateBookshelfSource = null)
        {
            var currentBuilder = new SqliteConnectionStringBuilder(currentConnectionString);
            if (currentBuilder.Mode == SqliteOpenMode.Memory ||
                string.IsNullOrWhiteSpace(currentBuilder.DataSource) ||
                string.Equals(currentBuilder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("BookshelfNG rollback requires a file-backed SQLite database.");
            }

            var currentPath = Path.GetFullPath(currentBuilder.DataSource);
            EnsureSourceDatabasePaths(currentPath);
            if (!File.Exists(currentPath))
            {
                throw new FileNotFoundException("The current ChaptarrNG database does not exist.", currentPath);
            }

            string sourceBackupPath;
            if (string.IsNullOrWhiteSpace(bookshelfBackupPath))
            {
                sourceBackupPath = FindLatestBookshelfBackup(currentPath);
            }
            else
            {
                sourceBackupPath = Path.GetFullPath(bookshelfBackupPath);
            }

            EnsureSourceDatabasePaths(sourceBackupPath);
            if (!File.Exists(sourceBackupPath))
            {
                throw new FileNotFoundException("The retained BookshelfNG database backup does not exist.", sourceBackupPath);
            }

            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(currentPath, sourceBackupPath, pathComparison))
            {
                throw new InvalidDataException("The BookshelfNG backup must be a separate file from the current ChaptarrNG database.");
            }

            var expectedBackupPrefix = Path.GetFileName(currentPath) + ".bookshelfng-backup-";
            var backupFileName = Path.GetFileName(sourceBackupPath);
            if (!backupFileName.StartsWith(expectedBackupPrefix, pathComparison) ||
                !backupFileName.EndsWith(".db", pathComparison))
            {
                throw new InvalidDataException(
                    $"'{backupFileName}' is not a retained BookshelfNG conversion backup for '{Path.GetFileName(currentPath)}'. No database was changed.");
            }

            var lockPath = currentPath + ".bookshelfng-conversion.lock";
            using var conversionLock = AcquireConversionLock(lockPath);

            var mergedLegacySources = string.Equals(
                Path.GetFileName(currentPath),
                "chaptarr.db",
                pathComparison)
                ? BookshelfMergeReceipt.ValidateForNativeDatabase(currentPath)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoringMergedSources =
                mergedLegacySources.Contains("readarr.db") &&
                mergedLegacySources.Contains("audioarr.db");

            if (restoringMergedSources)
            {
                var appDataFolder = Path.GetDirectoryName(currentPath) ??
                    throw new InvalidDataException("Dual-database rollback could not resolve the app-data directory. No database was changed.");
                if (!File.Exists(Path.Combine(appDataFolder, "readarr.db")) ||
                    !File.Exists(Path.Combine(appDataFolder, "audioarr.db")))
                {
                    throw new InvalidDataException(
                        "Dual-database rollback requires both original BookshelfNG databases to remain in appdata. No database was changed; restore the missing source from the independent appdata backup or keep using ChaptarrNG.");
                }
            }

            if (DatabaseLineageGuard.Detect(currentConnectionString, DatabaseType.SQLite) != DatabaseLineage.Chaptarr)
            {
                throw new InvalidDataException("The current database is not a ChaptarrNG database. No database was changed.");
            }

            ValidateDatabase(currentPath, DatabaseLineage.Chaptarr, checkForeignKeys: true);
            ValidateBookshelfSourceDatabase(sourceBackupPath);
            ValidateBookshelfBackup(sourceBackupPath, currentBuilder, migrateBookshelfSource);

            if (previewOnly)
            {
                return new BookshelfDatabaseRollbackResult(sourceBackupPath, null, previewOnly: true);
            }

            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var chaptarrBackupPath = currentPath + ".chaptarrng-backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + suffix + ".db";

            if (restoringMergedSources)
            {
                // The two original BookshelfNG databases remain the rollback
                // source of truth. Restoring their merged snapshot into
                // chaptarr.db would hide later BookshelfNG writes when an
                // operator retries the upgrade, because normal startup prefers
                // an existing chaptarr.db. Retire the Chaptarr target instead;
                // the next Chaptarr startup will merge the current originals.
                BookshelfMergeReceipt.ValidateForNativeDatabase(currentPath);
                CheckpointDatabase(currentPath, currentBuilder);
                SqliteConnection.ClearAllPools();
                DeleteSidecars(currentPath);
                File.Move(currentPath, chaptarrBackupPath);
                return new BookshelfDatabaseRollbackResult(
                    sourceBackupPath,
                    chaptarrBackupPath,
                    previewOnly: false,
                    originalSourcesRetained: true);
            }

            var stagePath = currentPath + ".bookshelf-restore-stage-" + suffix + ".db";
            var failedRestorePath = currentPath + ".bookshelf-restore-failed-" + suffix + ".db";
            try
            {
                File.Copy(sourceBackupPath, stagePath);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stagePath, File.GetUnixFileMode(currentPath));
                }

                ValidateBookshelfSourceDatabase(stagePath);
                using (var stagedBookshelf = OpenConnection(stagePath, SqliteOpenMode.ReadOnly, currentBuilder))
                {
                    BookshelfDatabaseImporter.ValidateSourceBackup(stagedBookshelf);
                }

                CheckpointDatabase(currentPath, currentBuilder);
                SqliteConnection.ClearAllPools();
                DeleteSidecars(currentPath);

                // Keep the exact post-migration file, including any ChaptarrNG writes,
                // before promoting the validated BookshelfNG copy.
                File.Move(currentPath, chaptarrBackupPath);

                try
                {
                    File.Move(stagePath, currentPath);
                    ValidateBookshelfSourceDatabase(currentPath);
                    using var restoredBookshelf = OpenConnection(currentPath, SqliteOpenMode.ReadOnly, currentBuilder);
                    BookshelfDatabaseImporter.ValidateSourceBackup(restoredBookshelf);
                }
                catch (Exception restoreFailure)
                {
                    string failedPath = null;
                    try
                    {
                        if (File.Exists(currentPath))
                        {
                            File.Move(currentPath, failedRestorePath);
                            failedPath = failedRestorePath;
                        }

                        File.Move(chaptarrBackupPath, currentPath);
                    }
                    catch (Exception rollbackFailure)
                    {
                        throw new AggregateException(
                            $"BookshelfNG rollback validation failed. The prior ChaptarrNG database remains at '{chaptarrBackupPath}' and must be restored manually.",
                            restoreFailure,
                            rollbackFailure);
                    }

                    throw new InvalidOperationException(
                        "BookshelfNG rollback validation failed; the prior ChaptarrNG database was restored" +
                        (failedPath == null ? "." : $". The failed BookshelfNG copy remains at '{failedPath}'."),
                        restoreFailure);
                }

                return new BookshelfDatabaseRollbackResult(sourceBackupPath, chaptarrBackupPath, previewOnly: false);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(stagePath))
                {
                    DeleteDatabaseFiles(stagePath);
                }
            }
        }

        private static string FindLatestBookshelfBackup(string currentPath)
        {
            var directory = Path.GetDirectoryName(currentPath);
            var pattern = Path.GetFileName(currentPath) + ".bookshelfng-backup-*.db";
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var latest = Directory.EnumerateFiles(directory, pattern)
                .OrderByDescending(path => Path.GetFileName(path), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .FirstOrDefault();

            if (latest == null)
            {
                throw new FileNotFoundException(
                    "No retained BookshelfNG SQLite conversion backup was found beside the current database. The installed application was not changed.",
                    Path.Combine(directory, pattern));
            }

            var expectedPrefix = Path.GetFileName(currentPath) + ".bookshelfng-backup-";
            if (!Path.GetFileName(latest).StartsWith(expectedPrefix, comparison))
            {
                throw new InvalidDataException("The newest retained SQLite backup does not belong to the current database. No database was changed.");
            }

            return latest;
        }

        private static void ValidateBookshelfBackup(
            string sourceBackupPath,
            SqliteConnectionStringBuilder connectionBuilder,
            Action<string> migrateBookshelfSource)
        {
            using (var sourceBackup = OpenConnection(sourceBackupPath, SqliteOpenMode.ReadOnly, connectionBuilder))
            {
                var sourceVersion = BookshelfDatabaseImporter.ValidateSourceBackup(sourceBackup);
                if (migrateBookshelfSource == null)
                {
                    BookshelfDatabaseImporter.ValidateSourceSchema(sourceBackup);
                    return;
                }

                if (sourceVersion < 0)
                {
                    throw new InvalidDataException("BookshelfNG backup has no supported schema version. No database was changed.");
                }
            }

            var validationPath = sourceBackupPath + ".validate-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db";
            try
            {
                File.Copy(sourceBackupPath, validationPath);
                var validationConnectionString = BuildConnectionString(connectionBuilder, validationPath, SqliteOpenMode.ReadWrite);
                migrateBookshelfSource(validationConnectionString);
                using var validatedSource = OpenConnection(validationPath, SqliteOpenMode.ReadOnly, connectionBuilder);
                BookshelfDatabaseImporter.ValidateSourceSchema(validatedSource);
            }
            finally
            {
                DeleteDatabaseFiles(validationPath);
            }
        }

        private static FileStream AcquireConversionLock(string path)
        {
            try
            {
                // The lock file persists so concurrent converters have one stable inode to lock.
                // Read access keeps acquisition usable when a previous preview was run under a
                // different container UID; the containing app-data directory still controls who
                // can create the file in the first place.
                if (!File.Exists(path))
                {
                    try
                    {
                        // OpenOrCreate with read-only access can create a file on Unix, but Windows
                        // requires write access for that first creation. Create it once with write
                        // access, then acquire the persistent lock read-only on every platform.
                        using var create = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        // Another converter created the stable lock path first. The exclusive
                        // read handle below decides which process owns the conversion.
                    }
                }

                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("Another BookshelfNG database conversion is already using this data directory.", exception);
            }
        }

        private static void CreateConsistentSnapshot(string sourcePath, string snapshotPath, SqliteConnectionStringBuilder sourceBuilder)
        {
            using var source = OpenConnection(sourcePath, SqliteOpenMode.ReadOnly, sourceBuilder);
            using var snapshot = OpenConnection(snapshotPath, SqliteOpenMode.ReadWriteCreate, sourceBuilder);
            source.BackupDatabase(snapshot);
        }

        private static Dictionary<string, long> GetSourceTableCounts(SqliteConnection source)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            using var tablesCommand = source.CreateCommand();
            tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var tables = tablesCommand.ExecuteReader();
            var names = new List<string>();
            while (tables.Read())
            {
                names.Add(tables.GetString(0));
            }

            tables.Close();

            foreach (var tableName in names)
            {
                using var countCommand = source.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(*) FROM \"{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                counts.Add(tableName, System.Convert.ToInt64(countCommand.ExecuteScalar()));
            }

            return counts;
        }

        private static void ValidateImportReport(
            IReadOnlyDictionary<string, long> sourceTables,
            BookshelfDatabaseImportReport report)
        {
            if (report == null)
            {
                throw new InvalidDataException("BookshelfNG import did not return a row-accounting report; the staged database was not promoted.");
            }

            var unreportedTables = sourceTables.Keys.Except(report.Tables.Keys, StringComparer.Ordinal).OrderBy(x => x).ToArray();
            var unknownTables = report.Tables.Keys.Except(sourceTables.Keys, StringComparer.Ordinal).OrderBy(x => x).ToArray();
            if (unreportedTables.Length > 0 || unknownTables.Length > 0)
            {
                var details = new List<string>();
                if (unreportedTables.Length > 0)
                {
                    details.Add("unreported source tables: " + string.Join(", ", unreportedTables));
                }

                if (unknownTables.Length > 0)
                {
                    details.Add("reported tables absent from source: " + string.Join(", ", unknownTables));
                }

                throw new InvalidDataException("BookshelfNG import row accounting is incomplete (" + string.Join("; ", details) + "); the staged database was not promoted.");
            }

            foreach (var sourceTable in sourceTables)
            {
                var disposition = report.Tables[sourceTable.Key];
                if (disposition.SourceRows != sourceTable.Value || disposition.AccountedRows != sourceTable.Value)
                {
                    throw new InvalidDataException(
                        $"BookshelfNG import row accounting mismatch for source table '{sourceTable.Key}' " +
                        $"(reported source rows {disposition.SourceRows}/{sourceTable.Value}; " +
                        $"classified rows {disposition.AccountedRows}/{sourceTable.Value}); the staged database was not promoted.");
                }
            }
        }

        private static SqliteConnection OpenValidationConnection(string path, string readOnlyConnectionString)
        {
            var connection = new SqliteConnection(readOnlyConnectionString);
            try
            {
                connection.Open();
                return connection;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 14)
            {
                connection.Dispose();

                // Some platforms cannot open a standalone WAL database read-only when its
                // -wal/-shm sidecars are absent. Retry without Create semantics and make the
                // connection query-only before any validation query runs.
                var fallbackBuilder = new SqliteConnectionStringBuilder(readOnlyConnectionString)
                {
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                };
                var fallback = new SqliteConnection(fallbackBuilder.ToString());
                try
                {
                    fallback.Open();
                    using var queryOnly = fallback.CreateCommand();
                    queryOnly.CommandText = "PRAGMA query_only=ON;";
                    queryOnly.ExecuteNonQuery();
                    return fallback;
                }
                catch
                {
                    fallback.Dispose();
                    throw;
                }
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void ValidateDatabase(string path, DatabaseLineage expectedLineage, bool checkForeignKeys)
        {
            var connectionString = BuildConnectionString(new SqliteConnectionStringBuilder(), path, SqliteOpenMode.ReadOnly);
            using var connection = OpenValidationConnection(path, connectionString);

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = integrity.ExecuteScalar()?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SQLite integrity check failed for staged database '{Path.GetFileName(path)}'.");
                }
            }

            if (checkForeignKeys)
            {
                using var foreignKeys = connection.CreateCommand();
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                using var reader = foreignKeys.ExecuteReader();
                if (reader.Read())
                {
                    throw new InvalidDataException($"SQLite foreign-key check failed for staged database '{Path.GetFileName(path)}'.");
                }
            }

            if (DatabaseLineageGuard.Detect(connectionString, DatabaseType.SQLite) != expectedLineage)
            {
                throw new InvalidDataException($"The database '{Path.GetFileName(path)}' does not have the expected {expectedLineage} lineage.");
            }
        }

        private static long ValidateBookshelfSourceDatabase(string path)
        {
            ValidateDatabase(path, DatabaseLineage.BookshelfNG, checkForeignKeys: false);

            var connectionString = BuildConnectionString(new SqliteConnectionStringBuilder(), path, SqliteOpenMode.ReadOnly);
            using var connection = OpenValidationConnection(path, connectionString);
            var allowedSeriesLinkForeignKeys = GetStaleSeriesBookForeignKeys(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            var orphanedSeriesBookRowIds = new HashSet<long>();
            while (reader.Read())
            {
                var sourceTable = reader.GetString(0);
                var parentTable = reader.GetString(2);
                var foreignKeyId = reader.GetInt32(3);
                if (string.Equals(sourceTable, "SeriesBookLink", StringComparison.OrdinalIgnoreCase) &&
                    allowedSeriesLinkForeignKeys.TryGetValue(foreignKeyId, out var expectedParentTable) &&
                    string.Equals(parentTable, expectedParentTable, StringComparison.OrdinalIgnoreCase))
                {
                    // BookshelfNG can retain a junction row after either endpoint
                    // is removed. The importer resets that unusable relationship
                    // while keeping the untouched source snapshot for rollback.
                    if (reader.IsDBNull(1))
                    {
                        throw new InvalidDataException(
                            "SQLite reported a stale BookshelfNG series link without a unique row identity; conversion was stopped before promotion.");
                    }

                    orphanedSeriesBookRowIds.Add(reader.GetInt64(1));

                    continue;
                }

                throw new InvalidDataException(
                    $"SQLite foreign-key check found an unsupported reference in BookshelfNG source database '{Path.GetFileName(path)}': '{sourceTable}' -> '{parentTable}'.");
            }

            return orphanedSeriesBookRowIds.Count;
        }

        private static Dictionary<int, string> GetStaleSeriesBookForeignKeys(SqliteConnection connection)
        {
            var foreignKeys = new Dictionary<int, List<(string Table, string From, string To)>>();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_list('SeriesBookLink');";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                if (!foreignKeys.TryGetValue(id, out var columns))
                {
                    columns = new List<(string Table, string From, string To)>();
                    foreignKeys.Add(id, columns);
                }

                columns.Add((reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }

            return foreignKeys
                .Where(entry => entry.Value.Count == 1 &&
                    string.Equals(entry.Value[0].To, "Id", StringComparison.OrdinalIgnoreCase) &&
                    ((string.Equals(entry.Value[0].Table, "Books", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(entry.Value[0].From, "BookId", StringComparison.OrdinalIgnoreCase)) ||
                     (string.Equals(entry.Value[0].Table, "Series", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(entry.Value[0].From, "SeriesId", StringComparison.OrdinalIgnoreCase))))
                .ToDictionary(entry => entry.Key, entry => entry.Value[0].Table);
        }

        private static void EnsureStaleSeriesBookLinksAreAccounted(
            long staleSeriesBookLinks,
            BookshelfDatabaseImportReport importReport)
        {
            if (staleSeriesBookLinks == 0)
            {
                return;
            }

            if (!importReport.Tables.TryGetValue("SeriesBookLink", out var result) ||
                result.ResetRows < staleSeriesBookLinks)
            {
                throw new InvalidDataException(
                    "BookshelfNG migration did not explicitly account for every stale series link to a missing endpoint; conversion was stopped before promotion.");
            }
        }

        private static void CheckpointDatabase(string path, SqliteConnectionStringBuilder sourceBuilder)
        {
            if (!File.Exists(path))
            {
                return;
            }

            using var connection = OpenConnection(path, SqliteOpenMode.ReadWrite, sourceBuilder);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var busy = reader.GetInt32(0);
                var frames = reader.GetInt32(1);
                var checkpointed = reader.GetInt32(2);
                if (busy != 0 || (frames >= 0 && checkpointed < frames))
                {
                    throw new IOException($"SQLite WAL checkpoint was incomplete for '{Path.GetFileName(path)}'; promotion was stopped.");
                }
            }
        }

        private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode, SqliteConnectionStringBuilder template)
        {
            var connection = new SqliteConnection(BuildConnectionString(template, path, mode));
            connection.Open();
            return connection;
        }

        private static string BuildConnectionString(SqliteConnectionStringBuilder template, string path, SqliteOpenMode mode)
        {
            var builder = new SqliteConnectionStringBuilder(template.ConnectionString)
            {
                DataSource = path,
                Mode = mode,
                Pooling = false
            };

            return mode == SqliteOpenMode.ReadOnly
                ? DatabaseLineageGuard.CreateSafeReadOnlyConnectionString(builder.ConnectionString)
                : builder.ConnectionString;
        }

        private static void DeleteSidecars(string databasePath)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            DeleteSidecars(databasePath);
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }

        private static void EnsureSourceDatabasePaths(string sourcePath)
        {
            var sourceDirectoryPath = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrWhiteSpace(sourceDirectoryPath))
            {
                throw new InvalidDataException("The BookshelfNG database directory could not be resolved safely.");
            }

            var sourceDirectory = new DirectoryInfo(sourceDirectoryPath);
            if (sourceDirectory.LinkTarget is not null ||
                (sourceDirectory.Exists && (sourceDirectory.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException($"The BookshelfNG data directory is a symbolic link or reparse point ('{sourceDirectoryPath}'). Resolve it to a regular directory before migrating.");
            }

            EnsureRegularDatabasePath(sourcePath, "BookshelfNG database", required: true);
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            {
                EnsureRegularDatabasePath(sourcePath + suffix, $"BookshelfNG SQLite sidecar '{suffix}'", required: false);
            }
        }

        private static void EnsureRegularDatabasePath(string path, string description, bool required)
        {
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null)
            {
                throw new InvalidDataException($"The {description} is a symbolic link ('{path}'). Resolve it to a regular file before migrating.");
            }

            var fileExists = File.Exists(path);
            var directoryExists = Directory.Exists(path);
            if (!fileExists && !directoryExists)
            {
                if (required)
                {
                    throw new FileNotFoundException($"The {description} does not exist.", path);
                }

                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"The {description} is a reparse point ('{path}'). Resolve it to a regular file before migrating.");
            }

            if (directoryExists || (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException($"The {description} is a directory, not a regular file ('{path}').");
            }
        }

        private static void EnsureConversionSpace(string sourcePath)
        {
            long sourceBytes = 0;
            foreach (var suffix in new[] { string.Empty, "-wal", "-journal" })
            {
                var path = sourcePath + suffix;
                if (File.Exists(path))
                {
                    sourceBytes = checked(sourceBytes + new FileInfo(path).Length);
                }
            }

            var requiredBytes = checked(sourceBytes * 4 + 64L * 1024 * 1024);
            var fullPath = Path.GetFullPath(sourcePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mount = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && IsPathOnDrive(fullPath, drive.RootDirectory.FullName, comparison))
                .MaxBy(drive => drive.RootDirectory.FullName.Length);
            if (mount == null)
            {
                throw new IOException("The BookshelfNG database filesystem could not be identified for the conversion space preflight.");
            }

            var availableBytes = mount.AvailableFreeSpace;
            if (availableBytes < requiredBytes)
            {
                throw new IOException(
                    $"The BookshelfNG database and SQLite sidecars need approximately {requiredBytes / (1024 * 1024)} MiB of free space for staged conversion; only {availableBytes / (1024 * 1024)} MiB is available. No database was changed.");
            }
        }

        private static bool IsPathOnDrive(string path, string driveRoot, StringComparison comparison)
        {
            var root = Path.GetFullPath(driveRoot);
            if (string.Equals(path, root, comparison))
            {
                return true;
            }

            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, comparison);
        }

        private static int CleanOrphanedStageFiles(string directory, string sourcePath)
        {
            var sourceFilePrefix = Path.GetFileName(sourcePath) + ".";
            var orphanStages = Directory.EnumerateFiles(directory)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.EndsWith(".db", StringComparison.Ordinal) &&
                           (name.StartsWith(sourceFilePrefix + "chaptarr-stage-", StringComparison.Ordinal) ||
                            name.StartsWith(sourceFilePrefix + "bookshelf-snapshot-", StringComparison.Ordinal) ||
                            name.StartsWith(sourceFilePrefix + "bookshelf-migrate-", StringComparison.Ordinal));
                })
                .ToArray();

            foreach (var orphan in orphanStages)
            {
                DeleteDatabaseFiles(orphan);
            }

            return orphanStages.Length;
        }
    }

    public sealed class BookshelfDatabaseConversionResult
    {
        public BookshelfDatabaseConversionResult(
            string backupPath,
            BookshelfDatabaseImportReport importReport,
            bool previewOnly)
        {
            BackupPath = backupPath;
            ImportReport = importReport ?? throw new ArgumentNullException(nameof(importReport));
            PreviewOnly = previewOnly;
        }

        public string BackupPath { get; }
        public BookshelfDatabaseImportReport ImportReport { get; }
        public bool PreviewOnly { get; }
    }

    public sealed class BookshelfDatabaseRollbackResult
    {
        public BookshelfDatabaseRollbackResult(
            string bookshelfBackupPath,
            string chaptarrBackupPath,
            bool previewOnly,
            bool originalSourcesRetained = false)
        {
            BookshelfBackupPath = bookshelfBackupPath ?? throw new ArgumentNullException(nameof(bookshelfBackupPath));
            ChaptarrBackupPath = chaptarrBackupPath;
            PreviewOnly = previewOnly;
            OriginalSourcesRetained = originalSourcesRetained;
        }

        public string BookshelfBackupPath { get; }
        public string ChaptarrBackupPath { get; }
        public bool PreviewOnly { get; }
        public bool OriginalSourcesRetained { get; }
    }

    /// <summary>
    /// Per-source-table accounting returned by the importer. Every row must be
    /// classified as preserved, transformed, rebuilt from canonical data, or
    /// deliberately reset with an explicit explanation before promotion.
    /// </summary>
    public sealed class BookshelfDatabaseImportReport
    {
        private readonly Dictionary<string, BookshelfDatabaseTableImportResult> _tables = new(StringComparer.Ordinal);
        private readonly List<string> _blockingIssues = new();

        public IReadOnlyDictionary<string, BookshelfDatabaseTableImportResult> Tables => _tables;
        public IReadOnlyList<string> BlockingIssues => _blockingIssues;

        public void AddBlockingIssue(string issue)
        {
            if (string.IsNullOrWhiteSpace(issue))
            {
                throw new ArgumentException("A blocking issue description is required.", nameof(issue));
            }

            if (!_blockingIssues.Contains(issue, StringComparer.Ordinal))
            {
                _blockingIssues.Add(issue);
            }
        }

        public void ThrowIfBlockingIssues()
        {
            if (_blockingIssues.Count > 0)
            {
                throw new InvalidDataException(
                    "BookshelfNG migration cannot be applied until the following blocking settings are mapped: " +
                    string.Join("; ", _blockingIssues) + ". Run -preview-bookshelf-migration to review the source without changing it.");
            }
        }

        public void RecordTable(
            string sourceTable,
            long sourceRows,
            long preservedRows,
            long transformedRows,
            long rebuiltRows,
            long resetRows,
            string targetMapping)
        {
            if (string.IsNullOrWhiteSpace(sourceTable))
            {
                throw new ArgumentException("A source table name is required.", nameof(sourceTable));
            }

            if (sourceRows < 0 || preservedRows < 0 || transformedRows < 0 || rebuiltRows < 0 || resetRows < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRows), "Source and disposition row counts must be non-negative.");
            }

            var accountedRows = checked(preservedRows + transformedRows + rebuiltRows + resetRows);
            if (accountedRows > sourceRows)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRows), "Disposition row counts cannot exceed the source row count.");
            }

            if (string.IsNullOrWhiteSpace(targetMapping))
            {
                throw new ArgumentException("Describe how the source table is represented in the Chaptarr target.", nameof(targetMapping));
            }

            if (!_tables.TryAdd(sourceTable, new BookshelfDatabaseTableImportResult(
                    sourceRows,
                    preservedRows,
                    transformedRows,
                    rebuiltRows,
                    resetRows,
                    targetMapping)))
            {
                throw new InvalidOperationException($"Source table '{sourceTable}' was reported more than once.");
            }
        }
    }

    public sealed class BookshelfDatabaseTableImportResult
    {
        public BookshelfDatabaseTableImportResult(
            long sourceRows,
            long preservedRows,
            long transformedRows,
            long rebuiltRows,
            long resetRows,
            string targetMapping)
        {
            SourceRows = sourceRows;
            PreservedRows = preservedRows;
            TransformedRows = transformedRows;
            RebuiltRows = rebuiltRows;
            ResetRows = resetRows;
            TargetMapping = targetMapping;
        }

        public long SourceRows { get; }
        public long PreservedRows { get; }
        public long TransformedRows { get; }
        public long RebuiltRows { get; }
        public long ResetRows { get; }
        public long AccountedRows => PreservedRows + TransformedRows + RebuiltRows + ResetRows;
        public string TargetMapping { get; }
    }
}
